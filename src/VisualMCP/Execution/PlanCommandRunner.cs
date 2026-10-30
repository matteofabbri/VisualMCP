using Snitch.Execution;

namespace VisualMCP.Execution;

/// <summary>
/// Adapts the engine's shell runner to a plan step. <paramref name="ct"/> is not wired through:
/// <see cref="Snitch.Execution.ProcessRunner"/> owns the lifetime of the process tree and stops it on its
/// own timeout, which is the only cancellation that can be guaranteed to kill the children too.
/// </summary>
public sealed class PlanCommandRunner(IRunCommandService commands) : IPlanCommandRunner
{
    public Task<object> RunAsync(
        string command, string shell, string? workingDirectory, int timeoutSeconds, CancellationToken ct)
        => commands.RunAsync(command, shell, workingDirectory, timeoutSeconds);
}
