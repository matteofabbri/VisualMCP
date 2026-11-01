namespace Snitch.Languages.CSharp.Parsing.Model;

public record SolutionInfo(string SolutionPath, IReadOnlyList<SolutionProject> Projects);
