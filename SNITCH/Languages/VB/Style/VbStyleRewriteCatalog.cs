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
        new TypeOfNameRewrite(),
        new ArgumentNameOfRewrite(),
        new SimplifyInterpolationRewrite(),
        new TupleElementNameRewrite(),
        new SimplifyObjectCreationRewrite(),
        new ParenthesesForClarityRewrite(),
        new QualifyMemberAccessRewrite(),
        new ModifierOrderRewrite(),
        new AccessibilityModifierRewrite(),
        new ReadonlyFieldRewrite(),
        new UnusedPrivateMemberRewrite(),
        new UnreadPrivateMemberRewrite(),
        new UnreachableCodeRewrite(),
        new ConditionalReturnRewrite(),
        new ConditionalAssignmentRewrite(),
        new MissingSelectCasesRewrite(),
        new ObjectInitializerRewrite(),
        new UnnecessaryAssignmentRewrite(),
        new HashCodeCombineRewrite(),
        new LinqWhereChainRewrite(),
        new LinqTypeCastRewrite(),
        new MultipleBlankLinesRewrite(),
        new BlankLineAfterBlockRewrite(),
        new FileHeaderRewrite(),
        new UseAutoPropertyRewrite(),
        new UnusedParameterRewrite(),
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
        ("IDE0028", "Visual Basic has no collection expression: the IDE's own suggestion is 'From { … }', which is a different shape"),
        ("IDE0037", "Visual Basic has no inferred member name: an anonymous type always spells '.Name = Name'"),
        ("IDE0050", "anonymous type → tuple is a refactoring: it changes the shape a caller sees"),
        ("IDE0058", "Visual Basic has no discard assignment, and it does not need one: a call is already a statement"),
        ("IDE0074", "Visual Basic has no null-coalescing assignment operator, so there is nothing to coalesce into"),
        ("IDE0079", "removing an unnecessary suppression is an audit over the compiler's diagnostics, not a local shape"),
        ("IDE0130", "namespace vs folder: a project layout decision"),
        ("IDE0270", "Visual Basic has no throw expression, so the coalesce form cannot carry the exception"),
    ];
}
