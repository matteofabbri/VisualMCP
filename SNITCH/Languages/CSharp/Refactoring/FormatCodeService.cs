using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The scope selection shared by the tools that operate on a document, a project or the solution.
/// Kept in one place so "document" means the same file for every tool that accepts it, and defined
/// over both languages a .NET solution can hold: C# and Visual Basic documents are in scope unless
/// the caller asks for one of them.
/// </summary>
internal static class DocumentScopes
{
    /// <summary>The languages the Roslyn formatter and the scope-shaped tools operate on.</summary>
    public static readonly string[] SupportedLanguages = [LanguageNames.CSharp, LanguageNames.VisualBasic];

    public static (List<Document> Documents, object? Error) Select(
        Solution solution, string scope, string? filePath, string? projectName, string[]? languages = null)
    {
        var accepted = languages ?? SupportedLanguages;

        IEnumerable<Document> documents = solution.Projects
            .Where(project => accepted.Contains(project.Language, StringComparer.Ordinal))
            .SelectMany(project => project.Documents)
            .Where(document => document.SourceCodeKind == SourceCodeKind.Regular && document.FilePath is not null);

        switch (scope)
        {
            case "document":
                if (string.IsNullOrWhiteSpace(filePath))
                    return ([], new { error = "scope=document needs filePath." });

                var path = Path.GetFullPath(filePath);
                documents = documents.Where(document =>
                    string.Equals(Path.GetFullPath(document.FilePath!), path, StringComparison.OrdinalIgnoreCase));

                if (!documents.Any())
                    return ([], new { error = $"{path} is not a document of the loaded solution." });
                break;

            case "project":
                if (string.IsNullOrWhiteSpace(projectName))
                    return ([], new { error = "scope=project needs projectName." });

                documents = documents.Where(document =>
                    string.Equals(document.Project.Name, projectName, StringComparison.OrdinalIgnoreCase));

                if (!documents.Any())
                    return ([], new { error = $"No project named '{projectName}'." });
                break;
        }

        return (documents.ToList(), null);
    }
}

/// <summary>
/// ReSharper's "Reformat Code" as a tool of its own. The style engine used the Roslyn formatter only
/// through the opt-in IDE0055 rule; this applies it directly to a document, a project or the whole
/// solution, using the project's own options (including <c>.editorconfig</c>), and keeps each file's
/// line-ending convention. Roslyn's formatter is language-aware, so the same call formats a
/// <c>.vb</c> document with VB's own rules.
/// </summary>
public interface IFormatCodeService
{
    Task<object> Format(
        string scope,
        string? filePath,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class FormatCodeService : IFormatCodeService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public FormatCodeService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> Format(
        string scope,
        string? filePath,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory. Call load_solution first." };

        var (documents, selectionError) = DocumentScopes.Select(solution, level, filePath, projectName);
        if (selectionError is not null) return selectionError;

        var files = new List<string>();

        foreach (var document in documents)
        {
            var before = (await document.GetTextAsync(cancellationToken)).ToString();
            var formatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
            var after = (await formatted.GetTextAsync(cancellationToken)).ToString();

            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            files.Add(document.FilePath ?? document.Name);
            if (dryRun) continue;

            _store?.RecordEdit("format_code", document.FilePath ?? document.Name, null, before, after);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, after, cancellationToken);
        }

        return new
        {
            tool = "format_code",
            scope = level,
            dryRun,
            applied = !dryRun,
            filesModified = files.Count,
            files,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }
}
