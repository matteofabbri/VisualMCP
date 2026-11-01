using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Move to global usings" for a Visual Basic project: every plain <c>Imports X</c> moves to the
/// project file's project-level <c>&lt;Import Include="X" /&gt;</c> items and the per-file statement goes.
/// The gate compiles the edited project with the new global imports applied to its options, so an ambiguity
/// is caught before the project file is written.
/// </summary>
internal static partial class VbRefactoringTail
{
    public static async Task<object> GlobalUsingsAsync(IRoslynWorkspace workspace, Solution solution, Project project,
        bool dryRun, CancellationToken cancellationToken)
    {
        var projectPath = project.FilePath;
        if (projectPath is null) return new { error = $"Project '{project.Name}' has no file on disk." };

        var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var rootElement = document.Root;
        if (rootElement is null) return new { error = $"Could not read {Path.GetFileName(projectPath)}." };

        var projectNamespace = rootElement.Name.Namespace;
        var existing = rootElement.Descendants(projectNamespace + "Import")
            .Select(item => (string?)item.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);

        var moved = new SortedSet<string>(StringComparer.Ordinal);
        var edits = new List<(Document Document, string OldText, string NewText, int Removed)>();

        foreach (var file in project.Documents)
        {
            if (!file.FilePath?.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true) continue;

            var syntaxRoot = await file.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var text = await file.GetTextAsync(cancellationToken);
            if (syntaxRoot is null) continue;

            var statements = syntaxRoot.Imports
                .Where(statement => statement.ImportsClauses.Count == 1 &&
                                    statement.ImportsClauses[0] is SimpleImportsClauseSyntax { Alias: null })
                .ToList();

            if (statements.Count == 0) continue;

            foreach (var statement in statements)
                moved.Add(((SimpleImportsClauseSyntax)statement.ImportsClauses[0]).Name.ToString());

            var oldText = text.ToString();
            var newText = oldText;
            foreach (var statement in statements.OrderByDescending(candidate => candidate.SpanStart))
            {
                var span = WholeLines(text, statement.Span);
                newText = newText[..span.Start] + newText[(span.Start + span.Length)..];
            }

            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                edits.Add((file, oldText, newText, statements.Count));
        }

        if (moved.Count == 0)
            return new { error = $"'{project.Name}' has no plain Imports statement to move." };

        var added = moved.Where(name => !existing.Contains(name)).ToList();

        // ── Gate: the project with the edited files and the new global imports must compile ──
        var before = await project.GetCompilationAsync(cancellationToken);
        if (before is null) return new { error = $"Project '{project.Name}' produced no compilation." };
        var beforeErrors = before.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projectedSolution = project.Solution;
        foreach (var edit in edits)
            projectedSolution = projectedSolution.WithDocumentText(edit.Document.Id,
                Microsoft.CodeAnalysis.Text.SourceText.From(edit.NewText));

        var projected = projectedSolution.GetProject(project.Id)!;
        var options = projected.CompilationOptions as VisualBasicCompilationOptions;
        if (options is null) return new { error = $"Project '{project.Name}' is not a Visual Basic compilation." };

        var imports = options.GlobalImports.Select(import => import.Name)
            .Concat(moved)
            .Distinct(StringComparer.Ordinal)
            .Select(GlobalImport.Parse)
            .ToList();

        var afterCompilation = await projected.WithCompilationOptions(options.WithGlobalImports(imports))
            .GetCompilationAsync(cancellationToken);
        if (afterCompilation is null) return new { error = $"Project '{project.Name}' produced no projected compilation." };

        var afterErrors = afterCompilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        var countAdded = afterErrors.Count - beforeErrors;

        if (countAdded > 0)
            return new
            {
                error = $"Refused: moving the Imports to project level would add {countAdded} compiler error(s) " +
                        $"({beforeErrors} → {afterErrors.Count}). Usually two imported namespaces both define a used type.",
                errors = afterErrors.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}").Distinct().Take(8).ToList(),
            };

        if (dryRun)
            return new
            {
                @refactoring = "global_usings",
                project = project.Name,
                namespaces = moved.ToList(),
                addedToProject = added,
                filesChanged = edits.Count,
                dryRun = true,
                applied = false,
                files = edits.Select(edit => new
                {
                    file = edit.Document.FilePath,
                    removed = edit.Removed,
                    preview = RefactoringFileWriter.Diff(edit.OldText, edit.NewText),
                }).ToList(),
                note = "Preview only: pass dryRun=false to write the project file and the sources.",
            };

        // ── Write the project file and the sources ───────────────────────────────────────────
        foreach (var edit in edits)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, edit.Document, edit.NewText, cancellationToken);

        var importGroup = rootElement.Elements(projectNamespace + "ItemGroup")
            .FirstOrDefault(group => group.Elements(projectNamespace + "Import").Any());

        if (added.Count > 0)
        {
            if (importGroup is null)
            {
                importGroup = new XElement(projectNamespace + "ItemGroup");
                rootElement.Add(importGroup);
            }

            foreach (var name in added)
                importGroup.Add(new XElement(projectNamespace + "Import", new XAttribute("Include", name)));
            document.Save(projectPath, SaveOptions.DisableFormatting);
        }

        // The project's compilation options changed with the file: reload so the editing tools see them.
        if (solution.FilePath is not null)
            await workspace.LoadSolutionAsync(solution.FilePath, cancellationToken);

        return new
        {
            @refactoring = "global_usings",
            project = project.Name,
            namespaces = moved.ToList(),
            addedToProject = added,
            filesChanged = edits.Count,
            dryRun = false,
            applied = true,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                removed = edit.Removed,
                preview = RefactoringFileWriter.Diff(edit.OldText, edit.NewText),
            }).ToList(),
            note = "Project-level Imports written; the solution was reloaded so the semantic model sees them.",
        };
    }
}
