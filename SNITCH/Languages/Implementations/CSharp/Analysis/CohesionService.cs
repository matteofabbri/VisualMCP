using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// LCOM4 cohesion: members that touch the same field (directly, or through fields in common) form one
/// connected component, and a class with more than one component is really more than one class. It is
/// the metric that answers "should this be split?", next to the complexity report's "is this too hard?".
/// </summary>
public interface IGetCohesionMetricsService
{
    Task<object> Measure(string? projectName, int minComponents, int top, CancellationToken cancellationToken = default);
}

public sealed partial class GetCohesionMetricsService : IGetCohesionMetricsService
{
    private readonly IRoslynWorkspace _workspace;

    public GetCohesionMetricsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Measure(
        string? projectName, int minComponents, int top, CancellationToken cancellationToken = default)
    {
        var minimum = minComponents <= 0 ? 2 : minComponents;
        var limit = top <= 0 ? 20 : top;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var classes = new List<Cohesion>();
        var typeCount = 0;

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var declaration in root.DescendantNodes().Where(IsTypeDeclaration))
                {
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type) continue;
                    if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) continue;

                    typeCount++;
                    var measure = Measure(type, declaration, model, document, cancellationToken);
                    if (measure is not null) classes.Add(measure);
                }
            }
        }

        var flagged = classes
            .Where(measure => measure.Components >= minimum)
            .OrderByDescending(measure => measure.Components)
            .ThenByDescending(measure => measure.Members)
            .Take(limit)
            .ToList();

        return new
        {
            tool = "get_cohesion_metrics",
            projectFilter = projectName ?? "all",
            typeCount,
            flaggedCount = classes.Count(measure => measure.Components >= minimum),
            classes = flagged.Select(measure => new
            {
                type = measure.Type,
                @file = measure.File,
                line = measure.Line,
                members = measure.Members,
                fieldTouching = measure.FieldTouching,
                stateless = measure.Stateless,
                components = measure.Components,
                lcom4 = measure.Components,
                cohesion = measure.CohesionScore,
            }).ToList(),
            note = "LCOM4 = connected components among the members that use a field. 1 means one responsibility; " +
                   "stateless members (helpers that touch no field) are counted separately, not as extra components.",
        };
    }

    private sealed record Cohesion(
        string Type, string File, int Line, int Members, int FieldTouching, int Stateless, int Components, double CohesionScore);

    private static Cohesion? Measure(
        INamedTypeSymbol type,
        SyntaxNode declaration,
        SemanticModel model,
        Document document,
        CancellationToken cancellationToken)
    {
        var members = new List<ISymbol>();

        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IFieldSymbol { IsConst: true }:
                case IFieldSymbol:
                case IMethodSymbol { MethodKind: MethodKind.Ordinary }:
                case IPropertySymbol:
                    members.Add(member);
                    break;
            }
        }

        if (members.Count < 2) return null;

        var fields = type.GetMembers().OfType<IFieldSymbol>()
            .Where(field => !field.IsStatic && !field.IsConst)
            .ToHashSet(SymbolEqualityComparer.Default);

        var touched = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var member in members)
        {
            var touchedFields = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var node = reference.GetSyntax(cancellationToken);
                if (node.SyntaxTree != model.SyntaxTree) continue;

                foreach (var name in NameNodes(node))
                {
                    if (model.GetSymbolInfo(name, cancellationToken).Symbol is IFieldSymbol field &&
                        fields.Contains(field))
                        touchedFields.Add(field);
                }
            }

            touched[member] = touchedFields;
        }

        var fieldTouching = touched.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).ToList();
        var stateless = members.Count - fieldTouching.Count;

        // Union-find: every field joins the members that use it, so a shared field is a connection and
        // two members joined through a chain of fields are in the same component.
        var parent = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        foreach (var member in fieldTouching) parent[member] = member;

        ISymbol Root(ISymbol member)
        {
            var current = member;
            while (!SymbolEqualityComparer.Default.Equals(parent[current], current))
                current = parent[current];
            return current;
        }

        void Union(ISymbol left, ISymbol right)
        {
            var leftRoot = Root(left);
            var rightRoot = Root(right);
            if (!SymbolEqualityComparer.Default.Equals(leftRoot, rightRoot)) parent[leftRoot] = rightRoot;
        }

        var byField = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        foreach (var member in fieldTouching)
        {
            foreach (var field in touched[member])
            {
                if (byField.TryGetValue(field, out var other)) Union(member, other);
                else byField[field] = member;
            }
        }

        var components = fieldTouching.Select(Root).Distinct(SymbolEqualityComparer.Default).Count();
        if (components == 0) components = 1;

        var cohesion = fieldTouching.Count <= 1
            ? 1.0
            : Math.Round(1.0 - (components - 1.0) / (fieldTouching.Count - 1.0), 2);

        return new Cohesion(
            type.ToDisplayString(),
            document.FilePath ?? document.Name,
            declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            members.Count,
            fieldTouching.Count,
            stateless,
            components,
            cohesion);
    }

    /// <summary>The type declarations the metric reads: C# classes and structs, VB classes, structures and modules.</summary>
    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? IsVisualBasicTypeDeclaration(node)
            : node is TypeDeclarationSyntax;

    /// <summary>
    /// The name nodes whose symbol a member body can read. The C# and VB identifier types are different
    /// classes with the same job, so the language picks the enumeration and the caller only sees nodes.
    /// </summary>
    private static IEnumerable<SyntaxNode> NameNodes(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicNameNodes(node)
            : node.DescendantNodes().OfType<IdentifierNameSyntax>();
}
