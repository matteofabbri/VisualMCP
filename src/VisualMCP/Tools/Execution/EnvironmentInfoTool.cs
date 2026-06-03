using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class EnvironmentInfoTool
{
    [McpServerTool, Description(
        "Report the local build environment / toolchain in one call: OS and architecture, installed .NET " +
        "SDKs and runtimes (and the active 'dotnet --version'), Visual Studio installations (via vswhere), " +
        "the MSVC 'cl.exe' compiler location, and — for any file/directory paths you pass — whether they " +
        "exist and their size. Use this INSTEAD OF ad-hoc shell commands (dotnet --version, where cl, " +
        "listing Program Files, ls -la a .lib) to discover what's installed and how big a file/library is.")]
    public static async Task<object> GetEnvironmentInfo(
        [Description("Optional: file or directory paths to report existence and size for (e.g. a built .lib/.dll/.exe).")] string[]? paths = null)
    {
        var workDir = Directory.GetCurrentDirectory();

        async Task<string> Capture(string exe, string args)
        {
            try
            {
                var (_, _, stdout, stderr, _) = await ProcessRunner.RunAsync(exe, args, workDir, 30);
                var text = (stdout + stderr).Trim();
                return text.Length == 0 ? "(no output)" : text;
            }
            catch (Exception ex) { return $"(failed: {ex.Message})"; }
        }

        static string[] Lines(string s) =>
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        // ── .NET toolchain ──────────────────────────────────────────────────
        var dotnetVersion  = await Capture("dotnet", "--version");
        var dotnetSdks     = Lines(await Capture("dotnet", "--list-sdks"));
        var dotnetRuntimes = Lines(await Capture("dotnet", "--list-runtimes"));

        // ── Visual Studio installations (vswhere) ───────────────────────────
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        object vsInfo;
        var vsInstallPaths = new List<string>();
        if (File.Exists(vswhere))
        {
            var json = await Capture(vswhere, "-all -prerelease -products * -format json -utf8");
            try
            {
                using var doc = JsonDocument.Parse(json);
                var installs = doc.RootElement.EnumerateArray().Select(e =>
                {
                    var path = e.TryGetProperty("installationPath", out var p) ? p.GetString() : null;
                    if (path is not null) vsInstallPaths.Add(path);
                    return (object)new
                    {
                        displayName = e.TryGetProperty("displayName", out var d) ? d.GetString() : null,
                        version     = e.TryGetProperty("installationVersion", out var v) ? v.GetString() : null,
                        installationPath = path,
                    };
                }).ToList();
                vsInfo = new { vswherePath = vswhere, count = installs.Count, installations = installs };
            }
            catch
            {
                vsInfo = new { vswherePath = vswhere, parseError = true, raw = ProcessRunner.Truncate(json, 2000) };
            }
        }
        else
        {
            vsInfo = new { vswherePath = (string?)null, note = "vswhere.exe not found; no Visual Studio installer present." };
        }

        // ── MSVC cl.exe ─────────────────────────────────────────────────────
        var clOnPath = FindOnPath("cl.exe");
        var clInVs = new List<string>();
        foreach (var vsPath in vsInstallPaths)
        {
            var msvcRoot = Path.Combine(vsPath, "VC", "Tools", "MSVC");
            if (!Directory.Exists(msvcRoot)) continue;
            foreach (var verDir in SafeDirs(msvcRoot))
            {
                foreach (var rel in new[] { @"bin\Hostx64\x64\cl.exe", @"bin\Hostx86\x86\cl.exe" })
                {
                    var cl = Path.Combine(verDir, rel);
                    if (File.Exists(cl)) clInVs.Add(cl);
                }
            }
        }

        // ── Path/size checks ────────────────────────────────────────────────
        List<object>? pathInfos = null;
        if (paths is { Length: > 0 })
        {
            pathInfos = new List<object>();
            foreach (var raw in paths)
            {
                var path = raw?.Trim();
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        pathInfos.Add(new
                        {
                            path,
                            exists = true,
                            type = "file",
                            sizeBytes = fi.Length,
                            sizeMB = Math.Round(fi.Length / 1024d / 1024d, 2),
                            lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        });
                    }
                    else if (Directory.Exists(path))
                    {
                        var di = new DirectoryInfo(path);
                        pathInfos.Add(new
                        {
                            path,
                            exists = true,
                            type = "directory",
                            lastModified = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        });
                    }
                    else
                    {
                        pathInfos.Add(new { path, exists = false });
                    }
                }
                catch (Exception ex)
                {
                    pathInfos.Add(new { path, error = ex.Message });
                }
            }
        }

        return new
        {
            os = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            workingDirectory = workDir,
            dotnet = new
            {
                version  = dotnetVersion,
                sdks     = dotnetSdks,
                runtimes = dotnetRuntimes,
            },
            visualStudio = vsInfo,
            msvc = new
            {
                clOnPath,
                clInVsInstalls = clInVs,
                note = clOnPath is null && clInVs.Count == 0
                    ? "cl.exe not found on PATH or under VS installs. For C++ builds, run from a Developer Command Prompt or call vcvarsall.bat first."
                    : null,
            },
            paths = pathInfos,
        };
    }

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }
}
