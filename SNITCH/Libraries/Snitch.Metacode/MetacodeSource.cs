using Microsoft.CodeAnalysis;

namespace Snitch.Metacode;

/// <summary>
/// What a frontend is asked to read. A tree when the language has one available in memory (Roslyn's
/// <see cref="SyntaxTree"/>), otherwise the text of an AST dump or of canonical pseudocode.
/// </summary>
public sealed class MetacodeSource
{
    private MetacodeSource(string text, string? path, MetacodeLanguage? language, SyntaxTree? roslyn, string? format = null)
    {
        Text = text;
        Path = path;
        Language = language;
        Roslyn = roslyn;
        Format = format;
    }

    public string Text { get; }

    public string? Path { get; }

    /// <summary>Explicit language, when the caller knows it. Null lets the frontends infer from the path.</summary>
    public MetacodeLanguage? Language { get; }

    /// <summary>A parsed C# tree, when the caller already has one; the C# frontend prefers it to text.</summary>
    public SyntaxTree? Roslyn { get; }

    /// <summary>
    /// The concrete format when two frontends share a language label. Today only
    /// <c>"ghidra"</c> — a snapshot produced by Ghidra headless, which is C-like but is not a C++
    /// translation unit and must not be handed to the C++ frontend.
    /// </summary>
    public string? Format { get; }

    public string Name => Path is null
        ? "<input>"
        : System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } stem ? stem : "<input>";

    public static MetacodeSource FromCSharp(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.CSharp, null);

    /// <summary>
    /// Visual Basic source text. The frontend parses it with Roslyn's VB syntax layer, so no tree is
    /// carried here: <see cref="Roslyn"/> is a C# <see cref="SyntaxTree"/> and would be the wrong one.
    /// </summary>
    public static MetacodeSource FromVisualBasic(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.VisualBasic, null);

    /// <summary>
    /// A Roslyn tree the caller already has. A C# tree travels with the source — the C# frontend prefers
    /// it to re-parsing — while a Visual Basic tree only contributes its text and path: the VB frontend
    /// parses VB itself, so handing it a C#-typed <see cref="Roslyn"/> would be the wrong tree.
    /// </summary>
    public static MetacodeSource FromSyntaxTree(SyntaxTree tree)
    {
        var path = tree.FilePath is { Length: > 0 } p ? p : null;

        return tree.Options.Language == LanguageNames.VisualBasic
            ? new MetacodeSource(tree.ToString(), path, MetacodeLanguage.VisualBasic, null)
            : new MetacodeSource(tree.ToString(), path, MetacodeLanguage.CSharp, tree);
    }

    /// <summary>
    /// A CPython <c>ast</c> dump as JSON: every node is an object with <c>_type</c> (or <c>type</c>)
    /// plus its own fields. See <c>docs/metacode.md</c> for the four-line dumper.
    /// </summary>
    public static MetacodeSource FromPythonAst(string json, string? path = null) =>
        new(json, path, MetacodeLanguage.Python, null);

    /// <summary>A Clang <c>-ast-dump=json</c> document, which is what <see cref="Frontends.CppFrontend"/> reads.</summary>
    public static MetacodeSource FromCppAst(string json, string? path = null) =>
        new(json, path, MetacodeLanguage.Cpp, null);

    /// <summary>
    /// Whether the text is a clang JSON AST dump. It looks at the head of the document instead of parsing
    /// it (the dump of one translation unit is megabytes): clang's root node is the only
    /// <c>TranslationUnitDecl</c> a document declares that way.
    /// </summary>
    public static bool LooksLikeCppAst(string text) =>
        LooksLikeJson(text) && Head(text).Contains("\"TranslationUnitDecl\"", StringComparison.Ordinal);

    /// <summary>Whether the text is a CPython <c>ast</c> dump: its root node is the module.</summary>
    public static bool LooksLikePythonAst(string text) =>
        LooksLikeJson(text) && Head(text).Contains("\"_type\": \"Module\"", StringComparison.Ordinal);

    /// <summary>Whether the text is the JSON AST dump of <paramref name="language"/>.</summary>
    public static bool LooksLikeDump(MetacodeLanguage language, string text) => language switch
    {
        MetacodeLanguage.Cpp => LooksLikeCppAst(text),
        MetacodeLanguage.Python => LooksLikePythonAst(text),
        _ => false,
    };

    private static bool LooksLikeJson(string text) => text.TrimStart().StartsWith('{');

    private static string Head(string text) => text[..Math.Min(text.Length, 4096)];

    /// <summary>
    /// A Ghidra headless snapshot (the JSON <c>ExportNativeGraph.java</c> writes). It has no language
    /// of its own — it holds C that Ghidra reconstructed — so the language stays null and the format
    /// says which frontend reads it.
    /// </summary>
    public static MetacodeSource FromGhidra(string json, string? path = null) =>
        new(json, path, null, null, Snitch.Metacode.Decompilation.GhidraFrontend.FormatName);

    /// <summary>The canonical pseudocode this library renders — the format for everything else.</summary>
    public static MetacodeSource FromPseudocode(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.Pseudocode, null);

    /// <summary>A Razor page (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>), as source text.</summary>
    public static MetacodeSource FromRazor(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.Razor, null);

    /// <summary>
    /// Java source text — what <see cref="Decompilation.JadxService"/> writes when it decompiles a
    /// .class/.dex/.jar/.apk, and what a Java project's own .java file contains.
    /// </summary>
    public static MetacodeSource FromJava(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.Java, null);

    /// <summary>
    /// JavaScript source text: an ES module, a classic script or a CommonJS file. The frontend parses it
    /// with Acornima and lowers the result, so no tree travels here — the same arrangement C# has with
    /// Roslyn, minus the in-memory tree.
    /// </summary>
    public static MetacodeSource FromJavaScript(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.JavaScript, null);

    /// <summary>
    /// Rust source text. Unlike Python and C++, the Rust reader parses the source itself — there is no AST
    /// dump this product could produce without a toolchain — so <see cref="Frontends.RustFrontend"/> is in
    /// <see cref="MetacodeTranslator.Default"/> and this is how a .rs file enters it.
    /// </summary>
    public static MetacodeSource FromRust(string text, string? path = null) =>
        new(text, path, MetacodeLanguage.Rust, null);

    public static MetacodeSource FromText(string text, MetacodeLanguage language, string? path = null) =>
        new(text, path, language, null);

    /// <summary>
    /// Reads a file and infers the source language from the extension: <c>.cs</c>, <c>.vb</c>,
    /// <c>.java</c>, <c>.rs</c>, the JavaScript extensions (<c>.js</c>, <c>.mjs</c>, <c>.cjs</c>, <c>.jsx</c>),
    /// <c>.cshtml</c>/<c>.razor</c> and the canonical-pseudocode extensions. A <c>.json</c>
    /// file is sniffed for the three dumps this library reads — a Ghidra snapshot (an image base plus a
    /// function list), a clang AST dump and a CPython <c>ast</c> dump. A <c>.py</c> or <c>.cpp</c>
    /// <em>source</em> has no reader: those frontends read the dump, and the dumper is run by the host
    /// (<c>translate_python_file</c> / <c>translate_cpp_file</c>, <c>metacode_translate</c> on that path).
    /// </summary>
    public static MetacodeSource FromFile(string path)
    {
        var text = File.ReadAllText(path);
        var extension = System.IO.Path.GetExtension(path);
        switch (extension.ToLowerInvariant())
        {
            case ".cs":
                return FromCSharp(text, path);
            case ".vb":
                return FromVisualBasic(text, path);
            case ".java":
                return FromJava(text, path);
            case ".js" or ".mjs" or ".cjs" or ".jsx":
                return FromJavaScript(text, path);
            case ".rs":
                return FromRust(text, path);
            case ".mc" or ".pseudo" or ".metacode":
                return FromPseudocode(text, path);
            case ".cshtml" or ".razor":
                return FromRazor(text, path);
            case ".json":
                if (Snitch.Metacode.Decompilation.GhidraSnapshot.LooksLikeSnapshot(text))
                    return FromGhidra(text, path);
                if (LooksLikeCppAst(text))
                    return FromCppAst(text, path);
                if (LooksLikePythonAst(text))
                    return FromPythonAst(text, path);
                return new MetacodeSource(text, path, null, null);
            default:
                return new MetacodeSource(text, path, null, null);
        }
    }
}

/// <summary>
/// A language frontend: it says whether it can read a source, and lowers it into the shared model.
/// Everything after that point — passes, rendering, parsing — is language-agnostic.
/// </summary>
public interface ILanguageFrontend
{
    string Name { get; }

    MetacodeLanguage Language { get; }

    bool CanHandle(MetacodeSource source);

    MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options);
}
