namespace Snitch.Storage.Schema;

/// <summary>The freshness stamp of one indexed document: what lets a build skip the files that did not change.</summary>
public sealed class TextIndexDocumentRow
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Length { get; set; }
    public long Modified { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int Lines { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>
/// The line numbers of one document, with the rowid each line was given in the FTS5 table — which is how a
/// document's lines are deleted exactly, since a virtual table does not report the id it assigned.
/// </summary>
public sealed class TextIndexLineRow
{
    public string Path { get; set; } = string.Empty;
    public int Line { get; set; }
    public long RowId { get; set; }
}
