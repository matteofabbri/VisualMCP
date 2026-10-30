using System.Diagnostics;
using Snitch;
using Snitch.Docker;
using Snitch.Git;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The git service against real repositories in temp folders (git is available on the machine; the
/// tests skip nothing — a missing git is a genuine failure here).
/// </summary>
public sealed class GitServiceTests
{
    private sealed class Repo : IDisposable
    {
        public string Directory { get; }
        public GitService Service { get; }

        public Repo()
        {
            Directory = Path.Combine(Path.GetTempPath(), "vmcp-git-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);

            Git("init", "-q");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "VisualMCP Tests");
            Git("checkout", "-q", "-b", "main");

            var options = new SnitchOptions { ApplicationName = "vmcptests" };
            var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
            Workspace = workspace;
            Service = new GitService(options, workspace);
        }

        public FakeWorkspace Workspace { get; }

        public void Write(string name, string content) => File.WriteAllText(Path.Combine(Directory, name), content);

        public string Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-q", "--allow-empty", "-m", message);
            var output = Capture("rev-parse", "HEAD");
            return output.Trim();
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
            Workspace.Dispose();
            // git marks its objects read-only on Windows, so a plain recursive delete can fail; the
            // temp folder is disposable either way.
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (Exception) { /* best effort */ }
        }
    }

    [Fact]
    public async Task GitSquash_FoldsTheLastCommitsIntoOne()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");
        repo.Write("a.txt", "2");
        repo.Commit("second");
        repo.Write("a.txt", "3");
        repo.Commit("third");

        var result = await repo.Service.SquashAsync(3, "all three, together", repo.Directory);
        ResultAssert.Succeeded(result);

        Assert.Equal("1", repo.Capture("rev-list", "--count", "HEAD").Trim());
        Assert.Equal("all three, together", repo.Capture("log", "-1", "--pretty=%s").Trim());
        Assert.Equal("3", repo.Capture("show", "HEAD:a.txt").Trim());
    }

    [Fact]
    public async Task GitSquash_RefusesADirtyWorkingTree()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");
        repo.Write("a.txt", "2");
        repo.Commit("second");
        repo.Write("a.txt", "uncommitted");

        var error = ResultAssert.Error(await repo.Service.SquashAsync(2, "fold", repo.Directory));
        Assert.Contains("not clean", error);
    }

    [Fact]
    public async Task GitAmend_RewordsTheLastCommitAndKeepsItsContent()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "content");
        repo.Commit("typo in mesage");

        var result = await repo.Service.AmendAsync("typo in message", repo.Directory);
        ResultAssert.Succeeded(result);

        Assert.Equal("typo in message", repo.Capture("log", "-1", "--pretty=%s").Trim());
        Assert.Equal("content", repo.Capture("show", "HEAD:a.txt").Trim());
        Assert.Equal("1", repo.Capture("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task GitBranch_CreatesRenamesListsAndDeletes()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        ResultAssert.Succeeded(await repo.Service.CreateBranchAsync("feature", checkout: true, startPoint: null, repo.Directory));
        Assert.Equal("feature", repo.Capture("rev-parse", "--abbrev-ref", "HEAD").Trim());

        ResultAssert.Succeeded(await repo.Service.RenameBranchAsync("feature", "feature-renamed", repo.Directory));
        Assert.Equal("feature-renamed", repo.Capture("rev-parse", "--abbrev-ref", "HEAD").Trim());

        var branches = ResultAssert.Json(await repo.Service.BranchesAsync(repo.Directory));
        var names = branches.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToList();
        Assert.Contains("feature-renamed", names);
        Assert.Contains("main", names);
        Assert.Equal("feature-renamed", branches.GetProperty("current").GetString());

        ResultAssert.Succeeded(await repo.Service.CheckoutAsync("main", create: false, repo.Directory));
        ResultAssert.Succeeded(await repo.Service.DeleteBranchAsync("feature-renamed", force: false, repo.Directory));

        var after = ResultAssert.Json(await repo.Service.BranchesAsync(repo.Directory));
        Assert.DoesNotContain(after.GetProperty("branches").EnumerateArray(),
            b => b.GetProperty("name").GetString() == "feature-renamed");
    }

    [Fact]
    public async Task GitMerge_FastForwardsAndReportsTheResult()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        ResultAssert.Succeeded(await repo.Service.CreateBranchAsync("topic", checkout: true, startPoint: null, repo.Directory));
        repo.Write("b.txt", "from topic");
        repo.Commit("topic work");
        ResultAssert.Succeeded(await repo.Service.CheckoutAsync("main", create: false, repo.Directory));

        var merged = await repo.Service.MergeAsync("topic", "no-ff", "merge topic", abort: false, repo.Directory);
        ResultAssert.Succeeded(merged);
        Assert.True(ResultAssert.Json(merged).GetProperty("merged").GetBoolean());

        Assert.Equal("from topic", repo.Capture("show", "HEAD:b.txt").Trim());
        // --no-ff always produces a merge commit.
        Assert.Equal("merge topic", repo.Capture("log", "-1", "--pretty=%s").Trim());
    }

    [Fact]
    public async Task GitReset_HardRequiresConfirm()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");
        repo.Write("a.txt", "2");
        repo.Commit("second");

        var refusal = ResultAssert.Error(await repo.Service.ResetAsync("HEAD~1", "hard", confirm: false, repo.Directory));
        Assert.Contains("confirm=true", refusal);
        Assert.Equal("2", repo.Capture("rev-list", "--count", "HEAD").Trim());

        ResultAssert.Succeeded(await repo.Service.ResetAsync("HEAD~1", "soft", confirm: false, repo.Directory));
        Assert.Equal("1", repo.Capture("rev-list", "--count", "HEAD").Trim());
        // soft keeps the undone work staged
        Assert.Contains("a.txt", repo.Capture("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task GitStash_PushesAndPopsTheWorkingTree()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "committed");
        repo.Commit("first");
        repo.Write("a.txt", "work in progress");

        var push = await repo.Service.StashAsync("push", "wip note", null, repo.Directory);
        ResultAssert.Succeeded(push);
        Assert.Equal("committed", File.ReadAllText(Path.Combine(repo.Directory, "a.txt")));
        Assert.Single(ResultAssert.Json(push).GetProperty("entries").EnumerateArray());

        ResultAssert.Succeeded(await repo.Service.StashAsync("pop", null, null, repo.Directory));
        Assert.Equal("work in progress", File.ReadAllText(Path.Combine(repo.Directory, "a.txt")));
    }

    [Fact]
    public async Task GitTag_CreatesListsAndDeletes()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        ResultAssert.Succeeded(await repo.Service.TagAsync("create", "v1.0.0", "first release", null, repo.Directory));

        var tags = ResultAssert.Json(await repo.Service.TagAsync("list", null, null, null, repo.Directory));
        Assert.Equal(1, tags.GetProperty("count").GetInt32());
        Assert.Equal("v1.0.0", tags.GetProperty("tags")[0].GetProperty("name").GetString());

        ResultAssert.Succeeded(await repo.Service.TagAsync("delete", "v1.0.0", null, null, repo.Directory));
        var after = ResultAssert.Json(await repo.Service.TagAsync("list", null, null, null, repo.Directory));
        Assert.Equal(0, after.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GitShowAndRun_ReturnTheInspectionData()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        var show = ResultAssert.Json(await repo.Service.ShowAsync("HEAD", null, repo.Directory));
        Assert.Equal("first", show.GetProperty("body").GetString()!.Split('\n')[0].Trim());

        var run = ResultAssert.Json(await repo.Service.RunAsync("rev-parse --abbrev-ref HEAD", repo.Directory, 30));
        Assert.True(run.GetProperty("succeeded").GetBoolean());
        Assert.Equal("main", run.GetProperty("stdout").GetString()!.Trim());
    }

    [Fact]
    public async Task GitSync_ForceNeedsConfirmation()
    {
        using var repo = new Repo();
        repo.Write("a.txt", "1");
        repo.Commit("first");

        var refusal = ResultAssert.Error(await repo.Service.SyncAsync("push", "origin", "main", tags: false, force: true, confirm: false, repo.Directory));
        Assert.Contains("confirm=true", refusal);
    }

    [Fact]
    public async Task Docker_ReportsAStructuredAnswerWhetherOrNotTheEngineIsUp()
    {
        var service = new DockerService();

        var status = ResultAssert.Json(await service.RunAsync(includeStopped: true));
        if (status.GetProperty("available").GetBoolean())
        {
            Assert.True(status.TryGetProperty("containers", out _));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("reason").GetString()));
        }

        var ps = ResultAssert.Json(await service.PsAsync(all: false, filter: null));
        Assert.True(ps.TryGetProperty("containers", out _) || ps.TryGetProperty("error", out _));
    }
}
