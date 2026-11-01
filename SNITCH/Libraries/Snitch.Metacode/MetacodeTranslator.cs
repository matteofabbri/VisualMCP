using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Passes;
using Snitch.Metacode.Rendering;

namespace Snitch.Metacode;

/// <summary>
/// The single entry point: it picks the frontend that can read a source, lowers it, runs the passes
/// that remove language differences, and hands back one document. Adding a language means adding one
/// <see cref="ILanguageFrontend"/> to <see cref="Frontends"/> — nothing here changes.
/// </summary>
public sealed class MetacodeTranslator
{
    /// <summary>
    /// The pipeline this product runs, which is exactly what it can produce: C#, Visual Basic and Java
    /// (Roslyn for the first two, this library's recursive-descent reader for Java), JavaScript (Acornima,
    /// an ECMAScript parser), Rust (this library's own recursive-descent reader), Razor (this repository's
    /// scanner), Python and C++ (an AST dump: <c>python -m ast</c>, <c>clang -Xclang -ast-dump=json</c>),
    /// and canonical pseudocode and a Ghidra snapshot — the library's own formats, written by its renderer
    /// and by its native pipeline.
    /// </summary>
    /// <remarks>
    /// Java is here because something in this product feeds it: <see cref="Decompilation.JadxService"/>
    /// turns a .class/.dex/.jar/.apk into .java sources. JavaScript is here because a repository beside a
    /// .NET solution usually has some, and <see cref="Frontends.JavaScriptFrontend"/> reads the source
    /// directly — the grammar is Acornima's, the lowering is this library's. Rust is here for the same
    /// reason and has no external tool at all: <see cref="Frontends.RustFrontend"/> is a recursive-descent
    /// reader over the source, because nothing here could produce a Rust AST dump without a toolchain.
    /// <see cref="PythonFrontend"/> and <see cref="CppFrontend"/> need an AST dump rather than the source,
    /// and something in this product produces one too: the host's <c>translate_python_file</c> /
    /// <c>translate_cpp_file</c> run the dumper, and <c>metacode_translate</c> / <c>metacode_compare</c> run
    /// it for a <c>.py</c> or a <c>.cpp</c> path. They are listed here because a dump is a
    /// <see cref="MetacodeSource"/> like any other; a reader listed here still claims only its own language,
    /// never a source file that merely has the right extension (a <c>.cpp</c> is not a clang dump).
    /// </remarks>
    public static MetacodeTranslator Default { get; } = new(new ILanguageFrontend[]
    {
        new CSharpFrontend(),
        new VisualBasicFrontend(),
        new JavaFrontend(),
        new JavaScriptFrontend(),
        new RustFrontend(),
        new RazorFrontend(),
        new PythonFrontend(),
        new CppFrontend(),
        new PseudocodeFrontend(),
        new GhidraFrontend(),
    });

    public MetacodeTranslator(IEnumerable<ILanguageFrontend> frontends) =>
        Frontends = frontends.ToList();

    public IReadOnlyList<ILanguageFrontend> Frontends { get; }

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions? options = null)
    {
        options ??= MetacodeOptions.Default;

        var frontend = Frontends.FirstOrDefault(f => f.CanHandle(source));
        if (frontend is null)
        {
            return new MetacodeDocument(
                source.Name,
                source.Language ?? MetacodeLanguage.Unknown,
                Array.Empty<MetacodeModule>(),
                new[]
                {
                    new MetacodeDiagnostic(
                        MetacodeDiagnosticCodes.NoFrontend,
                        MetacodeDiagnosticSeverity.Error,
                        "No frontend in this pipeline can read this source. The readers it has are " +
                        string.Join(", ", Frontends.Select(f => f.Name)) +
                        ". A .py or .cpp source file is not an AST dump: translate_python_file / translate_cpp_file " +
                        "(or metacode_translate / metacode_compare on that path) run the dumper that produces one.",
                        MetacodeOrigin.Unknown),
                });
        }

        var document = frontend.Translate(source, options);
        foreach (var pass in BuildPasses(options))
            document = pass.Apply(document);

        return document;
    }

    /// <summary>Translate and print in one call — the usual thing a caller wants.</summary>
    public string Render(MetacodeSource source, MetacodeOptions? options = null, PseudocodeOptions? renderOptions = null) =>
        new PseudocodeRenderer(renderOptions).Render(Translate(source, options));

    private static IEnumerable<IMetacodePass> BuildPasses(MetacodeOptions options)
    {
        if (options.NormalizeControlFlow)
            yield return NormalizeControlFlowPass.Instance;
        if (options.FlattenNestedBlocks)
            yield return FlattenSingleStatementBlocksPass.Instance;
        foreach (var pass in options.Passes)
            yield return pass;
    }
}
