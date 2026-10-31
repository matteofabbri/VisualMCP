using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Indexing;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodeIndexTool
{
    [McpServerTool(Name = "metacode_index"), Description(
        "The solution's METACODE, persisted in .visualMCP/visualmcp.db: every source of the solution lowered into " +
        "the canonical pseudocode model, one row per declaration with the file/span it came from and the structural " +
        "facts computed over the model. It is generated when the solution opens and kept in step with saved files " +
        "(the background pass compares each file's timestamp, then its content hash, so only what changed is " +
        "translated). Every source is stored TWICE: its native text and language in source_document, its metacode in " +
        "mc_document/mc_tree — a language no frontend reads keeps the native row and has no metacode, which status " +
        "reports as sources vs documents and the build report as unsupported. C#, Visual Basic and Razor all have one. " +
        "The COMPLETE AST of every declaration is stored node by node in mc_node too: the parent link, the parent " +
        "property that holds the node, its position there, its depth, its span and the node's own payload as JSON — " +
        "so the tree is a SQL query, not a rendering to parse. " +
        "action=status reports documents, sources, declarations, AST nodes, callables, types and the complexity range; " +
        "action=build translates the documents whose content changed (force re-translates everything); " +
        "action=complexity ranks callables by the CYCLOMATIC and COGNITIVE complexity of their metacode — the " +
        "language-neutral metric, not the Roslyn one that complexity_report reports; action=trees returns the " +
        "stored tree of a symbol (or every tree of a file), which is what metacode_generate turns back into code; " +
        "action=ast returns the complete stored AST of the matching declarations, nested, one node per class of the " +
        "model (If, Block, Invocation, Binary, …) with its span; " +
        "action=assemblies lists the binaries whose decompiled code the store also holds (metacoding action=decompile). " +
        "The document set is decided by language, not by extension: C#, Visual Basic and Razor pages/components have " +
        "a frontend (.cshtml/.razor are read from disk, they are not Roslyn documents), other languages are recorded " +
        "natively.")]
    public static async Task<object> MetacodeIndex(
        IMetacodeIndexService index,
        [Description("status | build | complexity | trees | ast | assemblies (default: status).")]
        string action = "status",
        [Description("For trees and ast: the symbol — a name ('Divide'), a qualified name ('Calculator.Divide') or a declaration key.")]
        string? symbol = null,
        [Description("For trees and ast: a file path, instead of (or together with) a symbol.")]
        string? path = null,
        [Description("For build: re-translate every document even when its hash matches.")]
        bool force = false,
        [Description("For build and complexity: only this project.")]
        string? projectName = null,
        [Description("For complexity: only this declaration kind ('method', 'constructor', 'property', ...).")]
        string? kind = null,
        [Description("For complexity: include callables with at least this cyclomatic complexity (default 1).")]
        int minCyclomatic = 1,
        [Description("How many rows to return (default 50).")]
        int limit = 50,
        [Description("For ast: stop after this many nodes in one call (default 2000); the answer says when it was cut.")]
        int maxNodes = 2000,
        CancellationToken cancellationToken = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "status":
                return index.Status();

            case "build":
                return await index.BuildAsync(force, projectName, cancellationToken);

            case "complexity":
                return index.Complexity(minCyclomatic, projectName, kind, limit);

            case "trees":
                if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(path))
                    return new { error = "trees needs symbol or path." };

                return index.Trees(symbol, path, limit);

            case "ast":
                if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(path))
                    return new { error = "ast needs symbol or path." };

                return index.Ast(symbol, path, maxNodes);

            case "assemblies":
                return index.DecompiledAssemblies();

            default:
                return new { error = "action must be status, build, complexity, trees, ast or assemblies." };
        }
    }
}
