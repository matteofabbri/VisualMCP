using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.Gate;

/// <summary>
/// The compiler verdict for one project state: the compilation, its errors already collected, and the
/// fingerprint that says which state it is. <see cref="FromCache"/> tells a repeated ask apart from a
/// fresh compile, and the timings say what the cache saved.
/// </summary>
public sealed class CachedCompilation
{
    internal CachedCompilation(
        ProjectId projectId,
        string projectName,
        Compilation compilation,
        string fingerprint,
        TimeSpan buildTime,
        bool fromCache)
    {
        ProjectId = projectId;
        ProjectName = projectName;
        Compilation = compilation;
        Fingerprint = fingerprint;
        BuildTime = buildTime;
        FromCache = fromCache;
        Errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        Messages = Describe(Errors, projectName);
    }

    public ProjectId ProjectId { get; }

    public string ProjectName { get; }

    public Compilation Compilation { get; }

    public IReadOnlyList<Diagnostic> Errors { get; }

    /// <summary>The errors as <c>ID file:line message</c>, distinct: the gate compares these strings.</summary>
    public IReadOnlyList<string> Messages { get; }

    public string Fingerprint { get; }

    public TimeSpan BuildTime { get; }

    /// <summary>True when the compilation was already in the cache and nothing was compiled.</summary>
    public bool FromCache { get; }

    /// <summary>
    /// The same compilation with this document's text replaced. Roslyn re-parses only that document
    /// (<see cref="SyntaxTree.WithChangedText"/>) and keeps the already-resolved references, which is
    /// what makes per-edit isolation affordable.
    /// </summary>
    public CachedCompilation WithDocument(Document document, SourceText text) =>
        With([(document, text)]);

    public CachedCompilation With(IReadOnlyList<(Document Document, SourceText Text)> changes)
    {
        if (changes.Count == 0) return this;

        var compilation = Compilation;
        foreach (var (document, text) in changes)
        {
            var tree = Find(compilation, document);
            compilation = tree is null
                ? compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                    text,
                    document.Project.ParseOptions as CSharpParseOptions,
                    document.FilePath ?? document.Name))
                : compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(text));
        }

        return new CachedCompilation(ProjectId, ProjectName, compilation, Fingerprint + "+edit", TimeSpan.Zero, fromCache: false);
    }

    private static SyntaxTree? Find(Compilation compilation, Document document)
    {
        var filePath = document.FilePath;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (filePath is not null && string.Equals(tree.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return tree;
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.Equals(Path.GetFileName(tree.FilePath), document.Name, StringComparison.OrdinalIgnoreCase))
                return tree;
        }

        return null;
    }

    /// <summary>The message shape the style engine and the refactoring gate have always compared.</summary>
    internal static IReadOnlyList<string> Describe(IEnumerable<Diagnostic> diagnostics, string projectName) => diagnostics
        .Select(diagnostic =>
        {
            var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
            var file = Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? projectName);
            return string.IsNullOrEmpty(file)
                ? $"{diagnostic.Id} {diagnostic.GetMessage()}"
                : $"{diagnostic.Id} {file}:{line} {diagnostic.GetMessage()}";
        })
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// Compiles a project once per state and hands the verdict back on every repeat. The gate recompiled
/// the whole project for its "before" and again for every candidate edit; the fingerprint (the
/// project's dependent semantic version plus a checksum per document) makes the "before" free and
/// <see cref="CachedCompilation.With"/> makes each "after" a re-parse of the changed document.
/// <para>
/// Nothing is persisted: Roslyn trees cannot be serialised, so what survives across calls is the
/// in-process entry only. The store keeps the other half — the per-document hashes that say which
/// files changed — and this class is where a future incremental gate can key on it.
/// </para>
/// </summary>
public sealed class CompilationCache
{
    private readonly ConcurrentDictionary<string, CachedCompilation> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;

    public CompilationCache(int capacity = 8) => _capacity = Math.Max(1, capacity);

    /// <summary>The process-wide cache: one solution model per server, so one cache.</summary>
    public static CompilationCache Shared { get; } = new();

    public async Task<CachedCompilation> GetAsync(Project project, CancellationToken cancellationToken = default)
    {
        var fingerprint = await FingerprintAsync(project, cancellationToken);
        if (_entries.TryGetValue(fingerprint, out var cached))
            return new CachedCompilation(
                cached.ProjectId, cached.ProjectName, cached.Compilation, cached.Fingerprint, TimeSpan.Zero, fromCache: true);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var compilation = await project.GetCompilationAsync(cancellationToken);
        started.Stop();

        // A project Roslyn cannot compile is not an error to report here: the caller has always treated
        // "no compilation" as "no errors", and a refusal built on it would point at the wrong thing.
        compilation ??= CSharpCompilation.Create(
            project.Name,
            references: Array.Empty<MetadataReference>(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var entry = new CachedCompilation(project.Id, project.Name, compilation, fingerprint, started.Elapsed, fromCache: false);
        Store(fingerprint, entry);
        return entry;
    }

    /// <summary>What the cache holds, for a status report.</summary>
    public IReadOnlyList<(string Project, string Fingerprint, int Errors, int Documents)> Snapshot() =>
        _entries.Values
            .OrderBy(entry => entry.ProjectName, StringComparer.Ordinal)
            .Select(entry => (
                entry.ProjectName,
                entry.Fingerprint,
                entry.Errors.Count,
                entry.Compilation.SyntaxTrees.Count()))
            .ToList();

    private void Store(string fingerprint, CachedCompilation entry)
    {
        if (!_entries.TryAdd(fingerprint, entry)) return;

        _order.Enqueue(fingerprint);
        while (_order.Count > _capacity && _order.TryDequeue(out var oldest))
            _entries.TryRemove(oldest, out _);
    }

    private static async Task<string> FingerprintAsync(Project project, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(project.Id.Id.ToString("N"));

        // The dependent version covers this project's documents and every project it references; the
        // per-document checksums cover the case where a text edit does not bump it, which would turn a
        // stale verdict into a silent wrong answer.
        var version = await project.GetDependentSemanticVersionAsync(cancellationToken);
        text.Append('|').Append(version.ToString());

        foreach (var document in project.Documents.OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            var content = await document.GetTextAsync(cancellationToken);
            text.Append('|').Append(document.Name).Append(':')
                .Append(Convert.ToHexString(content.GetChecksum().ToArray()));
        }

        foreach (var reference in project.MetadataReferences.Select(reference => reference.Display).OrderBy(name => name, StringComparer.Ordinal))
            text.Append('|').Append(reference);

        foreach (var reference in project.ProjectReferences.Select(reference => reference.ProjectId.Id).OrderBy(id => id))
            text.Append('|').Append(reference.ToString("N"));

        return text.ToString();
    }
}
