using Snitch.Metacode.Rendering;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// One decompiled type, already lowered: the pseudocode as the renderer prints it, the document it was
/// printed from, and how many members and callables the model found in it.
/// </summary>
public sealed record DecompiledTypeMetacode(
    string FullName,
    string Pseudocode,
    MetacodeDocument Document,
    int Members,
    int Functions);

/// <summary>
/// What a metacode decompilation produced. It never throws: a missing assembly or a type the engine
/// cannot read comes back as <see cref="Error"/> or in <see cref="Errors"/>, next to everything that
/// did succeed, because a decompilation that stops at the first awkward type is not useful.
/// <para>
/// <see cref="Errors"/> keeps at most five messages, so its count is only a lower bound on the types
/// that were lost; <see cref="FailedTypes"/> is the exact number for the requested page. A caller that
/// pages has to advance by <see cref="Types"/> + <see cref="Hidden"/> + <see cref="FailedTypes"/>.
/// </para>
/// </summary>
public sealed record MetacodeDecompilation(
    string Assembly,
    IReadOnlyList<DecompiledTypeMetacode> Types,
    MetacodeDocument Document,
    int TotalTypes,
    int Matched,
    int Hidden,
    IReadOnlyList<string> Errors,
    string? Mvid = null,
    string? Error = null,
    int FailedTypes = 0)
{
    public bool IsError => Error is not null;

    /// <summary>The whole result as canonical pseudocode.</summary>
    public string Pseudocode => new PseudocodeRenderer().Render(Document);

    public static MetacodeDecompilation Failed(string assembly, string error) =>
        new(assembly, Array.Empty<DecompiledTypeMetacode>(),
            new MetacodeDocument(assembly, MetacodeLanguage.CSharp, Array.Empty<MetacodeModule>(), Array.Empty<MetacodeDiagnostic>()),
            0, 0, 0, Array.Empty<string>(), Error: error);
}
