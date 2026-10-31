using System.Globalization;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IDatabaseTraceService
{
    /// <summary>What the database did during the trace: commands, shapes, slow statements, connections.</summary>
    DatabaseTraceReport Analyze(string tracePath, int top);
}

/// <summary>
/// The Database tool for EventPipe: it reads the events the data-access stack itself writes —
/// EF Core, SqlClient, Npgsql, MySql, SQLite, Oracle and Db2 all expose an EventSource — and turns
/// them into the list a database profile is read for: which statements ran, how long they took, which
/// shapes repeat (the N+1 signal) and how many connections were opened.
///
/// The payload names differ between providers, so the reader looks for the fields by meaning
/// (<c>CommandText</c>/<c>sql</c>/<c>Statement</c>, <c>Duration</c>/<c>Elapsed</c>) and pairs a
/// start/stop event by command id or activity id when the duration is not a field. Everything it found
/// is reported; when the trace holds no database event at all it says which providers the trace did
/// contain and how to collect it again, instead of returning an empty report as if the database were
/// innocent.
/// </summary>
public sealed class DatabaseTraceService : IDatabaseTraceService
{
    private static readonly string[] TextFields =
    [
        "CommandText", "commandText", "Command", "command", "Sql", "sql", "Statement", "statement",
        "SqlText", "sqlText",
    ];

    private static readonly string[] DurationFields =
    [
        "DurationMs", "durationMs", "DurationMS", "ElapsedMilliseconds", "ExecutionTime",
        "TotalMilliseconds", "Duration", "duration", "Elapsed", "elapsed",
    ];

    private static readonly string[] ConnectionFields =
    [
        "ConnectionId", "connectionId", "ConnectionID", "Connection",
    ];

    private static readonly string[] CommandIdFields =
    [
        "CommandId", "commandId", "CommandID",
    ];

    private static readonly string[] DatabaseProviders =
    [
        "entityframework", "sqlclient", "system.data", "npgsql", "mysql", "sqlite", "oracle", "db2",
    ];

    private readonly ITraceEventReader _reader;

    public DatabaseTraceService(ITraceEventReader reader) => _reader = reader;

    public DatabaseTraceReport Analyze(string tracePath, int top) => Analyze(_reader.Read(tracePath), top);

    public static DatabaseTraceReport Analyze(TraceEventSet set, int top = 15)
    {
        var normalizedTop = Math.Clamp(top <= 0 ? 15 : top, 1, 200);
        var warnings = new List<string>();
        var databaseEvents = set.Events.Where(record => IsDatabaseProvider(record.Provider)).ToList();

        var providersSeen = set.Events
            .GroupBy(record => record.Provider)
            .OrderByDescending(group => group.LongCount())
            .Select(group => group.Key)
            .Take(25)
            .ToList();

        var commands = new List<DatabaseCommandInfo>();
        var pending = new Dictionary<string, TraceEventRecord>(StringComparer.Ordinal);
        var connectionsOpened = 0;

        foreach (var record in databaseEvents)
        {
            var name = EventText.Normalize(record.Name);

            if (name.Contains("connection", StringComparison.Ordinal) && name.Contains("open", StringComparison.Ordinal))
                connectionsOpened++;

            var text = FindCommandText(record);
            var duration = DurationOf(record);
            var connectionId = record.Value(ConnectionFields);
            var commandId = record.Value(CommandIdFields);
            var key = commandId ?? (record.ActivityId != Guid.Empty
                ? record.ActivityId.ToString("N")
                : $"{record.ThreadId}|{name}");

            if (IsStop(name) && pending.Remove(key, out var start))
            {
                var startText = FindCommandText(start) ?? text;
                if (startText is not null)
                {
                    commands.Add(new DatabaseCommandInfo(
                        start.Provider,
                        start.Name,
                        startText,
                        Math.Round(start.TimeMs, 1),
                        Math.Round(duration ?? Math.Max(0, record.TimeMs - start.TimeMs), 3),
                        start.ThreadId,
                        connectionId ?? start.Value(ConnectionFields),
                        commandId ?? start.Value(CommandIdFields)));
                    continue;
                }
            }

            if (text is null) continue;

            if (duration is null)
            {
                // A start event: keep it until its stop arrives.
                pending[key] = record;
                continue;
            }

            commands.Add(new DatabaseCommandInfo(
                record.Provider,
                record.Name,
                text,
                Math.Round(record.TimeMs, 1),
                Math.Round(duration.Value, 3),
                record.ThreadId,
                connectionId,
                commandId));
        }

        var totalCommandMs = commands.Sum(command => command.DurationMs);
        var groups = commands
            .GroupBy(command => EventText.SqlShape(command.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DatabaseCommandGroup(
                group.Key,
                group.First().Text,
                group.Count(),
                Math.Round(group.Sum(command => command.DurationMs), 1),
                Math.Round(group.Average(command => command.DurationMs), 3),
                Math.Round(group.Max(command => command.DurationMs), 1)))
            .OrderByDescending(group => group.TotalMs)
            .ThenByDescending(group => group.Count)
            .Take(normalizedTop)
            .ToList();

        var slowest = commands
            .Where(command => command.DurationMs > 0)
            .OrderByDescending(command => command.DurationMs)
            .Take(normalizedTop)
            .ToList();

        var findings = BuildFindings(
            set.DurationMs, commands.Count, totalCommandMs, slowest, groups, connectionsOpened);

        if (databaseEvents.Count == 0)
        {
            warnings.Add(providersSeen.Count > 0
                ? $"No database provider emitted an event. The trace contains: {string.Join(", ", providersSeen.Take(8))}."
                : "The trace contains no event at all.");
        }

        var nextSteps = findings
            .Select(finding => finding.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new DatabaseTraceReport(
            set.Path,
            set.DurationMs,
            databaseEvents.Count > 0,
            providersSeen,
            databaseEvents.Count,
            commands.Count,
            Math.Round(totalCommandMs, 1),
            connectionsOpened,
            slowest,
            groups,
            findings,
            nextSteps,
            warnings);
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        double durationMs,
        int commandCount,
        double totalCommandMs,
        IReadOnlyList<DatabaseCommandInfo> slowest,
        IReadOnlyList<DatabaseCommandGroup> groups,
        int connectionsOpened)
    {
        var findings = new List<ProfilingFinding>();

        if (commandCount == 0)
        {
            findings.Add(new ProfilingFinding(
                "db-no-commands", "warning",
                "No database command was captured",
                "The data-access provider emitted no command event with readable text in this trace. " +
                "Either the application did not talk to a database in the window, or the provider was not enabled.",
                "Collect with the database preset (EF Core, SqlClient, Npgsql, MySql and SQLite providers are turned on): " +
                "profile_collect profile=database, then run profile_database on the new trace. profile_events lists " +
                "the providers a trace actually contains.",
                "profile_collect profile=database.",
                null));
            return findings;
        }

        var slow = slowest.FirstOrDefault();
        if (slow is not null && (slow.DurationMs >= 500 || (durationMs > 0 && slow.DurationMs >= durationMs * 0.1)))
        {
            findings.Add(new ProfilingFinding(
                "db-slow-command", slow.DurationMs >= 2000 ? "critical" : "warning",
                $"One command took {slow.DurationMs:N0} ms",
                $"{EventText.Truncate(slow.Text, 160)} ({slow.Provider}, {slow.EventName}) at {slow.TimeMs:N0} ms " +
                $"into the trace.",
                "Read the plan for this statement: a missing index, a parameter sniffing plan, or a query that " +
                "returns rows the code then filters in memory all look like this from the application side.",
                $"Open the query in the database tooling: {EventText.Truncate(slow.Text, 120)}",
                null));
        }

        var nPlusOne = groups
            .Where(group => group.Count >= 5 && group.MeanMs < 50 && group.TotalMs >= 200)
            .OrderByDescending(group => group.TotalMs)
            .FirstOrDefault();
        if (nPlusOne is not null)
        {
            findings.Add(new ProfilingFinding(
                "db-n-plus-one", "warning",
                $"The same statement shape ran {nPlusOne.Count:N0} times",
                $"{nPlusOne.Count:N0} executions of `{EventText.Truncate(nPlusOne.Shape, 160)}` totalled " +
                $"{nPlusOne.TotalMs:N0} ms (mean {nPlusOne.MeanMs:0.###} ms, max {nPlusOne.MaxMs:N0} ms). " +
                "Many fast executions of one shape is the signature of a query issued per item of a list.",
                "Batch the query (WHERE ... IN (...)), include the related data in the original query, or cache " +
                "the result. Load the list once and project it, instead of querying inside a loop.",
                "profile_root_cause on a CPU trace of the same operation shows the loop that issues them.",
                null));
        }

        if (durationMs > 0 && totalCommandMs / durationMs >= 0.25)
        {
            findings.Add(new ProfilingFinding(
                "db-time-share", "info",
                "The database accounts for a quarter of the window or more",
                $"{totalCommandMs:N0} ms of command time against a {durationMs:N0} ms window " +
                "({commandCount:N0} commands).",
                "The cost is in the data access, not in the CPU of this process: profile_slowdown ranks it against " +
                "CPU/GC/IO, and profile_root_cause names the code that issued the commands.",
                "profile_slowdown on the same trace.",
                null));
        }

        if (connectionsOpened >= 20)
        {
            findings.Add(new ProfilingFinding(
                "db-connection-churn", "info",
                $"{connectionsOpened:N0} connection opens",
                "Every open costs a handshake and, without pooling, a socket. A high count against a low command " +
                "count means the connections are not being reused.",
                "Keep connections pooled and short-lived, and check that the context is not created per row.",
                "profile_events filtered by the provider, event name 'Connection', to see the pattern.",
                null));
        }

        return findings;
    }

    private static bool IsDatabaseProvider(string provider)
    {
        foreach (var candidate in DatabaseProviders)
        {
            if (provider.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? FindCommandText(TraceEventRecord record)
    {
        foreach (var field in TextFields)
        {
            var value = record.Value(field);
            if (value is not null && EventText.LooksLikeSql(value))
                return EventText.Truncate(value.Trim(), 4000);
        }

        // A provider that puts the statement under a name we do not know: any string field that reads
        // like a statement is better than a missed command.
        foreach (var pair in record.Payload)
        {
            if (pair.Value is not null && pair.Value.Length >= 12 && EventText.LooksLikeSql(pair.Value))
                return EventText.Truncate(pair.Value.Trim(), 4000);
        }

        return null;
    }

    /// <summary>
    /// The duration the provider reported. A number is read as milliseconds; a <c>TimeSpan</c>-formatted
    /// value (EF Core reports <c>Duration</c> that way) is parsed as one; a value with a unit suffix is
    /// understood. Null means the provider did not report it and the caller pairs the start/stop events.
    /// </summary>
    private static double? DurationOf(TraceEventRecord record)
    {
        foreach (var field in DurationFields)
        {
            var text = record.Value(field);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // A number first: TimeSpan.TryParse("123") would read 123 as days.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
                return span.TotalMilliseconds;

            var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c is '.' or ',').ToArray());
            if (double.TryParse(digits.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool IsStop(string normalizedName) =>
        normalizedName.Contains("stop", StringComparison.Ordinal) ||
        normalizedName.Contains("end", StringComparison.Ordinal) ||
        normalizedName.Contains("executed", StringComparison.Ordinal);
}
