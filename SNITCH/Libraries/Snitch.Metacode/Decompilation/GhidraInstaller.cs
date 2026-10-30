using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Snitch.Platform;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// What this machine has: the <c>analyzeHeadless</c> script, the Ghidra home it belongs to, the version
/// the folder name carries, and where the answer came from — an explicit option, an environment
/// variable, or the tools folder this installer owns.
/// </summary>
public sealed record GhidraInstallation(
    bool Installed,
    string? HeadlessPath = null,
    string? Home = null,
    string? Version = null,
    string? Source = null,
    string? Asset = null,
    long Bytes = 0,
    TimeSpan Elapsed = default,
    string? ToolsDirectory = null,
    string? Error = null,
    string? Hint = null);

/// <summary>
/// Finds Ghidra, and installs it when it is not there. The release archive is the platform-independent
/// <c>ghidra_&lt;version&gt;_PUBLIC_&lt;date&gt;.zip</c>; <see cref="HostSystem"/> decides which launcher
/// inside it is the one this process can execute (<c>analyzeHeadless.bat</c> on Windows,
/// <c>analyzeHeadless</c> elsewhere) and whether the extracted scripts need the execute bit restored.
/// </summary>
public interface IGhidraInstaller
{
    /// <summary>What this machine resolves to right now — never downloads anything.</summary>
    GhidraInstallation Status(GhidraOptions? options = null);

    /// <summary>
    /// Returns the existing installation, or downloads and extracts the latest Ghidra release under
    /// <see cref="GhidraOptions.ToolsDirectory"/>. Idempotent: an installed Ghidra is reported, not
    /// replaced, unless <paramref name="force"/> is set.
    /// </summary>
    Task<GhidraInstallation> EnsureInstalledAsync(
        GhidraOptions? options = null,
        bool force = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The installer. It never throws for the expected failures (no network, a rate-limited GitHub API, an
/// archive that does not contain a launcher): the reason and the manual fix come back in
/// <see cref="GhidraInstallation.Error"/> and <see cref="GhidraInstallation.Hint"/>.
/// </summary>
public sealed class GhidraInstaller : IGhidraInstaller
{
    private const string ReleasesApi = "https://api.github.com/repos/NationalSecurityAgency/ghidra/releases/latest";

    public GhidraInstallation Status(GhidraOptions? options = null)
    {
        options ??= GhidraOptions.Default;

        var location = GhidraService.Resolve(options);
        if (location is not null)
            return Describe(location.Value.Headless, location.Value.Source, options);

        return new GhidraInstallation(
            Installed: false,
            ToolsDirectory: ToolsDirectory(options),
            Hint: "Call EnsureInstalledAsync (or analyze_native_binary / metacoding decompile, which install it on demand), " +
                  "set GHIDRA_INSTALL_DIR, or pass GhidraOptions.GhidraHome / HeadlessPath.");
    }

    public async Task<GhidraInstallation> EnsureInstalledAsync(
        GhidraOptions? options = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        options ??= GhidraOptions.Default;
        var started = Stopwatch.StartNew();

        var existing = Status(options);
        if (existing.Installed && !force) return existing;

        if (!options.AutoInstall)
        {
            return existing with
            {
                Error = existing.Error ?? "Ghidra is not installed and AutoInstall is disabled.",
                Elapsed = started.Elapsed,
            };
        }

        var tools = ToolsDirectory(options);
        var work = Path.Combine(Path.GetTempPath(), "visualmcp-ghidra-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(work);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.TryAddWithoutValidation("User-Agent", "VisualMCP/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GhidraInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Error: $"The Ghidra release feed answered HTTP {(int)response.StatusCode}.",
                    Hint: "Install Ghidra by hand, set GHIDRA_INSTALL_DIR, or pass GhidraOptions.GhidraHome.");
            }

            var release = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var asset = SelectAsset(release);
            if (asset is null)
            {
                return new GhidraInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Error: "The latest Ghidra release has no *_PUBLIC_*.zip asset.",
                    Hint: "Install Ghidra by hand and set GHIDRA_INSTALL_DIR.");
            }

            var archive = Path.Combine(work, asset.Value.Name);
            using (var download = await client
                       .GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!download.IsSuccessStatusCode)
                {
                    return new GhidraInstallation(
                        Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                        Asset: asset.Value.Name,
                        Error: $"The archive download failed with HTTP {(int)download.StatusCode}.",
                        Hint: "Retry later, or install Ghidra by hand and set GHIDRA_INSTALL_DIR.");
                }

                await using var file = File.Create(archive);
                await download.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var bytes = new FileInfo(archive).Length;
            var extracted = Path.Combine(work, "extracted");
            Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);

            var headless = FindHeadless(extracted);
            if (headless is null)
            {
                return new GhidraInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Asset: asset.Value.Name, Bytes: bytes,
                    Error: $"The archive did not contain support/{LauncherName}.",
                    Hint: "The release layout changed: install Ghidra by hand and set GHIDRA_INSTALL_DIR.");
            }

            var sourceHome = HomeOf(headless);
            if (sourceHome is null)
            {
                return new GhidraInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Asset: asset.Value.Name, Bytes: bytes,
                    Error: "The launcher is not inside a Ghidra home (no support folder).");
            }

            // The Ghidra home is the folder the archive extracted, e.g. ghidra_12.2_PUBLIC.
            var target = Path.Combine(tools, Path.GetFileName(sourceHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            Directory.CreateDirectory(tools);

            if (Directory.Exists(target))
            {
                // A folder with no launcher is a half-finished install, not an installation: replace it
                // rather than returning the path of a file inside the temporary extraction.
                var existingHeadless = FindHeadless(target);
                if (existingHeadless is not null && !force)
                    return Describe(existingHeadless, "tools", options) with { Elapsed = started.Elapsed };

                Directory.Delete(target, recursive: true);
            }

            Directory.Move(sourceHome, target);
            RestoreUnixModes(target);

            var installed = FindHeadless(target)!;
            var status = Describe(installed, "tools", options);
            return status with
            {
                Asset = asset.Value.Name,
                Bytes = bytes,
                Elapsed = started.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new GhidraInstallation(
                Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                Error: $"The Ghidra install failed: {exception.GetType().Name}: {exception.Message}",
                Hint: "Download the release by hand, extract it under the tools directory, or set GHIDRA_INSTALL_DIR.");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    /// <summary>The tools directory an install writes to: the option, else the per-user VisualMCP folder.</summary>
    public static string ToolsDirectory(GhidraOptions options) =>
        string.IsNullOrWhiteSpace(options.ToolsDirectory)
            ? GhidraOptions.DefaultToolsDirectory
            : Path.GetFullPath(options.ToolsDirectory);

    /// <summary>The launcher this platform executes: the batch file on Windows, the shell script elsewhere.</summary>
    public static string LauncherName => HostSystem.IsWindows ? "analyzeHeadless.bat" : "analyzeHeadless";

    /// <summary>
    /// The one archive Ghidra publishes per release (it is platform-independent); when several match,
    /// the highest version wins. Kept public and free of side effects so a caller can inspect a feed
    /// response without downloading anything.
    /// </summary>
    public static (string Name, string Url, string? Version)? SelectAsset(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(string Name, string Url, string? Version, Version Sort)>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;

            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");
            if (name is null || url is null) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("_PUBLIC_", StringComparison.OrdinalIgnoreCase)) continue;

            var version = VersionOf(name);
            candidates.Add((name, url, version?.ToString(), version ?? new Version(0, 0)));
        }

        if (candidates.Count == 0) return null;

        var best = candidates.OrderByDescending(candidate => candidate.Sort).First();
        return (best.Name, best.Url, best.Version);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Version? VersionOf(string fileName)
    {
        var start = fileName.IndexOf("ghidra_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += "ghidra_".Length;
        var end = fileName.IndexOf('_', start);
        if (end < 0) return null;

        var digits = fileName[start..end];
        return Version.TryParse(digits, out var version) ? version : null;
    }

    private static string? FindHeadless(string home)
    {
        var candidate = Path.Combine(home, "support", LauncherName);
        if (File.Exists(candidate)) return candidate;

        // An archive unpacked on another platform keeps the other spelling; finding it is still better
        // than declaring the installation broken, and it is what Resolve() would have accepted.
        var other = Path.Combine(home, "support", HostSystem.IsWindows ? "analyzeHeadless" : "analyzeHeadless.bat");
        return File.Exists(other) ? other : null;
    }

    private static string? HomeOf(string headless)
    {
        var support = Path.GetDirectoryName(headless);
        return support is null ? null : Path.GetDirectoryName(support);
    }

    private static GhidraInstallation Describe(string headless, string source, GhidraOptions options)
    {
        var home = HomeOf(headless);
        var folder = home is null ? null : Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new GhidraInstallation(
            Installed: true,
            HeadlessPath: headless,
            Home: home,
            Version: VersionFromFolder(folder),
            Source: source,
            ToolsDirectory: ToolsDirectory(options));
    }

    private static string? VersionFromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var start = folder.IndexOf("ghidra_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += "ghidra_".Length;
        var end = folder.IndexOf('_', start);
        return end > start ? folder[start..end] : null;
    }

    /// <summary>
    /// A zip carries no Unix mode bits on Windows, so the scripts that Ghidra's launcher runs arrive
    /// non-executable. Restoring the bit on the launchers and every shell script is what makes a
    /// Windows-extracted archive usable on Linux.
    /// </summary>
    private static void RestoreUnixModes(string home)
    {
        // HostSystem is the single answer to "which platform is this" in the library; the analyzer cannot
        // see through it, so the one platform-specific call below is guarded explicitly.
        if (HostSystem.IsWindows) return;

        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        foreach (var file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) &&
                name is not ("analyzeHeadless" or "ghidraRun" or "ghidraSvr" or "pyghidraRun" or "support" or "launch.sh"))
            {
                continue;
            }

#pragma warning disable CA1416 // HostSystem.IsWindows already returned above.
            try { File.SetUnixFileMode(file, mode); } catch { /* a read-only archive is not fatal */ }
#pragma warning restore CA1416
        }
    }
}
