using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class IntroduceFieldTool
{
    [McpServerTool(Name = "introduce_field"), Description(
        "Hoist an expression into a private readonly field initialised with it (Rider's 'Introduce field') and replace " +
        "its occurrences in the enclosing member with the field. Only CONTEXT-FREE expressions qualify: a field " +
        "initializer cannot read locals, parameters, 'this', awaited values or instance members, so those are refused " +
        "with the reason instead of producing code that does not compile. Locates the expression by its exact text on " +
        "the given line, like introduce_variable. dryRun defaults to true and returns a line diff.")]
    public static Task<object> IntroduceField(
        IIntroduceFieldService svc,
        [Description("File containing the expression (absolute or relative).")] string filePath,
        [Description("1-based line where the expression appears.")] int line,
        [Description("The exact text of the expression, as written on that line.")] string expressionText,
        [Description("Name for the new field, e.g. '_defaultTimeout'.")] string fieldName,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.IntroduceField(filePath, line, expressionText, fieldName, dryRun);
}
