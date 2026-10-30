namespace Snitch.Search;

/// <summary>
/// The solution's full-text index: every source, document and decompiled file, one row per line, so a
/// question about its contents is one query instead of a directory walk and a series of file reads.
/// </summary>
public interface IFullTextIndexService
{
    /// <summary>
    /// Indexes the files under the solution (or <paramref name="path"/>) whose length or modification
    /// time changed, drops the documents that no longer exist, and answers with what it did.
    /// </summary>
    Task<object> BuildAsync(string? path = null, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full-text search: words, phrases, <c>-exclusions</c>, and raw FTS5 syntax when the query uses it.
    /// Answers with <c>file:line</c> and the matching line, plus the files that match most, so no file
    /// has to be opened to find out where something is.
    /// </summary>
    Task<object> SearchAsync(string query, string? glob = null, int limit = 40, int contextLines = 0, CancellationToken cancellationToken = default);

    /// <summary>What the index holds, and why it is empty when it is.</summary>
    object Status();

    /// <summary>The indexed documents, largest first.</summary>
    object Documents(string? kind = null, int limit = 200);

    /// <summary>Indexes one file that was just written (a decompiled type), without a full build.</summary>
    void IndexFile(string path, string kind = "decompiled");

    /// <summary>Indexes text that is about to be written, so a search right after sees it.</summary>
    void IndexText(string path, string kind, string text);
}
