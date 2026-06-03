using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class GetSymbolInfoTool
{
    [McpServerTool, Description(
        "When you have a file + line (and optional column) and need to know exactly what symbol is there — its resolved type, kind and docs (the Visual Studio hover) — use this INSTEAD OF inferring it from surrounding text. " +
        "Roslyn's semantic model gives the true binding, including the inferred type of 'var' and overload resolution. " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> GetSymbolInfo(
        [Description("Absolute path to the source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based, default: 1)")] int column = 1)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));

        if (document is null)
            return new { error = $"File '{filePath}' is not part of the loaded solution." };

        var text  = await document.GetTextAsync();
        if (line < 1 || line > text.Lines.Count)
            return new { error = $"Line {line} is out of range (file has {text.Lines.Count} lines)." };

        var textLine = text.Lines[line - 1];
        var col      = Math.Clamp(column - 1, 0, Math.Max(0, textLine.End - textLine.Start - 1));
        var position = textLine.Start + col;

        var root  = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
            return new { error = "Could not get semantic model for this document." };

        var token     = root.FindToken(position);
        var node      = token.Parent;

        // Try declared symbol first (on a declaration site), then referenced symbol
        var declared  = node is not null ? model.GetDeclaredSymbol(node) : null;
        var symInfo   = node is not null ? model.GetSymbolInfo(node) : default;
        var typeInfo  = node is not null ? model.GetTypeInfo(node)   : default;

        var symbol = declared ?? symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault();

        if (symbol is null)
            return new
            {
                filePath, line, column,
                tokenText = token.Text,
                info      = "No symbol resolved at this position.",
                inferredType = typeInfo.Type?.ToDisplayString(),
            };

        var defLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return new
        {
            filePath,
            line,
            column,
            tokenText         = token.Text,
            symbol            = symbol.ToDisplayString(),
            kind              = symbol.Kind.ToString(),
            containingType    = symbol.ContainingType?.ToDisplayString(),
            containingNs      = symbol.ContainingNamespace?.ToDisplayString(),
            accessibility     = symbol.DeclaredAccessibility.ToString(),
            isStatic          = symbol.IsStatic,
            inferredType      = typeInfo.Type?.ToDisplayString(),
            xmlDoc            = symbol.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
            definedAt         = defLoc is null ? null : new
            {
                filePath = defLoc.SourceTree?.FilePath,
                line     = defLoc.GetLineSpan().StartLinePosition.Line + 1,
            },
        };
    }
}
