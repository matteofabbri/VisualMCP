using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Snitch.Metacode.Analysis;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Generation;
using Snitch.Metacode.Rendering;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Metacode.Indexing;

/// <summary>
/// The solution's metacode as a persisted, queryable artifact: build it when the solution opens, keep it
/// in step with the files that change, read the complexity the model gives each callable, and generate
/// C# from a stored tree (or from pseudocode).
/// </summary>
public interface IMetacodeIndexService
{
    /// <summary>
    /// Reads every file of the solution whose content changed and stores both halves of it: the file as
    /// its own language writes it (always) and the metacode of the languages a frontend can read. The
    /// document set is the whole solution, not a <c>.cs</c> filter: the Roslyn documents, every other text
    /// file found on disk (Razor pages and components are content items and never reach the workspace),
    /// and a text file no frontend reads becomes a single <c>file</c> node instead of being left out.
    /// <paramref name="force"/> re-reads even what looks unchanged. Incremental by design: an unchanged
    /// file is skipped on its timestamp alone, and a touched-but-identical file on its hash.
    /// </summary>
    Task<MetacodeBuildReport> BuildAsync(bool force = false, string? projectName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The cheap pass the background loop runs: same work as <see cref="BuildAsync"/> without
    /// <paramref name="force"/>, so a file saved since the last pass is picked up and nothing else is read.
    /// </summary>
    Task<MetacodeBuildReport> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the background pass that keeps the index in step with saved files: every
    /// <paramref name="interval"/> it runs <see cref="RefreshAsync"/>, which reads a file only when its
    /// timestamp (then its content hash) moved. Idempotent — a second call does nothing; the loop stops
    /// when <paramref name="stopping"/> is cancelled or the service is disposed.
    /// </summary>
    void StartWatching(CancellationToken stopping = default, TimeSpan? interval = null);

    /// <summary>What the store holds: documents, declarations, callables, types and the complexity range.</summary>
    MetacodeIndexStatus Status();

    /// <summary>The stored trees of a symbol (or of every declaration at a path), in source order.</summary>
    IReadOnlyList<MetacodeTreeRow> Trees(string? symbol = null, string? path = null, int limit = 50);

    /// <summary>
    /// The COMPLETE AST the store holds, node by node: every declaration that matches
    /// <paramref name="symbol"/> (a name, a qualified name or a declaration key) or lives at
    /// <paramref name="path"/>, nested the way the model holds it. At most <paramref name="maxNodes"/>
    /// nodes come back in one call, and the result says when the cap cut it.
    /// </summary>
    MetacodeAstResult Ast(string? symbol = null, string? path = null, int maxNodes = 2000);

    /// <summary>The callables ranked by the cyclomatic complexity of their metacode.</summary>
    IReadOnlyList<MetacodeComplexityRow> Complexity(
        int minCyclomatic = 1, string? projectName = null, string? kind = null, int limit = 50);

    /// <summary>
    /// Metacode in, C# out. The source is <paramref name="text"/> when given, otherwise the stored tree of
    /// <paramref name="symbol"/>, otherwise the rows stored for <paramref name="path"/> — and if the store
    /// has none for that path, the file itself is translated. Only <c>csharp</c> is a target today.
    /// </summary>
    MetacodeGenerateResult Generate(
        string? symbol = null,
        string? path = null,
        string? text = null,
        string? language = null,
        bool includeUsings = true,
        string? usings = null);

    /// <summary>
    /// Stores a decompilation as metacode trees, so decompiled code is queryable exactly like the
    /// solution's own code: one document per type, under <c>assembly://&lt;file name&gt;/&lt;type&gt;</c>.
    /// The rows are keyed by the assembly's MVID, so decompiling the same binary again replaces them
    /// instead of duplicating them, the solution's own refresh never prunes them, and every row records
    /// the binary it came from with the decompiled flag set.
    /// </summary>
    Task<MetacodeBuildReport> IndexDecompilationAsync(
        MetacodeDecompilation decompilation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores what Ghidra recovered from a native binary as one metacode document under
    /// <c>native://&lt;file name&gt;</c> — its functions as the declarations, in the same tables and with
    /// the same AST as any other code, flagged as decompiled and pointing at the binary.
    /// </summary>
    Task<MetacodeBuildReport> IndexNativeAsync(
        string binaryPath, string hash, MetacodeDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the Java sources JADX recovered from a class/dex/jar/apk, one document per decompiled
    /// <c>.java</c> file under <c>java://&lt;archive&gt;/&lt;unit&gt;</c>. The rows are keyed on the
    /// archive's SHA-256, so decompiling the same archive again replaces them instead of duplicating
    /// them, and every row records the archive it came from with the decompiled flag set.
    /// </summary>
    Task<MetacodeBuildReport> IndexJavaAsync(
        string archivePath, string hash, IReadOnlyList<JavaSourceUnit> sources, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes a given set of files — the folder a binary was decompiled from, or anything else a caller
    /// names — with the same freshness rules as a build pass. A file no frontend reads becomes a
    /// <c>file</c> node; a file that is not text at all is skipped.
    /// </summary>
    Task<MetacodeBuildReport> IndexFilesAsync(
        IReadOnlyList<string> paths, string? projectName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The MVID the store holds for a decompiled assembly (the name of the file, e.g.
    /// <c>Newtonsoft.Json.dll</c>), or null when it was never stored. A caller compares it with the
    /// binary's current MVID to decide whether decompiling again would tell it anything new.
    /// </summary>
    string? DecompiledAssemblyHash(string assemblyFileName);

    /// <summary>
    /// The SHA-256 the store holds for a decompiled Java archive (the name of the file, e.g.
    /// <c>app.jar</c>), or null when it was never stored. The Java counterpart of
    /// <see cref="DecompiledAssemblyHash"/>: the same fresh-archive check, keyed on the content hash
    /// because a jar has no MVID.
    /// </summary>
    string? DecompiledJavaHash(string archiveFileName);

    /// <summary>Which binaries the store holds metacode for — the answer to "what did I decompile?".</summary>
    IReadOnlyList<DecompiledAssemblyRow> DecompiledAssemblies();
}

/// <summary>
/// The implementation. Translation is purely syntactic (the C# frontend parses text; it needs no
/// compilation), which is what lets a refresh read a saved file straight from disk even when the
/// workspace snapshot predates the save.
/// </summary>
public sealed class MetacodeIndexService : IMetacodeIndexService, IDisposable
{
    private const string CSharp = "cs";
    private const string VisualBasic = "vb";

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "struct", "interface", "enum", "record", "union", "trait",
    };

    private static readonly HashSet<string> FunctionKinds = new(StringComparer.Ordinal)
    {
        "method", "constructor", "destructor", "operator", "function", "local-function",
    };

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore _store;
    private readonly MetacodeIndexStore _index = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>
    /// The text/binary verdict per path, with the timestamp it was made at. The solution walk runs every
    /// couple of seconds: opening every file of the tree to ask again would cost more than the pass.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Mtime, bool Text)> _textFiles = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _watching;

    public MetacodeIndexService(IRoslynWorkspace workspace, IVisualMcpStore store)
    {
        _workspace = workspace;
        _store = store;
    }

    // ── build / refresh ────────────────────────────────────────────────────────────────

    public Task<MetacodeBuildReport> BuildAsync(
        bool force = false, string? projectName = null, CancellationToken cancellationToken = default) =>
        RunAsync(force, projectName, cancellationToken);

    public Task<MetacodeBuildReport> RefreshAsync(CancellationToken cancellationToken = default) =>
        RunAsync(force: false, projectName: null, cancellationToken);

    public void StartWatching(CancellationToken stopping = default, TimeSpan? interval = null)
    {
        var created = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        // Idempotent without a lock: the first cancellation source wins, every later caller disposes its own.
        if (Interlocked.CompareExchange(ref _watching, created, null) is not null)
        {
            created.Dispose();
            return;
        }

        var period = interval ?? TimeSpan.FromSeconds(2);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(created.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await RefreshAsync(created.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // A tick that failed changes nothing: the next one re-reads the same files.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The host is shutting down, or the service was disposed.
            }
        }, CancellationToken.None);
    }

    private async Task<MetacodeBuildReport> RunAsync(bool force, string? projectName, CancellationToken cancellationToken)
    {
        // The background loop and a tool call can meet here; translating a file twice is pure waste.
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(force, projectName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private async Task<MetacodeBuildReport> RunCoreAsync(bool force, string? projectName, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var database = _store.EnsureDatabase();
        if (database is null)
            return Failure("No solution is loaded, so there is no metacode database. Call load_solution first.", stopwatch);

        if (!_index.Open(database))
            return Failure("The solution database could not be opened (is the solution folder writable?).", stopwatch, database);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return Failure("No C# solution could be auto-located from the working directory.", stopwatch, database);

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return Failure($"No project '{projectName}'.", stopwatch, database);

        var documents = Sources(projects, solution.FilePath);

        var known = _index.Known();
        int scanned = 0, indexed = 0, skipped = 0, unsupported = 0, declarations = 0, functions = 0, types = 0, diagnostics = 0;
        var failures = new ConcurrentQueue<string>();

        await Parallel.ForEachAsync(
            documents,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                Interlocked.Increment(ref scanned);

                try
                {
                    var outcome = await IndexOneAsync(item, force, known, token).ConfigureAwait(false);
                    if (outcome.Skipped)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var facts = outcome.Facts!;
                    if (!facts.HasReader)
                    {
                        // No frontend reads the language: the file is in the store (and in the AST) as a
                        // single `file` node, not as code.
                        Interlocked.Increment(ref unsupported);
                        return;
                    }

                    Interlocked.Increment(ref indexed);
                    Interlocked.Add(ref declarations, facts.Rows.Count);
                    Interlocked.Add(ref diagnostics, facts.Diagnostics);
                    foreach (var row in facts.Rows)
                    {
                        if (FunctionKinds.Contains(row.Kind)) Interlocked.Increment(ref functions);
                        if (TypeKinds.Contains(row.Kind)) Interlocked.Increment(ref types);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Enqueue($"{Path.GetFileName(item.Path)}: {exception.Message}");
                }
            }).ConfigureAwait(false);

        var removed = 0;
        if (projectName is null)
        {
            var present = documents.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = known.Keys
                // A decompiled assembly is not a document of the solution: its rows belong to the binary,
                // and only a new decompilation of that binary can make them stale.
                .Where(path => !MetacodeIndexStore.IsDecompiledPath(path))
                .Where(path => !present.Contains(path))
                .ToList();
            removed = await _index.RemoveAsync(stale, cancellationToken).ConfigureAwait(false);
        }

        var status = _index.Status();
        return new MetacodeBuildReport(
            scanned, indexed, skipped, removed, failures.Count, failures.Take(5).ToList(),
            declarations, functions, types, diagnostics,
            stopwatch.ElapsedMilliseconds, database, _workspace.LoadedSolutionPath)
        {
            Documents = status.Documents,
            Sources = status.Sources,
            Unsupported = unsupported,
            Nodes = status.Nodes,
        };
    }

    /// <summary>
    /// One file, from the freshness check to the committed rows. The build pass runs it in parallel; a
    /// folder sweep runs it in sequence. <see cref="IndexOutcome.Skipped"/> is the cheap answer: the file
    /// was not touched, or was touched but its content hash is the one the store already holds.
    /// </summary>
    private async Task<IndexOutcome> IndexOneAsync(
        IndexItem item, bool force, IReadOnlyDictionary<string, MetacodeDocumentState> known, CancellationToken cancellationToken)
    {
        var path = item.Path;
        var stat = Stat(path);
        known.TryGetValue(path, out var state);

        // Fast path: the file was not touched since the last pass and both halves are stored,
        // so its stored hash still holds.
        if (!force && state is { HasSource: true } && stat.Mtime != 0 && state.Mtime == stat.Mtime)
            return IndexOutcome.Unchanged;

        var (text, mtime) = await ReadAsync(item, path, cancellationToken).ConfigureAwait(false);
        var hash = MetacodeIndexStore.ContentHash(text);

        if (!force && state is { HasSource: true } && string.Equals(state.Hash, hash, StringComparison.Ordinal))
        {
            // Touched but identical (a save that changed nothing): keep the rows, renew the stamp.
            _index.Touch(path, mtime);
            return IndexOutcome.Unchanged;
        }

        var facts = Translate(text, item.Project, item.Language, path, hash, mtime);
        if (!await _index.ReplaceAsync(facts, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("the database refused the write");

        return new IndexOutcome(facts);
    }

    /// <summary>The result of indexing one file: the facts when anything was written, nothing when not.</summary>
    private readonly record struct IndexOutcome(MetacodeDocumentFacts? Facts)
    {
        public bool Skipped => Facts is null;

        public static IndexOutcome Unchanged => new(null);
    }

    /// <summary>
    /// Indexes a given set of files — the folder a binary was decompiled from, or anything else a caller
    /// names — with the same freshness rules as a build pass. Files that are not text are left alone.
    /// </summary>
    public async Task<MetacodeBuildReport> IndexFilesAsync(
        IReadOnlyList<string> paths, string? projectName = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var database = _store.EnsureDatabase();
        if (database is null)
            return Failure("No solution is loaded, so there is no metacode database. Call load_solution first.", stopwatch);

        if (!_index.Open(database))
            return Failure("The solution database could not be opened (is the solution folder writable?).", stopwatch, database);

        var known = _index.Known();
        int scanned = 0, indexed = 0, skipped = 0, unsupported = 0, declarations = 0, functions = 0, types = 0, diagnostics = 0;
        var failures = new List<string>();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            try
            {
                var outcome = await IndexOneAsync(
                        new IndexItem(path, projectName ?? string.Empty, WorkspaceSources.LanguageTag(path), null),
                        force: false, known, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Skipped)
                {
                    skipped++;
                    continue;
                }

                var facts = outcome.Facts!;
                if (!facts.HasReader) unsupported++;
                else indexed++;

                declarations += facts.Rows.Count(row => row.Kind != "file");
                diagnostics += facts.Diagnostics;
                foreach (var row in facts.Rows)
                {
                    if (FunctionKinds.Contains(row.Kind)) functions++;
                    if (TypeKinds.Contains(row.Kind)) types++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var status = _index.Status();
        return new MetacodeBuildReport(
            scanned, indexed, skipped, 0, failures.Count, failures.Take(5).ToList(),
            declarations, functions, types, diagnostics,
            stopwatch.ElapsedMilliseconds, database, _workspace.LoadedSolutionPath)
        {
            Documents = status.Documents,
            Sources = status.Sources,
            Unsupported = unsupported,
            Nodes = status.Nodes,
        };
    }

    private static MetacodeBuildReport Failure(string error, Stopwatch stopwatch, string? database = null) =>
        new(0, 0, 0, 0, 0, Array.Empty<string>(), 0, 0, 0, 0,
            stopwatch.ElapsedMilliseconds, database, null, error);

    // ── decompiled assemblies ──────────────────────────────────────────────────────────

    public async Task<MetacodeBuildReport> IndexDecompilationAsync(
        MetacodeDecompilation decompilation, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var database = _store.EnsureDatabase();
        if (database is null)
            return Failure("No solution is loaded, so there is no metacode database. Call load_solution first.", stopwatch);

        if (!_index.Open(database))
            return Failure("The solution database could not be opened (is the solution folder writable?).", stopwatch, database);

        if (decompilation.IsError) return Failure(decompilation.Error!, stopwatch, database);
        if (decompilation.Types.Count == 0)
            return Failure("The decompilation produced no type to store (everything was hidden or failed).", stopwatch, database);

        var assembly = Path.GetFileName(decompilation.Assembly);
        var identity = decompilation.Mvid
                       ?? MetacodeIndexStore.ContentHash(decompilation.Assembly + "|" + decompilation.TotalTypes);
        var project = "decompiled:" + assembly;

        int indexed = 0, declarations = 0, functions = 0, types = 0, diagnostics = 0;
        var failures = new List<string>();
        var written = new List<string>();

        foreach (var type in decompilation.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = DecompiledPath(assembly, type.FullName);
            var collected = MetacodeDeclarations.Collect(type.Document, path);

            // The whole assembly shares one freshness token — its MVID — so DecompiledHash can answer
            // "was this exact binary stored?" from any of its rows, and a new build replaces them all.
            // The provenance is the binary itself: the path is a store key that names no file on disk.
            var facts = new MetacodeDocumentFacts(
                path, project, identity, 0, CSharp, collected.Rows, type.Document.Diagnostics.Count)
            {
                Nodes = collected.Nodes,
                SourcePath = FullPathOf(decompilation.Assembly),
                Decompiled = true,
            };

            if (!await _index.ReplaceAsync(facts, cancellationToken).ConfigureAwait(false))
            {
                failures.Add($"{type.FullName}: the database refused the write");
                continue;
            }

            written.Add(path);
            indexed++;
            declarations += collected.Rows.Count;
            diagnostics += facts.Diagnostics;
            foreach (var row in collected.Rows)
            {
                if (FunctionKinds.Contains(row.Kind)) functions++;
                if (TypeKinds.Contains(row.Kind)) types++;
            }
        }

        // A complete decompilation knows every type of the assembly, so a type that disappeared from the
        // binary can be dropped. A paged one only knows its page, and must leave the rest alone.
        var removed = 0;
        if (decompilation.Matched <= decompilation.Types.Count + decompilation.Hidden + decompilation.FailedTypes)
        {
            removed = await _index
                .RemoveUnderAsync(MetacodeIndexStore.DecompiledPrefix + assembly + "/", written, cancellationToken)
                .ConfigureAwait(false);
        }

        var status = _index.Status();
        return new MetacodeBuildReport(
            decompilation.Types.Count, indexed, 0, removed, failures.Count, failures.Take(5).ToList(),
            declarations, functions, types, diagnostics,
            stopwatch.ElapsedMilliseconds, database, _workspace.LoadedSolutionPath)
        {
            Documents = status.Documents,
            Nodes = status.Nodes,
        };
    }

    public string? DecompiledAssemblyHash(string assemblyFileName)
    {
        if (string.IsNullOrWhiteSpace(assemblyFileName) || !TryOpen()) return null;
        return _index.DecompiledHash(assemblyFileName.Trim());
    }

    public string? DecompiledJavaHash(string archiveFileName)
    {
        if (string.IsNullOrWhiteSpace(archiveFileName) || !TryOpen()) return null;
        return _index.DecompiledHash(MetacodeIndexStore.JavaPrefix, archiveFileName.Trim());
    }

    /// <summary>
    /// Stores the functions Ghidra recovered from a native binary as one metacode document, so a native
    /// image is queryable exactly like a managed assembly or the solution's own code: same tables, same
    /// AST, same complexity. The path is <c>native://&lt;file&gt;</c>, the <em>source path</em> is the
    /// binary on disk, and every row is flagged as decompiled.
    /// </summary>
    public async Task<MetacodeBuildReport> IndexNativeAsync(
        string binaryPath,
        string hash,
        MetacodeDocument document,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var database = _store.EnsureDatabase();
        if (database is null)
            return Failure("No solution is loaded, so there is no metacode database. Call load_solution first.", stopwatch);

        if (!_index.Open(database))
            return Failure("The solution database could not be opened (is the solution folder writable?).", stopwatch, database);

        var assembly = Path.GetFileName(binaryPath);
        var path = MetacodeIndexStore.NativePrefix + assembly;
        var collected = MetacodeDeclarations.Collect(document, path);

        var facts = new MetacodeDocumentFacts(
            path, "decompiled:" + assembly, hash, 0, "native", collected.Rows, document.Diagnostics.Count)
        {
            Nodes = collected.Nodes,
            SourcePath = FullPathOf(binaryPath),
            Decompiled = true,
        };

        if (!await _index.ReplaceAsync(facts, cancellationToken).ConfigureAwait(false))
            return Failure($"The database refused the {collected.Rows.Count} recovered function(s) of {assembly}.", stopwatch, database);

        int functions = 0, types = 0;
        foreach (var row in collected.Rows)
        {
            if (FunctionKinds.Contains(row.Kind)) functions++;
            if (TypeKinds.Contains(row.Kind)) types++;
        }

        var status = _index.Status();
        return new MetacodeBuildReport(
            1, 1, 0, 0, 0, Array.Empty<string>(),
            collected.Rows.Count, functions, types, document.Diagnostics.Count,
            stopwatch.ElapsedMilliseconds, database, _workspace.LoadedSolutionPath)
        {
            Documents = status.Documents,
            Nodes = status.Nodes,
        };
    }

    public IReadOnlyList<DecompiledAssemblyRow> DecompiledAssemblies() =>
        TryOpen() ? _index.DecompiledAssemblies() : [];

    /// <summary>
    /// Stores the Java sources JADX recovered from a class/dex/jar/apk: one document per decompiled
    /// <c>.java</c> file, so the archive is queryable exactly like a managed assembly. The path is
    /// <c>java://&lt;archive&gt;/&lt;unit&gt;</c>, the <em>source path</em> is the archive on disk, and
    /// every row is flagged as decompiled. The Java text is kept too, in <c>source_document</c>, because
    /// unlike an ILSpy type it is a file a reader can look at.
    /// </summary>
    public async Task<MetacodeBuildReport> IndexJavaAsync(
        string archivePath,
        string hash,
        IReadOnlyList<JavaSourceUnit> sources,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var database = _store.EnsureDatabase();
        if (database is null)
            return Failure("No solution is loaded, so there is no metacode database. Call load_solution first.", stopwatch);

        if (!_index.Open(database))
            return Failure("The solution database could not be opened (is the solution folder writable?).", stopwatch, database);

        if (sources.Count == 0)
            return Failure("JADX produced no Java source to store.", stopwatch, database);

        var archive = Path.GetFileName(archivePath);
        var project = "decompiled:" + archive;
        var sourcePath = FullPathOf(archivePath);

        int indexed = 0, empty = 0, declarations = 0, functions = 0, types = 0, diagnostics = 0;
        var failures = new List<string>();
        var written = new List<string>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = JavaPath(archive, source.RelativePath);
            var collected = MetacodeDeclarations.Collect(source.Document, path);
            if (collected.Rows.Count == 0)
            {
                // A decompiled unit the reader could place nothing in is not a document of the store.
                empty++;
                continue;
            }

            var facts = new MetacodeDocumentFacts(
                path, project, hash, 0, "java", collected.Rows, source.Document.Diagnostics.Count)
            {
                Nodes = collected.Nodes,
                SourcePath = sourcePath,
                Decompiled = true,
                Source = source.Text,
            };

            if (!await _index.ReplaceAsync(facts, cancellationToken).ConfigureAwait(false))
            {
                failures.Add($"{source.RelativePath}: the database refused the write");
                continue;
            }

            written.Add(path);
            indexed++;
            declarations += collected.Rows.Count;
            diagnostics += facts.Diagnostics;
            foreach (var row in collected.Rows)
            {
                if (FunctionKinds.Contains(row.Kind)) functions++;
                if (TypeKinds.Contains(row.Kind)) types++;
            }
        }

        // A complete JADX run knows every source of the archive, so a unit that disappeared from the
        // decompilation (a class removed from the jar) can be dropped. A capped run does not.
        var removed = empty == 0
            ? await _index
                .RemoveUnderAsync(MetacodeIndexStore.JavaPrefix + archive + "/", written, cancellationToken)
                .ConfigureAwait(false)
            : 0;

        var status = _index.Status();
        return new MetacodeBuildReport(
            sources.Count, indexed, empty, removed, failures.Count, failures.Take(5).ToList(),
            declarations, functions, types, diagnostics,
            stopwatch.ElapsedMilliseconds, database, _workspace.LoadedSolutionPath)
        {
            Documents = status.Documents,
            Nodes = status.Nodes,
        };
    }

    /// <summary>
    /// The store key of one decompiled Java unit: the archive names the binary, the unit's path inside
    /// the archive (its package path) names the source. It is a key, never opened, so each segment is
    /// sanitised on its own — the separators are structure and must survive.
    /// </summary>
    private static string JavaPath(string archive, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = segments.Select(segment =>
        {
            var clean = segment;
            foreach (var invalid in Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']))
                clean = clean.Replace(invalid, '_');

            return clean.Length == 0 ? "_" : clean;
        });

        return MetacodeIndexStore.JavaPrefix + archive + "/" + string.Join("/", safe);
    }

    /// <summary>The absolute form of a path a caller gave, or the text itself when it is not one.</summary>
    private static string FullPathOf(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// One type as a document: the file name identifies the assembly, the type name the row. A type
    /// name is not a file name — <c>List&lt;T&gt;</c> carries characters Windows refuses — so it is
    /// sanitised. The path is a key, never opened.
    /// </summary>
    private static string DecompiledPath(string assembly, string typeFullName)    {
        var name = typeFullName;
        foreach (var invalid in Path.GetInvalidFileNameChars().Concat(['<', '>', ',', ' ']))
            name = name.Replace(invalid, '_');

        return MetacodeIndexStore.DecompiledPrefix + assembly + "/" + name;
    }

    /// <summary>One source of the solution, as the pass sees it: a Roslyn document, or a file only the disk knows.</summary>
    private sealed record IndexItem(string Path, string Project, string Language, Document? Document);

    /// <summary>
    /// Every file of the solution, not only the ones the compiler reads. Three sources, deduplicated in
    /// that order so the workspace's own view of a document wins over the disk's:
    /// <list type="number">
    /// <item>the Roslyn documents of each project — the Compile items, which are what a build sees;</item>
    /// <item>every text file under the solution root, found on disk: Razor pages and components (content
    /// items, so the workspace never holds them), project files, READMEs, JSON configs and the rest. A
    /// file no frontend reads becomes a single <c>file</c> node, so it is in the AST instead of missing;</item>
    /// <item>each project's own folder, for a workspace whose solution has no path on disk (an
    /// in-memory solution, a test).</item>
    /// </list>
    /// The same file reached twice (a shared file, a project nested in another project's folder) is
    /// indexed once, attributed to the project whose directory is its closest ancestor.
    /// </summary>
    private IReadOnlyList<IndexItem> Sources(IEnumerable<Project> projects, string? solutionPath)
    {
        var items = new List<IndexItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Name, string Directory)>();

        foreach (var project in projects)
        {
            var directory = ProjectDirectory(project);
            if (directory is not null) roots.Add((project.Name, directory));

            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path) continue;
                if (!seen.Add(path)) continue;
                items.Add(new IndexItem(path, project.Name, LanguageTag(document), document));
            }
        }

        var solutionRoot = SolutionDirectory(solutionPath, roots);
        foreach (var path in WorkspaceSources.FilesUnder(solutionRoot))
        {
            if (!IsTextFile(path)) continue;
            if (!seen.Add(path)) continue;
            items.Add(new IndexItem(path, ProjectOf(path, roots), WorkspaceSources.LanguageTag(path), null));
        }

        // A solution with no file (the tests' in-memory one) still has the projects' folders to walk.
        if (solutionRoot is null)
        {
            foreach (var (name, directory) in roots)
            {
                foreach (var path in WorkspaceSources.FilesUnder(directory))
                {
                    if (!IsTextFile(path)) continue;
                    if (!seen.Add(path)) continue;
                    items.Add(new IndexItem(path, name, WorkspaceSources.LanguageTag(path), null));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Whether a file can be read as text, answered from a cache keyed on its timestamp: a file that did
    /// not move is not opened again, which is what keeps the two-second pass a walk and not 1,200 reads.
    /// </summary>
    private bool IsTextFile(string path)
    {
        long mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (_textFiles.TryGetValue(path, out var known) && known.Mtime == mtime) return known.Text;

        var text = WorkspaceSources.IsTextFile(path, out mtime);
        _textFiles[path] = (mtime, text);
        return text;
    }

    /// <summary>The folder a project lives in, or null when the project has no file on disk.</summary>
    private static string? ProjectDirectory(Project project)
    {
        var file = project.FilePath;
        if (string.IsNullOrEmpty(file)) return null;

        var directory = Path.GetDirectoryName(file);
        return string.IsNullOrEmpty(directory) ? null : Path.GetFullPath(directory);
    }

    /// <summary>
    /// The root to walk: the solution's own folder when it has one, otherwise the shallowest project
    /// folder — which is the closest thing to "the solution's directory" an in-memory solution has.
    /// </summary>
    private static string? SolutionDirectory(string? solutionPath, IReadOnlyList<(string Name, string Directory)> roots)
    {
        if (!string.IsNullOrEmpty(solutionPath) && Path.GetDirectoryName(solutionPath) is { Length: > 0 } directory)
            return Path.GetFullPath(directory);

        return roots
            .Select(root => root.Directory)
            .OrderBy(directory => directory.Length)
            .FirstOrDefault();
    }

    /// <summary>The project whose folder is the closest ancestor of a file, or empty for a file no project owns.</summary>
    private static string ProjectOf(string path, IReadOnlyList<(string Name, string Directory)> roots)
    {
        var best = string.Empty;
        var bestLength = -1;

        foreach (var (name, directory) in roots)
        {
            if (directory.Length <= bestLength) continue;
            if (path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == directory.Length || path[directory.Length] is '\\' or '/'))
            {
                best = name;
                bestLength = directory.Length;
            }
        }

        return best;
    }

    /// <summary>The language a Roslyn document is written in, as a short tag: <c>cs</c>, <c>vb</c>.</summary>
    private static string LanguageTag(Document document) => document.Project.Language switch
    {
        LanguageNames.CSharp => CSharp,
        LanguageNames.VisualBasic => VisualBasic,
        _ => document.Project.Language.ToLowerInvariant(),
    };

    /// <summary>The language a path is written in, as the frontends name it — Unknown when none of them does.</summary>
    private static MetacodeLanguage LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => MetacodeLanguage.CSharp,
        ".cshtml" or ".razor" => MetacodeLanguage.Razor,
        ".vb" => MetacodeLanguage.VisualBasic,
        ".java" => MetacodeLanguage.Java,
        _ => MetacodeLanguage.Unknown,
    };

    /// <summary>The file's timestamp, or 0 when it is not on disk (a virtual document).</summary>
    private static (long Mtime, bool Exists) Stat(string path)
    {
        try
        {
            return (File.GetLastWriteTimeUtc(path).Ticks, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (0, false);
        }
    }

    /// <summary>
    /// The text to translate: the file as saved, not the workspace snapshot. That is the whole point of
    /// "updated when the file is saved" — an edit made in another editor never reached this workspace.
    /// A file only the workspace knows (a virtual document) falls back to its own text.
    /// </summary>
    private static async Task<(string Text, long Mtime)> ReadAsync(
        IndexItem item, string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
                return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the workspace text: the file is locked or gone, the model still knows it.
        }

        if (item.Document is null)
            throw new FileNotFoundException("The file is not readable and the workspace holds no document for it.", path);

        var text = await item.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return (text.ToString(), 0);
    }

    /// <summary>
    /// One source → both halves of the record: the text as its own language writes it, and the trees the
    /// model produced from it. The translator is built per document because the frontends carry
    /// per-translation state (their diagnostics), so the default singleton is not safe to share across the
    /// parallel pass. A file no frontend can read comes back with the NoFrontend diagnostic and a single
    /// <c>file</c> node: it belongs to the AST like everything else, marked as what it is.
    /// </summary>
    private static MetacodeDocumentFacts Translate(
        string text, string project, string language, string path, string hash, long mtime)
    {
        var translator = new MetacodeTranslator(
            [new CSharpFrontend(), new VisualBasicFrontend(), new JavaFrontend(), new RazorFrontend()]);
        var model = translator.Translate(MetacodeSource.FromText(text, LanguageOf(path), path), MetacodeOptions.Default);
        var collected = MetacodeDeclarations.Collect(model, path);
        var hasReader = model.Diagnostics.All(diagnostic => diagnostic.Code != MetacodeDiagnosticCodes.NoFrontend);

        if (!hasReader)
            collected = MetacodeDeclarations.FileDocument(path, text, language);

        return new MetacodeDocumentFacts(
            path, project, hash, mtime, language, collected.Rows, model.Diagnostics.Count)
        {
            Nodes = collected.Nodes,
            Source = text,
            HasReader = hasReader,
        };
    }

    // ── queries ────────────────────────────────────────────────────────────────────────

    public MetacodeIndexStatus Status()
    {
        var database = _store.EnsureDatabase();
        if (database is not null) _index.Open(database);

        var status = _index.Status();
        return status with { Solution = _workspace.LoadedSolutionPath };
    }

    public IReadOnlyList<MetacodeTreeRow> Trees(string? symbol = null, string? path = null, int limit = 50)
    {
        if (!TryOpen()) return [];

        if (!string.IsNullOrWhiteSpace(path))
            return _index.InPath(path.Trim(), limit);

        if (string.IsNullOrWhiteSpace(symbol)) return [];

        var rows = _index.Resolve(symbol.Trim(), limit);
        return rows
            .OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Line)
            .ToList();
    }

    public MetacodeAstResult Ast(string? symbol = null, string? path = null, int maxNodes = 2000)
    {
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(path))
            return new MetacodeAstResult(null, [], 0, false, "ast needs symbol or path.");

        if (!TryOpen())
            return new MetacodeAstResult(path, [], 0, false,
                "No solution is loaded, so there is no stored metacode. Call load_solution first.");

        // A symbol picks declarations wherever they live; a path alone means the whole file. Both
        // together means "this symbol, but only in this file" — a partial type is the reason.
        var declarations = string.IsNullOrWhiteSpace(symbol)
            ? _index.InPath(path!.Trim(), 200)
            : _index.Resolve(symbol.Trim(), 500)
                .Where(row => string.IsNullOrWhiteSpace(path) ||
                              string.Equals(row.Path, path.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (declarations.Count == 0)
            return new MetacodeAstResult(path, [], 0, false,
                $"The store has no declaration for '{(string.IsNullOrWhiteSpace(symbol) ? path : symbol)}'. " +
                "Build it (metacode_index action=build) or check the name.");

        var trees = new List<MetacodeAstTree>();
        var remaining = Math.Clamp(maxNodes, 1, 100_000);
        var returned = 0;
        var truncated = false;

        foreach (var declaration in declarations)
        {
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var stored = _index.AstCount(declaration.Path, declaration.Key);
            if (stored == 0) continue;

            var nodes = _index.Ast(declaration.Path, declaration.Key, remaining);
            if (nodes.Count < stored) truncated = true;

            returned += nodes.Count;
            remaining -= nodes.Count;
            trees.Add(new MetacodeAstTree(
                declaration.Path, declaration.Key, declaration.Kind, declaration.Name, declaration.Line,
                stored, nodes.Max(node => node.Depth), BuildTree(nodes))
            {
                SourcePath = declaration.SourcePath,
                Decompiled = declaration.Decompiled,
            });
        }

        if (trees.Count == 0)
            return new MetacodeAstResult(path, [], 0, false,
                "No AST is stored for those declarations: the index was built before mc_node existed. " +
                "Rebuild it (metacode_index action=build force=true).");

        return new MetacodeAstResult(path, trees, returned, truncated);
    }

    /// <summary>
    /// The nested shape from the flat rows: a node's children are the rows whose parent is it, ordered by
    /// the position they hold in the parent's property. Rows are pre-order, so a cap that cuts the tail
    /// never orphans a node — a cut node's children come after it and are cut with it.
    /// </summary>
    private static IReadOnlyList<MetacodeAstTreeNode> BuildTree(IReadOnlyList<MetacodeAstNode> nodes)
    {
        var byParent = nodes
            .GroupBy(node => node.ParentId)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Ordinal).ToList());

        List<MetacodeAstTreeNode> Children(int parent) =>
            (byParent.TryGetValue(parent, out var children) ? children : [])
            .Select(node => new MetacodeAstTreeNode(
                node.NodeId, node.Kind, node.Slot, node.Ordinal, node.Line, node.Column,
                Payload(node.Data), Children(node.NodeId)))
            .ToList();

        return Children(0);
    }

    /// <summary>The stored payload, parsed so the answer carries data instead of a JSON string.</summary>
    private static JsonElement Payload(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = data });
        }
    }

    /// <summary>
    /// The native text the store holds for a path — the file as its own language writes it, which is the
    /// half that exists even when no frontend could read it. Null when the store has never read the path.
    /// </summary>
    public (string Language, string Text)? Source(string path)
    {
        if (!TryOpen()) return null;
        return _index.SourceOf(path.Trim());
    }

    public IReadOnlyList<MetacodeComplexityRow> Complexity(
        int minCyclomatic = 1, string? projectName = null, string? kind = null, int limit = 50)
    {
        if (!TryOpen()) return [];

        return _index.Complexity(projectName, kind, Math.Max(1, minCyclomatic), limit)
            .Select(row => new MetacodeComplexityRow(
                row.Key, row.Name, row.Kind, row.Path, row.Line,
                row.Cyclomatic, row.Cognitive, row.MaxNesting, row.Statements, row.Branches,
                MetacodeComplexityAnalyzer.RiskOf(row.Cyclomatic, row.Cognitive))
            {
                SourcePath = row.SourcePath,
                Decompiled = row.Decompiled,
            })
            .ToList();
    }

    private bool TryOpen()
    {
        var database = _store.EnsureDatabase();
        return database is not null && _index.Open(database);
    }

    // ── generation ────────────────────────────────────────────────────────────────────

    public MetacodeGenerateResult Generate(
        string? symbol = null,
        string? path = null,
        string? text = null,
        string? language = null,
        bool includeUsings = true,
        string? usings = null)
    {
        var options = CSharpEmitOptions.Default with
        {
            IncludeUsings = includeUsings,
            Usings = SplitUsings(usings),
        };

        var diagnostics = new List<string>();

        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                var tag = Normalize(language) ?? "pseudo";
                var source = tag switch
                {
                    "cs" => MetacodeSource.FromCSharp(text),
                    "vb" => MetacodeSource.FromVisualBasic(text),
                    "pseudo" => MetacodeSource.FromPseudocode(text),
                    _ => null,
                };

                if (source is null)
                    return Failed($"language must be 'pseudo', 'cs' or 'vb' for text — the target is C#, the source cannot be a Razor page here.", diagnostics);

                return Emit(source, "text", diagnostics, options);
            }

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                if (!TryOpen())
                    return Failed("No solution is loaded, so there is no stored metacode. Call load_solution first.", diagnostics);

                var rows = Trees(symbol, path, limit: 1);
                if (rows.Count == 0)
                    return Failed($"The store has no declaration named '{symbol}'. Build it (metacode_index action=build) or pass text.", diagnostics);

                return EmitStored(rows[0], diagnostics, options);
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                if (TryOpen())
                {
                    var rows = Trees(path: path, limit: 1);
                    if (rows.Count > 0)
                        return EmitStored(rows[0], diagnostics, options);
                }

                if (!File.Exists(path))
                    return Failed($"File not found: {path}", diagnostics);

                var source = MetacodeSource.FromFile(path);
                if (source.Language is not (MetacodeLanguage.CSharp or MetacodeLanguage.VisualBasic or MetacodeLanguage.Pseudocode))
                    return Failed($"Only C#, Visual Basic and canonical pseudocode can be generated from; {path} is not one of them.", diagnostics);

                return Emit(source, "file", diagnostics, options);
            }

            return Failed("Pass text, symbol or path: generate needs something to translate.", diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics.Add($"{exception.GetType().Name}: {exception.Message}");
            return Failed("The generation failed.", diagnostics);
        }
    }

    private MetacodeGenerateResult Emit(
        MetacodeSource source, string origin, List<string> diagnostics, CSharpEmitOptions options)
    {
        var model = MetacodeTranslator.Default.Translate(source, MetacodeOptions.Default);
        diagnostics.AddRange(model.Diagnostics.Select(diagnostic => diagnostic.ToString()));

        var result = new CSharpEmitter(options).Emit(model);
        diagnostics.AddRange(result.Diagnostics);

        return new MetacodeGenerateResult(
            result.Code, origin, "csharp", diagnostics, result.Usings);
    }

    /// <summary>
    /// A stored tree is canonical pseudocode by itself (a type nests its members). The parser reads a
    /// whole document, so the declaration is wrapped in a module before it is read back and emitted.
    /// </summary>
    private MetacodeGenerateResult EmitStored(
        MetacodeTreeRow row, List<string> diagnostics, CSharpEmitOptions options)
    {
        var text = "MODULE generated\n" + row.Text.TrimEnd() + "\nEND MODULE\n";
        var model = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(text), MetacodeOptions.Default);

        foreach (var diagnostic in model.Diagnostics)
            diagnostics.Add(diagnostic.ToString());

        var declaration = model.Modules.SelectMany(module => module.Declarations).FirstOrDefault();
        if (declaration is null)
        {
            diagnostics.Add($"The stored tree of '{row.Name}' could not be read back ({model.Diagnostics.Count} diagnostic(s)).");
            return Failed("The stored metacode could not be parsed; rebuild it (metacode_index action=build force=true).", diagnostics);
        }

        var result = new CSharpEmitter(options).Emit(declaration);
        diagnostics.AddRange(result.Diagnostics);

        return new MetacodeGenerateResult(
            result.Code, $"store:{row.Key} ({row.Kind} at {Path.GetFileName(row.Path)}:{row.Line})", "csharp",
            diagnostics, result.Usings);
    }

    private static MetacodeGenerateResult Failed(string error, List<string> diagnostics) =>
        new(string.Empty, string.Empty, "csharp", diagnostics, Array.Empty<string>(), error);

    private static string? Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "cs" or "c#" or "csharp" => "cs",
        "vb" or "vbnet" or "visualbasic" or "visual-basic" => "vb",
        "pseudo" or "pseudocode" or "mc" or "metacode" => "pseudo",
        _ => language,
    };

    private static IReadOnlyList<string>? SplitUsings(string? usings) =>
        string.IsNullOrWhiteSpace(usings)
            ? null
            : usings.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose()
    {
        var watching = Interlocked.Exchange(ref _watching, null);
        if (watching is not null)
        {
            try { watching.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
            watching.Dispose();
        }

        _buildGate.Dispose();
        _index.Dispose();
    }
}

/// <summary>What one document contributed: a row per declaration and the complete AST of each of them.</summary>
internal sealed record MetacodeCollected(
    IReadOnlyList<MetacodeTreeRow> Rows,
    IReadOnlyList<MetacodeAstNode> Nodes);

/// <summary>
/// Walks a translated document and produces one row per declaration: the canonical pseudocode of that
/// declaration (a type nests its members), the file and span it came from, and the structural facts the
/// complexity analyzer computes over the model. The key is synthetic but stable — namespace, containing
/// types, name and arity — so a lookup does not need a semantic model.
/// </summary>
internal static class MetacodeDeclarations
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static MetacodeCollected Collect(MetacodeDocument model, string path)
    {
        var rows = new List<MetacodeTreeRow>();
        var nodes = new List<MetacodeAstNode>();
        var renderer = new PseudocodeRenderer();

        foreach (var module in model.Modules)
            Walk(module.Declarations, module.Name, [], rows, nodes, renderer, path);

        return new MetacodeCollected(rows, nodes);
    }

    /// <summary>
    /// The AST of a file no frontend reads: ONE node that says what the file is, so a README, a project
    /// file or a JSON config is in the store and in the AST instead of absent from both. Its text is the
    /// other half of the record (<c>source_document</c>), which is why the node carries a description and
    /// not a copy of the content.
    /// </summary>
    /// <remarks>
    /// The tree row is kind <c>file</c>, which is the one kind the complexity report and the declaration
    /// count exclude: it names a document, it does not pretend to be a declaration.
    /// </remarks>
    public static MetacodeCollected FileDocument(string path, string text, string language)
    {
        var name = Path.GetFileName(path);
        var lines = LineCount(text);
        var key = "file:" + name;

        var row = new MetacodeTreeRow(
            key, path, "file", name, 1, 1, lines,
            Cyclomatic: 0, Cognitive: 0, MaxNesting: 0, Statements: 0, Branches: 0, Expressions: 0,
            Text: string.Empty);

        var data = JsonSerializer.Serialize(
            new { path, name, language, lines, chars = text.Length }, PayloadJson);

        var node = new MetacodeAstNode(
            key, NodeId: 1, ParentId: 0, Slot: string.Empty, Ordinal: 0, Depth: 0,
            Kind: "File", Line: 1, Column: 1, EndLine: lines, Data: data);

        return new MetacodeCollected([row], [node]);
    }

    private static int LineCount(string text)
    {
        var lines = 1;
        foreach (var character in text)
            if (character == '\n') lines++;
        return lines;
    }

    private static void Walk(
        IReadOnlyList<MetacodeDeclaration> declarations,
        string @namespace,
        IReadOnlyList<string> types,
        List<MetacodeTreeRow> rows,
        List<MetacodeAstNode> nodes,
        PseudocodeRenderer renderer,
        string path)
    {
        foreach (var declaration in declarations)
        {
            var name = NameOf(declaration);
            var kind = KindOf(declaration);
            var metrics = MetricsOf(declaration);
            var origin = declaration.Origin;
            var key = KeyOf(@namespace, types, declaration, kind, name);

            rows.Add(new MetacodeTreeRow(
                key,
                path,
                kind,
                name,
                origin.IsKnown ? origin.StartLine : 0,
                origin.IsKnown ? origin.StartColumn : 0,
                origin.IsKnown ? origin.EndLine : 0,
                metrics.Cyclomatic,
                metrics.Cognitive,
                metrics.MaxNesting,
                metrics.Statements,
                metrics.Branches,
                metrics.Expressions,
                renderer.Render(declaration)));

            // The same declaration as a tree of nodes, so the store keeps the AST and not only its text.
            nodes.AddRange(MetacodeAst.Serialize(declaration, key));

            if (declaration is MetacodeTypeDeclaration type)
                Walk(type.Members, @namespace, [.. types, type.Name], rows, nodes, renderer, path);
        }
    }

    /// <summary>
    /// A stable, semantic-model-free identity: <c>method:Ns.Outer.Inner.Name(2)</c>, an indexer as
    /// <c>indexer:Ns.Type.this[1]</c>, a type with its generic arity. It is not the Roslyn symbol key —
    /// it is what the metacode itself can say.
    /// </summary>
    private static string KeyOf(
        string @namespace, IReadOnlyList<string> types, MetacodeDeclaration declaration, string kind, string name)
    {
        var container = string.Join(".", new[] { @namespace }.Concat(types).Where(part => part.Length > 0));

        return declaration switch
        {
            MetacodeTypeDeclaration type =>
                $"{kind}:{Join(container, Identifier(name))}"
                + (type.TypeParameters is { Count: > 0 } parameters ? "`" + parameters.Count : string.Empty),

            MetacodeFunction function =>
                $"{kind}:{Join(container, Identifier(name))}({function.ParameterList.Count})"
                + (function.TypeParameters is { Count: > 0 } parameters ? "`" + parameters.Count : string.Empty),

            MetacodeProperty property when kind == "indexer" =>
                $"indexer:{Join(container, "this")}[{(property.Parameters?.Count ?? 0)}]",

            MetacodeProperty =>
                $"property:{Join(container, Identifier(name))}",

            _ => $"{kind}:{Join(container, Identifier(name))}",
        };

        static string Identifier(string value) => value.Length == 0 ? "?" : value;
        static string Join(string container, string member) => container.Length == 0 ? member : container + "." + member;
    }

    private static string KindOf(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeTypeDeclaration type => type.Kind switch
        {
            MetacodeTypeKind.Class => "class",
            MetacodeTypeKind.Struct => "struct",
            MetacodeTypeKind.Interface => "interface",
            MetacodeTypeKind.Enum => "enum",
            MetacodeTypeKind.Record => "record",
            MetacodeTypeKind.Union => "union",
            MetacodeTypeKind.Trait => "trait",
            _ => "class",
        },
        MetacodeFunction function => function.Kind switch
        {
            MetacodeFunctionKind.Method => "method",
            MetacodeFunctionKind.Constructor => "constructor",
            MetacodeFunctionKind.Destructor => "destructor",
            MetacodeFunctionKind.Operator => "operator",
            MetacodeFunctionKind.LocalFunction => "local-function",
            _ => "function",
        },
        MetacodeProperty property => property.Parameters is { Count: > 0 } || property.Name == "this" ? "indexer" : "property",
        MetacodeField => "field",
        MetacodeEvent => "event",
        MetacodeTypeAlias => "alias",
        MetacodeEnumMember => "enum-member",
        MetacodeOpaqueDeclaration => "opaque",
        _ => "declaration",
    };

    private static string NameOf(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeTypeDeclaration type => type.Name,
        MetacodeFunction function => function.Name,
        MetacodeProperty property => property.Name,
        MetacodeField field => field.Name,
        MetacodeEvent @event => @event.Name,
        MetacodeTypeAlias alias => alias.Name,
        MetacodeEnumMember member => member.Name,
        _ => "?",
    };

    /// <summary>
    /// The score of a callable; a type has none of its own (its members have theirs), and a field or an
    /// event is not a path through anything.
    /// </summary>
    private static MetacodeComplexityMetrics MetricsOf(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeFunction function => MetacodeComplexityAnalyzer.Analyze(function),
        MetacodeProperty property => MetacodeComplexityAnalyzer.Analyze(property),
        _ => new MetacodeComplexityMetrics(0, 0, 0, 0, 0, 0, Array.Empty<MetacodeComplexityContribution>()),
    };
}
