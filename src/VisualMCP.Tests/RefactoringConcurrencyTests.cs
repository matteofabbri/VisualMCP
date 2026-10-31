using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Two tool calls that edit the same file must not both pass the stale check and overwrite each other.
/// The write path serialises the check-and-write per path, so the loser is refused with a reason and the
/// file keeps the winner's text — a visible failure instead of a lost update.
/// </summary>
public sealed class RefactoringConcurrencyTests
{
    [Fact]
    public async Task Two_concurrent_writes_to_one_file_never_lose_an_update()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var document = solution!.Projects.Single().Documents.Single(candidate => candidate.Name == "C.cs");

        // Both plans are built from the same snapshot: this is the race the gate has to make safe.
        const string firstText = "// first\ninternal class C { }\n";
        const string secondText = "// second\ninternal class C { }\n";

        var outcomes = await Task.WhenAll(
            Capture(() => RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, firstText)),
            Capture(() => RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, secondText)));

        Assert.Equal(1, outcomes.Count(error => error is null));
        Assert.Equal(1, outcomes.Count(error => error is InvalidOperationException));

        var onDisk = File.ReadAllText(workspace.PathOf("C.cs"));
        Assert.True(onDisk.Contains("// first", StringComparison.Ordinal) ^
                    onDisk.Contains("// second", StringComparison.Ordinal),
            "the file must hold exactly one of the two writes, not a mix: " + onDisk);
    }

    [Fact]
    public async Task A_write_planned_from_an_old_snapshot_is_refused()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var document = solution!.Projects.Single().Documents.Single(candidate => candidate.Name == "C.cs");

        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, "// written\ninternal class C { }\n");

        // The same captured document still describes the original text: a second plan from it is stale.
        var error = await Capture(() => RefactoringFileWriter.WriteAndUpdateAsync(
            workspace, document, "// stale\ninternal class C { }\n"));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("// written", File.ReadAllText(workspace.PathOf("C.cs")));
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
