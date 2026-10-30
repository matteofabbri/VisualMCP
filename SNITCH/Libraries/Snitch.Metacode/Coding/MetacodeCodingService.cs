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
    /// Decompiles an assembly and stores what it finds in <c>.visualMCP/visualmcp.db</c>. A managed
    /// assembly becomes one metacode tree per type (under <c>assembly://&lt;file&gt;/&lt;type&gt;</c>,
    /// queryable with <c>metacode_index</c>); a binary with no CLR metadata is handed to Ghidra, which
    /// recovers its functions, and those are stored in the dedicated native tables
    /// (<c>native_artifact</c>, <c>native_function</c>, <c>native_call</c>).
    /// A binary already stored (same MVID, or same SHA-256 for a native one) is not decompiled again
    /// unless <paramref name="force"/> is set.
    /// </summary>
    Task<MetacodeCodingResult> DecompileAsync(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int maxTypes = 0,
        bool force = false,
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

    public static MetacodeCodingResult Failed(string operation, string target, string error) =>
        new(operation, target, false, error, Error: error);
}

/// <summary>
/// The implementation. It owns no state beyond its collaborators: the workspace (to read and write the
/// solution), the metacode index (the trees and their refresh), the ILSpy decompiler (the producer of
/// trees from a managed assembly), Ghidra (the same for a native image) and the native store (where the
/// latter's output lands).
/// </summary>
public sealed class MetacodeCodingService : IMetacodeCodingService
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular);

    private readonly IRoslynWorkspace _workspace;
    private readonly IMetacodeIndexService _index;
    private readonly IMetacodeDecompileService _decompiler;
    private readonly IGhidraService _ghidra;
    private readonly INativeCodeStore _native;

    public MetacodeCodingService(
        IRoslynWorkspace workspace,
        IMetacodeIndexService index,
        IMetacodeDecompileService decompiler,
        IGhidraService ghidra,
        INativeCodeStore native)
    {
        _workspace = workspace;
        _index = index;
        _decompiler = decompiler;
        _ghidra = ghidra;
        _native = native;
    }

    // ── decompilation into the store ───────────────────────────────────────────────────

    public async Task<MetacodeCodingResult> DecompileAsync(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int maxTypes = 0,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return MetacodeCodingResult.Failed("decompile", assemblyPath, $"Assembly not found: {assemblyPath}");

        // ILSpy reads metadata, so machine code has to go the other way: Ghidra recovers C, the model
        // lowers it, and the dedicated native tables keep it. The detection is a PE header check, not an
        // exception from deep inside the decompiler.
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
        summary.Append(MetacodeIndexStore.DecompiledPrefix).Append('.');

        return new MetacodeCodingResult(
            "decompile", assembly, report.Indexed > 0, summary.ToString(),
            Diagnostics: decompilation.Errors.Count > 0 ? decompilation.Errors : null);
    }

    /// <summary>
    /// The non-managed half of <see cref="DecompileAsync"/>: Ghidra recovers the functions, the model
    /// gives each one a body and a complexity, and the native store keeps the result next to the
    /// solution's own database. Ghidra installs itself on first use; a failure names the run's log
    /// instead of throwing.
    /// </summary>
    private async Task<MetacodeCodingResult> DecompileNativeAsync(
        string binaryPath,
        string? nameFilter,
        string? namespaceFilter,
        bool force,
        CancellationToken cancellationToken)
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
            return MetacodeCodingResult.Failed("decompile", assembly,
                $"Ghidra could not run on {assembly}: {exception.GetType().Name}: {exception.Message}");
        }

        if (analysis.IsError || analysis.Snapshot is null)
        {
            var hint = " Ghidra is installed on demand under " + GhidraOptions.DefaultToolsDirectory +
                       "; set GHIDRA_INSTALL_DIR (or GhidraOptions.GhidraHome) to use a copy that is already there.";
            return MetacodeCodingResult.Failed("decompile", assembly,
                $"{assembly} is not a managed assembly, so ILSpy has no C# to read, and Ghidra could not decompile it: " +
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
            return MetacodeCodingResult.Failed("decompile", assembly,
                $"{assembly} is not managed: Ghidra recovered {analysis.Functions} function(s), but the solution store " +
                $"refused them: {exception.GetType().Name}: {exception.Message}");
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

        if (!string.IsNullOrWhiteSpace(nameFilter) || !string.IsNullOrWhiteSpace(namespaceFilter))
        {
            summary.Append(" nameFilter/namespaceFilter do not apply to a native binary: ")
                .Append("use analyze_native_binary with function= to read one function back.");
        }

        return new MetacodeCodingResult(
            "decompile", assembly, artifact.Stored, summary.ToString(),
            File: analysis.OutputJsonPath,
            Diagnostics: document.Diagnostics.Count > 0
                ? document.Diagnostics.Take(5).Select(diagnostic => diagnostic.ToString()).ToList()
                : null);
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
