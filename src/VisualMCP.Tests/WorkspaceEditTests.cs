using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The multi-document suggestion as one atomic change: its edits name files, which may live in different
/// projects, and either every document it plans is written or none is.
/// </summary>
public sealed class WorkspaceEditTests
{
    private const string LibSource = "namespace Lib; public sealed class Anchor { }";
    private const string AppSource = "namespace App; public static class Use { public static int Value = 1; }";

    [Fact]
    public async Task A_suggestion_can_edit_a_file_in_another_project()
    {
        using var workspace = FakeWorkspace.CreateProjects(
            ("Lib", [("Anchor.cs", LibSource)]),
            ("App", [("Use.cs", AppSource)]));

        var result = await RunAsync(workspace, replacement: "2");

        Assert.Equal(2, ResultAssert.Json(result).GetProperty("filesChanged").GetInt32());
        Assert.Contains("// touched", workspace.Read("Anchor.cs"), StringComparison.Ordinal);
        Assert.Contains("= 2", workspace.Read("Use.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_one_file_would_not_compile_neither_file_is_written()
    {
        using var workspace = FakeWorkspace.CreateProjects(
            ("Lib", [("Anchor.cs", LibSource)]),
            ("App", [("Use.cs", AppSource)]));

        // "string" in an int field: the App edit alone adds a compiler error.
        var result = await RunAsync(workspace, replacement: "\"not an int\"");

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(LibSource, workspace.Read("Anchor.cs"));
        Assert.Equal(AppSource, workspace.Read("Use.cs"));

        // Two entries: the compiler refused the App document, and the suggestion was withdrawn as a whole.
        var refusals = json.GetProperty("refused").EnumerateArray()
            .Select(entry => entry.GetProperty("reason").GetString()!)
            .ToList();
        Assert.Contains(refusals, reason => reason.Contains("compiler errors", StringComparison.Ordinal));
        Assert.Contains(refusals, reason => reason.Contains("none of it was applied", StringComparison.Ordinal));
    }

    private static Task<object> RunAsync(FakeWorkspace workspace, string replacement) =>
        new StyleRewriteService(
                workspace, store: null, new TwoFileRuleSet(replacement), CSharpStyleLanguage.Default, "test")
            .SimplifyAsync("solution", filePath: null, projectName: null, rules: null, dryRun: false);

    private sealed class TwoFileRuleSet(string replacement) : IStyleRuleSet
    {
        public IReadOnlyList<StyleRewrite> All { get; } = [new TwoFileRewrite(replacement)];

        public IReadOnlyList<StyleRule> Supported { get; } = [TwoFileRewrite.RuleDefinition];

        public IReadOnlyList<(string Id, string Reason)> NotRewritten => [];

        public string SupportedIds => TwoFileRewrite.RuleDefinition.Id;
    }

    /// <summary>Edits the declaration in Lib and the value in App: two files, two projects, one match.</summary>
    private sealed class TwoFileRewrite(string replacement) : StyleRewrite
    {
        public static readonly StyleRule RuleDefinition = new(
            "TEST0002", "Two-file probe", "insert a comment in one project and replace a value in another");

        public override StyleRule Rule => RuleDefinition;

        public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
            null;

        public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
        {
            if (node is not ClassDeclarationSyntax type || type.Identifier.ValueText != "Anchor") return null;
            if (type.SyntaxTree.FilePath is not { Length: > 0 } libPath) return null;
            if (context.Solution is null) return null;

            var app = context.Solution.Projects
                .SelectMany(project => project.Documents)
                .FirstOrDefault(document => document.Name == "Use.cs");
            if (app?.FilePath is not { } appPath) return null;

            var text = app.GetTextAsync().GetAwaiter().GetResult().ToString();
            var index = text.IndexOf("= 1;", StringComparison.Ordinal);
            if (index < 0) return null;

            return Shape(Rule, type,
                [
                    new SpanEdit(libPath, 0, 0, "// touched\n"),
                    new SpanEdit(appPath, index + 2, 1, replacement),
                ],
                "Anchor + Use", "touched + replaced");
        }
    }
}
