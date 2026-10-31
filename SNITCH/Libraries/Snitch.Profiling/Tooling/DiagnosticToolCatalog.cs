namespace Snitch.Profiling.Tooling;

/// <summary>
/// One entry of the dotnet diagnostics toolchain, with the boundary between what this library
/// does itself (through EventPipe / ClrMD) and what it still delegates to the external tool.
/// </summary>
public sealed record DiagnosticToolInfo(
    string Id,
    string Command,
    string PackageId,
    string Purpose,
    string NativeCoverage,
    string DelegatedOperations,
    string DocsUrl);

/// <summary>
/// The seven tools of the dotnet diagnostics toolchain. This table is the single place that
/// knows their package ids and documentation links; both the status service and the tests use it.
/// </summary>
public static class DiagnosticToolCatalog
{
    public static IReadOnlyList<DiagnosticToolInfo> All { get; } =
    [
        new(
            Id: "dotnet-trace",
            Command: "dotnet-trace",
            PackageId: "dotnet-trace",
            Purpose: "Collects EventPipe traces (CPU samples, GC, exceptions, contention, JIT) from a running .NET process.",
            NativeCoverage: "profile_collect starts and stops the EventPipe session itself (presets, custom providers, circular buffer, stackwalk) and writes a real .nettrace; profile_analyze reads that file with TraceEvent.",
            DelegatedOperations: "The 'convert', 'report' and 'list-profiles' verbs and the interactive UI remain with the external tool.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace"),

        new(
            Id: "dotnet-counters",
            Command: "dotnet-counters",
            PackageId: "dotnet-counters",
            Purpose: "Polls EventCounters (CPU, GC, thread pool, allocation rate, ASP.NET Core) and shows them as a live time series.",
            NativeCoverage: "profile_counters runs the same EventPipe/EventCounter session natively and returns the time series plus min/max/mean/last per counter.",
            DelegatedOperations: "The interactive 'monitor' console UI and 'collect' file export are not reproduced.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters"),

        new(
            Id: "dotnet-gcdump",
            Command: "dotnet-gcdump",
            PackageId: "dotnet-gcdump",
            Purpose: "Takes a snapshot of the managed heap: object counts and sizes grouped by type.",
            NativeCoverage: "profile_heap attaches with ClrMD and computes counts, sizes, LOH, pinned segment bytes, finalizable objects and duplicate strings without stopping the process for long.",
            DelegatedOperations: "The .gcdump file format (for Visual Studio / dotnet-gcdump report) is not written; use the external tool when you need that artifact.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-gcdump"),

        new(
            Id: "dotnet-dump",
            Command: "dotnet-dump",
            PackageId: "dotnet-dump",
            Purpose: "Collects and analyses process dumps with SOS commands.",
            NativeCoverage: "profile_dump collects a dump through DiagnosticsClient.WriteDump and analyses an existing dump with ClrMD (threads, stacks, heap, contended monitors).",
            DelegatedOperations: "Ad-hoc SOS commands (clrstack, dumpheap -stat, syncblk, ...) are not reproduced; the analysis answers the common questions directly.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-dump"),

        new(
            Id: "dotnet-stack",
            Command: "dotnet-stack",
            PackageId: "dotnet-stack",
            Purpose: "Prints the managed stacks of all threads of a running process.",
            NativeCoverage: "profile_stacks captures every managed thread stack through ClrMD, resolves frames to source when a PDB is available, and reports contended monitors with the holding thread.",
            DelegatedOperations: "The 'report' verb (a graphviz call graph) is not reproduced.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-stack"),

        new(
            Id: "dotnet-monitor",
            Command: "dotnet-monitor",
            PackageId: "dotnet-monitor",
            Purpose: "A long-running sidecar that exposes diagnostics over HTTP (metrics, dumps, traces).",
            NativeCoverage: "profile_monitor hosts the same sidecar in-process on loopback, with no binary and no child process: /info, /processes, /metrics (JSON or Prometheus), /trace, /dump, /heap, /stacks and /fileio are answered by the EventPipe/ClrMD services of this library.",
            DelegatedOperations: "The dotnet-monitor configuration schema, authentication and its continuous /live and /logs streams are not reproduced; the native monitor answers bounded requests, and /logs points at profile_events with the logging providers.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-monitor"),

        new(
            Id: "dotnet-symbol",
            Command: "dotnet-symbol",
            PackageId: "dotnet-symbol",
            Purpose: "Downloads the PDBs and DWARF/symbol files needed to symbolicate a dump or trace.",
            NativeCoverage: "profile_symbols reads the PE debug directory (CodeView GUID + age), queries the symbol server and caches the PDB next to the module, the same protocol the external tool speaks.",
            DelegatedOperations: "Bulk '--modules' / '--symbols' rescue of every module inside a dump is not reproduced; pass the module paths you need.",
            DocsUrl: "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-symbol"),
    ];

    public static DiagnosticToolInfo? Find(string idOrCommand) =>
        All.FirstOrDefault(t =>
            string.Equals(t.Id, idOrCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Command, idOrCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.PackageId, idOrCommand, StringComparison.OrdinalIgnoreCase));
}
