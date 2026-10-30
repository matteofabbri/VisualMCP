using System.Text.Json;
using Snitch.Metacode.Frontends;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Reads a Ghidra headless snapshot and lowers every recovered function into the shared model.
/// <para>
/// The C Ghidra prints is normalized into canonical pseudocode by <see cref="GhidraC"/> and read back
/// by <see cref="PseudocodeFrontend"/>. That is the "normalize first" choice documented on
/// <see cref="GhidraC"/>: the C++ frontend reads clang's JSON, not C text, and Ghidra's C is not a
/// translation unit any compiler would accept. Each function is normalized and parsed on its own, so a
/// function the reader cannot place costs that function and not the binary.
/// </para>
/// <para>
/// The nodes therefore come from the canonical text; the <em>origin</em> of each function is stamped
/// back with the binary it came from, its entry address, its prototype and its position in the
/// snapshot, because that — not a line of a source file that does not exist — is what a reader needs
/// to find the function again.
/// </para>
/// </summary>
public sealed class GhidraFrontend : ILanguageFrontend
{
    /// <summary>The <see cref="MetacodeSource.Format"/> this frontend claims.</summary>
    public const string FormatName = "ghidra";

    public string Name => "Ghidra";

    /// <summary>Ghidra reconstructs C; the model's closest label for that is C++.</summary>
    public MetacodeLanguage Language => MetacodeLanguage.Cpp;

    public bool CanHandle(MetacodeSource source) =>
        source.Format == FormatName
        || (source.Format is null && source.Language is null && GhidraSnapshot.LooksLikeSnapshot(source.Text));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions? options = null)
    {
        GhidraSnapshot snapshot;
        try
        {
            snapshot = GhidraSnapshot.Parse(source.Text);
        }
        catch (JsonException exception)
        {
            return new MetacodeDocument(source.Name, MetacodeLanguage.Cpp, Array.Empty<MetacodeModule>(), new[]
            {
                new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.ParseError,
                    MetacodeDiagnosticSeverity.Error,
                    "The input is not a Ghidra snapshot: " + exception.Message
                    + ". Run Ghidra headless with the ExportNativeGraph script (see docs/metacode.md).",
                    MetacodeOrigin.Unknown),
            });
        }

        return Translate(snapshot, options);
    }

    /// <summary>The same translation for a snapshot that is already parsed (the service hands one over).</summary>
    public MetacodeDocument Translate(GhidraSnapshot snapshot, MetacodeOptions? options = null)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        var declarations = new List<MetacodeDeclaration>();
        var document = snapshot.BinaryPath ?? snapshot.Image;

        for (var i = 0; i < snapshot.Functions.Count; i++)
        {
            var function = snapshot.Functions[i];
            var origin = new MetacodeOrigin(
                MetacodeLanguage.Cpp,
                document,
                i + 1,
                0,
                i + 1,
                0,
                function.Signature);

            if (!function.HasBody)
            {
                diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    MetacodeDiagnosticSeverity.Warning,
                    $"{function.Name}: the decompiler produced no C for this function",
                    origin));
                continue;
            }

            GhidraC.Result parsed;
            try
            {
                parsed = GhidraC.Parse(function);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.ParseError,
                    MetacodeDiagnosticSeverity.Error,
                    $"{function.Name}: the C reader failed — {exception.Message}",
                    origin));
                continue;
            }

            foreach (var warning in parsed.Warnings)
            {
                diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.LossyLowering,
                    MetacodeDiagnosticSeverity.Warning,
                    $"{function.Name}: {warning}",
                    origin));
            }

            var text = "MODULE " + ModuleName(snapshot) + "\n" + parsed.Pseudocode + "END MODULE\n";
            // Through the translator, not the parser directly, so the normalization passes that every
            // other language gets (a C-style `for` becomes a `while`) apply here too.
            var translated = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(text), options ?? MetacodeOptions.Default);

            foreach (var diagnostic in translated.Diagnostics)
                diagnostics.Add(diagnostic with { Origin = origin });

            foreach (var declaration in translated.Modules.SelectMany(m => m.Declarations))
            {
                declarations.Add(declaration is MetacodeFunction callable ? callable with { Origin = origin } : declaration);
            }
        }

        if (snapshot.Functions.Count == 0)
        {
            diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.MalformedTree,
                MetacodeDiagnosticSeverity.Error,
                "The snapshot contains no functions: the analysis may have failed before the export script ran.",
                MetacodeOrigin.Unknown));
        }

        var module = new MetacodeModule(ModuleName(snapshot), declarations);
        return new MetacodeDocument(ModuleName(snapshot), MetacodeLanguage.Cpp, new[] { module }, diagnostics);
    }

    /// <summary>
    /// The canonical pseudocode one function normalizes to, before it is read back: the normalized
    /// form of Ghidra's C, which is also the most direct way to see what the reader was given.
    /// </summary>
    public static string Normalize(GhidraFunction function, string moduleName, out IReadOnlyList<string> warnings)
    {
        var parsed = GhidraC.Parse(function);
        warnings = parsed.Warnings;
        return "MODULE " + moduleName + "\n" + parsed.Pseudocode + "END MODULE\n";
    }

    /// <summary>The module name is the image; a path separator would make the canonical line ambiguous.</summary>
    private static string ModuleName(GhidraSnapshot snapshot)
    {
        var image = Path.GetFileName(snapshot.Image);
        return string.IsNullOrWhiteSpace(image) ? "native" : image;
    }
}
