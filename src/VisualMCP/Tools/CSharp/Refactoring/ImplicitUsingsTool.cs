using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ImplicitUsingsTool
{
    [McpServerTool(Name = "implicit_usings"), Description(
        "Rider's implicit-usings toggle: writes `<ImplicitUsings>enable|disable</ImplicitUsings>` into the project " +
        "file. Disabling also writes the explicit `using` directives the files relied on, computed from the semantic " +
        "model and verified by compiling the project with the generated `*.GlobalUsings.g.cs` document removed. " +
        "The property takes effect on the next load_solution / build. dryRun defaults to true.")]
    public static Task<object> ImplicitUsings(
        IImplicitUsingsService svc,
        [Description("true enables implicit usings, false disables them and writes the explicit ones.")] bool enabled,
        [Description("Optional project name; required when the solution has several projects.")] string? projectName = null,
        [Description("true (default) previews only; pass false to write the project file and the sources.")] bool dryRun = true)
        => svc.ToggleImplicitUsings(enabled, projectName, dryRun);
}
