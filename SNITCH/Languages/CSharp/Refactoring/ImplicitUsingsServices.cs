using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's implicit-usings toggle: writes <c>&lt;ImplicitUsings&gt;enable|disable&lt;/ImplicitUsings&gt;</c> into
/// the project file. Disabling also writes the explicit <c>using</c> directives the files relied on, computed
/// from the semantic model and verified by compiling the project with the generated global-usings document
/// removed — the only way to see the difference the property will make.
/// </summary>
public interface IImplicitUsingsService
{
    Task<object> ToggleImplicitUsings(
        bool enabled,
        string? projectName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ImplicitUsingsService : IImplicitUsingsService
{
    /// <summary>The namespaces the .NET SDK imports implicitly for a console/library project.</summary>
    private static readonly string[] DefaultImplicitUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    private readonly IRoslynWorkspace _workspace;

    public ImplicitUsingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ToggleImplicitUsings(
        bool enabled,
        string? projectName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp).ToList();
        if (projectName is not null)
            projects = projects.Where(project => string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (projects.Count == 0)
            return new { error = projectName is null ? "The solution has no C# project." : $"No project named '{projectName}'." };
        if (projects.Count > 1)
            return new { error = "The solution has several projects; pass projectName.", projects = projects.Select(p => p.Name).ToList() };

        var project = projects[0];
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return new { error = $"The project file of '{project.Name}' is not on disk; cannot edit it." };

        var csproj = await File.ReadAllTextAsync(project.FilePath, cancellationToken);
        var updatedCsproj = SetProperty(csproj, "ImplicitUsings", enabled ? "enable" : "disable");
        if (updatedCsproj is null)
            return new { error = "Could not find a <PropertyGroup> in the project file." };

        if (enabled)
        {
            var onlyCsproj = !dryRun;
            if (!dryRun) await Platform.FileText.WriteAsync(project.FilePath, updatedCsproj, cancellationToken);

            return new
            {
                @refactoring = "implicit_usings",
                project = project.Name,
                @explicit = false,
                filesChanged = 0,
                dryRun,
                applied = onlyCsproj,
                projectFile = project.FilePath,
                preview = RefactoringFileWriter.Diff(csproj, updatedCsproj),
                note = "The property takes effect on the next load_solution / build; no source file needed a change.",
            };
        }

        // ── Disable: every file that used an implicit namespace needs it written out ─────
        var implicitNamespaces = await ReadImplicitNamespacesAsync(project, cancellationToken);
        var projection = solution;
        var generatedDocuments = new List<Document>();

        foreach (var document in project.Documents)
        {
            if (!IsGeneratedGlobalUsings(document)) continue;

            generatedDocuments.Add(document);
            projection = projection.RemoveDocument(document.Id);
        }

        var edits = new List<(Document Document, string NewText, string Preview)>();
        foreach (var document in project.Documents)
        {
            if (IsGeneratedGlobalUsings(document)) continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is not CompilationUnitSyntax unit || model is null) continue;

            var used = UsedImplicitNamespaces(unit, model, implicitNamespaces, cancellationToken);
            var missing = used
                .Where(name => !Imports(unit, name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (missing.Count == 0) continue;

            var updated = unit;
            foreach (var name in missing) updated = RefactoringNamespaces.AddUsing(updated, name);

            var newText = updated.ToFullString();
            edits.Add((document, newText, RefactoringFileWriter.Diff(unit.ToFullString(), newText)));
            projection = projection.WithDocumentText(document.Id, SourceText.From(newText));
        }

        // The gate is meaningful only when the generated import file is part of the project; without it the
        // projection compiles the same and the added usings are checked for "no new errors" anyway.
        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projection, new[] { project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "disabling implicit usings");

        if (!dryRun)
        {
            await Platform.FileText.WriteAsync(project.FilePath, updatedCsproj, cancellationToken);
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "implicit_usings",
            project = project.Name,
            @explicit = true,
            implicitNamespaces,
            filesChanged = edits.Count,
            generatedDocuments = generatedDocuments.Select(document => Path.GetFileName(document.FilePath)).ToList(),
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            projectFile = project.FilePath,
            preview = RefactoringFileWriter.Diff(csproj, updatedCsproj),
            files = edits.Select(edit => new { file = edit.Document.FilePath, preview = edit.Preview }).ToList(),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the project file and the files."
                : "Written. The property takes effect on the next load_solution / build.",
        };
    }

    /// <summary>The namespaces the project's generated import file declares, or the SDK default.</summary>
    private static async Task<IReadOnlyList<string>> ReadImplicitNamespacesAsync(Project project, CancellationToken cancellationToken)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var document in project.Documents.Where(IsGeneratedGlobalUsings))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            if (root is null) continue;

            foreach (var directive in root.Usings)
            {
                if (directive.Alias is not null) continue;
                if (directive.GlobalKeyword.Kind() == SyntaxKind.None) continue;
                if (directive.Name is null) continue;

                names.Add(directive.Name.ToString());
            }
        }

        return names.Count > 0 ? names.ToList() : DefaultImplicitUsings.ToList();
    }

    /// <summary>
    /// The implicit namespaces the file actually reads a symbol from. Resolving the identifier is what keeps
    /// this to the usings that are needed, instead of adding the whole set to every file.
    /// </summary>
    private static IReadOnlyCollection<string> UsedImplicitNamespaces(
        CompilationUnitSyntax root,
        SemanticModel model,
        IReadOnlyList<string> implicitNamespaces,
        CancellationToken cancellationToken)
    {
        var set = implicitNamespaces.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            var containing = symbol switch
            {
                ITypeSymbol type => type.ContainingNamespace,
                IMethodSymbol method => method.ContainingNamespace,
                IPropertySymbol property => property.ContainingNamespace,
                IFieldSymbol field => field.ContainingNamespace,
                IEventSymbol @event => @event.ContainingNamespace,
                _ => null,
            };

            if (containing is null || containing.IsGlobalNamespace) continue;

            var name = containing.ToDisplayString();
            if (set.Contains(name)) used.Add(name);
        }

        return used;
    }

    private static bool Imports(CompilationUnitSyntax unit, string name)
    {
        if (unit.Usings.Any(directive => directive.Alias is null && directive.Name?.ToString() == name)) return true;

        var fileNamespace = unit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        if (fileNamespace is null) return false;

        return string.Equals(fileNamespace, name, StringComparison.Ordinal) ||
               fileNamespace.StartsWith(name + ".", StringComparison.Ordinal) ||
               name.StartsWith(fileNamespace + ".", StringComparison.Ordinal);
    }

    internal static bool IsGeneratedGlobalUsings(Document document) =>
        document.Name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase) ||
        document.FilePath is not null && document.FilePath.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets or adds an MSBuild property, preserving the rest of the project file verbatim.</summary>
    internal static string? SetProperty(string csproj, string name, string value)
    {
        var existing = new Regex($@"<{name}>\s*[^<]*\s*</{name}>", RegexOptions.IgnoreCase);
        if (existing.IsMatch(csproj))
            return existing.Replace(csproj, $"<{name}>{value}</{name}>", 1);

        var group = Regex.Match(csproj, @"<PropertyGroup[^>]*>");
        if (group.Success)
            return csproj.Insert(group.Index + group.Length,
                Environment.NewLine + $"    <{name}>{value}</{name}>");

        var project = Regex.Match(csproj, @"<Project[^>]*>");
        if (project.Success)
            return csproj.Insert(project.Index + project.Length,
                $"{Environment.NewLine}  <PropertyGroup>{Environment.NewLine}    <{name}>{value}</{name}>{Environment.NewLine}  </PropertyGroup>");

        return null;
    }
}

/// <summary>
/// Rider's "Convert to non-global using": removes one <c>global using X;</c> and writes <c>using X;</c> into
/// the files that stop compiling without it. Compiler-driven, so only the files that actually need it change.
/// </summary>
public interface IConvertToNonGlobalUsingService
{
    Task<object> ConvertToNonGlobalUsing(
        string filePath,
        string usingNamespace,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConvertToNonGlobalUsingService : IConvertToNonGlobalUsingService
{
    private readonly IRoslynWorkspace _workspace;

    public ConvertToNonGlobalUsingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConvertToNonGlobalUsing(
        string filePath,
        string usingNamespace,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(usingNamespace)) return new { error = "The global using to convert is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root is null) return new { error = "Could not read the syntax tree." };

        var directive = root.Usings.FirstOrDefault(candidate =>
            candidate.GlobalKeyword.Kind() != SyntaxKind.None &&
            candidate.Alias is null &&
            string.Equals(candidate.Name?.ToString(), usingNamespace, StringComparison.Ordinal));

        if (directive is null)
            return new
            {
                error = $"{Path.GetFileName(path)} has no `global using {usingNamespace};`.",
                globalUsings = root.Usings
                    .Where(candidate => candidate.GlobalKeyword.Kind() != SyntaxKind.None)
                    .Select(candidate => candidate.Name?.ToString())
                    .Where(name => name is not null)
                    .ToList(),
            };

        var withoutDirective = root.WithUsings(root.Usings.Remove(directive)).ToFullString();

        // ── Which files stop compiling without it? ────────────────────────────────────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(withoutDirective));
        var project = projected.GetProject(document.Project.Id)!;
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null) return new { error = "The project produced no compilation." };

        var missing = compilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Where(diagnostic => diagnostic.Id is "CS0103" or "CS0246" or "CS0234")
            .Select(diagnostic => diagnostic.Location.SourceTree?.FilePath)
            .Where(file => file is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var edits = new List<(Document Document, string NewText)>();
        foreach (var file in missing)
        {
            var target = project.Documents.FirstOrDefault(candidate => candidate.FilePath is not null &&
                string.Equals(Path.GetFullPath(candidate.FilePath), Path.GetFullPath(file!), StringComparison.OrdinalIgnoreCase));
            if (target is null || target.Id == document.Id) continue;

            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            if (targetRoot is null) continue;

            var updated = RefactoringNamespaces.AddUsing(targetRoot, usingNamespace);
            edits.Add((target, updated.ToFullString()));
            projected = projected.WithDocumentText(target.Id, SourceText.From(updated.ToFullString()));
        }

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "converting the global using");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, withoutDirective, cancellationToken);
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "convert_to_non_global_using",
            globalUsing = usingNamespace,
            file = document.FilePath,
            filesChanged = edits.Count + 1,
            compilationErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = edits.Select(edit => new { file = edit.Document.FilePath }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }
}
