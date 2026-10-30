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
    ];

    /// <summary>Every id this engine can rewrite (a family such as IDE0028/IDE0300 shares one matcher).</summary>
    public static IReadOnlyList<StyleRule> Supported =>
        All.SelectMany(rewrite => rewrite.Rules).ToList();

    public static IEnumerable<StyleRule> RulesFor(string id) =>
        All.Where(rewrite => rewrite.Rules.Any(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
           .SelectMany(rewrite => rewrite.Rules);

    public static string SupportedIds => string.Join(", ", Supported.Select(rule => rule.Id));

    /// <summary>
    /// The rest of Microsoft's code-style table. Each entry says why there is no automatic rewrite, so
    /// "not supported" never reads as "forgotten".
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Reason)> NotRewritten =
    [
        ("IDE0005", "removes unused imports: use optimize_usings, which needs assembly-level analysis"),
        ("IDE0007", "prefers 'var' — contradicts IDE0008, so it is a team decision, not a fix"),
        ("IDE0008", "prefers the explicit type — contradicts IDE0007, so it is a team decision"),
        ("IDE0032", "use auto property: use property_to_auto"),
        ("IDE0048", "adds parentheses: contradicts IDE0047, so it is a team decision"),
        ("IDE0050", "anonymous type → tuple: changes the public shape"),
        ("IDE0064", "makes struct fields writable: changes the type's contract"),
        ("IDE0065", "using directive placement: layout policy"),
        ("IDE0076", "invalid global SuppressMessage: needs the documented target list"),
        ("IDE0077", "legacy SuppressMessage target: needs the documented target list"),
        ("IDE0078", "its '!(x is T)' form is what IDE0083 rewrites; the or-pattern form (x == default || x > default) has no mechanical shape"),
        ("IDE0081", "Visual Basic only"),
        ("IDE0084", "Visual Basic only"),
        ("IDE0110", "removes a discard: the discard may be deliberate"),
        ("IDE0130", "namespace vs folder: a project layout decision"),
        ("IDE0140", "Visual Basic only"),
        ("IDE0210", "top-level statements: restructures a program"),
        ("IDE0211", "Program.Main style: restructures a program"),
        ("IDE0221", "explicit cast: would add noise to every dereference"),
        ("IDE0290", "primary constructor: use convert_to_primary_constructor"),
        ("IDE0303", "collection expression for Create(): needs the builder contract"),
        ("IDE0304", "collection expression for a builder: needs the builder contract"),
        ("IDE0305", "collection expression for fluent chains: query restructuring"),
        ("IDE0330", "System.Threading.Lock: needs lock-pattern analysis"),
        ("IDE0350", "implicitly typed lambda: needs conversion analysis"),
        ("IDE0360", "simplified property accessors: layout"),
        ("IDE0370", "Microsoft split it into IDE0240/IDE0241, which this engine rewrites"),
        ("IDE0410", "labeled jump statements: control-flow restructuring"),
        ("IDE1005", "conditional delegate call: it needs the surrounding null check, and d?.Invoke(x) → d(x) changes what a null delegate does"),
        ("IDE1006", "naming styles: configured per repository, not a rewrite"),
        ("IDE1007", "the name does not exist in the current context: a compiler diagnostic"),
        ("IDE2000", "blank-line formatting: IDE0055 family"),
        ("IDE2001", "embedded statements on their own line: layout"),
        ("IDE2002", "blank lines around braces: layout"),
        ("IDE2003", "blank line after a block: layout"),
        ("IDE2004", "blank line in a constructor initializer: layout"),
        ("IDE2005", "blank line in a conditional expression: layout"),
        ("IDE2006", "blank line after an arrow: layout"),
        ("IDE3000", "Copilot suggestion: not a deterministic rewrite"),
    ];
}
