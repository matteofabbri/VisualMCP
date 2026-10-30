namespace Snitch.Metacode;

/// <summary>
/// The language a tree came from. It is only a label on a node — no part of the model is allowed to
/// behave differently because of it, except a frontend deciding what it can read.
/// </summary>
public enum MetacodeLanguage
{
    Unknown = 0,
    CSharp,
    VisualBasic,
    Python,
    Cpp,
    Java,
    JavaScript,
    TypeScript,
    Rust,
    Go,
    Sql,
    Pseudocode,
    Razor,
}

/// <summary>
/// Where a node came in the original source. Every node carries one so pseudocode can be traced back
/// to the line it was lowered from; a translated or synthesized node carries the origin of what it
/// replaced.
/// </summary>
public sealed record MetacodeOrigin(
    MetacodeLanguage Language = MetacodeLanguage.Unknown,
    string? Document = null,
    int StartLine = 0,
    int StartColumn = 0,
    int EndLine = 0,
    int EndColumn = 0,
    string? Snippet = null)
{
    public static MetacodeOrigin Unknown { get; } = new();

    public static MetacodeOrigin Synthetic(MetacodeLanguage language) => new(language);

    public bool IsKnown => StartLine > 0;

    public string Location => Document is null
        ? (IsKnown ? $"{StartLine}:{StartColumn}" : "?")
        : $"{Document}:{StartLine}:{StartColumn}";
}

[Flags]
public enum MetacodeModifiers
{
    None = 0,
    Public = 1 << 0,
    Protected = 1 << 1,
    Internal = 1 << 2,
    Private = 1 << 3,
    Static = 1 << 4,
    Abstract = 1 << 5,
    Virtual = 1 << 6,
    Override = 1 << 7,
    Sealed = 1 << 8,
    ReadOnly = 1 << 9,
    Const = 1 << 10,
    Async = 1 << 11,
    Partial = 1 << 12,
    Extern = 1 << 13,
    Unsafe = 1 << 14,
    New = 1 << 15,
}

/// <summary>
/// A type as written. This is deliberately syntactic: the whole library works without a semantic
/// model, so a type is whatever the source spelled — <c>int</c>, <c>List&lt;int&gt;</c>,
/// <c>std::vector&lt;int&gt;</c>, <c>list[int]</c>. <see cref="Dynamic"/> stands for a language that
/// has no annotations at all.
/// </summary>
public sealed record MetacodeTypeRef(
    string Name,
    IReadOnlyList<MetacodeTypeRef>? TypeArguments = null,
    bool IsArray = false,
    bool IsNullable = false,
    bool IsReference = false,
    bool IsPointer = false)
{
    public static MetacodeTypeRef Void { get; } = new("void");

    public static MetacodeTypeRef Dynamic { get; } = new("dynamic");

    public static MetacodeTypeRef Of(string name) => new(name);

    /// <summary>No annotation: a language without types, or a declaration whose type is inferred.</summary>
    public bool IsDynamic => Name == "dynamic";

    public IReadOnlyList<MetacodeTypeRef> Arguments => TypeArguments ?? Array.Empty<MetacodeTypeRef>();

    public string Render()
    {
        var text = Name;
        if (Arguments.Count > 0)
            text += "<" + string.Join(", ", Arguments.Select(a => a.Render())) + ">";
        if (IsArray)
            text += "[]";
        if (IsPointer)
            text += "*";
        if (IsReference)
            text += "&";
        if (IsNullable)
            text += "?";
        return text;
    }

    public override string ToString() => Render();
}

/// <summary>The root of everything that can be placed in the tree.</summary>
public abstract record MetacodeNode
{
    /// <summary>Where this node was lowered from; <see cref="MetacodeOrigin.Unknown"/> when synthesized.</summary>
    public MetacodeOrigin Origin { get; init; } = MetacodeOrigin.Unknown;
}

/// <summary>
/// One translated file, tree or compilation unit. The document is the unit the translator and the
/// frontends hand around: modules, plus everything the frontend could not express.
/// </summary>
public sealed record MetacodeDocument(
    string Name,
    MetacodeLanguage Language,
    IReadOnlyList<MetacodeModule> Modules,
    IReadOnlyList<MetacodeDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == MetacodeDiagnosticSeverity.Error);
}

public enum MetacodeModuleKind
{
    Module = 0,
    Namespace,
    TranslationUnit,
}

/// <summary>
/// A unit of declarations: a namespace in C#, a module in Python, a translation unit in C++. The
/// kind is metadata for the reader; the shape is the same in every language.
/// </summary>
public sealed record MetacodeModule(
    string Name,
    IReadOnlyList<MetacodeDeclaration> Declarations,
    MetacodeModuleKind Kind = MetacodeModuleKind.Module) : MetacodeNode;

public abstract record MetacodeDeclaration : MetacodeNode;

public enum MetacodeTypeKind
{
    Class = 0,
    Struct,
    Interface,
    Enum,
    Record,
    Union,
    Trait,
}

/// <summary>A class-like declaration. Members are declarations, so nesting stays uniform.</summary>
public sealed record MetacodeTypeDeclaration(
    string Name,
    MetacodeTypeKind Kind,
    IReadOnlyList<MetacodeDeclaration> Members,
    MetacodeModifiers Modifiers = MetacodeModifiers.None,
    IReadOnlyList<MetacodeTypeRef>? BaseTypes = null,
    IReadOnlyList<MetacodeTypeParameter>? TypeParameters = null,
    IReadOnlyList<string>? Decorators = null) : MetacodeDeclaration;

/// <summary>A type alias: C++'s <c>using Count = int;</c> or <c>typedef int Count;</c>.</summary>
public sealed record MetacodeTypeAlias(string Name, MetacodeTypeRef? Target = null) : MetacodeDeclaration;

public sealed record MetacodeTypeParameter(
    string Name,
    IReadOnlyList<string>? Constraints = null) : MetacodeNode;

public enum MetacodeFunctionKind
{
    Function = 0,
    Method,
    Constructor,
    Destructor,
    Operator,
    LocalFunction,
}

/// <summary>
/// A callable. The same record is a C# method, a Python function, a C++ free function and a lambda
/// lifted out of an expression, which is what lets the renderer print one shape for all of them.
/// <see cref="Decorators"/> is the Python decoration of the declaration as written, in source order.
/// </summary>
public sealed record MetacodeFunction(
    string Name,
    MetacodeFunctionKind Kind,
    IReadOnlyList<MetacodeParameter>? Parameters = null,
    MetacodeTypeRef? ReturnType = null,
    MetacodeBlock? Body = null,
    MetacodeModifiers Modifiers = MetacodeModifiers.None,
    IReadOnlyList<MetacodeTypeParameter>? TypeParameters = null,
    IReadOnlyList<string>? Decorators = null) : MetacodeDeclaration
{
    public IReadOnlyList<MetacodeParameter> ParameterList => Parameters ?? Array.Empty<MetacodeParameter>();
}

public enum MetacodeParameterKind
{
    Value = 0,
    Ref,
    Out,
    In,
    Params,
    Keyword,
}

public sealed record MetacodeParameter(
    string Name,
    MetacodeTypeRef? Type = null,
    MetacodeExpression? DefaultValue = null,
    MetacodeParameterKind Kind = MetacodeParameterKind.Value) : MetacodeNode;

public sealed record MetacodeField(
    string Name,
    MetacodeTypeRef? Type = null,
    MetacodeModifiers Modifiers = MetacodeModifiers.None,
    MetacodeExpression? Initializer = null) : MetacodeDeclaration;

/// <summary>One accessor of a property. A null body means the accessor is automatic.</summary>
public sealed record MetacodeAccessor(
    MetacodeModifiers Modifiers = MetacodeModifiers.None,
    MetacodeBlock? Body = null,
    string? ParameterName = null) : MetacodeNode
{
    public bool IsAuto => Body is null;
}

/// <summary>A property, or an indexer when <see cref="Parameters"/> is set (its name is then "this").</summary>
public sealed record MetacodeProperty(
    string Name,
    MetacodeTypeRef? Type = null,
    MetacodeModifiers Modifiers = MetacodeModifiers.None,
    MetacodeAccessor? Getter = null,
    MetacodeAccessor? Setter = null,
    MetacodeExpression? Initializer = null,
    IReadOnlyList<MetacodeParameter>? Parameters = null) : MetacodeDeclaration;

public sealed record MetacodeEnumMember(
    string Name,
    MetacodeExpression? Value = null) : MetacodeDeclaration;

/// <summary>
/// An event: a member other code subscribes to. Only its signature is modelled — the add/remove
/// accessors are the language's calling convention, not a shape of the algorithm.
/// </summary>
public sealed record MetacodeEvent(
    string Name,
    MetacodeTypeRef? Type = null,
    MetacodeModifiers Modifiers = MetacodeModifiers.None) : MetacodeDeclaration;

/// <summary>
/// A declaration the frontend could not express. It is never dropped: the source text is kept and a
/// diagnostic says why, so an incomplete translation is visible instead of silent.
/// </summary>
public sealed record MetacodeOpaqueDeclaration(
    MetacodeLanguage Language,
    string Text,
    string Reason) : MetacodeDeclaration;
