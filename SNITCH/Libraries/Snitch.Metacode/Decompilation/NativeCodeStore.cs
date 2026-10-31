using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Metacode.Analysis;
using Snitch.Metacode.Rendering;
using Snitch.Storage;
using Snitch.Storage.Schema;
using SchemaArtifactRow = Snitch.Storage.Schema.NativeArtifactRow;
using SchemaCallRow = Snitch.Storage.Schema.NativeCallRow;
using SchemaFunctionRow = Snitch.Storage.Schema.NativeFunctionRow;

namespace Snitch.Metacode.Decompilation;

/// <summary>One native binary whose decompilation the solution store keeps, keyed on the SHA-256 of the file.</summary>
public sealed record NativeArtifactRecord(
    long Id,
    string Sha256,
    string Binary,
    string Path,
    string Image,
    string Language,
    string Entry,
    string ImageBase,
    int Functions,
    long SizeBytes,
    string? Ghidra,
    string? Headless,
    DateTimeOffset CreatedAt,
    bool Stored,
    string? Database = null,
    string? Error = null)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// One recovered function: Ghidra's C, the canonical pseudocode the model made of it, and the complexity
/// both views agree on. The entry address is what makes it findable again in the binary.
/// </summary>
public sealed record NativeFunctionRecord(
    long Artifact,
    int Ordinal,
    string Name,
    string Entry,
    string Signature,
    int Size,
    string Decompiled,
    string Pseudocode,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    int Branches);

/// <summary>One call edge of the recovered program: the caller and the callee by entry address.</summary>
public sealed record NativeCallRecord(long Artifact, string Caller, string Callee);

/// <summary>
/// The native half of the solution store: what Ghidra recovered, in tables of its own
/// (<c>native_artifact</c>, <c>native_function</c>, <c>native_call</c>) rather than mixed into the
/// metacode of the solution's own documents.
/// <para>
/// The identity is the binary's SHA-256, so a copied or re-built file is recognised for what it is and
/// the same image is not stored twice. Everything here is rebuildable: the binary on disk is the truth.
/// </para>
/// </summary>
public interface INativeCodeStore
{
    /// <summary>The artifact stored for that SHA-256, or null.</summary>
    NativeArtifactRecord? Find(string sha256);

    /// <summary>
    /// Stores a Ghidra run. With <paramref name="force"/> an artifact already stored under the same hash
    /// is replaced (its functions and call edges with it); without it the existing row is returned and
    /// nothing is written. <paramref name="document"/> saves translating the snapshot twice when the
    /// caller already has it.
    /// </summary>
    NativeArtifactRecord Save(GhidraAnalysis analysis, bool force = false, MetacodeDocument? document = null);

    /// <summary>Which binaries the store holds, newest first.</summary>
    IReadOnlyList<NativeArtifactRecord> Artifacts(int limit = 100);

    /// <summary>The functions of one artifact, in snapshot order, optionally filtered by name or address.</summary>
    IReadOnlyList<NativeFunctionRecord> Functions(long artifactId, string? filter = null, int limit = 500);

    /// <summary>One function by name or entry address (with or without <c>0x</c>).</summary>
    NativeFunctionRecord? Function(long artifactId, string nameOrAddress);

    /// <summary>The call edges of one artifact.</summary>
    IReadOnlyList<NativeCallRecord> Calls(long artifactId, int limit = 5000);

    int FunctionCount(long artifactId);
}

/// <summary>
/// SQLite implementation. It owns its connection to the same <c>.visualMCP/visualmcp.db</c> the other
/// stores use and degrades to a no-op answer when no solution is loaded or SQLite is unavailable: the
/// decompilation itself has already happened, and losing the copy must not lose the result.
/// </summary>
public sealed class NativeCodeStore : INativeCodeStore, IDisposable
{
    private readonly IVisualMcpStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    public NativeCodeStore(IVisualMcpStore store) => _store = store;

    /// <summary>The database the store is reading, or null when it is not open.</summary>
    public string? DatabasePath => _path;

    /// <summary>The SHA-256 of a file, as the artifact identity.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ── write ────────────────────────────────────────────────────────────────────────

    public NativeArtifactRecord Save(GhidraAnalysis analysis, bool force = false, MetacodeDocument? document = null)
    {
        var snapshot = analysis.Snapshot;
        var binary = Path.GetFileName(analysis.BinaryPath);

        if (snapshot is null)
            return Failed(analysis.BinaryPath, analysis.Error ?? "the Ghidra run produced no snapshot.");
        if (string.IsNullOrWhiteSpace(analysis.BinaryPath) || !File.Exists(analysis.BinaryPath))
            return Failed(analysis.BinaryPath, "the binary is no longer on disk.");

        string sha;
        long size;
        try
        {
            sha = HashOf(analysis.BinaryPath);
            size = new FileInfo(analysis.BinaryPath).Length;
        }
        catch (Exception exception)
        {
            return Failed(analysis.BinaryPath, "the binary could not be read: " + exception.Message);
        }

        var rows = BuildRows(snapshot, document ?? analysis.ToMetacode());
        var ghidraVersion = VersionOf(analysis.HeadlessPath);
        var database = _store.EnsureDatabase();
        if (database is null)
        {
            return new NativeArtifactRecord(
                0, sha, binary, analysis.BinaryPath, snapshot.Image, snapshot.Language, snapshot.Entry,
                snapshot.ImageBase, snapshot.Functions.Count, size, null, analysis.HeadlessPath,
                DateTimeOffset.UtcNow, Stored: false, Database: null,
                Error: "No solution is loaded, so there is nowhere to store the decompilation.");
        }

        var connection = Open(database);
        if (connection is null)
        {
            return new NativeArtifactRecord(
                0, sha, binary, analysis.BinaryPath, snapshot.Image, snapshot.Language, snapshot.Entry,
                snapshot.ImageBase, snapshot.Functions.Count, size, null, analysis.HeadlessPath,
                DateTimeOffset.UtcNow, Stored: false, Database: database,
                Error: "The solution database could not be opened (is the solution folder writable?).");
        }

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var existing = context.NativeArtifacts.FirstOrDefault(row => row.Sha256 == sha);
            if (existing is not null && !force)
            {
                return Shape(existing, stored: false, database) with
                {
                    Binary = binary,
                    Path = analysis.BinaryPath,
                    SizeBytes = size,
                    Headless = analysis.HeadlessPath,
                };
            }

            var now = DateTimeOffset.UtcNow;
            using var transaction = context.Database.BeginTransaction();

            SchemaArtifactRow artifact;
            if (existing is not null)
            {
                context.NativeFunctions.Where(row => row.Artifact == existing.Id).ExecuteDelete();
                context.NativeCalls.Where(row => row.Artifact == existing.Id).ExecuteDelete();

                existing.Binary = binary;
                existing.Path = analysis.BinaryPath;
                existing.Image = snapshot.Image;
                existing.Language = snapshot.Language;
                existing.Entry = snapshot.Entry;
                existing.ImageBase = snapshot.ImageBase;
                existing.Functions = snapshot.Functions.Count;
                existing.SizeBytes = size;
                existing.Ghidra = ghidraVersion;
                existing.Headless = analysis.HeadlessPath;
                existing.CreatedAt = now;
                artifact = existing;
            }
            else
            {
                artifact = new SchemaArtifactRow
                {
                    Sha256 = sha,
                    Binary = binary,
                    Path = analysis.BinaryPath,
                    Image = snapshot.Image,
                    Language = snapshot.Language,
                    Entry = snapshot.Entry,
                    ImageBase = snapshot.ImageBase,
                    Functions = snapshot.Functions.Count,
                    SizeBytes = size,
                    Ghidra = ghidraVersion,
                    Headless = analysis.HeadlessPath,
                    CreatedAt = now,
                };

                context.NativeArtifacts.Add(artifact);

                // The child rows are keyed on the identity the database assigns.
                context.SaveChanges();
            }

            var id = artifact.Id;

            context.NativeFunctions.AddRange(rows.Functions.Select(row => new SchemaFunctionRow
            {
                Artifact = id,
                Ordinal = row.Ordinal,
                Name = row.Name,
                Entry = row.Entry,
                Signature = row.Signature,
                Size = row.Size,
                Decompiled = row.Decompiled,
                Pseudocode = row.Pseudocode,
                Cyclomatic = row.Cyclomatic,
                Cognitive = row.Cognitive,
                MaxNesting = row.MaxNesting,
                Statements = row.Statements,
                Branches = row.Branches,
            }));

            context.NativeCalls.AddRange(rows.Calls.Select(edge => new SchemaCallRow
            {
                Artifact = id,
                Caller = edge.Caller,
                Callee = edge.Callee,
            }));

            context.SaveChanges();
            transaction.Commit();

            return Shape(artifact, stored: true, database);
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException)
        {
            return Failed(analysis.BinaryPath, "the solution database refused the write: " + exception.Message, sha, database);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── read ─────────────────────────────────────────────────────────────────────────

    public NativeArtifactRecord? Find(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var connection = OpenStore();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var row = context.NativeArtifacts
                .AsNoTracking()
                .FirstOrDefault(artifact => artifact.Sha256 == sha256.Trim().ToLowerInvariant());

            return row is null ? null : Shape(row);
        }
        catch (SqliteException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<NativeArtifactRecord> Artifacts(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            // SQLite cannot ORDER BY a DateTimeOffset through EF; artifacts are few, so the page is
            // ordered after it is read.
            return context.NativeArtifacts
                .AsNoTracking()
                .ToList()
                .OrderByDescending(artifact => artifact.CreatedAt)
                .Take(limit)
                .Select(artifact => Shape(artifact))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<NativeFunctionRecord> Functions(long artifactId, string? filter = null, int limit = 500)
    {
        limit = Math.Clamp(limit, 1, 20_000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var query = context.NativeFunctions.AsNoTracking().Where(row => row.Artifact == artifactId);

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var needle = "%" + filter.Trim() + "%";
                query = query.Where(row => EF.Functions.Like(row.Name, needle) || EF.Functions.Like(row.Entry, needle));
            }

            return query
                .OrderBy(row => row.Ordinal)
                .Take(limit)
                .ToList()
                .Select(Shape)
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public NativeFunctionRecord? Function(long artifactId, string nameOrAddress)
    {
        if (string.IsNullOrWhiteSpace(nameOrAddress)) return null;
        var connection = OpenStore();
        if (connection is null) return null;

        var wanted = nameOrAddress.Trim();
        var address = wanted.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? wanted[2..] : wanted;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var row = context.NativeFunctions
                .AsNoTracking()
                .Where(function =>
                    function.Artifact == artifactId &&
                    (EF.Functions.Collate(function.Name, "NOCASE") == wanted ||
                     EF.Functions.Collate(function.Entry, "NOCASE") == address))
                .OrderBy(function => function.Ordinal)
                .FirstOrDefault();

            return row is null ? null : Shape(row);
        }
        catch (SqliteException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<NativeCallRecord> Calls(long artifactId, int limit = 5000)
    {
        limit = Math.Clamp(limit, 1, 50_000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.NativeCalls
                .AsNoTracking()
                .Where(row => row.Artifact == artifactId)
                .OrderBy(row => row.Caller)
                .ThenBy(row => row.Callee)
                .Take(limit)
                .ToList()
                .Select(row => new NativeCallRecord(row.Artifact, row.Caller, row.Callee))
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public int FunctionCount(long artifactId)
    {
        var connection = OpenStore();
        if (connection is null) return 0;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.NativeFunctions.Count(row => row.Artifact == artifactId);
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────

    private sealed record RowSet(
        IReadOnlyList<NativeFunctionRecord> Functions,
        IReadOnlyList<(string Caller, string Callee)> Calls);

    /// <summary>
    /// One pass over the model gives both the per-function pseudocode and the complexity, so a function
    /// that the C reader could not place costs that row (empty pseudocode, default score), not the binary.
    /// </summary>
    private static RowSet BuildRows(GhidraSnapshot snapshot, MetacodeDocument document)
    {
        var parsed = document.Modules
            .SelectMany(module => module.Declarations)
            .OfType<MetacodeFunction>()
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var renderer = new PseudocodeRenderer();
        var functions = new List<NativeFunctionRecord>(snapshot.Functions.Count);
        var calls = new HashSet<(string, string)>();

        for (var i = 0; i < snapshot.Functions.Count; i++)
        {
            var function = snapshot.Functions[i];
            parsed.TryGetValue(function.Name, out var model);

            var pseudocode = model is null ? string.Empty : renderer.Render(model).TrimEnd();
            var metrics = model is null ? MetacodeComplexityMetrics.Empty : MetacodeComplexityAnalyzer.Analyze(model);

            functions.Add(new NativeFunctionRecord(
                Artifact: 0,
                Ordinal: i,
                Name: function.Name,
                Entry: function.Entry,
                Signature: function.Signature,
                Size: function.Size,
                Decompiled: function.Decompiled,
                Pseudocode: pseudocode,
                Cyclomatic: metrics.Cyclomatic,
                Cognitive: metrics.Cognitive,
                MaxNesting: metrics.MaxNesting,
                Statements: metrics.Statements,
                Branches: metrics.Branches));

            foreach (var callee in function.Calls)
                calls.Add((function.Entry, callee));
        }

        return new RowSet(functions, calls.Select(edge => (edge.Item1, edge.Item2)).ToList());
    }

    private static NativeArtifactRecord Failed(string path, string error, string? sha = null, string? database = null) =>
        new(
            0, sha ?? string.Empty, Path.GetFileName(path), path, string.Empty, string.Empty, string.Empty,
            string.Empty, 0, 0, null, null, DateTimeOffset.UtcNow, Stored: false, Database: database, Error: error);

    /// <summary>The version the tools folder names: <c>.../ghidra_12.2_PUBLIC/support/analyzeHeadless</c>.</summary>
    private static string? VersionOf(string? headless)
    {
        if (string.IsNullOrWhiteSpace(headless)) return null;

        var home = Path.GetDirectoryName(Path.GetDirectoryName(headless));
        var folder = home is null ? null : Path.GetFileName(home);
        if (string.IsNullOrWhiteSpace(folder)) return null;

        const string prefix = "ghidra_";
        var start = folder.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += prefix.Length;
        var end = folder.IndexOf('_', start);
        return end > start ? folder[start..end] : folder[start..];
    }

    private static NativeArtifactRecord Shape(SchemaArtifactRow row, bool stored = true, string? database = null) =>
        new(row.Id, row.Sha256, row.Binary, row.Path, row.Image, row.Language, row.Entry,
            row.ImageBase, row.Functions, row.SizeBytes, row.Ghidra, row.Headless, row.CreatedAt,
            stored, database);

    private static NativeFunctionRecord Shape(SchemaFunctionRow row) =>
        new(row.Artifact, row.Ordinal, row.Name, row.Entry, row.Signature, row.Size, row.Decompiled,
            row.Pseudocode, row.Cyclomatic, row.Cognitive, row.MaxNesting, row.Statements, row.Branches);

    // ── connection ───────────────────────────────────────────────────────────────────

    private SqliteConnection? OpenStore() => _store.EnsureDatabase() is { } database ? Open(database) : null;

    private SqliteConnection? Open(string database)
    {
        _gate.Wait();
        try
        {
            if (_connection is not null && string.Equals(_path, database, StringComparison.OrdinalIgnoreCase))
                return _connection;
            if (_unavailable) return null;

            try
            {
                _connection?.Dispose();
                _connection = null;

                Directory.CreateDirectory(Path.GetDirectoryName(database)!);

                var connection = new SqliteConnection($"Data Source={database}");
                connection.Open();

                using (var pragma = connection.CreateCommand())
                {
                    // The store, the board and the other indexes write this file too.
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                _connection = connection;
                _path = database;
                return connection;
            }
            catch (Exception)
            {
                _unavailable = true;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
