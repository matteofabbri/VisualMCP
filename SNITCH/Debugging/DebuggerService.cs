using System.Collections.Concurrent;
using System.Diagnostics;
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
        var session = new DebugSession(NextSessionId(), client, "launch")
        {
            Program = target,
            Launch = new LaunchSpec(target, programArgs, workingDirectory, environmentJson, stopAtEntry),
        };
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
                await WaitForStopAsync(session, TimeSpan.FromSeconds(Math.Clamp(waitForStopSeconds, 1, 300)));

            return await DescribeAsync(session, breakpointReports);
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
                await WaitForStopAsync(session, TimeSpan.FromSeconds(Math.Clamp(waitForStopSeconds, 1, 300)));

            return await DescribeAsync(session, null);
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

            ResetHitCounts(session, full);
            return await SendBreakpointsAsync(session, full);
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId, file = full };
        }
    }

    public async Task<object> SetFunctionBreakpointsAsync(string sessionId, FunctionBreakpointSpec[]? breakpoints,
        bool clear)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        session.FunctionBreakpoints = clear
            ? []
            : (breakpoints ?? []).Where(spec => !string.IsNullOrWhiteSpace(spec.Name)).ToArray();

        try
        {
            var payload = session.FunctionBreakpoints.Select(spec =>
            {
                var entry = new Dictionary<string, object?> { ["name"] = spec.Name.Trim() };
                if (!string.IsNullOrWhiteSpace(spec.Condition)) entry["condition"] = spec.Condition;
                return entry;
            }).ToList();

            var body = await session.Client.RequestAsync("setFunctionBreakpoints",
                new Dictionary<string, object?> { ["breakpoints"] = payload }, TimeSpan.FromSeconds(15));

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
                        file = SourcePath(item),
                        verified = ok,
                        message = Str(item, "message"),
                    };
                    (ok ? verified : unverified).Add(entry);
                }
            }

            return new
            {
                sessionId,
                requested = payload.Count,
                verified,
                unverified,
                note = payload.Count == 0
                    ? "The function-breakpoint set is now empty."
                    : "A function breakpoint is set before the method is loaded: an unverified one names a method the " +
                      "runtime has not reached yet, and it binds when the module loads.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId };
        }
    }

    public async Task<object> SetDataBreakpointsAsync(string sessionId, string file, int line, string member,
        bool clear)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        if (clear)
        {
            var removed = session.DataBreakpoints.Values
                .Where(data => string.IsNullOrWhiteSpace(member) ||
                               string.Equals(data.Member, member.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (removed.Length == 0) return new { error = $"No data breakpoint is set on '{member}'.", sessionId };

            foreach (var data in removed) session.DataBreakpoints.TryRemove(data.Member, out _);

            var restored = new List<object>();
            foreach (var affected in removed.SelectMany(data => data.Sites)
                         .Select(site => site.File)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ResetHitCounts(session, affected);
                restored.Add(await SendOrReportAsync(session, affected));
            }

            return new { sessionId, cleared = removed.Select(data => data.Member).ToArray(), breakpoints = restored };
        }

        if (string.IsNullOrWhiteSpace(file)) return new { error = "A source file is required." };
        if (string.IsNullOrWhiteSpace(member))
            return new { error = "A member (field or property) name is required." };
        if (line <= 0) return new { error = "A 1-based line where the member appears is required." };

        var full = ResolveFile(file, BaseDirectory())!;

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new
            {
                error = "No solution is loaded, so the member's write sites cannot be resolved: pass a file that belongs to it.",
                sessionId,
            };

        var (breakpoint, error) = await DataBreakpointResolver.ResolveAsync(solution, full, line, member.Trim());
        if (breakpoint is null) return new { error, sessionId };

        session.DataBreakpoints[breakpoint.Member] = breakpoint;

        var reports = new List<object>();
        foreach (var affected in breakpoint.Sites.Select(site => site.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ResetHitCounts(session, affected);
            reports.Add(await SendOrReportAsync(session, affected));
        }

        return new
        {
            sessionId,
            member = breakpoint.Member,
            symbol = breakpoint.Symbol,
            sites = breakpoint.Sites.Select(site => new { file = site.File, line = site.Line, kind = site.Kind }),
            count = breakpoint.Sites.Length,
            breakpoints = reports,
            note = "The adapter has no data-breakpoint request, so these are the solution's write sites for the member, " +
                   "installed as line breakpoints: a write from code outside the solution's source does not stop.",
        };
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

            await WaitForStopAsync(session, TimeSpan.FromSeconds(10));
            return await DescribeAsync(session, null);
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

    public async Task<object> EvaluateAsync(string sessionId, string expression, int? frameId, string? context,
        string? newValue)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);
        if (string.IsNullOrWhiteSpace(expression)) return new { error = "An expression is required." };

        try
        {
            var frame = frameId ?? await TopFrameIdAsync(session);

            string? assignedValue = null;
            if (newValue is not null)
            {
                var assignment = new Dictionary<string, object?>
                {
                    ["expression"] = expression,
                    ["value"] = newValue,
                };
                if (frame is int assignmentFrame) assignment["frameId"] = assignmentFrame;

                var assigned = await session.Client.RequestAsync("setExpression", assignment, Short);
                assignedValue = Str(assigned, "value") ?? newValue;
            }

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
                assigned = assignedValue is not null,
                newValue = assignedValue,
                result = Str(body, "result"),
                type = Str(body, "type"),
                frameId = frame,
                expandable = reference > 0,
                variables = reference > 0 ? await ReadVariablesAsync(session, reference, 50, 1) : null,
                note = assignedValue is null
                    ? null
                    : $"'{expression}' was written into the running process: the value above is what it holds now.",
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, sessionId, expression };
        }
    }

    public async Task<object> SetExceptionBreakpointsAsync(string sessionId, string[]? filters,
        string[]? exceptionTypes, bool details)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        var wantedFilters = (filters ?? []).Where(filter => !string.IsNullOrWhiteSpace(filter)).ToArray();
        var wantedTypes = (exceptionTypes ?? []).Where(type => !string.IsNullOrWhiteSpace(type)).ToArray();
        if (wantedFilters.Length == 0 && wantedTypes.Length == 0)
        {
            // Nothing to configure: answer the question a caller actually asks here — what was thrown.
            var current = await ReadExceptionInfoAsync(session);
            return new
            {
                sessionId,
                state = session.State,
                exception = current ?? (object)new
                {
                    available = false,
                    reason = "The process is not stopped on an exception. Pass filters (\"all\", \"user-unhandled\") " +
                             "or exceptionTypes to change the settings instead.",
                },
                configured = new { filters = session.ExceptionFilters, exceptionTypes = session.ExceptionTypes },
            };
        }

        try
        {
            var arguments = new Dictionary<string, object?>();
            if (wantedFilters.Length > 0) arguments["filters"] = wantedFilters;

            if (wantedTypes.Length > 0)
            {
                // A type condition belongs to a filter: it narrows when that filter breaks. The first
                // filter named is the one that carries them, "all" (every throw) by default.
                var filterId = wantedFilters.FirstOrDefault() ?? "all";
                arguments["filterOptions"] = wantedTypes
                    .Select(type => new Dictionary<string, object?> { ["filterId"] = filterId, ["condition"] = type })
                    .ToArray();
            }

            var body = await session.Client.RequestAsync("setExceptionBreakpoints", arguments, Short);

            session.ExceptionFilters = wantedFilters;
            session.ExceptionTypes = wantedTypes;

            var exception = details ? await ReadExceptionInfoAsync(session) : null;

            return new
            {
                sessionId,
                filters = wantedFilters,
                exceptionTypes = wantedTypes,
                breakpoints = body.ValueKind == JsonValueKind.Object &&
                              body.TryGetProperty("breakpoints", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray()
                        .Select(item => (object)new { verified = item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True, message = Str(item, "message") })
                        .ToList()
                    : new List<object>(),
                exception,
                note = wantedTypes.Length > 0
                    ? "A type condition matches the exception's full type name exactly (System.NullReferenceException); " +
                      "prefix ! to exclude it. Base classes are not matched: name each type you want."
                    : "The adapter understands 'all' (every throw) and 'user-unhandled'; add exceptionTypes to narrow them.",
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

    /// <summary>
    /// Relaunch a launched session: the same program, arguments and environment, the same breakpoints,
    /// function breakpoints and exception settings. The restart is the edit → build → run loop in the
    /// absence of edit-and-continue, which this adapter only offers over GDB/MI.
    /// </summary>
    public async Task<object> RestartAsync(string sessionId, bool rebuild)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return NoSession(sessionId);

        var launch = session.Launch;
        if (launch is null)
            return new
            {
                error = $"Session '{sessionId}' attached to a running process: there is no launcher to repeat.",
                hint = "Restart the process where it was started, then debug_attach again.",
            };

        var breakpoints = session.Breakpoints
            .SelectMany(entry => entry.Value.Select(spec => new BreakpointSpec
            {
                File = entry.Key,
                Line = spec.Line,
                Condition = spec.Condition,
                HitCondition = spec.HitCondition,
            }))
            .ToArray();
        var functions = session.FunctionBreakpoints;
        var exceptionFilters = session.ExceptionFilters;
        var exceptionTypes = session.ExceptionTypes;

        object? build = null;
        if (rebuild)
        {
            build = await RebuildAsync(launch.Program);
            if (build is not null &&
                JsonSerializer.SerializeToElement(build).TryGetProperty("rebuilt", out var rebuilt) &&
                rebuilt.ValueKind == JsonValueKind.False)
                return new { error = "The rebuild failed, so the session was left running.", build };
        }

        await TerminateAsync(sessionId);

        var relaunched = await LaunchAsync(launch.Program, null, launch.Args, launch.WorkingDirectory,
            launch.EnvironmentJson, launch.StopAtEntry, breakpoints, waitForStopSeconds: 15);

        var relaunchedJson = JsonSerializer.SerializeToElement(relaunched);
        var newSessionId = relaunchedJson.TryGetProperty("sessionId", out var idValue) ? idValue.GetString() : null;

        var reapplied = new List<object>();
        if (newSessionId is not null)
        {
            if (functions.Length > 0)
                reapplied.Add(await SetFunctionBreakpointsAsync(newSessionId, functions, clear: false));
            if (exceptionFilters.Length > 0 || exceptionTypes.Length > 0)
                reapplied.Add(await SetExceptionBreakpointsAsync(newSessionId, exceptionFilters, exceptionTypes, details: false));
        }

        return new
        {
            restarted = true,
            previousSessionId = sessionId,
            sessionId = newSessionId,
            build,
            reapplied,
            session = relaunched,
            note = newSessionId is null
                ? "The relaunch failed: the error is in 'session'."
                : "The breakpoints, function breakpoints and exception settings carried over; the previous session is gone.",
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
                functionBreakpoints = session.FunctionBreakpoints.Select(spec => spec.Name).ToList(),
                dataBreakpoints = session.DataBreakpoints.Keys.ToList(),
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

    /// <summary>A failed re-send is reported in place: the write sites stay known, the adapter did not take them.</summary>
    private static async Task<object> SendOrReportAsync(DebugSession session, string file)
    {
        try
        {
            return await SendBreakpointsAsync(session, file);
        }
        catch (Exception ex)
        {
            return new { file, error = ex.Message };
        }
    }

    private static async Task<object> SendBreakpointsAsync(DebugSession session, string file)
    {
        var specs = session.Breakpoints.TryGetValue(file, out var current) ? current : [];

        var payload = new List<Dictionary<string, object?>>();
        var lines = new HashSet<int>();
        foreach (var spec in specs)
        {
            if (spec.Line <= 0 || !lines.Add(spec.Line)) continue;
            var entry = new Dictionary<string, object?> { ["line"] = spec.Line };
            if (!string.IsNullOrWhiteSpace(spec.Condition)) entry["condition"] = spec.Condition;
            payload.Add(entry);
        }

        // A data breakpoint is the set of lines that write the member. Installing them with the
        // file's own breakpoints keeps them alive when debug_breakpoints replaces that list.
        var dataLines = new List<int>();
        foreach (var data in session.DataBreakpoints.Values)
        {
            foreach (var site in data.Sites)
            {
                if (!DebugPath.Equal(site.File, file) || !lines.Add(site.Line)) continue;
                dataLines.Add(site.Line);
                payload.Add(new Dictionary<string, object?> { ["line"] = site.Line });
            }
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

        return dataLines.Count == 0
            ? (object)new { file, requested = payload.Count, verified, unverified }
            : new { file, requested = payload.Count, verified, unverified, dataBreakpointLines = dataLines };
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
            session.StopFile = null;
            session.StopLine = 0;
            session.HitConditionSkips = 0;

            await WaitForStopAsync(session, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));

            return await DescribeAsync(session, null);
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
        session.StopFile = null;
        session.StopLine = 0;
    }

    /// <summary>
    /// Waits for the next stop the caller should see. A breakpoint whose hit condition is not
    /// satisfied yet is resumed transparently: the adapter has no hit counter, so the counting is
    /// here, and <see cref="DebugSession.HitConditionSkips"/> reports what was absorbed.
    /// </summary>
    private async Task<DapMessage?> WaitForStopAsync(DebugSession session, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            var stop = await session.Client.WaitForStopAsync(remaining, session.LastStopSequence);
            if (stop is null) return null;

            await ApplyStopAsync(session, stop);
            if (stop.Event != "stopped" || session.StopReason != "breakpoint") return stop;

            var spec = FindHitConditionedSpec(session);
            if (spec is null) return stop;

            var count = session.HitCounts.AddOrUpdate(HitKey(session), 1, (_, seen) => seen + 1);
            if (HitConditionSatisfied(spec.HitCondition, count)) return stop;

            // Not the hit the caller asked for: resume and keep looking, bounded so a condition that
            // can never be satisfied (a typo, a line revisited forever) still returns a stop.
            if (session.HitConditionSkips >= 500) return stop;
            session.HitConditionSkips++;

            await session.Client.RequestAsync("continue",
                new Dictionary<string, object?> { ["threadId"] = session.StoppedThreadId ?? 0 }, Short);

            session.State = "running";
            session.StopReason = null;
            session.StoppedThreadId = null;
            session.StopLocation = null;
            session.StopFile = null;
            session.StopLine = 0;
        }
    }

    private static string HitKey(DebugSession session) => $"{session.StopFile}:{session.StopLine}";

    /// <summary>The breakpoint the current stop belongs to, when it carries a hit condition.</summary>
    private static BreakpointSpec? FindHitConditionedSpec(DebugSession session)
    {
        if (session.StopFile is null || session.StopLine <= 0) return null;
        foreach (var entry in session.Breakpoints)
        {
            if (!DebugPath.Equal(entry.Key, session.StopFile)) continue;
            return entry.Value.FirstOrDefault(spec =>
                spec.Line == session.StopLine && !string.IsNullOrWhiteSpace(spec.HitCondition));
        }

        return null;
    }

    /// <summary>
    /// The DAP hit-condition syntax: <c>&gt;5</c>, <c>&gt;=5</c>, <c>&lt;5</c>, <c>&lt;=5</c>, <c>5</c>
    /// (exactly), <c>%3</c> (every third hit). An unreadable condition stops rather than loops.
    /// </summary>
    public static bool HitConditionSatisfied(string? hitCondition, int count)
    {
        if (string.IsNullOrWhiteSpace(hitCondition)) return true;

        var text = hitCondition.Trim();
        try
        {
            if (text.StartsWith("%", StringComparison.Ordinal))
                return int.Parse(text[1..].Trim()) is var every && every > 0 && count % every == 0;

            if (text.StartsWith(">=", StringComparison.Ordinal))
                return count >= int.Parse(text[2..].Trim());
            if (text.StartsWith("<=", StringComparison.Ordinal))
                return count <= int.Parse(text[2..].Trim());
            if (text.StartsWith(">", StringComparison.Ordinal))
                return count > int.Parse(text[1..].Trim());
            if (text.StartsWith("<", StringComparison.Ordinal))
                return count < int.Parse(text[1..].Trim());
            if (text.StartsWith("==", StringComparison.Ordinal))
                return count == int.Parse(text[2..].Trim());

            return count == int.Parse(text);
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static async Task FillStopLocationAsync(DebugSession session)
    {
        try
        {
            var frames = await ReadFramesAsync(session, session.StoppedThreadId, 0, 1);
            if (frames.Count == 0) return;

            var frame = frames[0];
            session.StopLocation = frame.File is null ? frame.Name : $"{frame.File}:{frame.Line}";
            session.StopFile = frame.File;
            session.StopLine = frame.Line;
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

    private async Task<object> DescribeAsync(DebugSession session, List<object>? breakpointReports)
    {
        var processEvent = session.Client.LatestEvent("process");
        if (processEvent is not null && processEvent.Body.ValueKind == JsonValueKind.Object &&
            processEvent.Body.TryGetProperty("systemProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number)
            session.DebuggeeProcessId = pid.GetInt32();

        var exception = session.State == "stopped" && session.StopReason == "exception"
            ? await ReadExceptionInfoAsync(session)
            : null;

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
            functionBreakpoints = session.FunctionBreakpoints.Select(spec => spec.Name).ToList(),
            dataBreakpoints = session.DataBreakpoints.Values
                .Select(data => new { member = data.Member, sites = data.Sites.Length })
                .ToList(),
            exceptionBreakpoints = new { filters = session.ExceptionFilters, exceptionTypes = session.ExceptionTypes },
            hitConditionSkips = session.HitConditionSkips,
            exception,
            output = session.Client.ReadOutput(40),
            note = session.State switch
            {
                "stopped" when session.StopReason == "exception" =>
                    "Stopped on an exception: the type, the stage and the details are in 'exception'. debug_stack walks " +
                    "the throw site, debug_variables the frame; debug_continue resumes.",
                "stopped" => "Stopped. debug_stack for the frames, debug_variables for the locals, debug_evaluate for an " +
                             "expression (pass newValue to change one); debug_continue or debug_step to move.",
                "running" => "Running: nothing stopped within the wait window. Call debug_continue again (it waits for the " +
                             "next stop) or debug_pause.",
                "exited" => "The debuggee exited. debug_output has the console output; debug_restart relaunches it, " +
                            "debug_terminate closes the session.",
                _ => "The session is starting.",
            },
        };
    }

    /// <summary>The stopped exception, as the adapter reports it: type, stage, message and the frames it carries.</summary>
    private static async Task<object?> ReadExceptionInfoAsync(DebugSession session)
    {
        if (session.StoppedThreadId is not int threadId) return null;

        try
        {
            var body = await session.Client.RequestAsync("exceptionInfo",
                new Dictionary<string, object?> { ["threadId"] = threadId }, Short);

            var hasDetails = body.ValueKind == JsonValueKind.Object &&
                             body.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object;

            return new
            {
                id = Str(body, "exceptionId"),
                description = Str(body, "description"),
                breakMode = Str(body, "breakMode"),
                type = hasDetails ? Str(body.GetProperty("details"), "typeName") : null,
                fullTypeName = hasDetails ? Str(body.GetProperty("details"), "fullTypeName") : null,
                message = hasDetails ? Str(body.GetProperty("details"), "message") : null,
                details = hasDetails ? body.GetProperty("details").Clone() : (JsonElement?)null,
            };
        }
        catch
        {
            // The stop is still valid without the description: a failed read is not a failed stop.
            return null;
        }
    }

    // ── restart ──────────────────────────────────────────────────────────────
    private static async Task<object> RebuildAsync(string program)
    {
        try
        {
            var project = FindProjectFile(program);
            if (project is null)
                return new { rebuilt = false, error = $"No .csproj was found above '{Path.GetFileName(program)}' to rebuild." };

            var configuration = program.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(project)!,
            };
            start.ArgumentList.Add("build");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(configuration);
            start.ArgumentList.Add("--nologo");

            using var process = Process.Start(start);
            if (process is null) return new { rebuilt = false, project, error = "'dotnet build' could not start." };

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { /* already gone */ } }

            var output = await stdout + await stderr;
            return new
            {
                rebuilt = process.HasExited && process.ExitCode == 0,
                project,
                configuration,
                exitCode = process.HasExited ? process.ExitCode : (int?)null,
                output = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new { rebuilt = false, error = $"The rebuild failed to run: {ex.Message}" };
        }
    }

    /// <summary>The project that produced an assembly: the nearest .csproj above its output folder.</summary>
    private static string? FindProjectFile(string program)
    {
        var assembly = Path.GetFileNameWithoutExtension(program);
        var directory = Path.GetDirectoryName(program);

        for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            var candidates = Directory.EnumerateFiles(directory, "*.csproj").OrderBy(name => name).ToList();
            if (candidates.Count > 0)
            {
                return candidates.FirstOrDefault(candidate => string.Equals(
                    Path.GetFileNameWithoutExtension(candidate), assembly, StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static void ResetHitCounts(DebugSession session, string file)
    {
        var prefix = file + ":";
        foreach (var key in session.HitCounts.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                session.HitCounts.TryRemove(key, out _);
        }
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
