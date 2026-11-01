using System.Net.Http;
using System.Text.Json;

namespace Snitch.Languages.CSharp.Packages;

/// <summary>
/// Searches the NuGet gallery (the nuget.org search index, not the local cache) for
/// packages by id fragment or keywords. This is the answer to "which package do I add?";
/// <c>add_nuget_package</c> then needs the exact id this returns.
/// </summary>
public interface ISearchNuGetPackagesService
{
    Task<object> SearchAsync(string query, int take, bool prerelease, int timeoutSeconds);
}

public sealed class SearchNuGetPackagesService : ISearchNuGetPackagesService
{
    /// <summary>The v3 search endpoint of the nuget.org azuresearch service.</summary>
    internal const string SearchEndpoint = "https://azuresearch-usnc.nuget.org/query";

    public async Task<object> SearchAsync(string query, int take, bool prerelease, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new { error = "A search query is required." };

        var count = Math.Clamp(take < 1 ? 20 : take, 1, 100);
        var url = $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&take={count}" +
                  $"&prerelease={prerelease.ToString().ToLowerInvariant()}&semVerLevel=2.0.0";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds < 1 ? 30 : timeoutSeconds, 1, 120)) };
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return new { error = $"The NuGet search endpoint answered HTTP {(int)resp.StatusCode}.", query, url };

            var (totalHits, packages) = Parse(await resp.Content.ReadAsStringAsync());
            return new
            {
                query,
                take = count,
                prerelease,
                totalHits,
                packages,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"The NuGet search failed: {ex.Message}", query, url };
        }
    }

    /// <summary>One row of the gallery answer.</summary>
    public sealed record Hit(
        string Id,
        string Version,
        string Description,
        long TotalDownloads,
        List<string> Authors,
        List<string> Tags,
        int VersionCount,
        string ProjectUrl);

    /// <summary>Parses the azuresearch JSON; public so the shape is testable without a network.</summary>
    public static (int TotalHits, List<Hit> Packages) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var totalHits = root.TryGetProperty("totalHits", out var th) && th.TryGetInt32(out var n) ? n : 0;

        var packages = new List<Hit>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                packages.Add(new Hit(
                    Str(item, "id"),
                    Str(item, "version"),
                    Str(item, "description"),
                    Long(item, "totalDownloads"),
                    Strings(item, "authors"),
                    Strings(item, "tags"),
                    item.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array
                        ? versions.GetArrayLength()
                        : 0,
                    Str(item, "projectUrl")));
            }
        }
        return (totalHits, packages);
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static List<string> Strings(JsonElement o, string name)
    {
        var list = new List<string>();
        if (o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var s in v.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String) list.Add(s.GetString()!);
        return list;
    }
}
