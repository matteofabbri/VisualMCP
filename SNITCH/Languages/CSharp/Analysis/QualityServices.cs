using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Test quality, read from the tests themselves: a test with no assertion proves nothing, an empty
/// test is a placeholder, `Test1` says nothing when it fails at 3am, and `async void` hides a failure
/// from the runner. Four cheap rules over every test method the test frameworks recognise.
/// </summary>
public interface IAnalyzeTestQualityService
{
    Task<object> Analyze(string? projectName, int top, CancellationToken cancellationToken = default);
}

public sealed class AnalyzeTestQualityService : IAnalyzeTestQualityService
{
    private static readonly string[] TestAttributes =
        ["Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod"];

    private readonly IRoslynWorkspace _workspace;

    public AnalyzeTestQualityService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Analyze(
        string? projectName, int top, CancellationToken cancellationToken = default)
    {
        var limit = top <= 0 ? 100 : top;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var tests = new List<object>();
        var byIssue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var testCount = 0;
        var flagged = 0;

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!IsTest(method)) continue;
                    testCount++;

                    var issues = Issues(method);
                    if (issues.Count == 0) continue;
                    flagged++;

                    foreach (var issue in issues)
                        byIssue[issue] = byIssue.GetValueOrDefault(issue) + 1;

                    if (tests.Count >= limit) continue;

                    tests.Add(new
                    {
                        test = method.Identifier.ValueText,
                        type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
                        @file = document.FilePath,
                        line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        score = Math.Max(0, 100 - 20 * issues.Count),
                        issues,
                    });
                }
            }
        }

        return new
        {
            tool = "analyze_test_quality",
            projectFilter = projectName ?? "all",
            testCount,
            flaggedCount = flagged,
            byIssue,
            tests,
            note = "no-assert: the body never asserts; empty: no statements; non-descriptive-name: shorter than " +
                   "six characters or TestN; async-void: the runner cannot observe the failure.",
        };
    }

    private static bool IsTest(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(attribute => attribute.Name.ToString())
            .Any(name => TestAttributes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)));

    internal static List<string> Issues(MethodDeclarationSyntax method)
    {
        var issues = new List<string>();

        var hasBody = method.Body is not null || method.ExpressionBody is not null;
        if (!hasBody || method.Body is { Statements.Count: 0 }) issues.Add("empty");

        if (!Asserts(method)) issues.Add("no-assert");

        var name = method.Identifier.ValueText;
        if (name.Length < 6 || Regex.IsMatch(name, "^Test[0-9]*$")) issues.Add("non-descriptive-name");

        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword) &&
            method.ReturnType.ToString() == "void")
            issues.Add("async-void");

        return issues;
    }

    private static bool Asserts(MethodDeclarationSyntax method)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return false;

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var target = invocation.Expression.ToString();

            if (target.StartsWith("Assert", StringComparison.Ordinal) ||
                target.Contains(".Should", StringComparison.Ordinal) ||
                target.StartsWith("Verify", StringComparison.Ordinal) ||
                target.Contains(".Verify", StringComparison.Ordinal))
                return true;
        }

        // `=> Assert.Equal(...)` is an expression-bodied test with one call, already covered above.
        return false;
    }
}

/// <summary>
/// Missing argument validation: a public method that takes a reference-type parameter and never checks
/// it is a null waiting to happen three frames deep. The rule is deliberately syntactic — the guard
/// must appear in the method, whatever form it takes (<c>if (x is null) throw</c>,
/// <c>ArgumentNullException.ThrowIfNull(x)</c>, <c>x ?? throw</c>).
/// </summary>
public interface IFindMissingValidationService
{
    Task<object> Find(string? projectName, int top, CancellationToken cancellationToken = default);
}

public sealed class FindMissingValidationService : IFindMissingValidationService
{
    private readonly IRoslynWorkspace _workspace;

    public FindMissingValidationService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Find(
        string? projectName, int top, CancellationToken cancellationToken = default)
    {
        var limit = top <= 0 ? 100 : top;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var methods = new List<object>();
        var parametersChecked = 0;
        var unguarded = 0;

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol) continue;
                    if (!IsPublicSurface(symbol)) continue;

                    var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                    if (body is null) continue;

                    var missing = new List<string>();

                    foreach (var parameter in symbol.Parameters)
                    {
                        if (parameter.IsParams || parameter.Type.IsValueType) continue;
                        if (parameter.RefKind != RefKind.None) continue;

                        parametersChecked++;
                        if (IsGuarded(body, parameter.Name)) continue;

                        unguarded++;
                        missing.Add(parameter.Name);
                    }

                    if (missing.Count == 0 || methods.Count >= limit) continue;

                    methods.Add(new
                    {
                        type = symbol.ContainingType.ToDisplayString(),
                        method = symbol.Name,
                        @file = document.FilePath,
                        line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        unguarded = missing,
                    });
                }
            }
        }

        return new
        {
            tool = "find_missing_validation",
            projectFilter = projectName ?? "all",
            parametersChecked,
            unguardedCount = unguarded,
            methods,
            note = "A guard counts when the parameter is checked in this method: `if (x is null) throw`, " +
                   "`ArgumentNullException.ThrowIfNull(x)`, `x ?? throw`, or a pattern with `is null`.",
        };
    }

    private static bool IsPublicSurface(IMethodSymbol method) =>
        (method.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal) &&
        (method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor);

    internal static bool IsGuarded(SyntaxNode body, string name)
    {
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var target = invocation.Expression.ToString();
            if (!target.Contains("ThrowIfNull", StringComparison.Ordinal) &&
                !target.Contains("ArgumentNullException", StringComparison.Ordinal))
                continue;

            if (ContainsName(invocation.ArgumentList, name)) return true;
        }

        foreach (var statement in body.DescendantNodes().OfType<IfStatementSyntax>())
            if (IsNullTest(statement.Condition, name)) return true;

        foreach (var coalesce in body.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!coalesce.IsKind(SyntaxKind.CoalesceExpression)) continue;
            if (ContainsName(coalesce.Left, name) &&
                (coalesce.Right is ThrowExpressionSyntax || coalesce.Right.ToString().Contains("throw", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary><c>x == null</c>, <c>x is null</c>, <c>null == x</c>, <c>!(x is not null)</c>.</summary>
    private static bool IsNullTest(ExpressionSyntax condition, string name)
    {
        foreach (var binary in condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (!binary.IsKind(SyntaxKind.EqualsExpression)) continue;

            var leftNull = binary.Left.IsKind(SyntaxKind.NullLiteralExpression);
            var rightNull = binary.Right.IsKind(SyntaxKind.NullLiteralExpression);
            if (!leftNull && !rightNull) continue;

            if (ContainsName(leftNull ? binary.Right : binary.Left, name)) return true;
        }

        foreach (var pattern in condition.DescendantNodesAndSelf().OfType<IsPatternExpressionSyntax>())
            if (pattern.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax })
                if (ContainsName(pattern.Expression, name)) return true;

        return false;
    }

    private static bool ContainsName(SyntaxNode node, string name) =>
        node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(identifier => identifier.Identifier.ValueText == name);
}
