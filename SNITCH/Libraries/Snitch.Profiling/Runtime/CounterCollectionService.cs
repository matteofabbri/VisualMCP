using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Snitch.Profiling.Model;
using EventLevel = System.Diagnostics.Tracing.EventLevel;

namespace Snitch.Profiling.Runtime;

public interface ICounterCollectionService
{
    Task<CounterCollectionResult> CollectAsync(
        int processId, IReadOnlyList<string>? counterFilter, int durationSeconds, int intervalSeconds,
        IReadOnlyList<string>? providerNames = null, CancellationToken cancellationToken = default);

    IReadOnlyList<string> DefaultProviders { get; }
}

/// <summary>
/// Native replacement for <c>dotnet-counters collect</c>: it opens an EventPipe session, enables
/// the EventCounter providers and parses the "EventCounters" events as they arrive, producing the
/// same time series the external tool prints. No external process is spawned.
/// </summary>
public sealed class CounterCollectionService : ICounterCollectionService
{
    /// <summary>
    /// EventCounter providers enabled by default. Unknown providers are ignored by the runtime,
    /// so this list is safe for a console app, an ASP.NET Core app and a gRPC service alike.
    /// </summary>
    public IReadOnlyList<string> DefaultProviders { get; } =
    [
        "System.Runtime",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft-AspNetCore-Server-Kestrel",
        "System.Net.Http",
        "System.Net.Sockets",
        "Grpc.AspNetCore.Server",
    ];

    private readonly ProfilingOptions _options;

    public CounterCollectionService(ProfilingOptions options) => _options = options;

    public async Task<CounterCollectionResult> CollectAsync(
        int processId, IReadOnlyList<string>? counterFilter, int durationSeconds, int intervalSeconds,
        IReadOnlyList<string>? providerNames = null, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        var seconds = Math.Clamp(durationSeconds <= 0 ? _options.DefaultDurationSeconds : durationSeconds,
            1, _options.MaxDurationSeconds);
        var interval = Math.Clamp(intervalSeconds <= 0 ? 1 : intervalSeconds, 1, 10);
        var providers = (providerNames is { Count: > 0 } ? providerNames : DefaultProviders)
            .Select(name => new EventPipeProvider(
                name,
                EventLevel.Informational,
                keywords: 0,
                arguments: new Dictionary<string, string>
                {
                    ["EventCounterIntervalSec"] = interval.ToString(CultureInfo.InvariantCulture),
                }))
            .ToArray();

        var client = new DiagnosticsClient(processId);
        EventPipeSession session;
        try
        {
            session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 64);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start a counter session on process {processId}: {ex.Message}. " +
                "Check that the process is a live .NET process with diagnostics enabled.", ex);
        }

        var sink = new CounterSink(counterFilter);
        var stopwatch = Stopwatch.StartNew();

        using (session)
        {
            using var source = new EventPipeEventSource(session.EventStream);
            source.Dynamic.AddCallbackForProviderEvents(
                (_, eventName) => string.Equals(eventName, "EventCounters", StringComparison.Ordinal)
                    ? EventFilterResponse.AcceptEvent
                    : EventFilterResponse.RejectEvent,
                sink.OnCounter);

            var processing = Task.Run(() => source.Process(), CancellationToken.None);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { session.Stop(); } catch { /* the target may have exited */ }
                try { await processing.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); } catch { /* stopped stream */ }
            }
        }

        stopwatch.Stop();
        var series = sink.Build();
        var warnings = new List<string>();
        if (sink.ParseFailures > 0)
            warnings.Add(
                $"{sink.ParseFailures} EventCounter event(s) could not be read (payload fields unavailable); " +
                "the counters shown are the ones that parsed." +
                (sink.FirstFailureShape is null ? "" : $" First event: {sink.FirstFailureShape}"));
        if (series.Count == 0)
            warnings.Add(
                "No EventCounters were produced. The providers may not exist in this application, or the process " +
                "may have diagnostics disabled. Try profile='custom' providers on profile_counters, or collect a trace first.");

        var processName = SafeProcessName(processId);
        var seen = sink.Providers.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = providers.Select(p => p.Name).Where(p => !sink.Providers.Contains(p)).ToList();

        return new CounterCollectionResult(
            processId, processName, stopwatch.Elapsed.TotalMilliseconds, interval,
            providers.Select(p => p.Name).ToList(),
            series,
            missing,
            warnings);
    }

    private static string SafeProcessName(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.ProcessName; }
        catch { return "process"; }
    }

    /// <summary>Accumulates EventCounter payloads; one builder per (provider, counter name).</summary>
    private sealed class CounterSink
    {
        private readonly IReadOnlyList<string>? _filter;
        private readonly Dictionary<string, SeriesBuilder> _builders = new(StringComparer.OrdinalIgnoreCase);

        public CounterSink(IReadOnlyList<string>? filter) => _filter = filter;

        public HashSet<string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ParseFailures { get; private set; }

        public string? FirstFailureShape { get; private set; }

        public void OnCounter(TraceEvent data)
        {
            Providers.Add(data.ProviderName);

            var payload = ReadPayload(data);
            if (payload is null)
            {
                ParseFailures++;
                FirstFailureShape ??= DescribeShape(data);
                return;
            }

            if (_filter is { Count: > 0 } &&
                !_filter.Any(f => payload.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return;

            if (double.IsNaN(payload.Value))
            {
                ParseFailures++;
                return;
            }

            var key = data.ProviderName + "|" + payload.Name;
            if (!_builders.TryGetValue(key, out var builder))
            {
                builder = new SeriesBuilder(
                    data.ProviderName, payload.Name, payload.DisplayName, payload.Unit, payload.CounterType);
                _builders[key] = builder;
            }

            builder.Add(new CounterPoint(data.TimeStampRelativeMSec, payload.Value));
        }

        /// <summary>
        /// EventPipe delivers a System.Runtime counter as one EventCounters event whose payload is
        /// a single JSON document (<c>{"Payload":{"Name":"cpu-usage","Mean":12.5,...}}</c>). When
        /// TraceEvent has a parsed template the fields are available by name; otherwise the raw
        /// document is the only source, and its numbers may use the current culture's decimal
        /// separator, so they are read with a tolerant regex rather than JsonDocument.
        /// </summary>
        private static CounterPayload? ReadPayload(TraceEvent data)
        {
            var name = AsString(data, "Name");
            if (!string.IsNullOrWhiteSpace(name))
                return new CounterPayload(
                    name, AsString(data, "DisplayName"), AsDouble(data, "Mean"),
                    AsString(data, "DisplayUnits") ?? AsString(data, "Unit"), AsString(data, "CounterType"));

            object? raw = null;
            try { raw = data.PayloadValue(0); } catch { /* no payload */ }
            return FromPayloadObject(raw);
        }

        private static CounterPayload? FromPayloadObject(object? raw)
        {
            switch (raw)
            {
                case null:
                    return null;

                case string text:
                    return FromJson(text);

                case IDictionary<string, object> dictionary:
                {
                    var payload = dictionary.TryGetValue("Payload", out var nested) ? nested : dictionary;
                    if (payload is string json)
                        return FromJson(json);
                    if (payload is IDictionary<string, object> map)
                        return FromDictionary(map);
                    return FromReflection(payload);
                }
            }

            return FromReflection(raw);
        }

        private static CounterPayload? FromDictionary(IDictionary<string, object> map)
        {
            var name = ReadString(map, "Name");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return new CounterPayload(
                name,
                ReadString(map, "DisplayName"),
                ReadDouble(map, "Mean"),
                ReadString(map, "DisplayUnits") ?? ReadString(map, "Unit"),
                ReadString(map, "CounterType"));
        }

        /// <summary>Fallback for a strongly-typed payload: read Name/Mean/... properties by reflection.</summary>
        private static CounterPayload? FromReflection(object? payload)
        {
            if (payload is null)
                return null;

            var type = payload.GetType();
            var name = type.GetProperty("Name")?.GetValue(payload) as string
                       ?? type.GetProperty("Name")?.GetValue(payload)?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            object? Property(string property) =>
                type.GetProperty(property)?.GetValue(payload);

            static double ToDouble(object? value) => value switch
            {
                null => double.NaN,
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => double.NaN,
            };

            return new CounterPayload(
                name,
                Property("DisplayName")?.ToString(),
                ToDouble(Property("Mean")),
                Property("DisplayUnits")?.ToString() ?? Property("Unit")?.ToString(),
                Property("CounterType")?.ToString());
        }

        private static CounterPayload? FromJson(string json)
        {
            var jsonName = JsonString(json, "Name");
            if (string.IsNullOrWhiteSpace(jsonName))
                return null;

            return new CounterPayload(
                jsonName,
                JsonString(json, "DisplayName"),
                JsonNumber(json, "Mean"),
                JsonString(json, "DisplayUnits") ?? JsonString(json, "Unit"),
                JsonString(json, "CounterType"));
        }

        private static string? ReadString(IDictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) ? value as string ?? value?.ToString() : null;

        private static double ReadDouble(IDictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value)
                ? value switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => double.NaN,
                }
                : double.NaN;

        private static string? JsonString(string json, string field)
        {
            try
            {
                var match = Regex.Match(json, $@"""{Regex.Escape(field)}""\s*:\s*""((?:[^""\\]|\\.)*)""");
                return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
            }
            catch
            {
                return null;
            }
        }

        private static double JsonNumber(string json, string field)
        {
            try
            {
                var match = Regex.Match(json, $@"""{Regex.Escape(field)}""\s*:\s*(-?[0-9]+(?:[.,][0-9]+)?(?:[eE][+-]?[0-9]+)?)");
                if (!match.Success)
                    return double.NaN;
                var text = match.Groups[1].Value.Replace(',', '.');
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN;
            }
            catch
            {
                return double.NaN;
            }
        }

        public IReadOnlyList<CounterSeries> Build() =>
            _builders.Values
                .Select(b => b.Build())
                .OrderBy(s => s.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string DescribeShape(TraceEvent data)
        {
            try
            {
                var names = data.PayloadNames is { Length: > 0 } ? string.Join(",", data.PayloadNames) : "(none)";
                var value = "(none)";
                try
                {
                    var raw = data.PayloadValue(0);
                    value = raw is null ? "null" : $"{raw.GetType().FullName}: {raw}";
                }
                catch { value = "?"; }
                return $"{data.ProviderName}/{data.EventName} fields=[{names}] value=[{value}]";
            }
            catch (Exception ex)
            {
                return $"payload shape unavailable: {ex.Message}";
            }
        }

        private sealed record CounterPayload(
            string Name, string? DisplayName, double Value, string? Unit, string? CounterType);

        private static string? AsString(TraceEvent data, string field)
        {
            try { return data.PayloadByName(field) as string ?? data.PayloadByName(field)?.ToString(); }
            catch { return null; }
        }

        private static double AsDouble(TraceEvent data, string field)
        {
            try
            {
                var raw = data.PayloadByName(field);
                return raw switch
                {
                    null => double.NaN,
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => double.NaN,
                };
            }
            catch
            {
                return double.NaN;
            }
        }
    }

    private sealed class SeriesBuilder
    {
        private readonly string _provider;
        private readonly string _name;
        private readonly string? _displayName;
        private readonly string? _unit;
        private readonly string? _counterType;
        private readonly List<CounterPoint> _points = new();

        public SeriesBuilder(string provider, string name, string? displayName, string? unit, string? counterType)
        {
            _provider = provider;
            _name = name;
            _displayName = displayName;
            _unit = unit;
            _counterType = counterType;
        }

        public void Add(CounterPoint point) => _points.Add(point);

        public CounterSeries Build()
        {
            var start = _points.Count > 0 ? _points[0].TimeMs : 0;
            var normalized = _points.Select(p => p with { TimeMs = p.TimeMs - start }).ToList();
            var values = normalized.Select(p => p.Value).ToList();

            return new CounterSeries(
                _provider, _name, _displayName, _unit, _counterType,
                values.Count == 0 ? 0 : values.Min(),
                values.Count == 0 ? 0 : values.Max(),
                values.Count == 0 ? 0 : values.Average(),
                values.Count == 0 ? 0 : values[^1],
                normalized.Count,
                normalized);
        }
    }
}
