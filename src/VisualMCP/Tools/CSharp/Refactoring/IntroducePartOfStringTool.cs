using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class IntroducePartOfStringTool
{
    [McpServerTool(Name = "introduce_part_of_string"), Description(
        "Rider's 'Introduce part of string': lift a substring of one string literal into a private const and " +
        "leave a concatenation (`\"Hello \" + Extracted`) or the constant alone when the whole literal is taken. " +
        "The literal is chosen by line; the substring must occur exactly once in it. Compiler-gated. " +
        "dryRun defaults to true.")]
    public static Task<object> IntroducePartOfString(
        IIntroducePartOfStringService svc,
        [Description("File that contains the literal (absolute or relative).")] string filePath,
        [Description("1-based line of the string literal.")] int line,
        [Description("The exact substring to extract.")] string part,
        [Description("Name of the generated constant (default: Extracted).")] string constantName = "Extracted",
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.IntroducePartOfString(filePath, line, part, constantName, dryRun);
}
