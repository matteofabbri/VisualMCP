using System.Text;
using Snitch.Metacode;
using Snitch.Metacode.Rendering;

namespace VisualMCP.Tools.Metacode;

/// <summary>
/// The plumbing both metacode tools share: turn the tool arguments into a
/// <see cref="MetacodeSource"/> and the document into the canonical text plus its diagnostics.
/// </summary>
internal static class MetacodeToolHelpers
{
    /// <summary>
    /// Reads one side of a call. A path alone is inferred by extension (<see cref="MetacodeSource.FromFile"/>,
    /// which also sniffs a <c>.json</c> dump for a Ghidra snapshot's <c>imageBase</c>+<c>functions</c>); an
    /// explicit language wins over the extension, and is required for text, because there is no extension
    /// to go by.
    /// </summary>
    public static (MetacodeSource? Source, string? Error) Load(string? path, string? text, string? language, string side)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                return (null, $"{side}: file not found: {path}");

            // An unknown extension has no frontend (MC0007), and that diagnostic tells the caller to pass a
            // language — which is exactly what this branch lets them do.
            return Normalize(language) is { } tag
                ? FromText(File.ReadAllText(path), tag, path, side)
                : (MetacodeSource.FromFile(path), null);
        }

        if (string.IsNullOrWhiteSpace(text))
            return (null, $"{side}: pass either path or text.");

        return Normalize(language) is { } textTag
            ? FromText(text, textTag, null, side)
            : (null, $"{side}: text needs language='cs', 'razor', 'ghidra' or 'pseudo' — there is no path to infer it from.");
    }

    // The product's source languages are C# and Razor; pseudocode and a Ghidra snapshot are this
    // library's own formats. Python and C++ are not offered: their readers need an AST dump that
    // nothing here produces (see MetacodeTranslator.Default).
    private static (MetacodeSource? Source, string? Error) FromText(
        string text, string tag, string? path, string side) => tag switch
    {
        "cs" => (MetacodeSource.FromCSharp(text, path), null),
        "pseudo" => (MetacodeSource.FromPseudocode(text, path), null),
        "razor" => (MetacodeSource.FromRazor(text, path), null),
        // The snapshot analyze_native_binary leaves behind, and any older one: no Ghidra needed to read it.
        "ghidra" => (MetacodeSource.FromGhidra(text, path), null),
        // The enum has a VisualBasic member, but no ILanguageFrontend reads it: MetacodeTranslator would
        // answer MC0007. Say the real reason instead of letting the caller read that as a parse failure.
        "vb" => (null, $"{side}: Visual Basic has no metacode frontend yet — MetacodeLanguage.VisualBasic " +
                       "exists but no ILanguageFrontend can read it. Translate through C#/Razor or " +
                       "canonical pseudocode."),
        _ => (null, $"{side}: unknown language '{tag}'; use 'cs', 'razor', 'ghidra' or 'pseudo'."),
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
        "vb" or "vbnet" or "visualbasic" or "visual-basic" => "vb",
        _ => language,
    };
}
