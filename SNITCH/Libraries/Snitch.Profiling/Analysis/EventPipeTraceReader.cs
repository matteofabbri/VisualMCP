using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

/// <summary>Every event of a trace, formatted and in time order, plus what the read cost in completeness.</summary>
public sealed record TraceEventSet(
    string Path,
    double DurationMs,
    int EventsLost,
    bool Truncated,
    IReadOnlyList<TraceEventRecord> Events);

public interface ITraceEventReader
{
    TraceEventSet Read(string tracePath, int maxEvents = 200_000);
}

/// <summary>
/// Reads a .nettrace as events, without call stacks, for the analyses that need none: the Events
/// Viewer, the async timeline, the database command list and the instrumented profile.
///
/// It goes through <see cref="TraceLog"/> rather than <c>EventPipeEventSource.AllEvents</c> on purpose.
/// The raw EventPipe dispatch gives the runtime events a numeric identity
/// (<c>Provider(a669021c-…)</c>/<c>EventID(144)</c>) because their names live in the ETW manifest, not
/// in EventPipe metadata; TraceLog registers the parsers that know them, so the same trace reads as
/// <c>Microsoft-Windows-DotNETRuntime/GCStart</c>. EventSource providers (EF Core, the probe, anything
/// custom) get a dynamic template from their own manifest, with their payload names. The ETLX
/// conversion is written to a temp file and deleted; when call stacks are also wanted, the deep
/// <c>TraceStackReader</c> shares the same mechanics.
/// </summary>
public sealed class EventPipeTraceReader : ITraceEventReader
{
    private const int MaxPayloadFields = 32;
    private const int MaxValueChars = 500;

    public TraceEventSet Read(string tracePath, int maxEvents = 200_000)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var limit = Math.Clamp(maxEvents <= 0 ? 200_000 : maxEvents, 1, 2_000_000);
        var events = new List<TraceEventRecord>();
        var truncated = false;
        var lastTime = 0.0;

        var etlxPath = Path.Combine(Path.GetTempPath(), "visualmcp-profiling", $"{Guid.NewGuid():N}.etlx");
        Directory.CreateDirectory(Path.GetDirectoryName(etlxPath)!);

        try
        {
            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
            using var log = TraceLog.OpenOrConvert(etlxPath);
            var source = log.Events.GetSource();

            // Manifests for EventSource providers are parsed into dynamic templates as they are met.
            _ = new DynamicTraceEventParser(source);

            source.AllEvents += data =>
            {
                if (events.Count >= limit)
                {
                    truncated = true;
                    return;
                }

                try
                {
                    var record = Convert(data);
                    events.Add(record);
                    if (record.TimeMs > lastTime) lastTime = record.TimeMs;
                }
                catch
                {
                    // A malformed event must not abort the read: the rest of the trace is still valid.
                }
            };

            source.Process();

            return new TraceEventSet(
                Path.GetFullPath(tracePath),
                Math.Round(lastTime, 1),
                log.EventsLost,
                truncated,
                events);
        }
        finally
        {
            try { if (File.Exists(etlxPath)) File.Delete(etlxPath); } catch { /* best-effort */ }
        }
    }

    private static TraceEventRecord Convert(TraceEvent data)
    {
        var payload = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        string[] names;
        try
        {
            names = data.PayloadNames ?? [];
        }
        catch
        {
            names = [];
        }

        for (var i = 0; i < names.Length && payload.Count < MaxPayloadFields; i++)
        {
            try
            {
                payload[names[i]] = Clamp(data.PayloadString(i));
            }
            catch
            {
                // A payload field the template cannot read is simply absent.
            }
        }

        return new TraceEventRecord(
            Safe(() => data.ProviderName) ?? "UnknownProvider",
            Safe(() => data.EventName) ?? "UnknownEvent",
            data.TimeStampRelativeMSec,
            data.ThreadID,
            data.ActivityID,
            data.RelatedActivityID,
            data.Level.ToString(),
            payload);
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string? Clamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= MaxValueChars ? value : value[..MaxValueChars] + "…";
    }
}
