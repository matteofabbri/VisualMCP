using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodeTranslateTool
{
    [McpServerTool(Name = "metacode_translate"), Description(
        "Lower code into the canonical pseudocode language and print it: uppercase keywords, one statement per line, " +
        "operator precedence preserved. Reads the two source languages this product has — C# (Roslyn syntax only — no " +
        "compilation, no solution loaded) and a Razor page or Blazor component (.cshtml/.razor) — plus the library's own " +
        "formats: canonical pseudocode itself and a Ghidra headless snapshot (the `functions.json` analyze_native_binary " +
        "leaves behind). A construct the model has no node for is kept as `RAW[lang] text // reason` PLUS a diagnostic " +
        "(MC0001–MC0008) listed after the text, so an incomplete translation is visible instead of silent. Give a path — " +
        "the reader is inferred from the extension, and a `.json` is sniffed for a Ghidra snapshot's " +
        "`imageBase`+`functions` — or text plus language; passing language with a path overrides the extension. A Razor " +
        "page becomes a page class: @model and @inject are properties, @code/@functions members are lowered as C#, and the " +
        "markup body is the body of a Render method where @if/@else/@foreach/@for/@while/@lock/@using are control flow and " +
        "the HTML between them is RAW[razor]; a @switch, a try/catch or a brace-less body is kept as one RAW block with a " +
        "diagnostic. Use it to read a page or a native binary's recovered functions in a neutral shape, or with " +
        "metacode_compare to check that two spellings of an algorithm are the same. It never touches the loaded solution: " +
        "no workspace, no compilation, no project state.")]
    public static object Translate(
        [Description("Source file (.cs, .cshtml, .razor, .mc/.pseudo, or a Ghidra snapshot .json). Omit when passing text.")]
        string? path = null,
        [Description("Source text: C#, Razor or canonical pseudocode as source, a Ghidra snapshot as the JSON it is.")]
        string? text = null,
        [Description("For text: 'cs', 'razor', 'ghidra' or 'pseudo' (required). With a path it overrides the extension, which " +
                     "is how a file with an unknown extension is read.")]
        string? language = null,
        [Description("Prefix every declaration and statement with '// @file:line', so a line of pseudocode can be traced back.")]
        bool showOrigin = false,
        [Description("Keep whatever comments the frontend can see (default true).")]
        bool includeComments = true,
        [Description("Turn a C-style for into a WHILE so every language prints the same loop (default true).")]
        bool normalizeControlFlow = true,
        [Description("Fold a block whose only statement is a block into its parent (default true).")]
        bool flattenNestedBlocks = true)
    {
        var (source, loadError) = MetacodeToolHelpers.Load(path, text, language, "metacode_translate");
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
