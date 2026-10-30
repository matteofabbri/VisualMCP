using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class IntroduceParameterTool
{
    [McpServerTool(Name = "introduce_parameter"), Description(
        "Turn an expression that is fixed at compile time into a parameter (Rider's 'Introduce parameter', deterministic " +
        "half): the expression is replaced by the new parameter and every call site passes that same expression, so " +
        "behaviour does not change — you then edit the call sites you actually want to vary. Qualifies any literal, a " +
        "constant, arithmetic on constants, nameof(...), typeof(...) and default(T); an expression that reads state (a " +
        "local, a parameter, an instance member) is refused, because its value differs at each call site. Refuses " +
        "virtual/override/interface members, named-argument call sites and non-call uses. dryRun defaults to true.")]
    public static Task<object> IntroduceParameter(
        IIntroduceParameterService svc,
        [Description("File containing the expression (absolute or relative).")] string filePath,
        [Description("1-based line where the expression appears.")] int line,
        [Description("The exact expression text, as written on that line (e.g. 30, \"text\", 1 << 3, typeof(int)).")] string expressionText,
        [Description("Name for the new parameter.")] string parameterName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.IntroduceParameter(filePath, line, expressionText, parameterName, dryRun);
}
