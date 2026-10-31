using System.Diagnostics;
using Snitch;
using Snitch.Git;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// git_worktree / git_bisect / git_submodule against real repositories in temp folders:
/// the structured wrappers around the git CLI are exercised end to end, with no network.
/// </summary>
public sealed class GitWorktreeBisectSubmoduleTests
{
    private sealed class Repo : IDisposable
    {
        public string Directory { get; }
        public GitService Service { get; }

        public Repo()
        {
            Directory = Path.Combine(Path.GetTempPath(), "vmcp-gitx-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            Git("init", "-q");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "VisualMCP Tests");
            Git("checkout", "-q", "-b", "main");

            var options = new SnitchOptions { ApplicationName = "vmcptests" };
            Service = new GitService(options, FakeWorkspace.Create(("C.cs", "internal class C { }")));
        }

        public void Write(string name, string content) => File.WriteAllText(Path.Combine(Directory, name), content);

        public string Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "--allow-empty", "-m", message);
            return Capture("rev-parse", "HEAD").Trim();
        }

        public string Capture(params string[] args) => Run(args).stdout;

        private void Git(params string[] args)
        {
            var (exitCode, stdout, stderr) = Run(args);
            if (exitCode != 0)
                throw new Xunit.Sdk.XunitException($"git {string.Join(' ', args)} failed ({exitCode}): {stderr}{stdout}");
        }

        private (int exitCode, string stdout, string stderr) Run(string[] args)
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

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (Exception) { /* git marks objects read-only on Windows; best effort */ }
        }
    }

    [Fact]
    public async Task Worktree_AddsListsAndRemoves()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        var wt = Path.Combine(Path.GetTempPath(), "vmcp-wt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var added = ResultAssert.Json(await repo.Service.WorktreeAsync("add", wt, "feature", newBranch: true, force: false, repo.Directory));
            Assert.True(added.GetProperty("added").GetBoolean());
            Assert.Equal("feature", added.GetProperty("branch").GetString());

            var listed = ResultAssert.Json(await repo.Service.WorktreeAsync("list", null, null, false, false, repo.Directory));
            var worktrees = listed.GetProperty("worktrees");
            Assert.True(worktrees.GetArrayLength() >= 2, "the main tree plus the new worktree");

            var entry = worktrees.EnumerateArray()
                .First(w => string.Equals(
                    w.GetProperty("path").GetString()!.Replace('\\', '/'),
                    wt.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase)); // porcelain prints '/'-separated paths even on Windows
            Assert.Contains("feature", entry.GetProperty("branch").GetString());
            Assert.False(entry.GetProperty("detached").GetBoolean());

            var removed = ResultAssert.Json(await repo.Service.WorktreeAsync("remove", wt, null, false, false, repo.Directory));
            Assert.True(removed.GetProperty("removed").GetBoolean());
        }
        finally
        {
            try { if (System.IO.Directory.Exists(wt)) System.IO.Directory.Delete(wt, true); } catch { }
        }
    }

    [Fact]
    public async Task Bisect_StartsMarksAndFinishes()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        var c1 = repo.Commit("good one");
        repo.Write("a.txt", "2");
        var c2 = repo.Commit("the bug");
        repo.Write("a.txt", "3");
        var c3 = repo.Commit("bad too");
        repo.Capture("checkout", "-q", c3);

        var started = ResultAssert.Json(await repo.Service.BisectAsync("start", bad: "HEAD", good: c1, repo.Directory));
        Assert.True(started.GetProperty("succeeded").GetBoolean());

        // HEAD is now c2 (the middle commit); marking it bad finishes the bisection.
        var bad = ResultAssert.Json(await repo.Service.BisectAsync("bad", null, null, repo.Directory));
        Assert.True(bad.GetProperty("finished").GetBoolean());

        var log = ResultAssert.Json(await repo.Service.BisectAsync("log", null, null, repo.Directory));
        Assert.Contains("git bisect start", log.GetProperty("output").GetString());

        var reset = ResultAssert.Json(await repo.Service.BisectAsync("reset", null, null, repo.Directory));
        Assert.True(reset.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Submodule_StatusOnARepoWithoutSubmodulesIsEmpty()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        var status = ResultAssert.Json(await repo.Service.SubmoduleAsync("status", repo.Directory));
        Assert.True(status.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, status.GetProperty("count").GetInt32());
    }
}
