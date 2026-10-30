using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.Gate;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The compilation cache behind the style engine's compiler gate: a project state is compiled once, and
/// an edit is judged by re-parsing only its own document on top of that compilation.
/// </summary>
public sealed class CompilationCacheTests
{
    [Fact]
    public async Task A_repeated_ask_reuses_the_compilation()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", "namespace N; public sealed class A { public int M() => 1; }"));
        var project = workspace.CurrentSolution!.Projects.Single();

        var cache = new CompilationCache();
        var first = await cache.GetAsync(project);
        var second = await cache.GetAsync(project);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Same(first.Compilation, second.Compilation);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Empty(first.Messages);
    }

    [Fact]
    public async Task A_changed_document_is_a_different_state()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", "namespace N; public sealed class A { public int M() => 1; }"));
        var project = workspace.CurrentSolution!.Projects.Single();
        var document = project.Documents.Single();

        var cache = new CompilationCache();
        var before = await cache.GetAsync(project);

        var edited = project.Solution.WithDocumentText(document.Id, SourceText.From("namespace N; public sealed class A { }"));
        var after = await cache.GetAsync(edited.GetProject(project.Id)!);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.False(after.FromCache);
        Assert.NotSame(before.Compilation, after.Compilation);
    }

    [Fact]
    public async Task Splicing_one_document_keeps_the_other_trees()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", "namespace N; public sealed class A { }"),
            ("B.cs", "namespace N; public sealed class B { }"));
        var project = workspace.CurrentSolution!.Projects.Single();

        var cache = new CompilationCache();
        var baseline = await cache.GetAsync(project);
        var documentB = project.Documents.Single(document => document.Name == "B.cs");

        var spliced = baseline.WithDocument(documentB, SourceText.From("namespace N; public sealed class B { public int X; }"));

        var treeABefore = baseline.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("A.cs", StringComparison.Ordinal));
        var treeAAfter = spliced.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("A.cs", StringComparison.Ordinal));
        Assert.Same(treeABefore, treeAAfter);

        var treeBBefore = baseline.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("B.cs", StringComparison.Ordinal));
        var treeBAfter = spliced.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("B.cs", StringComparison.Ordinal));
        Assert.NotSame(treeBBefore, treeBAfter);
        Assert.Contains("public int X", treeBAfter.ToString(), StringComparison.Ordinal);
        Assert.Empty(spliced.Messages);
        Assert.NotEqual(baseline.Fingerprint, spliced.Fingerprint);
    }

    [Fact]
    public async Task A_spliced_document_reports_its_own_compiler_error()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", "namespace N; public sealed class A { }"));
        var project = workspace.CurrentSolution!.Projects.Single();
        var document = project.Documents.Single();

        var cache = new CompilationCache();
        var baseline = await cache.GetAsync(project);
        Assert.Empty(baseline.Messages);

        var broken = baseline.WithDocument(document, SourceText.From(
            "namespace N; public sealed class A { private int _x = \"not an int\"; }"));

        Assert.NotEmpty(broken.Messages);
        Assert.Contains(broken.Messages, message => message.Contains("CS0029", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_documents_are_spliced_in_one_compilation()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", "namespace N; public sealed class A { }"),
            ("B.cs", "namespace N; public sealed class B { }"));
        var project = workspace.CurrentSolution!.Projects.Single();

        var cache = new CompilationCache();
        var baseline = await cache.GetAsync(project);

        var changes = project.Documents
            .Select(document => (document, SourceText.From(document.Name == "A.cs"
                ? "namespace N; public sealed class A { public int One => 1; }"
                : "namespace N; public sealed class B { public int Two => 2; }")))
            .ToList();

        var spliced = baseline.With(changes);

        Assert.Empty(spliced.Messages);
        Assert.Equal(2, spliced.Compilation.SyntaxTrees.Count());
        Assert.All(spliced.Compilation.SyntaxTrees, tree => Assert.Contains("=>", tree.ToString(), StringComparison.Ordinal));
    }
}
