using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Refactoring;

/// <summary>
/// Inlines a local variable or a single-expression method/property into all its call sites.
/// Scope is intentionally narrow: only expression-bodied or single-return members, and
/// only local variables with a single initializer. Complex control flow is refused.
/// </summary>
[McpServerToolType]
public static class InlineSymbolTool
{
    [McpServerTool, Description(
        "Inline a symbol at all its use sites and remove the original declaration. " +
        "Supports: (1) local variables with a single initializer, " +
        "(2) methods/properties with a single expression body or a single 'return' statement. " +
        "Refuses if the body is multi-statement or if the symbol has no references. " +
        "Equivalent to ReSharper 'Inline Variable/Method'. Requires LoadSolution first.")]
    public static async Task<object> InlineSymbol(
        [Description("Symbol name to inline (local variable, method, or property)")] string symbolName,
        [Description("Optional: containing type name to disambiguate methods/properties")] string? containingType = null,
        [Description("Dry run â€” show what would change without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // â”€â”€ Try member (method / property) first â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var memberCandidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Member);

        var memberSymbol = memberCandidates
            .Where(s => s is IMethodSymbol or IPropertySymbol)
            .Where(s => containingType is null ||
                        s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) == true)
            .FirstOrDefault();

        if (memberSymbol is not null)
            return await InlineMember(memberSymbol, solution, dryRun);

        // â”€â”€ Fall back: look for local variable across all documents â”€â”€â”€â”€â”€â”€â”€â”€â”€
        return await InlineLocalVariable(symbolName, solution, dryRun);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Inline member (method / property)
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task<object> InlineMember(ISymbol symbol, Solution solution, bool dryRun)
    {
        var declLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLoc is null)
            return new { error = "Symbol has no source location." };

        var tree = declLoc.SourceTree!;
        var sourceDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);
        if (sourceDoc?.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync();
        if (root is null) return new { error = "Could not parse source file." };

        // Extract the expression to inline
        var span = declLoc.SourceSpan;
        var declNode = root.FindNode(span);

        ExpressionSyntax? inlineExpr = null;
        SyntaxNode? declMemberNode = null;
        List<string> paramNames = [];

        switch (declNode.FirstAncestorOrSelf<MemberDeclarationSyntax>())
        {
            case MethodDeclarationSyntax meth:
                declMemberNode = meth;
                paramNames = meth.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
                inlineExpr = ExtractMethodExpression(meth);
                break;

            case PropertyDeclarationSyntax prop:
                declMemberNode = prop;
                inlineExpr = ExtractPropertyExpression(prop);
                break;

            default:
                return new { error = "Symbol is not a method or property." };
        }

        if (inlineExpr is null)
            return new
            {
                error = "Can only inline methods/properties with a single expression body or a single 'return' statement. " +
                        "Multi-statement bodies are not supported."
            };

        // Find all references
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var allLocs = refs.SelectMany(r => r.Locations).ToList();

        if (allLocs.Count == 0)
            return new { error = "Symbol has no references â€” nothing to inline into." };

        // Group by document
        var byDoc = allLocs.GroupBy(l => l.Document.FilePath).ToList();

        var preview = new List<object>();
        var filesToWrite = new Dictionary<string, string>();

        foreach (var group in byDoc)
        {
            var docPath = group.Key;
            if (docPath is null) continue;

            var refDoc = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == docPath);
            if (refDoc is null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync();
            if (refRoot is null) continue;

            // Sort descending to avoid offset drift when splicing
            var sortedLocs = group.OrderByDescending(l => l.Location.SourceSpan.Start).ToList();
            var text = (await refDoc.GetTextAsync()).ToString();

            foreach (var loc in sortedLocs)
            {
                var s = loc.Location.SourceSpan;
                // The reference points at the identifier; find its invocation ancestor
                var refNode = refRoot.FindNode(s);
                var invocation = refNode.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                SyntaxNode? callNode = invocation ?? (SyntaxNode?)refNode;

                string replacement;
                if (invocation is not null && paramNames.Count > 0)
                {
                    // Substitute arguments into the expression
                    var args = invocation.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
                    replacement = SubstituteParams(inlineExpr.ToString(), paramNames, args);
                }
                else
                {
                    replacement = inlineExpr.ToString();
                }

                // Wrap in parens if needed (e.g. binary expr used inside another expr)
                if (callNode?.Parent is BinaryExpressionSyntax or ConditionalExpressionSyntax)
                    replacement = $"({replacement})";

                var callSpan = callNode?.Span ?? s;
                text = text[..callSpan.Start] + replacement + text[(callSpan.Start + callSpan.Length)..];
                preview.Add(new { file = docPath, line = loc.Location.GetLineSpan().StartLinePosition.Line + 1, replacement });
            }

            filesToWrite[docPath] = text;
        }

        // Also remove the declaration from its source file
        if (declMemberNode is not null)
        {
            string declFileText = filesToWrite.TryGetValue(sourceDoc.FilePath, out var already)
                ? already
                : (await sourceDoc.GetTextAsync()).ToString();

            var declSpan = declMemberNode.FullSpan;
            declFileText = declFileText[..declSpan.Start] + declFileText[(declSpan.Start + declSpan.Length)..];
            filesToWrite[sourceDoc.FilePath] = declFileText;
        }

        if (dryRun)
            return new { dryRun = true, symbol = symbol.ToDisplayString(), referencesInlined = allLocs.Count, preview };

        foreach (var (path, text) in filesToWrite)
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            symbol = symbol.ToDisplayString(),
            referencesInlined = allLocs.Count,
            filesModified = filesToWrite.Count,
        };
    }

    private static ExpressionSyntax? ExtractMethodExpression(MethodDeclarationSyntax meth)
    {
        // Expression-bodied: int Foo() => expr;
        if (meth.ExpressionBody is not null)
            return meth.ExpressionBody.Expression;

        // Block body with a single return statement
        if (meth.Body?.Statements is [ReturnStatementSyntax ret])
            return ret.Expression;

        return null;
    }

    private static ExpressionSyntax? ExtractPropertyExpression(PropertyDeclarationSyntax prop)
    {
        if (prop.ExpressionBody is not null)
            return prop.ExpressionBody.Expression;

        var getter = prop.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));

        if (getter?.ExpressionBody is not null)
            return getter.ExpressionBody.Expression;

        if (getter?.Body?.Statements is [ReturnStatementSyntax ret])
            return ret.Expression;

        return null;
    }

    private static string SubstituteParams(string exprText, List<string> paramNames, List<string> args)
    {
        for (int i = 0; i < Math.Min(paramNames.Count, args.Count); i++)
            exprText = System.Text.RegularExpressions.Regex.Replace(
                exprText, $@"\b{System.Text.RegularExpressions.Regex.Escape(paramNames[i])}\b", args[i]);
        return exprText;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Inline local variable
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task<object> InlineLocalVariable(string symbolName, Solution solution, bool dryRun)
    {
        // Scan all documents for a local variable declaration with this name
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                var locals = root.DescendantNodes()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .Where(l => l.Declaration.Variables
                        .Any(v => v.Identifier.Text.Equals(symbolName, StringComparison.Ordinal)))
                    .ToList();

                foreach (var localDecl in locals)
                {
                    var variable = localDecl.Declaration.Variables
                        .First(v => v.Identifier.Text.Equals(symbolName, StringComparison.Ordinal));

                    if (variable.Initializer is null)
                        return new { error = $"Local variable '{symbolName}' has no initializer â€” cannot inline." };

                    var initExpr = variable.Initializer.Value;
                    var localSymbol = model.GetDeclaredSymbol(variable);
                    if (localSymbol is null) continue;

                    // Find all reads of this local within the same method
                    var method = localDecl.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
                    if (method is null) continue;

                    var usages = method.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Where(id => id.Identifier.Text.Equals(symbolName, StringComparison.Ordinal))
                        .Where(id => !id.Parent.IsKind(SyntaxKind.VariableDeclarator))
                        .ToList();

                    if (usages.Count == 0)
                        return new { error = $"Local variable '{symbolName}' is declared but never used." };

                    // Check for writes (assignments) â€” refuse if the variable is reassigned
                    var assignments = method.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(a => a.Left is IdentifierNameSyntax id &&
                                    id.Identifier.Text.Equals(symbolName, StringComparison.Ordinal))
                        .ToList();

                    if (assignments.Count > 0)
                        return new { error = $"Local variable '{symbolName}' is reassigned â€” cannot safely inline." };

                    var replacementText = initExpr.ToString();

                    // Rebuild the source text: replace usages descending, then remove the declaration
                    var text = (await document.GetTextAsync()).ToString();

                    // Replace usages from end to start
                    foreach (var usage in usages.OrderByDescending(u => u.SpanStart))
                    {
                        var needsParens = usage.Parent is BinaryExpressionSyntax or
                            ConditionalExpressionSyntax or MemberAccessExpressionSyntax;
                        var rep = needsParens ? $"({replacementText})" : replacementText;
                        text = text[..usage.SpanStart] + rep + text[(usage.SpanStart + usage.Span.Length)..];
                    }

                    // Remove the local declaration statement (full span including trivia)
                    // Re-parse to get updated offsets after replacements
                    // Instead, use the original span (all replacements were after it if we go end-to-start,
                    // but the usages may be after the declaration â€” so the declaration span is stable)
                    var declSpan = localDecl.FullSpan;
                    text = text[..declSpan.Start] + text[(declSpan.Start + declSpan.Length)..];

                    if (dryRun)
                        return new
                        {
                            dryRun = true,
                            symbolName,
                            kind = "local variable",
                            file = document.FilePath,
                            usagesInlined = usages.Count,
                            initializerExpression = replacementText,
                        };

                    await File.WriteAllTextAsync(document.FilePath!, text, System.Text.Encoding.UTF8);

                    return new
                    {
                        dryRun = false,
                        symbolName,
                        kind = "local variable",
                        file = document.FilePath,
                        usagesInlined = usages.Count,
                    };
                }
            }
        }

        return new { error = $"Symbol '{symbolName}' not found as a local variable, method, or property." };
    }
}
