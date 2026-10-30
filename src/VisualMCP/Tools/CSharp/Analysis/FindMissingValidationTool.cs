using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindMissingValidationTool
{
    [McpServerTool(Name = "find_missing_validation"), Description(
        "Public methods that take a reference-type (or string) parameter and never check it: the null " +
        "that blows up three frames deep. A guard counts when it is in the method — " +
        "`if (x is null) throw`, `ArgumentNullException.ThrowIfNull(x)`, `x ?? throw`. Complements the " +
        "security rules: those find dangerous APIs, this finds the missing contract at the public edge.")]
    public static Task<object> FindMissingValidation(
        IFindMissingValidationService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("How many methods to return (default 100)")] int top = 100)
        => svc.Find(projectName, top);
}
