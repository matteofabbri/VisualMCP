using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IAnalyzeComplexityService
{
    /// <summary>
    /// Estimates the time and space complexity (Big-O) of the callables in the solution: which grow
    /// with n, why, and what to change. Heuristic and static: <c>profile_*</c> measures the truth.
    /// </summary>
    Task<object> AnalyzeComplexity(
        string? projectName = null,
        string? filePath = null,
        string? methodName = null,
        int topN = 25,
        string minComplexity = "O(n)");
}

public sealed class AnalyzeComplexityService : IAnalyzeComplexityService
{
    private const int MaxTopN = 200;
    private const int MaxListedLoops = 12;
    private const int MaxListedMemorySites = 12;

    private readonly IRoslynWorkspace _workspace;
    public AnalyzeComplexityService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeComplexity(
        string? projectName = null,
        string? filePath = null,
        string? methodName = null,
        int topN = 25,
        string minComplexity = "O(n)")
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        topN = Math.Clamp(topN, 1, MaxTopN);
        var threshold = ComplexityCost.ParseMin(minComplexity);

        var catalog = new MethodCatalog();
        var callables = new List<Callable>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                if (IsGenerated(document.FilePath)) continue;
                if (filePath is not null &&
                    (document.FilePath is null || !document.FilePath.Contains(filePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var node in CallableDeclarations(root))
                {
                    var symbol = DeclaredSymbol(model, node);
                    if (symbol is null || symbol.IsImplicitlyDeclared) continue;

                    var body = BodyOf(node);
                    if (body is null) continue;
                    if (!seen.Add(symbol.OriginalDefinition)) continue;

                    var callable = new Callable
                    {
                        Symbol = symbol,
                        Declaration = node,
                        Body = body,
                        Model = model,
                        Document = document,
                        Project = project,
                        Method = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FullName = symbol.ToDisplayString(),
                        ContainingType = symbol.ContainingType?.ToDisplayString() ?? string.Empty,
                        Kind = KindOf(symbol),
                        Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    };

                    callables.Add(callable);
                    catalog.Add(callable);
                }
            }
        }

        var analyzed = callables
            .Select(callable => new Analyzed(callable, catalog.Evaluate(callable)))
            .ToList();

        var matched = analyzed
            .Where(a => MatchesName(a.Callable, methodName))
            .Where(a => a.Result.Time.Rank >= threshold.Rank)
            .OrderByDescending(a => a.Result.Time.Rank)
            .ThenByDescending(a => a.Result.Space.Rank)
            .ThenBy(a => a.Callable.FullName, StringComparer.Ordinal)
            .ToList();

        var worstTime = analyzed.Count == 0
            ? (ComplexityCost?)null
            : analyzed.Select(a => a.Result.Time).Max();
        var worstSpace = analyzed.Count == 0
            ? (ComplexityCost?)null
            : analyzed.Select(a => a.Result.Space).Max();

        var methods = matched.Take(topN).Select(a => new
        {
            a.Callable.Method,
            a.Callable.FullName,
            a.Callable.ContainingType,
            a.Callable.Kind,
            Project = a.Callable.Project.Name,
            FilePath = a.Callable.Document.FilePath,
            a.Callable.Line,
            TimeComplexity = a.Result.Time.ToBigO(),
            SpaceComplexity = a.Result.Space.ToBigO(),
            a.Result.Confidence,
            Loops = a.Result.Loops.Take(MaxListedLoops).Select(loop => new
            {
                loop.Kind,
                loop.Line,
                Iterations = loop.Iterations.ToBigO(),
                loop.NestingDepth,
                loop.Bound,
            }).ToList(),
            Recursion = a.Result.Recursion is null ? null : new
            {
                a.Result.Recursion.SelfCalls,
                a.Result.Recursion.Pattern,
                TimeComplexity = a.Result.Recursion.Time.ToBigO(),
                StackComplexity = a.Result.Recursion.Stack.ToBigO(),
                a.Result.Recursion.Evidence,
            },
            Memory = a.Result.SpaceFacts.Take(MaxListedMemorySites).Select(fact => new
            {
                fact.Kind,
                fact.Line,
                SpaceComplexity = fact.Cost.ToBigO(),
                fact.Basis,
            }).ToList(),
            CalledMethods = a.Result.Callees,
            Evidence = a.Result.Evidence,
            Warnings = a.Result.Warnings,
            Recommendations = a.Result.Recommendations,
        }).ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            fileFilter = filePath,
            methodFilter = methodName,
            topN,
            minComplexity = threshold.ToBigO(),
            methodCount = analyzed.Count,
            matchedMethods = matched.Count,
            worstTimeComplexity = worstTime?.ToBigO() ?? "n/a",
            worstSpaceComplexity = worstSpace?.ToBigO() ?? "n/a",
            distribution = new
            {
                Constant = analyzed.Count(a => ClassOf(a.Result.Time) == "constant"),
                Logarithmic = analyzed.Count(a => ClassOf(a.Result.Time) == "logarithmic"),
                Linear = analyzed.Count(a => ClassOf(a.Result.Time) == "linear"),
                Linearithmic = analyzed.Count(a => ClassOf(a.Result.Time) == "linearithmic"),
                Quadratic = analyzed.Count(a => ClassOf(a.Result.Time) == "quadratic"),
                CubicOrWorse = analyzed.Count(a => ClassOf(a.Result.Time) == "cubicOrWorse"),
                Exponential = analyzed.Count(a => ClassOf(a.Result.Time) == "exponential"),
                Unknown = analyzed.Count(a => ClassOf(a.Result.Time) == "unknown"),
            },
            methods,
            notes = new[]
            {
                "Static estimate from the syntax tree, not a measurement: loops (nesting and bounds), LINQ chains, " +
                "sorting, linear collection lookups, recursion shape and allocation sites. Ranking: O(?) > O(2^n) > O(n^3) > " +
                "O(n^2) > O(n log n) > O(n) > O(log n) > O(1). Use profile_collect/profile_analyze when the real cost matters.",
                "n is the input size: a collection length, a loop bound or the recursion argument. Methods that call other " +
                "methods in the solution are inlined with their own complexity; calls into referenced assemblies are assumed O(1) " +
                "unless they are a known BCL API (Array.Sort, List.Contains, LINQ operators, string and file APIs).",
                "Space is peak auxiliary memory: materialised collections/arrays plus the call-stack depth of recursion. " +
                "Temporary allocations that are discarded immediately are not counted as growth.",
            },
        };
    }

    private readonly record struct Analyzed(Callable Callable, ComplexityResult Result);

    private static IEnumerable<SyntaxNode> CallableDeclarations(SyntaxNode root) =>
        root.DescendantNodes().Where(node => node is
            MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax or
            OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or LocalFunctionStatementSyntax or
            AccessorDeclarationSyntax);

    private static IMethodSymbol? DeclaredSymbol(SemanticModel model, SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => model.GetDeclaredSymbol(method) as IMethodSymbol,
        ConstructorDeclarationSyntax constructor => model.GetDeclaredSymbol(constructor) as IMethodSymbol,
        DestructorDeclarationSyntax destructor => model.GetDeclaredSymbol(destructor) as IMethodSymbol,
        OperatorDeclarationSyntax @operator => model.GetDeclaredSymbol(@operator) as IMethodSymbol,
        ConversionOperatorDeclarationSyntax conversion => model.GetDeclaredSymbol(conversion) as IMethodSymbol,
        LocalFunctionStatementSyntax localFunction => model.GetDeclaredSymbol(localFunction) as IMethodSymbol,
        AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor) as IMethodSymbol,
        _ => null,
    };

    private static SyntaxNode? BodyOf(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
        ConstructorDeclarationSyntax constructor => (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody?.Expression,
        DestructorDeclarationSyntax destructor => (SyntaxNode?)destructor.Body ?? destructor.ExpressionBody?.Expression,
        OperatorDeclarationSyntax @operator => (SyntaxNode?)@operator.Body ?? @operator.ExpressionBody?.Expression,
        ConversionOperatorDeclarationSyntax conversion => (SyntaxNode?)conversion.Body ?? conversion.ExpressionBody?.Expression,
        LocalFunctionStatementSyntax localFunction => (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody?.Expression,
        AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression,
        _ => null,
    };

    private static bool IsGenerated(string? path) =>
        path is not null &&
        (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
         || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
         || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
         || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesName(Callable callable, string? methodName) =>
        methodName is null
        || callable.Method.Contains(methodName, StringComparison.OrdinalIgnoreCase)
        || callable.FullName.Contains(methodName, StringComparison.OrdinalIgnoreCase);

    private static string KindOf(IMethodSymbol symbol) => symbol.MethodKind switch
    {
        MethodKind.Constructor => "constructor",
        MethodKind.Destructor => "destructor",
        MethodKind.LocalFunction => "local function",
        MethodKind.PropertyGet => "getter",
        MethodKind.PropertySet => "setter",
        MethodKind.UserDefinedOperator => "operator",
        MethodKind.Conversion => "conversion operator",
        MethodKind.EventAdd => "event add",
        MethodKind.EventRemove => "event remove",
        _ => "method",
    };

    private static string ClassOf(ComplexityCost cost) =>
        cost.Unknown ? "unknown"
        : cost.Exponential ? "exponential"
        : cost.Degree switch
        {
            0 => cost.LogPower > 0 ? "logarithmic" : "constant",
            1 => cost.LogPower > 0 ? "linearithmic" : "linear",
            2 => "quadratic",
            _ => "cubicOrWorse",
        };
}
