using Snitch.Metacode;
using Snitch.Metacode.Coding;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Indexing;
using Snitch.Platform;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The native side of the store: a Ghidra run becomes rows in native_artifact / native_function /
/// native_call, keyed on the binary's SHA-256, and metacoding sends a non-managed file there instead of
/// failing it in ILSpy. Ghidra is never executed here: the snapshot is the recorded one, replayed.
/// </summary>
public sealed class NativeCodeStoreTests
{
    private const string SnapshotJson = """
        {
         "image": "version.dll",
         "language": "x86:LE:64:default",
         "entry": "1800010f0",
         "imageBase": "180000000",
         "functions": [
          {
           "entry": "180001124",
           "name": "FUN_180001124",
           "signature": "undefined8 __fastcall FUN_180001124(undefined8 param_1, int param_2)",
           "size": 124,
           "decompiled": "\r\nundefined8 FUN_180001124(undefined8 param_1,int param_2)\r\n\r\n{\r\n  if (param_2 == 0) {\r\n    if (DAT_180007090 != 0xffffffff) {\r\n      TlsFree(DAT_180007090);\r\n    }\r\n    if (DAT_180007628 != (HMODULE)0x0) {\r\n      FreeLibrary(DAT_180007628);\r\n    }\r\n  }\r\n  else {\r\n    if (param_2 == 1) {\r\n      DAT_180007090 = TlsAlloc();\r\n    }\r\n    else if (param_2 != 2) {\r\n      return 1;\r\n    }\r\n    if (DAT_180007090 != 0xffffffff) {\r\n      TlsSetValue(DAT_180007090,(LPVOID)0x0);\r\n    }\r\n  }\r\n  return 1;\r\n}\r\n\r\n",
           "calls": [ "EXTERNAL:00000029", "EXTERNAL:00000028", "EXTERNAL:00000021", "EXTERNAL:00000023" ]
          },
          {
           "entry": "1800011b0",
           "name": "FUN_1800011b0",
           "signature": "bool __fastcall FUN_1800011b0(void)",
           "size": 52,
           "decompiled": "\r\nbool FUN_1800011b0(void)\r\n\r\n{\r\n  bool bVar1;\r\n  \r\n  DAT_180007658 = malloc(0x100);\r\n  bVar1 = DAT_180007658 != (undefined8 *)0x0;\r\n  DAT_180007660 = DAT_180007658;\r\n  if (bVar1) {\r\n    *DAT_180007658 = 0;\r\n  }\r\n  return !bVar1;\r\n}\r\n\r\n",
           "calls": [ "EXTERNAL:00000006" ]
          }
         ]
        }
        """;

    private static (FakeWorkspace Workspace, VisualMcpStore Store, NativeCodeStore Native, string Binary) Create()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var store = new VisualMcpStore(workspace);
        var native = new NativeCodeStore(store);

        var binary = Path.Combine(workspace.RootDirectory, "version.dll");
        File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03]);
        return (workspace, store, native, binary);
    }

    private static GhidraAnalysis Analysis(string binary, string? headless = null) =>
        new(
            binary,
            GhidraSnapshot.Parse(SnapshotJson) with { BinaryPath = binary },
            Path.Combine(Path.GetTempPath(), "vmcp-functions.json"),
            null,
            null,
            headless,
            null,
            TimeSpan.FromSeconds(2),
            ExitCode: 0,
            TimedOut: false);

    private static string HeadlessOf(FakeWorkspace workspace) =>
        Path.Combine(workspace.RootDirectory, "ghidra_12.2_PUBLIC", "support", GhidraInstaller.LauncherName);

    [Fact]
    public void A_native_run_is_stored_in_the_dedicated_tables()
    {
        var (workspace, store, native, binary) = Create();
        using (workspace)
        using (store)
        using (native)
        {
            var artifact = native.Save(Analysis(binary, HeadlessOf(workspace)));

            Assert.True(artifact.Stored, artifact.Error);
            Assert.Null(artifact.Error);
            Assert.Equal(2, artifact.Functions);
            Assert.Equal(NativeCodeStore.HashOf(binary), artifact.Sha256);
            Assert.Equal("12.2", artifact.Ghidra);
            Assert.Equal("x86:LE:64:default", artifact.Language);
            Assert.Equal("1800010f0", artifact.Entry);
            Assert.Equal(Path.Combine(workspace.RootDirectory, ".visualMCP", "visualmcp.db"), artifact.Database);

            Assert.Equal(2, native.FunctionCount(artifact.Id));

            var first = native.Function(artifact.Id, "FUN_180001124");
            Assert.NotNull(first);
            Assert.Equal("180001124", first!.Entry);
            Assert.Contains("TlsFree", first.Decompiled);
            Assert.Contains("FUNCTION FUN_180001124", first.Pseudocode);
            Assert.Contains("IF param_2 == 0 THEN", first.Pseudocode);
            Assert.True(first.Cyclomatic > 1, $"cyclomatic was {first.Cyclomatic}");

            // The call edges are the graph Ghidra saw, stored on their own.
            Assert.Equal(5, native.Calls(artifact.Id).Count);
        }
    }

    [Fact]
    public void A_second_save_reuses_the_row_keyed_on_the_sha()
    {
        var (workspace, store, native, binary) = Create();
        using (workspace)
        using (store)
        using (native)
        {
            var first = native.Save(Analysis(binary, HeadlessOf(workspace)));
            var second = native.Save(Analysis(binary, HeadlessOf(workspace)));

            Assert.True(first.Stored);
            Assert.False(second.Stored);
            Assert.Null(second.Error);
            Assert.Equal(first.Id, second.Id);
            Assert.Single(native.Artifacts());
            Assert.Equal(2, native.FunctionCount(first.Id));
        }
    }

    [Fact]
    public void A_forced_save_replaces_the_functions_of_the_same_binary()
    {
        var (workspace, store, native, binary) = Create();
        using (workspace)
        using (store)
        using (native)
        {
            var first = native.Save(Analysis(binary, HeadlessOf(workspace)));
            var replaced = native.Save(Analysis(binary, HeadlessOf(workspace)), force: true);

            Assert.True(replaced.Stored);
            Assert.Equal(first.Id, replaced.Id);
            Assert.Single(native.Artifacts());
            Assert.Equal(2, native.FunctionCount(first.Id));
        }
    }

    [Fact]
    public void The_native_store_finds_a_function_by_name_or_address()
    {
        var (workspace, store, native, binary) = Create();
        using (workspace)
        using (store)
        using (native)
        {
            var artifact = native.Save(Analysis(binary, HeadlessOf(workspace)));

            Assert.Equal("FUN_1800011b0", native.Function(artifact.Id, "fun_1800011b0")?.Name);
            Assert.Equal("FUN_1800011b0", native.Function(artifact.Id, "0x1800011b0")?.Name);
            Assert.Equal("FUN_1800011b0", native.Function(artifact.Id, "1800011b0")?.Name);
            Assert.Null(native.Function(artifact.Id, "no-such-function"));

            var filtered = native.Functions(artifact.Id, filter: "180001124");
            Assert.Single(filtered);
            Assert.Equal("FUN_180001124", filtered[0].Name);
        }
    }

    [Fact]
    public async Task A_non_managed_binary_is_routed_to_ghidra_by_metacoding()
    {
        var (workspace, store, native, binary) = Create();
        using (workspace)
        using (store)
        using (native)
        {
            var index = new MetacodeIndexService(workspace, store);
            var coding = new MetacodeCodingService(
                workspace, index, new DecompileService(),
                new FakeGhidra(Analysis(binary, HeadlessOf(workspace))), new JadxService(), native);
            using (index)
            {
                var result = await coding.DecompileAsync(binary);

                Assert.False(result.IsError, result.Error);
                Assert.True(result.Written, result.Summary);
                Assert.Contains("not a managed assembly", result.Summary);
                Assert.Contains("native_artifact", result.Summary);
                Assert.Contains("native_function", result.Summary);
                Assert.Contains("native_call", result.Summary);

                var artifact = Assert.Single(native.Artifacts());
                Assert.Equal(2, native.FunctionCount(artifact.Id));
            }
        }
    }

    [Fact]
    public async Task The_installer_reports_an_existing_ghidra_and_uses_the_platform_launcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-ghidra-status-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "ghidra_12.2_PUBLIC");
        Directory.CreateDirectory(Path.Combine(home, "support"));
        File.WriteAllText(Path.Combine(home, "support", GhidraInstaller.LauncherName), "#!/bin/sh\n");

        try
        {
            var options = new GhidraOptions { GhidraHome = home, ToolsDirectory = root, AutoInstall = false };
            var installer = new GhidraInstaller();

            var status = installer.Status(options);
            Assert.True(status.Installed, status.Error);
            Assert.Equal("12.2", status.Version);
            Assert.Equal("ghidraHome", status.Source);
            Assert.Equal(Path.Combine(home, "support", GhidraInstaller.LauncherName), status.HeadlessPath);
            Assert.Equal(HostSystem.IsWindows ? "analyzeHeadless.bat" : "analyzeHeadless", Path.GetFileName(status.HeadlessPath!));

            // An existing installation is reported, not replaced: no network is touched.
            var ensured = await installer.EnsureInstalledAsync(options);
            Assert.True(ensured.Installed);
            Assert.Null(ensured.Error);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_release_feed_selects_the_highest_public_zip()
    {
        const string release = """
            {
              "assets": [
                { "name": "ghidra_11.4_PUBLIC_20250101.zip", "browser_download_url": "https://example/11.4.zip" },
                { "name": "ghidra_12.2_PUBLIC_20250814.zip", "browser_download_url": "https://example/12.2.zip" },
                { "name": "ghidra_12.2_PUBLIC_20250814.tar.gz", "browser_download_url": "https://example/12.2.tar.gz" }
              ]
            }
            """;

        var asset = GhidraInstaller.SelectAsset(release);

        Assert.NotNull(asset);
        Assert.Equal("ghidra_12.2_PUBLIC_20250814.zip", asset!.Value.Name);
        Assert.Equal("https://example/12.2.zip", asset.Value.Url);
        Assert.Equal("12.2", asset.Value.Version);
    }

    /// <summary>A Ghidra service that replays a recorded snapshot: no external process, no machine dependency.</summary>
    private sealed class FakeGhidra : IGhidraService
    {
        private readonly GhidraAnalysis _analysis;

        public FakeGhidra(GhidraAnalysis analysis) => _analysis = analysis;

        public string? HeadlessPath => "fake-analyzeHeadless";

        public bool IsAvailable => true;

        public Task<GhidraAnalysis> AnalyzeAsync(
            string binaryPath, GhidraOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_analysis with { BinaryPath = binaryPath });
    }
}
