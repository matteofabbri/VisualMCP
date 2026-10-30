using Microsoft.CodeAnalysis;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The vocabulary of "how" a symbol is used. Every usage the index records carries one of these, so a
/// query can answer "where is this called", "where is this written" or "who implements this" without
/// re-reading a single file. The names are the contract: they are stored in the database and returned
/// by the tools, so a value is added rather than renamed.
/// </summary>
public static class SymbolUsageKind
{
    // members
    public const string Call = "call";
    public const string MethodGroup = "method-group";
    public const string Instantiate = "instantiate";
    public const string Read = "read";
    public const string Write = "write";
    public const string ReadWrite = "read-write";
    public const string InitializerWrite = "initializer-write";
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string RefArgument = "ref-argument";
    public const string OutArgument = "out-argument";
    public const string InArgument = "in-argument";
    public const string Override = "override";
    public const string Lock = "lock";

    // types
    public const string Inherit = "inherit";
    public const string Implement = "implement";
    public const string TypeArgument = "type-argument";
    public const string ParameterType = "parameter-type";
    public const string ReturnType = "return-type";
    public const string FieldType = "field-type";
    public const string PropertyType = "property-type";
    public const string LocalType = "local-type";
    public const string EventType = "event-type";
    public const string Attribute = "attribute";
    public const string Constraint = "constraint";
    public const string Catch = "catch";
    public const string Cast = "cast";
    public const string As = "as";
    public const string Pattern = "pattern";
    public const string TypeOf = "typeof";
    public const string Default = "default";
    public const string SizeOf = "sizeof";
    public const string Array = "array";
    public const string NameOf = "nameof";
    public const string Deconstruction = "deconstruction";
    public const string Using = "using";

    /// <summary>A type named where none of the rules above applies: a static-member qualifier, for instance.</summary>
    public const string Type = "type";
}

/// <summary>
/// One declaration: which member, with what signature and modifiers, at which file, line and span, and
/// what encloses it. Two declarations share a <see cref="Key"/> only when they are the same symbol —
/// a partial class has one key and several rows, an overload has its own key.
/// </summary>
public sealed record SymbolDeclaration(
    string Key,
    string Name,
    string Display,
    string Signature,
    string Kind,
    string Accessibility,
    string Modifiers,
    string ContainerKey,
    string ContainerName,
    string Namespace,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsOverride,
    bool IsExtension,
    int Arity,
    string? Summary,
    string Project,
    string Path,
    int Start,
    int Length,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string DocumentHash);

/// <summary>
/// One place a symbol is used: the kind of use, the symbol it binds to, and the member the use happens
/// inside — which is what makes call hierarchy (who calls this, what does this call) a plain query.
/// </summary>
public sealed record SymbolUsage(
    string SymbolKey,
    string Name,
    string Kind,
    string ContainerKey,
    string Path,
    int Start,
    int Length,
    int Line,
    int Column,
    string DocumentHash);

/// <summary>A document the index has rows for: its content hash is the freshness token.</summary>
public sealed record SymbolDocument(
    string Path, string Project, string Hash, int Declarations, int Usages, DateTimeOffset IndexedAt);

/// <summary>
/// The metacode tree of one declaration, rendered to the canonical pseudocode and stored with the key
/// of the symbol it belongs to. A type's tree contains its members; each member also has its own row,
/// so asking for a method does not mean rendering the whole type again.
/// </summary>
public sealed record SymbolMetacode(
    string SymbolKey, string Path, string Kind, string Name, int Line, string Text, string DocumentHash);

/// <summary>Everything one document contributes, computed before anything is written.</summary>
public sealed record SymbolDocumentFacts(
    string Path,
    string Project,
    string Hash,
    IReadOnlyList<SymbolDeclaration> Declarations,
    IReadOnlyList<SymbolUsage> Usages,
    IReadOnlyList<SymbolMetacode> Trees);

/// <summary>
/// The stable identity of a symbol, as stored and matched: the symbol kind plus its fully qualified
/// signature. Constructed generics and reduced extension methods are collapsed onto their definition,
/// so <c>List&lt;int&gt;</c> and <c>List&lt;string&gt;</c> are one entry and <c>x.Ext()</c> lands on the
/// <c>Ext(this X, …)</c> declaration.
/// </summary>
public static class SymbolKeys
{
    /// <summary>
    /// The format a key is built with. <c>SymbolDisplayFormat.FullyQualifiedFormat</c> is not enough in
    /// this Roslyn version: it omits the member options, so every method formats as its bare name and
    /// two overloads — or two members with the same name in different types — would collapse into one key.
    /// </summary>
    private static readonly SymbolDisplayFormat KeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeExplicitInterface |
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>The key of a symbol or of a type/namespace used as a container.</summary>
    public static string For(ISymbol symbol)
    {
        var definition = symbol switch
        {
            IMethodSymbol { ReducedFrom: { } reduced } => reduced.OriginalDefinition,
            _ => symbol.OriginalDefinition,
        };

        return $"{definition.Kind}:{definition.ToDisplayString(KeyFormat)}";
    }

    /// <summary>
    /// The key of whatever declares <paramref name="symbol"/>: the containing type, or the containing
    /// namespace when the symbol is a top-level type. Null for a symbol with no container.
    /// </summary>
    public static string? ContainerOf(ISymbol symbol) => symbol.ContainingSymbol is { } container
        ? For(container)
        : null;

    /// <summary>The readable half of a key, for output: <c>global::Ns.C.M(int)</c>.</summary>
    public static string Display(string key)
    {
        var separator = key.IndexOf(':');
        return separator >= 0 && separator < key.Length - 1 ? key[(separator + 1)..] : key;
    }
}
