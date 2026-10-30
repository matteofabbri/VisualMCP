using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.Gate;
using Snitch.Languages.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The semantic level of the gate: a rule that claims equivalence is compared operation by operation,
/// and a difference refuses the file instead of being written. The unit tests pin the normalisation
/// (implicit conversions and receivers are not differences); the end-to-end test proves that a rule
/// which lies is caught.
/// </summary>
public sealed class SemanticGateTests
{
    [Fact]
    public async Task Identical_trees_have_no_differences()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public int F(int x) => x + 1; }"));
        var document = workspace.CurrentSolution!.Projects.Single().Documents.Single();
        var model = (await document.GetSemanticModelAsync())!;
        var node = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<BinaryExpressionSyntax>().Single();

        var operation = model.GetOperation(node);

        Assert.NotNull(operation);
        Assert.Empty(SemanticEquivalence.Differences(operation, operation));
    }

    [Fact]
    public async Task A_different_operator_is_a_difference()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public bool F(int x) => x + 1 > 2 && x < 4; }"));
        var document = workspace.CurrentSolution!.Projects.Single().Documents.Single();
        var model = (await document.GetSemanticModelAsync())!;
        var binaries = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<BinaryExpressionSyntax>().ToList();
        var add = binaries.First(binary => binary.IsKind(SyntaxKind.AddExpression));
        var compare = binaries.First(binary => binary.IsKind(SyntaxKind.LessThanExpression));

        var differences = SemanticEquivalence.Differences(model.GetOperation(add), model.GetOperation(compare));

        Assert.NotEmpty(differences);
    }

    [Fact]
    public async Task A_different_constant_is_a_difference()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", "internal class C { public int A(int x) => x + 1; public int B(int x) => x + 2; }"));
        var document = workspace.CurrentSolution!.Projects.Single().Documents.Single();
        var model = (await document.GetSemanticModelAsync())!;
        var additions = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(binary => binary.IsKind(SyntaxKind.AddExpression))
            .ToList();

        var differences = SemanticEquivalence.Differences(model.GetOperation(additions[0]), model.GetOperation(additions[1]));

        Assert.NotEmpty(differences);
    }

    [Fact]
    public async Task This_qualification_is_not_a_semantic_difference()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", "internal class C { private int _x; public int A() => this._x; public int B() => _x; }"));
        var document = workspace.CurrentSolution!.Projects.Single().Documents.Single();
        var model = (await document.GetSemanticModelAsync())!;
        var root = (await document.GetSyntaxRootAsync())!;

        var qualified = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(access => access.Expression is ThisExpressionSyntax && access.Name.Identifier.ValueText == "_x");
        var methodB = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "B");
        var plain = methodB.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Single(name => name.Identifier.ValueText == "_x");

        Assert.Empty(SemanticEquivalence.Differences(model.GetOperation(qualified), model.GetOperation(plain)));
    }

    /// <summary>
    /// The rule matches <c>x + 1</c> and claims equivalence while producing <c>x + 2</c>: the compiler
    /// accepts it, and only the operation comparison can tell.
    /// </summary>
    [Fact]
    public async Task A_rule_that_claims_equivalence_and_changes_meaning_is_refused()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public int F(int x) => x + 1; }"));
        var service = new StyleRewriteService(
            workspace, store: null, new LyingRuleSet(), CSharpStyleLanguage.Default, "test");

        var result = await service.SimplifyAsync(
            "document", workspace.PathOf("C.cs"), projectName: null, rules: null, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());

        // The file is left exactly as it was: a refused rewrite writes nothing.
        Assert.Contains("x + 1", workspace.Read("C.cs"), StringComparison.Ordinal);

        var semantics = json.GetProperty("semantics");
        Assert.Equal(1, semantics.GetProperty("compared").GetInt32());
        Assert.Equal(0, semantics.GetProperty("verified").GetInt32());

        var failure = semantics.GetProperty("failures")[0];
        Assert.Equal("TEST0001", failure.GetProperty("Rule").GetString());
        Assert.NotEmpty(failure.GetProperty("Differences").EnumerateArray());

        var refused = json.GetProperty("refused")[0];
        Assert.Contains("claims equivalence", refused.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    private sealed class LyingRuleSet : IStyleRuleSet
    {
        private static readonly StyleRule Claiming = new(
            "TEST0001", "Claims-equivalence probe", "x + 1 → x + 2 while claiming equivalence", ClaimsEquivalence: true);

        private static readonly List<StyleRewrite> Rewrites = [new LyingRewrite()];

        public IReadOnlyList<StyleRewrite> All => Rewrites;

        public IReadOnlyList<StyleRule> Supported => [Claiming];

        public IReadOnlyList<(string Id, string Reason)> NotRewritten => [];

        public string SupportedIds => Claiming.Id;
    }

    private sealed class LyingRewrite : StyleRewrite
    {
        public override StyleRule Rule { get; } = new(
            "TEST0001", "Claims-equivalence probe", "x + 1 → x + 2 while claiming equivalence", ClaimsEquivalence: true);

        public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            if (node is not BinaryExpressionSyntax binary || !binary.IsKind(SyntaxKind.AddExpression))
                return null;
            if (binary.Right is not LiteralExpressionSyntax literal || literal.Token.ValueText != "1")
                return null;

            return Match(node, SyntaxFactory.ParseExpression(binary.Left + " + 2"));
        }
    }
}
