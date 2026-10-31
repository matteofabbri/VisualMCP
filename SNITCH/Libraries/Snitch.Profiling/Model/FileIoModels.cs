namespace Snitch.Profiling.Model;

/// <summary>The kernel file operations the collector turns into counters.</summary>
public enum FileIoOperation
{
    Read,
    Write,
    Open,
    Close,
    Delete,
    Rename,
    Enumerate,
    Other,
}

/// <summary>
/// One file touched during the window, with the process that touched it and the bytes and
/// operations it cost. The shares are of the report's totals, so they can be compared directly.
/// </summary>
public sealed record FileIoFileEntry(
    string Path,
    int ProcessId,
    string ProcessName,
    long ReadBytes,
    long WriteBytes,
    long ReadOps,
    long WriteOps,
    long Opens,
    long Closes,
    long Deletes,
    long Renames,
    double ReadShare,
    double WriteShare,
    double AverageWriteBytes,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>The same totals rolled up per process: "who", before "where".</summary>
public sealed record FileIoProcessEntry(
    int ProcessId,
    string ProcessName,
    long ReadBytes,
    long WriteBytes,
    long ReadOps,
    long WriteOps,
    int Files,
    double ReadShare,
    double WriteShare);

/// <summary>
/// The answer to "who is writing where, and how": per-process and per-file read/write bytes and
/// operation counts over a measured window, plus the findings those numbers imply.
/// </summary>
public sealed record FileIoReport(
    string Target,
    double DurationMs,
    long EventCount,
    bool Truncated,
    long TotalReadBytes,
    long TotalWriteBytes,
    long TotalReadOps,
    long TotalWriteOps,
    int DistinctFiles,
    int DistinctProcesses,
    double ReadMegabytesPerSecond,
    double WriteMegabytesPerSecond,
    IReadOnlyList<FileIoFileEntry> Files,
    IReadOnlyList<FileIoProcessEntry> Processes,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> Warnings);
