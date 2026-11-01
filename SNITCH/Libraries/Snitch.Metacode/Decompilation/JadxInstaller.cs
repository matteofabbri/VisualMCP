using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Snitch.Platform;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// What this machine has: the JADX CLI launcher (<c>bin/jadx.bat</c> on Windows, <c>bin/jadx</c>
/// elsewhere), the home it belongs to, the version the folder name carries, and where the answer came
/// from — an explicit option, an environment variable, or the tools folder this installer owns.
/// </summary>
public sealed record JadxInstallation(
    bool Installed,
    string? CliPath = null,
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
/// Finds JADX, and installs it when it is not there. Unlike Ghidra's, the release archive is not one
/// file: <c>jadx_&lt;version&gt;.zip</c> unpacks as a distribution with a <c>bin</c> folder (the
/// platform launchers) and a <c>lib</c> folder (the jars), at the archive root or one folder down
/// depending on how it was packed. <see cref="HostSystem"/> decides which launcher this process can
/// execute and whether the extracted scripts need the execute bit restored.
/// </summary>
public interface IJadxInstaller
{
    /// <summary>What this machine resolves to right now — never downloads anything.</summary>
    JadxInstallation Status(JadxOptions? options = null);

    /// <summary>
    /// Returns the existing installation, or downloads and extracts the latest JADX release under
    /// <see cref="JadxOptions.ToolsDirectory"/>. Idempotent: an installed JADX is reported, not
    /// replaced, unless <paramref name="force"/> is set.
    /// </summary>
    Task<JadxInstallation> EnsureInstalledAsync(
        JadxOptions? options = null,
        bool force = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The installer. It never throws for the expected failures (no network, a rate-limited GitHub API, an
/// archive that does not contain a launcher): the reason and the manual fix come back in
/// <see cref="JadxInstallation.Error"/> and <see cref="JadxInstallation.Hint"/>.
/// </summary>
public sealed class JadxInstaller : IJadxInstaller
{
    private const string ReleasesApi = "https://api.github.com/repos/skylot/jadx/releases/latest";

    /// <summary>How much of the archive may be downloaded; JADX is ~70 MB, so a runaway response is cut off.</summary>
    private const long MaxArchiveBytes = 512L * 1024 * 1024;

    public JadxInstallation Status(JadxOptions? options = null)
    {
        options ??= JadxOptions.Default;

        var location = JadxService.Resolve(options);
        if (location is not null)
            return Describe(location.Value.Cli, location.Value.Source, options);

        return new JadxInstallation(
            Installed: false,
            ToolsDirectory: ToolsDirectory(options),
            Hint: "Call EnsureInstalledAsync (or metacoding action=decompile on a .jar/.apk/.dex/.class, which installs it " +
                  "on demand), set JADX_HOME / JADX_CLI, or pass JadxOptions.JadxHome / CliPath. JADX itself needs a JDK " +
                  "on this machine (JAVA_HOME or the usual install roots).");
    }

    public async Task<JadxInstallation> EnsureInstalledAsync(
        JadxOptions? options = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        options ??= JadxOptions.Default;
        var started = Stopwatch.StartNew();

        var existing = Status(options);
        if (existing.Installed && !force) return existing;

        if (!options.AutoInstall)
        {
            return existing with
            {
                Error = existing.Error ?? "JADX is not installed and AutoInstall is disabled.",
                Elapsed = started.Elapsed,
            };
        }

        var tools = ToolsDirectory(options);
        var work = Path.Combine(Path.GetTempPath(), "visualmcp-jadx-" + Guid.NewGuid().ToString("N"));

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
                return new JadxInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Error: $"The JADX release feed answered HTTP {(int)response.StatusCode}.",
                    Hint: "Install JADX by hand, set JADX_HOME, or pass JadxOptions.JadxHome.");
            }

            var release = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var asset = SelectAsset(release);
            if (asset is null)
            {
                return new JadxInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Error: "The latest JADX release has no jadx-<version>.zip asset (the GUI bundles alone are not enough: " +
                           "the CLI is what decompiles).",
                    Hint: "Install JADX by hand (the cross-platform jadx-<version>.zip) and set JADX_HOME.");
            }

            var archive = Path.Combine(work, asset.Value.Name);
            using (var download = await client
                       .GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!download.IsSuccessStatusCode)
                {
                    return new JadxInstallation(
                        Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                        Asset: asset.Value.Name,
                        Error: $"The archive download failed with HTTP {(int)download.StatusCode}.",
                        Hint: "Retry later, or install JADX by hand and set JADX_HOME.");
                }

                var declared = download.Content.Headers.ContentLength;
                if (declared is > MaxArchiveBytes)
                {
                    return new JadxInstallation(
                        Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                        Asset: asset.Value.Name,
                        Error: $"The release archive announces {declared} bytes, more than the {MaxArchiveBytes / (1024 * 1024)} MB this installer accepts.",
                        Hint: "Install JADX by hand and set JADX_HOME.");
                }

                await using var file = File.Create(archive);
                await download.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var bytes = new FileInfo(archive).Length;
            var extracted = Path.Combine(work, "extracted");
            Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);

            var cli = FindCli(extracted);
            if (cli is null)
            {
                return new JadxInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Asset: asset.Value.Name, Bytes: bytes,
                    Error: $"The archive did not contain bin/{LauncherName}.",
                    Hint: "The release layout changed: install JADX by hand and set JADX_HOME.");
            }

            var sourceHome = HomeOf(cli);
            if (sourceHome is null)
            {
                return new JadxInstallation(
                    Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                    Asset: asset.Value.Name, Bytes: bytes,
                    Error: "The launcher is not inside a JADX home (no bin folder).");
            }

            // The release packs bin/ and lib/ at the archive root; when it nests them one folder down,
            // the folder name is the one to keep. Either way the target is named after the version.
            var folder = "jadx-" + (asset.Value.Version ?? "latest");
            var target = Path.Combine(tools, folder);
            Directory.CreateDirectory(tools);

            if (Directory.Exists(target))
            {
                // A folder with no launcher is a half-finished install, not an installation: replace it
                // rather than returning the path of a file inside the temporary extraction.
                var existingCli = FindCli(target);
                if (existingCli is not null && !force)
                    return Describe(existingCli, "tools", options) with { Elapsed = started.Elapsed };

                Directory.Delete(target, recursive: true);
            }

            ToolInstall.MoveDirectory(sourceHome, target);
            RestoreUnixModes(target);

            var installed = FindCli(target)!;
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
            return new JadxInstallation(
                Installed: false, ToolsDirectory: tools, Elapsed: started.Elapsed,
                Error: $"The JADX install failed: {exception.GetType().Name}: {exception.Message}",
                Hint: "Download the cross-platform jadx-<version>.zip by hand, extract it under the tools directory, " +
                      "or set JADX_HOME.");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    /// <summary>The tools directory an install writes to: the option, else the per-user VisualMCP folder.</summary>
    public static string ToolsDirectory(JadxOptions options) =>
        string.IsNullOrWhiteSpace(options.ToolsDirectory)
            ? JadxOptions.DefaultToolsDirectory
            : Path.GetFullPath(options.ToolsDirectory);

    /// <summary>The launcher this platform executes: the batch file on Windows, the shell script elsewhere.</summary>
    public static string LauncherName => HostSystem.IsWindows ? "jadx.bat" : "jadx";

    /// <summary>
    /// The one asset this installer wants: the cross-platform CLI bundle <c>jadx-&lt;version&gt;.zip</c>.
    /// The GUI bundles are excluded by name — they carry no <c>bin/jadx</c> pair for both platforms and
    /// are three times the size. Kept public and free of side effects so a caller can inspect a feed
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
            if (!name.StartsWith("jadx-", StringComparison.OrdinalIgnoreCase)) continue;

            // jadx-gui-*.zip and jadx-gui-*-with-jre-win.zip are the GUI distributions.
            var version = VersionOf(name);
            if (version is null) continue;

            candidates.Add((name, url, version.ToString(), version));
        }

        if (candidates.Count == 0) return null;

        var best = candidates.OrderByDescending(candidate => candidate.Sort).First();
        return (best.Name, best.Url, best.Version);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary><c>jadx-1.5.6.zip</c> → 1.5.6; a GUI bundle (whose stem is not a version) → null.</summary>
    private static Version? VersionOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith("jadx-", StringComparison.OrdinalIgnoreCase)) return null;

        var digits = stem["jadx-".Length..];
        return Version.TryParse(digits.TrimEnd('-'), out var version) ? version : null;
    }

    private static string? FindCli(string home)
    {
        var candidate = Path.Combine(home, "bin", LauncherName);
        if (File.Exists(candidate)) return candidate;

        // An archive unpacked on another platform keeps the other spelling; finding it is still better
        // than declaring the installation broken, and it is what Resolve() would have accepted.
        var other = Path.Combine(home, "bin", HostSystem.IsWindows ? "jadx" : "jadx.bat");
        if (File.Exists(other)) return other;

        // The release has been packed both ways: at the root and inside one folder.
        foreach (var directory in Directory.EnumerateDirectories(home))
        {
            candidate = Path.Combine(directory, "bin", LauncherName);
            if (File.Exists(candidate)) return candidate;

            candidate = Path.Combine(directory, "bin", HostSystem.IsWindows ? "jadx" : "jadx.bat");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? HomeOf(string cli)
    {
        var bin = Path.GetDirectoryName(cli);
        return bin is null ? null : Path.GetDirectoryName(bin);
    }

    private static JadxInstallation Describe(string cli, string source, JadxOptions options)
    {
        var home = HomeOf(cli);
        var folder = home is null ? null : Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new JadxInstallation(
            Installed: true,
            CliPath: cli,
            Home: home,
            Version: VersionFromFolder(folder),
            Source: source,
            ToolsDirectory: ToolsDirectory(options));
    }

    private static string? VersionFromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var start = folder.IndexOf("jadx-", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        return Version.TryParse(folder[(start + "jadx-".Length)..].TrimEnd('-'), out var version)
            ? version.ToString()
            : null;
    }

    /// <summary>
    /// A zip carries no Unix mode bits on Windows, so the scripts the launcher needs arrive
    /// non-executable. Restoring the bit on the launchers and every shell script is what makes a
    /// Windows-extracted archive usable on Linux and macOS.
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
            var inBin = string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "bin", StringComparison.OrdinalIgnoreCase);
            if (!name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) && !(inBin && name is "jadx" or "jadx-gui"))
                continue;

#pragma warning disable CA1416 // HostSystem.IsWindows already returned above.
            try { File.SetUnixFileMode(file, mode); } catch { /* a read-only archive is not fatal */ }
#pragma warning restore CA1416
        }
    }
}
