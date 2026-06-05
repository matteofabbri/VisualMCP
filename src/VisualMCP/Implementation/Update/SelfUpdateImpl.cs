using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace VisualMCP.Implementation.Update;

/// <summary>
/// Checks GitHub Releases for a newer build and, on request, downloads and applies
/// it. The running server cannot overwrite its own files, so the update is staged
/// and a detached helper swaps it in after this process exits (Claude Code then
/// relaunches the updated server).
/// </summary>
internal static class SelfUpdateImpl
{
    private const string DefaultRepo = "matteofabbri/VisualMCP";
    private const string UserAgent = "VisualMCP-selfupdate";

    private sealed record Asset(string Name, long Size, string Url);
    private sealed record Release(string? Tag, string? Published, string? HtmlUrl, List<Asset> Assets);

    internal static async Task<object> CheckAsync(string? repo)
    {
        var r = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo!.Trim();
        var (rel, err) = await GetLatestReleaseAsync(r);
        if (err is not null) return new { repo = r, error = err };

        return new
        {
            repo = r,
            currentVersion = CurrentVersion(),
            installDir = AppContext.BaseDirectory,
            latestTag = rel!.Tag,
            publishedAt = rel.Published,
            releaseUrl = rel.HtmlUrl,
            assets = rel.Assets.Select(a => new { a.Name, sizeMB = Math.Round(a.Size / 1024d / 1024d, 2) }),
        };
    }

    internal static async Task<object> UpdateAsync(string? repo, string? assetPattern)
    {
        var r = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo!.Trim();
        var (rel, err) = await GetLatestReleaseAsync(r);
        if (err is not null) return new { repo = r, error = err };
        if (rel!.Assets.Count == 0) return new { repo = r, error = $"Release '{rel.Tag}' has no downloadable assets." };

        var pattern = string.IsNullOrWhiteSpace(assetPattern) ? "win-x64" : assetPattern!.Trim();
        var asset = rel.Assets.FirstOrDefault(a => a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                 ?? rel.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new { repo = r, error = $"No matching .zip asset (pattern '{pattern}'). Assets: {string.Join(", ", rel.Assets.Select(a => a.Name))}" };

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(appDir)?.FullName ?? appDir;
        var staging = Path.Combine(parent, $"app_update_{Guid.NewGuid():N}");
        var zipPath = Path.Combine(Path.GetTempPath(), $"visualmcp-update-{Guid.NewGuid():N}.zip");

        try
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = true })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return new { error = $"Download failed: HTTP {(int)resp.StatusCode}", asset = asset.Name };
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var src = await resp.Content.ReadAsStreamAsync();
                await src.CopyToAsync(fs);
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            return new { error = $"Failed to download/extract update: {ex.Message}" };
        }
        finally { try { File.Delete(zipPath); } catch { } }

        // Detached helper that swaps staging -> appDir once this process has exited.
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
$app = '{{appDir}}'
$staging = '{{staging}}'
Start-Sleep -Seconds 2
for ($i = 0; $i -lt 60; $i++) {
    Get-Process VisualMCP -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        if (Test-Path $app) { Remove-Item -Recurse -Force $app -ErrorAction Stop }
        Move-Item $staging $app -ErrorAction Stop
        break
    } catch { Start-Sleep -Seconds 1 }
}
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
""";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"visualmcp-apply-update-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true));

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to launch updater: {ex.Message}. Update staged at {staging}." };
        }

        // Exit shortly so the helper can replace the (now unlocked) install.
        _ = Task.Run(async () => { await Task.Delay(1200); Environment.Exit(0); });

        var looksInstalled = appDir.Replace('/', '\\').Contains(@".claude\mcp-servers\VisualMCP\app", StringComparison.OrdinalIgnoreCase);
        return new
        {
            repo = r,
            updatingTo = rel.Tag,
            asset = asset.Name,
            installDir = appDir,
            staged = staging,
            applying = true,
            note = looksInstalled
                ? "Update downloaded. This server will exit now; a helper swaps in the new version and Claude Code relaunches it (brief disconnect)."
                : $"WARNING: the running server is not the global install ({appDir}); the update will replace THIS directory. Run self_update on the installed server instead if unintended.",
        };
    }

    private static async Task<(Release? rel, string? error)> GetLatestReleaseAsync(string repo)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, $"No published release found for '{repo}' (or the repo is private/nonexistent).");
            if (!resp.IsSuccessStatusCode)
                return (null, $"GitHub API returned HTTP {(int)resp.StatusCode} for '{repo}'.");

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var assets = new List<Asset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                    assets.Add(new Asset(
                        a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                        a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : ""));

            return (new Release(
                root.TryGetProperty("tag_name", out var t) ? t.GetString() : null,
                root.TryGetProperty("published_at", out var p) ? p.GetString() : null,
                root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                assets), null);
        }
        catch (Exception ex) { return (null, $"Failed to query GitHub: {ex.Message}"); }
    }

    private static string CurrentVersion() =>
        typeof(SelfUpdateImpl).Assembly.GetName().Version?.ToString() ?? "unknown";
}
