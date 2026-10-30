using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class GlobalUsingsTool
{
    [McpServerTool(Name = "global_usings"), Description(
        "Move a project's plain 'using X;' directives into one GlobalUsings.cs as 'global using X;' and remove them from " +
        "the individual files (Rider's 'Move to global usings'). Aliased and static usings are left alone. The change is " +
        "GATED on the compiler: if importing those namespaces project-wide would introduce errors — typically an " +
        "ambiguous reference between two namespaces that both define a type in use — nothing is written and the new " +
        "errors are reported. Pass projectName when the solution has more than one project. dryRun defaults to true.")]
    public static Task<object> GlobalUsings(
        IGlobalUsingsService svc,
        [Description("Project to clean up; required when the solution has several C# projects.")] string? projectName = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveToGlobalUsings(projectName, dryRun);
}
