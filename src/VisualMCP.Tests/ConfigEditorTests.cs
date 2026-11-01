using System.Text.Json.Nodes;
using VisualMCP.Install;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The installed binary's <c>--config</c> mode: the edits the POSIX installer used to make with a small
/// Python program (the server registration, the routing guard, the AGENTS.md block). Every key that is
/// not ours survives, ours is replaced instead of duplicated, and a hook an earlier version registered
/// is recognised and removed.
/// </summary>
public sealed class ConfigEditorTests
{
    [Fact]
    public void The_server_entry_is_written_and_the_rest_of_the_configuration_survives()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");
        File.WriteAllText(config, """{ "theme": "dark", "mcp": { "servers": { "Other": { "type": "stdio" } } } }""");

        Assert.Equal(0, ConfigEditor.Run(["zcode-set", config, "/opt/vmcp/VisualMCP", ""]));

        var root = Read(config);
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("stdio", root["mcp"]!["servers"]!["Other"]!["type"]!.GetValue<string>());

        var entry = root["mcp"]!["servers"]!["VisualMCP"]!;
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        Assert.Equal("/opt/vmcp/VisualMCP", entry["command"]!.GetValue<string>());
        Assert.Empty(entry["args"]!.AsArray());
        Assert.Equal(60000, entry["timeoutMs"]!.GetValue<int>());

        // Two-space indentation, a final newline and no byte-order mark: the shape the Python editor wrote.
        var text = File.ReadAllText(config);
        Assert.StartsWith("{", text);
        Assert.Contains("\n  \"theme\"", text);
        Assert.EndsWith("\n", text);
        Assert.False(text.StartsWith('\uFEFF'));

        // The argument goes into args when there is one (the dll-only layout).
        Assert.Equal(0, ConfigEditor.Run(["zcode-set", config, "dotnet", "/opt/vmcp/VisualMCP.dll"]));
        Assert.Equal("/opt/vmcp/VisualMCP.dll",
            Read(config)["mcp"]!["servers"]!["VisualMCP"]!["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Removing_the_server_entry_leaves_everything_else()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");
        File.WriteAllText(config, """{ "theme": "dark" }""");

        ConfigEditor.Run(["zcode-set", config, "/opt/vmcp/VisualMCP", ""]);
        Assert.Equal(0, ConfigEditor.Run(["zcode-remove", config]));

        var root = Read(config);
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.False(root["mcp"]!["servers"]!.AsObject().ContainsKey("VisualMCP"));

        // Removing it again is not an error and does not rewrite the document.
        Assert.Equal(0, ConfigEditor.Run(["zcode-remove", config]));
    }

    [Fact]
    public void A_hook_is_registered_once_and_an_older_python_entry_is_replaced()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");
        File.WriteAllText(config, """
            {
              "hooks": {
                "events": {
                  "PreToolUse": [
                    { "matcher": "Bash", "hooks": [ { "type": "command", "command": "python3 \"/x/pre-tool-use.py\"" } ] },
                    { "matcher": "Read", "hooks": [ { "type": "command", "command": "other-tool" } ] }
                  ]
                }
              }
            }
            """);

        var command = "\"/opt/vmcp/VisualMCP\" --hook pre-tool-use";
        Assert.Equal(0, ConfigEditor.Run(["zcode-hook-set", config, command]));
        Assert.Equal(0, ConfigEditor.Run(["zcode-hook-set", config, command]));

        var root = Read(config);
        Assert.True(root["hooks"]!["enabled"]!.GetValue<bool>());
        var matchers = root["hooks"]!["events"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, matchers.Count);
        Assert.Equal("other-tool", matchers[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("Bash|Write|Edit|MultiEdit|NotebookEdit", matchers[1]!["matcher"]!.GetValue<string>());
        Assert.Equal(command, matchers[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(15, matchers[1]!["hooks"]![0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void Removing_the_hook_keeps_a_foreign_one_and_drops_the_events_entry()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");
        File.WriteAllText(config, """
            { "hooks": { "events": { "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "command": "keep" } ] } ] } } }
            """);

        ConfigEditor.Run(["zcode-hook-set", config, "cmd --hook pre-tool-use"]);
        Assert.Equal(0, ConfigEditor.Run(["zcode-hook-remove", config]));

        var root = Read(config);
        var matchers = root["hooks"]!["events"]!["PreToolUse"]!.AsArray();
        Assert.Single(matchers);
        Assert.Equal("keep", matchers[0]!["hooks"]![0]!["command"]!.GetValue<string>());

        // With nothing foreign left the event entry goes; `enabled` — set when the hook was added —
        // stays, exactly as the Python editor this replaces left it.
        using var empty = new Folder();
        var only = empty.PathOf("config.json");
        ConfigEditor.Run(["zcode-hook-set", only, "cmd --hook pre-tool-use"]);
        Assert.Equal(0, ConfigEditor.Run(["zcode-hook-remove", only]));
        var emptied = Read(only);
        Assert.Null(emptied["hooks"]!["events"]);
        Assert.True(emptied["hooks"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void The_claude_shape_is_hooks_PreToolUse_without_an_events_level()
    {
        using var folder = new Folder();
        var config = folder.PathOf("settings.json");

        var command = "\"/opt/vmcp/VisualMCP\" --hook pre-tool-use";
        Assert.Equal(0, ConfigEditor.Run(["claude-hook-set", config, command]));

        var root = Read(config);
        var matchers = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(matchers);
        Assert.Equal(command, matchers[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Null(root["hooks"]!["events"]);

        Assert.Equal(0, ConfigEditor.Run(["claude-hook-remove", config]));
        Assert.Null(Read(config)["hooks"]);
    }

    [Fact]
    public void The_agents_block_is_written_once_and_removed_without_touching_the_rest()
    {
        using var folder = new Folder();
        var agents = folder.PathOf("AGENTS.md");
        var source = folder.PathOf("agents-block.md");
        File.WriteAllText(source, "<!-- VisualMCP:begin -->\nBlock\n<!-- VisualMCP:end -->\n");
        File.WriteAllText(agents, "# Mine\n\nSome text.\n");

        Assert.Equal(0, ConfigEditor.Run(["agents-block-set", agents, source]));
        Assert.Equal(0, ConfigEditor.Run(["agents-block-set", agents, source]));

        var text = File.ReadAllText(agents);
        Assert.Equal(1, Occurrences(text, "<!-- VisualMCP:begin"));
        Assert.Contains("# Mine", text);
        Assert.Contains("Block", text);

        Assert.Equal(0, ConfigEditor.Run(["agents-block-remove", agents]));
        text = File.ReadAllText(agents);
        Assert.DoesNotContain("VisualMCP:begin", text);
        Assert.Contains("Some text.", text);

        // A file that held nothing but the block is deleted.
        var only = folder.PathOf("Only.md");
        ConfigEditor.Run(["agents-block-set", only, source]);
        ConfigEditor.Run(["agents-block-remove", only]);
        Assert.False(File.Exists(only));
    }

    [Fact]
    public void The_command_line_dispatches_config_and_leaves_a_server_call_alone()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");

        Assert.True(InstallCommandLine.TryRun(
            ["--config", "zcode-set", config, "/opt/vmcp/VisualMCP", ""], out var exitCode));
        Assert.Equal(0, exitCode);
        Assert.Equal("/opt/vmcp/VisualMCP",
            Read(config)["mcp"]!["servers"]!["VisualMCP"]!["command"]!.GetValue<string>());

        // A normal server start is not one of the install modes.
        Assert.False(InstallCommandLine.TryRun([], out _));
        Assert.False(InstallCommandLine.TryRun(["--help"], out _));

        // A mode without its argument is a usage error, never a server start (which would read stdin):
        // this is what the installer's capability probe relies on.
        Assert.True(InstallCommandLine.TryRun(["--config"], out var noMode));
        Assert.Equal(2, noMode);
        Assert.True(InstallCommandLine.TryRun(["--hook"], out var noHook));
        Assert.Equal(2, noHook);
    }

    [Fact]
    public void An_unknown_mode_is_refused_without_writing()
    {
        using var folder = new Folder();
        var config = folder.PathOf("config.json");

        Assert.Equal(2, ConfigEditor.Run(["nonsense", config]));
        Assert.False(File.Exists(config));
    }

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>A temp folder for one test's configuration files.</summary>
    private sealed class Folder : IDisposable
    {
        public Folder()
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathOf(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
