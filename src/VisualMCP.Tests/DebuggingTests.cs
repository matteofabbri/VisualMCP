using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Snitch.Debugging;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The debugger layer: the DAP framing, the client's request/response pairing and event retention
/// against a scripted adapter, and — when netcoredbg is installed — a real launch/stop/inspect/
/// evaluate/continue round trip against a tiny console app.
/// </summary>
public sealed class DebuggingTests
{
    // ── framing ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task DapProtocol_WritesAndReadsAFramedMessage()
    {
        using var stream = new MemoryStream();
        await DapProtocol.WriteAsync(stream, "{\"seq\":1,\"type\":\"event\",\"event\":\"initialized\"}");
        stream.Position = 0;

        var body = await DapProtocol.ReadBodyAsync(stream);
        Assert.NotNull(body);

        var message = DapMessage.Parse(body!);
        Assert.True(message.IsEvent);
        Assert.Equal("initialized", message.Event);
        Assert.Equal(1, message.Seq);
    }

    [Fact]
    public async Task DapProtocol_RejectsAFrameWithoutContentLength()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("X-Other: 1\r\n\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => DapProtocol.ReadBodyAsync(stream));
    }

    [Fact]
    public async Task DapProtocol_ReportsEndOfStreamAsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await DapProtocol.ReadBodyAsync(stream));
    }

    // ── client against a scripted adapter ────────────────────────────────────
    [Fact]
    public async Task DapClient_PairsResponsesAndRetainsStopsAcrossWaiters()
    {
        var clientToAdapter = new TestChannelStream();
        var adapterToClient = new TestChannelStream();
        var client = new DapClient(adapterToClient, clientToAdapter);
        client.Start();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var adapter = Task.Run(() => ScriptedAdapter.RunAsync(clientToAdapter, adapterToClient, stop.Token));

        var initialize = await client.RequestAsync("initialize",
            new Dictionary<string, object?> { ["adapterID"] = "coreclr" }, TimeSpan.FromSeconds(5));
        Assert.True(initialize.GetProperty("supportsConfigurationDoneRequest").GetBoolean());

        var launch = client.RequestAsync("launch",
            new Dictionary<string, object?> { ["program"] = "demo.dll" }, TimeSpan.FromSeconds(10));

        var initialized = await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(5));
        Assert.NotNull(initialized);

        var breakpoints = await client.RequestAsync("setBreakpoints", new Dictionary<string, object?>
        {
            ["source"] = new Dictionary<string, object?> { ["path"] = "C:/demo/Program.cs" },
            ["breakpoints"] = new[] { new { line = 3 } },
        }, TimeSpan.FromSeconds(5));
        Assert.True(breakpoints.GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean());

        await client.RequestAsync("configurationDone", null, TimeSpan.FromSeconds(5));
        await launch;

        // The stop fired while the caller was still setting up: a later waiter must still see it.
        var stopped = await client.WaitForStopAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(stopped);
        Assert.Equal("stopped", stopped!.Event);
        Assert.Equal("breakpoint", stopped.BodyString("reason"));

        var stack = await client.RequestAsync("stackTrace",
            new Dictionary<string, object?> { ["threadId"] = 1 }, TimeSpan.FromSeconds(5));
        Assert.Equal("Program.<Main>$()", stack.GetProperty("stackFrames")[0].GetProperty("name").GetString());
        Assert.Equal(3, stack.GetProperty("stackFrames")[0].GetProperty("line").GetInt32());

        await client.DisposeAsync();
        stop.Cancel();
        try { await adapter; } catch (OperationCanceledException) { }
    }

    // ── against the real adapter ─────────────────────────────────────────────
    [Fact]
    public async Task Debugger_LaunchesStopsInspectsEvaluatesAndContinues()
    {
        var adapter = new NetCoreDbgTool(new DebugOptions()).Locate();
        if (adapter is null)
            return; // netcoredbg is optional here: debug_install downloads it

        var directory = Path.Combine(Path.GetTempPath(), "vmcp-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(source,
                """
                var total = 0;
                for (var i = 1; i <= 3; i++) total += i;
                Console.WriteLine($"total={total}");
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "Demo.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);

            var build = await RunAsync("dotnet", ["build", Path.Combine(directory, "Demo.csproj"), "-c", "Debug"], directory);
            Assert.True(build.ExitCode == 0, build.Output);

            var assembly = Directory.EnumerateFiles(Path.Combine(directory, "bin"), "Demo.dll", SearchOption.AllDirectories).First();

            var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
            using var _ = workspace;
            var debugger = new DebuggerService(workspace, new DebugOptions());

            var launch = ResultAssert.Json(await debugger.LaunchAsync(
                assembly, null, null, directory, null, stopAtEntry: false,
                [new BreakpointSpec { File = source, Line = 3 }], waitForStopSeconds: 60));

            // A cold start under a loaded machine can miss the first stop window: retry once with a
            // fresh session rather than reporting the adapter as broken.
            if (launch.TryGetProperty("state", out var state) && state.GetString() != "stopped")
            {
                await debugger.TerminateAsync(launch.GetProperty("sessionId").GetString()!);
                launch = ResultAssert.Json(await debugger.LaunchAsync(
                    assembly, null, null, directory, null, stopAtEntry: false,
                    [new BreakpointSpec { File = source, Line = 3 }], waitForStopSeconds: 60));
            }

            Assert.True(launch.TryGetProperty("state", out var finalState), launch.GetRawText());
            Assert.Equal("stopped", finalState.GetString());
            var sessionId = launch.GetProperty("sessionId").GetString()!;
            Assert.EndsWith("Program.cs:3", launch.GetProperty("stopLocation").GetString());

            var stack = ResultAssert.Json(await debugger.StackTraceAsync(sessionId, null, 0, 5));
            var frames = stack.GetProperty("frames");
            Assert.True(frames.GetArrayLength() > 0);
            Assert.Equal(3, frames[0].GetProperty("line").GetInt32());
            Assert.EndsWith("Program.cs", frames[0].GetProperty("file").GetString());

            var variables = ResultAssert.Json(await debugger.VariablesAsync(sessionId, null, "Locals", 20, 1));
            var names = variables.GetProperty("variables").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString()).ToList();
            Assert.Contains("total", names);

            var evaluate = ResultAssert.Json(await debugger.EvaluateAsync(sessionId, "total * 2", null, null));
            Assert.Equal("12", evaluate.GetProperty("result").GetString());

            var continued = ResultAssert.Json(await debugger.ContinueAsync(sessionId, null, 30));
            Assert.Equal("exited", continued.GetProperty("state").GetString());

            var terminated = ResultAssert.Json(await debugger.TerminateAsync(sessionId));
            Assert.True(terminated.GetProperty("terminated").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, string directory)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    /// <summary>
    /// A two-way pipe over an unbounded channel: one endpoint is given to the client, the other to a
    /// scripted adapter, so the DAP conversation runs with no OS process.
    /// </summary>
    internal sealed class TestChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                    return 0;

                if (!_channel.Reader.TryRead(out var next))
                    return 0;

                _current = next;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _channel.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => _channel.Writer.TryWrite(buffer[offset..(offset + count)]);

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The adapter side of the protocol: answers the requests the client sends, in DAP order.</summary>
    internal static class ScriptedAdapter
    {
        public static async Task RunAsync(Stream fromClient, Stream toClient, CancellationToken cancellationToken)
        {
            long launchSequence = 0;
            var sequence = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var body = await DapProtocol.ReadBodyAsync(fromClient, cancellationToken);
                if (body is null) return;

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var command = root.GetProperty("command").GetString();
                var requestSequence = root.GetProperty("seq").GetInt64();

                switch (command)
                {
                    case "initialize":
                        await ReplyAsync(toClient, ++sequence, requestSequence, "initialize",
                            new { supportsConfigurationDoneRequest = true }, cancellationToken);
                        break;

                    case "launch":
                        launchSequence = requestSequence;
                        await EventAsync(toClient, ++sequence, "initialized", new { }, cancellationToken);
                        break;

                    case "setBreakpoints":
                        await ReplyAsync(toClient, ++sequence, requestSequence, "setBreakpoints",
                            new { breakpoints = new[] { new { id = 1, verified = true, line = 3 } } }, cancellationToken);
                        break;

                    case "configurationDone":
                        await ReplyAsync(toClient, ++sequence, requestSequence, "configurationDone", new { }, cancellationToken);
                        await ReplyAsync(toClient, ++sequence, launchSequence, "launch", new { }, cancellationToken);
                        await EventAsync(toClient, ++sequence, "stopped", new { reason = "breakpoint", threadId = 1 }, cancellationToken);
                        break;

                    case "stackTrace":
                        await ReplyAsync(toClient, ++sequence, requestSequence, "stackTrace", new
                        {
                            stackFrames = new[]
                            {
                                new
                                {
                                    id = 7,
                                    name = "Program.<Main>$()",
                                    source = new { path = "C:/demo/Program.cs" },
                                    line = 3,
                                    column = 1,
                                },
                            },
                        }, cancellationToken);
                        break;

                    case "disconnect":
                        await ReplyAsync(toClient, ++sequence, requestSequence, "disconnect", new { }, cancellationToken);
                        return;
                }
            }
        }

        private static Task ReplyAsync(Stream stream, int sequence, long requestSequence, string command,
            object body, CancellationToken cancellationToken) =>
            DapProtocol.WriteAsync(stream, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["seq"] = sequence,
                ["type"] = "response",
                ["request_seq"] = requestSequence,
                ["success"] = true,
                ["command"] = command,
                ["body"] = body,
            }), cancellationToken);

        private static Task EventAsync(Stream stream, int sequence, string name, object body,
            CancellationToken cancellationToken) =>
            DapProtocol.WriteAsync(stream, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["seq"] = sequence,
                ["type"] = "event",
                ["event"] = name,
                ["body"] = body,
            }), cancellationToken);
    }
}
