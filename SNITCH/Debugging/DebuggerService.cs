using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Debugging;

/// <summary>
/// Sessions over one DAP adapter process each. The adapter (netcoredbg) speaks the standard
/// Debug Adapter Protocol, so the sequencing here is the protocol's: initialize, launch/attach,
/// the <c>initialized</c> event, breakpoints, configurationDone, then stops and resumes.
/// </summary>
public sealed class DebuggerService : IDebuggerService
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(15);

    private readonly IRoslynWorkspace _workspace;
    private readonly NetCoreDbgTool _tool;
    private readonly ConcurrentDictionary<string, DebugSession> _sessions = new();
    private long _counter;

    public DebuggerService(IRoslynWorkspace workspace, DebugOptions options)
    {
        _workspace = workspace;
        _tool = new NetCoreDbgTool(options);
    }

    public Task<object> StatusAsync() => Task.FromResult(_tool.Status());

    public Task<object> InstallAsync(bool force) => _tool.InstallAsync(force);

    // ── starting a session ───────────────────────────────────────────────────
    public async Task<object> LaunchAsync(string? program, string? projectName, string[]? programArgs,
        string? workingDirectory, string? environmentJson, bool stopAtEntry,
        BreakpointSpec[]? breakpoints, int waitForStopSeconds)
    {
        var adapter = _tool.Locate();
        if (adapter is null)
            return new
            {
                error = "netcoredbg is not installed.",
                hint = "Call debug_install (a few MB from the Samsung release), or set VISUALMCP_NETCOREDBG to its path.",
            };

        var (target, resolveError) = await ResolveProgramAsync(program, projectName);
        if (target is null) return new { error = resolveError };

        Dictionary<string, string>? environment = null;
        if (!string.IsNullOrWhiteSpace(environmentJson))
        {
            try
            {
                environment = JsonSerializer.Deserialize<Dictionary<string, string>>(environmentJson);
            }
            catch (JsonException ex)
            {
                return new { error = $"environmentJson must be a JSON object of strings: {ex.Message}" };
            }
        }

        var directory = ResolveWorkingDirectory(workingDirectory, target);
        var client = DapClient.StartProcess(adapter, ["--interpreter=vscode"], directory);
        var session = new DebugSession(NextSessionId(), client, "launch") { Program = target };
        _sessions[session.Id] = session;
        var breakpointReports = new List<object>();

        try
        {
            client.Start();
            await client.RequestAsync("initialize", InitializeArguments(), Short);

            var launchArguments = new Dictionary<string, object?>
            {
                ["program"] = target,
                ["args"] = programArgs ?? [],
                ["cwd"] = directory,
                ["stopAtEntry"] = stopAtEntry,
                ["justMyCode"] = true,
            };
            if (environment is not null) launchArguments["env"] = environment;

            var launchTask = client.RequestAsync("launch", launchArguments, TimeSpan.FromSeconds(30));
            await WaitForInitializedAsync(client, launchTask);

            foreach (var report in await ApplyInitialBreakpointsAsync(session, breakpoints, directory))
                breakpointReports.Add(report);

            try
            {
                await client.RequestAsync("configurationDone", null, Short);
            }
            catch (DebugAdapterException) when (launchTask.IsCompleted)
            {
                // An adapter that completed the launch before configurationDone does not want it.
            }

            await launchTask;
            session.State = "running";

            if (waitForStopSeconds > 0)
            {
                var stop = await client.WaitForStopAsync(TimeSpan.FromSeconds(Math.Clamp(waitForStopSeconds, 1, 300)));
                await ApplyStopAsync(session, stop);
            }

            return Describe(session, breakpointReports);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            _sessions.TryRemove(session.Id, out _);
            return new
            {
                error = $"The launch failed: {ex.Message}",
                stderr = client.ReadStderr(30),
                hint = "The adapter needs a built assembly (program: the .dll under bin/) and its runtimeconfig.json next to it.",
            };
        }
    }

    public async Task<object> AttachAsync(int processId, int waitForStopSeconds)
    {
        var adapter = _tool.Locate();
        if (adapter is null)
            return new { error = "netcoredbg is not installed.", hint = "Call debug_install first." };

        if (processId <= 0) return new { error = "A positive processId is required." };
        try
        {
            using var probe = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new { error = $"No process with id {processId} is running." };
        }

        var client = DapClient.StartProcess(adapter, ["--interpreter=vscode"], null);
        var session = new DebugSession(NextSessionId(), client, "attach") { Program = $"pid:{processId}" };
        _sessions[session.Id] = session;

        try
        {
            client.Start();
            await client.RequestAsync("initialize", InitializeArguments(), Short);

            var attachTask = client.RequestAsync("attach",
                new Dictionary<string, object?> { ["processId"] = processId }, TimeSpan.FromSeconds(30));
            await WaitForInitializedAsync(client, attachTask);

            try { await client.RequestAsync("configurationDone", null, Short); }
            catch (DebugAdapterException) when (attachTask.IsCompleted) { }

            await attachTask;
            session.State = "running";

            if (waitForStopSeconds > 0)
            {
                var stop = await client.WaitForStopAsync(TimeSpan.FromSeconds(Math.Clamp(waitForStopSeconds, 1, 300)));
                await ApplyStopAsync(session, stop);
            }

            return Describe(session, null);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            _sessions.TryRemove(session.Id, out _);
            return new { error = $"The attach failed: {ex.Message}", stderr = client.ReadStderr(30) };
        }
    }

    // ── breakpoints ──────────────────────────────────────────────────────────
    public async Task<object> SetBreakpointsAsync(string sessionId, string file, BreakpointSpec[]? breakpoints, bool clear)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);
        if (string.IsNullOrWhiteSpace(file)) return new { error = "A source file is required." };

        var full = ResolveFile(file, BaseDirectory())!;
        if (!clear && !File.Exists(full) && !session.Breakpoints.ContainsKey(full))
            return new { error = $"File not found: {full}", hint = "Pass an absolute path, or a path relative to the solution." };

        try
        {
            if (clear)
                session.Breakpoints.TryRemove(full, out _);
            else
                session.Breakpoints[full] = breakpoints ?? [];

            return await SendBreakpointsAsync(session, full);
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId, file = full };
        }
    }

    // ── running ──────────────────────────────────────────────────────────────
    public Task<object> ContinueAsync(string sessionId, int? threadId, int timeoutSeconds)
        => ResumeAsync(sessionId, "continue", threadId, timeoutSeconds);

    public Task<object> StepAsync(string sessionId, string kind, int? threadId, int timeoutSeconds)
    {
        var command = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "over" or "next" or "stepover" => "next",
            "into" or "in" or "stepin" => "stepIn",
            "out" or "stepout" => "stepOut",
            _ => null,
        };

        return command is null
            ? Task.FromResult<object>(new { error = $"Unknown step kind '{kind}'. Use over, into or out." })
            : ResumeAsync(sessionId, command, threadId, timeoutSeconds);
    }

    public async Task<object> PauseAsync(string sessionId, int? threadId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var thread = threadId ?? session.StoppedThreadId ?? await FirstThreadIdAsync(session);
            await session.Client.RequestAsync("pause",
                new Dictionary<string, object?> { ["threadId"] = thread }, Short);

            var stop = await session.Client.WaitForStopAsync(TimeSpan.FromSeconds(10), session.LastStopSequence);
            await ApplyStopAsync(session, stop);
            return Describe(session, null);
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    // ── inspecting ───────────────────────────────────────────────────────────
    public async Task<object> ThreadsAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var body = await session.Client.RequestAsync("threads", null, Short);
            var threads = new List<object>();
            if (body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("threads", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var id = Int(item, "id") ?? 0;
                    threads.Add(new { id, name = Str(item, "name"), stopped = id == session.StoppedThreadId });
                }
            }

            return new { sessionId, state = session.State, stoppedThreadId = session.StoppedThreadId, count = threads.Count, threads };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    public async Task<object> StackTraceAsync(string sessionId, int? threadId, int startFrame, int levels)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var frames = await ReadFramesAsync(session, threadId, startFrame, levels);
            return new
            {
                sessionId,
                threadId = threadId ?? session.StoppedThreadId,
                count = frames.Count,
                frames = frames.Select(frame => new
                {
                    id = frame.Id,
                    name = frame.Name,
                    file = frame.File,
                    line = frame.Line,
                    column = frame.Column,
                    location = frame.File is null ? null : $"{frame.File}:{frame.Line}",
                }),
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    public async Task<object> VariablesAsync(string sessionId, int? frameId, string? scope, int maxVariables, int depth)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var frame = frameId ?? await TopFrameIdAsync(session);
            if (frame is null)
                return new { error = "No stopped frame: the process must be stopped to read variables.", sessionId, state = session.State };

            var scopesBody = await session.Client.RequestAsync("scopes",
                new Dictionary<string, object?> { ["frameId"] = frame }, Short);

            var scopes = new List<(string Name, int Reference)>();
            if (scopesBody.ValueKind == JsonValueKind.Object &&
                scopesBody.TryGetProperty("scopes", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var name = Str(item, "name");
                    var reference = Int(item, "variablesReference") ?? 0;
                    if (name is not null && reference > 0) scopes.Add((name, reference));
                }
            }

            if (scopes.Count == 0)
                return new { error = "The adapter reported no variable scopes for that frame.", sessionId, frameId = frame };

            var wanted = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
            var selected = wanted is null
                ? scopes.FirstOrDefault(s => s.Name.Equals("Locals", StringComparison.OrdinalIgnoreCase))
                : scopes.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (selected.Name is null) selected = scopes[0];

            var variables = await ReadVariablesAsync(session, selected.Reference,
                Math.Clamp(maxVariables, 1, 500), Math.Clamp(depth, 1, 3));

            return new
            {
                sessionId,
                frameId = frame,
                scope = selected.Name,
                available = scopes.Select(s => s.Name),
                count = variables.Count,
                variables,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    public async Task<object> EvaluateAsync(string sessionId, string expression, int? frameId, string? context)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);
        if (string.IsNullOrWhiteSpace(expression)) return new { error = "An expression is required." };

        try
        {
            var frame = frameId ?? await TopFrameIdAsync(session);
            var arguments = new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["context"] = string.IsNullOrWhiteSpace(context) ? "watch" : context.Trim(),
            };
            if (frame is int id) arguments["frameId"] = id;

            var body = await session.Client.RequestAsync("evaluate", arguments, Short);
            var reference = Int(body, "variablesReference") ?? 0;

            return new
            {
                sessionId,
                expression,
                result = Str(body, "result"),
                type = Str(body, "type"),
                frameId = frame,
                expandable = reference > 0,
                variables = reference > 0 ? await ReadVariablesAsync(session, reference, 50, 1) : null,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId, expression };
        }
    }

    public async Task<object> SetExceptionBreakpointsAsync(string sessionId, string[] filters)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var body = await session.Client.RequestAsync("setExceptionBreakpoints",
                new Dictionary<string, object?> { ["filters"] = filters ?? [] }, Short);

            return new
            {
                sessionId,
                filters = filters ?? [],
                breakpoints = body.ValueKind == JsonValueKind.Object &&
                              body.TryGetProperty("breakpoints", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray()
                        .Select(item => (object)new { verified = item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True, message = Str(item, "message") })
                        .ToList()
                    : new List<object>(),
                note = "The .NET adapter understands 'all' and 'user-unhandled'.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    public Task<object> OutputAsync(string sessionId, int lines)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return Task.FromResult<object>(NoSession(sessionId));

        var count = Math.Clamp(lines, 1, 500);
        return Task.FromResult<object>(new
        {
            sessionId,
            state = session.State,
            lines = session.Client.ReadOutput(count),
            stderr = session.Client.ReadStderr(Math.Min(count, 100)),
        });
    }

    // ── ending ───────────────────────────────────────────────────────────────
    public async Task<object> TerminateAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return NoSession(sessionId);

        await session.DisposeAsync();
        return new
        {
            terminated = true,
            sessionId,
            program = session.Program,
            lastState = session.State,
            output = session.Client.ReadOutput(40),
        };
    }

    public Task<object> SessionsAsync()
    {
        var sessions = _sessions.Values
            .OrderBy(session => session.StartedAt)
            .Select(session => new
            {
                sessionId = session.Id,
                mode = session.Mode,
                program = session.Program,
                processId = session.DebuggeeProcessId,
                state = session.State,
                alive = session.Client.IsAlive,
                stoppedThreadId = session.StoppedThreadId,
                stopReason = session.StopReason,
                stopLocation = session.StopLocation,
                breakpointFiles = session.Breakpoints.Keys.ToList(),
                startedAt = session.StartedAt,
            })
            .ToList<object>();

        return Task.FromResult<object>(new
        {
            count = sessions.Count,
            sessions,
            adapter = _tool.Locate(),
            note = sessions.Count == 0 ? "No debug session. debug_launch starts one." : "Session state is live: a stop that happened between calls is still reflected here.",
        });
    }

    // ── session plumbing ─────────────────────────────────────────────────────
    private static async Task WaitForInitializedAsync(DapClient client, Task<JsonElement> startTask)
    {
        var initialized = client.WaitForEventAsync("initialized", InitializeTimeout);
        var first = await Task.WhenAny(initialized, startTask);
        if (first == startTask)
        {
            await startTask; // throws when the adapter refused the launch/attach
            return;
        }

        await initialized; // null on timeout: configurationDone below reports the real failure
    }

    private async Task<List<object>> ApplyInitialBreakpointsAsync(DebugSession session, BreakpointSpec[]? breakpoints,
        string workingDirectory)
    {
        var reports = new List<object>();
        if (breakpoints is null || breakpoints.Length == 0) return reports;

        foreach (var group in breakpoints
                     .Where(spec => spec.Line > 0 && !string.IsNullOrWhiteSpace(spec.File))
                     .GroupBy(spec => ResolveFile(spec.File, workingDirectory)!, StringComparer.OrdinalIgnoreCase))
        {
            session.Breakpoints[group.Key] = group.ToArray();
            reports.Add(await SendBreakpointsAsync(session, group.Key));
        }

        return reports;
    }

    private static async Task<object> SendBreakpointsAsync(DebugSession session, string file)
    {
        var specs = session.Breakpoints.TryGetValue(file, out var current) ? current : [];

        var payload = new List<Dictionary<string, object?>>();
        foreach (var spec in specs)
        {
            var entry = new Dictionary<string, object?> { ["line"] = spec.Line };
            if (!string.IsNullOrWhiteSpace(spec.Condition)) entry["condition"] = spec.Condition;
            if (!string.IsNullOrWhiteSpace(spec.HitCondition)) entry["hitCondition"] = spec.HitCondition;
            payload.Add(entry);
        }

        var arguments = new Dictionary<string, object?>
        {
            ["source"] = new Dictionary<string, object?> { ["name"] = Path.GetFileName(file), ["path"] = file },
            ["breakpoints"] = payload,
            ["sourceModified"] = false,
        };

        var body = await session.Client.RequestAsync("setBreakpoints", arguments, TimeSpan.FromSeconds(15));

        var verified = new List<object>();
        var unverified = new List<object>();
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("breakpoints", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var ok = item.TryGetProperty("verified", out var value) && value.ValueKind == JsonValueKind.True;
                var entry = new
                {
                    id = Int(item, "id"),
                    line = Int(item, "line"),
                    verified = ok,
                    message = Str(item, "message"),
                };
                (ok ? verified : unverified).Add(entry);
            }
        }

        return new { file, requested = payload.Count, verified, unverified };
    }

    private async Task<object> ResumeAsync(string sessionId, string command, int? threadId, int timeoutSeconds)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        try
        {
            var thread = threadId ?? session.StoppedThreadId ?? await FirstThreadIdAsync(session);
            var arguments = new Dictionary<string, object?> { ["threadId"] = thread };
            if (command == "continue") arguments["singleThread"] = false;

            await session.Client.RequestAsync(command, arguments, Short);

            session.State = "running";
            session.StopReason = null;
            session.StoppedThreadId = null;
            session.StopLocation = null;

            var stop = await session.Client.WaitForStopAsync(
                TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)), session.LastStopSequence);
            await ApplyStopAsync(session, stop);

            return Describe(session, null);
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    private async Task ApplyStopAsync(DebugSession session, DapMessage? stop)
    {
        if (stop is null)
        {
            if (session.State != "stopped") session.State = "running";
            return;
        }

        session.LastStopSequence = stop.Seq;

        if (stop.Event == "stopped")
        {
            session.State = "stopped";
            session.StopReason = stop.BodyString("reason") ?? "unknown";
            if (stop.Body.ValueKind == JsonValueKind.Object &&
                stop.Body.TryGetProperty("threadId", out var threadId) && threadId.ValueKind == JsonValueKind.Number)
                session.StoppedThreadId = threadId.GetInt32();

            await FillStopLocationAsync(session);
            return;
        }

        session.State = "exited";
        session.StopReason = stop.Event;
        session.StoppedThreadId = null;
        session.StopLocation = null;
    }

    private static async Task FillStopLocationAsync(DebugSession session)
    {
        try
        {
            var frames = await ReadFramesAsync(session, session.StoppedThreadId, 0, 1);
            if (frames.Count == 0) return;

            var frame = frames[0];
            session.StopLocation = frame.File is null ? frame.Name : $"{frame.File}:{frame.Line}";
        }
        catch
        {
            // The location is a convenience: a stack that cannot be read still leaves the stop valid.
        }
    }

    private static async Task<List<FrameInfo>> ReadFramesAsync(DebugSession session, int? threadId,
        int startFrame, int levels)
    {
        var thread = threadId ?? session.StoppedThreadId ?? await FirstThreadIdAsync(session);
        var arguments = new Dictionary<string, object?>
        {
            ["threadId"] = thread,
            ["startFrame"] = Math.Max(0, startFrame),
            ["levels"] = Math.Clamp(levels, 1, 200),
        };

        var body = await session.Client.RequestAsync("stackTrace", arguments, TimeSpan.FromSeconds(15));

        var frames = new List<FrameInfo>();
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("stackFrames", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
                frames.Add(new FrameInfo(Int(item, "id") ?? 0, Str(item, "name") ?? "(unknown)",
                    SourcePath(item), Int(item, "line") ?? 0, Int(item, "column") ?? 0));
        }

        return frames;
    }

    private static async Task<List<object>> ReadVariablesAsync(DebugSession session, int reference, int max, int depth)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["variablesReference"] = reference,
            ["start"] = 0,
            ["count"] = max,
        };

        var body = await session.Client.RequestAsync("variables", arguments, Short);

        var result = new List<object>();
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("variables", out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            var childReference = Int(item, "variablesReference") ?? 0;
            var entry = new Dictionary<string, object?>
            {
                ["name"] = Str(item, "name"),
                ["value"] = Str(item, "value"),
                ["type"] = Str(item, "type"),
                ["expandable"] = childReference > 0,
            };

            if (depth > 1 && childReference > 0 && result.Count < max)
                entry["children"] = await ReadVariablesAsync(session, childReference, max, depth - 1);

            result.Add(entry);
        }

        return result;
    }

    private static async Task<int?> TopFrameIdAsync(DebugSession session)
    {
        var frames = await ReadFramesAsync(session, session.StoppedThreadId, 0, 1);
        return frames.Count > 0 ? frames[0].Id : null;
    }

    private static async Task<int> FirstThreadIdAsync(DebugSession session)
    {
        var body = await session.Client.RequestAsync("threads", null, Short);
        if (body.ValueKind == JsonValueKind.Object &&
            body.TryGetProperty("threads", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
                if (Int(item, "id") is int id) return id;
        }

        throw new DebugAdapterException("The adapter reported no threads.");
    }

    private object Describe(DebugSession session, List<object>? breakpointReports)
    {
        var processEvent = session.Client.LatestEvent("process");
        if (processEvent is not null && processEvent.Body.ValueKind == JsonValueKind.Object &&
            processEvent.Body.TryGetProperty("systemProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number)
            session.DebuggeeProcessId = pid.GetInt32();

        return new
        {
            sessionId = session.Id,
            mode = session.Mode,
            program = session.Program,
            processId = session.DebuggeeProcessId,
            state = session.State,
            stoppedThreadId = session.StoppedThreadId,
            stopReason = session.StopReason,
            stopLocation = session.StopLocation,
            breakpoints = breakpointReports,
            output = session.Client.ReadOutput(40),
            note = session.State switch
            {
                "stopped" => "Stopped. debug_stack for the frames, debug_variables for the locals, debug_evaluate for an expression; debug_continue or debug_step to move.",
                "running" => "Running: nothing stopped within the wait window. Call debug_continue again (it waits for the next stop) or debug_pause.",
                "exited" => "The debuggee exited. debug_output has the console output; debug_terminate closes the session.",
                _ => "The session is starting.",
            },
        };
    }

    // ── resolution ───────────────────────────────────────────────────────────
    private async Task<(string? Target, string? Error)> ResolveProgramAsync(string? program, string? projectName)
    {
        if (!string.IsNullOrWhiteSpace(program))
        {
            var file = ResolveFile(program, BaseDirectory())!;
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var built = FindBuiltAssembly(file);
                return built is null
                    ? (null, $"No built assembly under '{Path.Combine(Path.GetDirectoryName(file)!, "bin")}' for " +
                             $"'{Path.GetFileName(file)}'. Run build_project first.")
                    : (built, null);
            }

            return File.Exists(file) ? (file, null) : (null, $"Program not found: {file}");
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return (null, "No solution is loaded, so projectName cannot be resolved; pass program as the path to a built .dll.");

        Project? project;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = solution.Projects.FirstOrDefault(
                p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return (null, $"Project '{projectName}' is not in the solution.");
        }
        else
        {
            var runnable = solution.Projects.Where(IsRunnable).ToList();
            if (runnable.Count == 0) return (null, "No runnable project in the solution; pass program explicitly.");
            if (runnable.Count > 1)
                return (null, $"Several runnable projects ({string.Join(", ", runnable.Select(p => p.Name))}); pass projectName or program.");
            project = runnable[0];
        }

        if (project.FilePath is null) return (null, $"Project '{project.Name}' has no file on disk.");

        var assembly = FindBuiltAssembly(project.FilePath);
        return assembly is null
            ? (null, $"'{project.Name}' has no built assembly under bin/. Run build_project first (the newest build wins).")
            : (assembly, null);
    }

    /// <summary>The newest assembly the project produced, so a debug launch does not need a path.</summary>
    private static string? FindBuiltAssembly(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath);
        if (directory is null) return null;

        var bin = Path.Combine(directory, "bin");
        if (!Directory.Exists(bin)) return null;

        var name = Path.GetFileNameWithoutExtension(projectPath) + ".dll";
        return Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsRunnable(Project project) =>
        project.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;

    private string BaseDirectory()
    {
        var solutionPath = _workspace.LoadedSolutionPath ?? _workspace.CurrentSolution?.FilePath;
        var directory = string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetDirectoryName(solutionPath);
        return directory ?? Directory.GetCurrentDirectory();
    }

    private string ResolveWorkingDirectory(string? workingDirectory, string program) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(program) ?? Directory.GetCurrentDirectory()
            : ResolveFile(workingDirectory, BaseDirectory())!;

    private static string? ResolveFile(string? file, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        return Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(baseDirectory, file));
    }

    private static Dictionary<string, object?> InitializeArguments() => new()
    {
        ["adapterID"] = "coreclr",
        ["clientID"] = "VisualMCP",
        ["clientName"] = "VisualMCP",
        ["linesStartAt1"] = true,
        ["columnsStartAt1"] = true,
        ["pathFormat"] = "path",
        ["supportsVariableType"] = true,
        ["supportsVariablePaging"] = true,
    };

    private string NextSessionId() => $"dbg-{Interlocked.Increment(ref _counter)}";

    private static object NoSession(string sessionId) => new
    {
        error = $"No debug session '{sessionId}'.",
        sessionId,
        hint = "Call debug_sessions to list the live sessions, or debug_launch to start one.",
    };

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? SourcePath(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object) return null;
        if (!frame.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) return null;
        return source.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;
    }

    private sealed record FrameInfo(int Id, string Name, string? File, int Line, int Column);
}
