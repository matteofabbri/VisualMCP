using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Snitch.Storage.Schema;
using SchemaDocumentRow = Snitch.Storage.Schema.MetacodeDocumentRow;
using SchemaSourceRow = Snitch.Storage.Schema.SourceDocumentRow;
using SchemaTreeRow = Snitch.Storage.Schema.MetacodeTreeRow;

namespace Snitch.Metacode.Indexing;

/// <summary>
/// The solution's metacode in <c>.visualMCP/visualmcp.db</c>: one row per document (the freshness token
/// is its content hash plus its timestamp) and one row per declaration, holding the canonical
/// pseudocode and the structural facts computed over the model.
/// </summary>
/// <remarks>
/// A document is stored twice, on purpose. <c>source_document</c> keeps the file as its own language
/// writes it — the native side, which exists for every file of the solution; <c>mc_document</c> and
/// <c>mc_tree</c> keep what Snitch's model made of it, which exists for the languages a frontend can
/// read. A file in a language with no reader is therefore present in the store AND in the AST as a single
/// <c>file</c> node, rather than missing from both.
/// </remarks>
/// <remarks>
/// Every row carries its provenance: <c>source_path</c> is the file the content came from (the binary,
/// for decompiled code) and <c>decompiled</c> says which of the two it is. That is what lets a
/// decompiled assembly and the solution's own code share these tables without a query ever confusing
/// them.
/// </remarks>
/// <remarks>
/// It owns its connection, like the symbol index and the board that share the same file, and every
/// operation degrades to a no-op when SQLite cannot be used: the index is an optimisation, never a
/// dependency. The tree text is gzip-compressed because a type's text repeats its members, each of which
/// also has a row of its own.
/// </remarks>
public sealed class MetacodeIndexStore : IDisposable
{
    /// <summary>
    /// The path prefix of a document that has no file: the metacode of a decompiled assembly. Those rows
    /// live in the same tables as the solution's own code (so one query sees both), and the prefix is what
    /// tells a refresh not to prune them — they are not documents of the solution and never will be.
    /// </summary>
    public const string DecompiledPrefix = "assembly://";

    /// <summary>
    /// The same idea for a native binary recovered with Ghidra: one document per image, its functions as
    /// the declarations. The prefix keeps it out of the solution's prune for the same reason.
    /// </summary>
    public const string NativePrefix = "native://";

    /// <summary>
    /// A Java binary decompiled with JADX: one document per decompiled .java file, under
    /// <c>java://&lt;archive&gt;/&lt;package/Type.java&gt;</c>. It is kept out of the solution's prune like
    /// the other two, and it is told apart from them because its content is source recovered from a
    /// class file, not .NET metadata and not machine code.
    /// </summary>
    public const string JavaPrefix = "java://";

    /// <summary>Whether a stored path is a decompiled artifact rather than a document of the solution.</summary>
    public static bool IsDecompiledPath(string path) =>
        path.StartsWith(DecompiledPrefix, StringComparison.Ordinal)
        || path.StartsWith(NativePrefix, StringComparison.Ordinal)
        || path.StartsWith(JavaPrefix, StringComparison.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    /// <summary>The database the index is reading, or null when it is not open.</summary>
    public string? Path => _path;

    /// <summary>The content hash a document is stamped with; the same function the other stores use.</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Opens the database, creating the metacode tables. Returns false when SQLite cannot be used.</summary>
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
                    // Other owners of the same file (the store, the board, the symbol index) write too:
                    // wait for them instead of failing the query.
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

    /// <summary>Every indexed path with its freshness token, so a refresh can skip what did not change.</summary>
    public Dictionary<string, MetacodeDocumentState> Known()
    {
        var known = new Dictionary<string, MetacodeDocumentState>(StringComparer.OrdinalIgnoreCase);
        var connection = Open();
        if (connection is null) return known;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            var documents = context.MetacodeDocuments
                .AsNoTracking()
                .Select(document => new
                {
                    document.Path,
                    document.Hash,
                    document.Mtime,
                    HasSource = context.SourceDocuments.Any(source => source.Path == document.Path),
                })
                .ToList();

            foreach (var document in documents)
                known[document.Path] = new MetacodeDocumentState(document.Hash, document.Mtime)
                {
                    HasSource = document.HasSource,
                };

            // A document with no reader has only the native row, and it is still a document of the store.
            var natives = context.SourceDocuments
                .AsNoTracking()
                .Where(source => !context.MetacodeDocuments.Any(document => document.Path == source.Path))
                .Select(source => new { source.Path, source.Hash, source.Mtime })
                .ToList();

            foreach (var source in natives)
                known[source.Path] = new MetacodeDocumentState(source.Hash, source.Mtime) { HasSource = true };

            return known;
        }
        catch (SqliteException)
        {
            return known;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes one document's rows, replacing whatever that path held before. Null when the database is unavailable.</summary>
    public async Task<bool> ReplaceAsync(MetacodeDocumentFacts facts, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            using var transaction = context.Database.BeginTransaction();

            context.MetacodeNodes.Where(node => node.Path == facts.Path).ExecuteDelete();
            context.MetacodeTrees.Where(tree => tree.Path == facts.Path).ExecuteDelete();

            // Provenance is a property of the document, written into every row it owns: a query that
            // only reads mc_tree still knows where the content came from and whether it was read.
            var sourcePath = facts.SourcePath ?? facts.Path;

            context.MetacodeTrees.AddRange(facts.Rows
                .GroupBy(row => (row.Key, row.Path, row.Line, row.Name))
                .Select(group => group.Last())
                .Select(row => new SchemaTreeRow
                {
                    DeclarationKey = row.Key,
                    Path = row.Path,
                    Kind = row.Kind,
                    Name = row.Name,
                    Line = row.Line,
                    Col = row.Column,
                    EndLine = row.EndLine,
                    Cyclomatic = row.Cyclomatic,
                    Cognitive = row.Cognitive,
                    MaxNesting = row.MaxNesting,
                    Statements = row.Statements,
                    Branches = row.Branches,
                    Expressions = row.Expressions,
                    Text = Compress(row.Text),
                    DocHash = facts.Hash,
                    SourcePath = row.SourcePath.Length > 0 ? row.SourcePath : sourcePath,
                    Decompiled = row.Decompiled || facts.Decompiled,
                }));

            // The complete AST, node by node: one row per node of every declaration, in pre-order, so
            // the tree is a query (`ORDER BY node_id`) instead of a rendering that has to be parsed.
            // A solution's AST runs to hundreds of thousands of rows, so they are written with one
            // prepared command inside the same transaction: tracking that many entities would cost more
            // memory than the index itself.
            if (facts.Nodes.Count > 0)
            {
                using var node = connection.CreateCommand();
                node.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                node.CommandText = """
                    INSERT OR REPLACE INTO mc_node
                        (path, declaration_key, node_id, parent_id, slot, ordinal, depth, kind,
                         line, col, end_line, data, doc_hash, source_path, decompiled)
                    VALUES ($path, $key, $id, $parent, $slot, $ordinal, $depth, $kind,
                            $line, $col, $end, $data, $hash, $source, $decompiled);
                    """;

                var nodePath = node.Parameters.Add("$path", SqliteType.Text);
                var nodeKey = node.Parameters.Add("$key", SqliteType.Text);
                var nodeId = node.Parameters.Add("$id", SqliteType.Integer);
                var parent = node.Parameters.Add("$parent", SqliteType.Integer);
                var slot = node.Parameters.Add("$slot", SqliteType.Text);
                var ordinal = node.Parameters.Add("$ordinal", SqliteType.Integer);
                var depth = node.Parameters.Add("$depth", SqliteType.Integer);
                var nodeKind = node.Parameters.Add("$kind", SqliteType.Text);
                var nodeLine = node.Parameters.Add("$line", SqliteType.Integer);
                var nodeCol = node.Parameters.Add("$col", SqliteType.Integer);
                var nodeEnd = node.Parameters.Add("$end", SqliteType.Integer);
                var nodeData = node.Parameters.Add("$data", SqliteType.Text);
                var nodeHash = node.Parameters.Add("$hash", SqliteType.Text);
                var nodeSource = node.Parameters.Add("$source", SqliteType.Text);
                var nodeDecompiled = node.Parameters.Add("$decompiled", SqliteType.Integer);

                foreach (var ast in facts.Nodes)
                {
                    nodePath.Value = facts.Path;
                    nodeKey.Value = ast.DeclarationKey;
                    nodeId.Value = ast.NodeId;
                    parent.Value = ast.ParentId;
                    slot.Value = ast.Slot;
                    ordinal.Value = ast.Ordinal;
                    depth.Value = ast.Depth;
                    nodeKind.Value = ast.Kind;
                    nodeLine.Value = ast.Line;
                    nodeCol.Value = ast.Column;
                    nodeEnd.Value = ast.EndLine;
                    nodeData.Value = ast.Data;
                    nodeHash.Value = facts.Hash;
                    nodeSource.Value = sourcePath;
                    nodeDecompiled.Value = facts.Decompiled ? 1 : 0;
                    node.ExecuteNonQuery();
                }
            }

            var declarations = facts.Rows
                .GroupBy(row => (row.Key, row.Path, row.Line, row.Name))
                .Count();
            var nodes = facts.Nodes.Count > 0
                ? facts.Nodes.Count
                : facts.Rows.Sum(row => row.Statements + row.Expressions);

            var document = context.MetacodeDocuments.FirstOrDefault(row => row.Path == facts.Path);
            if (document is null)
            {
                context.MetacodeDocuments.Add(new SchemaDocumentRow
                {
                    Path = facts.Path,
                    Project = facts.Project,
                    Hash = facts.Hash,
                    Mtime = facts.Mtime,
                    Language = facts.Language,
                    Declarations = declarations,
                    Nodes = nodes,
                    Diagnostics = facts.Diagnostics,
                    IndexedAt = DateTimeOffset.UtcNow,
                    SourcePath = sourcePath,
                    Decompiled = facts.Decompiled,
                });
            }
            else
            {
                document.Project = facts.Project;
                document.Hash = facts.Hash;
                document.Mtime = facts.Mtime;
                document.Language = facts.Language;
                document.Declarations = declarations;
                document.Nodes = nodes;
                document.Diagnostics = facts.Diagnostics;
                document.IndexedAt = DateTimeOffset.UtcNow;
                document.SourcePath = sourcePath;
                document.Decompiled = facts.Decompiled;
            }

            // The native half of the record: the file as its own language writes it, next to the tree
            // the model produced from it. One transaction, so the two never disagree about a file.
            if (facts.Source is not null)
                WriteSource(context, facts);

            context.SaveChanges();
            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The native half of one document. Called inside <see cref="ReplaceAsync"/>'s transaction, so the
    /// text and the trees it produced never disagree about a file.
    /// </summary>
    private static void WriteSource(VisualMcpDbContext context, MetacodeDocumentFacts facts)
    {
        var source = facts.Source!;

        var document = context.SourceDocuments.FirstOrDefault(row => row.Path == facts.Path);
        if (document is null)
        {
            context.SourceDocuments.Add(new SchemaSourceRow
            {
                Path = facts.Path,
                Project = facts.Project,
                Language = facts.Language,
                Hash = facts.Hash,
                Mtime = facts.Mtime,
                Chars = source.Length,
                Lines = LineCount(source),
                Text = source,
                IndexedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        document.Project = facts.Project;
        document.Language = facts.Language;
        document.Hash = facts.Hash;
        document.Mtime = facts.Mtime;
        document.Chars = source.Length;
        document.Lines = LineCount(source);
        document.Text = source;
        document.IndexedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>How many lines the text spans: a count of separators, not a split, because it is only a column.</summary>
    private static int LineCount(string text)
    {
        var lines = 1;
        foreach (var character in text)
            if (character == '\n') lines++;
        return lines;
    }

    /// <summary>
    /// Renews a document's timestamp without touching its rows: the file was written again and the
    /// content hash says nothing changed, so the next pass can skip it on the timestamp alone. It stamps
    /// both halves — a document with no reader has only the native row, and would otherwise be re-read on
    /// every pass.
    /// </summary>
    public void Touch(string path, long mtime)
    {
        var connection = Open();
        if (connection is null || mtime == 0) return;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            context.MetacodeDocuments
                .Where(document => document.Path == path)
                .ExecuteUpdate(setters => setters.SetProperty(document => document.Mtime, mtime));
            context.SourceDocuments
                .Where(document => document.Path == path)
                .ExecuteUpdate(setters => setters.SetProperty(document => document.Mtime, mtime));
        }
        catch (SqliteException)
        {
            // The stamp is an optimisation; a failed update only costs one more hash next time.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a document that is no longer part of the solution. Returns how many were removed.</summary>
    public async Task<int> RemoveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return 0;

        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            using var transaction = context.Database.BeginTransaction();
            var removed = paths.Sum(path => DeletePath(context, path));

            transaction.Commit();
            return removed;
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

    /// <summary>
    /// Drops every document of the solution that a pass no longer finds. Decompiled artifacts
    /// (<c>assembly://</c>, <c>native://</c>) are never theirs to remove: only a new decompilation of the
    /// same binary can make them stale. Returns how many documents were removed.
    /// </summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        var stale = new List<string>();
        foreach (var path in Known().Keys)
        {
            if (IsDecompiledPath(path)) continue;
            if (!paths.Contains(path)) stale.Add(path);
        }

        if (stale.Count == 0) return 0;

        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            using var transaction = context.Database.BeginTransaction();
            foreach (var path in stale)
                DeletePath(context, path);

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

    /// <summary>
    /// Drops every document under <paramref name="prefix"/> that is not in <paramref name="keep"/>: the
    /// types an assembly no longer contains after it was decompiled again. Returns how many documents were
    /// removed.
    /// </summary>
    public async Task<int> RemoveUnderAsync(
        string prefix, IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            // StartsWith is the framework's prefix comparison; it escapes the wildcards a hand-written
            // LIKE would have treated as patterns.
            var candidates = context.MetacodeDocuments
                .AsNoTracking()
                .Where(document => document.Path.StartsWith(prefix))
                .Select(document => document.Path)
                .ToList();

            var stale = candidates
                .Where(path => !keep.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (stale.Count == 0) return 0;

            using var transaction = context.Database.BeginTransaction();
            foreach (var path in stale)
                DeletePath(context, path);

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

    /// <summary>
    /// The content hash stored for the first document of a decompiled assembly — the assembly's MVID —
    /// or null when the store has never seen that assembly. It is the freshness token a re-decompilation
    /// compares against, so an unchanged binary is not decompiled twice.
    /// </summary>
    public string? DecompiledHash(string assemblyFileName) => DecompiledHash(DecompiledPrefix, assemblyFileName);

    /// <summary>
    /// The same question for any decompiled artifact: <paramref name="prefix"/> says which family
    /// (<c>assembly://</c>, <c>java://</c>) and <paramref name="fileName"/> the binary. The identity is
    /// whatever the decompiler keyed the rows on — the MVID, the SHA-256 of a Java archive.
    /// </summary>
    public string? DecompiledHash(string prefix, string fileName)
    {
        var connection = Open();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var path = prefix + fileName + "/";

            return context.MetacodeDocuments
                .AsNoTracking()
                .Where(document => document.Path.StartsWith(path))
                .OrderBy(document => document.Path)
                .Select(document => document.Hash)
                .FirstOrDefault();
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

    /// <summary>
    /// Which binaries the store holds code for: the file name, the identity its rows are keyed on (the
    /// MVID, or the SHA-256 for a native image), and how many documents each one contributed. It is what
    /// makes a decompiled binary discoverable — otherwise the only way to learn what is there would be to
    /// guess a type or function name.
    /// </summary>
    public IReadOnlyList<DecompiledAssemblyRow> DecompiledAssemblies()
    {
        var found = new Dictionary<string, (string Identity, int Documents, string Kind)>(StringComparer.OrdinalIgnoreCase);

        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var rows = context.MetacodeDocuments
                .AsNoTracking()
                .Where(document =>
                    document.Path.StartsWith(DecompiledPrefix)
                    || document.Path.StartsWith(NativePrefix)
                    || document.Path.StartsWith(JavaPrefix))
                .OrderBy(document => document.Path)
                .Select(document => new { document.Path, document.Hash })
                .ToList();

            foreach (var row in rows)
            {
                var path = row.Path;
                var identity = row.Hash;

                // assembly://<file>/<type>, java://<file>/<unit> and native://<file>: the file name is
                // everything before the first '/' — and for a native document there is no '/', so the
                // rest is the name itself.
                var (prefix, kind) = path.StartsWith(NativePrefix, StringComparison.Ordinal)
                    ? (NativePrefix, "native")
                    : path.StartsWith(JavaPrefix, StringComparison.Ordinal)
                        ? (JavaPrefix, "java")
                        : (DecompiledPrefix, "managed");

                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/');
                var assembly = slash > 0 ? rest[..slash] : rest;
                if (assembly.Length == 0) continue;

                found[assembly] = found.TryGetValue(assembly, out var existing)
                    ? (identity, existing.Documents + 1, kind)
                    : (identity, 1, kind);
            }

            return found
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new DecompiledAssemblyRow(entry.Key, entry.Value.Identity, entry.Value.Documents)
                {
                    Kind = entry.Value.Kind,
                })
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

    /// <summary>The stored rows of one file, in source order.</summary>
    public IReadOnlyList<MetacodeTreeRow> InPath(string path, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var rows = context.MetacodeTrees
                .AsNoTracking()
                .Where(tree => tree.Path == path)
                .OrderBy(tree => tree.Line)
                .ThenBy(tree => tree.Col)
                .Take(Clamp(limit))
                .ToList();

            return rows.Select(Shape).ToList();
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
    /// The stored AST of one declaration, in pre-order. The rows are the tree: a node's parent is the row
    /// <c>parent_id</c> names, the property that holds it is <c>slot</c>, and its position there is
    /// <c>ordinal</c>.
    /// </summary>
    public IReadOnlyList<MetacodeAstNode> Ast(string path, string declarationKey, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.MetacodeNodes
                .AsNoTracking()
                .Where(node => node.Path == path && node.DeclarationKey == declarationKey)
                .OrderBy(node => node.NodeId)
                .Take(Clamp(limit))
                .Select(node => new
                {
                    node.DeclarationKey,
                    node.NodeId,
                    node.ParentId,
                    node.Slot,
                    node.Ordinal,
                    node.Depth,
                    node.Kind,
                    node.Line,
                    node.Col,
                    node.EndLine,
                    node.Data,
                })
                .ToList()
                .Select(node => new MetacodeAstNode(
                    node.DeclarationKey, node.NodeId, node.ParentId, node.Slot, node.Ordinal, node.Depth,
                    node.Kind, node.Line, node.Col, node.EndLine, node.Data))
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

    /// <summary>How many nodes the store holds for one declaration, so a capped read can say it was cut.</summary>
    public int AstCount(string path, string declarationKey)
    {
        var connection = Open();
        if (connection is null) return 0;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.MetacodeNodes.Count(node => node.Path == path && node.DeclarationKey == declarationKey);
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

    /// <summary>Rows whose declaration key or name matches exactly; a qualified name matches the end of the key.</summary>
    public IReadOnlyList<MetacodeTreeRow> Resolve(string query, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var needle = $"%{query}%";

            var rows = context.MetacodeTrees
                .AsNoTracking()
                .Where(tree =>
                    tree.DeclarationKey == query ||
                    tree.Name == query ||
                    EF.Functions.Like(tree.DeclarationKey, needle))
                .OrderBy(tree => tree.Line)
                .ThenBy(tree => tree.Col)
                .Take(Clamp(limit))
                .ToList();

            return rows.Select(Shape).ToList();
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

    /// <summary>The callables, worst first — the complexity report.</summary>
    public IReadOnlyList<MetacodeTreeRow> Complexity(string? project, string? kind, int minCyclomatic, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            var callables = new[]
            {
                "method", "constructor", "destructor", "operator", "function",
                "local-function", "property", "indexer",
            };

            var query = context.MetacodeTrees
                .AsNoTracking()
                .Where(tree => tree.Cyclomatic >= minCyclomatic && callables.Contains(tree.Kind));

            if (project is not null)
                query = query.Where(tree =>
                    context.MetacodeDocuments.Any(document => document.Path == tree.Path && document.Project == project));
            if (kind is not null)
                query = query.Where(tree => tree.Kind == kind);

            var rows = query
                .OrderByDescending(tree => tree.Cyclomatic)
                .ThenByDescending(tree => tree.Cognitive)
                .ThenBy(tree => tree.Name)
                .ThenBy(tree => tree.Line)
                .Take(Clamp(limit))
                .ToList();

            return rows.Select(Shape).ToList();
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

    public MetacodeIndexStatus Status()
    {
        var connection = Open();
        if (connection is null) return new MetacodeIndexStatus(0, 0, 0, 0, 0, 0, null, _path, null);

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);

            var functionKinds = new[]
            {
                "method", "constructor", "destructor", "operator", "function", "local-function",
            };
            var typeKinds = new[] { "class", "struct", "interface", "enum", "record", "union", "trait" };

            var documents = context.MetacodeDocuments.Count();
            // A file no frontend reads is stored as one `file` node: it is a document of the store, not a
            // declaration of anything, so it is counted apart instead of inflating this number.
            var declarations = context.MetacodeTrees.Count(tree => tree.Kind != "file");
            var functions = context.MetacodeTrees.Count(tree => functionKinds.Contains(tree.Kind));
            var types = context.MetacodeTrees.Count(tree => typeKinds.Contains(tree.Kind));
            var max = context.MetacodeTrees.Select(tree => (int?)tree.Cyclomatic).Max() ?? 0;
            var average = (int)(context.MetacodeTrees
                .Where(tree => tree.Cyclomatic > 0)
                .Select(tree => (int?)tree.Cyclomatic)
                .Average() ?? 0);
            var sources = context.SourceDocuments.Count();
            var nodes = context.MetacodeNodes.Count();
            var decompiled = context.MetacodeDocuments.Count(document => document.Decompiled);
            var files = context.MetacodeTrees.Count(tree => tree.Kind == "file");

            // SQLite cannot ORDER BY a DateTimeOffset through EF; the timestamps are few enough to rank here.
            var indexedAt = context.MetacodeDocuments
                .AsNoTracking()
                .Select(document => document.IndexedAt)
                .ToList();
            var newest = indexedAt.Count == 0 ? null : indexedAt.Max().ToUniversalTime().ToString("O");

            return new MetacodeIndexStatus(
                documents, declarations, functions, types, max, average, newest, _path, null)
            {
                Sources = sources,
                Nodes = nodes,
                Decompiled = decompiled,
                Files = files,
            };
        }
        catch (SqliteException)
        {
            return new MetacodeIndexStatus(0, 0, 0, 0, 0, 0, null, _path, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many declarations the store holds for one path — used by the tests to prove an edit replaced them.</summary>
    public int CountInPath(string path)
    {
        var connection = Open();
        if (connection is null) return 0;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            return context.MetacodeTrees.Count(tree => tree.Path == path);
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

    /// <summary>
    /// The native text stored for a path, with the language tag it was read as. This half of the record
    /// exists for every document of the solution, including the ones no frontend could read.
    /// </summary>
    public (string Language, string Text)? SourceOf(string path)
    {
        var connection = Open();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            using var context = VisualMcpSchema.CreateContext(connection);
            var source = context.SourceDocuments
                .AsNoTracking()
                .Where(document => document.Path == path)
                .Select(document => new { document.Language, document.Text })
                .FirstOrDefault();

            return source is null ? null : (source.Language, source.Text);
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

    private SqliteConnection? Open() => _connection;

    /// <summary>Removes everything one path owns; the caller wraps the calls in its transaction.</summary>
    private static int DeletePath(VisualMcpDbContext context, string path)
    {
        var removed = 0;
        removed += context.MetacodeNodes.Where(row => row.Path == path).ExecuteDelete();
        removed += context.MetacodeTrees.Where(row => row.Path == path).ExecuteDelete();
        removed += context.MetacodeDocuments.Where(row => row.Path == path).ExecuteDelete();
        removed += context.SourceDocuments.Where(row => row.Path == path).ExecuteDelete();
        return removed;
    }

    /// <summary>One stored declaration in the shape the callers use, with its text decompressed.</summary>
    private static MetacodeTreeRow Shape(SchemaTreeRow row) =>
        new(row.DeclarationKey, row.Path, row.Kind, row.Name, row.Line, row.Col, row.EndLine,
            row.Cyclomatic, row.Cognitive, row.MaxNesting, row.Statements, row.Branches, row.Expressions,
            Decompress(row.Text))
        {
            SourcePath = row.SourcePath,
            Decompiled = row.Decompiled,
        };

    private static int Clamp(int limit) => limit <= 0 ? 1 : Math.Min(limit, 5000);

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
}
