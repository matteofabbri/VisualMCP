using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Nullable reference types as the compiler sees them: whether the context is enabled per project,
/// where the suppressions (<c>x!</c>) are, and which CS86xx warnings the nullable flow analysis raises.
/// A file full of <c>!</c> and a project with the feature off are the two ways nullability stops
/// protecting anything.
/// </summary>
public interface IAnalyzeNullabilityService
{
    Task<object> Analyze(string? projectName, int top, CancellationToken cancellationToken = default);
}

public sealed class AnalyzeNullabilityService : IAnalyzeNullabilityService
{
    private readonly IRoslynWorkspace _workspace;

    public AnalyzeNullabilityService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Analyze(
        string? projectName, int top, CancellationToken cancellationToken = default)
    {
        var limit = top <= 0 ? 20 : top;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = new List<object>();
        var files = new Dictionary<string, FileNullability>(StringComparer.OrdinalIgnoreCase);
        var totalSuppressions = 0;
        var totalWarnings = 0;

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            var options = project.CompilationOptions as CSharpCompilationOptions;
            var nullable = options?.NullableContextOptions.ToString() ?? "Unknown";

            var suppressions = 0;
            var directives = 0;

            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null) continue;

                var localSuppressions = root.DescendantNodes()
                    .Where(node => node.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                    .ToList();

                var localDirectives = root.DescendantNodes(descendIntoTrivia: true)
                    .Count(node => node.IsKind(SyntaxKind.NullableDirectiveTrivia));

                suppressions += localSuppressions.Count;
                directives += localDirectives;

                if (localSuppressions.Count == 0 && localDirectives == 0) continue;

                var file = document.FilePath ?? document.Name;
                var entry = files.TryGetValue(file, out var existing) ? existing : new FileNullability(file);
                entry.Suppressions += localSuppressions.Count;
                entry.Directives += localDirectives;
                entry.Lines.AddRange(localSuppressions
                    .Take(20)
                    .Select(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                files[file] = entry;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            var warnings = compilation?.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning &&
                                     diagnostic.Id.StartsWith("CS86", StringComparison.Ordinal))
                .ToList() ?? [];

            foreach (var warning in warnings)
            {
                var file = warning.Location.SourceTree?.FilePath ?? "(generated)";
                var entry = files.TryGetValue(file, out var existing) ? existing : new FileNullability(file);
                entry.Warnings++;
                files[file] = entry;
            }

            totalSuppressions += suppressions;
            totalWarnings += warnings.Count;

            projects.Add(new
            {
                project = project.Name,
                nullable,
                suppressions,
                nullableDirectives = directives,
                nullableWarnings = warnings.Count,
                byId = warnings.GroupBy(warning => warning.Id)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count()),
            });
        }

        return new
        {
            tool = "analyze_nullability",
            projectFilter = projectName ?? "all",
            projects,
            totalSuppressions,
            totalWarnings,
            files = files.Values
                .OrderByDescending(file => file.Suppressions + file.Warnings)
                .Take(limit)
                .Select(file => new
                {
                    @file = file.File,
                    suppressions = file.Suppressions,
                    warnings = file.Warnings,
                    directives = file.Directives,
                    lines = file.Lines.Distinct().Take(20).ToList(),
                })
                .ToList(),
            note = "`!` on a null-forgiving expression silences the analysis at that point; a project with " +
                   "nullable disabled gets no nullability diagnostics at all (the compile-time CS86xx warnings).",
        };
    }

    private sealed class FileNullability(string file)
    {
        public string File { get; } = file;
        public int Suppressions { get; set; }
        public int Warnings { get; set; }
        public int Directives { get; set; }
        public List<int> Lines { get; } = [];
    }
}

/// <summary>
/// Breaking changes between the working tree and a git revision, computed from the two public API
/// surfaces. Only files that differ are read, so it stays fast on a large repository; the comparison
/// is on signatures, so a body edit is not a breaking change and a changed return type is.
/// </summary>
public interface IFindBreakingChangesService
{
    Task<object> Find(string gitRef, int top, CancellationToken cancellationToken = default);
}

public sealed class FindBreakingChangesService : IFindBreakingChangesService
{
    private readonly IRoslynWorkspace _workspace;

    public FindBreakingChangesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Find(string gitRef, int top, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
            return new { error = "A git ref is required, e.g. 'main', 'HEAD~5' or a commit hash." };

        var limit = top <= 0 ? 100 : top;
        var solutionPath = _workspace.LoadedSolutionPath;
        if (string.IsNullOrWhiteSpace(solutionPath))
            return new { error = "No solution is loaded; the repository cannot be located." };

        var repository = FindRepository(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);
        if (repository is null)
            return new { error = "The loaded solution is not inside a git repository." };

        var (exitCode, _, stdout, stderr, _) = await Snitch.Execution.ProcessRunner.RunAsync(
            "git", $"diff --name-status \"{gitRef}\" -- \"*.cs\"", repository, 120);

        if (exitCode != 0)
            return new { error = $"git diff failed: {stderr.Trim()}", fromRef = gitRef };

        var breaking = new List<string>();
        var added = new List<string>();
        var changed = new List<string>();
        var filesCompared = 0;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var status = parts[0];
            var isRename = status.StartsWith("R", StringComparison.Ordinal);
            var oldPath = parts[1];
            var newPath = isRename && parts.Length >= 3 ? parts[2] : parts[1];

            var before = status.StartsWith("A", StringComparison.Ordinal)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ApiSurface(await ShowAsync(repository, gitRef, oldPath));

            var after = status.StartsWith("D", StringComparison.Ordinal)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ApiSurface(ReadOrEmpty(Path.Combine(repository, newPath)));

            filesCompared++;

            var (removedKeys, addedKeys, changedKeys) = CompareSurfaces(before, after);
            breaking.AddRange(removedKeys.Select(key => $"{newPath}: removed {key}"));
            changed.AddRange(changedKeys.Select(key => $"{newPath}: changed {key}"));
            added.AddRange(addedKeys.Select(key => $"{newPath}: added {key}"));
        }

        return new
        {
            tool = "find_breaking_changes",
            fromRef = gitRef,
            repository,
            filesCompared,
            breakingCount = breaking.Count + changed.Count,
            removedCount = breaking.Count,
            changedCount = changed.Count,
            addedCount = added.Count,
            breaking = breaking.Take(limit).ToList(),
            changed = changed.Take(limit).ToList(),
            added = added.Take(limit).ToList(),
            note = "A removed or re-signed public member is breaking; an added one is not. Renames read as " +
                   "remove + add. Only files that differ between the ref and the working tree are compared.",
        };
    }

    private static async Task<string> ShowAsync(string repository, string gitRef, string path)
    {
        var (exitCode, _, stdout, _, _) = await Snitch.Execution.ProcessRunner.RunAsync(
            "git", $"show \"{gitRef}:{path.Replace('\\', '/')}\"", repository, 60);

        return exitCode == 0 ? stdout : string.Empty;
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    private static string? FindRepository(string? directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
                return current;
        }

        return null;
    }

    // ── the two API surfaces ─────────────────────────────────────────────────

    /// <summary>
    /// The public and protected API of one source text, keyed by a stable identity: a type by name, a
    /// member by its type, kind, name and parameter types. The value is the signature, so a changed
    /// return type is visible while a changed body is not.
    /// </summary>
    public static Dictionary<string, string> ApiSurface(string source)
    {
        var surface = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(source)) return surface;

        var root = SyntaxFactory.ParseCompilationUnit(source, options: new CSharpParseOptions(LanguageVersion.Preview));

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!IsVisible(type.Modifiers)) continue;

            var scope = ScopeOf(type);
            var typeName = scope + type.Identifier.ValueText;
            surface[$"T:{typeName}"] = TypeHeader(type);

            if (type is not TypeDeclarationSyntax declaration) continue;

            foreach (var member in declaration.Members)
            {
                var isInterfaceMember = type is InterfaceDeclarationSyntax;
                if (!isInterfaceMember && !IsVisible(member.Modifiers)) continue;

                AddMember(surface, typeName, member);
            }
        }

        foreach (var @delegate in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (!IsVisible(@delegate.Modifiers)) continue;
            surface[$"T:{ScopeOf(@delegate)}{@delegate.Identifier.ValueText}"] =
                "delegate(" + Parameters(@delegate.ParameterList) + "):" + Collapse(@delegate.ReturnType.ToString());
        }

        return surface;
    }

    private static void AddMember(Dictionary<string, string> surface, string typeName, MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                surface[$"{typeName}::{method.Identifier.ValueText}({Parameters(method.ParameterList)})"] =
                    Collapse(method.ReturnType.ToString()) + TypeParameters(method.TypeParameterList);
                break;

            case ConstructorDeclarationSyntax constructor:
                surface[$"{typeName}::ctor({Parameters(constructor.ParameterList)})"] = "ctor";
                break;

            case PropertyDeclarationSyntax property:
                surface[$"{typeName}::P:{property.Identifier.ValueText}"] =
                    Collapse(property.Type.ToString()) + Accessors(property.AccessorList);
                break;

            case IndexerDeclarationSyntax indexer:
                surface[$"{typeName}::I({Parameters(indexer.ParameterList)})"] =
                    Collapse(indexer.Type.ToString()) + Accessors(indexer.AccessorList);
                break;

            case EventFieldDeclarationSyntax @event:
                foreach (var variable in @event.Declaration.Variables)
                    surface[$"{typeName}::E:{variable.Identifier.ValueText}"] = Collapse(@event.Declaration.Type.ToString());
                break;

            case EventDeclarationSyntax namedEvent:
                surface[$"{typeName}::E:{namedEvent.Identifier.ValueText}"] = Collapse(namedEvent.Type.ToString());
                break;

            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    surface[$"{typeName}::F:{variable.Identifier.ValueText}"] = Collapse(field.Declaration.Type.ToString());
                break;
        }
    }

    /// <summary>What disappeared or changed; both are breaking, and the caller keeps them apart.</summary>
    public static (List<string> Removed, List<string> Added, List<string> Changed) CompareSurfaces(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var removed = before.Keys.Except(after.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        var changed = before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
            .Where(key => !string.Equals(before[key], after[key], StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => $"{key} (was {before[key]}, now {after[key]})")
            .ToList();

        return (removed, added, changed);
    }

    private static bool IsVisible(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword);

    private static string ScopeOf(SyntaxNode node)
    {
        var names = new List<string>();
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case BaseNamespaceDeclarationSyntax @namespace:
                    names.Insert(0, @namespace.Name.ToString());
                    break;
                case BaseTypeDeclarationSyntax type:
                    names.Insert(0, type.Identifier.ValueText);
                    break;
            }
        }

        return names.Count == 0 ? string.Empty : string.Join(".", names) + ".";
    }

    private static string TypeHeader(BaseTypeDeclarationSyntax type)
    {
        var keyword = type switch
        {
            RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                ? "record struct" : "record",
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            EnumDeclarationSyntax => "enum",
            _ => "type",
        };

        var bases = type.BaseList is null ? string.Empty : " : " + Collapse(type.BaseList.ToString());
        var typeParameters = type is TypeDeclarationSyntax declaration ? TypeParameters(declaration.TypeParameterList) : string.Empty;
        return keyword + typeParameters + bases;
    }

    private static string Accessors(AccessorListSyntax? accessors) =>
        accessors is null
            ? string.Empty
            : "{" + string.Join(",", accessors.Accessors.Select(accessor => accessor.Keyword.ValueText)
                .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)) + "}";

    private static string Parameters(BaseParameterListSyntax? parameters) =>
        parameters is null
            ? string.Empty
            : string.Join(",", parameters.Parameters.Select(parameter =>
                Collapse(parameter.Type?.ToString() ?? "?")));

    private static string TypeParameters(TypeParameterListSyntax? parameters) =>
        parameters is null ? string.Empty : Collapse(parameters.ToString());

    private static string Collapse(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
}
