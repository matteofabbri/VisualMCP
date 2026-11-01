namespace Snitch.Metacode;

/// <summary>
/// What the model can be written back as.
/// <para>
/// Metacode reads every language a frontend understands — C#, Visual Basic, Java, JavaScript, Rust, Razor,
/// SQL, the Python and C++ dumps — and stores the result in **its own AST**: the canonical pseudocode in
/// the solution store, not the shape the source happened to have. Producing source again is a separate,
/// on-demand step, and it exists only where an emitter exists. Today that is C# alone
/// (<see cref="Generation.CSharpEmitter"/>).
/// </para>
/// <para>
/// So this type is not a list of forbidden languages, and Razor is not a special case in it: Razor is one
/// input among the others, and it has no emitter like they have none. What follows is the only rule the
/// write path needs — an edit may land in a C# file — and it is the same rule whether the declaration came
/// from a page, a .vb file or a JavaScript module. Splicing generated C# into one of those would produce a
/// file its own compiler rejects, and the model never held the markup, the VB syntax or the JavaScript it
/// would take to write the language back.
/// </para>
/// </summary>
public static class MetacodeEmission
{
    /// <summary>The language the model is emitted to — the only one with an emitter.</summary>
    public const MetacodeLanguage Target = MetacodeLanguage.CSharp;

    /// <summary>Whether the model may be written back to this file: true for a file in the emitted language.</summary>
    public static bool CanWriteTo(string? path) => IsTargetFile(path);

    /// <summary>Whether the path is written in the language the emitter produces.</summary>
    public static bool IsTargetFile(string? path) =>
        path is not null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reason a path may not be written, or null when it may. One message for every caller.</summary>
    public static string? Refusal(string? path)
    {
        if (CanWriteTo(path))
            return null;

        var name = path is null ? "This declaration" : Path.GetFileName(path);
        return $"{name} is not a C# file. Metacode keeps its own AST and produces source only where an " +
               "emitter exists — C# today — so an edit can only be written back to a .cs file. Every other " +
               "language is an input: read it with the model, and change it with its own tools.";
    }
}
