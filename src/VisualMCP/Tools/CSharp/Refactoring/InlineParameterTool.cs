using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class InlineParameterTool
{
    [McpServerTool(Name = "inline_parameter"), Description(
        "Rider's 'Inline parameter': remove a parameter and put its value into the method body. Accepted only when " +
        "every call site passes the same expression, that expression does not read a local/parameter/instance " +
        "member of the caller, and it still binds to the same symbol and type inside the method (checked with " +
        "speculative binding) — otherwise inlining would silently change what the body means and the whole " +
        "operation is refused. The compiler gates the result. dryRun defaults to true.")]
    public static Task<object> InlineParameter(
        IInlineParameterService svc,
        [Description("File that declares the method (absolute or relative).")] string filePath,
        [Description("Method name.")] string methodName,
        [Description("Parameter to remove.")] string parameterName,
        [Description("Optional containing type name, when several types declare a method with this name.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.InlineParameter(filePath, methodName, parameterName, containingType, dryRun);
}
