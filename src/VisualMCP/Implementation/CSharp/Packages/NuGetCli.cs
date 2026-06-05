using System.Text.Json;
using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.CSharp.Packages;

/// <summary>Shared helper around `dotnet list package --format json` for the NuGet tools.</summary>
internal static class NuGetCli
{
    internal sealed record Package(
        string Project, string Framework, string Id,
        string? Requested, string? Resolved, string? Latest,
        bool Transitive, List<object> Vulnerabilities, List<string> DeprecationReasons);

    internal static async Task<(string? target, object? error)> ResolveTargetAsync(string? projectName)
    {
        var svc = RoslynWorkspaceService.Instance;
        var solution = await svc.EnsureSolutionLoadedAsync();

        if (projectName is not null)
        {
            if (solution is null) return (null, NoSolution());
            var proj = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (proj is null) return (null, new { error = $"Project '{projectName}' not found in the solution." });
            if (proj.FilePath is null) return (null, new { error = $"Project '{projectName}' has no file path on disk." });
            return (proj.FilePath, null);
        }

        var sln = svc.LoadedSolutionPath ?? solution?.FilePath;
        if (sln is null) return (null, NoSolution());
        return (sln, null);
    }

    internal static object NoSolution() => new
    {
        error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx."
    };

    internal static async Task<(List<Package>? packages, object? error)> ListAsync(
        string target, string extraFlags, int timeoutSeconds)
    {
        var args = $"list \"{target}\" package {extraFlags} --format json".Replace("  ", " ").Trim();
        var dir  = Directory.Exists(target) ? target : (Path.GetDirectoryName(target) ?? Directory.GetCurrentDirectory());

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, dir, timeoutSeconds);

        if (timedOut)
            return (null, new { error = $"'dotnet list package' timed out after {timeoutSeconds}s (slow NuGet feed?). Command: dotnet {args}" });

        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
            return (null, new
            {
                error = "dotnet returned no JSON. The project may need restoring first — run build_project (or 'dotnet restore').",
                exitCode,
                output = ProcessRunner.Truncate(string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr, 2000),
            });

        try { return (Parse(stdout[jsonStart..]), null); }
        catch (Exception ex) { return (null, new { error = $"Failed to parse dotnet output: {ex.Message}", output = ProcessRunner.Truncate(stdout, 2000) }); }
    }

    private static List<Package> Parse(string json)
    {
        var list = new List<Package>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("projects", out var projects)) return list;

        foreach (var proj in projects.EnumerateArray())
        {
            var projPath = proj.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
            var projName = Path.GetFileNameWithoutExtension(projPath);

            if (!proj.TryGetProperty("frameworks", out var fws)) continue;

            foreach (var fw in fws.EnumerateArray())
            {
                var fwName = fw.TryGetProperty("framework", out var fn) ? fn.GetString() ?? "" : "";
                Collect(fw, "topLevelPackages", transitive: false);
                Collect(fw, "transitivePackages", transitive: true);

                void Collect(JsonElement frameworkEl, string property, bool transitive)
                {
                    if (!frameworkEl.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return;

                    foreach (var p in arr.EnumerateArray())
                    {
                        string? S(string n) => p.TryGetProperty(n, out var v) ? v.GetString() : null;

                        var vulns = new List<object>();
                        if (p.TryGetProperty("vulnerabilities", out var va) && va.ValueKind == JsonValueKind.Array)
                            foreach (var v in va.EnumerateArray())
                                vulns.Add(new
                                {
                                    severity    = v.TryGetProperty("severity", out var sv) ? sv.GetString() : null,
                                    advisoryUrl = v.TryGetProperty("advisoryurl", out var au) ? au.GetString() : null,
                                });

                        var deps = new List<string>();
                        if (p.TryGetProperty("deprecationReasons", out var da) && da.ValueKind == JsonValueKind.Array)
                            foreach (var d in da.EnumerateArray())
                                if (d.GetString() is { } s) deps.Add(s);

                        list.Add(new Package(projName, fwName, S("id") ?? "?",
                            S("requestedVersion"), S("resolvedVersion"), S("latestVersion"),
                            transitive, vulns, deps));
                    }
                }
            }
        }

        return list;
    }
}
