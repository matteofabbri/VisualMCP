using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IAnalyzeNamespaceCouplingService
{
    Task<object> AnalyzeNamespaceCoupling(string? namespaceFilter, double minInstability);
}

public sealed class AnalyzeNamespaceCouplingService : IAnalyzeNamespaceCouplingService
{
    private readonly IRoslynWorkspace _workspace;
    public AnalyzeNamespaceCouplingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeNamespaceCoupling(
        string? namespaceFilter = null,
        double minInstability = 0.0)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        // Map: namespace -> set of namespaces it depends on (efferent)
        var efferent = new Dictionary<string, HashSet<string>>();
        // Map: namespace -> set of namespaces that depend on it (afferent)
        var afferent = new Dictionary<string, HashSet<string>>();
        // Map: namespace -> (total types, abstract types)
        var typeStats = new Dictionary<string, (int Total, int Abstract)>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var model = await document.GetSemanticModelAsync();
                var root  = await document.GetSyntaxRootAsync();
                if (model is null || root is null) continue;

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var sym = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (sym is null) continue;

                    var ns = sym.ContainingNamespace?.ToDisplayString() ?? "<global>";
                    if (namespaceFilter is not null && !ns.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Count type stats
                    if (!typeStats.ContainsKey(ns)) typeStats[ns] = (0, 0);
                    var (tot, abs) = typeStats[ns];
                    bool isAbstract = sym.IsAbstract || sym.TypeKind == TypeKind.Interface;
                    typeStats[ns] = (tot + 1, abs + (isAbstract ? 1 : 0));

                    if (!efferent.ContainsKey(ns)) efferent[ns] = [];
                    if (!afferent.ContainsKey(ns)) afferent[ns] = [];

                    // Walk all referenced types in the body
                    foreach (var node in typeDecl.DescendantNodes())
                    {
                        var typeInfo = model.GetTypeInfo(node);
                        var refType  = (typeInfo.Type ?? typeInfo.ConvertedType) as INamedTypeSymbol;
                        if (refType is null) continue;

                        var refNs = refType.ContainingNamespace?.ToDisplayString() ?? "<global>";
                        if (refNs == ns || refNs == "<global>") continue;
                        if (namespaceFilter is not null && !refNs.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        efferent[ns].Add(refNs);

                        if (!afferent.ContainsKey(refNs)) afferent[refNs] = [];
                        if (!efferent.ContainsKey(refNs)) efferent[refNs] = [];
                        afferent[refNs].Add(ns);
                    }
                }
            }
        }

        var allNs = efferent.Keys.Union(afferent.Keys).Union(typeStats.Keys).Distinct().ToList();

        var metrics = allNs.Select(ns =>
        {
            var ce = efferent.GetValueOrDefault(ns)?.Count ?? 0;
            var ca = afferent.GetValueOrDefault(ns)?.Count ?? 0;
            var (total, abstracts) = typeStats.GetValueOrDefault(ns);

            double instability   = (ce + ca) == 0 ? 0.0 : (double)ce / (ce + ca);
            double abstractness  = total == 0 ? 0.0 : (double)abstracts / total;
            double distance      = Math.Abs(abstractness + instability - 1.0);

            return new
            {
                Namespace      = ns,
                AfferentCa     = ca,
                EfferentCe     = ce,
                Instability    = Math.Round(instability, 3),
                Abstractness   = Math.Round(abstractness, 3),
                Distance       = Math.Round(distance, 3),
                TotalTypes     = total,
                AbstractTypes  = abstracts,
                DependsOn      = efferent.GetValueOrDefault(ns)?.OrderBy(x => x).ToList() ?? [],
                UsedBy         = afferent.GetValueOrDefault(ns)?.OrderBy(x => x).ToList() ?? [],
            };
        })
        .Where(m => m.Instability >= minInstability)
        .OrderByDescending(m => m.Distance)
        .ToList();

        return new
        {
            namespaceFilter = namespaceFilter ?? "all",
            minInstability,
            namespaceCount = metrics.Count,
            metrics,
        };
    }
}
