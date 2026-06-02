namespace VisualMCP.Parsing.Model;

public record SolutionInfo(string SolutionPath, IReadOnlyList<SolutionProject> Projects);
