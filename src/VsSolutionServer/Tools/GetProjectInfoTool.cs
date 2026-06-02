using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Parsing;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class GetProjectInfoTool
{
    [McpServerTool, Description("Parse a .csproj file and return its target framework, source files, project references, and NuGet packages.")]
    public static object GetProjectInfo(
        [Description("Absolute or relative path to the .csproj file")] string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var info = CsprojParser.Parse(path);
        return new
        {
            info.Name,
            info.TargetFramework,
            sourceFileCount = info.SourceFiles.Count,
            sourceFiles = info.SourceFiles,
            projectReferences = info.ProjectReferences,
            packageReferences = info.PackageReferences
        };
    }
}
