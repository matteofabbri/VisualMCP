using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>One syntax tree the compilation holds but no <see cref="Document"/> points at.</summary>
public sealed record GeneratedTreeInfo(string Path, int Length, bool HasGeneratedCodeAttribute, IReadOnlyList<string> Types);

/// <summary>
/// Shows what source generators contributed to a project: the generators the project references and the
/// trees they produced, which exist in the compilation but not as documents. The tools read documents,
/// so this is the answer to "is that type generated, and where does it come from" instead of a silent
/// absence from navigation and analysis.
/// </summary>
public interface IGeneratedSourcesService
{
    Task<object> ListAsync(string? projectName, bool includeSource = false, int maxCharsPerTree = 20_000);
}

public sealed class GeneratedSourcesService : IGeneratedSourcesService
{
    private readonly IRoslynWorkspace _workspace;
    public GeneratedSourcesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, bool includeSource = false,
        int maxCharsPerTree = 20_000)
    {
        maxCharsPerTree = Math.Clamp(maxCharsPerTree, 200, 200_000);
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var perProject = new List<object>();
        var totalGenerated = 0;

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            var documentPaths = project.Documents
                .Select(document => document.FilePath)
                .Where(path => path is not null)
                .Select(path => Path.GetFullPath(path!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var generated = FindGeneratedTrees(documentPaths, compilation.SyntaxTrees);
            totalGenerated += generated.Count;

            var trees = compilation.SyntaxTrees
                .Where(tree => tree.FilePath.Length > 0)
                .GroupBy(tree => Path.GetFullPath(tree.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            perProject.Add(new
            {
                project = project.Name,
                generators = GeneratorsOf(project),
                documentCount = project.Documents.Count(),
                generatedTreeCount = generated.Count,
                generatedTrees = generated
                    .Select(info => Describe(info, trees, includeSource, maxCharsPerTree))
                    .ToList(),
            });
        }

        return new
        {
            projectFilter = projectName ?? "all",
            projects = perProject.Count,
            generatedTreeCount = totalGenerated,
            includeSource,
            maxCharsPerTree,
            byProject = perProject,
            note = totalGenerated == 0
                ? "No generated tree was found: either the project uses no source generator, or this workspace does not run them (an Adhoc workspace never does)."
                : "Generated trees exist in the compilation but not as documents: navigation, read_symbol and the editors " +
                  "cannot address them. Their types are visible to the compiler, so a symbol with no source location is " +
                  "usually one of these. includeSource=true returns each tree's text, capped at maxCharsPerTree.",
        };
    }

    /// <summary>The text of one generated tree, capped. <c>Truncated</c> says the cap cut it.</summary>
    public static (string Source, bool Truncated) ReadSource(SyntaxTree tree, int maxChars)
    {
        var source = tree.GetText().ToString();
        return source.Length > maxChars
            ? (source[..maxChars], true)
            : (source, false);
    }

    /// <summary>The report row for one tree: its metadata, or metadata plus text when the text was asked for.</summary>
    private static object Describe(GeneratedTreeInfo info, IReadOnlyDictionary<string, SyntaxTree> trees,
        bool includeSource, int maxChars)
    {
        if (!includeSource) return info;

        var (source, truncated) = trees.TryGetValue(Path.GetFullPath(info.Path), out var tree)
            ? ReadSource(tree, maxChars)
            : (string.Empty, false);

        return new
        {
            path = info.Path,
            length = info.Length,
            hasGeneratedCodeAttribute = info.HasGeneratedCodeAttribute,
            types = info.Types,
            source,
            truncated,
        };
    }

    /// <summary>The trees the compilation has and the project's documents do not — the generated ones.</summary>
    public static List<GeneratedTreeInfo> FindGeneratedTrees(
        IReadOnlySet<string> documentPaths,
        IEnumerable<SyntaxTree> trees)
    {
        var generated = new List<GeneratedTreeInfo>();

        foreach (var tree in trees)
        {
            if (tree.FilePath.Length == 0) continue;
            if (documentPaths.Contains(Path.GetFullPath(tree.FilePath))) continue;

            var root = tree.GetRoot();
            var types = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(declaration => declaration.Identifier.ValueText)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToList();

            var hasGeneratedCodeAttribute = root.DescendantNodes().OfType<AttributeSyntax>().Any(attribute =>
            {
                var name = attribute.Name.ToString();
                return name == "GeneratedCode" || name.EndsWith("GeneratedCode", StringComparison.Ordinal) ||
                       name.EndsWith("GeneratedCodeAttribute", StringComparison.Ordinal);
            });

            generated.Add(new GeneratedTreeInfo(tree.FilePath, root.FullSpan.Length, hasGeneratedCodeAttribute, types));
        }

        return generated
            .OrderBy(tree => tree.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GeneratorsOf(Project project)
    {
        var generators = new List<string>();
        foreach (var reference in project.AnalyzerReferences)
        {
            try
            {
                foreach (var generator in reference.GetGenerators(LanguageNames.CSharp))
                {
                    var type = generator.GetType();
                    generators.Add(type.FullName ?? type.Name);
                }
            }
            catch
            {
                // An analyzer reference that cannot produce generators (a missing dependency) is not a failure.
            }
        }

        return generators.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }
}
