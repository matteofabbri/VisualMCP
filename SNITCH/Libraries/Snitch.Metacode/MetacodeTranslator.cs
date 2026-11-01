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
    /// (Roslyn for the first two, this library's recursive-descent reader for Java), Razor (this
    /// repository's scanner), and canonical pseudocode and a Ghidra snapshot — the library's own formats,
    /// written by its renderer and by its native pipeline.
    /// </summary>
    /// <remarks>
    /// Java is here because something in this product feeds it: <see cref="Decompilation.JadxService"/>
    /// turns a .class/.dex/.jar/.apk into .java sources. Python and C++ are still deliberately absent —
    /// <see cref="PythonFrontend"/> and <see cref="CppFrontend"/> read an AST dump (<c>python -m ast</c>,
    /// <c>clang -Xclang -ast-dump=json</c>) that nothing in this product generates, so listing them would
    /// advertise languages the pipeline cannot be fed. They stay available as opt-in readers: pass one to
    /// the constructor when the caller has such a dump.
    /// </remarks>
    public static MetacodeTranslator Default { get; } = new(new ILanguageFrontend[]
    {
        new CSharpFrontend(),
        new VisualBasicFrontend(),
        new JavaFrontend(),
        new RazorFrontend(),
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
                        "No frontend in this pipeline can read this source. The default pipeline reads C#, Razor, canonical pseudocode and Ghidra snapshots; a Python or clang AST dump needs a translator built with that reader.",
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
