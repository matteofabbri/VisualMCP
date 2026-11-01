using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>One syntax tree the compilation holds but no <see cref="Document"/> points at.</summary>
public sealed record GeneratedTreeInfo(string Path, int Length, bool HasGeneratedCodeAttribute, IReadOnlyList<string> Types);

/// <summary>
/// A declaration found in a generated tree, with the rank its name match earned (4 exact, 3 prefix,
/// 1 contains) so a caller can rank it the same way it ranks a source declaration.
/// </summary>
public sealed record GeneratedDeclaration(ISymbol Symbol, string Project, string Path, int Line, int Column, int Rank);

/// <summary>
/// Shows what source generators contributed to a project: the generators the project references, the trees
/// they produced — which exist in the compilation but not as documents — and, asked by name, the
/// declarations those trees hold. This is the way into generated code that a document-based search cannot
/// reach, so "is that type generated, and where" has an answer instead of a silent absence.
/// </summary>
public interface IGeneratedSourcesService
{
    Task<object> ListAsync(string? projectName, bool includeSource = false, int maxCharsPerTree = 20_000,
        string? symbol = null);
}

public sealed class GeneratedSourcesService : IGeneratedSourcesService
{
    /// <summary>The rank an exact name match earns; prefix and contains are lower. Same scale as find_symbol.</summary>
    public const int ExactMatch = 4;
    public const int PrefixMatch = 3;
    public const int ContainsMatch = 1;

    private readonly IRoslynWorkspace _workspace;
    public GeneratedSourcesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, bool includeSource = false,
        int maxCharsPerTree = 20_000, string? symbol = null)
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

            var documentPaths = DocumentPaths(project);
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

        // Asked by name, the answer is the declarations inside the generated trees — the half of the model
        // a document search cannot see, returned with the same exact/prefix/contains ranking it uses.
        var matches = string.IsNullOrWhiteSpace(symbol)
            ? null
            : (await FindDeclarationsAsync(projects, symbol.Trim(), maxResults: 200))
                .Select(match => new
                {
                    project = match.Project,
                    match.Path,
                    match.Line,
                    match.Column,
                    kind = match.Symbol.Kind.ToString(),
                    symbol = match.Symbol.ToDisplayString(),
                    accessibility = match.Symbol.DeclaredAccessibility.ToString(),
                    matchKind = match.Rank switch
                    {
                        ExactMatch => "exact",
                        PrefixMatch => "prefix",
                        _ => "contains",
                    },
                    source = includeSource ? SourceOf(match.Symbol, maxCharsPerTree) : null,
                })
                .ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            projects = perProject.Count,
            generatedTreeCount = totalGenerated,
            includeSource,
            maxCharsPerTree,
            byProject = perProject,
            symbolFilter = symbol,
            symbolMatches = matches,
            note = totalGenerated == 0
                ? "No generated tree was found: either the project uses no source generator, or this workspace does not run them (an Adhoc workspace never does)."
                : "Generated trees exist in the compilation but not as documents. This tool is the inventory; " +
                  "find_symbol and read_symbol fall back to these trees, so a generated declaration can be located and " +
                  "read like a source one. Pass symbol= to search the generated declarations alone (exact, then prefix, " +
                  "then contains) — includeSource=true adds each match's code.",
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

    /// <summary>The full paths of the documents of a project, which are what tells a generated tree apart.</summary>
    public static HashSet<string> DocumentPaths(Project project) =>
        project.Documents
            .Select(document => document.FilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The declarations in a compilation's generated trees (the trees no document points at) whose name
    /// matches <paramref name="name"/>, best match first. This is the syntax-level way into generated code:
    /// a document search never sees these trees, and their symbols do have a location — in the emitted file.
    /// </summary>
    public static List<GeneratedDeclaration> FindDeclarations(
        Compilation compilation,
        string projectName,
        IReadOnlySet<string> documentPaths,
        string name,
        int maxResults = 200)
    {
        var results = new List<GeneratedDeclaration>();
        if (string.IsNullOrWhiteSpace(name)) return results;

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.FilePath.Length == 0) continue;
            if (documentPaths.Contains(Path.GetFullPath(tree.FilePath))) continue;

            var root = tree.GetRoot();

            // Filter by the written name first, so the semantic model is built only for a tree that can answer.
            var candidates = root.DescendantNodes()
                .Where(node => DeclarationName(node) is { Length: > 0 } declared &&
                               declared.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) continue;

            var model = compilation.GetSemanticModel(tree);
            var text = tree.GetText();

            foreach (var node in candidates)
            {
                var symbol = model.GetDeclaredSymbol(node);
                if (symbol is null) continue;
                if (symbol is ILocalSymbol) continue;

                var position = text.Lines.GetLinePosition(node.SpanStart);
                results.Add(new GeneratedDeclaration(
                    symbol,
                    projectName,
                    tree.FilePath,
                    position.Line + 1,
                    position.Character + 1,
                    RankOf(symbol.Name, name)));
            }
        }

        return results
            .OrderByDescending(declaration => declaration.Rank)
            .ThenBy(declaration => declaration.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(declaration => declaration.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>The same search over the projects of a solution, one compilation at a time.</summary>
    public static async Task<List<GeneratedDeclaration>> FindDeclarationsAsync(
        IEnumerable<Project> projects,
        string name,
        int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        var results = new List<GeneratedDeclaration>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            results.AddRange(FindDeclarations(compilation, project.Name, DocumentPaths(project), name, maxResults));
        }

        return results
            .OrderByDescending(declaration => declaration.Rank)
            .ThenBy(declaration => declaration.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>4 exact, 3 prefix, 1 contains — the ranking find_symbol applies to a source declaration too.</summary>
    public static int RankOf(string candidate, string requested) =>
        candidate.Equals(requested, StringComparison.OrdinalIgnoreCase) ? ExactMatch
        : candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ? PrefixMatch
        : ContainsMatch;

    /// <summary>The name a node declares, or null for a node that declares nothing.</summary>
    private static string? DeclarationName(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
        EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The declaration's own text, capped — the code block a generated symbol stands for.</summary>
    private static string? SourceOf(ISymbol symbol, int maxChars)
    {
        var node = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (node is null) return null;

        var text = node.ToString();
        return text.Length > maxChars ? text[..maxChars] : text;
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
