using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The workspace snapshot is shared by every tool call, and <c>run_plan</c> deliberately runs writes in
/// parallel. These tests pin down the property that makes that safe: concurrent edits to different
/// documents all end up in the model, instead of the last writer's snapshot replacing the others'.
/// </summary>
public sealed class WorkspaceConcurrencyTests
{
    [Fact]
    public void ConcurrentWritesToDifferentDocumentsAllSurvive()
    {
        const int writers = 24;

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(projectId, "P", "P", LanguageNames.CSharp);

        var documents = new DocumentId[writers];
        for (var i = 0; i < writers; i++)
        {
            documents[i] = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(
                documents[i], $"D{i}.cs", $"internal class D{i} {{ }}", filePath: $"D{i}.cs");
        }

        var target = new SnitchSolution("P", "P.sln", SolutionFormat.Sln);
        Assert.True(target.TryReplaceLoaded(null, solution));

        // Dedicated threads behind a barrier, not Parallel.For: the pool happily serialises a workload this
        // small, and a test that never overlaps cannot tell a compare-and-swap from an assignment.
        using var barrier = new Barrier(writers);
        var failures = new ConcurrentQueue<string>();
        var threads = new List<Thread>(writers);

        for (var i = 0; i < writers; i++)
        {
            var index = i;
            var thread = new Thread(() =>
            {
                var documentId = documents[index];
                var text = SourceText.From($"internal class D{index} {{ /* {index} */ }}");
                barrier.SignalAndWait();

                if (!target.TryUpdate(current => current.WithDocumentText(documentId, text)))
                    failures.Enqueue($"writer {index} found no snapshot to change");
            });

            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Empty(failures);

        var final = target.Loaded!;
        for (var i = 0; i < writers; i++)
        {
            var text = final.GetDocument(documents[i])!
                .GetTextAsync()
                .GetAwaiter()
                .GetResult()
                .ToString();
            Assert.Contains($"/* {i} */", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TryUpdateOnAnUnloadedSolutionDoesNothing()
    {
        var target = new SnitchSolution("P", "P.sln", SolutionFormat.Sln);

        Assert.False(target.TryUpdate(_ => throw new Xunit.Sdk.XunitException("the change must not run")));
        Assert.Null(target.Loaded);
    }

    [Fact]
    public void TryReplaceLoadedOnlySwapsTheSnapshotItWasBasedOn()
    {
        using var workspace = new AdhocWorkspace();
        var target = new SnitchSolution("P", "P.sln", SolutionFormat.Sln);
        var first = workspace.CurrentSolution;
        var second = first.AddProject(ProjectId.CreateNewId(), "Q", "Q", LanguageNames.CSharp);

        Assert.True(target.TryReplaceLoaded(null, first));
        Assert.False(target.TryReplaceLoaded(null, second));   // someone else already moved it on
        Assert.Same(first, target.Loaded);

        Assert.True(target.TryReplaceLoaded(first, second));
        Assert.Same(second, target.Loaded);
    }
}
