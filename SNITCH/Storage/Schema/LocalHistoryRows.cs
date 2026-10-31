namespace Snitch.Storage.Schema;

/// <summary>
/// One snapshot of a file's whole text, taken by <c>local_history</c>: the per-file history ReSharper
/// keeps independently of commits. The text is stored compressed and deduplicated on its hash, so a
/// file that is saved without changing keeps one row.
/// </summary>
public sealed class FileSnapshotRow
{
    public long Id { get; set; }
    public string File { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public int Chars { get; set; }
    public string? Origin { get; set; }
    public byte[]? Text { get; set; }
}
