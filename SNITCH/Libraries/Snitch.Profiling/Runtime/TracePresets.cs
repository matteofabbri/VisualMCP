using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace Snitch.Profiling.Runtime;

/// <summary>An EventPipe provider set that answers one class of question.</summary>
/// <remarks>
/// <see cref="ExtraProviders"/> is for the providers that are not the CLR runtime: the task provider
/// the async analysis reads, the data-access providers the Database tool reads, and the probe an
/// instrumented assembly writes to. A provider that no process in the session implements is harmless:
/// EventPipe simply has no events for it.
/// </remarks>
public sealed record TracePreset(
    string Id,
    string Description,
    long RuntimeKeywords,
    EventLevel Level,
    bool SampleProfiler,
    bool RequestStackwalk,
    IReadOnlyList<EventPipeProvider>? ExtraProviders = null);

/// <summary>
/// The keyword sets VisualMCP uses. <c>0x4C14FCCBD</c> is the runtime keyword mask dotnet-trace
/// uses for its sampling profile (GC + GCHandle + Loader + JIT + Exception + Contention +
/// Threading + Type + …); the others are composed from <c>ClrTraceEventParser.Keywords</c> so a
/// reader can see exactly which events a preset turns on.
///
/// Allocation ticks and exception events only carry their call stacks when the session requests
/// stack walks, and allocation ticks are only emitted at Verbose. That is why the gc preset is
/// Verbose with RequestStackwalk: it is what makes allocation-site attribution possible.
/// </summary>
public static class TracePresets
{
    public const long CpuSamplingKeywords = 0x4C14FCCBD;

    // ClrTraceEventParser.Keywords values.
    private const long GC = 0x1;
    private const long GCHandle = 0x2;
    private const long Loader = 0x8;
    private const long Jit = 0x10;
    private const long Contention = 0x4000;
    private const long Exception = 0x8000;
    private const long Threading = 0x10000;
    private const long Type = 0x80000;
    private const long GCHeapSurvivalAndMovement = 0x400000;
    private const long GCHeapAndTypeNames = 0x1000000;
    private const long ThreadTransfer = 0x80000000;

    // Declared before All on purpose: a static initializer runs in textual order, and the database
    // preset reads DatabaseProviders while All is being built.
    /// <summary>The provider the runtime writes its task and await events to.</summary>
    public const string TaskProvider = "System.Threading.Tasks.TplEventSource";

    /// <summary>The provider the instrumenter's probe writes to (see <c>Snitch.Profiling.Probe</c>).</summary>
    public const string ProbeProvider = "Snitch-Profiling-Probe";

    /// <summary>
    /// The data-access EventSources the database preset turns on. A name no library in the process
    /// implements produces no event and costs nothing.
    /// </summary>
    public static IReadOnlyList<string> DatabaseProviders { get; } =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.SqlClient.EventSource",
        "System.Data.SqlClient.EventSource",
        "Npgsql",
        "MySqlConnector",
        "MySql.Data",
        "Microsoft.Data.Sqlite",
        "Oracle.ManagedDataAccess",
        "IBM.Data.DB2",
    ];

    public static IReadOnlyList<TracePreset> All { get; } =
    [
        new("cpu",
            "CPU sampling: sample profiler stacks plus the runtime events needed to resolve method names and GC context.",
            CpuSamplingKeywords, EventLevel.Informational, SampleProfiler: true, RequestStackwalk: true),

        new("gc",
            "Garbage collection and allocations: GC start/stop, heap stats, allocation ticks with type names and call stacks, finalization and thread-pool activity. Verbose, because allocation ticks only exist at that level.",
            GC | GCHandle | Loader | Jit | Type | GCHeapSurvivalAndMovement | GCHeapAndTypeNames | Threading | ThreadTransfer,
            EventLevel.Verbose, SampleProfiler: false, RequestStackwalk: true),

        new("exceptions",
            "Exception flow with throw sites: thrown exceptions carrying their call stacks, plus loader/JIT/type events.",
            Exception | Loader | Jit | Type,
            EventLevel.Informational, SampleProfiler: false, RequestStackwalk: true),

        new("threading",
            "Locking and thread pool: contention, thread-pool adjustments, work-item transfers, exceptions with stacks.",
            Contention | Threading | ThreadTransfer | Exception | Loader,
            EventLevel.Informational, SampleProfiler: false, RequestStackwalk: true),

        new("runtime",
            "The full runtime default keyword set with CPU samples and stack walks: use when you do not know which question to ask yet.",
            CpuSamplingKeywords, EventLevel.Informational, SampleProfiler: true, RequestStackwalk: true),

        new("diagnose",
            "Everything profile_diagnose needs in one trace: CPU samples plus Verbose allocation ticks with call stacks, GC, exceptions and contention. More data than 'runtime'.",
            CpuSamplingKeywords, EventLevel.Verbose, SampleProfiler: true, RequestStackwalk: true),

        new("async",
            "Task and await timeline: System.Threading.Tasks.TplEventSource at every task keyword, so profile_async can pair each await's start and resume and report the exact wait, plus the state machines that never completed.",
            0, EventLevel.Informational, SampleProfiler: false, RequestStackwalk: false,
            [new EventPipeProvider(TaskProvider, EventLevel.Informational, keywords: 0x1FF, arguments: null)]),

        new("database",
            "Data access: the EventSource of EF Core, SqlClient, Npgsql, MySql, SQLite, Oracle and Db2 at Verbose, so profile_database can list commands, durations and repeating shapes.",
            0, EventLevel.Informational, SampleProfiler: false, RequestStackwalk: false,
            DatabaseProviders
                .Select(name => new EventPipeProvider(name, EventLevel.Verbose, keywords: -1, arguments: null))
                .ToArray()),

        new("instrumentation",
            "The probe an assembly rewritten by profile_instrumentation writes to: exact enter/exit events, no sampling. Use it while the instrumented copy runs, then profile_instrumentation action=analyze.",
            0, EventLevel.Informational, SampleProfiler: false, RequestStackwalk: false,
            [new EventPipeProvider(ProbeProvider, EventLevel.Verbose, keywords: -1, arguments: null)]),
    ];

    public static TracePreset? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Builds the EventPipe provider list for a preset or a custom "Provider:Keywords:Level" string.</summary>
public static class TraceProviderBuilder
{
    public const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    public const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    public static (IReadOnlyList<EventPipeProvider> Providers, TracePreset Preset) Build(
        string profile, string? customProviders, bool requestStackwalk = true)
    {
        if (string.Equals(profile, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(customProviders))
                throw new ArgumentException(
                    "profile='custom' needs providers, e.g. 'Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:Informational,Microsoft-DotNETCore-SampleProfiler:0x0:Informational'.");

            var parsed = customProviders
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseProvider)
                .ToArray();

            var customPreset = new TracePreset(
                "custom", "Custom provider list.", 0, EventLevel.Informational,
                SampleProfiler: false, RequestStackwalk: requestStackwalk);
            return (parsed, customPreset);
        }

        var preset = TracePresets.Find(profile)
            ?? throw new ArgumentException(
                $"Unknown profile '{profile}'. Use one of: {string.Join(", ", TracePresets.All.Select(p => p.Id))}, custom.");

        var providers = new List<EventPipeProvider>();
        if (preset.RuntimeKeywords != 0)
            providers.Add(new EventPipeProvider(RuntimeProvider, preset.Level, preset.RuntimeKeywords, arguments: null));

        if (preset.SampleProfiler)
            providers.Add(new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational, keywords: 0, arguments: null));

        if (preset.ExtraProviders is { Count: > 0 } extra)
            providers.AddRange(extra);

        var effectiveStackwalk = preset.RequestStackwalk && requestStackwalk;
        return (providers, preset with { RequestStackwalk = effectiveStackwalk });
    }

    private static EventPipeProvider ParseProvider(string text)
    {
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        var name = parts[0];
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"Invalid provider entry '{text}'.");

        var keywords = 0L;
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            var raw = parts[1];
            keywords = raw is "*" or "all"
                ? unchecked((long)0xFFFFFFFFFFFFFFFF)
                : raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? unchecked((long)Convert.ToUInt64(raw[2..], 16))
                    : long.Parse(raw);
        }

        var level = EventLevel.Informational;
        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
        {
            if (!Enum.TryParse<EventLevel>(parts[2], ignoreCase: true, out level))
                throw new ArgumentException(
                    $"Invalid level '{parts[2]}' in provider '{text}'. Use Critical, Error, Warning, Informational, Verbose or LogAlways.");
        }

        return new EventPipeProvider(name, level, keywords, arguments: null);
    }
}
