using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snitch.Languages.CSharp.Testing;
using Snitch.Workspace;

namespace Snitch.Debugging;

/// <summary>What a <c>debug_test</c> call asks for.</summary>
public sealed record DebugTestRequest(
    string? ProjectName,
    string? TestName,
    string? ClassName,
    string? Filter,
    BreakpointSpec[]? Breakpoints,
    int StartTimeoutSeconds,
    int WaitForStopSeconds);

/// <summary>
/// Debug a unit test: VSTest is started with <c>VSTEST_HOST_DEBUG=1</c> so its test host waits for a
/// debugger, the host pid is read from the output, the DAP adapter attaches to it, the breakpoints are set
/// and the host is resumed. From there the session is a normal one — debug_stack, debug_variables and the
/// rest answer on it.
/// </summary>
public interface IDebugTestService
{
    Task<object> DebugTestAsync(DebugTestRequest request);

    /// <summary>What the test host printed so far (the test results arrive there, not on the DAP channel).</summary>
    object Output(string sessionId, int lines);
}

public sealed class DebugTestService : IDebugTestService
{
    /// <summary>The line VSTest prints when the host waits for a debugger: "Process Id: 1234, Name: testhost".</summary>
    private static readonly Regex ProcessIdPattern = new(@"Process\s+Id:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, RunningTest> Running = new(StringComparer.Ordinal);

    private sealed record RunningTest(Process Process, ConcurrentQueue<string> Output);

    private readonly IRoslynWorkspace _workspace;
    private readonly IDebuggerService _debugger;

    public DebugTestService(IRoslynWorkspace workspace, IDebuggerService debugger)
    {
        _workspace = workspace;
        _debugger = debugger;
    }

    public async Task<object> DebugTestAsync(DebugTestRequest request)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory. Call load_solution first." };

        var project = TestSuiteRunner.ResolveTestProject(solution, request.ProjectName, out var resolveError);
        if (project is null) return new { error = resolveError };

        var filter = TestSuiteRunner.ComposeFilter(request.TestName, request.ClassName, request.Filter);
        var startTimeout = Math.Clamp(request.StartTimeoutSeconds < 5 ? 60 : request.StartTimeoutSeconds, 5, 600);
        var stopWait = Math.Clamp(request.WaitForStopSeconds < 0 ? 15 : request.WaitForStopSeconds, 0, 300);

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(project.FilePath!)!,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(project.FilePath!);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add(filter);
        }

        // VSTest reads this and makes the test host wait for a debugger instead of running the tests.
        start.Environment["VSTEST_HOST_DEBUG"] = "1";

        Process process;
        try { process = Process.Start(start)!; }
        catch (Exception exception)
        {
            return new { error = $"Could not start 'dotnet test': {exception.Message}" };
        }

        var output = new ConcurrentQueue<string>();
        var hostPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(() => DrainAsync(process.StandardOutput, output, hostPid));
        _ = Task.Run(() => DrainAsync(process.StandardError, output, hostPid));

        try { process.StandardInput.Close(); } catch { /* EOF for dotnet */ }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(startTimeout));
        int pid;
        try
        {
            pid = await hostPid.Task.WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            Kill(process);
            var reason = exception is OperationCanceledException
                ? $"The test host did not announce itself within {startTimeout}s."
                : $"The test run ended before a test host announced itself: {exception.Message}";
            return new
            {
                error = reason,
                hint = "VSTEST_HOST_DEBUG is honoured by VSTest; if the SDK or an adapter ignores it, debug the test " +
                       "project's testhost by launching it under debug_launch, or run the test without the debugger first.",
                filter,
                output = Tail(output, 60),
            };
        }

        var attach = await _debugger.AttachAsync(pid, waitForStopSeconds: request.Breakpoints is { Length: > 0 } ? 5 : 0);
        var attachJson = JsonSerializer.SerializeToElement(attach);
        if (attachJson.TryGetProperty("error", out var attachError))
        {
            Kill(process);
            return new
            {
                error = attachError.GetString(),
                hint = attachJson.TryGetProperty("hint", out var hint) ? hint.GetString() : "Call debug_install first.",
                testProcessId = pid,
                output = Tail(output, 40),
            };
        }

        var sessionId = attachJson.TryGetProperty("sessionId", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Kill(process);
            return new { error = "The adapter attached without a session id.", attach };
        }

        Running[sessionId] = new RunningTest(process, output);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Running.TryRemove(sessionId, out _);

        var breakpoints = new List<object>();
        if (request.Breakpoints is { Length: > 0 })
        {
            foreach (var group in request.Breakpoints
                         .Where(spec => !string.IsNullOrWhiteSpace(spec.File))
                         .GroupBy(spec => spec.File!, StringComparer.OrdinalIgnoreCase))
            {
                breakpoints.Add(await _debugger.SetBreakpointsAsync(sessionId, group.Key, group.ToArray(), clear: false));
            }
        }

        var continued = request.Breakpoints is { Length: > 0 } && stopWait > 0
            ? await _debugger.ContinueAsync(sessionId, null, stopWait)
            : null;
        var continuedJson = continued is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(continued);
        var stopLocation = continuedJson is { } json && json.TryGetProperty("stopLocation", out var location)
            ? location.GetString()
            : null;

        return new
        {
            sessionId,
            testProcessId = pid,
            project = project.Name,
            filter = filter ?? "all tests",
            breakpoints,
            continued,
            state = attachJson.TryGetProperty("state", out var state) ? state.GetString() : null,
            stopLocation,
            output = Tail(output, 40),
            note = "The test host is attached and running. Call debug_test with sessionId to read what it printed " +
                   "(the test results go to the VSTest console, not the debug channel), and the debug_* tools to " +
                   "inspect or step the session.",
        };
    }

    public object Output(string sessionId, int lines)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new { error = "A sessionId is required." };
        if (!Running.TryGetValue(sessionId, out var running))
            return new { error = $"No test run is known for session '{sessionId}'.", hint = "debug_sessions lists live sessions." };

        var count = Math.Clamp(lines < 1 ? 100 : lines, 1, 500);
        var all = running.Output.ToArray();
        return new
        {
            sessionId,
            running = !running.Process.HasExited,
            exitCode = running.Process.HasExited ? running.Process.ExitCode : (int?)null,
            lines = all.TakeLast(count).ToList(),
        };
    }

    /// <summary>Reads one stream until it ends, mirroring every line into the buffer and looking for the host pid.</summary>
    private static async Task DrainAsync(StreamReader reader, ConcurrentQueue<string> output, TaskCompletionSource<int> hostPid)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            output.Enqueue(line);
            if (hostPid.Task.IsCompleted) continue;
            if (TryParseHostProcessId(line, out var pid)) hostPid.TrySetResult(pid);
        }

        hostPid.TrySetException(new InvalidOperationException("The test host output ended before it announced a process id."));
    }

    /// <summary>The host pid line VSTest prints under <c>VSTEST_HOST_DEBUG</c>. Public so the contract is testable.</summary>
    public static bool TryParseHostProcessId(string line, out int processId)
    {
        processId = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var match = ProcessIdPattern.Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, out processId) && processId > 0;
    }

    private static List<string> Tail(ConcurrentQueue<string> output, int count) =>
        output.ToArray().TakeLast(count).ToList();

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }
}
