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
    /// <remarks>
    /// No default pipeline reads this: <see cref="Frontends.PythonFrontend"/> is opt-in, because nothing
    /// in this product produces such a dump.
    /// </remarks>
    public static MetacodeSource FromPythonAst(string json, string? path = null) =>
        new(json, path, MetacodeLanguage.Python, null);

    /// <summary>
    /// A Clang <c>-ast-dump=json</c> document. Opt-in for the same reason as
    /// <see cref="FromPythonAst"/>: <see cref="Frontends.CppFrontend"/> is not in
    /// <see cref="MetacodeTranslator.Default"/>.
    /// </summary>
    public static MetacodeSource FromCppAst(string json, string? path = null) =>
        new(json, path, MetacodeLanguage.Cpp, null);

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

    public static MetacodeSource FromText(string text, MetacodeLanguage language, string? path = null) =>
        new(text, path, language, null);

    /// <summary>
    /// Reads a file and infers the source language from the extension: <c>.cs</c>, <c>.vb</c>,
    /// <c>.java</c>, <c>.cshtml</c>/<c>.razor</c> and the canonical-pseudocode extensions. A <c>.json</c>
    /// file is sniffed for a Ghidra snapshot (an image base plus a function list) and nothing else. A
    /// Python or clang AST dump has no extension to infer it from — pass <see cref="FromPythonAst"/> or
    /// <see cref="FromCppAst"/> — and whoever wants to read one builds a translator with the matching reader.
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
            case ".mc" or ".pseudo" or ".metacode":
                return FromPseudocode(text, path);
            case ".cshtml" or ".razor":
                return FromRazor(text, path);
            case ".json":
                if (Snitch.Metacode.Decompilation.GhidraSnapshot.LooksLikeSnapshot(text))
                    return FromGhidra(text, path);
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
