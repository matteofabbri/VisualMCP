using System.Text;
using Snitch.Metacode;
using Snitch.Metacode.Rendering;
using VisualMCP.Translation;

namespace VisualMCP.Tools.Metacode;

/// <summary>
/// The plumbing both metacode tools share: turn the tool arguments into a
/// <see cref="MetacodeSource"/> and the document into the canonical text plus its diagnostics.
/// </summary>
internal static class MetacodeToolHelpers
{
    /// <summary>
    /// Reads one side of a call. A path alone is inferred by extension (<see cref="MetacodeSource.FromFile"/>,
    /// which also sniffs a <c>.json</c> for a Ghidra snapshot or a Python/clang AST dump); an explicit
    /// language wins over the extension, and is required for text, because there is no extension to go by.
    ///
    /// A <c>.py</c> or a <c>.cpp</c> path — or the name of that language — is not a dump but the source a
    /// dump is made from, so the dumper runs (<c>python -m ast</c>, <c>clang -ast-dump=json</c>) and the
    /// result goes through the same readers and options as every other language. Text that already is a
    /// dump is recognised as one and skips the external process.
    /// </summary>
    public static async Task<(MetacodeSource? Source, string? Error)> LoadAsync(
        string? path,
        string? text,
        string? language,
        string side,
        ISourceDumpTranslationService dumps,
        int timeoutSeconds,
        string? filter = null,
        string? clangArgs = null)
    {
        var tag = Normalize(language) ?? InferDumpTag(path);
        if (tag is "cpp" or "python")
            return await LoadDumpAsync(path, text, tag, side, dumps, timeoutSeconds, filter, clangArgs);

        if (!string.IsNullOrWhiteSpace(filter) || !string.IsNullOrWhiteSpace(clangArgs))
            return (null, $"{side}: filter and clangArgs apply to C++ only (language='cpp' on a .cpp/.h path).");

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                return (null, $"{side}: file not found: {path}");

            // An unknown extension has no frontend (MC0007), and that diagnostic tells the caller to pass a
            // language — which is exactly what this branch lets them do.
            return tag is { } explicitTag
                ? FromText(File.ReadAllText(path), explicitTag, path, side)
                : (MetacodeSource.FromFile(path), null);
        }

        if (string.IsNullOrWhiteSpace(text))
            return (null, $"{side}: pass either path or text.");

        return tag is { } textTag
            ? FromText(text, textTag, null, side)
            : (null, $"{side}: text needs language='cs', 'vb', 'java', 'js', 'cpp', 'python', 'rust', 'razor', 'ghidra' or 'pseudo' — there is no path to infer it from.");
    }

    /// <summary>
    /// One side in a language whose reader takes an AST dump. A dump the caller already has is read
    /// directly; otherwise the dumper produces one from the path, or from the text written to a temporary
    /// file — a C++ translation unit that includes a header next to the caller's text cannot resolve it
    /// from a temporary folder, so pass the path in that case.
    /// </summary>
    private static async Task<(MetacodeSource? Source, string? Error)> LoadDumpAsync(
        string? path, string? text, string tag, string side,
        ISourceDumpTranslationService dumps, int timeoutSeconds, string? filter, string? clangArgs)
    {
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
        {
            var dumpedLanguage = tag == "cpp" ? MetacodeLanguage.Cpp : MetacodeLanguage.Python;
            if (!MetacodeSource.LooksLikeDump(dumpedLanguage, text))
            {
                return (null, $"{side}: the text is JSON but not a {tag} AST dump (expected {ExpectedDump(tag)}). " +
                              "Pass the dump the dumper produces, a source path to have one produced, or language='pseudo' for canonical text.");
            }

            return FromText(text, tag, path, side);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (string.IsNullOrWhiteSpace(path))
                return (null, $"{side}: pass either path or text.");
            if (!File.Exists(path))
                return (null, $"{side}: file not found: {path}");
        }

        var dump = await dumps.ProduceDumpAsync(
            tag,
            string.IsNullOrWhiteSpace(text) ? path : null,
            string.IsNullOrWhiteSpace(text) ? null : text,
            timeoutSeconds,
            filter,
            clangArgs);

        if (dump.Error is not null)
        {
            var hint = string.IsNullOrWhiteSpace(dump.Hint) ? string.Empty : " " + dump.Hint;
            return (null, $"{side}: {dump.Error}{hint}");
        }

        return FromText(dump.Json!, tag, path, side);
    }

    /// <summary>The root marker that tells one dump from another, for the error that names it.</summary>
    private static string ExpectedDump(string tag) => tag == "cpp"
        ? "'\"kind\": \"TranslationUnitDecl\"' from clang -Xclang -ast-dump=json"
        : "'\"_type\": \"Module\"' from CPython's ast module";

    /// <summary>The language of a source file that needs a dump, from its extension.</summary>
    private static string? InferDumpTag(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cpp" or ".cc" or ".cxx" or ".c++" or ".c" or ".hpp" or ".hh" or ".hxx" or ".h" or ".ipp" or ".inl" => "cpp",
            ".py" or ".pyw" => "python",
            _ => null,
        };
    }

    // The product's source languages are C#, Visual Basic, Java, JavaScript, Rust and Razor; pseudocode and
    // a Ghidra snapshot are this library's own formats. Python and C++ are read from the AST dump the caller
    // passes here (LoadDumpAsync produces it, or accepts one already made).
    private static (MetacodeSource? Source, string? Error) FromText(
        string text, string tag, string? path, string side) => tag switch
    {
        "cs" => (MetacodeSource.FromCSharp(text, path), null),
        "vb" => (MetacodeSource.FromVisualBasic(text, path), null),
        // Java source: what JADX writes, and what a Java project's own .java file contains.
        "java" => (MetacodeSource.FromJava(text, path), null),
        // JavaScript source, read by the ECMAScript parser and lowered by the JavaScript frontend.
        "js" => (MetacodeSource.FromJavaScript(text, path), null),
        "pseudo" => (MetacodeSource.FromPseudocode(text, path), null),
        "razor" => (MetacodeSource.FromRazor(text, path), null),
        // Rust source, read by this library's own recursive-descent reader (no toolchain needed).
        "rust" => (MetacodeSource.FromRust(text, path), null),
        // The snapshot analyze_native_binary leaves behind, and any older one: no Ghidra needed to read it.
        "ghidra" => (MetacodeSource.FromGhidra(text, path), null),
        "cpp" => (MetacodeSource.FromCppAst(text, path), null),
        "python" => (MetacodeSource.FromPythonAst(text, path), null),
        _ => (null, $"{side}: unknown language '{tag}'; use 'cs', 'vb', 'java', 'js', 'cpp', 'python', 'rust', 'razor', 'ghidra' or 'pseudo'."),
    };

    /// <summary>
    /// Canonical pseudocode for a source, with the options the tool exposes.
    ///
    /// <see cref="MetacodeTranslator.Default"/> is used directly rather than resolved from the container:
    /// it is the library's documented single entry point, and <c>AddSnitchMetacode()</c> registers that very
    /// instance, so a host and a tool share it either way. Translation needs no workspace or project state,
    /// which is why this capability has no service of its own to be absent.
    /// </summary>
    public static (MetacodeDocument? Document, string? Text, string? Error) Render(
        MetacodeSource source, bool showOrigin, bool includeComments, bool normalizeControlFlow, bool flattenNestedBlocks)
    {
        try
        {
            var options = MetacodeOptions.Default with
            {
                IncludeComments = includeComments,
                NormalizeControlFlow = normalizeControlFlow,
                FlattenNestedBlocks = flattenNestedBlocks,
            };

            var document = MetacodeTranslator.Default.Translate(source, options);
            var renderer = new PseudocodeRenderer(PseudocodeOptions.Default with { ShowOrigin = showOrigin });
            return (document, renderer.Render(document), null);
        }
        catch (Exception ex)
        {
            return (null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The diagnostics of a translation, as the short lines <see cref="MetacodeDiagnostic.ToString"/>
    /// already produces ("error MC0003 at file:line:col: …"). Empty when there is nothing to say.
    /// </summary>
    public static string DiagnosticsBlock(MetacodeDocument document)
    {
        if (document.Diagnostics.Count == 0)
            return string.Empty;

        var text = new StringBuilder();
        text.Append("// diagnostics (").Append(document.Diagnostics.Count).Append(") — the text above is incomplete where one says so\n");
        foreach (var diagnostic in document.Diagnostics)
            text.Append("// ").Append(diagnostic.ToString().Replace("\r", string.Empty).Replace('\n', ' ')).Append('\n');
        return text.ToString();
    }

    /// <summary>One sentence for the header line, in the shape read_file/read_tree use.</summary>
    public static string Header(MetacodeDocument document, int lines) =>
        $"{document.Name} · {PseudocodeRenderer.LanguageTag(document.Language)} · " +
        $"{document.Modules.Count} module(s) · {lines} line(s) · {document.Diagnostics.Count} diagnostic(s)";

    public static int LineCount(string text) =>
        text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);

    private static string? Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "cs" or "c#" or "csharp" => "cs",
        "pseudo" or "pseudocode" or "mc" or "metacode" => "pseudo",
        "razor" or "cshtml" or "blazor" => "razor",
        "ghidra" or "ghidra-snapshot" or "native" => "ghidra",
        "java" or "jadx" or "jar" => "java",
        "js" or "javascript" or "mjs" or "cjs" or "jsx" or "ecmascript" => "js",
        "vb" or "vbnet" or "visualbasic" or "visual-basic" => "vb",
        "cpp" or "c++" or "cxx" or "c" => "cpp",
        "python" or "py" => "python",
        "rust" or "rs" => "rust",
        _ => language,
    };
}
