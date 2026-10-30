using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The namespace-level dependency edges of the solution, collected the way an architecture rule needs
/// them: the declaring type's namespace on one side, the type its code actually touches on the other.
/// </summary>
internal static class NamespaceEdges
{
    public sealed record Edge(string From, string To, string File, int Line);

    public static async Task<List<Edge>> CollectAsync(
        Solution solution, string? namespaceFilter, CancellationToken cancellationToken)
    {
        var edges = new List<Edge>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol) continue;

                    var source = symbol.ContainingNamespace?.ToDisplayString() ?? "<global>";
                    if (namespaceFilter is not null && !source.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        var info = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
                        var target = info as INamedTypeSymbol ?? info?.ContainingType;
                        if (target is null) continue;

                        var targetNamespace = target.ContainingNamespace?.ToDisplayString() ?? "<global>";
                        if (targetNamespace is "<global>" || targetNamespace == source) continue;
                        if (namespaceFilter is not null &&
                            !targetNamespace.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var position = identifier.GetLocation().GetLineSpan().StartLinePosition;
                        edges.Add(new Edge(source, targetNamespace, document.FilePath ?? document.Name, position.Line + 1));
                    }
                }
            }
        }

        return edges;
    }
}

/// <summary>
/// Layer enforcement: the caller names the layers top-to-bottom (Web, Application, Domain,
/// Infrastructure); a dependency may only point downwards. Every edge that points back up is a
/// violation with a file and line to look at — the check ReSharper's architecture rules do, over the
/// real type usages instead of the project references.
/// </summary>
public interface ICheckLayerViolationsService
{
    Task<object> Check(string layers, string? namespaceFilter, int maxResults, CancellationToken cancellationToken = default);
}

public sealed class CheckLayerViolationsService : ICheckLayerViolationsService
{
    private readonly IRoslynWorkspace _workspace;

    public CheckLayerViolationsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Check(
        string layers, string? namespaceFilter, int maxResults, CancellationToken cancellationToken = default)
    {
        var ordered = (layers ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count < 2)
            return new { error = "layers must list at least two layer names, top-most first (e.g. 'Web,Application,Domain,Infrastructure')." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var edges = await NamespaceEdges.CollectAsync(solution, namespaceFilter, cancellationToken);

        var violations = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            var from = LayerOf(edge.From, ordered);
            var to = LayerOf(edge.To, ordered);
            if (from is null || to is null) continue;

            var fromRank = ordered.IndexOf(from);
            var toRank = ordered.IndexOf(to);

            // Same layer is allowed; a dependency may only go downwards (to a higher index).
            if (toRank >= fromRank) continue;

            var key = from + ">" + to + "|" + edge.From + "|" + edge.To;
            if (!seen.Add(key)) continue;

            violations.Add(new
            {
                from,
                to,
                fromNamespace = edge.From,
                toNamespace = edge.To,
                @file = edge.File,
                line = edge.Line,
                rule = $"'{from}' must not depend on '{to}' (that dependency points up).",
            });

            if (violations.Count >= (maxResults <= 0 ? 200 : maxResults)) break;
        }

        return new
        {
            tool = "check_layer_violations",
            layers = ordered,
            edgesConsidered = edges.Count,
            violationCount = violations.Count,
            clean = violations.Count == 0,
            violations,
            note = violations.Count == 0
                ? "Every mapped dependency between these layers points downwards."
                : "Each entry is a place where the code reaches back up a layer.",
        };
    }

    /// <summary>
    /// Which layer a namespace belongs to: the longest layer name that appears as a namespace segment,
    /// so `Web` and `WebApi` resolve correctly when both are listed.
    /// </summary>
    internal static string? LayerOf(string @namespace, IReadOnlyList<string> layers)
    {
        var segments = @namespace.Split('.');

        return layers
            .Where(layer => segments.Any(segment => string.Equals(segment, layer, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(layer => layer.Length)
            .FirstOrDefault();
    }
}

/// <summary>
/// Exports the dependency graph in a shape a diagram tool understands — Graphviz DOT or Mermaid — at
/// project or namespace level. `analyze_dependencies` answers the question in JSON; this produces the
/// artifact you paste into a document.
/// </summary>
public interface IExportDependencyGraphService
{
    Task<object> Export(string format, string level, CancellationToken cancellationToken = default);
}

public sealed class ExportDependencyGraphService : IExportDependencyGraphService
{
    private readonly IRoslynWorkspace _workspace;

    public ExportDependencyGraphService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Export(string format, string level, CancellationToken cancellationToken = default)
    {
        var kind = (format ?? "dot").Trim().ToLowerInvariant();
        if (kind is not ("dot" or "mermaid"))
            return new { error = "format must be dot or mermaid." };

        var scope = (level ?? "projects").Trim().ToLowerInvariant();
        if (scope is not ("projects" or "namespaces"))
            return new { error = "level must be projects or namespaces." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var edges = new List<(string From, string To)>();

        if (scope == "projects")
        {
            var names = solution.Projects.ToDictionary(project => project.Id, project => project.Name);
            edges.AddRange(solution.Projects
                .SelectMany(project => project.ProjectReferences
                    .Where(reference => names.ContainsKey(reference.ProjectId))
                    .Select(reference => (From: names[project.Id], To: names[reference.ProjectId]))));
        }
        else
        {
            var collected = await NamespaceEdges.CollectAsync(solution, null, cancellationToken);
            edges.AddRange(collected.Select(edge => (edge.From, edge.To)).Distinct());
        }

        var nodes = edges.SelectMany(edge => new[] { edge.From, edge.To }).Distinct(StringComparer.Ordinal).OrderBy(name => name).ToList();
        var graph = kind == "dot" ? Dot(nodes, edges) : Mermaid(nodes, edges);

        return new
        {
            tool = "export_dependency_graph",
            format = kind,
            level = scope,
            nodeCount = nodes.Count,
            edgeCount = edges.Distinct().Count(),
            graph,
        };
    }

    private static string Dot(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges)
    {
        var builder = new StringBuilder();
        builder.AppendLine("digraph dependencies {");
        builder.AppendLine("  rankdir=LR;");
        builder.AppendLine("  node [shape=box, fontname=\"Segoe UI\"];");
        foreach (var node in nodes) builder.Append("  \"").Append(Escape(node)).AppendLine("\";");
        foreach (var edge in edges.Distinct())
            builder.Append("  \"").Append(Escape(edge.From)).Append("\" -> \"").Append(Escape(edge.To)).AppendLine("\";");
        builder.Append('}');
        return builder.ToString();
    }

    private static string Mermaid(IReadOnlyList<string> nodes, IReadOnlyList<(string From, string To)> edges)
    {
        var builder = new StringBuilder();
        builder.AppendLine("graph LR");
        foreach (var node in nodes)
            builder.Append("  ").Append(Id(node)).Append("[\"").Append(Escape(node)).AppendLine("\"]");
        foreach (var edge in edges.Distinct())
            builder.Append("  ").Append(Id(edge.From)).Append(" --> ").AppendLine(Id(edge.To));
        return builder.ToString().TrimEnd();
    }

    private static string Id(string name) =>
        new string(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string Escape(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
