using System.Text;

namespace Snitch.Metacode;

/// <summary>
/// The expression vocabulary. Operators are shared, not per-language: <c>&amp;&amp;</c>, <c>and</c> and
/// <c>&amp;&amp;</c> all lower to <see cref="MetacodeBinaryOperator.LogicalAnd"/>, and the renderer
/// prints it as <c>AND</c> for every source.
/// </summary>
public abstract record MetacodeExpression : MetacodeNode;

public enum MetacodeLiteralKind
{
    Number = 0,
    String,
    Character,
    Boolean,
    Null,
    Default,
    Raw,
}

/// <summary>A literal, kept as the source spelled it. <see cref="Text"/> is the rendered form.</summary>
public sealed record MetacodeLiteral(MetacodeLiteralKind Kind, string Text) : MetacodeExpression
{
    public static MetacodeLiteral Number(string text) => new(MetacodeLiteralKind.Number, text);

    public static MetacodeLiteral String(string text) => new(MetacodeLiteralKind.String, text);

    public static MetacodeLiteral Boolean(bool value) => new(MetacodeLiteralKind.Boolean, value ? "true" : "false");

    public static MetacodeLiteral Null { get; } = new(MetacodeLiteralKind.Null, "null");

    public static MetacodeLiteral True { get; } = Boolean(true);

    public static MetacodeLiteral False { get; } = Boolean(false);
}

public sealed record MetacodeIdentifier(string Name) : MetacodeExpression;

public sealed record MetacodeMemberAccess(
    MetacodeExpression Target,
    string Member,
    bool NullConditional = false) : MetacodeExpression;

/// <summary>One argument of a call, with the name it was passed under when the language has them.</summary>
public sealed record MetacodeArgument(
    string? Name,
    MetacodeExpression Value,
    MetacodeParameterKind Kind = MetacodeParameterKind.Value) : MetacodeNode
{
    public static MetacodeArgument Of(MetacodeExpression value) => new(null, value);
}

public sealed record MetacodeInvocation(
    MetacodeExpression Callee,
    IReadOnlyList<MetacodeArgument>? Arguments = null) : MetacodeExpression
{
    public IReadOnlyList<MetacodeArgument> ArgumentList => Arguments ?? Array.Empty<MetacodeArgument>();
}

public enum MetacodeBinaryOperator
{
    Add = 0,
    Subtract,
    Multiply,
    Divide,
    IntegerDivide,
    Modulo,
    Power,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LogicalAnd,
    LogicalOr,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    NullCoalesce,
    Contains,
    NotContains,
}

public sealed record MetacodeBinary(
    MetacodeBinaryOperator Operator,
    MetacodeExpression Left,
    MetacodeExpression Right) : MetacodeExpression;

public enum MetacodeUnaryOperator
{
    Not = 0,
    Negate,
    Plus,
    BitwiseNot,
    Increment,
    Decrement,

    /// <summary><c>*p</c>: the value a pointer points at — C, C++ and everything Ghidra decompiles.</summary>
    Dereference,

    /// <summary><c>&amp;x</c>: the address of a place, the counterpart of <see cref="Dereference"/>.</summary>
    AddressOf,
}

public sealed record MetacodeUnary(
    MetacodeUnaryOperator Operator,
    MetacodeExpression Operand,
    bool IsPostfix = false) : MetacodeExpression;

/// <summary>The conditional operator: <c>c ? a : b</c>, Python's <c>a if c else b</c>.</summary>
public sealed record MetacodeConditional(
    MetacodeExpression Condition,
    MetacodeExpression WhenTrue,
    MetacodeExpression WhenFalse) : MetacodeExpression;

public sealed record MetacodeIndex(
    MetacodeExpression Target,
    IReadOnlyList<MetacodeExpression> Indices) : MetacodeExpression;

public sealed record MetacodeMemberInitializer(string Name, MetacodeExpression Value) : MetacodeNode;

/// <summary>
/// <c>new T(args)</c>, plus the two initializer forms: collection elements
/// (<c>new List&lt;int&gt; { 1, 2 }</c>) and member assignments (<c>new Foo { A = 1 }</c>).
/// </summary>
public sealed record MetacodeObjectCreation(
    MetacodeTypeRef Type,
    IReadOnlyList<MetacodeArgument>? Arguments = null,
    IReadOnlyList<MetacodeExpression>? CollectionElements = null,
    IReadOnlyList<MetacodeMemberInitializer>? MemberInitializers = null) : MetacodeExpression;

/// <summary>
/// <c>target with { A = x }</c>: a copy of a value whose listed members are replaced — the object
/// update a C# record, a case class or Python's <c>dataclasses.replace</c> expresses.
/// </summary>
public sealed record MetacodeWithExpression(
    MetacodeExpression Target,
    IReadOnlyList<MetacodeMemberInitializer> Initializers) : MetacodeExpression;

/// <summary>
/// <c>new { A = x, y }</c>: an object with no declared type, as a list of named values. A shorthand
/// member keeps the name of the expression it reads.
/// </summary>
public sealed record MetacodeAnonymousObject(
    IReadOnlyList<MetacodeMemberInitializer> Members) : MetacodeExpression;

public sealed record MetacodeNewArray(
    MetacodeTypeRef? ElementType,
    IReadOnlyList<MetacodeExpression>? Sizes = null,
    IReadOnlyList<MetacodeExpression>? Elements = null) : MetacodeExpression;

public sealed record MetacodeCast(MetacodeTypeRef Type, MetacodeExpression Operand) : MetacodeExpression;

public enum MetacodeTypeCheckKind
{
    Is = 0,
    IsNot,
    As,
}

/// <summary><c>x is T</c>, <c>x is not T</c>, <c>x as T</c>, and the pattern form with a binding name.</summary>
public sealed record MetacodeTypeCheck(
    MetacodeExpression Operand,
    MetacodeTypeRef Type,
    MetacodeTypeCheckKind Kind = MetacodeTypeCheckKind.Is,
    string? Binding = null) : MetacodeExpression;

/// <summary>
/// <c>operand is pattern</c> for a pattern the model keeps as text — the same decision
/// <see cref="MetacodeSwitchArm"/> makes, because pattern syntax is the most language-specific part of
/// an expression. <see cref="Pattern"/> is what follows <c>is</c>, e.g. <c>not ("a" or "b")</c> or
/// <c>{ } x</c>; the operand stays a real node, so a pattern match is not opaque.
/// </summary>
public sealed record MetacodePatternMatch(
    MetacodeExpression Operand,
    string Pattern) : MetacodeExpression;

/// <summary>
/// A pattern used where only a pattern is legal (a switch case label), kept as text for the same
/// reason as <see cref="MetacodePatternMatch"/>.
/// </summary>
public sealed record MetacodePatternText(string Pattern) : MetacodeExpression
{
    /// <summary>
    /// The canonical spelling of a pattern's text: whitespace collapsed and the padding inside
    /// parentheses removed, so the C# frontend and the pseudocode reader produce the same string and a
    /// renderer↔parser round trip is exact.
    /// </summary>
    public static string Normalize(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return string.Empty;

        var builder = new StringBuilder(pattern.Length);
        var pendingSpace = false;
        var inString = false;
        var inCharacter = false;
        var escaped = false;

        foreach (var c in pattern)
        {
            if (inString || inCharacter)
            {
                builder.Append(c);
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (inString && c == '"')
                    inString = false;
                else if (inCharacter && c == '\'')
                    inCharacter = false;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (c == '"')
                inString = true;
            else if (c == '\'')
                inCharacter = true;

            var previous = builder.Length > 0 ? builder[^1] : '\0';
            var noSpaceBefore = c is ')' or ']' or ',' or ':' or '.';
            var noSpaceAfterPrevious = previous is '(' or '[' or '.';

            if (pendingSpace && builder.Length > 0 && !noSpaceBefore && !noSpaceAfterPrevious)
                builder.Append(' ');

            builder.Append(c);
            pendingSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary><c>..items</c>: a spread element of a collection expression.</summary>
public sealed record MetacodeSpread(MetacodeExpression Operand) : MetacodeExpression;

public enum MetacodeCollectionKind
{
    Array = 0,
    List,
    Set,
    Tuple,
}

public sealed record MetacodeCollection(
    MetacodeCollectionKind Kind,
    IReadOnlyList<MetacodeExpression> Items) : MetacodeExpression;

public sealed record MetacodeDictionaryEntry(
    MetacodeExpression Key,
    MetacodeExpression Value) : MetacodeNode;

public sealed record MetacodeDictionary(
    IReadOnlyList<MetacodeDictionaryEntry> Entries) : MetacodeExpression;

/// <summary>The four containers a comprehension can build, which is also how it prints.</summary>
public enum MetacodeComprehensionKind
{
    List = 0,
    Set,
    Dictionary,
    Generator,
}

public enum MetacodeComprehensionClauseKind
{
    For = 0,
    If,
}

/// <summary>
/// One clause of a comprehension, in source order: <c>for name[, name] in expression</c> or
/// <c>if condition</c>. <see cref="Variable"/> is null on an <see cref="MetacodeComprehensionClauseKind.If"/>
/// clause.
/// </summary>
public sealed record MetacodeComprehensionClause(
    MetacodeComprehensionClauseKind Kind,
    IReadOnlyList<string>? Variable,
    MetacodeExpression Expression) : MetacodeNode
{
    public IReadOnlyList<string> Targets => Variable ?? Array.Empty<string>();
}

/// <summary>
/// <c>[element for x in xs if p]</c> and its set, dict and generator spellings. For a
/// <see cref="MetacodeComprehensionKind.Dictionary"/>, <see cref="Element"/> is the key and
/// <see cref="Value"/> the value; every other kind leaves <see cref="Value"/> null.
/// </summary>
public sealed record MetacodeComprehension(
    MetacodeComprehensionKind Kind,
    MetacodeExpression Element,
    IReadOnlyList<MetacodeComprehensionClause> Clauses,
    MetacodeExpression? Value = null) : MetacodeExpression;

/// <summary>A lambda, either expression-bodied or block-bodied — never both.</summary>
public sealed record MetacodeLambda(
    IReadOnlyList<MetacodeParameter>? Parameters = null,
    MetacodeBlock? Body = null,
    MetacodeExpression? Expression = null) : MetacodeExpression;

public sealed record MetacodeAwait(MetacodeExpression Operand) : MetacodeExpression;

/// <summary>A slice or range: <c>a[1..^1]</c>, Python's <c>a[1:5:2]</c>.</summary>
public sealed record MetacodeRange(
    MetacodeExpression? Start = null,
    MetacodeExpression? End = null,
    MetacodeExpression? Step = null) : MetacodeExpression;

/// <summary>The from-the-end index <c>^1</c>.</summary>
public sealed record MetacodeIndexFromEnd(MetacodeExpression Operand) : MetacodeExpression;

public sealed record MetacodeInterpolationPart(
    string? Text,
    MetacodeExpression? Expression) : MetacodeNode;

public sealed record MetacodeInterpolatedString(
    IReadOnlyList<MetacodeInterpolationPart> Parts) : MetacodeExpression;

/// <summary>An assignment used as an expression (<c>x = y = 1</c>, the walrus <c>:=</c>).</summary>
public sealed record MetacodeAssignmentExpression(
    MetacodeExpression Target,
    MetacodeAssignmentOperator Operator,
    MetacodeExpression Value) : MetacodeExpression;

public sealed record MetacodeTypeOf(MetacodeTypeRef Type) : MetacodeExpression;

/// <summary>
/// One arm of a switch expression. The pattern stays text because pattern syntax is the most
/// language-specific part of an expression; the value it produces is a real node.
/// </summary>
public sealed record MetacodeSwitchArm(
    string Pattern,
    MetacodeExpression? Guard,
    MetacodeExpression Value) : MetacodeNode;

public sealed record MetacodeSwitchExpression(
    MetacodeExpression Subject,
    IReadOnlyList<MetacodeSwitchArm> Arms) : MetacodeExpression;

public sealed record MetacodeOpaqueExpression(
    MetacodeLanguage Language,
    string Text,
    string Reason) : MetacodeExpression;
