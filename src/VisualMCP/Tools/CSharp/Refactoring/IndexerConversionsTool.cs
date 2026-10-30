using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class IndexerConversionsTool
{
    [McpServerTool(Name = "function_to_indexer"), Description(
        "Rider's 'Function to indexer': a value method with parameters becomes `T this[p1, ..., pN] { get }` " +
        "and its calls become element accesses; a void method whose last parameter is named `value` becomes the " +
        "setter (`x.Set(i, v)` becomes `x[i] = v`). Refuses static/generic/virtual methods and method-group uses. " +
        "Call sites must be in the same file as the declaration. Compiler-gated. dryRun defaults to true.")]
    public static Task<object> FunctionToIndexer(
        IIndexerConversionsService svc,
        [Description("File that declares the method (absolute or relative).")] string filePath,
        [Description("Method to convert.")] string methodName,
        [Description("Optional containing type name, when several types declare a method with this name.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.FunctionToIndexer(filePath, methodName, containingType, dryRun);

    [McpServerTool(Name = "indexer_to_function"), Description(
        "Rider's 'Indexer to function': the indexer's getter becomes `Get(p1, ...)` and its setter becomes " +
        "`Set(p1, ..., value)`; `x[i]` is rewritten at the call sites (a write `x[i] = v` is rewritten to " +
        "`x.Set(i, v)` when the model resolves the write to the indexer). Refuses when the indexer is used in " +
        "another file or a target name already exists. Compiler-gated. dryRun defaults to true.")]
    public static Task<object> IndexerToFunction(
        IIndexerConversionsService svc,
        [Description("File that declares the indexer (absolute or relative).")] string filePath,
        [Description("Optional containing type name, when the file declares several types.")] string? containingType = null,
        [Description("Optional getter name (default: Get).")] string? getterName = null,
        [Description("Optional setter name (default: Set).")] string? setterName = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.IndexerToFunction(filePath, containingType, getterName, setterName, dryRun);
}
