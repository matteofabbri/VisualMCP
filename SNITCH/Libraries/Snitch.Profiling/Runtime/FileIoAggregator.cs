using System.Globalization;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Runtime;

/// <summary>
/// Turns the file operations the kernel provider delivers into per-file and per-process sums.
/// The provider reports every operation on the machine, so the process filter and the path
/// substring are applied here, before anything is stored: a filtered event costs no memory.
///
/// This type is deliberately free of ETW: the collector feeds it, and a test can feed it too.
/// </summary>
public sealed class FileIoAggregator
{
    public const long DefaultMaxEvents = 5_000_000;

    private readonly int? _processFilter;
    private readonly string? _pathFilter;
    private readonly long _maxEvents;
    private readonly Dictionary<(int ProcessId, string Path), FileAccumulator> _files = new();
    private readonly Dictionary<int, ProcessAccumulator> _processes = new();

    public FileIoAggregator(int? processFilter = null, string? pathFilter = null, long maxEvents = DefaultMaxEvents)
    {
        _processFilter = processFilter;
        _pathFilter = string.IsNullOrWhiteSpace(pathFilter) ? null : pathFilter.Trim();
        _maxEvents = Math.Max(1, maxEvents);
    }

    /// <summary>Events that passed the filters and were aggregated.</summary>
    public long Events { get; private set; }

    /// <summary>True when the event budget was exhausted; the report is then a prefix of the window.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The processes that produced at least one counted event.</summary>
    public IReadOnlyCollection<int> ProcessIds => _processes.Keys;

    public void Add(FileIoOperation operation, int processId, string? path, long bytes = 0)
    {
        if (_processFilter is not null && processId != _processFilter)
            return;

        var filePath = Normalize(path);
        if (_pathFilter is not null && !filePath.Contains(_pathFilter, StringComparison.OrdinalIgnoreCase))
            return;

        Events++;
        if (Events > _maxEvents)
        {
            Truncated = true;
            return;
        }

        var now = DateTime.UtcNow;
        if (!_processes.TryGetValue(processId, out var process))
        {
            process = new ProcessAccumulator { ProcessId = processId, FirstSeenUtc = now };
            _processes[processId] = process;
        }

        process.LastSeenUtc = now;

        if (!_files.TryGetValue((processId, filePath), out var file))
        {
            file = new FileAccumulator { Path = filePath, ProcessId = processId, FirstSeenUtc = now };
            _files[(processId, filePath)] = file;
        }

        file.LastSeenUtc = now;
        file.Operations++;

        var size = Math.Max(0, bytes);
        switch (operation)
        {
            case FileIoOperation.Read:
                file.ReadBytes += size;
                file.ReadOps++;
                process.ReadBytes += size;
                process.ReadOps++;
                break;

            case FileIoOperation.Write:
                file.WriteBytes += size;
                file.WriteOps++;
                process.WriteBytes += size;
                process.WriteOps++;
                break;

            case FileIoOperation.Open:
                file.Opens++;
                process.Opens++;
                break;

            case FileIoOperation.Close:
                file.Closes++;
                break;

            case FileIoOperation.Delete:
                file.Deletes++;
                break;

            case FileIoOperation.Rename:
                file.Renames++;
                break;

            case FileIoOperation.Enumerate:
                file.Enumerations++;
                break;

            default:
                break;
        }
    }

    public FileIoReport Build(
        string target,
        double durationMs,
        int top,
        Func<int, string?>? nameResolver = null,
        IReadOnlyList<string>? warnings = null)
    {
        top = Math.Clamp(top <= 0 ? 25 : top, 1, 200);
        var totalReadBytes = _files.Values.Sum(f => f.ReadBytes);
        var totalWriteBytes = _files.Values.Sum(f => f.WriteBytes);
        var totalReadOps = _files.Values.Sum(f => f.ReadOps);
        var totalWriteOps = _files.Values.Sum(f => f.WriteOps);
        var seconds = Math.Max(0.001, durationMs / 1000.0);

        string Name(int processId) => nameResolver?.Invoke(processId) ?? $"pid {processId}";

        var files = _files.Values
            .Select(f => new FileIoFileEntry(
                f.Path,
                f.ProcessId,
                Name(f.ProcessId),
                f.ReadBytes,
                f.WriteBytes,
                f.ReadOps,
                f.WriteOps,
                f.Opens,
                f.Closes,
                f.Deletes,
                f.Renames,
                Share(f.ReadBytes, totalReadBytes),
                Share(f.WriteBytes, totalWriteBytes),
                f.WriteOps == 0 ? 0 : Math.Round((double)f.WriteBytes / f.WriteOps, 1),
                f.FirstSeenUtc,
                f.LastSeenUtc))
            .OrderByDescending(f => f.WriteBytes)
            .ThenByDescending(f => f.ReadBytes)
            .Take(top)
            .ToList();

        var filesPerProcess = _files.Values
            .GroupBy(f => f.ProcessId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var processes = _processes.Values
            .Select(p => new FileIoProcessEntry(
                p.ProcessId,
                Name(p.ProcessId),
                p.ReadBytes,
                p.WriteBytes,
                p.ReadOps,
                p.WriteOps,
                filesPerProcess.TryGetValue(p.ProcessId, out var count) ? count : 0,
                Share(p.ReadBytes, totalReadBytes),
                Share(p.WriteBytes, totalWriteBytes)))
            .OrderByDescending(p => p.WriteBytes)
            .ThenByDescending(p => p.ReadBytes)
            .Take(top)
            .ToList();

        var reportWarnings = new List<string>(warnings ?? []);
        if (Truncated)
            reportWarnings.Add(FormattableString.Invariant(
                $"More than {_maxEvents:N0} file events were observed; the report covers the first {_maxEvents:N0}. Shorten the window or filter by process to see the whole picture."));

        var unknownOperations = _files.Values.Where(f => f.Path == UnknownPath).Sum(f => f.Operations);
        if (unknownOperations > 0)
            reportWarnings.Add(FormattableString.Invariant(
                $"{unknownOperations:N0} operation(s) had no resolved path: the file was opened before the capture started. The process totals are correct; the per-file split for those operations is not."));

        if (Events == 0)
            reportWarnings.Add(
                "No file I/O was observed in the window. The process may have been idle, the path filter may be too narrow, " +
                "or the kernel provider delivered no events.");

        if (_processFilter is null && _processes.Count > 1)
            reportWarnings.Add(
                "Captured system-wide. Pass processId or processName to attribute the I/O to one application.");

        if (_pathFilter is not null)
            reportWarnings.Add($"Only paths containing '{_pathFilter}' were counted.");

        var findings = BuildFindings(
            _files.Values, totalWriteBytes, totalReadBytes, unknownOperations, durationMs);

        return new FileIoReport(
            target,
            Math.Round(durationMs, 1),
            Events,
            Truncated,
            totalReadBytes,
            totalWriteBytes,
            totalReadOps,
            totalWriteOps,
            _files.Count,
            _processes.Count,
            Math.Round(totalReadBytes / 1024.0 / 1024.0 / seconds, 2),
            Math.Round(totalWriteBytes / 1024.0 / 1024.0 / seconds, 2),
            files,
            processes,
            findings,
            reportWarnings);
    }

    private List<ProfilingFinding> BuildFindings(
        IEnumerable<FileAccumulator> files,
        long totalWriteBytes,
        long totalReadBytes,
        long unknownOperations,
        double durationMs)
    {
        var findings = new List<ProfilingFinding>();
        var all = files.ToList();

        var topWrite = all.Where(f => f.WriteBytes > 0).OrderByDescending(f => f.WriteBytes).FirstOrDefault();
        if (topWrite is not null && totalWriteBytes > 0)
        {
            var share = Share(topWrite.WriteBytes, totalWriteBytes);
            if (share >= 40 && topWrite.WriteBytes >= 10L * 1024 * 1024)
            {
                findings.Add(new ProfilingFinding(
                    "file-write-hotspot",
                    share >= 70 ? "critical" : "warning",
                    $"Writes concentrate on {ShortName(topWrite.Path)}",
                    FormattableString.Invariant(
                        $"{FormatBytes(topWrite.WriteBytes)} ({share:0.0}% of all writes) in {topWrite.WriteOps:N0} operations by process {topWrite.ProcessId}."),
                    "Find the call that writes this file. If it is a log, rate-limit or batch it; if it is data, " +
                    "buffer the writes instead of flushing a small record per call.",
                    "Open the writer for this path; profile_allocations shows who allocates it.",
                    topWrite.Path));
            }
        }

        var storm = all
            .Where(f => f.WriteOps >= 1000 && f.WriteBytes > 0)
            .OrderByDescending(f => f.WriteOps)
            .FirstOrDefault(f => (double)f.WriteBytes / f.WriteOps < 4096);
        if (storm is not null)
        {
            findings.Add(new ProfilingFinding(
                "small-write-storm",
                "warning",
                $"Many small writes to {ShortName(storm.Path)}",
                FormattableString.Invariant(
                    $"{storm.WriteOps:N0} writes averaging {storm.WriteBytes / (double)storm.WriteOps:0} bytes each ({FormatBytes(storm.WriteBytes)} total)."),
                "Each small write is a syscall and a metadata touch. Use a buffered stream (or a larger buffer) and flush " +
                "on a batch boundary instead of per record.",
                "Look for FileStream/StreamWriter without a buffer in the writing code path.",
                storm.Path));
        }

        var churn = all.Where(f => f.Opens >= 1000).OrderByDescending(f => f.Opens).FirstOrDefault();
        if (churn is not null)
        {
            findings.Add(new ProfilingFinding(
                "handle-churn",
                "warning",
                $"Open/close churn on {ShortName(churn.Path)}",
                FormattableString.Invariant(
                    $"{churn.Opens:N0} opens and {churn.Closes:N0} closes in the window, for {FormatBytes(churn.WriteBytes)} written."),
                "Keep one handle open for the life of the component instead of opening the file per operation; " +
                "a per-call File.Open costs a create, a security check and a close every time.",
                "Find the per-call File.Open/File.AppendAllText on this path.",
                churn.Path));
        }

        if (totalWriteBytes >= 100L * 1024 * 1024)
        {
            findings.Add(new ProfilingFinding(
                "write-volume",
                "info",
                "The window wrote a large volume",
                FormattableString.Invariant(
                    $"{FormatBytes(totalWriteBytes)} written and {FormatBytes(totalReadBytes)} read by {_processes.Count} process(es) over {durationMs / 1000.0:0.0} s."),
                "Confirm the volume is expected. If it comes from logs or a cache, consider sampling or an in-memory buffer " +
                "with a periodic flush.",
                "profile_file_io with a pathFilter on the hot directory to isolate the writer.",
                null));
        }

        var temp = all
            .Where(f => IsTempPath(f.Path) && f.WriteBytes > 0)
            .Sum(f => f.WriteBytes);
        if (temp >= 10L * 1024 * 1024 && Share(temp, totalWriteBytes) >= 25)
        {
            findings.Add(new ProfilingFinding(
                "temp-writes",
                "info",
                "A large share of writes goes to temp",
                FormattableString.Invariant(
                    $"{FormatBytes(temp)} ({Share(temp, totalWriteBytes):0.0}% of all writes) is written under the temporary directory."),
                "Temp files are still disk I/O and are usually a staging pattern. Keep the bytes in memory, or write the " +
                "final file directly when the data is already complete.",
                "Find the Path.GetTempFileName / CreateTempFile call.",
                null));
        }

        if (unknownOperations > 0 && Events > 0 && Share(unknownOperations, Events) >= 50)
        {
            findings.Add(new ProfilingFinding(
                "unresolved-paths",
                "info",
                "Most operations have no resolved path",
                FormattableString.Invariant(
                    $"{unknownOperations:N0} of {Events:N0} operations could not be mapped to a file name."),
                "Start the capture before the process opens its files: FileIOInit only reports the name of a file whose " +
                "create event is inside the window.",
                "Start the capture first, then drive the workload.",
                null));
        }

        if (Events == 0)
        {
            findings.Add(new ProfilingFinding(
                "no-file-io",
                "info",
                "No file I/O was observed",
                "The kernel provider delivered no read or write events that passed the filters.",
                "Make sure the workload was running during the capture, and that the path filter matches the files it touches.",
                "profile_file_io without pathFilter and with processId set.",
                null));
        }

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    private const string UnknownPath = "(unknown)";

    private static string Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? UnknownPath : path.Trim();

    private static double Share(long value, long total) =>
        total <= 0 ? 0 : Math.Round(value * 100.0 / total, 2);

    private static bool IsTempPath(string path) =>
        path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/tmp/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase);

    private static string ShortName(string path)
    {
        // Kernel file names are Windows paths even when the aggregator is exercised in a test on
        // another OS, where Path.GetFileName would not treat '\' as a separator.
        var name = path.Split('\\', '/').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0 / 1024 / 1024:0.0} GB"),
        >= 1024L * 1024 => FormattableString.Invariant($"{bytes / 1024.0 / 1024:0.0} MB"),
        >= 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.0} KB"),
        _ => $"{bytes} B",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1,
    };

    private sealed class FileAccumulator
    {
        public string Path { get; init; } = "";
        public int ProcessId { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public long Operations { get; set; }
        public long ReadBytes { get; set; }
        public long WriteBytes { get; set; }
        public long ReadOps { get; set; }
        public long WriteOps { get; set; }
        public long Opens { get; set; }
        public long Closes { get; set; }
        public long Deletes { get; set; }
        public long Renames { get; set; }
        public long Enumerations { get; set; }
    }

    private sealed class ProcessAccumulator
    {
        public int ProcessId { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; set; }
        public long ReadBytes { get; set; }
        public long WriteBytes { get; set; }
        public long ReadOps { get; set; }
        public long WriteOps { get; set; }
        public long Opens { get; set; }
    }
}
