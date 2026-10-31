using System.IO.Compression;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Storage.Schema;
using SchemaDocumentRow = Snitch.Storage.Schema.SymbolDocumentRow;
using SchemaMetacodeRow = Snitch.Storage.Schema.SymbolMetacodeRow;
using SchemaSymbolRow = Snitch.Storage.Schema.SymbolRow;
using SchemaUsageRow = Snitch.Storage.Schema.SymbolUsageRow;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The symbol index on disk: three tables inside <c>&lt;solution&gt;/.visualMCP/visualmcp.db</c> — the
/// declarations, the uses and the fingerprint of each indexed document. It owns its own connection and
/// its own tables, like the board does, and every fact in it is rebuildable from the source files: the
/// files on disk stay the only truth.
/// </summary>
/// <remarks>
/// Writes replace one document at a time, in one transaction, so a build interrupted halfway leaves
/// the documents it finished valid and the rest to be indexed again on the next run.
/// </remarks>
public sealed class SymbolIndexStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    /// <summary>The database the index is reading, or null when it is not open.</summary>
    public string? Path => _path;

    /// <summary>The content hash a document is stamped with; the same function the edit history uses.</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Opens the database, creating the index tables. Returns false when SQLite cannot be used.</summary>
    public bool Open(string databasePath)
    {
        _gate.Wait();
        try
        {
            if (_connection is not null && string.Equals(_path, databasePath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_unavailable) return false;

            try
            {
                _connection?.Dispose();
                _connection = null;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

                var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                using (var pragma = connection.CreateCommand())
                {
                    // Other owners of the same file (the store, the board) write too: wait for them
                    // instead of failing the query.
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                _connection = connection;
                _path = databasePath;
                return true;
            }
            catch (Exception)
            {
                _unavailable = true;
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The stored hash of every indexed document, so a build can skip the unchanged ones.</summary>
    public Dictionary<string, string> Hashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connection = Open();
        if (connection is null) return hashes;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var documents = context.SymbolDocuments
                .AsNoTracking()
                .Select(document => new { document.Path, document.Hash })
                .ToList();

            foreach (var document in documents)
                hashes[document.Path] = document.Hash;

            return hashes;
        }
        catch (SqliteException)
        {
            return hashes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces every row of one document: the declarations, the uses and the fingerprint.</summary>
    public async Task ReplaceAsync(SymbolDocumentFacts facts, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            using var transaction = context.Database.BeginTransaction();

            context.Symbols.Where(row => row.Path == facts.Path).ExecuteDelete();
            context.SymbolUsages.Where(row => row.Path == facts.Path).ExecuteDelete();
            context.SymbolMetacode.Where(row => row.Path == facts.Path).ExecuteDelete();

            context.Symbols.AddRange(facts.Declarations
                .GroupBy(declaration => (declaration.Key, declaration.Path, declaration.Start))
                .Select(group => group.Last())
                .Select(declaration => new SchemaSymbolRow
                {
                    SymbolKey = declaration.Key,
                    Name = declaration.Name,
                    Display = declaration.Display,
                    Signature = declaration.Signature,
                    Kind = declaration.Kind,
                    Accessibility = declaration.Accessibility,
                    Modifiers = declaration.Modifiers,
                    ContainerKey = declaration.ContainerKey,
                    ContainerName = declaration.ContainerName,
                    Namespace = declaration.Namespace,
                    IsStatic = declaration.IsStatic,
                    IsAbstract = declaration.IsAbstract,
                    IsVirtual = declaration.IsVirtual,
                    IsOverride = declaration.IsOverride,
                    IsExtension = declaration.IsExtension,
                    Arity = declaration.Arity,
                    Summary = declaration.Summary,
                    Project = declaration.Project,
                    Path = declaration.Path,
                    Start = declaration.Start,
                    Length = declaration.Length,
                    Line = declaration.Line,
                    Col = declaration.Column,
                    EndLine = declaration.EndLine,
                    EndCol = declaration.EndColumn,
                    DocHash = declaration.DocumentHash,
                }));

            context.SymbolUsages.AddRange(facts.Usages.Select(usage => new SchemaUsageRow
            {
                SymbolKey = usage.SymbolKey,
                Name = usage.Name,
                Kind = usage.Kind,
                ContainerKey = usage.ContainerKey,
                Path = usage.Path,
                Start = usage.Start,
                Length = usage.Length,
                Line = usage.Line,
                Col = usage.Column,
                DocHash = usage.DocumentHash,
            }));

            var document = context.SymbolDocuments.FirstOrDefault(row => row.Path == facts.Path);
            if (document is null)
            {
                context.SymbolDocuments.Add(new SchemaDocumentRow
                {
                    Path = facts.Path,
                    Project = facts.Project,
                    Hash = facts.Hash,
                    Declarations = facts.Declarations.Count,
                    Usages = facts.Usages.Count,
                    IndexedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                document.Project = facts.Project;
                document.Hash = facts.Hash;
                document.Declarations = facts.Declarations.Count;
                document.Usages = facts.Usages.Count;
                document.IndexedAt = DateTimeOffset.UtcNow;
            }

            context.SymbolMetacode.AddRange(facts.Trees
                .GroupBy(tree => (tree.SymbolKey, tree.Path))
                .Select(group => group.Last())
                .Select(tree => new SchemaMetacodeRow
                {
                    SymbolKey = tree.SymbolKey,
                    Path = tree.Path,
                    Kind = tree.Kind,
                    Name = tree.Name,
                    Line = tree.Line,
                    Text = Compress(tree.Text),
                    DocHash = tree.DocumentHash,
                }));

            context.SaveChanges();
            transaction.Commit();
        }
        catch (SqliteException)
        {
            // A document that fails to write is re-indexed next time: its hash was never stamped.
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── queries ─────────────────────────────────────────────────────────────

    /// <summary>Declarations by symbol name, optionally a substring search and/or one kind.</summary>
    public IReadOnlyList<SymbolDeclaration> DeclarationsByName(string name, bool partial, string? kind, int limit)
    {
        var pattern = "%" + Escape(name) + "%";

        return partial
            ? QueryDeclarations(
                row => EF.Functions.Like(row.Name, pattern, "\\") && (kind == null || row.Kind == kind),
                QueryDeclarationsByName,
                limit)
            : QueryDeclarations(
                row => row.Name == name && (kind == null || row.Kind == kind),
                QueryDeclarationsByName,
                limit);
    }

    /// <summary>Every declaration of one key (a partial type has more than one).</summary>
    public IReadOnlyList<SymbolDeclaration> DeclarationsByKey(string key, int limit = 50) =>
        QueryDeclarations(
            row => row.SymbolKey == key,
            query => query.OrderBy(row => row.Path).ThenBy(row => row.Start),
            limit);

    public IReadOnlyList<SymbolDeclaration> DeclarationsByPath(string path, int limit) =>
        QueryDeclarations(
            row => row.Path == path,
            query => query.OrderBy(row => row.Start),
            limit);

    public IReadOnlyList<SymbolDeclaration> DeclarationsInContainer(string containerKey, int limit) =>
        QueryDeclarations(
            row => row.ContainerKey == containerKey,
            query => query.OrderBy(row => row.Start),
            limit);

    public IReadOnlyList<SymbolUsage> UsagesOf(string symbolKey, string? kind, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var query = context.SymbolUsages.AsNoTracking().Where(row => row.SymbolKey == symbolKey);
            if (kind is not null) query = query.Where(row => row.Kind == kind);

            return query
                .OrderBy(row => row.Path)
                .ThenBy(row => row.Line)
                .ThenBy(row => row.Col)
                .Take(Clamp(limit))
                .ToList()
                .Select(row => new SymbolUsage(
                    row.SymbolKey, row.Name, row.Kind, row.ContainerKey, row.Path,
                    row.Start, row.Length, row.Line, row.Col, row.DocHash))
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

    /// <summary>The uses of a symbol grouped by the member they happen inside — VS's "find all references", grouped.</summary>
    public IReadOnlyList<SymbolContainerCount> ContainersOf(string symbolKey, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var rows = context.SymbolUsages
                .AsNoTracking()
                .Where(row => row.SymbolKey == symbolKey && row.Kind != "override" && row.Kind != "implement")
                .Select(row => new { row.ContainerKey, row.Kind })
                .ToList();

            return rows
                .GroupBy(row => row.ContainerKey)
                .Select(group => new SymbolContainerCount(
                    group.Key, group.Count(), string.Join(",", group.Select(row => row.Kind).Distinct().OrderBy(kind => kind, StringComparer.Ordinal))))
                .OrderByDescending(count => count.Count)
                .ThenBy(count => count.ContainerKey, StringComparer.Ordinal)
                .Take(Clamp(limit))
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

    /// <summary>What a member uses, grouped by the used symbol — the other half of the call hierarchy.</summary>
    public IReadOnlyList<SymbolTargetCount> TargetsOf(string containerKey, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var rows = context.SymbolUsages
                .AsNoTracking()
                .Where(row => row.ContainerKey == containerKey && row.Kind != "override")
                .Select(row => new { row.SymbolKey, row.Name, row.Kind })
                .ToList();

            return rows
                .GroupBy(row => new { row.SymbolKey, row.Name })
                .Select(group => new SymbolTargetCount(
                    group.Key.SymbolKey, group.Key.Name, group.Count(),
                    string.Join(",", group.Select(row => row.Kind).Distinct().OrderBy(kind => kind, StringComparer.Ordinal))))
                .OrderByDescending(count => count.Count)
                .ThenBy(count => count.Name, StringComparer.Ordinal)
                .Take(Clamp(limit))
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

    /// <summary>The kinds a symbol is used in, with how often each occurs.</summary>
    public IReadOnlyList<SymbolKindCount> UsageCounts(string symbolKey)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.SymbolUsages
                .AsNoTracking()
                .Where(row => row.SymbolKey == symbolKey)
                .GroupBy(row => row.Kind)
                .Select(group => new { Kind = group.Key, Uses = group.Count() })
                .OrderByDescending(entry => entry.Uses)
                .ThenBy(entry => entry.Kind)
                .ToList()
                .Select(entry => new SymbolKindCount(entry.Kind, entry.Uses))
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

    /// <summary>Total uses per symbol key, for a list of keys in one query (the counts a search shows).</summary>
    public Dictionary<string, int> UsageTotals(IReadOnlyList<string> keys)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (keys.Count == 0) return totals;

        var connection = Open();
        if (connection is null) return totals;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var rows = context.SymbolUsages
                .AsNoTracking()
                .Where(row =>
                    row.Kind != "override" && row.Kind != "implement" && keys.Contains(row.SymbolKey))
                .GroupBy(row => row.SymbolKey)
                .Select(group => new { Key = group.Key, Uses = group.Count() })
                .ToList();

            foreach (var row in rows)
                totals[row.Key] = row.Uses;

            return totals;
        }
        catch (SqliteException)
        {
            return totals;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One side of a relationship: the types a type extends or implements, and — with
    /// <paramref name="direction"/> incoming — the types that extend it and the members that implement it.
    /// </summary>
    public IReadOnlyList<(string Key, string Name)> Relationship(
        string key, string kind, bool incoming, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            if (incoming)
            {
                return context.SymbolUsages
                    .AsNoTracking()
                    .Where(row => row.SymbolKey == key && row.Kind == kind)
                    .Select(row => row.ContainerKey)
                    .Distinct()
                    .OrderBy(container => container)
                    .Take(Clamp(limit))
                    .ToList()
                    .Select(container => (container, string.Empty))
                    .ToList();
            }

            return context.SymbolUsages
                .AsNoTracking()
                .Where(row => row.ContainerKey == key && row.Kind == kind)
                .Select(row => new { row.SymbolKey, row.Name })
                .Distinct()
                .OrderBy(edge => edge.Name)
                .ThenBy(edge => edge.SymbolKey)
                .Take(Clamp(limit))
                .ToList()
                .Select(edge => (edge.SymbolKey, edge.Name))
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

    /// <summary>The metacode tree of one declaration, or none when it has no body the model can render.</summary>
    public IReadOnlyList<SymbolMetacode> MetacodeOf(string symbolKey, int limit = 5) =>
        Metacode(query => query.Where(row => row.SymbolKey == symbolKey).OrderBy(row => row.Path), limit);

    /// <summary>Every tree the index holds for a file, in source order: the file as metacode.</summary>
    public IReadOnlyList<SymbolMetacode> MetacodeInPath(string path, int limit = 200) =>
        Metacode(query => query.Where(row => row.Path == path).OrderBy(row => row.Line), limit);

    /// <summary>Non-public declarations nothing in the index uses.</summary>
    public IReadOnlyList<SymbolDeclaration> Unused(int limit, string? kind = null)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var nonPublic = new[] { "private", "internal", "protected", "private protected", "protected internal" };

            var query = context.Symbols
                .AsNoTracking()
                .Where(row =>
                    nonPublic.Contains(row.Accessibility) &&
                    !context.SymbolUsages.Any(usage => usage.SymbolKey == row.SymbolKey));

            if (kind is not null) query = query.Where(row => row.Kind == kind);

            return query
                .OrderBy(row => row.Path)
                .ThenBy(row => row.Start)
                .Take(Clamp(limit))
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

    /// <summary>
    /// Drops the rows of documents the solution no longer contains, so a deleted or renamed file does
    /// not keep answering queries. Returns how many documents were pruned.
    /// </summary>
    public async Task<int> PruneAsync(IReadOnlySet<string> live, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var paths = context.SymbolDocuments
                .AsNoTracking()
                .Select(document => document.Path)
                .ToList();

            var stale = paths.Where(path => !live.Contains(path)).ToList();
            if (stale.Count == 0) return 0;

            using var transaction = context.Database.BeginTransaction();
            foreach (var path in stale)
            {
                context.Symbols.Where(row => row.Path == path).ExecuteDelete();
                context.SymbolUsages.Where(row => row.Path == path).ExecuteDelete();
                context.SymbolMetacode.Where(row => row.Path == path).ExecuteDelete();
                context.SymbolDocuments.Where(row => row.Path == path).ExecuteDelete();
            }

            transaction.Commit();
            return stale.Count;
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

    /// <summary>How many documents, declarations and uses are stored, plus the histogram of kinds.</summary>
    public SymbolIndexStatus Status()
    {
        var connection = Open();
        if (connection is null)
            return new SymbolIndexStatus(0, 0, 0, 0, null, [], []);

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            var documents = context.SymbolDocuments.Count();
            var symbols = context.Symbols.Select(row => row.SymbolKey).Distinct().Count();
            var declarations = context.Symbols.Count();
            var usages = context.SymbolUsages.Count();

            // SQLite cannot ORDER BY a DateTimeOffset through EF; the timestamps are few enough to rank here.
            var indexedAt = context.SymbolDocuments
                .AsNoTracking()
                .Select(document => document.IndexedAt)
                .ToList();
            var newest = indexedAt.Count == 0 ? null : indexedAt.Max().ToUniversalTime().ToString("O");

            return new SymbolIndexStatus(
                documents, symbols, declarations, usages, newest,
                Histogram(context.Symbols.Select(row => row.Kind)),
                Histogram(context.SymbolUsages.Select(row => row.Kind)));
        }
        catch (SqliteException)
        {
            return new SymbolIndexStatus(0, 0, 0, 0, null, [], []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the index; the next build re-indexes everything.</summary>
    public void Clear()
    {
        var connection = Open();
        if (connection is null) return;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            context.SymbolUsages.ExecuteDelete();
            context.SymbolMetacode.ExecuteDelete();
            context.Symbols.ExecuteDelete();
            context.SymbolDocuments.ExecuteDelete();
        }
        catch (SqliteException)
        {
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

        _gate.Dispose();
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private SqliteConnection? Open()
    {
        _gate.Wait();
        try
        {
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<SymbolDeclaration> QueryDeclarations(
        Expression<Func<SchemaSymbolRow, bool>> where,
        Func<IQueryable<SchemaSymbolRow>, IQueryable<SchemaSymbolRow>> order,
        int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return order(context.Symbols.AsNoTracking().Where(where))
                .Take(Clamp(limit))
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

    private static IQueryable<SchemaSymbolRow> QueryDeclarationsByName(IQueryable<SchemaSymbolRow> query) =>
        query.OrderBy(row => row.Name).ThenBy(row => row.Path).ThenBy(row => row.Start);

    private IReadOnlyList<SymbolMetacode> Metacode(
        Func<IQueryable<SchemaMetacodeRow>, IQueryable<SchemaMetacodeRow>> shape, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return shape(context.SymbolMetacode.AsNoTracking())
                .Take(Clamp(limit))
                .ToList()
                .Select(row => new SymbolMetacode(
                    row.SymbolKey, row.Path, row.Kind, row.Name, row.Line, Decompress(row.Text), row.DocHash))
                .ToList();
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SymbolDeclaration Shape(SchemaSymbolRow row) =>
        new(row.SymbolKey, row.Name, row.Display, row.Signature, row.Kind, row.Accessibility, row.Modifiers,
            row.ContainerKey, row.ContainerName, row.Namespace, row.IsStatic, row.IsAbstract, row.IsVirtual,
            row.IsOverride, row.IsExtension, row.Arity, row.Summary, row.Project, row.Path, row.Start,
            row.Length, row.Line, row.Col, row.EndLine, row.EndCol, row.DocHash);

    private static IReadOnlyList<SymbolKindCount> Histogram(IQueryable<string> kinds)
    {
        return kinds
            .GroupBy(kind => kind)
            .Select(group => new { Kind = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Kind)
            .ToList()
            .Select(entry => new SymbolKindCount(entry.Kind, entry.Count))
            .ToList();
    }

    /// <summary>A LIKE pattern takes its own metacharacters: a query for <c>%</c> must not match everything.</summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// The pseudocode of a type repeats its members, and every member has its own row too: compressed,
    /// the duplication costs a fraction of the space it would as text.
    /// </summary>
    private static byte[] Compress(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    private static string Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int Clamp(int limit) => limit is < 1 ? 1 : limit > 5000 ? 5000 : limit;
}

/// <summary>One kind of use and how often it occurs.</summary>
public sealed record SymbolKindCount(string Kind, int Count);

/// <summary>A member that uses a symbol, and how.</summary>
public sealed record SymbolContainerCount(string ContainerKey, int Count, string Kinds);

/// <summary>A symbol used from inside one member, and how.</summary>
public sealed record SymbolTargetCount(string SymbolKey, string Name, int Count, string Kinds);

/// <summary>What the index holds, as one query answers it.</summary>
public sealed record SymbolIndexStatus(
    int Documents,
    int Symbols,
    int Declarations,
    int Usages,
    string? Newest,
    IReadOnlyList<SymbolKindCount> DeclarationKinds,
    IReadOnlyList<SymbolKindCount> UsageKinds);
