using System.Diagnostics;
using System.Text;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The POSIX routing hook (hooks/pre-tool-use.py) is the Linux/macOS half of the guard: these tests run
/// it with real payloads and pin the decisions the PowerShell hook makes on Windows — deny a probe,
/// pass a build, pass the explicit escape hatch, deny a shell rewrite.
/// </summary>
public sealed class RoutingHookTests
{
    private static readonly string HookPath = LocateHook();

    [Fact]
    public async Task A_search_probe_inside_a_dotnet_repository_is_denied()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunAsync(repo.Directory, "grep -rn Foo .");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"permissionDecision\":\"deny\"", output);
        Assert.Contains("search_text", output);
    }

    [Fact]
    public async Task A_build_segment_passes()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunAsync(repo.Directory, "dotnet test | tail -3");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task The_escape_hatch_passes()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunAsync(repo.Directory, "grep -rn Foo . vmcp-allow");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task A_shell_rewrite_is_denied_as_a_write()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunAsync(repo.Directory, "echo hi > file.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("rewrites files through the shell", output);
        Assert.Contains("Edit", output);
    }

    [Fact]
    public async Task A_probe_outside_a_dotnet_repository_passes()
    {
        var outside = Path.Combine(Path.GetTempPath(), "vmcp-nosln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var (exitCode, output) = await RunAsync(outside, "ls -la");
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, output);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_write_into_a_memory_store_is_denied_in_favour_of_the_board()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunPayloadAsync(repo.Directory, "Write", Path.Combine(repo.Directory, "MEMORY.md"));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"permissionDecision\":\"deny\"", output);
        Assert.Contains("board_remember", output);
    }

    [Fact]
    public async Task Creating_a_plan_file_at_the_repository_root_is_denied_in_favour_of_a_card()
    {
        using var repo = new DotNetRepo();
        var (exitCode, output) = await RunPayloadAsync(repo.Directory, "Write", Path.Combine(repo.Directory, "release-plan.md"));

        Assert.Equal(0, exitCode);
        Assert.Contains("deny", output);
        Assert.Contains("create_task_checklist", output);
    }

    [Fact]
    public async Task Editing_an_existing_document_is_not_a_plan_file()
    {
        using var repo = new DotNetRepo();
        var path = Path.Combine(repo.Directory, "docs", "gaps.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# gaps\n");

        var (exitCode, output) = await RunPayloadAsync(repo.Directory, "Edit", path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task A_write_outside_a_dotnet_repository_passes()
    {
        var outside = Path.Combine(Path.GetTempPath(), "vmcp-nowrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var (exitCode, output) = await RunPayloadAsync(outside, "Write", Path.Combine(outside, "notes.md"));
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, output);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { /* best-effort */ }
        }
    }

    private static Task<(int ExitCode, string Output)> RunAsync(string cwd, string command)
    {
        var payload = $$$"""{"tool_name":"Bash","cwd":"{{{cwd.Replace("\\", "/")}}}","tool_input":{"command":"{{{command}}}"}}""";
        return ExecuteAsync(payload);
    }

    private static Task<(int ExitCode, string Output)> RunPayloadAsync(string cwd, string tool, string path)
    {
        var payload = $$$"""{"tool_name":"{{{tool}}}","cwd":"{{{cwd.Replace("\\", "/")}}}","tool_input":{"file_path":"{{{path.Replace("\\", "/")}}}"}}""";
        return ExecuteAsync(payload);
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(string payload)
    {
        var interpreter = Interpreter();
        var start = new ProcessStartInfo(interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(HookPath);

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }

    private static string Interpreter()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (probe is null) continue;
                probe.WaitForExit(5000);
                if (probe.HasExited && probe.ExitCode == 0) return candidate;
            }
            catch
            {
                // try the next name
            }
        }

        throw new InvalidOperationException("No Python 3 interpreter found: the POSIX routing hook needs one.");
    }

    private static string LocateHook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "hooks", "pre-tool-use.py");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("hooks/pre-tool-use.py not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A temp folder with a dummy .sln, which is what the hook tests for.</summary>
    private sealed class DotNetRepo : IDisposable
    {
        public DotNetRepo()
        {
            Directory = Path.Combine(Path.GetTempPath(), "vmcp-hook-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "Probe.sln"), "Microsoft Visual Studio Solution File");
        }

        public string Directory { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, true); } catch { /* best-effort */ }
        }
    }
}
