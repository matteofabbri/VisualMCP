using System.Text.Json;
using Snitch.Profiling.Internal;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Tooling;

/// <summary>How to invoke a diagnostic tool once it has been located.</summary>
public sealed record ToolResolution(
    string Id,
    bool Installed,
    string Source,
    string? CommandPath,
    string? Version,
    string? WorkingDirectory,
    string PrefixArguments)
{
    /// <summary>True when the tool runs through 'dotnet tool run' because it lives in a local manifest.</summary>
    public bool IsLocalTool => Source == "local";
}

public interface IDiagnosticToolService
{
    Task<IReadOnlyList<DiagnosticToolStatus>> GetStatusAsync(
        string? tool, bool checkLatest, string? solutionDirectory, CancellationToken cancellationToken = default);

    Task<ToolInstallResult> EnsureAsync(
        string tool, string action, string scope, string? version,
        string? solutionDirectory, int timeoutSeconds, CancellationToken cancellationToken = default);

    Task<ToolResolution> ResolveAsync(
        string tool, string? solutionDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects, installs and updates the dotnet diagnostic tools. Detection is layered: a local tool
/// manifest wins over a global install, which wins over anything on PATH. That mirrors how the
/// dotnet CLI resolves tool commands, so the version reported here is the one that would run.
/// </summary>
public sealed class DiagnosticToolService : IDiagnosticToolService
{
    private static readonly TimeSpan LatestVersionTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProfilingOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, (string? Version, DateTime FetchedAt)> _latestCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _latestLock = new(1, 1);

    public DiagnosticToolService(ProfilingOptions options) => _options = options;

    public async Task<IReadOnlyList<DiagnosticToolStatus>> GetStatusAsync(
        string? tool, bool checkLatest, string? solutionDirectory, CancellationToken cancellationToken = default)
    {
        var tools = tool is null
            ? DiagnosticToolCatalog.All
            : DiagnosticToolCatalog.All.Where(t =>
                string.Equals(t.Id, tool, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Command, tool, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.PackageId, tool, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (tools.Count == 0)
            return [new DiagnosticToolStatus(tool!, tool!, tool!, false, null, null, "unknown", null,
                "Not a dotnet diagnostics tool.", "", "", "")];

        var results = new List<DiagnosticToolStatus>(tools.Count);
        foreach (var info in tools)
        {
            var resolved = await ResolveAsync(info.Id, solutionDirectory, cancellationToken).ConfigureAwait(false);
            string? latest = null;
            if (checkLatest)
                latest = await GetLatestVersionAsync(info.PackageId, cancellationToken).ConfigureAwait(false);

            results.Add(new DiagnosticToolStatus(
                info.Id, info.Command, info.PackageId,
                resolved.Installed, resolved.Version, latest,
                resolved.Source, resolved.CommandPath,
                info.Purpose, info.NativeCoverage, info.DelegatedOperations, info.DocsUrl));
        }

        return results;
    }

    public async Task<ToolInstallResult> EnsureAsync(
        string tool, string action, string scope, string? version,
        string? solutionDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var info = DiagnosticToolCatalog.Find(tool);
        if (info is null)
            return new ToolInstallResult(tool, action, false, -1,
                $"Unknown tool '{tool}'. Known tools: {string.Join(", ", DiagnosticToolCatalog.All.Select(t => t.Id))}.");

        action = action.Trim().ToLowerInvariant();
        if (action is not ("install" or "update"))
            return new ToolInstallResult(tool, action, false, -1, "Action must be 'install' or 'update'.");

        var resolved = await ResolveAsync(info.Id, solutionDirectory, cancellationToken).ConfigureAwait(false);
        if (action == "install" && resolved.Installed)
            return new ToolInstallResult(info.Id, action, true, 0,
                $"{info.Id} is already installed ({resolved.Version ?? "unknown version"}, {resolved.Source}). Use action='update' to upgrade it.");

        var root = ResolveWorkingDirectory(solutionDirectory);
        var manifest = FindManifest(root);
        var effectiveScope = scope.Trim().ToLowerInvariant() switch
        {
            "local" => "local",
            "global" => "global",
            _ => manifest is not null ? "local" : "global",
        };

        if (effectiveScope == "local" && manifest is null)
        {
            var created = await ProcessRunner.RunAsync(
                _options.DotnetExecutable, "new tool-manifest", root, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (created.ExitCode != 0)
                return new ToolInstallResult(info.Id, action, false, created.ExitCode,
                    $"Could not create a local tool manifest in '{root}'.\n{created.Stdout}\n{created.Stderr}");
            manifest = FindManifest(root);
        }

        var verb = action == "install" ? "install" : "update";
        var scopeArg = effectiveScope == "global" ? " --global" : "";
        var versionArg = string.IsNullOrWhiteSpace(version) ? "" : $" --version {version}";
        var arguments = $"tool {verb}{scopeArg} {info.PackageId}{versionArg}";
        var workingDirectory = effectiveScope == "global" ? root : Path.GetDirectoryName(Path.GetDirectoryName(manifest!))!;

        var run = await ProcessRunner.RunAsync(
            _options.DotnetExecutable, arguments, workingDirectory, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        var output = string.Join('\n', new[] { run.Stdout.Trim(), run.Stderr.Trim() }.Where(s => s.Length > 0));
        return new ToolInstallResult(info.Id, action, run.ExitCode == 0, run.ExitCode,
            string.IsNullOrWhiteSpace(output) ? $"dotnet {arguments} exited with code {run.ExitCode}." : output);
    }

    public async Task<ToolResolution> ResolveAsync(
        string tool, string? solutionDirectory, CancellationToken cancellationToken = default)
    {
        var info = DiagnosticToolCatalog.Find(tool);
        if (info is null)
            return new ToolResolution(tool, false, "unknown", null, null, null, "");

        var root = ResolveWorkingDirectory(solutionDirectory);

        // 1. Local manifest (the dotnet CLI's first choice inside a repository).
        var manifest = FindManifest(root);
        if (manifest is not null && TryReadManifestVersion(manifest, info, out var localVersion))
        {
            var version = localVersion;
            var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(manifest)!)!;
            var invoked = await RunVersionAsync(_options.DotnetExecutable, $"tool run {info.Command} --version",
                repoRoot, cancellationToken).ConfigureAwait(false);
            if (invoked.Version is not null)
                version = invoked.Version;

            return new ToolResolution(info.Id, true, "local", null, version ?? localVersion,
                repoRoot, $"tool run {info.Command}");
        }

        // 2. Global tool.
        var globalVersion = await ReadGlobalToolVersionAsync(info.PackageId, cancellationToken).ConfigureAwait(false);
        if (globalVersion is not null)
        {
            var commandsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            var exe = ResolveExecutable(Path.Combine(commandsDir, info.Command));
            var probed = exe is not null
                ? await RunVersionAsync(exe, "--version", commandsDir, cancellationToken).ConfigureAwait(false)
                : default;
            return new ToolResolution(info.Id, true, "global", exe, probed.Version ?? globalVersion, commandsDir, "");
        }

        // 3. On PATH.
        var onPath = ResolveOnPath(info.Command);
        if (onPath is not null)
        {
            var probed = await RunVersionAsync(onPath, "--version", Path.GetDirectoryName(onPath), cancellationToken).ConfigureAwait(false);
            return new ToolResolution(info.Id, true, "path", onPath, probed.Version, Path.GetDirectoryName(onPath), "");
        }

        return new ToolResolution(info.Id, false, "not-installed", null, null, root, "");
    }

    private async Task<(string? Version, string? Error)> RunVersionAsync(
        string exe, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var run = await ProcessRunner.RunAsync(exe, arguments, workingDirectory, 20, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return (null, run.Stderr.Trim());

            var text = (run.Stdout + "\n" + run.Stderr).Trim();
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));
            return (line, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<string?> ReadGlobalToolVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await ProcessRunner.RunAsync(
                _options.DotnetExecutable, "tool list --global", null, 60, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
                return null;

            foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 2)
                    continue;
                if (!string.Equals(columns[0], packageId, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Version.TryParse(columns[1], out _) ? columns[1] : null;
            }
        }
        catch
        {
            // A broken dotnet installation must not break status reporting.
        }

        return null;
    }

    private static bool TryReadManifestVersion(string manifestPath, DiagnosticToolInfo info, out string? version)
    {
        version = null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("tools", out var tools))
                return false;

            foreach (var property in tools.EnumerateObject())
            {
                if (!string.Equals(property.Name, info.PackageId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.TryGetProperty("version", out var v))
                    version = v.GetString();
                return true;
            }
        }
        catch
        {
            // A malformed manifest is treated as "not present"; the caller falls back to global/PATH.
        }

        return false;
    }

    private static string? FindManifest(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".config", "dotnet-tools.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static string? ResolveOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var resolved = ResolveExecutable(Path.Combine(directory.Trim(), command));
                if (resolved is not null)
                    return resolved;
            }
            catch
            {
                // Invalid PATH entries are common; skip them.
            }
        }
        return null;
    }

    private static string? ResolveExecutable(string pathWithoutExtension)
    {
        if (File.Exists(pathWithoutExtension))
            return pathWithoutExtension;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat" }
            : new[] { "" };

        foreach (var extension in extensions)
        {
            var candidate = pathWithoutExtension + extension;
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private async Task<string?> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        await _latestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latestCache.TryGetValue(packageId, out var cached) && DateTime.UtcNow - cached.FetchedAt < LatestVersionTtl)
                return cached.Version;

            var url = $"{_options.NuGetServiceIndex.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json";
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("versions", out var versions))
                return null;

            var latest = versions.EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => !string.IsNullOrWhiteSpace(v) && !v.Contains('-'))
                .LastOrDefault();

            _latestCache[packageId] = (latest, DateTime.UtcNow);
            return latest;
        }
        catch
        {
            // Offline or NuGet outage: the status still reports the installed version.
            _latestCache[packageId] = (null, DateTime.UtcNow);
            return null;
        }
        finally
        {
            _latestLock.Release();
        }
    }

    private string ResolveWorkingDirectory(string? solutionDirectory) =>
        !string.IsNullOrWhiteSpace(solutionDirectory) && Directory.Exists(solutionDirectory)
            ? Path.GetFullPath(solutionDirectory)
            : Environment.CurrentDirectory;
}
