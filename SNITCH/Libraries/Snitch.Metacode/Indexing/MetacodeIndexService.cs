using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// Reads every source of the solution whose content changed and stores both halves of it: the file as
    /// its own language writes it (always) and the metacode of the languages a frontend can read. The
    /// document set is decided by language, not by a <c>.cs</c> filter — Razor pages and components come
    /// from disk, because they are content items and never reach the workspace.
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
    /// instead of duplicating them, and the solution's own refresh never prunes them.
    /// </summary>
    Task<MetacodeBuildReport> IndexDecompilationAsync(
        MetacodeDecompilation decompilation, CancellationToken cancellationToken = default);

    /// <summary>
    /// The MVID the store holds for a decompiled assembly (the name of the file, e.g.
    /// <c>Newtonsoft.Json.dll</c>), or null when it was never stored. A caller compares it with the
    /// binary's current MVID to decide whether decompiling again would tell it anything new.
    /// </summary>
    string? DecompiledAssemblyHash(string assemblyFileName);

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
    private const string Razor = "razor";

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

        var documents = Sources(projects);

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
                var path = item.Path;

                try
                {
                    var stat = Stat(path);
                    known.TryGetValue(path, out var state);

                    // Fast path: the file was not touched since the last pass and both halves are stored,
                    // so its stored hash still holds.
                    if (!force && state is { HasSource: true } && stat.Mtime != 0 && state.Mtime == stat.Mtime)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var (text, mtime) = await ReadAsync(item, path, token).ConfigureAwait(false);
                    var hash = MetacodeIndexStore.ContentHash(text);

                    if (!force && state is { HasSource: true } && string.Equals(state.Hash, hash, StringComparison.Ordinal))
                    {
                        // Touched but identical (a save that changed nothing): keep the rows, renew the stamp.
                        _index.Touch(path, mtime);
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var facts = Translate(text, item.Project, item.Language, path, hash, mtime);

                    // A language with no reader still gets its native half written: the store says the
                    // solution contains the file and that no frontend could read it, instead of skipping it.
                    var written = facts.HasReader
                        ? await _index.ReplaceAsync(facts, token).ConfigureAwait(false)
                        : await _index.ReplaceSourceAsync(facts, token).ConfigureAwait(false);
                    if (!written)
                        throw new InvalidOperationException("the database refused the write");

                    if (!facts.HasReader)
                    {
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
                    failures.Enqueue($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }).ConfigureAwait(false);

        var removed = 0;
        if (projectName is null)
        {
            var present = documents.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = known.Keys
                // A decompiled assembly is not a document of the solution: its rows belong to the binary,
                // and only a new decompilation of that binary can make them stale.
                .Where(path => !path.StartsWith(MetacodeIndexStore.DecompiledPrefix, StringComparison.Ordinal))
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
            var rows = MetacodeDeclarations.Collect(type.Document, path);

            // The whole assembly shares one freshness token — its MVID — so DecompiledHash can answer
            // "was this exact binary stored?" from any of its rows, and a new build replaces them all.
            var facts = new MetacodeDocumentFacts(path, project, identity, 0, CSharp, rows, type.Document.Diagnostics.Count);

            if (!await _index.ReplaceAsync(facts, cancellationToken).ConfigureAwait(false))
            {
                failures.Add($"{type.FullName}: the database refused the write");
                continue;
            }

            written.Add(path);
            indexed++;
            declarations += rows.Count;
            diagnostics += facts.Diagnostics;
            foreach (var row in rows)
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
        };
    }

    public string? DecompiledAssemblyHash(string assemblyFileName)
    {
        if (string.IsNullOrWhiteSpace(assemblyFileName) || !TryOpen()) return null;
        return _index.DecompiledHash(assemblyFileName.Trim());
    }

    public IReadOnlyList<DecompiledAssemblyRow> DecompiledAssemblies() =>
        TryOpen() ? _index.DecompiledAssemblies() : [];

    /// <summary>
    /// One type as a document: the file name identifies the assembly, the type name the row. A type
    /// name is not a file name — <c>List&lt;T&gt;</c> carries characters Windows refuses — so it is
    /// sanitised. The path is a key, never opened.
    /// </summary>
    private static string DecompiledPath(string assembly, string typeFullName)
    {
        var name = typeFullName;
        foreach (var invalid in Path.GetInvalidFileNameChars().Concat(['<', '>', ',', ' ']))
            name = name.Replace(invalid, '_');

        return MetacodeIndexStore.DecompiledPrefix + assembly + "/" + name;
    }

    /// <summary>One source of the solution, as the pass sees it: a Roslyn document, or a file only the disk knows.</summary>
    private sealed record IndexItem(string Path, string Project, string Language, Document? Document);

    /// <summary>
    /// Every source of the solution in its own language: the Roslyn documents of each project, plus the
    /// Razor pages and components, which are content items and never reach the workspace as documents.
    /// The same file reached through two projects (a shared file, a nested folder) is indexed once.
    /// </summary>
    private static IReadOnlyList<IndexItem> Sources(IEnumerable<Project> projects)
    {
        var items = new List<IndexItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path) continue;
                if (!seen.Add(path)) continue;
                items.Add(new IndexItem(path, project.Name, LanguageTag(document), document));
            }

            foreach (var path in RazorFilesUnder(Path.GetDirectoryName(project.FilePath)))
            {
                if (!seen.Add(path)) continue;
                items.Add(new IndexItem(path, project.Name, Razor, null));
            }
        }

        return items;
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
        _ => MetacodeLanguage.Unknown,
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "TestResults",
    };

    /// <summary>
    /// The Razor files under a project, found on disk: a <c>.cshtml</c> is a content item, so the
    /// workspace is not a place it exists. Build outputs and tool folders are skipped.
    /// </summary>
    private static IEnumerable<string> RazorFilesUnder(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

                if (entry.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.GetFullPath(entry);
                }
            }
        }
    }

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
    /// parallel pass. A document no frontend can read comes back with the NoFrontend diagnostic and
    /// <see cref="MetacodeDocumentFacts.HasReader"/> false, which is what keeps it in the store as a
    /// source and out of it as metacode.
    /// </summary>
    private static MetacodeDocumentFacts Translate(
        string text, string project, string language, string path, string hash, long mtime)
    {
        var translator = new MetacodeTranslator([new CSharpFrontend(), new RazorFrontend()]);
        var model = translator.Translate(MetacodeSource.FromText(text, LanguageOf(path), path), MetacodeOptions.Default);
        var rows = MetacodeDeclarations.Collect(model, path);
        var hasReader = model.Diagnostics.All(diagnostic => diagnostic.Code != MetacodeDiagnosticCodes.NoFrontend);

        return new MetacodeDocumentFacts(path, project, hash, mtime, language, rows, model.Diagnostics.Count)
        {
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
                MetacodeComplexityAnalyzer.RiskOf(row.Cyclomatic, row.Cognitive)))
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
                    "pseudo" => MetacodeSource.FromPseudocode(text),
                    _ => null,
                };

                if (source is null)
                    return Failed($"language must be 'pseudo' or 'cs' for text — the target is C#, the source cannot be a Razor page here.", diagnostics);

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
                if (source.Language is not (MetacodeLanguage.CSharp or MetacodeLanguage.Pseudocode))
                    return Failed($"Only C# and canonical pseudocode can be generated from; {path} is not one of them.", diagnostics);

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

/// <summary>
/// Walks a translated document and produces one row per declaration: the canonical pseudocode of that
/// declaration (a type nests its members), the file and span it came from, and the structural facts the
/// complexity analyzer computes over the model. The key is synthetic but stable — namespace, containing
/// types, name and arity — so a lookup does not need a semantic model.
/// </summary>
internal static class MetacodeDeclarations
{
    public static IReadOnlyList<MetacodeTreeRow> Collect(MetacodeDocument model, string path)
    {
        var rows = new List<MetacodeTreeRow>();
        var renderer = new PseudocodeRenderer();

        foreach (var module in model.Modules)
            Walk(module.Declarations, module.Name, [], rows, renderer, path);

        return rows;
    }

    private static void Walk(
        IReadOnlyList<MetacodeDeclaration> declarations,
        string @namespace,
        IReadOnlyList<string> types,
        List<MetacodeTreeRow> rows,
        PseudocodeRenderer renderer,
        string path)
    {
        foreach (var declaration in declarations)
        {
            var name = NameOf(declaration);
            var kind = KindOf(declaration);
            var metrics = MetricsOf(declaration);
            var origin = declaration.Origin;

            rows.Add(new MetacodeTreeRow(
                KeyOf(@namespace, types, declaration, kind, name),
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

            if (declaration is MetacodeTypeDeclaration type)
                Walk(type.Members, @namespace, [.. types, type.Name], rows, renderer, path);
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
