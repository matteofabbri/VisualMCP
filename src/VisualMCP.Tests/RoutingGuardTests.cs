using System.Text;
using System.Text.Json;
using VisualMCP.Install;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The routing guard as the server binary runs it (<c>--hook pre-tool-use</c>): a read-only shell probe
/// on a .NET repository is denied with the tool that replaces it, a write where the result does not
/// belong is denied with the board, and everything else — a build, another tree, the escape hatch —
/// passes. The same decisions the POSIX hook made in Python, now as one tested implementation.
/// </summary>
public sealed class RoutingGuardTests
{
    [Theory]
    [InlineData("grep -rn Foo src", "search_text")]
    [InlineData("rg --files src", "search_text")]
    [InlineData("sed -n '1,40p' File.cs", "read_file")]
    [InlineData("cat File.cs", "read_file")]
    [InlineData("ls -la src", "list_directory")]
    [InlineData("find . -name '*.cs'", "list_directory")]
    public void A_read_only_shell_probe_in_a_dotnet_repository_is_denied_with_the_tool(
        string command, string expected)
    {
        using var repository = new Repository();

        var reason = RoutingGuard.Decide("Bash", command, null, repository.Root);

        Assert.NotNull(reason);
        Assert.Contains(expected, reason);
        Assert.Contains("vmcp-allow", reason);
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("git status")]
    [InlineData("dotnet test | tail -3")]
    [InlineData("npm run build")]
    [InlineData("cargo build --release")]
    public void A_tooling_command_passes(string command)
    {
        using var repository = new Repository();

        Assert.Null(RoutingGuard.Decide("Bash", command, null, repository.Root));
    }

    [Fact]
    public void The_same_probe_outside_a_dotnet_repository_passes()
    {
        using var repository = new Repository(solution: false);

        Assert.Null(RoutingGuard.Decide("Bash", "grep -rn Foo .", null, repository.Root));
    }

    [Fact]
    public void The_escape_hatch_passes_a_probe_it_would_otherwise_deny()
    {
        using var repository = new Repository();

        Assert.Null(RoutingGuard.Decide("Bash", "grep -rn Foo src # vmcp-allow", null, repository.Root));
    }

    [Theory]
    [InlineData("sed -i 's/a/b/' File.cs")]
    [InlineData("cat > Out.cs")]
    [InlineData("printf 'x' > Out.cs")]
    [InlineData("python -c \"open('f','w').write('x')\"")]
    [InlineData("python - <<'PY'\nopen('f','w').write('x')\nPY")]
    public void A_shell_rewrite_is_denied_in_favour_of_the_harness_edit(string command)
    {
        using var repository = new Repository();

        var reason = RoutingGuard.Decide("Bash", command, null, repository.Root);

        Assert.NotNull(reason);
        Assert.Contains("harness tool", reason);
    }

    [Fact]
    public void A_new_plan_markdown_at_the_repository_root_is_denied()
    {
        using var repository = new Repository();

        var reason = RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "notes.md"), repository.Root);

        Assert.NotNull(reason);
        Assert.Contains("repository root", reason);
        Assert.Contains("board", reason);
    }

    [Fact]
    public void The_published_documents_at_the_root_pass()
    {
        using var repository = new Repository();

        Assert.Null(RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "README.md"), repository.Root));
        Assert.Null(RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "CONTRIBUTING.md"), repository.Root));
    }

    [Fact]
    public void A_plan_like_markdown_in_docs_is_denied_and_an_existing_one_is_editable()
    {
        using var repository = new Repository();
        Directory.CreateDirectory(Path.Combine(repository.Root, "docs"));
        File.WriteAllText(Path.Combine(repository.Root, "docs", "gaps.md"), "# gaps");

        var reason = RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "docs", "plan.md"), repository.Root);
        Assert.NotNull(reason);
        Assert.Contains("plan", reason);

        // Editing a document that already exists is never refused.
        Assert.Null(RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "docs", "gaps.md"), repository.Root));

        // A document that is not a plan is fine, and another folder is never inspected.
        Assert.Null(RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "docs", "metacode.md"), repository.Root));
        Assert.Null(RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "src", "notes.md"), repository.Root));
    }

    [Fact]
    public void A_memory_store_write_is_denied_wherever_it_is()
    {
        using var repository = new Repository();

        var memory = RoutingGuard.Decide("Write", null, Path.Combine(repository.Root, "src", "MEMORY.md"), repository.Root);
        Assert.NotNull(memory);
        Assert.Contains("board_remember", memory);

        var store = RoutingGuard.Decide(
            "Edit", null, "/home/me/.claude/projects/x/memory/note.md", repository.Root);
        Assert.NotNull(store);
        Assert.Contains("board_recall", store);
    }

    [Fact]
    public async Task The_hook_reads_the_payload_and_writes_the_three_key_decision()
    {
        using var repository = new Repository();
        var payload = JsonSerializer.Serialize(new
        {
            tool_name = "Bash",
            tool_input = new { command = "grep -rn Foo src" },
            cwd = repository.Root,
        });

        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var stdout = new StringWriter();
        var exitCode = await RoutingGuard.RunHookAsync(stdin, stdout);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString());
        var output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains("search_text", output.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal(3, output.EnumerateObject().Count());
    }

    [Fact]
    public async Task An_unparsable_payload_passes_without_output()
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("not a payload"));
        var stdout = new StringWriter();

        Assert.Equal(0, await RoutingGuard.RunHookAsync(stdin, stdout));
        Assert.Equal(string.Empty, stdout.ToString());

        using var empty = new MemoryStream(Array.Empty<byte>());
        Assert.Equal(0, await RoutingGuard.RunHookAsync(empty, stdout));
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>A temp directory, with a .sln in it unless the case is about another tree.</summary>
    private sealed class Repository : IDisposable
    {
        public Repository(bool solution = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (solution) File.WriteAllText(Path.Combine(Root, "Probe.sln"), "Microsoft Visual Studio Solution File");
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
