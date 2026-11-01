using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>
/// Answers "which methods throw this exception?" the way a semantic query does: the direct throws, and
/// — when asked — the methods that reach one by calling a thrower, with the shortest call path. A
/// method that catches the type is reported as a boundary instead of propagating through it.
/// </summary>
public interface IFindThrowSitesService
{
    Task<object> Find(
        string exceptionType, bool transitive, string? projectName, int maxResults,
        CancellationToken cancellationToken = default);
}

public sealed class FindThrowSitesService : IFindThrowSitesService
{
    private readonly IRoslynWorkspace _workspace;

    public FindThrowSitesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Find(
        string exceptionType, bool transitive, string? projectName, int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exceptionType))
            return new { error = "exceptionType is required, e.g. 'InvalidOperationException' or 'System.ArgumentException'." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new
            {
                error = "No C# solution could be auto-located from the working directory. Call load_solution first.",
            };

        var query = exceptionType.Trim();
        var target = await ResolveAsync(solution, query, projectName, cancellationToken);
        var targetName = target ?? (query.Contains('.') ? query : "System." + query);

        var methods = await MethodCollector.CollectAsync(solution, projectName, cancellationToken);
        var index = new Dictionary<IMethodSymbol, MethodDeclarationModel>(SymbolEqualityComparer.Default);
        foreach (var method in methods) index[method.Definition] = method;

        var direct = new Dictionary<IMethodSymbol, (string Type, string File, int Line)>(SymbolEqualityComparer.Default);
        var boundaries = new Dictionary<IMethodSymbol, (string File, int Line)>(SymbolEqualityComparer.Default);
        var callers = new Dictionary<IMethodSymbol, List<(IMethodSymbol Caller, string File, int Line)>>(SymbolEqualityComparer.Default);

        foreach (var method in methods)
        {
            foreach (var node in method.Scope.DescendantNodes(n => n is not LocalFunctionStatementSyntax))
            {
                switch (node)
                {
                    case ThrowStatementSyntax { Expression: { } thrown } when ThrownType(thrown, method.Model) is { } type &&
                                                                           Matches(type, targetName):
                        direct.TryAdd(method.Definition, (type.Name, method.File, MethodCollector.Line(node)));
                        break;

                    case ThrowExpressionSyntax { Expression: { } thrown } when ThrownType(thrown, method.Model) is { } type &&
                                                                             Matches(type, targetName):
                        direct.TryAdd(method.Definition, (type.Name, method.File, MethodCollector.Line(node)));
                        break;

                    case CatchClauseSyntax { Declaration: { } declaration } when
                        method.Model.GetTypeInfo(declaration.Type).Type is { } caught && Matches(caught, targetName):
                        boundaries.TryAdd(method.Definition, (method.File, MethodCollector.Line(node)));
                        break;

                    case InvocationExpressionSyntax invocation when
                        method.Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol callee &&
                        index.ContainsKey((callee.ReducedFrom ?? callee).OriginalDefinition):
                    {
                        var declared = (callee.ReducedFrom ?? callee).OriginalDefinition;
                        if (!callers.TryGetValue(declared, out var list))
                            callers[declared] = list = [];
                        list.Add((method.Definition, method.File, MethodCollector.Line(invocation)));
                        break;
                    }
                }
            }
        }

        // Reverse breadth-first search: a method reaches a throw unless it catches the type first, so a
        // boundary is reported but does not propagate to its own callers.
        var reached = new Dictionary<IMethodSymbol, PathEntry>(SymbolEqualityComparer.Default);
        foreach (var thrower in direct.Keys)
            if (!boundaries.ContainsKey(thrower))
                reached[thrower] = new PathEntry(null, null);

        if (transitive)
        {
            var queue = new Queue<IMethodSymbol>(reached.Keys);
            while (queue.Count > 0)
            {
                var callee = queue.Dequeue();
                if (!callers.TryGetValue(callee, out var callSites)) continue;

                foreach (var (caller, file, line) in callSites)
                {
                    if (boundaries.ContainsKey(caller) || reached.ContainsKey(caller)) continue;
                    reached[caller] = new PathEntry(callee, new FlowStep(file, line, $"calls {Name(callee)}"));
                    queue.Enqueue(caller);
                }
            }
        }

        var root = Path.GetDirectoryName(_workspace.LoadedSolutionPath ?? string.Empty) ?? string.Empty;
        var limit = Math.Clamp(maxResults, 1, 5_000);

        var throwers = reached
            .Select(pair => new
            {
                method = Name(pair.Key),
                type = direct.TryGetValue(pair.Key, out var thrown) ? thrown.Type : null,
                file = (direct.TryGetValue(pair.Key, out var location) ? location.File : index[pair.Key].File) is { } file
                    ? Relative(root, file) : null,
                line = direct.TryGetValue(pair.Key, out var where) ? where.Line : MethodCollector.Line(index[pair.Key].Scope),
                kind = direct.ContainsKey(pair.Key) ? "direct" : "indirect",
                path = PathOf(pair.Key, reached, root),
            })
            .OrderBy(entry => entry.kind == "direct" ? 0 : 1)
            .ThenBy(entry => entry.method, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return new
        {
            tool = "find_throw_sites",
            exception = query,
            resolvedType = targetName,
            transitive,
            projectFilter = projectName ?? "all",
            methodsAnalyzed = methods.Count,
            directCount = direct.Count,
            throwerCount = reached.Count,
            boundaryCount = boundaries.Count,
            throwers = throwers.Where(entry => entry.kind == "direct").Concat(throwers.Where(entry => entry.kind != "direct")).ToList(),
            boundaries = boundaries.Select(pair => new
            {
                method = Name(pair.Key),
                file = Relative(root, pair.Value.File),
                line = pair.Value.Line,
                note = "catches the exception, so it does not propagate to its own callers",
            }).OrderBy(entry => entry.method, StringComparer.OrdinalIgnoreCase).ToList(),
            note = "A direct throw creates the exception; an indirect entry reaches one through the calls listed in " +
                   "its path, which is the shortest chain found. A method that catches the type is a boundary: it " +
                   "is reported, and the search does not continue past it. Throws inside local functions are " +
                   "attributed to the local function.",
        };
    }

    // ── the type the caller asked about ────────────────────────────────────────

    private static async Task<string?> ResolveAsync(
        Solution solution, string query, string? projectName, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects.Where(project =>
                     projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            var direct = compilation.GetTypeByMetadataName(query) ?? compilation.GetTypeByMetadataName("System." + query);
            if (direct is not null && DerivesFromException(direct)) return direct.ToDisplayString();

            foreach (var type in Types(compilation.GlobalNamespace, limit: 20_000))
                if (type.Name == query && DerivesFromException(type))
                    return type.ToDisplayString();
        }

        return null;
    }

    private static bool DerivesFromException(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "System.Exception")
                return true;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace, int limit)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(@namespace);
        var seen = 0;

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    stack.Push(child);
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    if (++seen >= limit) yield break;
                }
            }
        }
    }

    /// <summary>A thrown type matches when it is the target or derives from it.</summary>
    private static bool Matches(ITypeSymbol type, string target)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (string.Equals(current.ToDisplayString(), target, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static ITypeSymbol? ThrownType(ExpressionSyntax expression, SemanticModel model) =>
        expression is ObjectCreationExpressionSyntax creation
            ? model.GetTypeInfo(creation).Type
            : model.GetTypeInfo(expression).Type;

    // ── output helpers ─────────────────────────────────────────────────────────

    private static List<object> PathOf(
        IMethodSymbol method,
        Dictionary<IMethodSymbol, PathEntry> reached,
        string root)
    {
        var path = new List<object>();
        var current = method;
        var guard = 0;

        while (reached.TryGetValue(current, out var entry) && entry.Parent is { } parent && guard++ < 32)
        {
            path.Add(new
            {
                caller = Name(current),
                callee = Name(parent),
                file = entry.Step is null ? null : Relative(root, entry.Step.File),
                line = entry.Step?.Line,
            });
            current = parent;
        }

        return path;
    }

    private static string Name(IMethodSymbol method) =>
        (method.ContainingType is { } type ? type.Name + "." : string.Empty) + method.Name;

    private static string Relative(string root, string file)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return file;
        try { return Path.GetRelativePath(root, file).Replace('\\', '/'); }
        catch (ArgumentException) { return file; }
    }

    private sealed record PathEntry(IMethodSymbol? Parent, FlowStep? Step);
}
