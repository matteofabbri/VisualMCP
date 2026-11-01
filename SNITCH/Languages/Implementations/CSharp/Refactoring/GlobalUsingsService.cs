using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move to global usings": every plain <c>using X;</c> of a project becomes one
/// <c>global using X;</c> in a single file, and the per-file directives are removed. Gated on the
/// compiler: the operation is refused when it would introduce errors (typically an ambiguous
/// reference caused by globally importing two namespaces that both define a used type).
/// </summary>
public interface IGlobalUsingsService
{
    Task<object> MoveToGlobalUsings(
        string? projectName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class GlobalUsingsService : IGlobalUsingsService
{
    private readonly IRoslynWorkspace _workspace;

    public GlobalUsingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveToGlobalUsings(
        string? projectName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp).ToList();
        var project = projectName is null
            ? projects.Count == 1 ? projects[0] : null
            : projects.FirstOrDefault(candidate => candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (project is null)
            return new
            {
                error = projectName is null
                    ? "The solution has several projects: pass projectName."
                    : $"Project '{projectName}' not found.",
                projects = projects.Select(candidate => candidate.Name).ToList(),
            };

        var projectDirectory = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        if (projectDirectory is null) return new { error = $"Project '{project.Name}' has no directory on disk." };

        var globalFilePath = Path.Combine(projectDirectory, "GlobalUsings.cs");
        if (File.Exists(globalFilePath) && !project.Documents.Any(d => string.Equals(d.FilePath, globalFilePath, StringComparison.OrdinalIgnoreCase)))
            return new { error = $"{globalFilePath} exists on disk but is not part of the model; add it to the project or remove it first." };

        // ── Collect the directives that can become global ─────────────────────────────────
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        var edits = new List<(Document Document, string NewText, int Removed)>();

        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            if (root is null) continue;

            var candidates = root.Usings
                .Concat(root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(ns => ns.Usings))
                .Where(directive => directive.Alias is null
                                    && directive.StaticKeyword.IsKind(SyntaxKind.None)
                                    && !directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)
                                    && directive.Name is not null)
                .ToList();

            if (candidates.Count == 0) continue;

            foreach (var directive in candidates) namespaces.Add(directive.Name!.ToString());

            var newRoot = root.RemoveNodes(candidates, SyntaxRemoveOptions.KeepNoTrivia)
                          ?? root;

            // Drop the blank line a removed using block leaves behind at the top of the file.
            var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
            var newText = Tidy(newRoot.ToFullString());
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((document, newText, candidates.Count));
        }

        if (namespaces.Count == 0) return new { error = $"'{project.Name}' has no plain using directives to move." };

        var globalText = string.Join(Environment.NewLine,
            new[] { "// Namespaces imported project-wide (moved here from individual files).", "" }
                .Concat(namespaces.Select(ns => $"global using {ns};"))
                .Append(string.Empty));

        // ── Gate: the project must still compile, with no new errors ──────────────────────
        var before = (await project.GetCompilationAsync(cancellationToken))!.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projected = project;
        foreach (var edit in edits)
            projected = projected.Solution.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText))
                .GetProject(project.Id)!;

        var globalDocument = projected.AddDocument("GlobalUsings.cs", SourceText.From(globalText), filePath: globalFilePath);
        var afterCompilation = await globalDocument.Project.GetCompilationAsync(cancellationToken);
        var newErrors = (afterCompilation!.GetDiagnostics(cancellationToken))
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        var added = newErrors.Count - before;

        if (added > 0)
            return new
            {
                error = $"Refused: moving the usings to a global file would add {added} compiler error(s) " +
                        $"({before} → {newErrors.Count}). Usually two imported namespaces both define a used type.",
                errors = newErrors.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}").Distinct().Take(8).ToList(),
            };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);

            await Platform.FileText.WriteAsync(globalFilePath, globalText, cancellationToken);
            _workspace.TryAddDocument(project.Id, "GlobalUsings.cs", globalFilePath, SourceText.From(globalText));
        }

        return new
        {
            @refactoring = "global_usings",
            project = project.Name,
            globalFile = globalFilePath,
            namespacesMoved = namespaces.Count,
            filesCleaned = edits.Count,
            directivesRemoved = edits.Sum(edit => edit.Removed),
            compilerErrors = new { before, after = newErrors.Count },
            dryRun,
            applied = !dryRun,
            preview = string.Join(Environment.NewLine, globalText.Split(Environment.NewLine).Take(6)),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>Drops the blank lines a removed using block leaves at the top of a file.</summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        return string.Join("\n", lines);
    }
}
