using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis.Performance;

/// <summary>One performance anti-pattern, with the place and the fix.</summary>
public sealed record PerformanceFinding(
    string Rule,
    string Severity,
    string File,
    int Line,
    string Member,
    string Message,
    string Snippet);

/// <summary>Every finding of one project, plus what the run looked at.</summary>
public sealed record PerformanceReport(
    string Project,
    int FilesAnalyzed,
    IReadOnlyList<PerformanceFinding> Findings)
{
    public IReadOnlyDictionary<string, int> Counts => Findings
        .GroupBy(finding => finding.Rule)
        .OrderByDescending(group => group.Count())
        .ToDictionary(group => group.Key, group => group.Count());
}

public interface IPerformanceAnalyzer
{
    Task<object> FindAntiPatternsAsync(string? projectName, int maxResults, CancellationToken cancellationToken = default);
}

/// <summary>
/// The three allocation anti-patterns an agent can actually fix mechanically, each checked with the
/// semantic model rather than by matching text:
/// <list type="bullet">
/// <item><c>PF0001</c> string concatenation in a loop — quadratic copies, and a <c>StringBuilder</c> or a
/// <c>string.Join</c> is the fix.</item>
/// <item><c>PF0002</c> a LINQ query materialised inside a loop — the sequence is rebuilt every
/// iteration, which is usually a call that belongs before the loop.</item>
/// <item><c>PF0003</c> a collection or array allocated with a constant size inside a loop — the
/// allocation is per iteration and is rarely needed.</item>
/// </list>
/// A finding is a place to look, not a proof: the engine reports the shape and the symbol, and the
/// message says what the alternative is.
/// </summary>
public sealed class PerformancePatternAnalyzer : IPerformanceAnalyzer
{
    private static readonly HashSet<string> Materializers = new(StringComparer.Ordinal)
    {
        "ToList", "ToArray", "ToHashSet", "ToDictionary", "ToLookup",
    };

    private readonly IRoslynWorkspace _workspace;

    public PerformancePatternAnalyzer(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindAntiPatternsAsync(
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var reports = new List<PerformanceReport>();
        var remaining = Math.Max(1, maxResults);

        foreach (var project in projects)
        {
            var findings = new List<PerformanceFinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var files = 0;

            foreach (var document in project.Documents.Where(IsAnalyzable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files++;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var loop in root.DescendantNodes().Where(IsLoop))
                {
                    var body = LoopBody(loop);
                    if (body is null) continue;

                    foreach (var node in body.DescendantNodesAndSelf())
                    {
                        var finding = Describe(node, model, document, cancellationToken);
                        if (finding is null) continue;
                        if (!seen.Add($"{finding.Rule}|{finding.File}|{finding.Line}|{finding.Snippet}")) continue;

                        findings.Add(finding);
                        if (findings.Count >= remaining) break;
                    }

                    if (findings.Count >= remaining) break;
                }

                if (findings.Count >= remaining) break;
            }

            reports.Add(new PerformanceReport(project.Name, files, findings));
            remaining -= findings.Count;
            if (remaining <= 0) break;
        }

        var total = reports.Sum(report => report.Findings.Count);

        return new
        {
            tool = "find_performance_anti_patterns",
            total,
            findings = reports
                .SelectMany(report => report.Findings)
                .OrderBy(finding => finding.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(finding => finding.Line)
                .ToList(),
            projects = reports.Select(report => new
            {
                project = report.Project,
                filesAnalyzed = report.FilesAnalyzed,
                findings = report.Findings.Count,
                byRule = report.Counts,
            }).ToList(),
            rules = new[]
            {
                new { id = "PF0001", title = "String concatenation in a loop", fix = "StringBuilder, or string.Join over the sequence" },
                new { id = "PF0002", title = "LINQ materialised inside a loop", fix = "move the query before the loop, or keep it lazy" },
                new { id = "PF0003", title = "Collection allocated inside a loop", fix = "hoist the allocation, or reuse one buffer" },
            },
            note = total == 0
                ? "No shape from this rule set was found. Use profile_hotpaths / profile_allocations when the problem is measured rather than read."
                : "Each finding is a place to look: the rule set is deliberately small and semantic, and a finding does not prove a hot path.",
        };
    }

    private static bool IsAnalyzable(Document document) =>
        document.SourceCodeKind == SourceCodeKind.Regular &&
        !document.Name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        !document.Name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoop(SyntaxNode node) => node
        is ForStatementSyntax or ForEachStatementSyntax
        or WhileStatementSyntax or DoStatementSyntax
        or ForEachVariableStatementSyntax;

    private static SyntaxNode? LoopBody(SyntaxNode loop) => loop switch
    {
        ForStatementSyntax @for => @for.Statement,
        ForEachStatementSyntax @foreach => @foreach.Statement,
        ForEachVariableStatementSyntax @foreach => @foreach.Statement,
        WhileStatementSyntax @while => @while.Statement,
        DoStatementSyntax @do => @do.Statement,
        _ => null,
    };

    private static PerformanceFinding? Describe(
        SyntaxNode node,
        SemanticModel model,
        Document document,
        CancellationToken cancellationToken)
    {
        var file = document.FilePath ?? document.Name;
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var member = Member(node);

        switch (node)
        {
            case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.AddAssignmentExpression):
            {
                if (model.GetTypeInfo(assignment.Left, cancellationToken).Type?.SpecialType != SpecialType.System_String)
                    return null;

                return new PerformanceFinding(
                    "PF0001", "warning", file, line, member,
                    $"'{assignment.Left}' is a string appended inside a loop: each iteration copies the whole value.",
                    Snippet(assignment));
            }

            case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax access
                                                              && Materializers.Contains(access.Name.Identifier.ValueText):
            {
                var receiver = model.GetTypeInfo(access.Expression, cancellationToken).Type;
                if (receiver is null || receiver.SpecialType == SpecialType.System_String) return null;

                return new PerformanceFinding(
                    "PF0002", "info", file, line, member,
                    $"'{access.Name.Identifier.ValueText}()' inside a loop rebuilds the sequence on every iteration.",
                    Snippet(invocation));
            }

            case ArrayCreationExpressionSyntax array when HasConstantSize(array):
                return new PerformanceFinding(
                    "PF0003", "info", file, line, member,
                    "An array with a constant size is allocated on every iteration of the loop.",
                    Snippet(array));

            case ObjectCreationExpressionSyntax creation when IsConstantAllocation(creation, model, cancellationToken):
                return new PerformanceFinding(
                    "PF0003", "info", file, line, member,
                    "A collection is allocated on every iteration of the loop.",
                    Snippet(creation));

            default:
                return null;
        }
    }

    private static bool HasConstantSize(ArrayCreationExpressionSyntax array) =>
        array.Type.RankSpecifiers.Any(rank => rank.Sizes.Count == 1 && rank.Sizes[0] is LiteralExpressionSyntax);

    private static bool IsConstantAllocation(
        ObjectCreationExpressionSyntax creation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(creation, cancellationToken).Type;
        if (type is null) return false;

        var isCollection = type.AllInterfaces.Any(@interface =>
            @interface.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.ICollection<T>");
        if (!isCollection) return false;

        // A capacity argument is a deliberate allocation; without one the collection grows, which is worse.
        return creation.ArgumentList?.Arguments.Count == 0;
    }

    /// <summary>
    /// The containing member. A local function is a statement, not a member declaration, so it is looked
    /// up first: the nearest one wins over the method that holds it.
    /// </summary>
    private static string Member(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case BaseTypeDeclarationSyntax type:
                    return type.Identifier.ValueText;
            }
        }

        return "(file)";
    }

    private static string Snippet(SyntaxNode node)
    {
        var single = string.Join(' ', node.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
        return single.Length <= 120 ? single : single[..120] + "…";
    }
}
