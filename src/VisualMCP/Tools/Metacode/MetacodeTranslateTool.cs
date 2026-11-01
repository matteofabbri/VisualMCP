using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Formatting;
using VisualMCP.Translation;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodeTranslateTool
{
    [McpServerTool(Name = "metacode_translate"), Description(
        "Lower a source file into the canonical pseudocode language and print it: uppercase keywords, one statement per line, " +
        "operator precedence preserved. Reads C# (Roslyn syntax only — no compilation, no solution loaded), a Razor page " +
        "or Blazor component (.cshtml/.razor), Java, JavaScript, Rust, Python and C/C++, plus the library's own formats: " +
        "canonical pseudocode itself and a Ghidra headless snapshot (the `functions.json` analyze_native_binary leaves " +
        "behind). " +
        "JavaScript is read by an ECMAScript parser (Acornima), so an ES module, a classic script and a CommonJS file all " +
        "lower; JSX and TypeScript annotations are not ECMAScript and are reported as a syntax error at their line. " +
        "Rust is read by this library's own recursive-descent reader, so no toolchain is needed — an `impl` block becomes " +
        "a CLASS named after the type it implements, an attribute a decorator. " +
        "Python and C++ are read from an AST dump, and a `.py`/`.cpp`/`.h` path gets one produced for you (CPython's ast, " +
        "`clang -Xclang -ast-dump=json`; pass language='cpp'/'python' with source text to do the same, or the dump itself " +
        "to skip the external process). A construct the model has no node for is kept as `RAW[lang] text // reason` PLUS a " +
        "diagnostic (MC0001–MC0008) listed after the text, so an incomplete translation is visible instead of silent. Give " +
        "a path — the reader is inferred from the extension, and a `.json` is sniffed for a Ghidra snapshot, a clang dump " +
        "or a CPython ast dump — or text plus language; passing language with a path overrides the extension. A Razor page " +
        "becomes a page class: @model and @inject are properties, @section is a method of its own, @code/@functions members " +
        "are lowered as C#, and the markup body is the body of a Render method where @if/@else, @foreach, @for, @while, " +
        "@do/while, @switch with its case/default sections, @try/catch/finally, @lock, @using and a @{ } block are model " +
        "nodes — with a braced or a brace-less body — and the HTML between them is RAW[razor]. Use it to read a page, a " +
        "JavaScript file, a Rust file, a C++ file or a native binary's recovered functions in a neutral shape, or with " +
        "metacode_compare to check that two spellings of an algorithm are the same. It never " +
        "touches the loaded solution: no workspace, no compilation, no project state.")]
    public static async Task<object> Translate(
        ISourceDumpTranslationService dumps,
        [Description("Source file (.cs, .vb, .java, .js/.mjs/.cjs/.jsx, .rs, .cshtml, .razor, .py, .cpp/.cc/.cxx/.c/.hpp/.h, " +
                     ".mc/.pseudo, or a Ghidra snapshot / clang / CPython dump .json). Omit when passing text.")]
        string? path = null,
        [Description("Source text: C#, Visual Basic, Java, JavaScript, Rust, Razor, Python or C/C++ as source, or a Ghidra " +
                     "snapshot / clang / CPython AST dump as the JSON it is.")]
        string? text = null,
        [Description("For text: 'cs', 'vb', 'java', 'js', 'rust', 'cpp', 'python', 'razor', 'ghidra' or 'pseudo' (required). With a " +
                     "path it overrides the extension, which is how a file with an unknown extension is read.")]
        string? language = null,
        [Description("Prefix every declaration and statement with '// @file:line', so a line of pseudocode can be traced back.")]
        bool showOrigin = false,
        [Description("Keep whatever comments the frontend can see (default true).")]
        bool includeComments = true,
        [Description("Turn a C-style for into a WHILE so every language prints the same loop (default true).")]
        bool normalizeControlFlow = true,
        [Description("Fold a block whose only statement is a block into its parent (default true).")]
        bool flattenNestedBlocks = true,
        [Description("C++ only: keep just the declarations whose qualified name contains this text (clang " +
                     "-ast-dump-filter). A file that includes <vector>/<string> dumps the whole standard library " +
                     "without it — pass your namespace or type name.")]
        string? filter = null,
        [Description("C++ only: extra clang arguments for this project, passed verbatim and resolved from the " +
                     "file's folder — '-std=c++20 -I../include -DFOO=1'.")]
        string? clangArgs = null,
        [Description("Timeout in seconds for the Python/C++ dumper when a path or source text needs one (default 120, max 900).")]
        int timeoutSeconds = 120)
    {
        var (source, loadError) = await MetacodeToolHelpers.LoadAsync(
            path, text, language, "metacode_translate", dumps, timeoutSeconds, filter, clangArgs);
        if (source is null) return ToolText.Fail(loadError!);

        var (document, rendered, renderError) = MetacodeToolHelpers.Render(
            source,
            showOrigin: showOrigin,
            includeComments: includeComments,
            normalizeControlFlow: normalizeControlFlow,
            flattenNestedBlocks: flattenNestedBlocks);
        if (document is null || rendered is null) return ToolText.Fail($"metacode_translate failed: {renderError}");

        var header = MetacodeToolHelpers.Header(document, MetacodeToolHelpers.LineCount(rendered));
        var diagnostics = MetacodeToolHelpers.DiagnosticsBlock(document);

        return diagnostics.Length == 0
            ? ToolText.Ok($"{header}\n\n{rendered}")
            : ToolText.Ok($"{header}\n\n{rendered}\n{diagnostics}");
    }
}
