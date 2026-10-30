using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Navigation;

/// <summary>
/// The map of the codebase for an agent that has just landed in it: the projects and what each one
/// is, the namespaces with the types that carry the most surface, the entry points, and the
/// repository's own Markdown so the reader knows where to read next. Syntax-level on purpose — it has
/// to answer in one call, not compile the world.
/// </summary>
public interface ISolutionMapService
{
    Task<object> MapAsync(string? projectName, int maxAreas, int maxTypes);
}

public sealed class SolutionMapService : ISolutionMapService
{
    private readonly IRoslynWorkspace _workspace;
    public SolutionMapService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MapAsync(string? projectName = null, int maxAreas = 20, int maxTypes = 10)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        maxAreas = Math.Clamp(maxAreas, 1, 200);
        maxTypes = Math.Clamp(maxTypes, 1, 100);

        var projects = new List<object>();
        var namespaces = new Dictionary<string, (int Types, int Members, List<(string Name, int Members)> Top)>(StringComparer.Ordinal);
        var entryPoints = new List<object>();
        var skipped = 0;

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            var isTest = project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                         ReferencesTestFramework(project.FilePath);

            var documents = project.Documents.Where(document => document.SupportsSyntaxTree).ToList();
            var typeCount = 0;

            foreach (var document in documents)
            {
                var root = await document.GetSyntaxRootAsync() as CompilationUnitSyntax;
                if (root is null) { skipped++; continue; }

                if (root.Members.Any(member => member is GlobalStatementSyntax))
                    entryPoints.Add(new
                    {
                        kind = "topLevelStatements",
                        project = project.Name,
                        file = document.FilePath,
                    });

                foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    typeCount++;
                    var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "(global)";
                    var members = type switch
                    {
                        TypeDeclarationSyntax declaration => declaration.Members.Count,
                        EnumDeclarationSyntax enumeration => enumeration.Members.Count,
                        _ => 0,
                    };

                    if (!namespaces.TryGetValue(ns, out var bucket))
                        namespaces[ns] = bucket = (0, 0, new List<(string, int)>());

                    bucket.Types++;
                    bucket.Members += members;
                    bucket.Top.Add((type.Identifier.ValueText, members));
                    namespaces[ns] = bucket;
                }

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (method.Identifier.ValueText != "Main") continue;
                    if (!method.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) continue;

                    entryPoints.Add(new
                    {
                        kind = "main",
                        project = project.Name,
                        file = document.FilePath,
                        line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }
            }

            projects.Add(new
            {
                name = project.Name,
                language = project.Language,
                kind = project.CompilationOptions?.OutputKind.ToString(),
                isTest,
                assemblyName = project.AssemblyName,
                projectFile = project.FilePath,
                directory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath),
                documents = documents.Count,
                types = typeCount,
                projectReferences = project.ProjectReferences.Count(),
                metadataReferences = project.MetadataReferences.Count(),
            });
        }

        var areas = namespaces
            .OrderByDescending(entry => entry.Value.Members)
            .Take(maxAreas)
            .Select(entry => new
            {
                @namespace = entry.Key,
                types = entry.Value.Types,
                members = entry.Value.Members,
                largest = entry.Value.Top
                    .OrderByDescending(type => type.Members)
                    .Take(maxTypes)
                    .Select(type => new { name = type.Name, members = type.Members })
                    .ToList(),
            })
            .ToList();

        var solutionPath = solution.FilePath ?? _workspace.LoadedSolutionPath;
        var directory = solutionPath is null ? null : Path.GetDirectoryName(solutionPath);

        return new
        {
            solution = solutionPath,
            projectCount = projects.Count,
            projects,
            areas,
            entryPoints,
            docs = ReadDocs(directory),
            skipped,
            note = "Syntax-level map: projects, the namespaces that carry the most members, the entry " +
                   "points and the repository's own Markdown. For a symbol-level view use reference_index " +
                   "(or find_symbol); for the capabilities of the SERVER (not of this codebase) use capability_report.",
        };
    }

    private static bool ReferencesTestFramework(string? projectFile)
    {
        if (projectFile is null || !File.Exists(projectFile)) return false;

        try
        {
            var text = File.ReadAllText(projectFile);
            return text.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("mstest", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The repository's Markdown, with the first heading of each file, so the reader knows where to go.</summary>
    private static List<object> ReadDocs(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];

        var docs = new List<object>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
                         .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                        !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                         .OrderBy(file => file.Length)
                         .Take(40))
            {
                var firstHeading = File.ReadLines(file).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
                docs.Add(new
                {
                    path = Path.GetRelativePath(directory, file).Replace('\\', '/'),
                    title = firstHeading?.TrimStart('#', ' '),
                });
            }
        }
        catch
        {
            // A doc listing is a convenience.
        }

        return docs;
    }
}
