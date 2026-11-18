using Snitch;
using Snitch.IO;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// One VisualMCP process serves one workspace. These pin the three consequences: the root cannot be
/// repointed, load_solution refuses a solution outside it with an actionable error, and the store
/// belongs to the workspace's primary solution rather than to whichever one is active.
/// </summary>
public sealed class WorkspaceBoundaryTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public WorkspaceBoundaryTests()
    {
        _root = Temp("vmcp-root-");
        _outside = Temp("vmcp-outside-");

        File.WriteAllText(Path.Combine(_root, "Root.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_outside, "Other.sln"), string.Empty);
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_outside);
    }

    [Fact]
    public async Task Load_solution_refuses_a_path_outside_the_pinned_root()
    {
        using var context = new SnitchContext(new SnitchOptions());
        context.PinWorkspace(_root);

        var result = await new IoService(context).LoadAsync(Path.Combine(_outside, "Other.sln"));

        Assert.NotNull(result.Error);
        Assert.Contains("outside this server's workspace", result.Error);
        Assert.Contains(_root, result.Error);
    }

    [Fact]
    public void A_pinned_workspace_root_cannot_be_repointed()
    {
        using var context = new SnitchContext(new SnitchOptions());
        context.PinWorkspace(_root);

        var error = Assert.Throws<InvalidOperationException>(() => context.WorkingDirectory = _outside);
        Assert.Contains("pinned", error.Message);

        Assert.Throws<InvalidOperationException>(() => context.PinWorkspace(_outside));
        Assert.Equal(_root, context.WorkspaceRoot);
    }

    [Fact]
    public void The_store_lives_next_to_the_primary_solution_and_does_not_move()
    {
        // A nested solution is a normal layout (a sample, a submodule). It must not fork a second
        // database and a second board for the same repository.
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Nested.sln"), string.Empty);

        using var context = new SnitchContext(new SnitchOptions());
        context.PinWorkspace(_root);
        using var store = new VisualMcpStore(context);

        var first = store.EnsureDatabase();
        Assert.NotNull(first);
        Assert.Equal(Path.Combine(_root, ".visualMCP"), Path.GetDirectoryName(first));

        // Re-scanning the environment (which changes what discovery would pick as active) does not
        // move the store: it was resolved once.
        context.RefreshSolutions();
        Assert.Equal(first, store.EnsureDatabase());
    }

    private static string Temp(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind is not a test failure.
        }
    }
}
