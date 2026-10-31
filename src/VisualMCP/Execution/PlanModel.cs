using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Snitch.Platform;

namespace VisualMCP.Execution;

/// <summary>
/// A plan: a dependency graph of steps that <c>run_plan</c> executes natively, with no model in the loop.
/// </summary>
public sealed class PlanSpec
{
    [Description("Optional label for the plan, shown in the report.")]
    public string? Name { get; set; }

    [Description(
        "The steps. Each one is either { kind: 'command', command, shell, workingDirectory } or " +
        "{ kind: 'tool', tool, arguments }, plus optional id, dependsOn, paths, forEach, as, " +
        "timeoutSeconds, continueOnError. Steps with no dependency between them run concurrently.")]
    public List<PlanStepSpec>? Steps { get; set; }

    [Description("How many steps may run at once (default: the processor count, capped at 8; max 32).")]
    public int? MaxConcurrency { get; set; }

    [Description("'stop' (default) stops starting new steps after the first failure; 'continue' runs every independent step anyway.")]
    public string? OnFailure { get; set; }

    [Description("Characters of output kept per step (default 4000). A longer result is replaced by a truncated preview.")]
    public int? OutputChars { get; set; }
}

/// <summary>One step as the caller writes it, before a for-each expands it.</summary>
public sealed class PlanStepSpec
{
    [Description("Identifier used by dependsOn and in the report. Defaults to 'step1', 'step2', …")]
    public string? Id { get; set; }

    [Description("'command' or 'tool'.")]
    public string? Kind { get; set; }

    [Description("For kind 'command': the command or script text to run. May span multiple lines.")]
    public string? Command { get; set; }

    [Description("For kind 'command': 'powershell' (Windows PowerShell), 'pwsh', 'cmd' (Windows only) or 'bash'. Default: 'powershell' on Windows, 'bash' elsewhere.")]
    public string? Shell { get; set; }

    [Description("Working directory for a command step, or the base directory its paths are resolved against.")]
    public string? WorkingDirectory { get; set; }

    [Description("For kind 'tool': the tool name, exactly as tools/list advertises it (e.g. 'simplify_code').")]
    public string? Tool { get; set; }

    [Description("For kind 'tool': a JSON object of that tool's arguments, by the same names the tool's schema uses.")]
    public JsonElement? Arguments { get; set; }

    [Description("Ids of steps that must finish (and succeed) before this one starts.")]
    public string[]? DependsOn { get; set; }

    [Description(
        "Files or directories this step reads or writes. Two steps whose paths overlap never run at the " +
        "same time, which is what makes parallel edits to one tree safe. Declare them for anything that writes.")]
    public string[]? Paths { get; set; }

    [Description(
        "Fans this step out: one concrete step per item, with 'stepId[index]' as its id. Inside command, " +
        "tool, workingDirectory, paths and string arguments, '${item}' (or '${<as>}') is replaced by the item.")]
    public string[]? ForEach { get; set; }

    [Description("The placeholder name a for-each exposes. Defaults to 'item'.")]
    public string? As { get; set; }

    [Description("Seconds before the step is stopped and reported as timed out (default 600, max 3600).")]
    public int? TimeoutSeconds { get; set; }

    [Description("When true, a failure here does not skip the steps depending on it.")]
    public bool? ContinueOnError { get; set; }
}

/// <summary>A validated plan, ready for the scheduler.</summary>
internal sealed record CompiledPlan(
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string> Errors,
    int MaxConcurrency,
    bool StopOnFailure,
    int OutputChars);

/// <summary>
/// Turns a <see cref="PlanSpec"/> into concrete <see cref="PlanStep"/>s: expands for-each steps,
/// resolves dependencies through those expansions, and refuses the plan — before anything runs — when
/// an id is duplicated, a dependency or a tool does not exist, or the graph has a cycle.
/// </summary>
internal static class PlanCompiler
{
    public static CompiledPlan Compile(PlanSpec spec, IReadOnlyCollection<string> knownTools, string defaultDirectory)
    {
        var errors = new List<string>();
        var outputChars = Math.Clamp(spec.OutputChars ?? 4000, 200, 200_000);
        var maxConcurrency = Math.Clamp(spec.MaxConcurrency ?? Math.Min(Environment.ProcessorCount, 8), 1, 32);
        var stopOnFailure = !string.Equals(spec.OnFailure?.Trim(), "continue", StringComparison.OrdinalIgnoreCase);

        if (spec.Steps is not { Count: > 0 })
        {
            return new CompiledPlan([], ["The plan has no steps."], maxConcurrency, stopOnFailure, outputChars);
        }

        // ── expansion ───────────────────────────────────────────────────────────
        var declared = new List<(PlanStepSpec Spec, string Id)>();
        var declaredById = new Dictionary<string, PlanStepSpec>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var id = string.IsNullOrWhiteSpace(step.Id) ? $"step{i + 1}" : step.Id.Trim();
            if (id.Contains(' ', StringComparison.Ordinal))
            {
                errors.Add($"step {i + 1}: id '{id}' must not contain spaces.");
                continue;
            }

            if (!declaredById.TryAdd(id, step))
            {
                errors.Add($"step id '{id}' is used more than once.");
                continue;
            }

            declared.Add((step, id));
        }

        var steps = new List<PlanStep>();
        var alias = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (step, id) in declared)
        {
            PlanStepKind? kind = step.Kind?.Trim().ToLowerInvariant() switch
            {
                "command" => PlanStepKind.Command,
                "tool" => PlanStepKind.Tool,
                _ => null,
            };

            if (kind is null)
            {
                errors.Add($"step '{id}': kind must be 'command' or 'tool' (got '{step.Kind}').");
                continue;
            }

            var name = step.As?.Trim() is { Length: > 0 } aliasName ? aliasName : "item";
            var timeout = Math.Clamp(step.TimeoutSeconds ?? 600, 1, 3600);
            var shell = string.IsNullOrWhiteSpace(step.Shell) ? HostSystem.DefaultShell : step.Shell.Trim();
            var continueOnError = step.ContinueOnError ?? false;

            var items = step.ForEach is { Length: > 0 } ? step.ForEach : null;
            if (step.ForEach is { Length: 0 })
            {
                errors.Add($"step '{id}': forEach is empty, so the step would expand to nothing.");
                continue;
            }

            var ids = new List<string>(items?.Length ?? 1);
            if (items is null)
            {
                steps.Add(Build(step, id, id, kind.Value, null, name, timeout, shell, continueOnError, defaultDirectory));
                ids.Add(id);
            }
            else
            {
                for (var index = 0; index < items.Length; index++)
                {
                    var concreteId = $"{id}[{index}]";
                    steps.Add(Build(step, concreteId, id, kind.Value, items[index], name, timeout, shell, continueOnError, defaultDirectory));
                    ids.Add(concreteId);
                }
            }

            alias[id] = ids;
        }

        // ── ids, dependencies, tools, cycles ────────────────────────────────────
        var byId = new Dictionary<string, PlanStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!byId.TryAdd(step.Id, step))
                errors.Add($"step id '{step.Id}' is used more than once.");
        }

        var resolved = new List<PlanStep>(steps.Count);
        foreach (var step in steps)
        {
            var declaredStep = declaredById[step.GroupId ?? step.Id];

            var deps = new List<string>();
            foreach (var dep in declaredStep.DependsOn ?? [])
            {
                if (!alias.TryGetValue(dep, out var targets))
                {
                    errors.Add($"step '{step.Id}': dependsOn '{dep}', which is not a step of this plan.");
                    continue;
                }

                foreach (var target in targets)
                {
                    if (string.Equals(target, step.Id, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"step '{step.Id}' depends on itself.");
                    else if (!deps.Contains(target, StringComparer.OrdinalIgnoreCase))
                        deps.Add(target);
                }
            }

            if (step.Kind == PlanStepKind.Tool
                && !knownTools.Contains(step.Target, StringComparer.OrdinalIgnoreCase)
                && !errors.Exists(e => e.Contains($"step '{step.Id}'", StringComparison.OrdinalIgnoreCase)))
            {
                var suggestion = Suggest(step.Target, knownTools);
                errors.Add($"step '{step.Id}': there is no tool called '{step.Target}'." +
                           (suggestion is null ? "" : $" Closest match: '{suggestion}'."));
            }

            resolved.Add(new PlanStep
            {
                Id = step.Id,
                Kind = step.Kind,
                Target = step.Target,
                Command = step.Command,
                Shell = step.Shell,
                WorkingDirectory = step.WorkingDirectory,
                Arguments = step.Arguments,
                DependsOn = deps,
                Paths = step.Paths,
                TimeoutSeconds = step.TimeoutSeconds,
                ContinueOnError = step.ContinueOnError,
                GroupId = step.GroupId,
            });
        }

        if (errors.Count == 0)
        {
            var cycle = FindCycle(resolved);
            if (cycle is not null)
                errors.Add($"the plan has a cycle: {cycle}.");
        }

        return new CompiledPlan(errors.Count == 0 ? resolved : [], errors, maxConcurrency, stopOnFailure, outputChars);
    }

    private static PlanStep Build(
        PlanStepSpec spec,
        string id,
        string groupId,
        PlanStepKind kind,
        string? item,
        string itemName,
        int timeout,
        string shell,
        bool continueOnError,
        string defaultDirectory)
    {
        string? Apply(string? text) => text is null
            ? null
            : item is null ? text : text.Replace("${" + itemName + "}", item, StringComparison.Ordinal);

        var workingDirectory = Apply(spec.WorkingDirectory);
        var paths = (spec.Paths ?? [])
            .Select(p => Normalize(Apply(p) ?? p, workingDirectory ?? defaultDirectory))
            .ToArray();

        return new PlanStep
        {
            Id = id,
            Kind = kind,
            Target = kind == PlanStepKind.Tool ? Apply(spec.Tool) ?? "" : "command",
            Command = Apply(spec.Command),
            Shell = shell,
            WorkingDirectory = workingDirectory,
            Arguments = item is null ? spec.Arguments : Substitute(spec.Arguments, itemName, item),
            DependsOn = [],
            Paths = paths,
            TimeoutSeconds = timeout,
            ContinueOnError = continueOnError,
            GroupId = groupId == id ? null : groupId,
        };
    }

    /// <summary>
    /// Absolute, separator-normalised form so two spellings of one directory compare equal.
    /// Windows paths compare case-insensitively, everything else exactly.
    /// </summary>
    private static string Normalize(string path, string baseDirectory)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path);
        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception)
        {
            // An invalid path still has to compare to something; keep it as written.
        }

        full = full.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>Replaces a <c>${name}</c> placeholder that makes up a whole JSON string value.</summary>
    private static JsonElement Substitute(JsonElement? arguments, string name, string value)
    {
        var token = "${" + name + "}";
        var raw = arguments?.GetRawText() ?? "{}";
        if (!raw.Contains("${", StringComparison.Ordinal))
            return arguments ?? JsonSerializer.SerializeToElement(new { });

        var node = JsonNode.Parse(raw);
        Rewrite(node, token, value);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Rewrite(JsonNode? node, string token, string value)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    var child = obj[key];
                    if (child is JsonValue candidate
                        && candidate.TryGetValue<string>(out var text)
                        && string.Equals(text, token, StringComparison.Ordinal))
                    {
                        obj[key] = JsonValue.Create(value);
                    }
                    else
                    {
                        Rewrite(child, token, value);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item
                        && item.TryGetValue<string>(out var text)
                        && string.Equals(text, token, StringComparison.Ordinal))
                    {
                        array[i] = JsonValue.Create(value);
                    }
                    else
                    {
                        Rewrite(array[i], token, value);
                    }
                }

                break;
        }
    }

    /// <summary>Kahn's algorithm; returns a readable cycle path, or null when the graph is acyclic.</summary>
    private static string? FindCycle(IReadOnlyList<PlanStep> steps)
    {
        var indegree = steps.ToDictionary(s => s.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = steps.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!indegree.ContainsKey(dep))
                    continue;

                indegree[step.Id]++;
                dependents[dep].Add(step.Id);
            }
        }

        var ready = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var seen = 0;
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            seen++;
            foreach (var next in dependents[id])
            {
                if (--indegree[next] == 0)
                    ready.Enqueue(next);
            }
        }

        if (seen == steps.Count)
            return null;

        var stuck = string.Join(", ", indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key));
        return $"these steps wait on each other: {stuck}";
    }

    private static string? Suggest(string wanted, IReadOnlyCollection<string> known)
    {
        var lowered = wanted.ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);
        var exact = known.FirstOrDefault(k => string.Equals(k, lowered, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        return known
            .OrderByDescending(k => CommonPrefix(k.ToLowerInvariant(), lowered))
            .FirstOrDefault(k => k.Contains(lowered, StringComparison.OrdinalIgnoreCase)
                                 || lowered.Contains(k, StringComparison.OrdinalIgnoreCase)
                                 || CommonPrefix(k.ToLowerInvariant(), lowered) >= 4);
    }

    private static int CommonPrefix(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i])
            i++;

        return i;
    }
}
