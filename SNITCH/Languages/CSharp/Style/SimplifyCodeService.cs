using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.Style;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// C# on top of the shared pipeline: the rule set, the language adapter, the name the report uses, and
/// the three ids this engine does not rewrite itself but can reach — IDE0005, IDE0032 and IDE0290 are
/// the jobs of <c>optimize_usings</c>, <c>property_to_auto</c> and <c>convert_to_primary_constructor</c>.
/// <para>
/// Delegating is not wiring: the first service is scope-shaped and takes one call, the other two are
/// per-symbol, so a scope-shaped request has to be turned into the candidate list first. That is what the
/// scan below does, and every candidate still goes through its own service, its own compiler gate and
/// its own edit-history record.
/// </para>
/// </summary>
public sealed class SimplifyCodeService : StyleRewriteService, ISimplifyCodeService
{
    /// <summary>The ids this tool accepts but answers with another engine service.</summary>
    private static readonly HashSet<string> DelegatedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "IDE0005", "IDE0032", "IDE0290",
    };

    /// <summary>A scope request is a list of candidates, and a list has to stop somewhere.</summary>
    private const int MaxCandidates = 50;

    private readonly IRoslynWorkspace _workspace;

    public SimplifyCodeService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
        : base(workspace, store, CSharpStyleRuleSet.Default, CSharpStyleLanguage.Default, "simplify_code")
    {
        _workspace = workspace;
    }

    /// <summary>Every rule id the C# engine can apply.</summary>
    public static new IReadOnlyList<StyleRule> Rules => StyleRewriteCatalog.Supported;

    /// <summary>The ids Microsoft's code-style table defines that this engine does not rewrite, with the reason.</summary>
    public static new IReadOnlyList<(string Id, string Reason)> NotRewritten => StyleRewriteCatalog.NotRewritten;

    public override async Task<object> SimplifyAsync(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var (delegated, remainder) = Split(rules);
        if (delegated.Count == 0)
            return await base.SimplifyAsync(scope, filePath, projectName, rules, dryRun, cancellationToken);

        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level == "document" && string.IsNullOrWhiteSpace(filePath))
            return new { error = "scope=document needs filePath." };

        // The style half of the request runs first and reports exactly as it always does; the delegated
        // half is then one report per id. A request that names only delegated ids has no style half.
        object? style = remainder.Count == 0
            ? null
            : await base.SimplifyAsync(level, filePath, projectName, string.Join(',', remainder), dryRun, cancellationToken);

        var delegatedReports = new List<object>();
        foreach (var id in delegated)
            delegatedReports.Add(await RunDelegatedAsync(id, level, filePath, projectName, dryRun, cancellationToken));

        return new
        {
            @refactoring = "simplify_code",
            scope = level,
            rules = delegated.Concat(remainder).ToList(),
            dryRun,
            applied = !dryRun,
            style,
            delegated = delegatedReports,
            note = "IDE0005, IDE0032 and IDE0290 are answered by optimize_usings, property_to_auto and " +
                   "convert_to_primary_constructor: each keeps its own compiler gate and records its own edits. " +
                   "They are not part of rules=all — ask for them by id.",
        };
    }

    /// <summary>
    /// Splits the requested ids into the delegated ones and the rest. "All" means the style catalogue: a
    /// header, a property rewrite and a constructor rewrite are not side effects of a blanket request.
    /// </summary>
    private static (List<string> Delegated, List<string> Remainder) Split(string? rules)
    {
        var delegated = new List<string>();
        var remainder = new List<string>();
        if (string.IsNullOrWhiteSpace(rules)) return (delegated, remainder);

        foreach (var id in rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            (DelegatedIds.Contains(id) ? delegated : remainder).Add(id);

        return (delegated, remainder);
    }

    private async Task<object> RunDelegatedAsync(
        string id,
        string level,
        string? filePath,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { id, error = "No solution could be auto-located from the working directory." };

        var documents = InScope(solution, level, filePath, projectName).ToList();
        if (documents.Count == 0)
            return new { id, error = "The selected scope holds no C# document." };

        return id.ToUpperInvariant() switch
        {
            "IDE0005" => new
            {
                id,
                action = "optimize_usings",
                result = await new OptimizeUsingsService(_workspace)
                    .OptimizeUsings(projectName, level == "document" ? filePath : null, sort: true, dryRun),
            },
            "IDE0032" => await RunPropertiesAsync(id, documents, dryRun, cancellationToken),
            "IDE0290" => await RunPrimaryConstructorsAsync(id, documents, dryRun, cancellationToken),
            _ => new { id, error = "unknown delegated id" },
        };
    }

    private async Task<object> RunPropertiesAsync(
        string id,
        IReadOnlyList<Document> documents,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Path, string? Type, string Property)>();
        foreach (var document in documents)
        {
            if (document.FilePath is not { Length: > 0 } path) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null) continue;

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!HasTrivialAccessors(property)) continue;
                var owner = property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                candidates.Add((path, owner?.Identifier.ValueText, property.Identifier.ValueText));
                if (candidates.Count >= MaxCandidates) break;
            }

            if (candidates.Count >= MaxCandidates) break;
        }

        var service = new PropertyToAutoService(_workspace);
        var results = new List<object>();
        foreach (var candidate in candidates)
        {
            results.Add(new
            {
                candidate.Type,
                candidate.Property,
                result = await service.PropertyToAuto(
                    candidate.Path, candidate.Property, candidate.Type, dryRun, cancellationToken),
            });
        }

        return new { id, action = "property_to_auto", candidates = candidates.Count, results, truncated = candidates.Count >= MaxCandidates };
    }

    private async Task<object> RunPrimaryConstructorsAsync(
        string id,
        IReadOnlyList<Document> documents,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Path, string Type)>();
        foreach (var document in documents)
        {
            if (document.FilePath is not { Length: > 0 } path) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null) continue;

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!HasSingleAssigningConstructor(type)) continue;
                candidates.Add((path, type.Identifier.ValueText));
                if (candidates.Count >= MaxCandidates) break;
            }

            if (candidates.Count >= MaxCandidates) break;
        }

        var service = new ConvertToPrimaryConstructorService(_workspace);
        var results = new List<object>();
        foreach (var candidate in candidates)
        {
            results.Add(new
            {
                candidate.Type,
                result = await service.ConvertToPrimaryConstructor(candidate.Path, candidate.Type, dryRun, cancellationToken),
            });
        }

        return new { id, action = "convert_to_primary_constructor", candidates = candidates.Count, results, truncated = candidates.Count >= MaxCandidates };
    }

    /// <summary>
    /// The syntactic shape of a trivial property — a getter that reads one name and a setter that writes
    /// the same one. It is only a filter: whether the field is used elsewhere is the service's question.
    /// </summary>
    private static bool HasTrivialAccessors(PropertyDeclarationSyntax property)
    {
        if (property.AccessorList?.Accessors is not { Count: 2 } accessors) return false;
        var getter = accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setter = accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (getter is null || setter is null) return false;

        ExpressionSyntax? read = getter.ExpressionBody?.Expression;
        if (read is null
            && getter.Body?.Statements is { Count: 1 } getterStatements
            && getterStatements[0] is ReturnStatementSyntax { Expression: { } } returnStatement)
        {
            read = returnStatement.Expression;
        }

        if (read is not IdentifierNameSyntax readName) return false;

        ExpressionSyntax? written = setter.ExpressionBody?.Expression;
        if (written is null
            && setter.Body?.Statements is { Count: 1 } setterStatements
            && setterStatements[0] is ExpressionStatementSyntax setterExpression)
        {
            written = setterExpression.Expression;
        }

        return written is AssignmentExpressionSyntax
        {
            Left: IdentifierNameSyntax writeName,
            Right: IdentifierNameSyntax { Identifier.ValueText: "value" },
        } && writeName.Identifier.ValueText == readName.Identifier.ValueText;
    }

    /// <summary>One constructor whose body only assigns parameters to members.</summary>
    private static bool HasSingleAssigningConstructor(TypeDeclarationSyntax type)
    {
        var constructors = type.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count != 1) return false;

        var body = constructors[0].Body?.Statements;
        if (body is not { Count: > 0 }) return false;

        foreach (var statement in body)
        {
            if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment })
                return false;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                return false;
            if (assignment.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                return false;
            if (assignment.Right is not IdentifierNameSyntax)
                return false;
        }

        return true;
    }

    private static IEnumerable<Document> InScope(Solution solution, string level, string? filePath, string? projectName)
    {
        var documents = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(project => project.Documents)
            .Where(document => document.SourceCodeKind == SourceCodeKind.Regular);

        if (level == "document")
        {
            documents = documents.Where(document =>
                document.FilePath is not null &&
                string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }

        return documents;
    }
}
