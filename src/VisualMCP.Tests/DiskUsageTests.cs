using System.Text.Json;
using Snitch.Execution;
using Snitch.Platform;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The disk-policy collection runs alone: the guard-apply test changes TEMP for the process, and a test
/// collection running next to it would start writing its own temp files under a folder this one deletes.
/// </summary>
[CollectionDefinition("disk-policy", DisableParallelization = true)]
public sealed class DiskPolicyCollectionDefinition
{
}

[Collection("disk-policy")]
public sealed class DiskUsageTests
{
    [Fact]
    public void Policy_BuildsEveryManagedVariableUnderTheRoot()
    {
        var root = Scratch("policy");
        try
        {
            var state = DiskPolicy.Build(root);

            Assert.True(state.Enabled);
            Assert.Equal(Path.GetFullPath(root), state.Root);
            Assert.Equal(Path.Combine(root, "temp"), state.Variables["TEMP"]);
            Assert.Equal(Path.Combine(root, "temp"), state.Variables["TMP"]);
            Assert.Equal(Path.Combine(root, "temp"), state.Variables[DiskPolicy.TempVariable]);
            Assert.Equal(Path.Combine(root, "nuget-packages"), state.Variables["NUGET_PACKAGES"]);
            Assert.Equal(Path.Combine(root, "nuget-http-cache"), state.Variables["NUGET_HTTP_CACHE_PATH"]);
            Assert.Equal(Path.Combine(root, "dotnet-home"), state.Variables["DOTNET_CLI_HOME"]);
            Assert.Equal(Path.Combine(root, "dotnet-bundle"), state.Variables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Policy_SavesAndLoadsTheSameVariables()
    {
        var workspace = Scratch("policy-save");
        try
        {
            var state = DiskPolicy.Build(Path.Combine(workspace, "redirect"));

            var path = DiskPolicy.Save(workspace, state);
            Assert.Equal(Path.Combine(workspace, ".visualMCP", "disk-policy.json"), path);
            Assert.True(File.Exists(path));

            var loaded = DiskPolicy.TryLoadFromFile(path, out var error);
            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Enabled);
            Assert.Equal(state.Root, loaded.Root);
            Assert.Equal(state.Variables["NUGET_PACKAGES"], loaded.Variables["NUGET_PACKAGES"]);
            Assert.Equal(state.Variables["TEMP"], loaded.Variables["TEMP"]);
            Assert.Equal(Path.GetFullPath(path), loaded.PolicyPath);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public void Policy_RejectsAVariablesThatIsNotAnAbsolutePath()
    {
        var workspace = Scratch("policy-bad");
        try
        {
            var path = Path.Combine(workspace, ".visualMCP", "disk-policy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "enabled": true,
                  "root": "D:\\vmcp-tmp",
                  "variables": { "TEMP": "relative\\temp", "NUGET_PACKAGES": "D:\\vmcp-tmp\\nuget-packages" }
                }
                """);

            Assert.Null(DiskPolicy.TryLoadFromFile(path, out var error));
            Assert.Contains("TEMP", error);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task Report_MeasuresTheRootsItIsGiven()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var root = Path.Combine(workspace.RootDirectory, "cache");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "package.bin"), new byte[4096]);

        var service = new DiskUsageService(workspace);
        var report = ResultAssert.Json(await service.RunAsync(
            action: "report",
            roots: [root],
            includeWellKnown: false,
            timeoutSeconds: 30));

        Assert.Equal("report", report.GetProperty("action").GetString());
        Assert.True(report.GetProperty("drives").GetArrayLength() >= 1);
        Assert.Contains(report.GetProperty("mechanism").EnumerateArray(),
            note => note.GetString()!.Contains("NUGET_PACKAGES", StringComparison.Ordinal));

        var folder = report.GetProperty("folders").EnumerateArray()
            .Single(candidate => candidate.GetProperty("path").GetString() == Path.GetFullPath(root));
        Assert.True(folder.GetProperty("bytes").GetInt64() >= 4096);
        Assert.Equal("custom", folder.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, folder.GetProperty("cleanTarget").ValueKind);
    }

    [Fact]
    public async Task Clean_DeletesOnlyStaleEntriesAndKeepsFreshOnes()
    {
        var root = Scratch("clean");
        try
        {
            var staleDirectory = Path.Combine(root, "vmcp-old-run");
            var freshDirectory = Path.Combine(root, "keep-me");
            Directory.CreateDirectory(staleDirectory);
            Directory.CreateDirectory(freshDirectory);
            File.WriteAllBytes(Path.Combine(staleDirectory, "trace.nettrace"), new byte[4096]);
            File.WriteAllText(Path.Combine(freshDirectory, "note.txt"), "fresh");
            Directory.SetLastWriteTimeUtc(staleDirectory, DateTime.UtcNow.AddDays(-3));

            var staleFile = Path.Combine(root, "vmcp-stale.log");
            File.WriteAllText(staleFile, "stale");
            File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-3));

            using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
            var service = new DiskUsageService(workspace);

            var result = ResultAssert.Json(await service.RunAsync(
                action: "clean",
                targets: ["temp-stale"],
                root: root,
                olderThanHours: 24,
                dryRun: false,
                confirm: true,
                includeWellKnown: false,
                timeoutSeconds: 30));

            Assert.True(result.GetProperty("totalFreedBytes").GetInt64() >= 4096 + 5);
            Assert.False(Directory.Exists(staleDirectory));
            Assert.False(File.Exists(staleFile));
            Assert.True(Directory.Exists(freshDirectory));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Clean_RefusesARealDeletionWithoutConfirm()
    {
        var root = Scratch("clean-refused");
        try
        {
            using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
            var service = new DiskUsageService(workspace);

            var result = await service.RunAsync(
                action: "clean",
                targets: ["temp-stale"],
                root: root,
                dryRun: false,
                includeWellKnown: false);

            Assert.Contains("confirm", ResultAssert.Error(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Guard_CheckChangesNothing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var root = Path.Combine(Path.GetTempPath(), $"vmcp-disk-check-{Guid.NewGuid():N}");
        var service = new DiskUsageService(workspace);

        var tempBefore = Path.GetTempPath();
        var tempVariableBefore = Environment.GetEnvironmentVariable("TEMP");

        var result = ResultAssert.Json(await service.RunAsync(
            action: "guard", root: root, apply: false, includeWellKnown: false));

        Assert.False(result.GetProperty("applied").GetBoolean());
        Assert.Equal(tempBefore, Path.GetTempPath());
        Assert.Equal(tempVariableBefore, Environment.GetEnvironmentVariable("TEMP"));
        Assert.Equal(Path.Combine(root, "temp"),
            result.GetProperty("variables").GetProperty("TEMP").GetString());
    }

    [Fact]
    public async Task Guard_ApplyRedirectsTempAndPersistsThePolicy()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var root = Path.Combine(Path.GetTempPath(), $"vmcp-disk-redirect-{Guid.NewGuid():N}");
        var service = new DiskUsageService(workspace);

        var environmentBefore = DiskPolicy.ManagedVariables.ToDictionary(
            name => name, name => Environment.GetEnvironmentVariable(name));
        var stateBefore = DiskPolicy.Current;

        try
        {
            var result = ResultAssert.Json(await service.RunAsync(
                action: "guard", root: root, apply: true, includeWellKnown: false));

            Assert.True(result.GetProperty("applied").GetBoolean());
            Assert.StartsWith(Path.GetFullPath(root), Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

            var policyPath = result.GetProperty("policyPath").GetString();
            Assert.NotNull(policyPath);
            Assert.True(File.Exists(policyPath));

            var loaded = DiskPolicy.TryLoadFromFile(policyPath!, out var error);
            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal(Path.GetFullPath(root), loaded!.Root);

            // A policy-root clean removes leftovers next to the redirected folders, never the folders themselves.
            var leftover = Path.Combine(root, "vmcp-earlier-session");
            Directory.CreateDirectory(leftover);
            File.WriteAllText(Path.Combine(leftover, "trace.log"), "old");
            Directory.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddDays(-3));

            var cleanup = ResultAssert.Json(await service.RunAsync(
                action: "clean",
                targets: ["policy-root"],
                olderThanHours: 24,
                dryRun: false,
                confirm: true,
                includeWellKnown: false));

            Assert.True(cleanup.GetProperty("totalFreedBytes").GetInt64() >= 3);
            Assert.False(Directory.Exists(leftover));
            Assert.True(Directory.Exists(Path.Combine(root, "temp")));
            Assert.True(Directory.Exists(Path.Combine(root, "nuget-packages")));
        }
        finally
        {
            foreach (var (name, value) in environmentBefore)
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

            DiskPolicy.Set(stateBefore);
            Cleanup(root);
        }
    }

    private static string Scratch(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vmcp-disk-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover scratch folder is not a test failure.
        }
    }
}
