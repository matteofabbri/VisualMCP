using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Reports which types and members of assemblies outside the solution the solution actually uses, with
/// the call sites. The internals of a referenced assembly are not analysed, but its <em>surface</em> is:
/// this is the cross-assembly question a call graph cannot answer alone — "what do we depend on, and
/// where".
/// </summary>
public interface IExternalUsagesService
{
    Task<object> FindExternalUsages(string? projectName, string? assemblyName, bool includeFramework, int maxResults);
}

public sealed class ExternalUsagesService : IExternalUsagesService
{
    private readonly IRoslynWorkspace _workspace;
    public ExternalUsagesService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <summary>Assemblies that ship with .NET: noise unless the caller asks for them.</summary>
    private static readonly string[] FrameworkPrefixes =
    [
        "System", "Microsoft", "netstandard", "mscorlib", "WindowsBase", "Accessibility",
    ];

    public async Task<object> FindExternalUsages(
        string? projectName = null,
        string? assemblyName = null,
        bool includeFramework = false,
        int maxResults = 200)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var solutionAssemblies = solution.Projects
            .Select(project => project.AssemblyName ?? project.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The path and version of each referenced assembly: what decompile_type / assembly_workspace need
        // to open the internals this call does not analyse.
        var referenced = new Dictionary<string, (string? Path, string? Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
                if (referenced.ContainsKey(assembly.Name)) continue;

                referenced[assembly.Name] = (
                    (reference as PortableExecutableReference)?.FilePath,
                    assembly.Identity.Version.ToString());
            }
        }

        var limit = Math.Clamp(maxResults < 1 ? 200 : maxResults, 1, 5000);
        var filter = string.IsNullOrWhiteSpace(assemblyName) ? null : assemblyName.Trim();

        var usages = new Dictionary<string, ExternalUsage>(StringComparer.Ordinal);
        var scannedDocuments = 0;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;
                scannedDocuments++;

                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(name).Symbol;
                    if (symbol is null) continue;
                    if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) continue;

                    var assembly = ContainingAssemblyOf(symbol);
                    if (assembly is null) continue;
                    if (solutionAssemblies.Contains(assembly)) continue;
                    if (!includeFramework && IsFrameworkAssembly(assembly)) continue;
                    if (filter is not null && !assembly.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                    var containingType = symbol.ContainingType?.ToDisplayString() ?? assembly;
                    var member = symbol is INamedTypeSymbol ? null : symbol.Name;
                    var key = $"{assembly}::{containingType}::{member}";

                    if (usages.TryGetValue(key, out var existing))
                    {
                        usages[key] = existing with { Count = existing.Count + 1 };
                        continue;
                    }

                    usages[key] = new ExternalUsage(
                        assembly,
                        containingType,
                        member,
                        project.Name,
                        document.FilePath,
                        name.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        symbol.Kind.ToString(),
                        1);
                }
            }
        }

        var ordered = usages.Values
            .OrderByDescending(usage => usage.Count)
            .ThenBy(usage => usage.Assembly, StringComparer.OrdinalIgnoreCase)
            .ThenBy(usage => usage.Type, StringComparer.Ordinal)
            .ToList();

        var byAssembly = ordered
            .GroupBy(usage => usage.Assembly, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                assembly = group.Key,
                version = referenced.GetValueOrDefault(group.Key).Version,
                path = referenced.GetValueOrDefault(group.Key).Path,
                distinctMembers = group.Count(),
                usages = group.Sum(usage => usage.Count),
                topTypes = group.GroupBy(usage => usage.Type)
                    .OrderByDescending(types => types.Sum(usage => usage.Count))
                    .Take(5)
                    .Select(types => new { type = types.Key, usages = types.Sum(usage => usage.Count) })
                    .ToList(),
            })
            .OrderByDescending(group => group.usages)
            .ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            assemblyFilter = filter,
            includeFramework,
            scannedDocuments,
            externalAssemblies = byAssembly.Count,
            distinctMembers = ordered.Count,
            totalUsages = ordered.Sum(usage => usage.Count),
            byAssembly,
            usages = ordered.Take(limit).ToList(),
            truncated = ordered.Count > limit,
            note = "This is the used surface of each referenced assembly; its other internals are not analysed here. " +
                   "Each assembly row carries the path and version that decompile_type and assembly_workspace read " +
                   "the internals from. " +
                   (includeFramework ? "Framework assemblies are included." : "System/Microsoft assemblies are excluded; pass includeFramework=true to include them."),
        };
    }

    private static string? ContainingAssemblyOf(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly;
        if (assembly is null) return null;
        // A type declared in source belongs to the solution by definition, even when its assembly
        // name happens to collide with a reference.
        return symbol.OriginalDefinition.Locations.Any(location => location.IsInSource) ? null : assembly.Name;
    }

    private static bool IsFrameworkAssembly(string assembly) =>
        FrameworkPrefixes.Any(prefix => assembly.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private sealed record ExternalUsage(
        string Assembly,
        string Type,
        string? Member,
        string Project,
        string? File,
        int Line,
        string Kind,
        int Count);
}
