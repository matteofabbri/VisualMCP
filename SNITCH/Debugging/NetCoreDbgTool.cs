using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Snitch.Debugging;

/// <summary>
/// Locates netcoredbg (the Samsung DAP adapter for .NET) and installs it on demand from the
/// GitHub release into the shared tools directory — the same contract as the profiling tools:
/// nothing external is assumed to be on PATH, and the environment variable
/// <c>VISUALMCP_NETCOREDBG</c> wins over everything.
/// </summary>
public sealed class NetCoreDbgTool
{
    private const string ReleasesApi = "https://api.github.com/repos/Samsung/netcoredbg/releases/latest";
    private const string EnvironmentVariable = "VISUALMCP_NETCOREDBG";

    private readonly DebugOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public NetCoreDbgTool(DebugOptions options) => _options = options;

    /// <summary>The adapter executable, or null when it is not installed.</summary>
    public string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath) && File.Exists(_options.ExecutablePath))
            return Path.GetFullPath(_options.ExecutablePath);

        var name = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var candidate = Path.Combine(_options.ToolsDirectory, "netcoredbg", name);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Whether the adapter is present, where, and how to point at a different build.</summary>
    public object Status()
    {
        var path = Locate();
        return new
        {
            installed = path is not null,
            path,
            toolsDirectory = _options.ToolsDirectory,
            environmentVariable = $"{EnvironmentVariable} (absolute path to the netcoredbg executable)",
            next = path is null
                ? "Call debug_install to download netcoredbg from the Samsung release (a few MB), or set the environment variable."
                : "Ready: debug_launch starts a program under the adapter.",
        };
    }

    /// <summary>
    /// Downloads the matching asset of the latest release and extracts it under
    /// <c>&lt;tools&gt;/netcoredbg</c>. Idempotent: an installed adapter is reported, not replaced.
    /// </summary>
    public async Task<object> InstallAsync(bool force = false)
    {
        var existing = Locate();
        if (existing is not null && !force)
            return new { installed = true, alreadyPresent = true, path = existing };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.TryAddWithoutValidation("User-Agent", "VisualMCP/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new
                {
                    installed = false,
                    statusCode = (int)response.StatusCode,
                    error = "The GitHub release metadata could not be read.",
                    hint = $"Set {EnvironmentVariable} to an existing netcoredbg executable to skip the download.",
                };

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;

            var asset = SelectAsset(root);
            if (asset is null)
                return new
                {
                    installed = false,
                    tag,
                    error = $"The release has no asset for {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.",
                };

            var work = Path.Combine(Path.GetTempPath(), "visualmcp-netcoredbg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var archive = Path.Combine(work, asset.Value.Name);
                using (var download = await _http.GetAsync(asset.Value.Url))
                {
                    if (!download.IsSuccessStatusCode)
                        return new { installed = false, error = $"The download failed with HTTP {(int)download.StatusCode}." };

                    await using var file = File.Create(archive);
                    await download.Content.CopyToAsync(file);
                }

                var extracted = Path.Combine(work, "extracted");
                Directory.CreateDirectory(extracted);
                if (asset.Value.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);
                else
                    ExtractTarGz(archive, extracted);

                var executableName = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
                var found = Directory.EnumerateFiles(extracted, executableName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is null)
                    return new { installed = false, error = $"The archive did not contain {executableName}." };

                var sourceDirectory = Path.GetDirectoryName(found)!;
                var target = Path.Combine(_options.ToolsDirectory, "netcoredbg");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(_options.ToolsDirectory);
                Directory.Move(sourceDirectory, target);

                var installed = Path.Combine(target, executableName);
                if (!OperatingSystem.IsWindows())
                {
                    const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                              UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(installed, mode);
                }

                return new
                {
                    installed = true,
                    alreadyPresent = false,
                    version = tag,
                    path = installed,
                    asset = asset.Value.Name,
                    note = "netcoredbg is ready. debug_launch starts a built .dll under it.",
                };
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup is best effort */ }
            }
        }
        catch (Exception ex)
        {
            return new
            {
                installed = false,
                error = $"The netcoredbg install failed: {ex.Message}",
                hint = $"Download the release by hand and set {EnvironmentVariable}, or pass its path in DebugOptions.ExecutablePath.",
            };
        }
    }

    private static (string Name, string Url)? SelectAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var wanted = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            Architecture.Arm64 when OperatingSystem.IsMacOS() => "osx-arm64",
            Architecture.X64 when OperatingSystem.IsWindows() => "win64",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux-amd64",
            Architecture.X64 when OperatingSystem.IsMacOS() => "osx-x64",
            _ => null,
        };
        if (wanted is null) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
            if (name is null || url is null) continue;
            if (!name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("symbol", StringComparison.OrdinalIgnoreCase)) continue;
            return (name, url);
        }

        return null;
    }

    private static void ExtractTarGz(string archive, string destination)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }
}
