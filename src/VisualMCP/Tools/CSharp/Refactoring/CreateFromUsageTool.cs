using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class CreateFromUsageTool
{
    [McpServerTool(Name = "create_from_usage"), Description(
        "ReSharper's 'Create from usage': reads the compiler error on a line and creates what the code " +
        "already assumes exists. CS1061 adds the method (parameter types taken from the arguments, return " +
        "type from the context) or property to the receiver's own type; CS0246 creates the type in the " +
        "current namespace; CS0103 creates a method on the enclosing type. Refused unless the project " +
        "gains no compiler error. Dry run by default.")]
    public static Task<object> CreateFromUsage(
        ICreateFromUsageService svc,
        [Description("The file that contains the call")] string filePath,
        [Description("1-based line of the error")] int line,
        [Description("Optional: the missing name, when the line holds more than one error")] string? memberName = null,
        [Description("Preview only (default true). Pass false to write the file.")] bool dryRun = true)
        => svc.CreateFromUsage(filePath, line, memberName, dryRun);
}
