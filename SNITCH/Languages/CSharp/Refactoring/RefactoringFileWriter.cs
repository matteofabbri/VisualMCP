using System.Text;
using Snitch.Platform;
using Snitch.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Writes a rewritten document back to disk, keeping the file's existing byte-order-mark convention
/// and its line endings instead of imposing new ones, and updates the in-memory solution so a
/// following refactoring does not rewrite the file from the old text.
/// </summary>
/// <remarks>
/// Public because the metacoding service (in <c>Snitch.Metacode</c>) writes generated code through the
/// same guarded path: the stale-file check and the line-ending normalisation must not have a second,
/// subtly different implementation.
/// </remarks>
public static class RefactoringFileWriter
{
    /// <summary>Writes the file and replaces the document's text in the active solution.</summary>
    /// <exception cref="InvalidOperationException">
    /// The file on disk no longer matches the modelled text: someone edited it after the solution was
    /// loaded, and writing would silently discard that edit.
    /// </exception>
    public static async Task WriteAndUpdateAsync(
        IRoslynWorkspace workspace, Document document, string newText, CancellationToken cancellationToken = default)
    {
        // Roslyn emits CRLF from ElasticCarriageReturnLineFeed whatever the file uses. Normalising to
        // the file's own convention is what stops a refactoring from turning one edited line into a
        // full-file diff — or, on Linux, from injecting CRLF into an LF repository.
        var text = document.FilePath is null ? newText : FileText.NormalizeFor(document.FilePath, newText);

        if (document.FilePath is not null)
        {
            var modelled = (await document.GetTextAsync(cancellationToken)).ToString();
            var onDisk = await File.ReadAllTextAsync(document.FilePath, cancellationToken);
            if (!string.Equals(modelled, onDisk, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(document.FilePath)} changed on disk after the solution was loaded; " +
                    "refusing to overwrite it. Call load_solution, then retry.");
            }

            await FileText.WriteAsync(document.FilePath, text, cancellationToken);
        }

        var previous = await document.GetTextAsync(cancellationToken);
        workspace.TryUpdateDocument(document.Id, SourceText.From(text, previous.Encoding ?? Encoding.UTF8));
    }

    /// <summary>
    /// Writes a file given only its path: the document is resolved from the active solution so the
    /// model is updated too. A file that is not part of the solution (a newly created one) is simply
    /// written — nothing to update.
    /// </summary>
    public static async Task WriteAndUpdateAsync(
        IRoslynWorkspace workspace, string filePath, string newText, CancellationToken cancellationToken = default)
    {
        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var document = solution?.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), Path.GetFullPath(filePath),
                                                       HostSystem.PathComparison));

        if (document is null)
        {
            await FileText.WriteAsync(filePath, newText, cancellationToken);
            return;
        }

        await WriteAndUpdateAsync(workspace, document, newText, cancellationToken);
    }

    public static Task WriteAsync(string path, string text, CancellationToken cancellationToken = default) =>
        FileText.WriteAsync(path, text, cancellationToken);

    /// <summary>Common prefix/suffix line diff, capped so one preview cannot flood a tool result.</summary>
    public static string Diff(string oldText, string newText, int maxLines = 12)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var removed = oldLines.Length - suffix - prefix;
        var added = newLines.Length - suffix - prefix;

        var builder = new StringBuilder();
        builder.Append("@@ line ").Append(prefix + 1).AppendLine(" @@");
        for (var i = 0; i < Math.Min(removed, maxLines); i++)
            builder.Append("- ").Append(oldLines[prefix + i].TrimEnd('\r')).AppendLine();
        for (var i = 0; i < Math.Min(added, maxLines); i++)
            builder.Append("+ ").Append(newLines[prefix + i].TrimEnd('\r')).AppendLine();
        if (removed > maxLines || added > maxLines)
            builder.Append("… (preview truncated)");

        return builder.ToString().TrimEnd('\n');
    }
}
