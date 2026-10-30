using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Snitch.Debugging;

/// <summary>
/// A Debug Adapter Protocol client over one adapter process: it frames requests, pairs each
/// response with its request by sequence number, and keeps every event the adapter sent so a
/// caller that arrives late (a <c>stopped</c> event that fired between two tool calls) still
/// sees it. No lock: the pending table and the event log are concurrent collections and the
/// event signal is an exchanged <see cref="TaskCompletionSource"/>.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private const int OutputBufferLines = 500;
    private const int StderrBufferLines = 200;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Process? _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DapMessage>> _pending = new();
    private readonly ConcurrentDictionary<long, DapMessage> _events = new();
    private readonly ConcurrentQueue<string> _consoleOutput = new();
    private readonly ConcurrentQueue<string> _adapterStderr = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private Task? _reader;

    public DapClient(Stream input, Stream output, Process? process = null)
    {
        _input = input;
        _output = output;
        _process = process;
    }

    /// <summary>The adapter process id, when this client started one.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>False once the adapter process exited (or never existed and the stream closed).</summary>
    public bool IsAlive => _process is null || !_process.HasExited;

    /// <summary>The last transport error the read loop saw, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts the adapter process and returns a client reading and writing its pipes.</summary>
    public static DapClient StartProcess(string executable, IEnumerable<string> arguments, string? workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var process = Process.Start(start)
            ?? throw new DebugAdapterException($"The debug adapter '{executable}' did not start.");

        var client = new DapClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process);
        client.Start();

        // An adapter that dies with an unread stderr pipe blocks on write; drain it.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                    Remember(client._adapterStderr, line, StderrBufferLines);
            }
            catch { /* the process went away */ }
        });

        return client;
    }

    /// <summary>Starts the read loop. Safe to call once; later calls are no-ops.</summary>
    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string command, object? arguments, TimeSpan timeout)
    {
        if (_stop.IsCancellationRequested)
            throw new DebugAdapterException("The debug session is closed.");

        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<DapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sequence] = completion;

        var packet = new Dictionary<string, object?>
        {
            ["seq"] = sequence,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null) packet["arguments"] = arguments;

        await DapProtocol.WriteAsync(_output, JsonSerializer.Serialize(packet), _stop.Token)
            .ConfigureAwait(false);

        var winner = await Task.WhenAny(completion.Task, Task.Delay(timeout, _stop.Token))
            .ConfigureAwait(false);
        if (winner != completion.Task)
        {
            _pending.TryRemove(sequence, out _);
            throw new TimeoutException(
                $"The debug adapter did not answer '{command}' within {timeout.TotalSeconds:0.#}s.");
        }

        var response = await completion.Task.ConfigureAwait(false);
        if (response.Success == false)
            throw new DebugAdapterException($"{command}: {response.ErrorMessage ?? "the adapter refused the request"}");

        return response.Body;
    }

    /// <summary>
    /// Waits for an event named <paramref name="name"/>, scanning the retained log first so an
    /// event that already happened is still returned. Returns null on timeout.
    /// </summary>
    public async Task<DapMessage?> WaitForEventAsync(string name, TimeSpan timeout,
        Func<DapMessage, bool>? predicate = null, long afterSequence = 0)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var signal = Volatile.Read(ref _signal);

            foreach (var message in _events.Values.OrderBy(e => e.Seq))
            {
                if (message.Seq <= afterSequence) continue;
                if (message.Event != name) continue;
                if (predicate is not null && !predicate(message)) continue;
                return message;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            await Task.WhenAny(signal.Task, Task.Delay(remaining)).ConfigureAwait(false);
        }
    }

    /// <summary>The highest event sequence number this client has retained.</summary>
    public long LastEventSequence => _events.IsEmpty ? 0 : _events.Keys.Max();

    /// <summary>The most recent event with this name, or null.</summary>
    public DapMessage? LatestEvent(string name)
    {
        DapMessage? latest = null;
        foreach (var message in _events.Values)
        {
            if (message.Event != name) continue;
            if (latest is null || message.Seq > latest.Seq) latest = message;
        }
        return latest;
    }

    /// <summary>Waits for <c>stopped</c>, <c>exited</c> or <c>terminated</c> — whichever comes next.</summary>
    public async Task<DapMessage?> WaitForStopAsync(TimeSpan timeout, long afterSequence = 0)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var signal = Volatile.Read(ref _signal);

            foreach (var message in _events.Values.OrderBy(e => e.Seq))
            {
                if (message.Seq <= afterSequence) continue;
                if (message.Event is not ("stopped" or "exited" or "terminated")) continue;
                return message;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            await Task.WhenAny(signal.Task, Task.Delay(remaining)).ConfigureAwait(false);
        }
    }

    /// <summary>The last <paramref name="count"/> lines the debuggee wrote to its output events.</summary>
    public string[] ReadOutput(int count) => _consoleOutput.TakeLast(Math.Clamp(count, 1, OutputBufferLines)).ToArray();

    /// <summary>The adapter's own stderr lines, for a failure that the protocol did not report.</summary>
    public string[] ReadStderr(int count) => _adapterStderr.TakeLast(Math.Clamp(count, 1, StderrBufferLines)).ToArray();

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var body = await DapProtocol.ReadBodyAsync(_input, _stop.Token).ConfigureAwait(false);
                if (body is null) break;

                DapMessage message;
                try
                {
                    message = DapMessage.Parse(body);
                }
                catch (JsonException ex)
                {
                    LastError = $"An unreadable DAP packet was skipped: {ex.Message}";
                    continue;
                }

                if (message.IsResponse)
                {
                    if (message.RequestSeq is long requestSeq && _pending.TryRemove(requestSeq, out var completion))
                        completion.TrySetResult(message);
                    continue;
                }

                if (!message.IsEvent) continue;

                if (message.Event == "output")
                {
                    var line = message.BodyString("output");
                    if (!string.IsNullOrEmpty(line)) Remember(_consoleOutput, line.TrimEnd('\r', '\n'), OutputBufferLines);
                }

                _events[message.Seq] = message;
                var signal = Interlocked.Exchange(
                    ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                signal.TrySetResult();
            }
        }
        catch (OperationCanceledException) { /* closed on purpose */ }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            foreach (var entry in _pending)
                entry.Value.TrySetException(new DebugAdapterException(
                    $"The debug adapter closed before answering request {entry.Key}." +
                    (LastError is null ? string.Empty : " " + LastError)));
            _pending.Clear();

            var signal = Interlocked.Exchange(
                ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            signal.TrySetResult();
        }
    }

    private static void Remember(ConcurrentQueue<string> queue, string line, int limit)
    {
        queue.Enqueue(line);
        while (queue.Count > limit) queue.TryDequeue(out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            try
            {
                using var shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await DapProtocol.WriteAsync(_output, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["seq"] = Interlocked.Increment(ref _sequence),
                    ["type"] = "request",
                    ["command"] = "disconnect",
                    ["arguments"] = new Dictionary<string, object?> { ["terminateDebuggee"] = true },
                }), shortTimeout.Token).ConfigureAwait(false);
            }
            catch { /* the adapter is already gone */ }

            _stop.Cancel();
        }

        try
        {
            if (_process is { HasExited: false })
            {
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { _process.Kill(entireProcessTree: true); } catch { } }
            }
        }
        catch { /* best effort */ }

        _process?.Dispose();
        _stop.Dispose();
    }
}
