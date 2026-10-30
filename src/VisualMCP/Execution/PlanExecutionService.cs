using System.Diagnostics;
using System.Text.Json;
using Snitch.Workspace;

namespace VisualMCP.Execution;

/// <summary>
/// Runs a plan: a DAG of steps executed natively, in parallel where the graph allows it, with no model
/// in the loop. It is the deterministic half of what a multi-agent workflow does — fan-out, ordering,
/// isolation and aggregation — and it answers in one round trip instead of one per step.
///
/// Two rules make it safe on a single shared tree:
/// a step whose declared <c>paths</c> overlap a running step's waits, so two parallel edits never touch
/// the same file; and dependencies are strict, so a failed step skips the steps that needed it unless the
/// failure was declared tolerable.
/// </summary>
public sealed class PlanExecutionService : IPlanExecutionService
{
    private const string Ok = "ok";
    private const string Failed = "failed";
    private const string Skipped = "skipped";

    private readonly IPlanCommandRunner _commands;
    private readonly IPlanToolInvoker _tools;
    private readonly IRoslynWorkspace? _workspace;

    public PlanExecutionService(
        IPlanCommandRunner commands, IPlanToolInvoker tools, IRoslynWorkspace? workspace = null)
    {
        _commands = commands;
        _tools = tools;
        _workspace = workspace;
    }

    public async Task<object> RunAsync(PlanSpec spec, CancellationToken ct = default)
    {
        var defaultDirectory = _workspace?.LoadedSolutionPath is { Length: > 0 } solution
            ? Path.GetDirectoryName(solution) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        var compiled = PlanCompiler.Compile(spec, _tools.Names, defaultDirectory);
        if (compiled.Errors.Count > 0)
        {
            return new
            {
                verdict = "rejected",
                error = "The plan was not started: nothing ran. Fix the errors and call run_plan again.",
                errors = compiled.Errors,
            };
        }

        var clock = Stopwatch.StartNew();
        var pending = new List<PlanStep>(compiled.Steps);
        var running = new Dictionary<string, Task<Outcome>>(StringComparer.OrdinalIgnoreCase);
        var runningSteps = new Dictionary<string, PlanStep>(StringComparer.OrdinalIgnoreCase);
        var done = new Dictionary<string, Outcome>(StringComparer.OrdinalIgnoreCase);
        var report = new List<Outcome>(compiled.Steps.Count);

        var peak = 0;
        var deferred = 0;
        var stopped = false;
        string? stoppedReason = null;

        while (pending.Count > 0 || running.Count > 0)
        {
            // A step whose dependency failed will never run: record it as skipped instead of leaving it pending.
            for (var i = 0; i < pending.Count; i++)
            {
                if (Evaluate(pending[i], done, out var reason) != StepState.Blocked)
                    continue;

                var skip = Outcome.Skip(pending[i], clock.ElapsedMilliseconds, reason);
                pending.RemoveAt(i--);
                done[skip.Id] = skip;
                report.Add(skip);
            }

            if (!stopped && ct.IsCancellationRequested)
            {
                stopped = true;
                stoppedReason = "the call was cancelled";
            }

            for (var i = 0; i < pending.Count && !stopped; i++)
            {
                if (running.Count >= compiled.MaxConcurrency)
                    break;

                var step = pending[i];
                if (Evaluate(step, done, out _) != StepState.Ready)
                    continue;

                if (runningSteps.Values.Any(other => Conflicts(step, other)))
                {
                    deferred++;
                    continue;
                }

                pending.RemoveAt(i--);
                runningSteps[step.Id] = step;
                running[step.Id] = RunStepAsync(step, compiled.OutputChars, clock, ct);
                peak = Math.Max(peak, running.Count);
            }

            if (stopped && running.Count == 0)
            {
                foreach (var step in pending)
                {
                    var skip = Outcome.Skip(step, clock.ElapsedMilliseconds, stoppedReason ?? "the plan stopped");
                    done[skip.Id] = skip;
                    report.Add(skip);
                }

                pending.Clear();
                break;
            }

            if (running.Count == 0)
            {
                // Unreachable while the graph is acyclic (cycles are rejected up front); never hang on it.
                foreach (var step in pending)
                    report.Add(Outcome.Skip(step, clock.ElapsedMilliseconds, "no runnable step"));
                pending.Clear();
                break;
            }

            var finished = await Task.WhenAny(running.Values).ConfigureAwait(false);
            var finishedId = running.First(pair => ReferenceEquals(pair.Value, finished)).Key;
            var finishedStep = runningSteps[finishedId];
            running.Remove(finishedId);
            runningSteps.Remove(finishedId);

            var outcome = await finished.ConfigureAwait(false);
            done[outcome.Id] = outcome;
            report.Add(outcome);

            if (outcome.Status != Ok && !finishedStep.ContinueOnError && compiled.StopOnFailure)
            {
                stopped = true;
                stoppedReason = $"the plan stopped after '{outcome.Id}' failed";
            }
        }

        clock.Stop();

        var hardFailures = report.Count(o => o.Status == Failed && !o.ContinueOnError);
        var softFailures = report.Count(o => o.Status == Failed && o.ContinueOnError);
        var verdict = hardFailures > 0 ? "failed" : softFailures > 0 ? "partial" : "ok";

        var ordered = report.OrderBy(o => o.StartedMs).ToArray();
        var sumStepMs = ordered.Sum(o => o.DurationMs);

        return new
        {
            plan = string.IsNullOrWhiteSpace(spec.Name) ? null : spec.Name,
            verdict,
            wallTimeMs = (long)clock.Elapsed.TotalMilliseconds,
            counts = new
            {
                total = ordered.Length,
                ok = ordered.Count(o => o.Status == Ok),
                failed = ordered.Count(o => o.Status == Failed),
                skipped = ordered.Count(o => o.Status == Skipped),
            },
            concurrency = new
            {
                requested = compiled.MaxConcurrency,
                peak,
                serializedByPaths = deferred,
                sumStepMs,
                savedMs = Math.Max(0, sumStepMs - (long)clock.Elapsed.TotalMilliseconds),
                note = peak <= 1 && ordered.Length > 1
                    ? "The steps never overlapped: the graph, the path declarations or maxConcurrency forced them to run one at a time."
                    : "Steps that declare overlapping paths are kept apart on purpose; everything else overlapped.",
            },
            steps = ordered.Select(o => new
            {
                id = o.Id,
                kind = o.Kind,
                target = o.Target,
                status = o.Status,
                dependsOn = o.DependsOn,
                startedMs = o.StartedMs,
                durationMs = o.DurationMs,
                exitCode = o.ExitCode,
                error = o.Error,
                truncated = o.Truncated,
                result = o.Result,
            }),
        };
    }

    private enum StepState
    {
        Ready,
        Waiting,
        Blocked,
    }

    private static StepState Evaluate(PlanStep step, IReadOnlyDictionary<string, Outcome> done, out string? reason)
    {
        reason = null;
        var waiting = false;

        foreach (var dep in step.DependsOn)
        {
            if (!done.TryGetValue(dep, out var outcome))
            {
                waiting = true;
                continue;
            }

            if (outcome.Status == Ok || outcome.ContinueOnError)
                continue;

            reason = $"dependency '{dep}' {outcome.Status}";
            return StepState.Blocked;
        }

        return waiting ? StepState.Waiting : StepState.Ready;
    }

    /// <summary>
    /// Two steps collide when one declares a file or directory the other also declares — equal, or nested.
    /// Paths are already absolute and normalised by the compiler.
    /// </summary>
    private static bool Conflicts(PlanStep candidate, PlanStep running)
    {
        foreach (var mine in candidate.Paths)
        {
            foreach (var theirs in running.Paths)
            {
                if (string.Equals(mine, theirs, StringComparison.Ordinal)
                    || mine.StartsWith(theirs + "/", StringComparison.Ordinal)
                    || theirs.StartsWith(mine + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<Outcome> RunStepAsync(PlanStep step, int outputChars, Stopwatch clock, CancellationToken ct)
    {
        var startedMs = clock.ElapsedMilliseconds;
        var timer = Stopwatch.StartNew();

        try
        {
            return step.Kind == PlanStepKind.Command
                ? await RunCommandAsync(step, outputChars, startedMs, timer, ct).ConfigureAwait(false)
                : await RunToolAsync(step, outputChars, startedMs, timer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Outcome.Fail(step, startedMs, timer, $"timed out after {step.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            var message = ex is PlanStepException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            return Outcome.Fail(step, startedMs, timer, message);
        }
    }

    private async Task<Outcome> RunCommandAsync(
        PlanStep step, int outputChars, long startedMs, Stopwatch timer, CancellationToken ct)
    {
        var raw = await _commands
            .RunAsync(step.Command!, step.Shell!, step.WorkingDirectory, step.TimeoutSeconds, ct)
            .ConfigureAwait(false);

        var json = JsonSerializer.SerializeToElement(raw);
        var exitCode = Int(json, "exitCode");
        var timedOut = Bool(json, "timedOut");
        var error = String(json, "error");
        var ok = error is null && !timedOut && exitCode is 0;

        return new Outcome
        {
            Id = step.Id,
            Kind = "command",
            Target = "command",
            Status = ok ? Ok : Failed,
            StartedMs = startedMs,
            DurationMs = (long)timer.Elapsed.TotalMilliseconds,
            ExitCode = exitCode,
            Error = error ?? (timedOut ? $"timed out after {step.TimeoutSeconds}s" : ok ? null : $"exit code {exitCode}"),
            DependsOn = step.DependsOn,
            ContinueOnError = step.ContinueOnError,
            Result = new
            {
                exitCode,
                timedOut,
                stdout = Clip(String(json, "stdout"), outputChars),
                stderr = Clip(String(json, "stderr"), outputChars),
            },
        };
    }

    private async Task<Outcome> RunToolAsync(
        PlanStep step, int outputChars, long startedMs, Stopwatch timer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

        var result = await _tools.InvokeAsync(step.Target, Normalize(step.Arguments), timeout.Token).ConfigureAwait(false);
        var json = JsonSerializer.SerializeToElement(result);

        // A tool reports a refused or impossible operation by returning a top-level "error" instead of
        // throwing (see ToolServices.Missing) — that is a failed step here, not a success with a message.
        var error = json.ValueKind == JsonValueKind.Object ? String(json, "error") : null;
        var (payload, truncated) = Shape(json, outputChars);

        return new Outcome
        {
            Id = step.Id,
            Kind = "tool",
            Target = step.Target,
            Status = error is null ? Ok : Failed,
            StartedMs = startedMs,
            DurationMs = (long)timer.Elapsed.TotalMilliseconds,
            Error = error,
            DependsOn = step.DependsOn,
            ContinueOnError = step.ContinueOnError,
            Result = payload,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// A step's <c>arguments</c> is a free-form object, so its JSON schema has no <c>type</c> to reject a
    /// string: a caller that sends the object JSON-encoded gets it parsed here instead of a confusing
    /// "missing argument" from the tool.
    /// </summary>
    private static JsonElement? Normalize(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.String } encoded)
            return arguments;

        try
        {
            using var document = JsonDocument.Parse(encoded.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    /// <summary>Keeps a result intact while it fits the budget; past it, a preview with its real size.</summary>
    private static (object? Payload, bool Truncated) Shape(JsonElement json, int maxChars)
    {
        var text = json.ValueKind == JsonValueKind.Undefined ? "null" : json.GetRawText();
        if (text.Length <= maxChars)
            return (json, false);

        return (new { truncated = true, chars = text.Length, preview = text[..maxChars] }, true);
    }

    private static string? String(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool Bool(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;

    private static string Clip(string? text, int max) =>
        text is null ? "" : text.Length <= max ? text : text[..max] + $"…(+{text.Length - max} chars)";

    private sealed class Outcome
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Target { get; init; }
        public required string Status { get; init; }
        public long StartedMs { get; init; }
        public long DurationMs { get; init; }
        public int? ExitCode { get; init; }
        public string? Error { get; init; }
        public bool Truncated { get; init; }
        public object? Result { get; init; }
        public IReadOnlyList<string> DependsOn { get; init; } = [];
        public bool ContinueOnError { get; init; }

        public static Outcome Skip(PlanStep step, long nowMs, string? reason) => new()
        {
            Id = step.Id,
            Kind = step.Kind == PlanStepKind.Tool ? "tool" : "command",
            Target = step.Target,
            Status = Skipped,
            StartedMs = nowMs,
            Error = reason,
            DependsOn = step.DependsOn,
            ContinueOnError = step.ContinueOnError,
        };

        public static Outcome Fail(PlanStep step, long startedMs, Stopwatch timer, string error) => new()
        {
            Id = step.Id,
            Kind = step.Kind == PlanStepKind.Tool ? "tool" : "command",
            Target = step.Target,
            Status = Failed,
            StartedMs = startedMs,
            DurationMs = (long)timer.Elapsed.TotalMilliseconds,
            Error = error,
            DependsOn = step.DependsOn,
            ContinueOnError = step.ContinueOnError,
        };
    }
}
