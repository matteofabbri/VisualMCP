using System.Diagnostics;
using Snitch;
using Snitch.Git;

namespace VisualMCP.Tests;

/// <summary>
/// A throwaway git repository in a temp folder, used by the git tests. git is a real dependency here:
/// a missing git is a genuine failure, not a skipped test.
/// </summary>
internal sealed class TestGitRepo : IDisposable
{
    public string Directory { get; }
    public GitService Service { get; }
    public FakeWorkspace Workspace { get; }

    public TestGitRepo()
    {
        Directory = Path.Combine(Path.GetTempPath(), "vmcp-git-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        Git("init", "-q");
        Git("config", "user.email", "tests@example.com");
        Git("config", "user.name", "VisualMCP Tests");
        Git("checkout", "-q", "-b", "main");

        var options = new SnitchOptions { ApplicationName = "vmcptests" };
        Workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        Service = new GitService(options, Workspace);
    }

    public void Write(string name, string content)
    {
        var path = Path.Combine(Directory, name);
        var folder = Path.GetDirectoryName(path);
        if (folder is not null) System.IO.Directory.CreateDirectory(folder);
        File.WriteAllText(path, content);
    }

    public string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    public string Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
        return Capture("rev-parse", "HEAD").Trim();
    }

    public string Capture(params string[] args) => Run(args).stdout;

    /// <summary>Runs git without failing the test — for the commands that are expected to fail.</summary>
    public (int exitCode, string stdout, string stderr) Run(params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private void Git(params string[] args)
    {
        var (exitCode, stdout, stderr) = Run(args);
        if (exitCode != 0)
            throw new Xunit.Sdk.XunitException($"git {string.Join(' ', args)} failed ({exitCode}): {stderr}{stdout}");
    }

    public void Dispose()
    {
        Workspace.Dispose();
        // git marks its objects read-only on Windows, so a plain recursive delete can fail; the
        // temp folder is disposable either way.
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (Exception) { /* best effort */ }
    }
}
