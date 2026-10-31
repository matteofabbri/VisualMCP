using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Monitor;

public sealed record MonitorStatus(
    bool Installed,
    string? Version,
    bool Running,
    int? ProcessId,
    string? Url,
    string? MetricsUrl,
    string? SettingsPath,
    string? Error)
{
    /// <summary>"native" for the in-process monitor; kept as a field so a client can tell it apart.</summary>
    public string Mode { get; init; } = "native";
}

public sealed record MonitorProbeResult(
    string Url,
    int StatusCode,
    double ElapsedMs,
    string? BodyPreview,
    string? Error);

public interface IDiagnosticsMonitorService
{
    Task<MonitorStatus> StatusAsync(string? solutionDirectory, CancellationToken cancellationToken = default);

    Task<MonitorStatus> StartAsync(
        string solutionDirectory, string? url, CancellationToken cancellationToken = default);

    MonitorStatus Stop();

    Task<MonitorProbeResult> ProbeAsync(string? url, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// The long-running diagnostics sidecar, reimplemented in-process: an HTTP listener on loopback
/// whose routes are the tools of this library. There is no dotnet-monitor binary, no settings
/// file and no child process; the same EventPipe/ClrMD services the MCP tools use answer the
/// requests, so an external client (curl, a dashboard, a script) sees the same numbers.
///
/// Routes are bounded requests, not continuous streams: <c>/metrics?durationSeconds=5</c> samples
/// for that window and answers. The listener is loopback-only and has no authentication, which is
/// the same posture as the previous <c>--no-auth</c> sidecar; do not expose the port.
/// </summary>
public sealed class NativeMonitorService : IDiagnosticsMonitorService
{
    private const string DefaultUrl = "http://127.0.0.1:52323";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProfilingOptions _options;
    private readonly IProfiledProcessService _processes;
    private readonly ICounterCollectionService _counters;
    private readonly ITraceCollectionService _traces;
    private readonly IDumpService _dumps;
    private readonly IManagedHeapService _heap;
    private readonly IManagedStackService _stacks;
    private readonly IFileIoService _fileIo;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LoopbackHttpServer? _server;
    private string? _url;
    private string? _solutionDirectory;
    private DateTime _startedAt;

    public NativeMonitorService(
        ProfilingOptions options,
        IProfiledProcessService processes,
        ICounterCollectionService counters,
        ITraceCollectionService traces,
        IDumpService dumps,
        IManagedHeapService heap,
        IManagedStackService stacks,
        IFileIoService fileIo)
    {
        _options = options;
        _processes = processes;
        _counters = counters;
        _traces = traces;
        _dumps = dumps;
        _heap = heap;
        _stacks = stacks;
        _fileIo = fileIo;
    }

    private static string Version =>
        typeof(NativeMonitorService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(NativeMonitorService).Assembly.GetName().Version?.ToString()
        ?? "native";

    public async Task<MonitorStatus> StatusAsync(string? solutionDirectory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _server is null
                ? new MonitorStatus(true, Version, false, null, null, null, null,
                    "The native monitor is not running. Call profile_monitor action='start'.")
                : Current(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonitorStatus> StartAsync(
        string solutionDirectory, string? url, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server is not null)
                return Current("The native monitor is already running.");

            var listenUrl = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim().TrimEnd('/');
            if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                !uri.IsLoopback ||
                uri.AbsolutePath is not ("" or "/") ||
                !string.IsNullOrEmpty(uri.Query))
            {
                return new MonitorStatus(true, Version, false, null, null, null, null,
                    "The native monitor binds a loopback http URL only, e.g. http://127.0.0.1:52323 " +
                    "(use port 0 to let the OS choose).");
            }

            LoopbackHttpServer server;
            try
            {
                server = new LoopbackHttpServer(IPAddress.Loopback, uri.Port, HandleAsync);
                server.Start();
            }
            catch (SocketException ex)
            {
                return new MonitorStatus(true, Version, false, null, null, null, null,
                    $"Could not bind {listenUrl}: {ex.Message}. Another process is probably using the port; " +
                    "pass url='http://127.0.0.1:0' to pick a free one.");
            }

            _server = server;
            _url = $"http://127.0.0.1:{server.Port}";
            _solutionDirectory = Path.GetFullPath(solutionDirectory);
            _startedAt = DateTime.UtcNow;

            return Current(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public MonitorStatus Stop()
    {
        _gate.Wait();
        try
        {
            if (_server is null)
                return new MonitorStatus(true, Version, false, null, null, null, null,
                    "The native monitor was not running.");

            try { _server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* the listener is already gone */ }

            _server = null;
            _url = null;
            _solutionDirectory = null;
            return new MonitorStatus(true, Version, false, null, null, null, null, "Native monitor stopped.");
        }
        finally
        {
            _gate.Release();
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

    private MonitorStatus Current(string? note) => new(
        true, Version, _server is not null, _server is not null ? Environment.ProcessId : null,
        _url, _url, null,
        note);

    private async Task<HttpResponseInfo> HandleAsync(HttpRequestInfo request, CancellationToken cancellationToken)
    {
        var path = request.Path.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        return path.ToLowerInvariant() switch
        {
            "/" => Html(IndexHtml()),
            "/info" => Json(Info()),
            "/processes" => ProcessList(request.Query.GetValueOrDefault("nameFilter")),
            "/metrics" => await MetricsAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/trace" => await TraceAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/dump" => await DumpAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/heap" => await HeapAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/stacks" => await StacksAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/fileio" => await FileIoAsync(request.Query, cancellationToken).ConfigureAwait(false),
            "/artifact" => Artifact(request.Query),
            "/logs" => Error(501,
                "The native monitor does not collect logs. Enable the logging EventSource providers with " +
                "profile_collect profile='custom' and read them with profile_events."),
            _ => Error(404, $"Unknown route '{request.Path}'. Available: /info, /processes, /metrics, /trace, /dump, /heap, /stacks, /fileio, /artifact."),
        };
    }

    private object Info() => new
    {
        product = "VisualMCP",
        mode = "native",
        version = Version,
        pid = Environment.ProcessId,
        os = Environment.OSVersion.ToString(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        startedAt = _startedAt,
        uptimeSeconds = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 1),
        outputDirectory = _solutionDirectory is null ? null : _options.ResolveOutputDirectory(_solutionDirectory),
        routes = new[]
        {
            "/info", "/processes", "/metrics?pid=&durationSeconds=", "/trace?pid=&profile=&durationSeconds=",
            "/dump?pid=&type=", "/heap?pid=&top=", "/stacks?pid=&maxFrames=",
            "/fileio?pid=&durationSeconds=&pathFilter=", "/artifact?path=",
        },
    };

    private HttpResponseInfo ProcessList(string? nameFilter)
    {
        var list = _processes.List(nameFilter);
        return Json(new { count = list.Count, processes = list });
    }

    private async Task<HttpResponseInfo> MetricsAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!TryPid(query, out var pid, out var error))
            return error!;

        var seconds = Int(query, "durationSeconds", 5, 1, 60);
        var counters = Csv(query.GetValueOrDefault("counters"));
        var providers = Csv(query.GetValueOrDefault("providers"));
        var result = await _counters.CollectAsync(
            pid, counters, seconds, intervalSeconds: 1,
            providerNames: providers is { Count: > 0 } ? providers : null,
            cancellationToken).ConfigureAwait(false);

        return string.Equals(query.GetValueOrDefault("format"), "prometheus", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseInfo(200, "text/plain; version=0.0.4; charset=utf-8",
                Encoding.UTF8.GetBytes(ToPrometheus(result)))
            : Json(result);
    }

    private async Task<HttpResponseInfo> TraceAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!TryPid(query, out var pid, out var error))
            return error!;

        var seconds = Int(query, "durationSeconds", 10, 1, 120);
        var profile = query.GetValueOrDefault("profile") ?? "cpu";
        var requestStackwalk = Bool(query, "requestStackwalk", true);
        var outputPath = query.GetValueOrDefault("outputPath");

        try
        {
            var result = await _traces.CollectAsync(
                pid, outputPath, profile, null, seconds, 256, requestStackwalk, cancellationToken).ConfigureAwait(false);
            return Bool(query, "download", false)
                ? FileResponse(result.Path)
                : Json(new
                {
                    result,
                    downloadUrl = "/artifact?path=" + Uri.EscapeDataString(result.Path),
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(500, ex.Message);
        }
    }

    private async Task<HttpResponseInfo> DumpAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!TryPid(query, out var pid, out var error))
            return error!;

        try
        {
            var result = await _dumps.CollectAsync(pid, query.GetValueOrDefault("outputPath"),
                query.GetValueOrDefault("type") ?? "withheap", cancellationToken).ConfigureAwait(false);
            return Bool(query, "download", false)
                ? FileResponse(result.Path)
                : Json(new
                {
                    result,
                    downloadUrl = "/artifact?path=" + Uri.EscapeDataString(result.Path),
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(500, ex.Message);
        }
    }

    private async Task<HttpResponseInfo> HeapAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!TryPid(query, out var pid, out var error))
            return error!;
        if (pid == Environment.ProcessId)
            return Error(400, "ClrMD cannot attach to the server's own process; profile the application instead.");

        try
        {
            var heap = await _heap.CaptureAsync(
                pid, Int(query, "top", 25, 1, 200), query.GetValueOrDefault("focusType"),
                Bool(query, "includeDuplicateStrings", true), cancellationToken).ConfigureAwait(false);
            return Json(heap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(500, ex.Message);
        }
    }

    private async Task<HttpResponseInfo> StacksAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        if (!TryPid(query, out var pid, out var error))
            return error!;
        if (pid == Environment.ProcessId)
            return Error(400, "ClrMD cannot attach to the server's own process; profile the application instead.");

        try
        {
            var stacks = await _stacks.CaptureAsync(
                pid, Int(query, "maxFrames", 40, 1, 300), Bool(query, "includeRuntimeFrames", false),
                cancellationToken).ConfigureAwait(false);
            return Json(stacks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(500, ex.Message);
        }
    }

    private async Task<HttpResponseInfo> FileIoAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        int? pid = null;
        if (query.TryGetValue("pid", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return Error(400, "pid must be a positive integer.");
            pid = parsed;
        }

        try
        {
            var report = await _fileIo.CollectAsync(
                pid, Int(query, "durationSeconds", 10, 1, 120), query.GetValueOrDefault("pathFilter"),
                Int(query, "top", 25, 1, 200), cancellationToken).ConfigureAwait(false);
            return Json(report);
        }
        catch (PlatformNotSupportedException ex)
        {
            return Error(501, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(403, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(500, ex.Message);
        }
    }

    private HttpResponseInfo Artifact(IReadOnlyDictionary<string, string> query)
    {
        var requested = query.GetValueOrDefault("path");
        if (string.IsNullOrWhiteSpace(requested))
            return Error(400, "path is required.");

        var root = Path.GetFullPath(_options.ResolveOutputDirectory(_solutionDirectory ?? Environment.CurrentDirectory));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var candidate = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(root, requested));

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return Error(403, "Artifacts are served from the profiling output directory only.");
        if (!File.Exists(candidate))
            return Error(404, $"Artifact not found: {candidate}");

        return FileResponse(candidate);
    }

    private static HttpResponseInfo FileResponse(string path) =>
        new(200, "application/octet-stream", FilePath: path, DownloadName: Path.GetFileName(path));

    private static HttpResponseInfo Json(object value, int statusCode = 200) =>
        new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static HttpResponseInfo Error(int statusCode, string message) =>
        Json(new { error = message }, statusCode);

    private static HttpResponseInfo Html(string html) =>
        new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

    private static bool TryPid(IReadOnlyDictionary<string, string> query, out int pid, out HttpResponseInfo? error)
    {
        pid = 0;
        error = null;
        var raw = query.GetValueOrDefault("pid");
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = Error(400, "pid is required.");
            return false;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0)
        {
            error = Error(400, "pid must be a positive integer.");
            return false;
        }
        return true;
    }

    private static int Int(
        IReadOnlyDictionary<string, string> query, string key, int defaultValue, int min, int max)
    {
        if (!query.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }
        return Math.Clamp(value, min, max);
    }

    private static bool Bool(IReadOnlyDictionary<string, string> query, string key, bool defaultValue)
    {
        if (!query.TryGetValue(key, out var raw))
            return defaultValue;
        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    private static IReadOnlyList<string>? Csv(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ToPrometheus(CounterCollectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# VisualMCP native monitor — EventCounters, last value of the window.");
        foreach (var series in result.Series)
        {
            var metric = MetricName($"{series.Provider}_{series.Name}");
            var provider = Escape(series.Provider);
            var counter = Escape(series.Name);
            sb.AppendLine($"# TYPE {metric} gauge");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{metric}{{provider=\"{provider}\",counter=\"{counter}\"}} {series.Last}"));
        }
        return sb.ToString();
    }

    private static string MetricName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private string IndexHtml() => """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>VisualMCP monitor</title>
        <style>
          body{font-family:system-ui,sans-serif;margin:2rem;max-width:60rem;color:#222}
          code{background:#f2f2f2;padding:.1rem .3rem;border-radius:3px}
          li{margin:.35rem 0}
        </style></head>
        <body>
        <h1>VisualMCP native monitor</h1>
        <p>In-process diagnostics over the same EventPipe/ClrMD services the MCP tools use.
        Bounded requests, loopback only, no authentication.</p>
        <ul>
          <li><a href="/info">/info</a> — version, process, uptime, routes</li>
          <li><a href="/processes">/processes</a> — profiled .NET processes</li>
          <li><code>/metrics?pid=1234&amp;durationSeconds=5</code> — EventCounters (<code>format=prometheus</code>)</li>
          <li><code>/trace?pid=1234&amp;profile=cpu&amp;durationSeconds=10</code> — .nettrace</li>
          <li><code>/dump?pid=1234&amp;type=withheap</code> — .dmp</li>
          <li><code>/heap?pid=1234&amp;top=25</code> — managed heap snapshot</li>
          <li><code>/stacks?pid=1234&amp;maxFrames=40</code> — every managed stack</li>
          <li><code>/fileio?pid=1234&amp;durationSeconds=10</code> — who writes where (Windows, elevated)</li>
        </ul>
        </body></html>
        """;
}
