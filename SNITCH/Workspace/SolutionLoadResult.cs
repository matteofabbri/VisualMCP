using Microsoft.CodeAnalysis;

namespace Snitch.Workspace;

/// <summary>The outcome of loading a solution: the Roslyn model plus any workspace diagnostics.</summary>
public record SolutionLoadResult(Solution Solution, IReadOnlyList<string> Diagnostics);
