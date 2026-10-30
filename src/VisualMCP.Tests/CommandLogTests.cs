using System.Text.Json;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The command log: one row per received tool call, with the date, the time and what was asked for.
/// </summary>
public sealed class CommandLogTests
{
    private static (FakeWorkspace Workspace, VisualMcpStore Store) NewStore()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        return (workspace, new VisualMcpStore(workspace));
    }

    [Fact]
    public void RecordCommand_StoresDateAndTimeAndArguments()
    {
        var (workspace, store) = NewStore();
        using var _ = workspace;

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        store.RecordCommand("apply_rename", "{\"oldName\":\"A\"}");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var entry = Assert.Single(store.RecentCommands(10));
        Assert.Equal("apply_rename", entry.Tool);
        Assert.Equal("{\"oldName\":\"A\"}", entry.Arguments);
        Assert.InRange(entry.OccurredAt, before, after);
    }

    [Fact]
    public void RecentCommands_ReturnsNewestFirstAndFiltersByTool()
    {
        var (workspace, store) = NewStore();
        using var _ = workspace;

        store.RecordCommand("read_file");
        store.RecordCommand("apply_rename");
        store.RecordCommand("read_file");

        var all = store.RecentCommands(10);
        Assert.Equal(3, all.Count);
        Assert.Equal("read_file", all[0].Tool);   // newest first
        Assert.Equal("apply_rename", all[1].Tool);

        var onlyRename = store.RecentCommands(10, "apply_rename");
        Assert.Single(onlyRename);
        Assert.Equal("apply_rename", onlyRename[0].Tool);
    }

    [Fact]
    public void CommandCount_CountsEveryRowAndSurvivesAReopen()
    {
        var (workspace, store) = NewStore();
        using var _ = workspace;

        store.RecordCommand("build_project");
        store.RecordCommand("run_tests");
        Assert.Equal(2, store.CommandCount());

        // A second store over the same folder is what a server restart amounts to.
        var reopened = new VisualMcpStore(workspace);
        Assert.Equal(2, reopened.CommandCount());
        Assert.Equal("run_tests", reopened.RecentCommands(1)[0].Tool);
    }

    [Fact]
    public void RecordCommand_IgnoresAnEmptyToolName()
    {
        var (workspace, store) = NewStore();
        using var _ = workspace;

        store.RecordCommand("  ");
        Assert.Equal(0, store.CommandCount());
    }

    [Fact]
    public void Formatter_RedactsCredentialKeysAndCapsThePayload()
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("NUGET_API_KEY"),
            ["value"] = JsonSerializer.SerializeToElement("super-secret-value"),
            ["token"] = JsonSerializer.SerializeToElement("abc123"),
            ["Authorization"] = JsonSerializer.SerializeToElement("Bearer xyz"),
            ["path"] = JsonSerializer.SerializeToElement("src/App.cs"),
        };

        var text = CommandLogFormatter.Format("gh_secret", arguments);

        Assert.DoesNotContain("super-secret-value", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("Bearer xyz", text);
        Assert.Contains("src/App.cs", text);
        Assert.Contains("NUGET_API_KEY", text);

        // A harmless "value" for a tool that is not about secrets stays readable.
        var plain = CommandLogFormatter.Format("some_tool", new Dictionary<string, JsonElement>
        {
            ["value"] = JsonSerializer.SerializeToElement(42),
        });
        Assert.Contains("42", plain);
    }

    [Fact]
    public void Formatter_ClipsLongValuesAndReportsMissingArguments()
    {
        var longText = new string('x', 5000);
        var text = CommandLogFormatter.Format("run_command", new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement(longText),
        });

        Assert.True(text.Length <= 1100, $"arguments were not capped: {text.Length} chars");
        Assert.Contains("…", text);
        Assert.Equal("(no args)", CommandLogFormatter.Format("list_directory", null));
    }
}
