using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class IntroduceVariableTool
{
    [McpServerTool, Description(
        "Extract an expression at a given file + line into a new local variable, replacing every occurrence " +
        "of the same expression in the enclosing method. " +
        "Equivalent to ReSharper 'Introduce Variable'. Requires LoadSolution first.")]
    public static Task<object> IntroduceVariable(
        IIntroduceVariableService svc,
        [Description("Absolute path to the source file")] string filePath,
        [Description("Line number (1-based) where the expression appears")] int line,
        [Description("The exact text of the expression to extract (used to locate and replace all occurrences)")] string expressionText,
        [Description("Name for the new local variable")] string variableName,
        [Description("Dry run — show the change without writing to disk (default: false)")] bool dryRun = false)
        => svc.IntroduceVariable(filePath, line, expressionText, variableName, dryRun);
}