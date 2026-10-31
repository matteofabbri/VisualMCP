namespace VisualMCP.Startup;

/// <summary>
/// What one memory-import pass found and did. Paths are absolute; an unset member is omitted by the
/// server's null-ignoring serializer.
/// </summary>
public sealed record MemoryImportReport
{
    /// <summary>False when the pass is switched off (<c>VISUALMCP_MEMORY_IMPORT=off</c>).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The file stores that hold notes for this project (empty when none does).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>The topics written to the board by this pass (a changed note supersedes the previous version).</summary>
    public IReadOnlyList<string> Imported { get; init; } = [];

    /// <summary>Notes whose body the board already held unchanged; they were left exactly as they were.</summary>
    public IReadOnlyList<string> AlreadyPresent { get; init; } = [];

    /// <summary>Notes too large to import; reported instead of read.</summary>
    public IReadOnlyList<string> Oversized { get; init; } = [];

    /// <summary>Why something was skipped, in plain sentences.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when another store holds notes for this project.</summary>
    public bool HasSources => Sources.Count > 0;

    /// <summary>True when this pass wrote something to the board.</summary>
    public bool ImportedAnything => Imported.Count > 0;
}
