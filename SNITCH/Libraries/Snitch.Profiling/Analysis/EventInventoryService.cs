using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IEventInventoryService
{
    /// <summary>Every provider/event pair of a trace, with its count and a sample payload.</summary>
    EventInventoryReport Analyze(string tracePath, string? provider, string? eventName, int top);
}

/// <summary>
/// The Events Viewer for EventPipe: it turns any .nettrace into the inventory of what it contains —
/// provider, event name, how often it fired, the payload fields, and one sample event — with a filter
/// on provider or event name. It needs no provider-specific parser, which is the point: a trace
/// collected with <c>profile_collect profile=custom providers=...</c> can be read here before any
/// specialized tool claims to understand it.
/// </summary>
public sealed class EventInventoryService : IEventInventoryService
{
    private readonly ITraceEventReader _reader;

    public EventInventoryService(ITraceEventReader reader) => _reader = reader;

    public EventInventoryReport Analyze(string tracePath, string? provider, string? eventName, int top) =>
        Analyze(_reader.Read(tracePath), provider, eventName, top);

    public static EventInventoryReport Analyze(
        TraceEventSet set, string? provider = null, string? eventName = null, int top = 50)
    {
        var normalizedTop = Math.Clamp(top <= 0 ? 50 : top, 1, 500);
        var providerFilter = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        var eventFilter = string.IsNullOrWhiteSpace(eventName) ? null : eventName.Trim();

        var matching = set.Events
            .Where(record => providerFilter is null ||
                             record.Provider.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(record => eventFilter is null ||
                             record.Name.Contains(eventFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var duration = Math.Max(1, set.DurationMs);
        var groups = matching.GroupBy(record => (record.Provider, record.Name)).ToList();
        var rows = groups
            .Select(group =>
            {
                var first = group.OrderBy(record => record.TimeMs).First();
                var fields = first.Payload.Keys.Take(16).ToList();
                var sample = first.Payload
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Take(8)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                return new TraceEventTypeRow(
                    group.Key.Provider,
                    group.Key.Name,
                    group.LongCount(),
                    Math.Round(group.Min(record => record.TimeMs), 1),
                    Math.Round(group.Max(record => record.TimeMs), 1),
                    Math.Round(group.LongCount() * 1000.0 / duration, 2),
                    first.Level,
                    fields,
                    sample);
            })
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedTop)
            .ToList();

        var warnings = new List<string>();
        if (set.Truncated)
            warnings.Add(
                $"Only the first {set.Events.Count:N0} event(s) were read; the counts are a floor. " +
                "Raise maxEvents or collect a shorter window for the full inventory.");
        if (set.EventsLost > 0)
            warnings.Add($"{set.EventsLost:N0} event(s) were lost by the runtime buffer while the trace was collected.");

        var findings = new List<ProfilingFinding>();
        if (set.Events.Count == 0)
        {
            findings.Add(new ProfilingFinding(
                "events-none", "warning",
                "The trace contains no event",
                "The file is empty, or no provider produced an event in the window.",
                "Collect again with at least one provider: profile_collect profile=custom with an explicit " +
                "providers list, or one of the presets for the question you are asking.",
                "profile_collect profile=custom providers='<Name>:<Keywords>:<Level>'.",
                null));
        }
        else if (rows.Count == 0)
        {
            findings.Add(new ProfilingFinding(
                "events-filtered-out", "info",
                "No event matches the filter",
                $"{set.Events.Count:N0} event(s) are in the trace but none matches provider='{providerFilter}' " +
                $"event='{eventFilter}'.",
                "Drop the filter (or use a substring of the name shown by the unfiltered call) to see what the " +
                "trace actually contains.",
                "profile_events on the same trace without filters.",
                null));
        }

        var nextSteps = findings
            .Select(finding => finding.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        if (nextSteps.Count == 0)
            nextSteps.Add("Pick the event you need from the rows: the payload fields tell you what a provider exposes, and the sample is one real event.");

        return new EventInventoryReport(
            set.Path,
            set.DurationMs,
            set.Events.Count,
            set.EventsLost,
            set.Truncated,
            set.Events.Select(record => record.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            groups.Count,
            rows,
            findings,
            nextSteps,
            warnings);
    }
}
