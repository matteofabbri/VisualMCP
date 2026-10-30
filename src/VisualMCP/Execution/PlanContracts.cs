using System.Text.Json;

namespace VisualMCP.Execution;

/// <summary>What a plan step does: run a shell command, or call another VisualMCP tool.</summary>
public enum PlanStepKind
{
    Command,
    Tool,
}

/// <summary>
/// One step of a plan, after <c>forEach</c> expansion: the shape the scheduler works with.
/// </summary>
public sealed class PlanStep
{
    /// <summary>Unique id inside the plan. A for-each step expands to <c>id[0]</c>, <c>id[1]</c>, …</summary>
    public required string Id { get; init; }

    public required PlanStepKind Kind { get; init; }

    /// <summary>The tool name for a tool step, <c>"command"</c> otherwise — what the report shows.</summary>
    public string Target { get; init; } = "";

    public string? Command { get; init; }

    public string? Shell { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>The tool's arguments, as the JSON object the caller supplied.</summary>
    public JsonElement? Arguments { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Files or directories this step touches, used to keep conflicting steps apart.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>When true, a failure does not skip the steps that depend on this one.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>The id the caller wrote, when a for-each expanded this step; null otherwise.</summary>
    public string? GroupId { get; init; }
}

/// <summary>Raised when a plan is well-formed but a step cannot be executed (an unknown tool, say).</summary>
public sealed class PlanStepException(string message) : Exception(message);

/// <summary>What a command step returns, independently of the MCP tool that normally wraps it.</summary>
public interface IPlanCommandRunner
{
    /// <summary>Runs a shell command in <paramref name="workingDirectory"/> and returns the tool's own result shape.</summary>
    Task<object> RunAsync(
        string command, string shell, string? workingDirectory, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// Calls another VisualMCP tool by the name <c>tools/list</c> advertises. Implemented by reflection over
/// the tool classes, so a plan reaches every tool this server has instead of a hand-picked subset.
/// </summary>
public interface IPlanToolInvoker
{
    /// <summary>Every callable tool name, canonical spelling first (aliases included).</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>Invokes one tool. Throws <see cref="PlanStepException"/> when the name is unknown or an argument is missing.</summary>
    Task<object?> InvokeAsync(string name, JsonElement? arguments, CancellationToken ct);
}

/// <summary>Runs a plan and answers with the aggregated report.</summary>
public interface IPlanExecutionService
{
    Task<object> RunAsync(PlanSpec spec, CancellationToken ct = default);
}
