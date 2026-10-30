namespace VisualMCP.Startup;

/// <summary>
/// What one memory-import pass found and did. Paths are absolute; an unset member is omitted by the
/// server's null-ignoring serializer.
/// </summary>
public sealed record MemoryImportReport
{
    /// <summary>False when the pass is switched off (<c>VISUALMCP_MEMORY_IMPORT=off</c>).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The stores that hold notes for this project (empty when none does).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>The client's memory directory the notes were carried to, or null when none was found.</summary>
    public string? Target { get; init; }

    /// <summary>The notes written into <see cref="Target"/> by this pass.</summary>
    public IReadOnlyList<string> Imported { get; init; } = [];

    /// <summary>Notes the client already had; they were left exactly as they were.</summary>
    public IReadOnlyList<string> AlreadyPresent { get; init; } = [];

    /// <summary>Notes too large to import; reported instead of copied.</summary>
    public IReadOnlyList<string> Oversized { get; init; } = [];

    /// <summary>Index lines appended to the client's <c>MEMORY.md</c>.</summary>
    public int IndexLinesAdded { get; init; }

    /// <summary>Why something was skipped, in plain sentences.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when another store holds notes for this project.</summary>
    public bool HasSources => Sources.Count > 0;

    /// <summary>True when this pass changed anything in the client's store.</summary>
    public bool ImportedAnything => Imported.Count > 0 || IndexLinesAdded > 0;
}
