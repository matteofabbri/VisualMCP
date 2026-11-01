using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Snitch.Workspace;

namespace Snitch.Platform;

/// <summary>
/// Rewrites the line endings of text files to one style. It is the explicit migration the rest of the
/// engine never does on its own: pick the scope, choose <c>auto</c> (each file's own majority), <c>lf</c>
/// or <c>crlf</c>, and every file that does not already comply is rewritten in one pass.
/// </summary>
public interface IFixLineEndingsService
{
    Task<FixLineEndingsResult> FixAsync(
        string scope,
        string? path,
        string? projectName,
        string? style,
        string? extensions,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class FixLineEndingsService : IFixLineEndingsService
{
    private static readonly string[] DefaultExtensions =
        [".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".md", ".json", ".yml", ".yaml", ".txt", ".xml", ".config"];

    private readonly IRoslynWorkspace _workspace;

    public FixLineEndingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<FixLineEndingsResult> FixAsync(
        string scope,
        string? path,
        string? projectName,
        string? style,
        string? extensions,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("file" or "directory" or "project" or "solution"))
            return new FixLineEndingsResult { Error = "scope must be file, directory, project or solution." };

        NewLineStyle? forced = null;
        if (!string.IsNullOrWhiteSpace(style) && !style.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!LineEndings.TryParseStyle(style, out var parsed))
                return new FixLineEndingsResult { Error = $"'{style}' is not a newline style. Use auto, lf or crlf." };
            forced = parsed;
        }

        var filter = ParseExtensions(extensions);
        var (files, selectionError) = await SelectFilesAsync(level, path, projectName, filter, cancellationToken);
        if (selectionError is not null) return selectionError;

        var changes = new List<LineEndingChange>();
        var skipped = new List<LineEndingSkip>();
        var alreadyFine = 0;

        foreach (var file in files)
        {
            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (Exception exception)
            {
                skipped.Add(new LineEndingSkip(file, exception.Message));
                continue;
            }

            // auto = the file's own majority, so a mixed file becomes consistent without moving to the
            // other convention; a file whose endings are already uniform is left alone.
            var target = forced ?? LineEndings.Detect(text, LineEndings.ForFile(file));
            var normalized = LineEndings.Normalize(text, target);
            if (string.Equals(normalized, text, StringComparison.Ordinal))
            {
                alreadyFine++;
                continue;
            }

            var (crlf, lf) = LineEndings.Count(text);
            changes.Add(new LineEndingChange(
                file,
                crlf > 0 && lf > 0 ? $"mixed (crlf {crlf}, lf {lf})" : LineEndings.Describe(crlf > 0 ? NewLineStyle.CrLf : NewLineStyle.Lf),
                LineEndings.Describe(target),
                crlf,
                lf));

            if (!dryRun)
            {
                // The explicit style, not the file's own: WriteAsync(path, text) would normalise back.
                await FileText.WriteAsync(file, normalized, target, cancellationToken);
                await UpdateModelAsync(file, normalized, cancellationToken);
            }
        }

        return new FixLineEndingsResult
        {
            Refactoring = "fix_line_endings",
            Scope = level,
            Style = forced is null ? "auto" : LineEndings.Describe(forced.Value),
            Host = HostSystem.Describe(),
            DryRun = dryRun,
            Applied = !dryRun,
            Scanned = files.Count,
            AlreadyCorrect = alreadyFine,
            FilesChanged = changes.Count,
            Changes = changes.Take(200).ToList(),
            Skipped = skipped.Take(20).ToList(),
            Note = dryRun
                ? "Preview only: pass dryRun=false to rewrite the files."
                : "Files rewritten.",
        };
    }

    // ── selection ────────────────────────────────────────────────────────────
    private async Task<(IReadOnlyList<string> Files, FixLineEndingsResult? Error)> SelectFilesAsync(
        string level, string? path, string? projectName, HashSet<string>? filter, CancellationToken cancellationToken)
    {
        switch (level)
        {
            case "file":
                {
                    if (string.IsNullOrWhiteSpace(path))
                        return ([], new FixLineEndingsResult { Error = "scope=file needs path." });

                    var full = Path.GetFullPath(path);
                    return File.Exists(full)
                        ? ([full], null)
                        : ([], new FixLineEndingsResult { Error = $"{full} does not exist." });
                }

            case "directory":
                {
                    var directory = string.IsNullOrWhiteSpace(path) ? DiscoverRoot() : Path.GetFullPath(path!);
                    if (directory is null)
                        return ([], new FixLineEndingsResult { Error = "scope=directory needs path (no solution is loaded)." });
                    if (!Directory.Exists(directory))
                        return ([], new FixLineEndingsResult { Error = $"{directory} does not exist." });

                    return (Enumerate(directory, filter), null);
                }

            case "project":
                {
                    var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
                    if (solution is null)
                        return ([], new FixLineEndingsResult { Error = "No C# solution could be auto-located. Call load_solution first." });
                    if (string.IsNullOrWhiteSpace(projectName))
                        return ([], new FixLineEndingsResult
                        {
                            Error = "scope=project needs projectName.",
                            Projects = solution.Projects.Select(p => p.Name).ToList(),
                        });

                    var project = solution.Projects.FirstOrDefault(candidate =>
                        candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                    if (project is null)
                        return ([], new FixLineEndingsResult
                        {
                            Error = $"No project '{projectName}'.",
                            Projects = solution.Projects.Select(p => p.Name).ToList(),
                        });

                    return (Documents(project, filter), null);
                }

            default:
                {
                    var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
                    if (solution is null)
                        return ([], new FixLineEndingsResult { Error = "No C# solution could be auto-located. Call load_solution first." });

                    return (solution.Projects.SelectMany(project => Documents(project, filter)).ToList(), null);
                }
        }
    }

    private static List<string> Documents(Project project, HashSet<string>? filter) =>
        project.Documents
            .Select(document => document.FilePath)
            .OfType<string>()
            .Where(file => filter is null || filter.Contains(Path.GetExtension(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static HashSet<string>? ParseExtensions(string? extensions)
    {
        if (string.IsNullOrWhiteSpace(extensions)) return null;

        return extensions
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Enumerate(string directory, HashSet<string>? filter)
    {
        var wanted = filter ?? DefaultExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(child);
                    if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
                    pending.Push(child);
                }

                files.AddRange(Directory.EnumerateFiles(current)
                    .Where(file => wanted.Contains(Path.GetExtension(file))));
            }
            catch (Exception)
            {
                // An unreadable folder is not a reason to fail the whole sweep.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private string? DiscoverRoot()
    {
        var solution = _workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath();
        return solution is null ? null : Path.GetDirectoryName(solution);
    }

    /// <summary>Keeps the in-memory solution in step, so a following tool does not restore the old text.</summary>
    private async Task UpdateModelAsync(string file, string text, CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var document = solution?.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), Path.GetFullPath(file),
                                                       HostSystem.PathComparison));

        if (document is null) return;

        var previous = await document.GetTextAsync(cancellationToken);
        _workspace.TryUpdateDocument(document.Id, SourceText.From(text, previous.Encoding ?? Encoding.UTF8));
    }
}
