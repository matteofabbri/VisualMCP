using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Navigation;

/// <summary>
/// The call hierarchy in both directions: who calls this member (incoming) and what it calls
/// (outgoing), as a tree with a depth. find_callers gives the direct callers only; this answers the
/// question a call-hierarchy window answers — the chain, not one level.
/// </summary>
public interface ICallHierarchyService
{
    Task<object> BuildAsync(string symbolName, string? containingType, string direction, int depth);
}

public sealed class CallHierarchyService : ICallHierarchyService
{
    private const int MaxNodes = 300;

    private readonly IRoslynWorkspace _workspace;
    public CallHierarchyService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> BuildAsync(string symbolName, string? containingType = null, string direction = "both",
        int depth = 2)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return new { error = "A symbol name is required." };

        var mode = (direction ?? "both").Trim().ToLowerInvariant();
        if (mode is not ("incoming" or "outgoing" or "both"))
            return new { error = $"Unknown direction '{direction}'. Use incoming, outgoing or both." };

        depth = Math.Clamp(depth, 1, 5);

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Member);

        var symbols = candidates
            .Where(symbol => containingType is null ||
                             (symbol.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
        {
            var types = await SymbolFinder.FindSourceDeclarationsAsync(
                solution, name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type);
            symbols = types.ToList();
        }

        if (symbols.Count == 0) return new { error = $"Symbol '{symbolName}' not found." };
        if (symbols.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {symbols.Count} members named '{symbolName}'. Pass containingType.",
                candidates = symbols.Select(symbol => symbol.ToDisplayString()).Take(10).ToList(),
            };

        var target = symbols[0];
        var budget = new Budget(MaxNodes);

        var incoming = mode is "incoming" or "both" ? await WhoCallsAsync(target, solution, depth, budget) : null;
        var outgoing = mode is "outgoing" or "both" ? await WhatItCallsAsync(target, solution, depth, budget) : null;

        return new
        {
            symbol = target.ToDisplayString(),
            kind = target.Kind.ToString(),
            direction = mode,
            depth,
            incoming,
            outgoing,
            truncated = budget.Exhausted,
            note = "incoming = callers (a use that is not inside a callable member is reported as a field or " +
                   "property initializer); outgoing = the members this one calls directly. Depth counts levels " +
                   "from the symbol.",
        };
    }

    private static async Task<object?> WhoCallsAsync(ISymbol symbol, Solution solution, int depth, Budget budget)
    {
        if (depth <= 0 || !budget.Take()) return null;

        var callers = new Dictionary<string, (string Name, string Kind, string? File, int? Line, ISymbol Symbol)>(
            StringComparer.Ordinal);

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution))
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                var document = location.Document;
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                var node = root.FindToken(location.Location.SourceSpan.Start).Parent;
                if (node is null) continue;

                var declarator = node.AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>()
                    .FirstOrDefault(member => member is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                        or FieldDeclarationSyntax or EventDeclarationSyntax or BaseTypeDeclarationSyntax);

                if (declarator is null) continue;

                var declared = model.GetDeclaredSymbol(declarator);
                if (declared is null) continue;

                var key = declared.ToDisplayString() + "@" + document.FilePath;
                if (callers.ContainsKey(key)) continue;

                var span = declarator.GetLocation().GetLineSpan();
                callers[key] = (declared.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    declared.Kind.ToString(), document.FilePath, span.StartLinePosition.Line + 1, declared);
            }
        }

        var children = new List<object>();
        foreach (var caller in callers.Values.Take(50))
        {
            var nested = await WhoCallsAsync(caller.Symbol, solution, depth - 1, budget);
            children.Add(new
            {
                name = caller.Name,
                kind = caller.Kind,
                file = caller.File,
                line = caller.Line,
                callers = nested,
            });
        }

        return new { name = symbol.Name, count = children.Count, children };
    }

    private static async Task<object?> WhatItCallsAsync(ISymbol symbol, Solution solution, int depth, Budget budget)
    {
        if (depth <= 0 || !budget.Take()) return null;
        if (symbol is not IMethodSymbol method)
            return symbol is INamedTypeSymbol ? null : new { name = symbol.Name, note = "not a method: no outgoing calls" };

        var callees = new Dictionary<string, (string Name, string? File, int? Line, ISymbol Symbol)>(StringComparer.Ordinal);

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = await reference.GetSyntaxAsync();
            var document = solution.GetDocument(syntax.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync();
            if (model is null || document?.FilePath is null) continue;

            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
                if (callee.MethodKind is MethodKind.DelegateInvoke or MethodKind.BuiltinOperator) continue;

                var key = callee.ToDisplayString();
                if (callees.ContainsKey(key)) continue;

                var location = callee.Locations.FirstOrDefault(candidate => candidate.IsInSource)?.GetLineSpan();
                callees[key] = (callee.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    location?.Path, location is null ? null : location.Value.StartLinePosition.Line + 1, callee);
            }
        }

        var children = new List<object>();
        foreach (var callee in callees.Values.Take(50))
        {
            var nested = await WhatItCallsAsync(callee.Symbol, solution, depth - 1, budget);
            children.Add(new
            {
                name = callee.Name,
                file = callee.File,
                line = callee.Line,
                calls = nested,
            });
        }

        return new { name = symbol.Name, count = children.Count, children };
    }

    /// <summary>A shared node budget, so a wide hierarchy cannot walk the whole solution.</summary>
    private sealed class Budget
    {
        private int _left;
        public Budget(int left) => _left = left;

        public bool Exhausted => _left <= 0;

        public bool Take()
        {
            if (_left <= 0) return false;
            _left--;
            return true;
        }
    }
}
