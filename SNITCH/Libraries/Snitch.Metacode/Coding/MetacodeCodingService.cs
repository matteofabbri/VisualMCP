using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Generation;
using Snitch.Metacode.Indexing;
using Snitch.Workspace;

namespace Snitch.Metacode.Coding;

/// <summary>
/// The write side of the metacode model: the operations that are more than a mechanical style rule.
/// </summary>
/// <remarks>
/// The mechanical improvements (simplify_code, the refactorings, code_cleanup) stay where they are: they
/// rewrite syntax Roslyn can already explain. This service is for the operations that go through the
/// <em>model</em> — decompile an assembly into the trees the store keeps, or take a tree (pseudocode, or
/// C# the caller already has) and make it the body of a declaration in the solution, compiler-gated.
/// It is the only writer that starts from a metacode tree rather than from a syntax node.
/// </remarks>
public interface IMetacodeCodingService
{
    /// <summary>
    /// Decompiles a binary and stores what it finds in <c>.visualMCP/visualmcp.db</c>, as part of the
    /// same metacode AST the solution's own files live in. A managed assembly becomes one metacode tree
    /// per type (under <c>assembly://&lt;file&gt;/&lt;type&gt;</c>, queryable with <c>metacode_index</c>);
    /// a binary with no CLR metadata is handed to Ghidra, whose recovered functions are stored as
    /// <c>native://&lt;file&gt;</c> — and, as before, in the dedicated native tables
    /// (<c>native_artifact</c>, <c>native_function</c>, <c>native_call</c>). Every stored row carries the
    /// binary in <c>source_path</c> and is flagged decompiled.
    /// <para>
    /// A binary already stored (same MVID, or same SHA-256 for a native one) is not decompiled again
    /// unless <paramref name="force"/> is set. With <paramref name="siblings"/> (the default) the folder
    /// the binary came from is swept afterwards: its text files are read into the store and its other
    /// binaries are decompiled, so one call fills the AST with the whole folder instead of one file.
    /// </para>
    /// </summary>
    Task<MetacodeCodingResult> DecompileAsync(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int maxTypes = 0,
        bool force = false,
        bool siblings = true,
        int maxSiblings = 25,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the code of an existing declaration with what <paramref name="source"/> describes and
    /// writes the emitted C# back to the file the declaration came from. <paramref name="language"/> is
    /// <c>pseudo</c> (the canonical metacode text, default) or <c>cs</c>. The edit is compiler-gated and
    /// previewed unless <paramref name="dryRun"/> is false.
    /// </summary>
    Task<MetacodeCodingResult> RewriteAsync(
        string symbol,
        string source,
        string? language = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new member to an existing type, generated from <paramref name="source"/>. The type is named
    /// simply (<c>Calculator</c>) or qualified (<c>Demo.Calculator</c>); the member is appended and the
    /// file compiler-gated and written on success.
    /// </summary>
    Task<MetacodeCodingResult> InsertAsync(
        string intoType,
        string source,
        string? language = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a folder sweep did after a decompilation: how many sibling files were read as text, decompiled
/// as managed assemblies, handed to Ghidra, or left alone, with the reason for the ones left alone.
/// </summary>
public sealed record FolderSweep(
    string Folder,
    int Text,
    int Managed,
    int Native,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Details)
{
    public int Total => Text + Managed + Java + Native + Skipped + Failed;

    /// <summary>Java archives decompiled with JADX in this sweep.</summary>
    public int Java { get; init; }
}

/// <summary>
/// What a metacoding operation produced. <see cref="Code"/> is the generated C#, <see cref="Diff"/> the
/// preview of a dry run, <see cref="Summary"/> the one-line answer a caller can print as-is.
/// </summary>
public sealed record MetacodeCodingResult(
    string Operation,
    string Target,
    bool Written,
    string Summary,
    string? File = null,
    string? Code = null,
    string? Diff = null,
    IReadOnlyList<string>? Diagnostics = null,
    string? Error = null)
{
    public bool IsError => Error is not null;

    /// <summary>What the sibling sweep of <c>decompile</c> did, when it ran.</summary>
    public FolderSweep? Sweep { get; init; }

    public static MetacodeCodingResult Failed(string operation, string target, string error) =>
        new(operation, target, false, error, Error: error);
}

/// <summary>
/// The implementation. It owns no state beyond its collaborators: the workspace (to read and write the
/// solution), the metacode index (the trees and their refresh), the ILSpy decompiler (the producer of
/// trees from a managed assembly), Ghidra (the same for a native image), JADX (the same for a Java
/// binary) and the native store (where the Ghidra output lands).
/// </summary>
public sealed class MetacodeCodingService : IMetacodeCodingService
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular);

    private readonly IRoslynWorkspace _workspace;
    private readonly IMetacodeIndexService _index;
    private readonly IMetacodeDecompileService _decompiler;
    private readonly IGhidraService _ghidra;
    private readonly IJadxService _jadx;
    private readonly INativeCodeStore _native;

    public MetacodeCodingService(
        IRoslynWorkspace workspace,
        IMetacodeIndexService index,
        IMetacodeDecompileService decompiler,
        IGhidraService ghidra,
        IJadxService jadx,
        INativeCodeStore native)
    {
        _workspace = workspace;
        _index = index;
        _decompiler = decompiler;
        _ghidra = ghidra;
        _jadx = jadx;
        _native = native;
    }

    // ── decompilation into the store ───────────────────────────────────────────────────

    public async Task<MetacodeCodingResult> DecompileAsync(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int maxTypes = 0,
        bool force = false,
        bool siblings = true,
        int maxSiblings = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return MetacodeCodingResult.Failed("decompile", assemblyPath, $"Assembly not found: {assemblyPath}");

        var result = await DecompileCoreAsync(
            assemblyPath, nameFilter, namespaceFilter, maxTypes, force, cancellationToken).ConfigureAwait(false);

        // The folder the binary came from is part of the same investigation: the text files next to it
        // are the documentation/config that binary belongs to, and its other binaries are the rest of
        // the program. Read and decompile them into the same AST, so one call answers "what is here".
        if (!siblings || result.IsError)
            return result;

        var folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return result;

        FolderSweep sweep;
        try
        {
            sweep = await SweepAsync(folder, assemblyPath, maxSiblings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return result with
            {
                Summary = result.Summary + " The sibling sweep failed: " + exception.Message,
                Sweep = new FolderSweep(folder, 0, 0, 0, 0, 1, [exception.Message]),
            };
        }

        return result with
        {
            Summary = result.Summary + " " + Describe(sweep),
            Sweep = sweep,
        };
    }

    /// <summary>The Java/managed/native half of <see cref="DecompileAsync"/>, without the folder sweep.</summary>
    private async Task<MetacodeCodingResult> DecompileCoreAsync(
        string assemblyPath,
        string? nameFilter,
        string? namespaceFilter,
        int maxTypes,
        bool force,
        CancellationToken cancellationToken)
    {
        // Three decompilers, one per family of binary, and the file's own bytes pick it: Java bytecode
        // (class/dex/zip-of-classes) goes to JADX, .NET metadata to ILSpy, and machine code to Ghidra.
        if (JavaArchiveDetector.IsJavaBinary(assemblyPath))
        {
            return await DecompileJavaAsync(assemblyPath, force, cancellationToken).ConfigureAwait(false);
        }

        // ILSpy reads metadata, so machine code has to go the other way: Ghidra recovers C, the model
        // lowers it, and the native tables (plus the AST) keep it. The detection is a PE header check,
        // not an exception from deep inside the decompiler.
        if (!ManagedAssemblyDetector.IsManaged(assemblyPath))
        {
            return await DecompileNativeAsync(
                assemblyPath, nameFilter, namespaceFilter, force, cancellationToken).ConfigureAwait(false);
        }

        var assembly = Path.GetFileName(assemblyPath);
        var mvid = AssemblyMvid.TryRead(assemblyPath);

        // Decompiling is the expensive half; the store's answer is the MVID it already holds.
        if (!force && mvid is not null && string.Equals(_index.DecompiledAssemblyHash(assembly), mvid, StringComparison.Ordinal))
        {
            return new MetacodeCodingResult(
                "decompile", assembly, false,
                $"{assembly} is already stored as metacode (MVID {mvid[..Math.Min(8, mvid.Length)]}); pass force=true to decompile it again.");
        }

        var decompilation = _decompiler.DecompileAssemblyToMetacode(
            assemblyPath, nameFilter, namespaceFilter, skip: 0, maxTypes: Math.Max(0, maxTypes));

        if (decompilation.IsError) return MetacodeCodingResult.Failed("decompile", assembly, decompilation.Error!);

        var report = await _index.IndexDecompilationAsync(decompilation, cancellationToken).ConfigureAwait(false);
        if (report.Error is not null) return MetacodeCodingResult.Failed("decompile", assembly, report.Error);

        var summary = new StringBuilder()
            .Append(assembly).Append(": ").Append(report.Indexed).Append(" type(s) stored as metacode trees — ")
            .Append(report.Declarations).Append(" declaration(s), ").Append(report.Functions).Append(" callable(s), ")
            .Append(report.Diagnostics).Append(" diagnostic(s)");
        if (report.Removed > 0) summary.Append(", ").Append(report.Removed).Append(" stale type(s) removed");
        if (report.Failed > 0) summary.Append(", ").Append(report.Failed).Append(" failed");
        summary.Append(". Query them with metacode_index (trees, complexity) — paths start with ");
        summary.Append(MetacodeIndexStore.DecompiledPrefix)
            .Append("; every row records this binary in source_path and is flagged decompiled.");

        return new MetacodeCodingResult(
            "decompile", assembly, report.Indexed > 0, summary.ToString(),
            Diagnostics: decompilation.Errors.Count > 0 ? decompilation.Errors : null);
    }

    /// <summary>
    /// The non-managed half of <see cref="DecompileCoreAsync"/>: Ghidra recovers the functions, the model
    /// gives each one a body and a complexity, and the result lands in two places — the dedicated native
    /// tables, and the unified metacode AST under <c>native://&lt;file&gt;</c> so it is queryable exactly
    /// like the solution's own code. Ghidra installs itself on first use; a failure names the run's log
    /// instead of throwing.
    /// </summary>
    private async Task<MetacodeCodingResult> DecompileNativeAsync(
        string binaryPath,
        string? nameFilter,
        string? namespaceFilter,
        bool force,
        CancellationToken cancellationToken)
    {
        var ingest = await IngestNativeAsync(binaryPath, force, cancellationToken).ConfigureAwait(false);
        if (ingest.Error is not null)
            return MetacodeCodingResult.Failed("decompile", ingest.Assembly, ingest.Error);

        var summary = new StringBuilder(ingest.Summary);
        if (!string.IsNullOrWhiteSpace(nameFilter) || !string.IsNullOrWhiteSpace(namespaceFilter))
        {
            summary.Append(" nameFilter/namespaceFilter do not apply to a native binary: ")
                .Append("use analyze_native_binary with function= to read one function back.");
        }

        return new MetacodeCodingResult(
            "decompile", ingest.Assembly, ingest.Stored, summary.ToString(),
            File: ingest.File, Diagnostics: ingest.Diagnostics);
    }

    /// <summary>What decompiling one native binary did, in the shape both the tool and the sweep report.</summary>
    private sealed record NativeIngest(
        string Assembly,
        bool Stored,
        string Summary,
        string? File = null,
        IReadOnlyList<string>? Diagnostics = null,
        string? Error = null);

    /// <summary>
    /// Run Ghidra on a native binary, save its functions in the native tables, and store the same
    /// functions as a metacode document — one entry point for the single-binary path and the sweep.
    /// </summary>
    private async Task<NativeIngest> IngestNativeAsync(string binaryPath, bool force, CancellationToken cancellationToken)
    {
        var assembly = Path.GetFileName(binaryPath);

        GhidraAnalysis analysis;
        try
        {
            analysis = await _ghidra.AnalyzeAsync(binaryPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new NativeIngest(assembly, false, string.Empty,
                Error: $"Ghidra could not run on {assembly}: {exception.GetType().Name}: {exception.Message}");
        }

        if (analysis.IsError || analysis.Snapshot is null)
        {
            var hint = " Ghidra is installed on demand under " + GhidraOptions.DefaultToolsDirectory +
                       "; set GHIDRA_INSTALL_DIR (or GhidraOptions.GhidraHome) to use a copy that is already there.";
            return new NativeIngest(assembly, false, string.Empty,
                Error: $"{assembly} is not a managed assembly, so ILSpy has no C# to read, and Ghidra could not decompile it: " +
                       (analysis.Error ?? "no snapshot was produced.") + hint);
        }

        var document = analysis.ToMetacode();

        NativeArtifactRecord artifact;
        try
        {
            artifact = _native.Save(analysis, force, document);
        }
        catch (Exception exception)
        {
            return new NativeIngest(assembly, false, string.Empty,
                Error: $"{assembly} is not managed: Ghidra recovered {analysis.Functions} function(s), but the solution store " +
                       $"refused them: {exception.GetType().Name}: {exception.Message}");
        }

        // The same functions, in the tables the rest of the metacode lives in: native code becomes part
        // of one AST instead of a side table only the native readers can query.
        var indexed = 0;
        string? astError = null;
        if (!artifact.IsError)
        {
            try
            {
                var ast = await _index.IndexNativeAsync(binaryPath, artifact.Sha256, document, cancellationToken).ConfigureAwait(false);
                if (ast.Error is null) indexed = ast.Declarations;
                else astError = ast.Error;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                astError = exception.Message;
            }
        }

        var summary = new StringBuilder()
            .Append(assembly).Append(" is not a managed assembly: Ghidra recovered ")
            .Append(analysis.Functions).Append(" function(s)");

        if (artifact.Error is not null)
        {
            summary.Append(". They were not stored: ").Append(artifact.Error);
        }
        else if (artifact.Stored)
        {
            summary.Append(" and stored them in ").Append(artifact.Database)
                .Append(" — native_artifact #").Append(artifact.Id)
                .Append(", native_function (").Append(analysis.Functions).Append(" row(s)), native_call (call edges).")
                .Append(" Each function row carries Ghidra's C and the canonical pseudocode; ")
                .Append("the complexity columns come from the same model as the rest of the solution.")
                .Append(" Image ").Append(analysis.Snapshot.Image)
                .Append(" (").Append(analysis.Snapshot.Language).Append(')');
            if (!string.IsNullOrWhiteSpace(analysis.Snapshot.Entry))
                summary.Append(", entry ").Append(analysis.Snapshot.Entry);
        }
        else
        {
            summary.Append(", already stored as native_artifact #").Append(artifact.Id)
                .Append(" (SHA-256 ").Append(artifact.Sha256[..Math.Min(12, artifact.Sha256.Length)])
                .Append("); pass force=true to decompile it again.");
        }

        if (astError is not null)
            summary.Append(" The metacode AST did not take them: ").Append(astError);
        else if (indexed > 0)
            summary.Append(" They are also in the metacode AST as ")
                .Append(MetacodeIndexStore.NativePrefix).Append(assembly)
                .Append(" (").Append(indexed).Append(" declaration(s), decompiled).");

        return new NativeIngest(
            assembly,
            artifact.Stored || indexed > 0,
            summary.ToString(),
            analysis.OutputJsonPath,
            document.Diagnostics.Count > 0
                ? document.Diagnostics.Take(5).Select(diagnostic => diagnostic.ToString()).ToList()
                : null);
    }

    /// <summary>
    /// The Java half of <see cref="DecompileCoreAsync"/>: JADX recovers the sources, the Java frontend
    /// lowers them into the model, and every decompiled file becomes a document under
    /// <c>java://&lt;archive&gt;/&lt;unit&gt;</c> in the same AST. JADX installs itself on first use; a
    /// failure names the run's log instead of throwing.
    /// </summary>
    private async Task<MetacodeCodingResult> DecompileJavaAsync(
        string archivePath, bool force, CancellationToken cancellationToken)
    {
        var ingest = await IngestJavaAsync(archivePath, force, cancellationToken).ConfigureAwait(false);
        if (ingest.Error is not null)
            return MetacodeCodingResult.Failed("decompile", ingest.Archive, ingest.Error);

        return new MetacodeCodingResult(
            "decompile", ingest.Archive, ingest.Stored, ingest.Summary,
            File: ingest.File, Diagnostics: ingest.Diagnostics);
    }

    /// <summary>What decompiling one Java archive did, in the shape both the tool and the sweep report.</summary>
    private sealed record JavaIngest(
        string Archive,
        bool Stored,
        string Summary,
        string? File = null,
        IReadOnlyList<string>? Diagnostics = null,
        string? Error = null);

    /// <summary>
    /// Run JADX on a Java binary, lower every source it wrote with the Java frontend, and store them in
    /// the metacode AST — one entry point for the single-archive path and the sweep.
    /// </summary>
    private async Task<JavaIngest> IngestJavaAsync(string archivePath, bool force, CancellationToken cancellationToken)
    {
        var archive = Path.GetFileName(archivePath);
        var sha = NativeCodeStore.HashOf(archivePath);

        // JADX on a large APK is minutes, not milliseconds: an archive the store already holds is not
        // run again unless the caller asks for it.
        if (!force && string.Equals(_index.DecompiledJavaHash(archive), sha, StringComparison.Ordinal))
        {
            return new JavaIngest(archive, false,
                $"{archive} is already stored as metacode (SHA-256 {sha[..Math.Min(12, sha.Length)]}); " +
                "pass force=true to decompile it again.");
        }

        JadxAnalysis analysis;
        try
        {
            analysis = await _jadx.DecompileAsync(archivePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new JavaIngest(archive, false, string.Empty,
                Error: $"JADX could not run on {archive}: {exception.GetType().Name}: {exception.Message}");
        }

        if (analysis.IsError)
        {
            var hint = " JADX is installed on demand under " + JadxOptions.DefaultToolsDirectory +
                       "; set JADX_HOME (or JADX_CLI) to use a copy that is already there, and make sure a JDK is installed.";
            return new JavaIngest(archive, false, string.Empty, analysis.LogPath,
                Error: $"{archive} is Java bytecode and JADX produced no source: " +
                       (analysis.Error ?? "no .java file was written.") + hint);
        }

        var units = new List<JavaSourceUnit>(analysis.Sources.Count);
        var failures = new List<string>();

        foreach (var source in analysis.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(source.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{source.RelativePath}: {exception.Message}");
                continue;
            }

            // Through the translator, not the frontend directly, so Java gets the same normalization
            // passes as every other language (a C-style `for` becomes a `while`).
            var document = MetacodeTranslator.Default.Translate(
                MetacodeSource.FromJava(text, source.Path), MetacodeOptions.Default);

            units.Add(new JavaSourceUnit(source.RelativePath, source.TypeName, document, text));
        }

        if (units.Count == 0)
        {
            return new JavaIngest(archive, false, string.Empty, analysis.LogPath,
                Error: $"{archive}: JADX wrote {analysis.Files} source file(s) but none could be read. " +
                       "The log is at " + analysis.LogPath + ".");
        }

        MetacodeBuildReport report;
        try
        {
            report = await _index.IndexJavaAsync(archivePath, sha, units, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new JavaIngest(archive, false, string.Empty, analysis.LogPath,
                Error: $"{archive}: JADX produced {units.Count} source file(s), but the solution store refused them: " +
                       $"{exception.GetType().Name}: {exception.Message}");
        }

        if (report.Error is not null)
            return new JavaIngest(archive, false, string.Empty, analysis.LogPath, Error: report.Error);

        var summary = new StringBuilder()
            .Append(archive).Append(" is Java bytecode: JADX recovered ").Append(analysis.Files)
            .Append(" source file(s) in ").Append(analysis.Elapsed.TotalSeconds.ToString("0.0")).Append(" s and ")
            .Append(report.Indexed).Append(" of them are stored as metacode trees — ")
            .Append(report.Declarations).Append(" declaration(s), ").Append(report.Functions).Append(" callable(s), ")
            .Append(report.Diagnostics).Append(" diagnostic(s)");
        if (analysis.Truncated > 0)
            summary.Append(", ").Append(analysis.Truncated).Append(" file(s) past the cap were not read");
        if (report.Removed > 0) summary.Append(", ").Append(report.Removed).Append(" stale unit(s) removed");
        if (report.Failed > 0) summary.Append(", ").Append(report.Failed).Append(" failed");
        summary.Append(". Query them with metacode_index (trees, complexity) — paths start with ")
            .Append(MetacodeIndexStore.JavaPrefix)
            .Append("; every row records the archive in source_path and is flagged decompiled.");

        var diagnostics = new List<string>();
        if (analysis.LogPath is not null) diagnostics.Add("log: " + analysis.LogPath);
        diagnostics.AddRange(failures.Take(5));
        diagnostics.AddRange(report.Failures.Take(5));

        return new JavaIngest(archive, report.Indexed > 0, summary.ToString(), analysis.LogPath,
            diagnostics.Count > 0 ? diagnostics : null);
    }

    // ── the folder a binary came from ─────────────────────────────────────────────────

    /// <summary>
    /// How many native siblings the sweep may hand to Ghidra. A Ghidra run is tens of seconds, not
    /// milliseconds: a folder of a dozen executables must not turn one decompile into a coffee break.
    /// </summary>
    private const int MaxNativeSiblings = 2;

    /// <summary>A sibling bigger than this is a data file, not a program or a source worth reading here.</summary>
    private const long MaxSiblingBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How many Java archives the sweep may hand to JADX. One: a jar in a folder is already the whole
    /// program, and decompiling all of its dependencies would spend minutes on code nobody asked about.
    /// </summary>
    private const int MaxJavaSiblings = 1;

    /// <summary>
    /// The same folder as the binary just decompiled, read into the same store: every text file goes in
    /// as a document (a <c>file</c> node when no frontend reads it), every other managed assembly is
    /// decompiled, a Java archive is handed to JADX, and a couple of native images are handed to Ghidra.
    /// Bounded by <paramref name="maxSiblings"/> files and by <see cref="MaxNativeSiblings"/> /
    /// <see cref="MaxJavaSiblings"/> runs, and idempotent: a file the store already holds is skipped on its
    /// hash, a binary already decompiled on its MVID/SHA.
    /// </summary>
    private async Task<FolderSweep> SweepAsync(
        string folder, string sourcePath, int maxSiblings, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var texts = new List<string>();
        var managed = new List<string>();
        var java = new List<string>();
        var native = new List<string>();
        int skipped = 0, failed = 0;

        var self = Path.GetFullPath(sourcePath);
        var budget = Math.Clamp(maxSiblings, 0, 500);

        string[] siblings;
        try
        {
            siblings = Directory.GetFiles(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FolderSweep(folder, 0, 0, 0, 0, 1, ["The folder could not be read: " + exception.Message]);
        }

        var candidates = siblings
            .Select(Path.GetFullPath)
            .Where(file => !string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var examined = 0;
        foreach (var file in candidates)
        {
            if (examined >= budget)
            {
                details.Add($"{candidates.Count - examined} more file(s) were not examined (maxSiblings={budget}).");
                break;
            }

            var name = Path.GetFileName(file);
            examined++;

            try
            {
                var length = new FileInfo(file).Length;
                if (length > MaxSiblingBytes)
                {
                    skipped++;
                    details.Add($"{name}: larger than {MaxSiblingBytes / (1024 * 1024)} MB");
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                details.Add($"{name}: {exception.Message}");
                continue;
            }

            if (WorkspaceSources.IsTextFile(file))
            {
                texts.Add(file);
                continue;
            }

            // Java bytecode comes before the executable-magic check: a .class starts with CAFEBABE,
            // which is also a Mach-O fat binary, and a .jar is a zip and would otherwise be dismissed as
            // "neither text nor an executable image".
            if (JavaArchiveDetector.IsJavaBinary(file))
            {
                java.Add(file);
                continue;
            }

            if (!LooksLikeBinary(file))
            {
                // A database, an image, a PDB: not code and not a program this folder describes.
                skipped++;
                details.Add($"{name}: neither text nor an executable image");
                continue;
            }

            if (ManagedAssemblyDetector.IsManaged(file)) managed.Add(file);
            else native.Add(file);
        }

        // The text files, through the same incremental path a build pass uses.
        if (texts.Count > 0)
        {
            try
            {
                var report = await _index.IndexFilesAsync(texts, projectName: null, cancellationToken).ConfigureAwait(false);
                if (report.Error is not null)
                {
                    failed += texts.Count;
                    details.Add("text files: " + report.Error);
                }
                else if (report.Failed > 0)
                {
                    failed += report.Failed;
                    details.AddRange(report.Failures);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed += texts.Count;
                details.Add("text files: " + exception.Message);
            }
        }

        var managedDecompiled = 0;
        foreach (var assembly in managed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(assembly);
            try
            {
                var result = await DecompileCoreAsync(assembly, null, null, 0, force: false, cancellationToken)
                    .ConfigureAwait(false);

                if (result.IsError)
                {
                    failed++;
                    details.Add($"{name}: {result.Error}");
                }
                else if (result.Written)
                {
                    managedDecompiled++;
                }
                else
                {
                    // The store already held this exact binary (same MVID): nothing was decompiled again.
                    skipped++;
                    details.Add($"{name}: already stored");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                details.Add($"{name}: {exception.Message}");
            }
        }

        var javas = 0;
        foreach (var archive in java)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(archive);
            if (javas >= MaxJavaSiblings)
            {
                skipped++;
                details.Add($"{name}: Java archive not decompiled (at most {MaxJavaSiblings} per sweep — " +
                            "pass its path to decompile it on its own)");
                continue;
            }

            try
            {
                var ingest = await IngestJavaAsync(archive, force: false, cancellationToken).ConfigureAwait(false);
                if (ingest.Error is not null)
                {
                    failed++;
                    details.Add($"{name}: {ingest.Error}");
                }
                else if (ingest.Stored)
                {
                    javas++;
                }
                else
                {
                    // The store already held this exact archive (same SHA-256).
                    skipped++;
                    details.Add($"{name}: already stored");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                details.Add($"{name}: {exception.Message}");
            }
        }

        var natives = 0;
        foreach (var binary in native)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(binary);
            if (natives >= MaxNativeSiblings)
            {
                skipped++;
                details.Add($"{name}: native binary not decompiled (at most {MaxNativeSiblings} per sweep — " +
                            "pass its path to decompile it on its own)");
                continue;
            }

            try
            {
                // Ghidra is the expensive half: a binary the native table already holds is not run again.
                var sha = NativeCodeStore.HashOf(binary);
                if (_native.Find(sha) is not null)
                {
                    skipped++;
                    details.Add($"{name}: already stored (SHA-256 {sha[..12]})");
                    continue;
                }

                var ingest = await IngestNativeAsync(binary, force: false, cancellationToken).ConfigureAwait(false);
                if (ingest.Error is not null)
                {
                    failed++;
                    details.Add($"{name}: {ingest.Error}");
                }
                else
                {
                    natives++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                details.Add($"{name}: {exception.Message}");
            }
        }

        return new FolderSweep(folder, texts.Count, managedDecompiled, natives, skipped, failed, details)
        {
            Java = javas,
        };
    }

    /// <summary>Whether a file starts with the magic of a PE, ELF or Mach-O image.</summary>
    private static bool LooksLikeBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;

            if (head[0] == (byte)'M' && head[1] == (byte)'Z') return true;             // PE / DOS
            if (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F') return true;

            var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head);
            return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCAFEBABE or 0xCEFAEDFE or 0xCAFEBABF;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The sweep as one sentence, for the decompile result's summary.</summary>
    private static string Describe(FolderSweep sweep)
    {
        var summary = new StringBuilder()
            .Append("Swept ").Append(Path.GetFileName(sweep.Folder) ?? sweep.Folder).Append(": ")
            .Append(sweep.Text).Append(" text file(s) read, ")
            .Append(sweep.Managed).Append(" managed assembly(ies) decompiled, ")
            .Append(sweep.Java).Append(" Java archive(s) decompiled, ")
            .Append(sweep.Native).Append(" native binary(ies) parsed, ")
            .Append(sweep.Skipped).Append(" skipped, ")
            .Append(sweep.Failed).Append(" failed");

        var notes = sweep.Details.Where(detail => !detail.Contains("were not examined", StringComparison.Ordinal)).Take(5).ToList();
        if (notes.Count > 0) summary.Append(" — ").Append(string.Join("; ", notes));

        return summary.Append('.').ToString();
    }

    // ── trees back into the solution ───────────────────────────────────────────────────

    public async Task<MetacodeCodingResult> RewriteAsync(
        string symbol,
        string source,
        string? language = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return MetacodeCodingResult.Failed("rewrite", symbol, "rewrite needs a symbol.");

        var (code, diagnostics, emitError) = Emit(source, language);
        if (emitError is not null) return MetacodeCodingResult.Failed("rewrite", symbol, emitError);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return MetacodeCodingResult.Failed("rewrite", symbol, "No solution is loaded. Call load_solution first.");

        var rows = _index.Trees(symbol, limit: 50)
            .Where(candidate => File.Exists(candidate.Path))
            .ToList();

        // A name can match several declarations (an overload, the same name in two types). Prefer the
        // exact key, then the exact name; if the survivors still live in different files, say so instead
        // of rewriting whichever happened to come first.
        var exact = rows
            .Where(candidate =>
                string.Equals(candidate.Key, symbol, StringComparison.Ordinal) ||
                candidate.Key.EndsWith("." + symbol, StringComparison.Ordinal) ||
                string.Equals(candidate.Name, symbol, StringComparison.Ordinal))
            .ToList();
        var pool = exact.Count > 0 ? exact : rows;

        if (pool.Count == 0)
        {
            return MetacodeCodingResult.Failed("rewrite", symbol,
                $"The metacode store has no declaration '{symbol}' in a file on disk. " +
                "Build it with metacode_index action=build, or use insert for new code.");
        }

        var files = pool.Select(candidate => candidate.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > 1)
        {
            return MetacodeCodingResult.Failed("rewrite", symbol,
                $"'{symbol}' is ambiguous: it matches declarations in {files.Count} files — " +
                string.Join(", ", files.Select(Path.GetFileName).Take(5)) + ". Use a qualified name or a declaration key.");
        }

        var row = pool[0];

        // The emitter produces C#, so the edit can only land in a C# file. The declaration may have come
        // from any language the model reads — a page, a .vb file, a JavaScript module — and says so here
        // instead of failing later on a document the solution does not have.
        if (MetacodeEmission.Refusal(row.Path) is { } refusal)
            return MetacodeCodingResult.Failed("rewrite", symbol, refusal);

        var document = FindDocument(solution, row.Path);
        if (document is null)
            return MetacodeCodingResult.Failed("rewrite", symbol, $"{row.Path} is not part of the loaded solution.");

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return MetacodeCodingResult.Failed("rewrite", symbol, $"{row.Path} has no syntax tree.");

        var target = FindDeclaration(root, text.Lines, row);
        if (target is null)
        {
            return MetacodeCodingResult.Failed("rewrite", symbol,
                $"'{symbol}' was stored for {Path.GetFileName(row.Path)}:{row.Line}, but that declaration is no longer there. " +
                "Rebuild the index (metacode_index action=build).");
        }

        var replacement = BuildReplacement(target, text, code);
        if (replacement is null)
        {
            return MetacodeCodingResult.Failed("rewrite", symbol,
                "The generated code is not a declaration that can stand where the old one was.");
        }

        return await ApplyAsync(
            document, root, root.ReplaceNode(target, replacement),
            "rewrite", $"{symbol} in {Path.GetFileName(row.Path)}",
            code, diagnostics, dryRun, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MetacodeCodingResult> InsertAsync(
        string intoType,
        string source,
        string? language = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(intoType))
            return MetacodeCodingResult.Failed("insert", intoType, "insert needs the type to insert into.");

        var (code, diagnostics, emitError) = Emit(source, language);
        if (emitError is not null) return MetacodeCodingResult.Failed("insert", intoType, emitError);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return MetacodeCodingResult.Failed("insert", intoType, "No solution is loaded. Call load_solution first.");

        var (document, type) = await FindTypeAsync(solution, intoType, cancellationToken).ConfigureAwait(false);
        if (document is null || type is null)
            return MetacodeCodingResult.Failed("insert", intoType, $"No type named '{intoType}' in the loaded solution.");

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return MetacodeCodingResult.Failed("insert", intoType, $"{document.Name} has no syntax tree.");

        var member = BuildMember(type, text, code);
        if (member is null)
        {
            return MetacodeCodingResult.Failed("insert", intoType,
                "The generated code is not a member C# can place in a type.");
        }

        return await ApplyAsync(
            document, root, root.ReplaceNode(type, type.AddMembers(member)),
            "insert", $"{type.Identifier.Text} in {Path.GetFileName(document.FilePath) ?? document.Name}",
            code, diagnostics, dryRun, cancellationToken).ConfigureAwait(false);
    }

    // ── the shared edit path ──────────────────────────────────────────────────────────

    /// <summary>
    /// Preview, gate, write. Every metacoding edit goes through here so the diff, the compiler verdict and
    /// the write are the same code for both operations.
    /// </summary>
    private async Task<MetacodeCodingResult> ApplyAsync(
        Document document,
        SyntaxNode oldRoot,
        SyntaxNode newRoot,
        string operation,
        string target,
        string code,
        IReadOnlyList<string> diagnostics,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // The last line of defence, so no future caller of the shared edit path can write a language the
        // emitter does not produce either.
        if (MetacodeEmission.Refusal(document.FilePath) is { } refusal)
            return MetacodeCodingResult.Failed(operation, target, refusal);

        var oldText = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        var newText = newRoot.ToFullString();
        var file = document.FilePath;

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return new MetacodeCodingResult(
                operation, target, false,
                "The generated code is identical to what the file already contains.",
                file, code, Diagnostics: diagnostics);
        }

        if (dryRun)
        {
            var changed = CountChangedLines(oldText, newText);
            return new MetacodeCodingResult(
                operation, target, false,
                $"Preview only — {changed} line(s) would change. Pass dryRun=false to write.",
                file, code, RefactoringFileWriter.Diff(oldText, newText), diagnostics);
        }

        var updated = document.Project.Solution.WithDocumentSyntaxRoot(document.Id, newRoot);
        var gate = await RefactoringCompilerGate
            .CompareAsync(document.Project.Solution, updated, [document.Project.Id], cancellationToken)
            .ConfigureAwait(false);

        if (!gate.Passed)
        {
            var errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors;
            return new MetacodeCodingResult(
                operation, target, false,
                $"Refused: the generated code would add {gate.Added} compiler error(s) ({gate.Before} -> {gate.After}).",
                file, code, RefactoringFileWriter.Diff(oldText, newText), errors)
            {
                Error = $"Refused: the generated code would add {gate.Added} compiler error(s). Nothing was written.",
            };
        }

        await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken).ConfigureAwait(false);

        // The store is the model the next call reads: bring it in step with the file just written.
        try
        {
            await _index.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed refresh costs freshness, not correctness: the next pass picks the file up.
        }

        return new MetacodeCodingResult(
            operation, target, true,
            $"Written to {Path.GetFileName(file) ?? file} — compiler error count unchanged ({gate.After}).",
            file, code, RefactoringFileWriter.Diff(oldText, newText), diagnostics);
    }

    /// <summary>How many lines actually differ: everything between the common prefix and the common suffix.</summary>
    private static int CountChangedLines(string oldText, string newText)
    {
        var before = oldText.Replace("\r\n", "\n").Split('\n');
        var after = newText.Replace("\r\n", "\n").Split('\n');

        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length &&
               string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
               string.Equals(before[before.Length - 1 - suffix], after[after.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        return (before.Length - prefix - suffix) + (after.Length - prefix - suffix);
    }

    // ── source → C# ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The C# a caller asked for. <c>pseudo</c> (the default) is read by the pseudocode frontend and
    /// emitted by the C# emitter; <c>cs</c> is taken as written. Both come back with the diagnostics of the
    /// direction that produced them.
    /// </summary>
    private static (string Code, IReadOnlyList<string> Diagnostics, string? Error) Emit(string source, string? language)
    {
        if (string.IsNullOrWhiteSpace(source))
            return (string.Empty, Array.Empty<string>(), "Nothing to generate: the source is empty.");

        var tag = string.IsNullOrWhiteSpace(language) ? "pseudo" : language.Trim().ToLowerInvariant();
        switch (tag)
        {
            case "cs" or "c#" or "csharp":
                return (source.Trim(), Array.Empty<string>(), null);

            case "pseudo" or "pseudocode" or "mc" or "metacode":
                break;

            default:
                return (string.Empty, Array.Empty<string>(),
                    $"language must be 'pseudo' (metacode text, default) or 'cs'; got '{language}'.");
        }

        var text = source.Contains("MODULE", StringComparison.Ordinal)
            ? source
            : "MODULE generated\n" + source.TrimEnd() + "\nEND MODULE\n";

        // A translator per call: the frontends carry per-translation state (their diagnostics), so the
        // shared default instance is not safe across concurrent tool calls.
        var translator = new MetacodeTranslator([new PseudocodeFrontend()]);
        var model = translator.Translate(MetacodeSource.FromPseudocode(text), MetacodeOptions.Default);

        var diagnostics = model.Diagnostics.Select(diagnostic => diagnostic.ToString()).ToList();
        if (model.Modules.SelectMany(module => module.Declarations).FirstOrDefault() is not { } declaration)
        {
            return (string.Empty, diagnostics,
                "The source did not read back as one declaration (" + model.Diagnostics.Count + " diagnostic(s)).");
        }

        var emitted = new CSharpEmitter(CSharpEmitOptions.Default with { IncludeUsings = false }).Emit(declaration);
        diagnostics.AddRange(emitted.Diagnostics);
        return (emitted.Code.TrimEnd(), diagnostics, null);
    }

    // ── syntax placement ─────────────────────────────────────────────────────────────

    /// <summary>Parses the generated code as a member declaration, indented like the node it replaces.</summary>
    private static MemberDeclarationSyntax? BuildReplacement(MemberDeclarationSyntax target, SourceText text, string code)
    {
        var line = text.Lines.GetLineFromPosition(target.SpanStart);
        var indent = new string(' ', Math.Max(0, target.SpanStart - line.Start));
        var parsed = SyntaxFactory.ParseMemberDeclaration(Indent(code, indent), options: ParseOptions);
        if (parsed is null) return null;

        return parsed
            .WithLeadingTrivia(target.GetLeadingTrivia())
            .WithTrailingTrivia(target.GetTrailingTrivia());
    }

    /// <summary>
    /// Parses the generated code as a member and appends it to the type, indented like the type's first
    /// member (or one level inside the type when it has none).
    /// </summary>
    private static MemberDeclarationSyntax? BuildMember(TypeDeclarationSyntax type, SourceText text, string code)
    {
        string indent;
        if (type.Members.Count > 0)
        {
            var first = type.Members[0];
            var line = text.Lines.GetLineFromPosition(first.SpanStart);
            indent = new string(' ', Math.Max(0, first.SpanStart - line.Start));
        }
        else
        {
            var line = text.Lines.GetLineFromPosition(type.SpanStart);
            indent = new string(' ', Math.Max(0, type.SpanStart - line.Start) + 4);
        }

        var parsed = SyntaxFactory.ParseMemberDeclaration(Indent(code, indent), options: ParseOptions);
        if (parsed is null) return null;

        // A blank line before it, and the indentation of the closing brace after it.
        return parsed
            .WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia("\n" + indent))
            .WithTrailingTrivia(SyntaxFactory.ParseTrailingTrivia("\n"));
    }

    private static string Indent(string code, string indent)
    {
        var lines = code.Replace("\r\n", "\n").TrimEnd().Split('\n');
        var builder = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            builder.Append('\n');
            if (lines[i].Length > 0) builder.Append(indent);
            builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    private static MemberDeclarationSyntax? FindDeclaration(
        SyntaxNode root, TextLineCollection lines, MetacodeTreeRow row)
    {
        var candidates = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(declaration => declaration is not BaseNamespaceDeclarationSyntax)
            .ToList();

        var byLine = row.Line > 0
            ? candidates.Where(declaration => StartLine(declaration, lines) == row.Line).ToList()
            : [];

        var named = byLine.Where(declaration => NameOf(declaration) == row.Name).ToList();
        if (named.Count == 1) return named[0];
        if (byLine.Count == 1) return byLine[0];

        var byName = candidates.Where(declaration => NameOf(declaration) == row.Name).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static int StartLine(SyntaxNode node, TextLineCollection lines) =>
        lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;

    private static string? NameOf(MemberDeclarationSyntax declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.Text,
        MethodDeclarationSyntax method => method.Identifier.Text,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.Text,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        IndexerDeclarationSyntax => "this",
        EventDeclarationSyntax @event => @event.Identifier.Text,
        OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EnumMemberDeclarationSyntax member => member.Identifier.Text,
        _ => null,
    };

    private static Document? FindDocument(Solution solution, string path)
    {
        var full = FullPath(path);
        return solution.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(document =>
                document.FilePath is not null &&
                string.Equals(FullPath(document.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The type to insert into, preferring an exact qualified name over a simple one over a suffix, so
    /// <c>Demo.Calculator</c> is not lost to another <c>Calculator</c> that happens to be read first.
    /// </summary>
    private static async Task<(Document? Document, TypeDeclarationSyntax? Type)> FindTypeAsync(
        Solution solution, string name, CancellationToken cancellationToken)
    {
        FoundType? qualified = null;
        FoundType? simple = null;
        FoundType? suffix = null;

        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            if (document.FilePath is not { } path || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) continue;

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var identifier = type.Identifier.Text;

                if (qualified is null && string.Equals(QualifiedNameOf(type), name, StringComparison.Ordinal))
                    qualified = new FoundType(document, type);

                if (simple is null && string.Equals(identifier, name, StringComparison.Ordinal))
                    simple = new FoundType(document, type);

                if (suffix is null && identifier.EndsWith(name, StringComparison.Ordinal))
                    suffix = new FoundType(document, type);
            }
        }

        var found = qualified ?? simple ?? suffix;
        return found is null ? (null, null) : (found.Document, found.Type);
    }

    private sealed record FoundType(Document Document, TypeDeclarationSyntax Type);

    private static string QualifiedNameOf(TypeDeclarationSyntax type)
    {
        var parts = type.Ancestors()
            .Where(node => node is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            .Reverse()
            .Select(node => node switch
            {
                BaseTypeDeclarationSyntax outer => outer.Identifier.Text,
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                _ => string.Empty,
            })
            .Where(part => part.Length > 0)
            .Concat([type.Identifier.Text]);

        return string.Join(".", parts);
    }

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }
}
