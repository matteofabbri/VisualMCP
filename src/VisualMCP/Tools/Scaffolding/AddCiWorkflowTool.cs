using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Scaffolding;

namespace VisualMCP.Tools.Scaffolding;

[McpServerToolType]
public static class AddCiWorkflowTool
{
    [McpServerTool(Name = "add_ci_workflow"), Description(
        "Write a GitHub Actions workflow into .github/workflows of a repository. workflowType " +
        "'dotnet-multi-os' builds and publishes the given .NET project on Windows, Linux and macOS " +
        "(win-x64/linux-x64/osx-arm64) and uploads the artifacts. Use INSTEAD OF hand-writing the YAML.")]
    public static object AddCiWorkflow(
        [Description("Repository root directory.")] string directory,
        [Description("Path to the .csproj or .sln to build, relative to the repo (e.g. 'src/App/App.csproj').")] string projectPath,
        [Description("Workflow file name without extension (default: 'build').")] string name = "build",
        [Description("Workflow template id (default: 'dotnet-multi-os').")] string workflowType = "dotnet-multi-os",
        [Description("Overwrite an existing workflow file (default: false).")] bool overwrite = false)
        => ScaffoldImpl.AddCiWorkflow(directory, projectPath, name, workflowType, overwrite);
}
