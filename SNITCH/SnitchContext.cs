using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch;

/// <summary>
/// The Snitch <em>environment</em>: the single source of truth for what we are working on —
/// every solution discovered under the working directory (each loaded into Roslyn), the
/// <see cref="ActiveSolution"/> tools operate on, its documentation, and the current selection.
/// </summary>
/// <remarks>
/// Registered as a singleton by <see cref="SnitchServiceCollectionExtensions.AddSnitch"/>, so the
/// state it carries is process-wide. New code depends on this type and injects the individual
/// <c>I*Service</c> interfaces it needs; the legacy <see cref="IRoslynWorkspace"/> surface and the
/// all-services facade live on <see cref="SnitchContextLegacy"/>.
/// </remarks>
public sealed class SnitchContext : IAsyncDisposable, IRoslynWorkspace
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _workingDirectory = Directory.GetCurrentDirectory();
    private List<SnitchSolution>? _solutions;
    private List<SnitchDoc>? _docs;
    private string? _docsRoot;
    private readonly object _docsLock = new();
    private SnitchSolution? _activeSolution;
    private bool _disposed;

    public SnitchContext(SnitchOptions options) => Options = options;

    // ──────────────────────────────────────────────────────────────────────────
    //  Session state — what we are working on
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Host identity and feature configuration for this process.</summary>
    public SnitchOptions Options { get; }

    /// <summary>
    /// The active solution — the one tools operate on by default. Defaults to the
    /// shallowest discovered solution once the environment is opened; switch it with
    /// <see cref="ActivateSolutionAsync"/>.
    /// </summary>
    public SnitchSolution? ActiveSolution => _activeSolution;

    // ──────────────────────────────────────────────────────────────────────────
    //  IRoslynWorkspace — the "active solution" view the feature services depend on.
    //  SnitchContext is now the only implementation: the former SnitchContextLegacy
    //  adapter was folded in here, since nothing ever used its service facade.
    // ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Solution? CurrentSolution => _activeSolution?.Loaded;

    /// <inheritdoc/>
    public string? LoadedSolutionPath => _activeSolution?.Path;

    /// <inheritdoc/>
    public async Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default)
        => (await EnsureOpenedAsync(ct))?.Loaded;

    /// <inheritdoc/>
    public async Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        var solution = await ActivateSolutionAsync(solutionPath, ct);
        return new SolutionLoadResult(solution.Loaded!, solution.Diagnostics);
    }

    /// <inheritdoc/>
    public string? DiscoverSolutionPath() =>
        Solutions
            .OrderBy(solution => solution.Path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(solution => solution.Path.Length)
            .FirstOrDefault()?.Path;

    /// <summary>Synchronous disposal for <see cref="IRoslynWorkspace"/>; the host disposes asynchronously.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Replaces one document's text in the active solution. Tools that rewrite a file on disk call
    /// this so the next tool call reads the file as it now is, instead of the pre-write snapshot —
    /// without it a second refactoring would rewrite from stale text and undo the first.
    /// </summary>
    /// <remarks>
    /// The swap is a compare-and-retry, not an assignment: two steps of one <c>run_plan</c> can rewrite
    /// different documents at the same time, and an assignment from a stale snapshot would silently drop
    /// one of the two from the model while the disk kept both.
    /// </remarks>
    public bool TryUpdateDocument(DocumentId documentId, SourceText text)
    {
        var target = _activeSolution;
        if (target is null) return false;

        return target.TryUpdate(solution =>
            solution.GetDocument(documentId) is null ? null : solution.WithDocumentText(documentId, text));
    }

    /// <summary>
    /// Adds a document the model does not know yet (a file a tool just created) so the next call
    /// sees it without a reload. Returns the new id, or null when the project is unknown or the
    /// path is already taken.
    /// </summary>
    public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text)
    {
        var target = _activeSolution;
        if (target is null) return null;

        var normalized = Path.GetFullPath(filePath);
        DocumentId? created = null;

        // The check and the add happen inside the retried lambda: under contention the "already there"
        // answer belongs to the snapshot this attempt is based on, not to the one it started from.
        var applied = target.TryUpdate(solution =>
        {
            if (solution.GetProject(projectId) is null) return null;

            var alreadyThere = solution.Projects
                .SelectMany(project => project.Documents)
                .Any(document => document.FilePath is not null &&
                                 string.Equals(Path.GetFullPath(document.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
            if (alreadyThere) return null;

            created = DocumentId.CreateNewId(projectId);
            return solution.AddDocument(created, name, text, filePath: filePath);
        });

        return applied ? created : null;
    }

    /// <summary>
    /// Removes a document the tool has just moved or deleted from disk, so the model does not keep
    /// reporting a file that is no longer there.
    /// </summary>
    public bool TryRemoveDocument(DocumentId documentId)
    {
        var target = _activeSolution;
        if (target is null) return false;

        return target.TryUpdate(solution =>
            solution.GetDocument(documentId) is null ? null : solution.RemoveDocument(documentId));
    }

    /// <summary>
    /// Directory that work is rooted in. Defaults to the process working directory;
    /// a host or tool may repoint it. Reassigning it invalidates the cached
    /// <see cref="Solutions"/> and <see cref="Docs"/> so the next access rescans the new root.
    /// </summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (string.Equals(_workingDirectory, value, StringComparison.OrdinalIgnoreCase))
                return;
            DisposeSolutionWorkspaces();   // drop solutions loaded from the old root
            _workingDirectory = value;
            _solutions = null;
            // _docs is cached per-root (see Docs), so the new root re-indexes on next access.
        }
    }

    /// <summary>
    /// Every solution (<c>.sln</c>/<c>.slnx</c>) found anywhere beneath
    /// <see cref="WorkingDirectory"/> — which solution it is and where it lives, plus its
    /// loaded model once the environment is opened (see <see cref="SnitchSolution"/>).
    /// Scanned on first access and cached; call <see cref="RefreshSolutions"/> to rescan.
    /// </summary>
    public IReadOnlyList<SnitchSolution> Solutions => _solutions ??= DiscoverSolutions();

    /// <summary>
    /// Disposes anything loaded and rescans <see cref="WorkingDirectory"/> for solutions
    /// (metadata only — does not reload them; use <see cref="OpenEnvironmentAsync"/> for that).
    /// </summary>
    public IReadOnlyList<SnitchSolution> RefreshSolutions()
    {
        DisposeSolutionWorkspaces();
        return _solutions = DiscoverSolutions();
    }

    /// <summary>
    /// The project's documentation: Markdown (<c>*.md</c>/<c>*.markdown</c>/<c>*.mdx</c>) plus
    /// well-known doc text files (<c>README</c>, <c>CHANGELOG</c>, … as <c>.txt</c>) found beneath
    /// <see cref="WorkingDirectory"/>, with each file's (capped) content already read — see
    /// <see cref="SnitchDoc"/>. Build-output, VCS and package folders are pruned. Indexed on first
    /// access and cached per root; call <see cref="RefreshDocs"/> to re-index.
    /// </summary>
    public IReadOnlyList<SnitchDoc> Docs
    {
        get
        {
            var root = WorkingDirectory;
            lock (_docsLock)
            {
                if (_docs is not null && string.Equals(_docsRoot, root, StringComparison.OrdinalIgnoreCase))
                    return _docs;
                _docsRoot = root;
                return _docs = IndexDocs(root);
            }
        }
    }

    /// <summary>Re-indexes the documentation under <see cref="WorkingDirectory"/>.</summary>
    public IReadOnlyList<SnitchDoc> RefreshDocs()
    {
        var root = WorkingDirectory;
        lock (_docsLock)
        {
            _docsRoot = root;
            return _docs = IndexDocs(root);
        }
    }

    /// <summary>Indexes the documentation on a background thread; safe to call on every load.</summary>
    public void WarmDocsInBackground()
        => _ = Task.Run(() => { try { _ = Docs; } catch { /* best-effort */ } });

    /// <summary>
    /// Starts work on an environment: points the context at <paramref name="root"/> (or the
    /// current <see cref="WorkingDirectory"/> when null), discovers every solution under it
    /// and <em>loads them all</em> into Roslyn so the whole environment is ready up front.
    /// The shallowest loaded solution becomes the <see cref="ActiveSolution"/>.
    /// </summary>
    /// <remarks>
    /// Loading is resilient: a solution that fails to load is left with a null
    /// <see cref="SnitchSolution.Loaded"/> (its errors in <see cref="SnitchSolution.Diagnostics"/>)
    /// and does not abort the others.
    /// </remarks>
    /// <returns>The discovered (and now mostly loaded) solutions.</returns>
    public async Task<IReadOnlyList<SnitchSolution>> OpenEnvironmentAsync(
        string? root = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await OpenEnvironmentCoreAsync(root, ct);
            return _solutions ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the solution at <paramref name="path"/> the <see cref="ActiveSolution"/>, loading
    /// it (and adding it to <see cref="Solutions"/>) if not already loaded. Use this to switch
    /// the solution tools operate on, or to open one outside the working directory.
    /// </summary>
    public async Task<SnitchSolution> ActivateSolutionAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ActivateSolutionCoreAsync(path, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ensures the environment is opened (auto-opening from <see cref="WorkingDirectory"/> on
    /// first use) and returns the <see cref="ActiveSolution"/>, or null when none can be located.
    /// </summary>
    public async Task<SnitchSolution?> EnsureOpenedAsync(CancellationToken ct = default)
    {
        if (_activeSolution?.Loaded is not null)
            return _activeSolution;

        await _gate.WaitAsync(ct);
        try
        {
            if (_activeSolution?.Loaded is not null)
                return _activeSolution;
            await OpenEnvironmentCoreAsync(root: null, ct);
            return _activeSolution;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── loading internals (callers already hold the gate) ──────────────────────────

    private async Task OpenEnvironmentCoreAsync(string? root, CancellationToken ct)
    {
        if (root is not null)
            WorkingDirectory = root;       // setter disposes old workspaces + clears cache

        DisposeSolutionWorkspaces();
        var solutions = _solutions = DiscoverSolutions();
        if (solutions.Count == 0)
            return;

        foreach (var solution in solutions)
        {
            ct.ThrowIfCancellationRequested();
            try { await LoadSolutionFileAsync(solution, ct); }
            catch { /* resilient: skip; diagnostics captured on the solution */ }
        }

        // Active = shallowest path, then shortest — a repo's top-level solution rather than a
        // nested sample/fixture. Prefer one that actually loaded.
        _activeSolution = PickPrimary(solutions.Where(s => s.IsLoaded)) ?? PickPrimary(solutions);
        WarmDocsInBackground();
    }

    private async Task<SnitchSolution> ActivateSolutionCoreAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var solutions = _solutions ??= DiscoverSolutions();

        var solution = solutions.FirstOrDefault(
            s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));

        if (solution is null)
        {
            // A solution outside the discovered set (explicit load by path).
            solution = SnitchSolution.FromPath(full);
            _solutions = solutions
                .Append(solution)
                .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!solution.IsLoaded)
            await LoadSolutionFileAsync(solution, ct);

        _activeSolution = solution;
        WarmDocsInBackground();
        return solution;
    }

    private static async Task LoadSolutionFileAsync(SnitchSolution solution, CancellationToken ct)
    {
        var workspace = MSBuildWorkspace.Create();
        var diagnostics = new List<string>();
        workspace.WorkspaceFailed += (_, e) =>
            diagnostics.Add($"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}");

        try
        {
            var loaded = await workspace.OpenSolutionAsync(solution.Path, cancellationToken: ct);
            solution.Attach(workspace, loaded, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            solution.Attach(null, null, diagnostics);
            throw;
        }
    }

    private static SnitchSolution? PickPrimary(IEnumerable<SnitchSolution> solutions) =>
        solutions
            .OrderBy(s => s.Path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(s => s.Path.Length)
            .FirstOrDefault();

    private void DisposeSolutionWorkspaces()
    {
        if (_solutions is not null)
            foreach (var s in _solutions)
                s.DisposeWorkspace();
        _activeSolution = null;
    }

    private List<SnitchSolution> DiscoverSolutions()
    {
        var root = WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        return SolutionDiscovery.Enumerate(root)
            .Select(SnitchSolution.FromPath)
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Documentation indexing limits (folded in from the former ProjectDocsService).
    private const long MaxDocBytesForContent = 512 * 1024;
    private const int  MaxDocChars           = 64 * 1024;
    private const int  MaxDocs               = 400;

    private static readonly HashSet<string> DocTextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "CHANGELOG", "CONTRIBUTING", "NOTES", "TODO", "ARCHITECTURE", "AUTHORS", "ROADMAP",
    };

    private static List<SnitchDoc> IndexDocs(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        // Markdown family anywhere, plus well-known doc text files (README.txt, CHANGELOG.txt, …).
        var paths = FileDiscovery.Walk(root, "*.md")
            .Concat(FileDiscovery.Walk(root, "*.markdown"))
            .Concat(FileDiscovery.Walk(root, "*.mdx"))
            .Concat(FileDiscovery.Walk(root, "*.txt")
                .Where(f => DocTextNames.Contains(Path.GetFileNameWithoutExtension(f))));

        var docs = new List<SnitchDoc>();
        foreach (var f in paths.Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                               .Take(MaxDocs))
        {
            try
            {
                var fi = new FileInfo(f);
                string? content = null;
                var omitted = false;
                if (fi.Length <= MaxDocBytesForContent)
                {
                    var text = File.ReadAllText(f);
                    content = text.Length > MaxDocChars ? text[..MaxDocChars] + "\n…(truncated)" : text;
                }
                else omitted = true;

                docs.Add(new SnitchDoc(f, Path.GetRelativePath(root, f), fi.Length, content, omitted));
            }
            catch { /* skip unreadable file */ }
        }
        return docs;
    }

    /// <summary>The currently selected project, if any.</summary>
    public ProjectId? CurrentProject { get; set; }

    /// <summary>The currently selected file (absolute path), if any.</summary>
    public string? CurrentFilePath { get; set; }

    /// <summary>
    /// Resolves <see cref="CurrentProject"/> against the active solution, returning the live
    /// <see cref="Project"/> or null when nothing is selected/loaded.
    /// </summary>
    public Project? GetCurrentProject()
        => CurrentProject is null ? null : _activeSolution?.Loaded?.GetProject(CurrentProject);

    /// <summary>Disposes every solution workspace this environment owns. Idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposeSolutionWorkspaces();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

}

/// <summary>On-disk format of a solution file.</summary>
public enum SolutionFormat
{
    /// <summary>Classic <c>.sln</c> text format.</summary>
    Sln,

    /// <summary>XML-based <c>.slnx</c> format.</summary>
    Slnx,
}

/// <summary>
/// A solution discovered under the working directory: which solution it is
/// (<see cref="Name"/>, <see cref="Format"/>), where it lives
/// (<see cref="Path"/>, <see cref="Directory"/>) and — once the environment is opened —
/// its loaded Roslyn model (<see cref="Loaded"/>).
/// </summary>
public sealed class SnitchSolution
{
    private MSBuildWorkspace? _workspace;
    private Solution? _loaded;

    public SnitchSolution(string name, string path, SolutionFormat format)
    {
        Name = name;
        Path = path;
        Format = format;
    }

    /// <summary>File name without extension, e.g. <c>VisualMCP</c>.</summary>
    public string Name { get; }

    /// <summary>Absolute path to the <c>.sln</c>/<c>.slnx</c> file.</summary>
    public string Path { get; }

    /// <summary>Whether it is a classic <c>.sln</c> or an XML <c>.slnx</c>.</summary>
    public SolutionFormat Format { get; }

    /// <summary>Absolute directory that contains the solution file.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>
    /// The loaded Roslyn solution, or null until the environment is opened
    /// (see <see cref="SnitchContext.OpenEnvironmentAsync"/>) or if loading failed.
    /// </summary>
    public Solution? Loaded
    {
        get => Volatile.Read(ref _loaded);
        private set => Volatile.Write(ref _loaded, value);
    }

    /// <summary>
    /// Swaps the loaded solution for <paramref name="replacement"/>, but only if nobody swapped it since
    /// <paramref name="expected"/> was read. False means another writer won the race: re-read
    /// <see cref="Loaded"/>, rebuild the change on top of it, and try again (see <see cref="TryUpdate"/>).
    /// </summary>
    public bool TryReplaceLoaded(Solution? expected, Solution replacement) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _loaded, replacement, expected), expected);

    /// <summary>
    /// Applies <paramref name="change"/> to the current snapshot, retrying when another writer swapped it
    /// in the meantime. This is what keeps concurrent refactorings from erasing each other: the change is
    /// always rebuilt on the snapshot it is about to replace, never on the one the caller started from.
    /// The lambda may run more than once, so it must be pure — read from its argument, change nothing else.
    /// </summary>
    /// <returns>False when nothing was loaded or <paramref name="change"/> declined the snapshot (null).</returns>
    public bool TryUpdate(Func<Solution, Solution?> change)
    {
        while (true)
        {
            var current = Loaded;
            if (current is null) return false;

            var next = change(current);
            if (next is null) return false;

            if (TryReplaceLoaded(current, next)) return true;
        }
    }

    /// <summary>True once <see cref="Loaded"/> holds a successfully loaded solution.</summary>
    public bool IsLoaded => Loaded is not null;

    /// <summary>
    /// Workspace diagnostics gathered while loading this solution (empty when clean or
    /// not yet loaded).
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    /// <summary>Records the outcome of a load attempt, replacing any previous workspace.</summary>
    internal void Attach(MSBuildWorkspace? workspace, Solution? loaded, IReadOnlyList<string> diagnostics)
    {
        _workspace?.Dispose();
        _workspace = workspace;
        Loaded = loaded;
        Diagnostics = diagnostics;
    }

    /// <summary>Disposes the owned workspace and clears the loaded model.</summary>
    internal void DisposeWorkspace()
    {
        _workspace?.Dispose();
        _workspace = null;
        Loaded = null;
    }

    /// <summary>Builds a <see cref="SnitchSolution"/> from a solution file path.</summary>
    internal static SnitchSolution FromPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var format = full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? SolutionFormat.Slnx
            : SolutionFormat.Sln;
        return new SnitchSolution(
            System.IO.Path.GetFileNameWithoutExtension(full), full, format);
    }
}

/// <summary>
/// A Markdown document discovered under the working directory: which doc it is
/// (<see cref="Name"/>, <see cref="FileName"/>) and where it lives
/// (<see cref="Path"/>, <see cref="Directory"/>, <see cref="RelativePath"/>).
/// </summary>
public sealed class SnitchDoc
{
    public SnitchDoc(string path, string relativePath, long sizeBytes, string? content, bool contentOmitted)
    {
        Path = path;
        RelativePath = relativePath;
        SizeBytes = sizeBytes;
        Content = content;
        ContentOmitted = contentOmitted;
    }

    /// <summary>Absolute path to the document file.</summary>
    public string Path { get; }

    /// <summary>Path relative to the working directory, e.g. <c>docs/architecture.md</c>.</summary>
    public string RelativePath { get; }

    /// <summary>Size of the file on disk, in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>
    /// The file's text, truncated to a per-file cap — or null when the file was too large
    /// (<see cref="ContentOmitted"/>). Use <see cref="ReadText"/> for the full, untruncated text.
    /// </summary>
    public string? Content { get; }

    /// <summary>True when the file was too large to cache its content here.</summary>
    public bool ContentOmitted { get; }

    /// <summary>File name without extension, e.g. <c>README</c>.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>File name with extension, e.g. <c>README.md</c>.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Absolute directory that contains the document.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Reads the document's full, untruncated text from disk.</summary>
    public string ReadText() => File.ReadAllText(Path);
}
