using System.IO.Compression;
using System.Text;
using Snitch.Metacode;
using Snitch.Metacode.Coding;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Indexing;
using Snitch.Platform;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The JADX half of decompilation: the release feed the installer reads, the platform launcher it looks
/// for, the bytecode detector that routes a file to it, and a full metacoding run — JADX replaced by a
/// recorded result — that lands the recovered Java in the same AST as everything else.
/// </summary>
public sealed class JadxTests
{
    private const string JavaSource = """
        package com.example;

        public class Greeter {
            private final String name;

            public Greeter(String name) {
                this.name = name;
            }

            public String greet() {
                if (this.name == null) {
                    return "hello";
                }
                return "hello " + this.name;
            }

            public int score(int[] values) {
                int total = 0;
                for (int value : values) {
                    total += value;
                }
                return total;
            }
        }
        """;

    [Fact]
    public void The_release_feed_selects_the_cross_platform_cli_bundle()
    {
        const string release = """
            {
              "assets": [
                { "name": "jadx-gui-1.5.6-win.zip", "browser_download_url": "https://example/gui.zip" },
                { "name": "jadx-gui-1.5.6-with-jre-win.zip", "browser_download_url": "https://example/gui-jre.zip" },
                { "name": "jadx-1.5.5.zip", "browser_download_url": "https://example/1.5.5.zip" },
                { "name": "jadx-1.5.6.zip", "browser_download_url": "https://example/1.5.6.zip" }
              ]
            }
            """;

        var asset = JadxInstaller.SelectAsset(release);

        Assert.NotNull(asset);
        Assert.Equal("jadx-1.5.6.zip", asset!.Value.Name);
        Assert.Equal("https://example/1.5.6.zip", asset.Value.Url);
        Assert.Equal("1.5.6", asset.Value.Version);
    }

    [Fact]
    public void The_installer_reports_an_existing_jadx_and_uses_the_platform_launcher()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var home = Path.Combine(workspace.RootDirectory, "jadx-1.5.6");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        File.WriteAllText(Path.Combine(home, "bin", JadxInstaller.LauncherName), "#!/bin/sh\n");

        var options = new JadxOptions { JadxHome = home, ToolsDirectory = workspace.RootDirectory, AutoInstall = false };
        var status = new JadxInstaller().Status(options);

        Assert.True(status.Installed, status.Error);
        Assert.Equal("1.5.6", status.Version);
        Assert.Equal("jadxHome", status.Source);
        Assert.Equal(Path.Combine(home, "bin", JadxInstaller.LauncherName), status.CliPath);
        Assert.Equal(HostSystem.IsWindows ? "jadx.bat" : "jadx", Path.GetFileName(status.CliPath!));
    }

    [Fact]
    public void The_installer_finds_a_launcher_the_archive_nested_one_folder_deep()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var tools = Path.Combine(workspace.RootDirectory, "tools");
        var home = Path.Combine(tools, "jadx-1.5.6");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", JadxInstaller.LauncherName), "#!/bin/sh\n");

        // No JadxHome: the tools folder is what the resolver walks, exactly as after an install.
        var status = new JadxInstaller().Status(new JadxOptions { ToolsDirectory = tools, AutoInstall = false });

        Assert.True(status.Installed, status.Error);
        Assert.Equal("tools", status.Source);
        Assert.Equal("1.5.6", status.Version);
    }

    [Fact]
    public void The_detector_knows_class_dex_and_archives_of_classes()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var root = workspace.RootDirectory;

        var classFile = Path.Combine(root, "Hello.class");
        File.WriteAllBytes(classFile, [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34]);
        Assert.True(JavaArchiveDetector.IsJavaBinary(classFile));

        var dexFile = Path.Combine(root, "classes.dex");
        File.WriteAllBytes(dexFile, Encoding.ASCII.GetBytes("dex\n035\0"));
        Assert.True(JavaArchiveDetector.IsJavaBinary(dexFile));

        var jar = Path.Combine(root, "app.jar");
        CreateArchive(jar, "META-INF/MANIFEST.MF", "com/example/Hello.class");
        Assert.True(JavaArchiveDetector.IsJavaBinary(jar));

        // A zip is not Java by its extension: only its content decides.
        var plainZip = Path.Combine(root, "notes.zip");
        CreateArchive(plainZip, "notes.txt", "readme.txt");
        Assert.False(JavaArchiveDetector.IsJavaBinary(plainZip));

        Assert.False(JavaArchiveDetector.IsJavaBinary(Path.Combine(root, "C.cs")));
    }

    [Fact]
    public async Task A_java_archive_is_routed_to_jadx_and_stored_under_the_java_scheme()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        using var index = new MetacodeIndexService(workspace, store);

        var archive = Path.Combine(workspace.RootDirectory, "fake.jar");
        File.WriteAllBytes(archive, [(byte)'P', (byte)'K', 3, 4, 0, 0]);

        var source = Path.Combine(workspace.RootDirectory, "Greeter.java");
        await File.WriteAllTextAsync(source, JavaSource);

        var analysis = new JadxAnalysis(
            archive,
            workspace.RootDirectory,
            new[] { new JadxSourceFile(source, "com/example/Greeter.java", "Greeter") },
            Path.Combine(workspace.RootDirectory, "jadx.log"),
            "fake-jadx",
            null,
            TimeSpan.FromMilliseconds(120),
            ExitCode: 0,
            TimedOut: false);

        var coding = new MetacodeCodingService(
            workspace, index, new DecompileService(), new GhidraService(), new FakeJadx(analysis),
            new NativeCodeStore(store));

        var result = await coding.DecompileAsync(archive, siblings: false);

        Assert.False(result.IsError, result.Error);
        Assert.True(result.Written, result.Summary);
        Assert.Contains("Java bytecode", result.Summary);
        Assert.Contains("java://", result.Summary);

        var decompiled = Assert.Single(index.DecompiledAssemblies());
        Assert.Equal("fake.jar", decompiled.Assembly);
        Assert.Equal("java", decompiled.Kind);
        Assert.Equal(NativeCodeStore.HashOf(archive), decompiled.Mvid);
        Assert.Equal(1, decompiled.Types);

        var greet = index.Trees("greet");
        Assert.Contains(greet, row => row.Path == "java://fake.jar/com/example/Greeter.java");
        Assert.Contains("METHOD greet", greet[0].Text);

        // A second run is the store's answer, not JADX's: same SHA-256, nothing decompiled again.
        var again = await coding.DecompileAsync(archive, siblings: false);
        Assert.False(again.IsError, again.Error);
        Assert.False(again.Written);
        Assert.Contains("already stored", again.Summary);
    }

    private static void CreateArchive(string path, params string[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("content");
        }
    }

    /// <summary>A JADX that replays a recorded run: no external process, no JDK, no network.</summary>
    private sealed class FakeJadx : IJadxService
    {
        private readonly JadxAnalysis _analysis;

        public FakeJadx(JadxAnalysis analysis) => _analysis = analysis;

        public string? CliPath => "fake-jadx";

        public bool IsAvailable => true;

        public Task<JadxAnalysis> DecompileAsync(
            string archivePath, JadxOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_analysis with { ArchivePath = archivePath });
    }
}
