namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// The rewrites <c>simplify_code</c> can apply, and the ids it deliberately does not apply with a
/// reason for each. The second list is what keeps the tool honest: every IDE rule in Microsoft's
/// code-style table is either here or there, never silently missing.
/// </summary>
public static class StyleRewriteCatalog
{
    public static readonly IReadOnlyList<StyleRewrite> All =
    [
        new CollectionExpressionRewrite(),
        new TargetTypedNewRewrite(),
        new SimplifyNameRewrite(),
        new SimplifyMemberAccessRewrite(),
        new RemoveThisQualificationRewrite(),
        new UnnecessaryCastRewrite(),
        new PredefinedTypeRewrite(),
        new NullPropagationRewrite(),
        new CoalesceExpressionRewrite(),
        new SimplifyDefaultRewrite(),
        new IsNullRewrite(),
        new UnnecessaryParenthesesRewrite(),
        new CompoundAssignmentRewrite(),
        new IndexOperatorRewrite(),
        new RangeOperatorRewrite(),
        new CoalesceAssignmentRewrite(),
        new NotPatternRewrite(),
        new UnnecessaryEqualityRewrite(),
        new SimplifyConditionalRewrite(),
        new UnnecessaryLambdaRewrite(),
        new ArgumentNullNameofRewrite(),
        new UnboundGenericNameofRewrite(),
        new SimplifyInterpolationRewrite(),
        new TupleElementNameRewrite(),
        new InferredMemberNameRewrite(),
        new TypeofToNameofRewrite(),
        new NullCheckOverTypeCheckRewrite(),
        new ExtendedPropertyPatternRewrite(),
        new Utf8StringLiteralRewrite(),
        new StackAllocCollectionRewrite(),
        new ReadonlyFieldRewrite(),
        new ReadonlyStructRewrite(),
        new ReadonlyStructMemberRewrite(),
        new AsWithNullCheckRewrite(),
        new IsWithCastRewrite(),
        new ThrowExpressionRewrite(),
        new InlineVariableDeclarationRewrite(),
        new SimpleUsingRewrite(),
        new TupleSwapRewrite(),
        new MethodExpressionBodyRewrite(),
        new ConstructorExpressionBodyRewrite(),
        new OperatorExpressionBodyRewrite(),
        new PropertyExpressionBodyRewrite(),
        new AccessorExpressionBodyRewrite(),
        new LambdaExpressionBodyRewrite(),
        new LocalFunctionExpressionBodyRewrite(),
        new ConditionalReturnRewrite(),
        new ConditionalAssignmentRewrite(),
        new ObjectInitializerRewrite(),
        new DeconstructionRewrite(),
        new SwitchExpressionRewrite(),
        new BracesRewrite(),
        new ModifierOrderRewrite(),
        new NamespaceStyleRewrite(),
        new FileHeaderRewrite(),
        new FormattingRewrite(),
        new StaticLocalFunctionRewrite(),
        new StaticAnonymousFunctionRewrite(),
        new HashCodeCombineRewrite(),
        new ConditionalAccessAsPatternRewrite(),
        new UnnecessaryUnsafeRewrite(),
        new UnreachableCodeRewrite(),
        new UnusedPrivateMemberRewrite(),
        new MissingSwitchCasesRewrite(),
        new MissingSwitchArmsRewrite(),
        new LinqWhereChainRewrite(),
        new LinqTypeCastRewrite(),
        new ForeachExplicitCastRewrite(),
        new AccessibilityModifierRewrite(),
        new UnnecessarySuppressionRewrite(),
        new UnnecessarySuppressionAttributeRewrite(),
        new LambdaToLocalFunctionRewrite(),
        new UnusedExpressionValueRewrite(),
        new RedundantNullableDirectiveRewrite(),
        new MakeMethodSynchronousRewrite(),
        new UnusedParameterRewrite(),
        new UnreadPrivateMemberRewrite(),
        new UnnecessaryAssignmentRewrite(),
        new PreferVarRewrite(),
        new PreferExplicitTypeRewrite(),
        new ParenthesesForClarityRewrite(),
        new MultipleBlankLinesRewrite(),
        new EmbeddedStatementOnOwnLineRewrite(),
        new BlankLinesBetweenBracesRewrite(),
        new BlankLineAfterBlockRewrite(),
        new ConstructorInitializerBlankLineRewrite(),
        new ConditionalExpressionBlankLineRewrite(),
        new ArrowClauseBlankLineRewrite(),
    ];

    /// <summary>Every id this engine can rewrite (a family such as IDE0028/IDE0300 shares one matcher).</summary>
    public static IReadOnlyList<StyleRule> Supported =>
        All.SelectMany(rewrite => rewrite.Rules).ToList();

    public static IEnumerable<StyleRule> RulesFor(string id) =>
        All.Where(rewrite => rewrite.Rules.Any(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
           .SelectMany(rewrite => rewrite.Rules);

    public static string SupportedIds => string.Join(", ", Supported.Select(rule => rule.Id));

    /// <summary>
    /// Every id in Microsoft's code-style table is one of four things, and the four lists together are
    /// exactly that table: rewritten here, delegated to a dedicated tool, not applicable to C#, or not a
    /// code-style rewrite at all (a refactoring, a change of shape, a compiler diagnostic). No id is
    /// left without its category.
    /// </summary>
    /// <remarks>
    /// <see cref="NotRewritten"/> is the union, kept for the tool's "this id does nothing here" answer:
    /// it names the category so a caller can act on it instead of reading "unsupported".
    /// </remarks>
    public static readonly IReadOnlyList<(string Id, string Reason)> Delegated =
    [
        ("IDE0005", "delegated: optimize_usings removes unused imports (it needs assembly-level analysis)"),
        ("IDE0032", "delegated: property_to_auto turns a trivial accessor pair into an auto-property"),
        ("IDE0290", "delegated: convert_to_primary_constructor moves the assignments into the type header"),
    ];

    public static readonly IReadOnlyList<(string Id, string Reason)> NotApplicable =
    [
        ("IDE0081", "Visual Basic only: the C# table lists it for completeness (simplify_vb_code owns it)"),
        ("IDE0084", "Visual Basic only: simplify_vb_code owns the IsNot direction"),
        ("IDE0140", "Visual Basic only: simplify_vb_code owns it"),
        ("IDE1007", "a compiler diagnostic (the name does not exist), not a style rule"),
        ("IDE0370", "superseded by Microsoft: it was split into IDE0240/IDE0241, which this engine rewrites"),
    ];

    public static readonly IReadOnlyList<(string Id, string Reason)> NotRewrites =
    [
        ("IDE0050", "anonymous type → tuple is a refactoring: it changes the shape a caller sees"),
        ("IDE0064", "writable struct fields change the type's contract"),
        ("IDE0065", "using-directive placement is a layout policy of the formatter, not a node rewrite"),
        ("IDE0076", "invalid global SuppressMessage target: needs the documented target list, a suppression audit"),
        ("IDE0077", "legacy SuppressMessage target: the same suppression audit"),
        ("IDE0078", "its negated form is IDE0083, which this engine rewrites; the or-pattern form has no mechanical shape"),
        ("IDE0110", "removing a discard is a semantic decision: the discard may be deliberate"),
        ("IDE0130", "namespace vs folder is a project-layout decision"),
        ("IDE0210", "top-level statements restructure a program: a refactoring"),
        ("IDE0211", "Program.Main style restructures a program: a refactoring"),
        ("IDE0221", "an explicit cast on every dereference adds noise; the rule is not a rewrite"),
        ("IDE0303", "collection expression for a builder needs the builder contract: a refactoring"),
        ("IDE0304", "collection expression over a builder: the same refactoring"),
        ("IDE0305", "collection expression for a fluent chain: query restructuring"),
        ("IDE0330", "System.Threading.Lock conversion needs lock-pattern analysis: a refactoring"),
        ("IDE0350", "implicitly typed lambda needs the target's conversion analysis"),
        ("IDE0360", "simplified property accessors are layout, owned by IDE0025/IDE0026/IDE0027 here"),
        ("IDE0410", "labeled jump statements are control-flow restructuring"),
        ("IDE1005", "d?.Invoke(x) → d(x) changes what a null delegate does: not an equivalent rewrite"),
        ("IDE1006", "naming styles need a rename (apply_rename), not a syntax rewrite"),
        ("IDE3000", "a Copilot suggestion: not deterministic"),
    ];

    /// <summary>The union of the three lists above: every id the engine does not rewrite, with its category.</summary>
    public static IReadOnlyList<(string Id, string Reason)> NotRewritten { get; } =
        Delegated.Concat(NotApplicable).Concat(NotRewrites).ToList();
}
