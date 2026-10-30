namespace Snitch.Metacode;

public enum MetacodeDiagnosticSeverity
{
    Info = 0,
    Warning,
    Error,
}

/// <summary>
/// Something the translation could not do, said out loud. A frontend that meets syntax it has no
/// node for emits one of these next to the opaque node that kept the text.
/// </summary>
public sealed record MetacodeDiagnostic(
    string Code,
    MetacodeDiagnosticSeverity Severity,
    string Message,
    MetacodeOrigin Origin)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Code} at {Origin.Location}: {Message}";
}

/// <summary>The stable codes, so a caller can switch on them instead of matching messages.</summary>
public static class MetacodeDiagnosticCodes
{
    public const string SyntaxError = "MC0001";
    public const string UnsupportedDeclaration = "MC0002";
    public const string UnsupportedStatement = "MC0003";
    public const string UnsupportedExpression = "MC0004";
    public const string MalformedTree = "MC0005";
    public const string ParseError = "MC0006";
    public const string NoFrontend = "MC0007";
    public const string LossyLowering = "MC0008";
}

/// <summary>
/// How a tree is translated. The two flags switch the normalization passes on; <see cref="Passes"/>
/// appends caller-authored passes after them.
/// </summary>
public sealed record MetacodeOptions
{
    public static MetacodeOptions Default { get; } = new();

    /// <summary>Turn a C-style <c>for</c> into a <c>while</c>, so every language prints the same loop.</summary>
    public bool NormalizeControlFlow { get; init; } = true;

    /// <summary>Fold a block whose only statement is a block into its parent.</summary>
    public bool FlattenNestedBlocks { get; init; } = true;

    /// <summary>Keep whatever comments a frontend can see.</summary>
    public bool IncludeComments { get; init; } = true;

    /// <summary>Extra passes, run after the built-in ones.</summary>
    public IReadOnlyList<IMetacodePass> Passes { get; init; } = Array.Empty<IMetacodePass>();
}

/// <summary>A structural rewrite of a translated document. Passes are where language differences die.</summary>
public interface IMetacodePass
{
    string Name { get; }

    MetacodeDocument Apply(MetacodeDocument document);
}
