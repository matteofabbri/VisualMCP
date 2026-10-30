using System.Diagnostics;
using System.Text.Json;
using Snitch.Profiling.Tooling;

namespace Snitch.Profiling.Monitor;

public sealed record MonitorStatus(
    bool Installed,
    string? Version,
    bool Running,
    int? ProcessId,
    string? Url,
    string? MetricsUrl,
    string? SettingsPath,
    string? Error);

public sealed record MonitorProbeResult(
    string Url,
    int StatusCode,
    double ElapsedMs,
    string? BodyPreview,
    string? Error);

public interface IDotnetMonitorService
{
    Task<MonitorStatus> StatusAsync(string? solutionDirectory, CancellationToken cancellationToken = default);

    Task<MonitorStatus> StartAsync(
        string solutionDirectory, string? url, string? configPath, CancellationToken cancellationToken = default);

    MonitorStatus Stop();

    Task<MonitorProbeResult> ProbeAsync(string? url, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wrapper around <c>dotnet-monitor</c>, the one tool of the seven that is a service rather than an
/// operation: VisualMCP starts it locally (no auth, loopback only), generates its settings file,
/// probes its HTTP endpoints and stops it. Profiling itself stays native in this library.
/// </summary>
public sealed class DotnetMonitorService : IDotnetMonitorService
{
    private const string DefaultUrl = "http://127.0.0.1:52323";
    private const string DefaultMetricsUrl = "http://127.0.0.1:52325";

    private readonly ProfilingOptions _options;
    private readonly IDiagnosticToolService _tools;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly object _gate = new();

    private Process? _process;
    private int? _processId;
    private string? _url;
    private string? _metricsUrl;
    private string? _settingsPath;

    public DotnetMonitorService(ProfilingOptions options, IDiagnosticToolService tools)
    {
        _options = options;
        _tools = tools;
    }

    public async Task<MonitorStatus> StatusAsync(string? solutionDirectory, CancellationToken cancellationToken = default)
    {
        var resolution = await _tools.ResolveAsync("dotnet-monitor", solutionDirectory, cancellationToken).ConfigureAwait(false);
        var running = _process is { HasExited: false };

        return new MonitorStatus(
            resolution.Installed,
            resolution.Version,
            running,
            running ? _processId : null,
            running ? _url : null,
            running ? _metricsUrl : null,
            running ? _settingsPath : null,
            running ? null : "dotnet-monitor is not running in this server process.");
    }

    public async Task<MonitorStatus> StartAsync(
        string solutionDirectory, string? url, string? configPath, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                return new MonitorStatus(true, null, true, _processId, _url, _metricsUrl, _settingsPath,
                    "dotnet-monitor is already running.");
        }

        var resolution = await _tools.ResolveAsync("dotnet-monitor", solutionDirectory, cancellationToken).ConfigureAwait(false);
        if (!resolution.Installed)
            return new MonitorStatus(false, null, false, null, null, null, null,
                "dotnet-monitor is not installed. Run profile_tools with action='install' and tool='dotnet-monitor'.");

        if (!resolution.IsLocalTool && resolution.CommandPath is null)
            return new MonitorStatus(true, resolution.Version, false, null, null, null, null,
                "dotnet-monitor is listed but its command shim could not be located. Install it again with " +
                "profile_tools action='install' tool='dotnet-monitor'.");

        var outputDirectory = _options.ResolveOutputDirectory(solutionDirectory);
        Directory.CreateDirectory(outputDirectory);

        var listenUrl = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.TrimEnd('/');
        var metricsUrl = BuildMetricsUrl(listenUrl);
        var settingsPath = string.IsNullOrWhiteSpace(configPath)
            ? WriteDefaultSettings(outputDirectory)
            : Path.GetFullPath(configPath);

        var arguments =
            $"collect --no-auth --urls {listenUrl} --metricUrls {metricsUrl} --configuration-file-path \"{settingsPath}\"";

        var executable = resolution.IsLocalTool ? _options.DotnetExecutable : resolution.CommandPath!;
        var argumentText = resolution.IsLocalTool ? $"tool run dotnet-monitor {arguments}" : arguments;

        var psi = new ProcessStartInfo(executable, argumentText)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = resolution.WorkingDirectory ?? solutionDirectory,
        };

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();
            try { process.StandardInput.Close(); } catch { /* best-effort */ }
        }
        catch (Exception ex)
        {
            return new MonitorStatus(true, resolution.Version, false, null, null, null, settingsPath,
                $"Could not start dotnet-monitor: {ex.Message}");
        }

        lock (_gate)
        {
            _process = process;
            _processId = process.Id;
            _url = listenUrl;
            _metricsUrl = metricsUrl;
            _settingsPath = settingsPath;
        }

        // Give the server a moment: if the arguments are wrong it exits immediately and the
        // stderr is the only useful thing we can return.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            var error = await ReadRemainingErrorAsync(process).ConfigureAwait(false);
            lock (_gate)
            {
                _process = null;
                _processId = null;
            }
            return new MonitorStatus(true, resolution.Version, false, null, null, null, settingsPath,
                $"dotnet-monitor exited immediately (code {process.ExitCode}). {error}");
        }

        var probe = await ProbeAsync(listenUrl, "/info", cancellationToken).ConfigureAwait(false);
        return new MonitorStatus(
            true, resolution.Version, true, process.Id, listenUrl, metricsUrl, settingsPath,
            probe.Error is null ? null : $"Started, but /info answered: {probe.Error}");
    }

    public MonitorStatus Stop()
    {
        lock (_gate)
        {
            if (_process is null || _process.HasExited)
            {
                _process = null;
                _processId = null;
                return new MonitorStatus(true, null, false, null, null, null, _settingsPath,
                    "dotnet-monitor was not running.");
            }

            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { _process.Dispose(); } catch { /* best-effort */ }
            _process = null;
            _processId = null;
            return new MonitorStatus(true, null, false, null, null, null, _settingsPath, "dotnet-monitor stopped.");
        }
    }

    public async Task<MonitorProbeResult> ProbeAsync(
        string? url, string path, CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(url) ? _url ?? DefaultUrl : url.TrimEnd('/');
        var relative = path.StartsWith('/') ? path : "/" + path;
        var target = baseUrl + relative;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _http.GetAsync(target, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new MonitorProbeResult(
                target, (int)response.StatusCode, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                body.Length <= 2000 ? body : body[..2000] + "…", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new MonitorProbeResult(target, 0, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), null, ex.Message);
        }
    }

    private static string BuildMetricsUrl(string listenUrl) =>
        listenUrl.Contains(":52323", StringComparison.Ordinal)
            ? listenUrl.Replace(":52323", ":52325", StringComparison.Ordinal)
            : DefaultMetricsUrl;

    private static string WriteDefaultSettings(string outputDirectory)
    {
        var settingsPath = Path.Combine(outputDirectory, "monitor-settings.json");
        var dumps = Path.Combine(outputDirectory, "dumps");
        Directory.CreateDirectory(dumps);

        var settings = new
        {
            Storage = new { DumpPath = dumps },
            Dumps = new { Enabled = true, DumpType = "WithHeap" },
            Metrics = new { Enabled = true },
        };

        File.WriteAllText(settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settingsPath;
    }

    private static async Task<string> ReadRemainingErrorAsync(Process process)
    {
        try
        {
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var text = (error + "\n" + output).Trim();
            return text.Length == 0 ? "" : text.Length > 3000 ? text[..3000] + "…" : text;
        }
        catch
        {
            return "";
        }
    }
}
