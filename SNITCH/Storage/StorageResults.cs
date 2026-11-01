namespace Snitch.Storage;

/// <summary>What <see cref="IEditHistoryService.RevertAsync"/> did, or why it refused.</summary>
/// <remarks>
/// Every member is nullable so a refusal leaves the success members unset; the MCP serializer drops a
/// null member, so the answer keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record RevertEditResult
{
    /// <summary>The edit that was undone.</summary>
    public long? Reverted { get; init; }

    /// <summary>The file that was restored.</summary>
    public string? File { get; init; }

    /// <summary>How many characters the restored text holds.</summary>
    public int? RestoredChars { get; init; }

    /// <summary>How many characters the edit had left in place.</summary>
    public int? DiscardedChars { get; init; }

    /// <summary>The tool that had made the edit.</summary>
    public string? UndoneTool { get; init; }

    /// <summary>The rules that had produced the edit.</summary>
    public string? UndoneRules { get; init; }

    /// <summary>When the undone edit had been applied.</summary>
    public string? AppliedAt { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>The edit the refusal is about.</summary>
    public long? EditId { get; init; }

    /// <summary>What to do about a refusal.</summary>
    public string? Hint { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="ILocalHistoryService.SnapshotAsync"/> captured.</summary>
public sealed record LocalHistorySnapshotResult
{
    /// <summary>The store the snapshots went into.</summary>
    public string? Database { get; init; }

    /// <summary>How many files were scanned.</summary>
    public int? Scanned { get; init; }

    /// <summary>How many snapshots were taken.</summary>
    public int? Captured { get; init; }

    /// <summary>How many files were already at their newest snapshot.</summary>
    public int? Unchanged { get; init; }

    /// <summary>How many files were skipped (unreadable, too large).</summary>
    public int? Skipped { get; init; }

    /// <summary>The snapshots that were taken.</summary>
    public IReadOnlyList<LocalHistorySnapshotEntry>? Files { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>One snapshot a capture took.</summary>
/// <param name="File">The absolute path of the file.</param>
/// <param name="Hash">The content hash of the snapshot.</param>
/// <param name="Chars">The text length of the snapshot.</param>
public sealed record LocalHistorySnapshotEntry(string File, string Hash, int Chars);

/// <summary>What <see cref="ILocalHistoryService.HistoryAsync"/> found for one file.</summary>
public sealed record LocalHistoryHistoryResult
{
    /// <summary>The absolute path of the file.</summary>
    public string? File { get; init; }

    /// <summary>Whether the file is on disk.</summary>
    public bool? Exists { get; init; }

    /// <summary>The file as it stands.</summary>
    public LocalHistoryCurrentState? Current { get; init; }

    /// <summary>How many snapshots were returned.</summary>
    public int? Returned { get; init; }

    /// <summary>The snapshots, newest first.</summary>
    public IReadOnlyList<LocalHistorySnapshotSummary>? Snapshots { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>The file as it stands, for a history comparison.</summary>
/// <param name="Hash">The content hash, or <c>null</c> when the file is gone.</param>
/// <param name="Chars">The text length, or <c>null</c> when the file is gone.</param>
public sealed record LocalHistoryCurrentState(string? Hash, int? Chars);

/// <summary>One snapshot in a file's history.</summary>
/// <param name="Id">The snapshot id.</param>
/// <param name="At">When it was captured, ISO-8601.</param>
/// <param name="Hash">The content hash.</param>
/// <param name="Chars">The text length.</param>
/// <param name="Origin">What produced it, e.g. <c>snapshot</c> or <c>before-restore#12</c>.</param>
public sealed record LocalHistorySnapshotSummary(long Id, string At, string Hash, int Chars, string? Origin);

/// <summary>What <see cref="ILocalHistoryService.DiffAsync"/> compared.</summary>
public sealed record LocalHistoryDiffResult
{
    /// <summary>The absolute path of the file.</summary>
    public string? File { get; init; }

    /// <summary>The older text length, when two snapshots were compared.</summary>
    public int? Left { get; init; }

    /// <summary>The newer text length, when two snapshots were compared.</summary>
    public int? Right { get; init; }

    /// <summary>What the diff is against: <c>current file</c> or <c>snapshot #n</c>.</summary>
    public string? Against { get; init; }

    /// <summary>The snapshot the diff starts from, against the file's current text.</summary>
    public LocalHistoryDiffSnapshot? Snapshot { get; init; }

    /// <summary>The unified diff, capped at 200 lines.</summary>
    public string? Diff { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>The snapshot a diff starts from.</summary>
/// <param name="Id">The snapshot id.</param>
/// <param name="At">When it was captured, ISO-8601.</param>
/// <param name="Chars">The text length of the snapshot.</param>
public sealed record LocalHistoryDiffSnapshot(long Id, string At, int Chars);

/// <summary>
/// Base of every answer <see cref="ILocalHistoryService.RestoreAsync"/> gives.
/// </summary>
/// <remarks>
/// <c>snapshot</c> is a number when the file already holds that text and an object when a restore ran,
/// so each shape has its own record; the declared type is this base and the serializer writes the
/// runtime type.
/// </remarks>
public record LocalHistoryRestoreResult
{
    /// <summary>The absolute path of the file.</summary>
    public string? File { get; init; }

    /// <summary>Whether the snapshots's text was written.</summary>
    public bool? Applied { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>The file already held that snapshot's text.</summary>
public sealed record LocalHistoryRestoreUnchangedResult : LocalHistoryRestoreResult
{
    /// <summary>The snapshot that was compared.</summary>
    public long? Snapshot { get; init; }
}

/// <summary>A restore was computed, and written unless it was a dry run.</summary>
public sealed record LocalHistoryRestoreAppliedResult : LocalHistoryRestoreResult
{
    /// <summary>The snapshot that was restored.</summary>
    public LocalHistoryRestoreSnapshot? Snapshot { get; init; }

    /// <summary>Whether the call was a preview.</summary>
    public bool? DryRun { get; init; }

    /// <summary>The unified diff between the file and the snapshot, capped at 200 lines.</summary>
    public string? Diff { get; init; }
}

/// <summary>The snapshot a restore wrote.</summary>
/// <param name="Id">The snapshot id.</param>
/// <param name="At">When it was captured, ISO-8601.</param>
public sealed record LocalHistoryRestoreSnapshot(long Id, string At);
