using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using System.ComponentModel;
using System.Text;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class ReadSymbolTool
{
    /// <summary>Upper bound on the symbols rendered for one name, so a partial match cannot flood the result.</summary>
    private const int MaxSymbols = 20;

    [McpServerTool(Name = "read_symbol"), Description(
        "Return the SOURCE of a declaration — the whole body of a type, method, property or field — resolved from the " +
        "compiler's semantic model (correct overloads, partial/nested types, no regex false positives). " +
        "Use this INSTEAD OF search_text + read_file when you want the code of a name you just saw: one call, and you get " +
        "the declaration body with line numbers. Pass containingType to disambiguate. For who-calls-what, use find_callers.")]
    public static async Task<CallToolResult> ReadSymbol(
        IRoslynWorkspace workspace,
        [Description("Symbol name to locate (a type or a member).")] string symbolName,
        [Description("Optional containing type name to disambiguate overloads or nested types.")] string? containingType = null,
        [Description("If true, matches any symbol whose name contains symbolName (default: false = exact name).")] bool partialMatch = false,
        [Description("Maximum lines of source per declaration (20-1000, default 300).")] int maxLines = 300)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return ToolText.Fail("read_symbol needs a symbol name.");

        var requested = maxLines;
        maxLines = Math.Clamp(maxLines, 20, 1000);

        var solution = await workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return ToolText.Fail(
                "No C# solution could be auto-located from the working directory. " +
                "Call load_solution with an explicit .sln/.slnx first.");

        var matches = partialMatch
            ? await SymbolFinder.FindSourceDeclarationsAsync(
                solution,
                name => name.Contains(symbolName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.TypeAndMember)
            : await SymbolFinder.FindSourceDeclarationsAsync(
                solution, symbolName, ignoreCase: true, filter: SymbolFilter.TypeAndMember);

        var symbols = matches
            .Where(symbol => containingType is null ||
                             string.Equals(symbol.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(symbol => symbol.ToDisplayString())
            .Select(group => group.First())
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count == 0)
            return ToolText.Fail(containingType is null
                ? $"No source declaration found for '{symbolName}'."
                : $"No source declaration found for '{symbolName}' in '{containingType}'.");

        var selected = symbols.Take(MaxSymbols).ToList();
        var builder = new StringBuilder();
        builder.Append($"{symbolName} — {selected.Count} symbol(s)");
        if (symbols.Count > selected.Count)
            builder.Append($" (showing the first {selected.Count} of {symbols.Count}; narrow with containingType or partialMatch)");
        builder.Append('\n');

        var declarations = 0;
        foreach (var symbol in selected)
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var node = await reference.GetSyntaxAsync();
                var text = await node.SyntaxTree.GetTextAsync();
                var firstLine = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;
                var lastLine = text.Lines.GetLineFromPosition(node.Span.End).LineNumber;
                var declarationLines = lastLine - firstLine + 1;
                var take = Math.Min(declarationLines, maxLines);
                var truncated = declarationLines > take;

                builder.Append('\n').Append(symbol.ToDisplayString())
                       .Append(" — ").Append(symbol.Kind);
                if (symbol.ContainingType is not null)
                    builder.Append(", in ").Append(symbol.ContainingType.Name);
                builder.Append(" — ").Append(node.SyntaxTree.FilePath)
                       .Append(':').Append(firstLine + 1)
                       .Append('-').Append(lastLine + 1)
                       .Append(" (").Append(declarationLines).Append(" lines");
                if (truncated)
                    builder.Append(", truncated to ").Append(maxLines)
                           .Append(requested > maxLines ? $" because maxLines is capped at {maxLines}" : "")
                           .Append("; call again with a higher maxLines or read the file range");
                builder.Append(")\n\n");

                for (var i = 0; i < take; i++)
                {
                    builder.Append((firstLine + i + 1).ToString().PadLeft(5))
                           .Append(": ")
                           .Append(text.Lines[firstLine + i].ToString())
                           .Append('\n');
                }

                declarations++;
            }
        }

        if (declarations == 0)
            return ToolText.Fail($"'{symbolName}' has no source declaration in the loaded solution (metadata only).");

        return ToolText.Ok(builder.ToString().TrimEnd('\n'));
    }
}
