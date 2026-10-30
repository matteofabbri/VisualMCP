using Snitch.Languages.Style;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// The rewrites <c>simplify_code</c> can apply to Visual Basic, and the ids of Microsoft's table it
/// deliberately does not apply with a reason for each. The universe of the second list is the VB column
/// of that table — the <c>dotnet_style_*</c> rules that apply to both languages plus the
/// <c>visual_basic_style_*</c> ones. An id that Visual Basic does not define at all (C# pattern
/// matching, braces, expression bodies, collection expressions) is not a VB rule and is not listed;
/// the C# catalogue is where those ids live.
/// </summary>
public static class VbStyleRewriteCatalog
{
    public static readonly IReadOnlyList<StyleRewrite> All =
    [
        new SimplifyNameRewrite(),
        new SimplifyMemberAccessRewrite(),
        new RemoveMeQualificationRewrite(),
        new UnnecessaryCastRewrite(),
        new PredefinedTypeRewrite(),
        new IsNothingRewrite(),
        new CoalesceRewrite(),
        new NullPropagationRewrite(),
        new ParenthesesRewrite(),
        new CompoundAssignmentRewrite(),
        new BooleanEqualityRewrite(),
        new SimplifyConditionalRewrite(),
        new IsNotRewrite(),
        new RemoveByValRewrite(),
    ];

    /// <summary>Every id this engine can rewrite (IDE0029/IDE0030 share one matcher).</summary>
    public static IReadOnlyList<StyleRule> Supported =>
        All.SelectMany(rewrite => rewrite.Rules).ToList();

    public static string SupportedIds => string.Join(", ", Supported.Select(rule => rule.Id));

    /// <summary>
    /// The VB rules this layer does not rewrite. Each entry says why, so "not supported" never reads
    /// as "forgotten". The two option-driven entries are the ones whose key the option parser already
    /// maps (<c>visual_basic_preferred_modifier_order</c>, <c>dotnet_style_require_accessibility_modifiers</c>):
    /// the value is available to a matcher, there just is no matcher yet.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Reason)> NotRewritten =
    [
        ("IDE0009", "the other half of IDE0003: requiring 'Me.' is a team preference, and the dotnet_style_qualification_for_* key it needs is not parsed"),
        ("IDE0010", "completing a Select Case needs the enum's members and one case for each: not a local-shape rewrite"),
        ("IDE0017", "object initializers: VB's 'New C With { ... }' fold is not implemented in this pass"),
        ("IDE0028", "VB fills a collection with 'From { ... }', so the C# collection-expression rewrite has no counterpart here"),
        ("IDE0032", "use auto property: the answer needs every write of the backing field; property_to_auto does it for C#"),
        ("IDE0033", "explicitly provided tuple name: resolving ItemN through the tuple's element symbols is not in this pass"),
        ("IDE0035", "unreachable code: needs control-flow analysis of the rest of the block"),
        ("IDE0036", "order modifiers: the 'visual_basic_preferred_modifier_order' key is parsed into StyleOptions, but the per-declaration token rewrite is not written yet"),
        ("IDE0037", "inferred member names: VB anonymous types spell the same shape differently; not in this pass"),
        ("IDE0040", "accessibility: VB's default differs per declaration kind (Public in a Module, Private in a Class), so adding one needs that table"),
        ("IDE0044", "readonly field: needs every reference classified as a read or a write"),
        ("IDE0045", "conditional expression for assignment: the If/Select Case fold is not implemented"),
        ("IDE0046", "conditional expression for return: the same fold is not implemented"),
        ("IDE0048", "adds parentheses: contradicts IDE0047, so it is a team decision"),
        ("IDE0050", "anonymous type → tuple: changes the public shape"),
        ("IDE0051", "remove unused private member: needs every reference in the solution; not in this pass"),
        ("IDE0052", "remove unread private member: needs every reference classified as a read or a write"),
        ("IDE0058", "VB has no discard assignment ('_ = ...'), so there is no place to put an unread value"),
        ("IDE0059", "remove unnecessary value assignment: needs the flow of every read and write"),
        ("IDE0060", "remove unused parameter: changes the signature and every call site, across documents"),
        ("IDE0070", "HashCode.Combine: the hand-written hash fold is not recognised in this pass"),
        ("IDE0071", "simplify interpolation: the rewrite has to rebuild the interpolated string around the interpolation"),
        ("IDE0073", "adds a file header: needs the repository's own header template"),
        ("IDE0074", "VB has no null-coalescing assignment operator, so there is nothing to coalesce into"),
        ("IDE0079", "removes unnecessary suppressions: an analysis finding, not a rewrite"),
        ("IDE0082", "GetType(T).Name → NameOf(T): implementable, but not in this pass"),
        ("IDE0120", "LINQ Where(...).Any() → Any(...): needs the extension-method binding of every LINQ operator"),
        ("IDE0121", "LINQ Where(...).Cast(Of T)() → OfType(Of T)(): the same binding is needed"),
        ("IDE0130", "namespace vs folder: a project layout decision"),
        ("IDE0140", "simplify object creation ('Dim x As New C' versus 'Dim x As C = New C'): a declaration-layout choice, not implemented"),
        ("IDE0270", "null check plus throw: VB has no throw expression, so the coalesce form cannot carry the exception"),
        ("IDE0280", "use nameof: the argument-name literal rewrite is not in this pass"),
        ("IDE2000", "avoid multiple blank lines: layout, IDE0055 family"),
        ("IDE2003", "blank line between a block and the next statement: layout"),
    ];
}
