using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Storage.Schema;
using Snitch.Workspace;

namespace Snitch.Storage;

/// <summary>
/// ReSharper's Local History: whole-file snapshots kept in the solution store, independent of commits.
/// <c>snapshot</c> captures every source file whose content changed since its last snapshot, <c>history</c>
/// lists what is stored for a file, <c>diff</c> shows a snapshot against the file (or against another
/// snapshot), and <c>restore</c> writes one back.
/// </summary>
/// <remarks>
/// The connection is this service's own, guarded by a <see cref="SemaphoreSlim"/>: several owners open the
/// same database file with their own connection, which is the store's documented model, and a semaphore
/// keeps the writes serialised without adding another monitor to the process.
/// </remarks>
public interface ILocalHistoryService : IDisposable
{
    Task<LocalHistorySnapshotResult> SnapshotAsync(CancellationToken cancellationToken = default);

    Task<LocalHistoryHistoryResult> HistoryAsync(string filePath, int limit = 20);

    Task<LocalHistoryDiffResult> DiffAsync(string filePath, long id, long? otherId = null, CancellationToken cancellationToken = default);

    Task<LocalHistoryRestoreResult> RestoreAsync(string filePath, long id, bool dryRun = true, CancellationToken cancellationToken = default);
}

public sealed class LocalHistoryService : ILocalHistoryService
{
    /// <summary>How many snapshots one file keeps: enough to walk back a session, bounded so the file stays small.</summary>
    private const int KeepPerFile = 50;

    /// <summary>A snapshot is a convenience, not a backup: a large file is simply not captured.</summary>
    private const long MaxFileBytes = 1_500_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".cshtml", ".razor", ".xaml", ".axaml",
        ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx", ".editorconfig",
        ".json", ".yml", ".yaml", ".md", ".sql", ".http", ".css", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".xml", ".config", ".txt", ".gitignore",
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "TestResults",
    };

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _connectedDatabase;

    public LocalHistoryService(IRoslynWorkspace workspace, IVisualMcpStore store)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<LocalHistorySnapshotResult> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            if (connection is null) return new LocalHistorySnapshotResult { Error = NoStoreMessage };

            var files = await SourceFilesAsync(cancellationToken);
            if (files.Count == 0) return new LocalHistorySnapshotResult { Error = "No source file of the loaded solution is on disk." };

            var latest = LatestHashes(connection);
            var captured = new List<LocalHistorySnapshotEntry>();
            int unchanged = 0, skipped = 0;

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string text;
                try
                {
                    if (new FileInfo(path).Length > MaxFileBytes)
                    {
                        skipped++;
                        continue;
                    }

                    text = await File.ReadAllTextAsync(path, cancellationToken);
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }

                var hash = VisualMcpStore.ContentHash(text);
                if (latest.TryGetValue(path, out var previous) && previous == hash)
                {
                    unchanged++;
                    continue;
                }

                Insert(connection, path, hash, text, "snapshot");
                latest[path] = hash;
                captured.Add(new LocalHistorySnapshotEntry(path, hash, text.Length));
            }

            return new LocalHistorySnapshotResult
            {
                Database = _store.DatabasePath,
                Scanned = files.Count,
                Captured = captured.Count,
                Unchanged = unchanged,
                Skipped = skipped,
                Files = captured,
                Note = "A snapshot is taken only for a file whose content differs from its newest one; " +
                       $"each file keeps the newest {KeepPerFile}.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalHistoryHistoryResult> HistoryAsync(string filePath, int limit = 20)
    {
        await _gate.WaitAsync();
        try
        {
            var connection = await OpenAsync(CancellationToken.None);
            if (connection is null) return new LocalHistoryHistoryResult { Error = NoStoreMessage };

            var path = FullPath(filePath);
            if (path is null) return new LocalHistoryHistoryResult { Error = $"Invalid path '{filePath}'." };

            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            using var context = VisualMcpSchema.CreateContext(connection);
            var snapshots = context.FileSnapshots
                .AsNoTracking()
                .Where(row => row.File == path)
                .OrderByDescending(row => row.Id)
                .Take(limit)
                .Select(row => new { row.Id, row.CapturedAt, row.Hash, row.Chars, row.Origin })
                .ToList()
                .Select(row => new LocalHistorySnapshotSummary(
                    row.Id,
                    row.CapturedAt.ToUniversalTime().ToString("O"),
                    row.Hash,
                    row.Chars,
                    row.Origin))
                .ToList();

            var current = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            var currentHash = current is null ? null : VisualMcpStore.ContentHash(current);

            return new LocalHistoryHistoryResult
            {
                File = path,
                Exists = current is not null,
                Current = new LocalHistoryCurrentState(currentHash, current?.Length),
                Returned = snapshots.Count,
                Snapshots = snapshots,
                Note = "A snapshot whose hash equals `current.hash` is the file as it stands.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalHistoryDiffResult> DiffAsync(string filePath, long id, long? otherId = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            if (connection is null) return new LocalHistoryDiffResult { Error = NoStoreMessage };

            var path = FullPath(filePath);
            if (path is null) return new LocalHistoryDiffResult { Error = $"Invalid path '{filePath}'." };

            var baseline = Read(connection, id);
            if (baseline is null) return new LocalHistoryDiffResult { Error = $"No snapshot #{id}." };
            if (!string.Equals(baseline.Value.File, path, StringComparison.OrdinalIgnoreCase))
                return new LocalHistoryDiffResult { Error = $"Snapshot #{id} belongs to {baseline.Value.File}, not {path}." };

            string current;
            if (otherId is { } other)
            {
                var otherSnapshot = Read(connection, other);
                if (otherSnapshot is null) return new LocalHistoryDiffResult { Error = $"No snapshot #{other}." };
                if (!string.Equals(otherSnapshot.Value.File, path, StringComparison.OrdinalIgnoreCase))
                    return new LocalHistoryDiffResult { Error = $"Snapshot #{other} belongs to {otherSnapshot.Value.File}, not {path}." };

                var firstOlder = baseline.Value.CapturedAt <= otherSnapshot.Value.CapturedAt;
                var older = firstOlder ? baseline.Value.Text : otherSnapshot.Value.Text;
                var newer = firstOlder ? otherSnapshot.Value.Text : baseline.Value.Text;

                return new LocalHistoryDiffResult
                {
                    File = path,
                    Left = older.Length,
                    Right = newer.Length,
                    Against = $"snapshot #{other}",
                    Diff = RefactoringFileWriter.Diff(older, newer, maxLines: 200),
                };
            }

            current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;

            return new LocalHistoryDiffResult
            {
                File = path,
                Snapshot = new LocalHistoryDiffSnapshot(id, baseline.Value.CapturedAt.ToString("O"), baseline.Value.Text.Length),
                Against = "current file",
                Diff = RefactoringFileWriter.Diff(baseline.Value.Text, current, maxLines: 200),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalHistoryRestoreResult> RestoreAsync(string filePath, long id, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connection = await OpenAsync(cancellationToken);
            if (connection is null) return new LocalHistoryRestoreResult { Error = NoStoreMessage };

            var path = FullPath(filePath);
            if (path is null) return new LocalHistoryRestoreResult { Error = $"Invalid path '{filePath}'." };

            var snapshot = Read(connection, id);
            if (snapshot is null) return new LocalHistoryRestoreResult { Error = $"No snapshot #{id}." };
            if (!string.Equals(snapshot.Value.File, path, StringComparison.OrdinalIgnoreCase))
                return new LocalHistoryRestoreResult { Error = $"Snapshot #{id} belongs to {snapshot.Value.File}, not {path}." };

            var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
            var currentHash = VisualMcpStore.ContentHash(current);
            var snapshotHash = VisualMcpStore.ContentHash(snapshot.Value.Text);
            var diff = RefactoringFileWriter.Diff(current, snapshot.Value.Text, maxLines: 200);

            if (currentHash == snapshotHash)
                return new LocalHistoryRestoreUnchangedResult
                {
                    File = path,
                    Snapshot = id,
                    Applied = false,
                    Note = "The file already holds that snapshot's text.",
                };

            if (!dryRun)
            {
                // Restoring is itself a change: the text it replaces is captured first, so the restore can
                // be reverted by restoring the snapshot that is about to be written.
                Insert(connection, path, currentHash, current, $"before-restore#{id}");
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, path, snapshot.Value.Text, cancellationToken);
                _store.RecordEdit("local_history_restore", path, $"snapshot #{id}", current, snapshot.Value.Text);
            }

            return new LocalHistoryRestoreAppliedResult
            {
                File = path,
                Snapshot = new LocalHistoryRestoreSnapshot(id, snapshot.Value.CapturedAt.ToString("O")),
                DryRun = dryRun,
                Applied = !dryRun,
                Diff = diff,
                Note = dryRun
                    ? "Preview only: pass dryRun=false to write the snapshot."
                    : "File written; the text it replaced is snapshotted as `before-restore`.",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── files ───────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> SourceFilesAsync(CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is not null)
        {
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is { Length: > 0 } path && File.Exists(path))
                        files.Add(Path.GetFullPath(path));
                }

                if (Path.GetDirectoryName(project.FilePath) is { Length: > 0 } directory && Directory.Exists(directory))
                {
                    foreach (var path in EnumerateSourceFiles(directory))
                        files.Add(path);
                }
            }
        }
        else if (_workspace.LoadedSolutionPath is { } solutionPath &&
                 Path.GetDirectoryName(solutionPath) is { Length: > 0 } solutionDirectory)
        {
            foreach (var path in EnumerateSourceFiles(solutionDirectory))
                files.Add(path);
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every snapshot-able file under a directory, with build and VCS folders pruned as it walks.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                        pending.Push(entry);
                    continue;
                }

                if (Extensions.Contains(Path.GetExtension(entry)) || Extensions.Contains(Path.GetFileName(entry)))
                    yield return Path.GetFullPath(entry);
            }
        }
    }

    private static string? FullPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try { return Path.GetFullPath(filePath); }
        catch (Exception) { return null; }
    }

    // ── storage ─────────────────────────────────────────────────────────────────────────

    private async Task<SqliteConnection?> OpenAsync(CancellationToken cancellationToken)
    {
        var database = _store.DatabasePath ?? _store.EnsureDatabase();
        if (database is null) return null;

        if (_connection is not null && string.Equals(_connectedDatabase, database, StringComparison.OrdinalIgnoreCase))
            return _connection;

        _connection?.Dispose();
        var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync(cancellationToken);

        using (var busy = connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
        }

        VisualMcpSchema.Ensure(connection);

        _connection = connection;
        _connectedDatabase = database;
        return connection;
    }

    private static Dictionary<string, string> LatestHashes(SqliteConnection connection)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var context = VisualMcpSchema.CreateContext(connection);
        var stamps = context.FileSnapshots
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .Select(row => new { row.File, row.Hash })
            .ToList();

        foreach (var stamp in stamps)
            latest[stamp.File] = stamp.Hash;

        return latest;
    }

    private static void Insert(SqliteConnection connection, string path, string hash, string text, string origin)
    {
        using var context = VisualMcpSchema.CreateContext(connection);

        // The unique (file, hash) index is the deduplication: unchanged content is one row, not two.
        if (context.FileSnapshots.Any(row => row.File == path && row.Hash == hash)) return;

        context.FileSnapshots.Add(new FileSnapshotRow
        {
            File = path,
            Hash = hash,
            CapturedAt = DateTimeOffset.UtcNow,
            Chars = text.Length,
            Origin = origin,
            Text = VisualMcpStore.CompressText(text),
        });

        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Another owner of the database captured the same content between the check and the
            // write; the index refused the duplicate, which is the outcome this method wants anyway.
            context.ChangeTracker.Clear();
        }

        // A file keeps its newest snapshots only: a snapshot is a convenience, not a backup.
        var stale = context.FileSnapshots
            .Where(row => row.File == path)
            .OrderByDescending(row => row.Id)
            .Skip(KeepPerFile)
            .ToList();

        if (stale.Count == 0) return;

        context.FileSnapshots.RemoveRange(stale);
        context.SaveChanges();
    }

    private static (string File, DateTimeOffset CapturedAt, string Text)? Read(SqliteConnection connection, long id)
    {
        using var context = VisualMcpSchema.CreateContext(connection);
        var snapshot = context.FileSnapshots
            .AsNoTracking()
            .Where(row => row.Id == id)
            .Select(row => new { row.File, row.CapturedAt, row.Text })
            .FirstOrDefault();

        return snapshot?.Text is null
            ? null
            : (snapshot.File, snapshot.CapturedAt, VisualMcpStore.DecompressText(snapshot.Text));
    }

    private const string NoStoreMessage =
        "No solution store is available, so there is no local history. Call load_solution first.";

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }
}
