using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ChangeNullabilityTool
{
    [McpServerTool(Name = "change_nullability"), Description(
        "Toggle the nullable annotation on a declaration's type (Rider's 'Change nullability'), with a compiler gate: " +
        "the project's nullability diagnostics are counted before and after, and the change is REFUSED when it would " +
        "introduce new CS86xx/CS87xx diagnostics. That turns 'just add a ?' into a checked change instead of blind " +
        "propagation — when it refuses, the affected usages are what needs fixing first. Works on fields, properties, " +
        "locals, parameters and non-void return types; value types are refused because '?' there means Nullable<T>. " +
        "Costs a full project diagnostic pass (~seconds). dryRun defaults to true and the diagnostic delta is always " +
        "reported.")]
    public static Task<object> ChangeNullability(
        IChangeNullabilityService svc,
        [Description("File containing the declaration (absolute or relative).")] string filePath,
        [Description("1-based line where the declaration starts.")] int line,
        [Description("Optional: 'nullable' or 'notnull'; omit to toggle.")] string? direction = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ChangeNullability(filePath, line, direction, dryRun);
}
