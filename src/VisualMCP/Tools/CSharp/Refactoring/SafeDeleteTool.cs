using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class SafeDeleteTool
{
    [McpServerTool, Description(
        "When you want to remove a type/method/property/field, use this INSTEAD OF deleting by hand: it first verifies via Roslyn that the symbol has zero references in the solution, " +
        "and refuses (listing every reference) if any exist — the ReSharper Safe Delete behaviour that prevents breaking the build. " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> SafeDelete(
        [Description("Symbol name to delete (class, method, property, field, etc.)")] string symbolName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null,
        [Description("Dry run â€” verify safety without deleting (default: false)")] bool dryRun = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.All);

        var symbols = candidates
            .Where(s => containingType is null ||
                        (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
            return new { error = $"Symbol '{symbolName}' not found." };
        if (symbols.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {symbols.Count} symbols named '{symbolName}'. Specify containingType to disambiguate.",
                candidates = symbols.Select(s => s.ToDisplayString()).ToList(),
            };

        var symbol = symbols[0];

        // Check references
        var refGroups = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var allRefs = refGroups.SelectMany(r => r.Locations).ToList();

        if (allRefs.Count > 0)
        {
            return new
            {
                safe = false,
                symbol = symbol.ToDisplayString(),
                kind = symbol.Kind.ToString(),
                reason = $"Cannot delete: {allRefs.Count} reference(s) found.",
                references = allRefs.Select(l => new
                {
                    FilePath = l.Document.FilePath,
                    Line = l.Location.GetLineSpan().StartLinePosition.Line + 1,
                    Column = l.Location.GetLineSpan().StartLinePosition.Character + 1,
                }).OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList(),
            };
        }

        // Find the declaring syntax node
        var declLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation is null)
            return new { error = "Could not locate source declaration." };

        var tree = declLocation.SourceTree;
        var docWithTree = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);

        if (docWithTree is null || docWithTree.FilePath is null)
            return new { error = "Could not locate the document for this symbol." };

        if (dryRun)
        {
            return new
            {
                safe = true,
                dryRun = true,
                symbol = symbol.ToDisplayString(),
                kind = symbol.Kind.ToString(),
                declarationFile = docWithTree.FilePath,
                message = "Symbol has zero references and can be safely deleted.",
            };
        }

        // Remove the declaring node from its document
        var root = await docWithTree.GetSyntaxRootAsync();
        if (root is null)
            return new { error = "Could not read syntax tree." };

        var span = declLocation.SourceSpan;
        var nodeToRemove = root.FindNode(span);

        // Walk up to the nearest member declaration
        while (nodeToRemove is not null &&
               nodeToRemove is not MemberDeclarationSyntax &&
               nodeToRemove is not StatementSyntax)
        {
            nodeToRemove = nodeToRemove.Parent;
        }

        if (nodeToRemove is null)
            return new { error = "Could not identify the declaration node to remove." };

        SyntaxNode newRoot;
        if (nodeToRemove.Parent is TypeDeclarationSyntax parentType)
        {
            // Remove member from type
            var newMembers = parentType.Members.Remove((MemberDeclarationSyntax)nodeToRemove);
            newRoot = root.ReplaceNode(parentType, parentType.WithMembers(newMembers));
        }
        else if (nodeToRemove.Parent is CompilationUnitSyntax compilationUnit)
        {
            // Top-level member (e.g. top-level type)
            var newMembers = compilationUnit.Members.Remove((MemberDeclarationSyntax)nodeToRemove);
            newRoot = root.ReplaceNode(compilationUnit, compilationUnit.WithMembers(newMembers));
        }
        else
        {
            return new { error = $"Unsupported declaration context: {nodeToRemove.Parent?.GetType().Name}." };
        }

        await File.WriteAllTextAsync(docWithTree.FilePath, newRoot.ToFullString(), System.Text.Encoding.UTF8);

        return new
        {
            safe = true,
            dryRun = false,
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            declarationFile = docWithTree.FilePath,
            message = "Symbol deleted successfully.",
        };
    }
}
