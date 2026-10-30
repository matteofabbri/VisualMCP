namespace Snitch.Metacode.Decompilation;

/// <summary>Decompilation backed by ICSharpCode.Decompiler (the ILSpy engine).</summary>
/// <remarks>
/// Decompilation is a producer of metacode: that is why this — and the Ghidra analyzer next to it —
/// lives in <c>Snitch.Metacode</c> rather than in the language services. The metacode half of the same
/// capability is <see cref="IMetacodeDecompileService"/>.
/// </remarks>
public interface IDecompileService
{
    /// <summary>
    /// One type as C#. <paramref name="compact"/> strips the indentation, line breaks and comments that
    /// carry no meaning, which is a third of the characters ILSpy prints; the result lexes to the same
    /// tokens.
    /// </summary>
    object DecompileType(string assemblyPath, string typeFullName, int maxChars, bool compact = true);

    /// <summary>
    /// Whole-assembly decompilation. <paramref name="outputPath"/> null returns a truncated preview,
    /// a file path writes the entire module to that file. <paramref name="compact"/> applies to the
    /// preview; a file always keeps ILSpy's formatting.
    /// </summary>
    object DecompileAssembly(string assemblyPath, string? outputPath, int maxChars, bool compact = true);

    /// <summary>
    /// Type-level decompilation: select by name/namespace, list the matching names without
    /// decompiling (<paramref name="listOnly"/>), and write one file per type when
    /// <paramref name="outputPath"/> is a directory. <paramref name="skip"/>/<paramref name="maxTypes"/>
    /// page through a large assembly so a single call stays short.
    /// <para>
    /// With <paramref name="persist"/> the export is kept in the solution store, under a directory named
    /// after the assembly's module version id, and a later call for the same binary reuses it instead of
    /// decompiling again.
    /// </para>
    /// </summary>
    object DecompileTypes(
        string assemblyPath,
        string? nameFilter,
        string? namespaceFilter,
        int skip,
        int maxTypes,
        bool listOnly,
        string? outputPath,
        int maxChars,
        bool persist = false,
        bool compact = true);
}

/// <summary>
/// The same decompiler, but the output of every call is METACODE: the C# ILSpy produces is read by
/// <see cref="Frontends.CSharpFrontend"/> and lowered into the shared model, so a decompiled type has
/// the same shape as a type read from source — declarations, members, statements, expressions and the
/// origin of every node.
/// </summary>
public interface IMetacodeDecompileService
{
    /// <summary>One type: ILSpy → C# → the C# frontend → <see cref="MetacodeDocument"/>.</summary>
    MetacodeDecompilation DecompileTypeToMetacode(string assemblyPath, string typeFullName);

    /// <summary>
    /// The types of a whole assembly, selected by name/namespace and paged like
    /// <see cref="IDecompileService.DecompileTypes"/>. Each type is translated on its own, so one type
    /// the model cannot read does not cost the others.
    /// </summary>
    MetacodeDecompilation DecompileAssemblyToMetacode(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int skip = 0,
        int maxTypes = 0);
}
