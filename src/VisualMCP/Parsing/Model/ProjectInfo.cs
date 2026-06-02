namespace VisualMCP.Parsing.Model;

public record ProjectInfo(
    string ProjectPath,
    string Name,
    string TargetFramework,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageRef> PackageReferences);
