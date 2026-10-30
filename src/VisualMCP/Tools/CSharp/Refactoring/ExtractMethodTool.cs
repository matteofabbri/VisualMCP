using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ExtractMethodTool
{
    [McpServerTool(Name = "extract_method"), Description(
        "Extract a contiguous block of statements into a new private method (Rider's 'Extract method'), selected by " +
        "line range. Locals declared before the block that it reads become parameters; a single trailing " +
        "'return expression;' becomes the new method's return value, and the call site becomes 'return New(args);'. " +
        "A block that awaits is extracted as 'private async Task[<T>]' and called with 'await', which requires the " +
        "enclosing method to be async already. It is deliberately conservative and REFUSES what it cannot prove " +
        "equivalent: yield, any break/continue/goto, a return anywhere but last, a write to a pre-existing local, a " +
        "local declared inside the block and used after it, a name that already exists on the type, and any change " +
        "that would add compiler errors. dryRun defaults to true and returns a line diff.")]
    public static Task<object> ExtractMethod(
        IExtractMethodService svc,
        [Description("File containing the statements (absolute or relative).")] string filePath,
        [Description("First line of the block (1-based, inclusive).")] int fromLine,
        [Description("Last line of the block (1-based, inclusive).")] int toLine,
        [Description("Name for the new private method.")] string methodName,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ExtractMethod(filePath, fromLine, toLine, methodName, dryRun);
}
