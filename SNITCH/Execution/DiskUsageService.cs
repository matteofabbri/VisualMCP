using System.Diagnostics;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// Answers "where is the disk going?" and "how do I keep the next build from writing there?".
///
/// <para><b>report</b> measures the folders a .NET session actually grows — the process TEMP, the NuGet
/// caches, the .NET CLI home, the tool installs (Ghidra, netcoredbg), the MCP log, the solution store and,
/// with <c>deep</c>, the workspace's own bin/obj folders — and returns each with its absolute path, bytes,
/// file count, risk and the clean target that would remove it.</para>
///
/// <para><b>clean</b> deletes one of those targets. The dry run is the default, and a real deletion needs
/// <c>dryRun=false</c> plus <c>confirm=true</c>; nothing outside a known target's root is ever touched.
/// Stale temp entries are filtered by age so an in-flight build is not pulled out from under itself.</para>
///
/// <para><b>guard</b> is the prevention half: it redirects TEMP/TMP, NUGET_PACKAGES, the NuGet HTTP
/// cache, DOTNET_CLI_HOME and DOTNET_BUNDLE_EXTRACT_BASE_DIR under a root on another drive, applies them
/// to this process (every child inherits them) and persists the policy in
/// <c>&lt;workspace&gt;/.visualMCP/disk-policy.json</c> so the next start keeps them.</para>
/// </summary>
public interface IDiskUsageService
{
    /// <summary>
    /// Runs one action. <paramref name="includeWellKnown"/> is true for the tool; a test or an embedder
    /// that only wants the paths it names passes false.
    /// </summary>
    Task<object> RunAsync(
        string action = "report",
        string[]? roots = null,
        bool deep = false,
        int top = 10,
        string[]? targets = null,
        double olderThanHours = 24,
        bool dryRun = true,
        bool confirm = false,
        bool apply = false,
        string? root = null,
        int timeoutSeconds = 20,
        bool includeWellKnown = true,
        CancellationToken cancellationToken = default);
}

/// <summary>The report/clean/guard implementation. One instance, no state beyond the policy it writes.</summary>
public sealed class DiskUsageService : IDiskUsageService
{
    private static readonly string[] StaleTempPrefixes =
        ["vmcp-", "visualmcp-", "mcp-", "testhost", "MSBuildTemp", "VBCSCompiler", "Roslyn", ".net"];

    private static readonly string[] SafeCleanTargets =
    [
        "temp-stale", "nuget-http-cache", "nuget-plugins-cache", "nuget-temp",
        "crash-dumps", "windows-temp", "wer", "mcp-log",
        "workspace-profiling", "workspace-binlogs", "workspace-temp", "test-results", "artifacts",
        "policy-root",
    ];

    private static readonly string[] ReclaimExtraCleanTargets =
    [
        "nuget-packages", "visualmcp-tools", "workspace-decompiled", "workspace-symbols",
        "solution-build-output",
    ];

    private static readonly string[] MechanismNotes =
    [
        "A dotnet build writes MSBuild temp, the compiler server's temp and (on test) testhost under TEMP; " +
        "a restore writes packages, the HTTP cache and NuGet scratch under the user profile.",
        "TEMP, TMP, NUGET_PACKAGES, NUGET_HTTP_CACHE_PATH, DOTNET_CLI_HOME and DOTNET_BUNDLE_EXTRACT_BASE_DIR " +
        "are inherited by every child process, so redirecting them once covers builds, tests, git and shells.",
        "This server's own temporary files use Path.GetTempPath(), so the same redirect moves them too.",
    ];

    private readonly IRoslynWorkspace _workspace;

    /// <summary>Creates the service over the active solution (or the working directory when none is loaded).</summary>
    public DiskUsageService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <inheritdoc/>
    public async Task<object> RunAsync(
        string action = "report",
        string[]? roots = null,
        bool deep = false,
        int top = 10,
        string[]? targets = null,
        double olderThanHours = 24,
        bool dryRun = true,
        bool confirm = false,
        bool apply = false,
        string? root = null,
        int timeoutSeconds = 20,
        bool includeWellKnown = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return action?.Trim().ToLowerInvariant() switch
            {
                null or "" or "report" or "scan" => await ReportAsync(roots, deep, top, includeWellKnown, timeoutSeconds, cancellationToken),
                "clean" => await CleanAsync(targets, olderThanHours, dryRun, confirm, root, timeoutSeconds, cancellationToken),
                "guard" => Guard(root, apply),
                _ => new
                {
                    error = $"unknown action '{action}'",
                    actions = new[] { "report", "clean", "guard" },
                    hint = "report measures the folders; clean removes one target; guard redirects TEMP/NuGet/.NET CLI state.",
                },
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new { error = ex.Message, errorType = ex.GetType().Name, action };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  report
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<object> ReportAsync(
        string[]? extraRoots, bool deep, int top, bool includeWellKnown, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var budgetSeconds = Math.Clamp(timeoutSeconds, 5, 300);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

        var targets = await BuildTargetsAsync(extraRoots, includeWellKnown, deep, cancellationToken);
        var scans = new FolderScan?[targets.Count];

        // Four at a time: a size walk is I/O-bound, and one huge cache must not serialize the report.
        Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            if (budget.IsCancellationRequested) return;
            scans[index] = Scan(targets[index], budget.Token, deep, top);
        });

        var measured = scans.Where(scan => scan is not null).Select(scan => scan!).ToList();
        var byId = measured.ToDictionary(scan => scan.Target.Id, scan => scan, StringComparer.OrdinalIgnoreCase);
        var drives = ReadDrives();
        var system = drives.FirstOrDefault(drive => drive.IsSystem);
        var tempPath = Path.GetTempPath();
        var workspaceRoot = ResolveWorkspaceRoot();
        var policy = DiskPolicy.Current;
        var findings = BuildFindings(measured, byId, drives, system, tempPath, policy, workspaceRoot);
        var partial = budget.IsCancellationRequested || measured.Any(scan => scan.Partial);

        return new
        {
            action = "report",
            generatedUtc = DateTime.UtcNow.ToString("O"),
            elapsedMs = started.ElapsedMilliseconds,
            partial,
            timeoutSeconds = budgetSeconds,
            drives = drives.Select(ShapeDrive).ToArray(),
            systemDrive = system is null ? null : new
            {
                path = system.Name,
                freeBytes = system.FreeBytes,
                totalBytes = system.TotalBytes,
                freeGB = RoundGb(system.FreeBytes),
                freePercent = system.TotalBytes == 0 ? 100 : Math.Round(system.FreeBytes * 100.0 / system.TotalBytes, 1),
            },
            workspace = workspaceRoot,
            temp = new
            {
                path = tempPath,
                drive = Path.GetPathRoot(tempPath),
                onSystemDrive = SameRoot(Path.GetPathRoot(tempPath), SystemDriveRoot()),
                fromPolicy = policy.Enabled,
            },
            policy = new
            {
                active = policy.Enabled,
                root = policy.Root,
                policyPath = policy.PolicyPath ?? DiskPolicy.FindPolicyFile(workspaceRoot),
                variables = policy.Variables,
                currentEnvironment = DiskPolicy.ManagedVariables.ToDictionary(
                    name => name, name => Environment.GetEnvironmentVariable(name)),
            },
            folders = measured
                .OrderByDescending(scan => scan.Bytes)
                .Select(scan => ShapeFolder(scan, deep))
                .ToArray(),
            findings,
            mechanism = MechanismNotes,
            next = new
            {
                guard = "disk_usage action=guard root=\"<drive>:\\vmcp-tmp\" apply=true — move TEMP, NuGet caches and .NET CLI state there",
                clean = "disk_usage action=clean targets=[\"temp-stale\"] dryRun=false confirm=true — remove stale temp entries",
            },
            notes = new[]
            {
                "Sizes are measured now; a build that is running when this call arrives is included in the numbers.",
                "A folder marked partial hit the time budget: its number is what was counted before the budget ran out.",
                "Nothing under a target root is touched by clean without dryRun=false and confirm=true.",
            },
        };
    }

    private async Task<List<ScanTarget>> BuildTargetsAsync(
        string[]? extraRoots, bool includeWellKnown, bool deep, CancellationToken cancellationToken)
    {
        var targets = new List<ScanTarget>();

        if (includeWellKnown)
        {
            var temp = Path.GetTempPath();
            Add(targets, "temp", "Build and tool temp (TEMP/TMP)", "temp", temp, false, "safe", "temp-stale",
                "MSBuild temp, testhost, NuGet scratch and this server's own temp files land here.");

            var activePolicy = DiskPolicy.Current;
            if (activePolicy.Enabled && activePolicy.Root is { Length: > 0 } policyRoot)
                Add(targets, "policy-root", "Disk policy root (redirected data and earlier leftovers)", "temp",
                    policyRoot, false, "safe", "policy-root",
                    "Everything the policy redirects, plus anything earlier sessions left directly here. Clean keeps the redirected subfolders.");

            if (OperatingSystem.IsWindows())
            {
                var localTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
                if (!SamePath(localTemp, temp))
                    Add(targets, "temp-legacy", "Local\\Temp (a previous temp root)", "temp", localTemp, false, "safe", "temp-legacy",
                        "Residue from before TEMP was redirected still lives here.");

                Add(targets, "windows-temp", "Windows temp", "temp",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), false, "safe", "windows-temp",
                    "System temp; clearing it may need elevation.");

                Add(targets, "crash-dumps", "Crash dumps", "diagnostics",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                    false, "safe", "crash-dumps", "Process dumps from crashed applications.");

                Add(targets, "wer", "Windows Error Reporting queue", "diagnostics",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Windows", "WER"),
                    false, "safe", "wer", "Crash reports; clearing the queue may need elevation.");
            }

            var nuget = await NuGetLocalsAsync(cancellationToken);
            AddNuGetTarget(targets, nuget, "global-packages", "nuget-packages", "NuGet global packages",
                "Every restored package; deleting it forces a re-download on the next restore.", "reinstall");
            AddNuGetTarget(targets, nuget, "http-cache", "nuget-http-cache", "NuGet HTTP cache",
                "Downloaded package responses; safe to clear, restore re-fetches only what it needs.", "safe");
            AddNuGetTarget(targets, nuget, "temp", "nuget-temp", "NuGet scratch",
                "Restore scratch space; safe to clear.", "safe");
            AddNuGetTarget(targets, nuget, "plugins-cache", "nuget-plugins-cache", "NuGet plugins cache",
                "Credential providers and plugin bits; safe to clear.", "safe");

            if (nuget.Count == 0)
            {
                Add(targets, "nuget-packages", "NuGet global packages", "nuget",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                    false, "reinstall", "nuget-packages",
                    "Every restored package; the dotnet CLI did not report the location, so this is the default.");
            }

            Add(targets, "dotnet-home", ".NET CLI home", "dotnet",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
                false, "config", null, "CLI state and installed workloads; report only (deleting workloads breaks builds).");

            Add(targets, "visualmcp-tools", "VisualMCP tools (Ghidra, netcoredbg)", "tools",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualMCP", "tools"),
                false, "reinstall", "visualmcp-tools",
                "Downloaded on demand; deleting it makes the next analyze_native_binary/debug_install fetch again.");

            Add(targets, "mcp-log", "VisualMCP debug log", "logs",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "mcp-servers", "VisualMCP", "debug.log"),
                true, "safe", "mcp-log", "Every request and tool call; truncating it keeps the file, clears the size.");

            Add(targets, "zcode-history", "ZCode history and databases", "history",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode"),
                false, "history", null, "Conversation history; report only — deleting it loses sessions.");

            Add(targets, "claude-config", "Claude client configuration", "config",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
                false, "config", null, "Client configuration and project memory; report only.");

            if (OperatingSystem.IsWindows())
            {
                Add(targets, "vs-cache", "Visual Studio caches", "ide",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio"),
                    false, "config", null, "Component and Roslyn caches; report only (Visual Studio manages them).");
            }

            AddWorkspaceTargets(targets, ResolveWorkspaceRoot(), deep);
        }

        if (extraRoots is not null)
        {
            foreach (var raw in extraRoots)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }

                targets.Add(new ScanTarget(
                    "custom-" + targets.Count, raw, "custom", full, File.Exists(full) && !Directory.Exists(full),
                    "user", null, "Caller-supplied path; reported, never deleted by this tool."));
            }
        }

        return targets;
    }

    private static void AddWorkspaceTargets(List<ScanTarget> targets, string workspaceRoot, bool deep)
    {
        var store = Path.Combine(workspaceRoot, ".visualMCP");
        Add(targets, "workspace-store", ".visualMCP (solution store)", "solution-store", store, false, "derived", null,
            "Profiling traces, binlogs, decompiled code, the symbol cache and the SQLite store. The subfolders below have their own targets.");
        Add(targets, "workspace-profiling", ".visualMCP/profiling (traces, dumps, reports)", "solution-store",
            Path.Combine(store, "profiling"), false, "safe", "workspace-profiling", "Profiling artifacts; re-collected when needed.");
        Add(targets, "workspace-binlogs", ".visualMCP/binlogs", "solution-store",
            Path.Combine(store, "binlogs"), false, "safe", "workspace-binlogs", "MSBuild binary logs; recomputed on request.");
        Add(targets, "workspace-temp", ".visualMCP/tmp", "solution-store",
            Path.Combine(store, "tmp"), false, "safe", "workspace-temp", "Repository-local scratch files.");
        Add(targets, "workspace-decompiled", ".visualMCP/decompiled", "solution-store",
            Path.Combine(store, "decompiled"), false, "derived", "workspace-decompiled", "Decompiled assemblies; re-decompiled on demand.");
        Add(targets, "workspace-symbols", ".visualMCP/profiling/symbols", "solution-store",
            Path.Combine(store, "profiling", "symbols"), false, "reinstall", "workspace-symbols", "Downloaded PDBs; fetched again when profiling needs them.");

        Add(targets, "test-results", "TestResults", "workspace",
            Path.Combine(workspaceRoot, "TestResults"), false, "safe", "test-results", "TRX and coverage output from test runs.");
        Add(targets, "artifacts", "artifacts", "workspace",
            Path.Combine(workspaceRoot, "artifacts"), false, "safe", "artifacts", "Build output produced with --artifacts-path.");

        if (!deep) return;

        foreach (var output in FindBuildOutputs(workspaceRoot, 4, CancellationToken.None))
        {
            targets.Add(new ScanTarget(
                "build-output-" + targets.Count, output, "build-output", output, false, "safe", "solution-build-output",
                "bin/obj: removed by clean, rebuilt by the next build (restore runs again)."));
        }
    }

    private static void AddNuGetTarget(
        List<ScanTarget> targets, IReadOnlyDictionary<string, string> nuget, string key,
        string cleanTarget, string label, string note, string risk)
    {
        if (!nuget.TryGetValue(key, out var path) || string.IsNullOrWhiteSpace(path)) return;

        targets.Add(new ScanTarget(cleanTarget, label, "nuget", path, false, risk, cleanTarget, note));
    }

    private static void Add(
        List<ScanTarget> targets, string id, string label, string category, string path, bool isFile,
        string risk, string? cleanTarget, string note)
        => targets.Add(new ScanTarget(id, label, category, Path.GetFullPath(path), isFile, risk, cleanTarget, note));

    private static FolderScan Scan(ScanTarget target, CancellationToken token, bool deep, int top)
    {
        try
        {
            if (target.IsFile)
            {
                var file = new FileInfo(target.Path);
                return new FolderScan(target, file.Exists, file.Exists ? file.Length : 0, file.Exists ? 1 : 0, false, null);
            }

            if (!Directory.Exists(target.Path))
                return new FolderScan(target, false, 0, 0, false, null);

            var (bytes, files, partial) = MeasureDirectory(target.Path, token);
            var children = deep ? MeasureChildren(target.Path, top, token) : null;
            return new FolderScan(target, true, bytes, files, partial, children);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FolderScan(target, false, 0, 0, false, null);
        }
    }

    private static List<FolderChild> MeasureChildren(string path, int top, CancellationToken token)
    {
        var children = new List<FolderChild>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                if (token.IsCancellationRequested) break;
                var (bytes, files, partial) = MeasureDirectory(directory, token);
                children.Add(new FolderChild(Path.GetFileName(directory), directory, true, bytes, files, partial));
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (token.IsCancellationRequested) break;
                try { children.Add(new FolderChild(Path.GetFileName(file), file, false, new FileInfo(file).Length, 1, false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        return children.OrderByDescending(child => child.Bytes).Take(top).ToList();
    }

    /// <summary>
    /// The size walk. <see cref="EnumerationOptions.IgnoreInaccessible"/> keeps a permission error from
    /// ending the whole folder, and reparse points are skipped so a junction cannot make one tree count twice.
    /// The cancellation check every 1024 files is what makes the time budget meaningful on a huge cache.
    /// </summary>
    private static (long Bytes, long Files, bool Partial) MeasureDirectory(string path, CancellationToken token)
    {
        long bytes = 0;
        long files = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                if ((files & 1023) == 0 && token.IsCancellationRequested) return (bytes, files, true);
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        return (bytes, files, token.IsCancellationRequested);
    }

    private static List<object> BuildFindings(
        IReadOnlyList<FolderScan> measured,
        IReadOnlyDictionary<string, FolderScan> byId,
        IReadOnlyList<DriveSnapshot> drives,
        DriveSnapshot? system,
        string tempPath,
        DiskPolicyState policy,
        string workspaceRoot)
    {
        var findings = new List<object>();

        void Add(string id, string severity, string problem, string fix, long bytes = 0, params string[] paths)
            => findings.Add(new
            {
                id,
                severity,
                bytes,
                mb = RoundMb(bytes),
                paths,
                problem,
                fix,
            });

        var suggestedRoot = SuggestRoot(workspaceRoot, drives, system);

        if (system is not null)
        {
            var freePercent = system.TotalBytes == 0 ? 100 : system.FreeBytes * 100.0 / system.TotalBytes;
            if (system.FreeBytes < 1L * 1024 * 1024 * 1024 || freePercent < 2)
            {
                Add("system-drive-critical", "critical",
                    $"The system drive {system.Name} has {RoundGb(system.FreeBytes)} GB free " +
                    $"({Math.Round(freePercent, 1)}% of {RoundGb(system.TotalBytes)} GB). Builds and SQLite writes start failing at 0.",
                    "Clean the largest safe targets below, then run disk_usage action=guard apply=true so the next build does not refill it.",
                    system.FreeBytes, system.Name);
            }
            else if (system.FreeBytes < 5L * 1024 * 1024 * 1024 || freePercent < 10)
            {
                Add("system-drive-low", "warning",
                    $"The system drive {system.Name} has {RoundGb(system.FreeBytes)} GB free " +
                    $"({Math.Round(freePercent, 1)}%). A full test run plus NuGet restores can consume several GB.",
                    "Run disk_usage action=guard apply=true before the next build, and clean temp-stale + the NuGet caches.",
                    system.FreeBytes, system.Name);
            }
        }

        if (SameRoot(Path.GetPathRoot(tempPath), SystemDriveRoot()))
        {
            Add("temp-on-system-drive", "warning",
                $"TEMP is {tempPath}, on the system drive: every dotnet build/test, every tool temporary project " +
                "and this server's own temp files grow it.",
                suggestedRoot is null
                    ? "Pass an explicit root on another drive: disk_usage action=guard root=\"D:\\\\vmcp-tmp\" apply=true"
                    : $"disk_usage action=guard root=\"{suggestedRoot}\" apply=true",
                byId.TryGetValue("temp", out var temp) ? temp.Bytes : 0, tempPath);
        }
        else if (!policy.Enabled)
        {
            Add("no-policy", "info",
                $"TEMP is already on another drive ({tempPath}), but the policy is not persisted: a restart of the " +
                "client may restore the default.",
                suggestedRoot is null
                    ? "disk_usage action=guard root=\"<root>\" apply=true"
                    : $"disk_usage action=guard root=\"{suggestedRoot}\" apply=true");
        }

        if (byId.TryGetValue("nuget-packages", out var nuget) && nuget.Exists &&
            SameRoot(Path.GetPathRoot(nuget.Target.Path), SystemDriveRoot()) && nuget.Bytes > 1L * 1024 * 1024 * 1024)
        {
            Add("nuget-on-system-drive", "warning",
                $"The NuGet global-packages folder is {RoundGb(nuget.Bytes)} GB on the system drive. Every new package " +
                "version adds to it, and every restore reads it.",
                "disk_usage action=guard apply=true moves NUGET_PACKAGES with the rest; cleaning it forces a re-download (target nuget-packages).",
                nuget.Bytes, nuget.Target.Path);
        }

        if (byId.TryGetValue("visualmcp-tools", out var tools) && tools.Exists && tools.Bytes > 512L * 1024 * 1024)
        {
            Add("tool-install-large", "info",
                $"The on-demand tool installs (Ghidra, netcoredbg) are {RoundGb(tools.Bytes)} GB.",
                "disk_usage action=clean targets=[\"visualmcp-tools\"] dryRun=false confirm=true — the next analyze_native_binary/debug_install downloads again.",
                tools.Bytes, tools.Target.Path);
        }

        if (byId.TryGetValue("mcp-log", out var log) && log.Exists && log.Bytes > 50L * 1024 * 1024)
        {
            Add("mcp-log-large", "info",
                $"The MCP debug log is {RoundMb(log.Bytes)} MB and every request appends to it.",
                "disk_usage action=clean targets=[\"mcp-log\"] dryRun=false confirm=true truncates it in place.",
                log.Bytes, log.Target.Path);
        }

        if (byId.TryGetValue("policy-root", out var policyFolder) && policyFolder.Exists &&
            policyFolder.Bytes > 1L * 1024 * 1024 * 1024)
        {
            Add("policy-root-large", "info",
                $"The disk policy root is {RoundGb(policyFolder.Bytes)} GB, including leftovers from earlier sessions ({policyFolder.Files} files).",
                "disk_usage action=clean targets=[\"policy-root\"] dryRun=false confirm=true — removes children older than olderThanHours while keeping the redirected temp/NuGet/.NET folders.",
                policyFolder.Bytes, policyFolder.Target.Path);
        }

        if (byId.TryGetValue("workspace-store", out var store) && store.Exists && store.Bytes > 1L * 1024 * 1024 * 1024)
        {
            Add("solution-store-large", "info",
                $"The solution store is {RoundGb(store.Bytes)} GB (profiling traces, decompiled code, the SQLite database).",
                "disk_usage action=clean targets=[\"workspace-profiling\",\"workspace-binlogs\",\"workspace-decompiled\"] dryRun=false confirm=true",
                store.Bytes, store.Target.Path);
        }

        var stale = CountStaleTemp(tempPath, 24, 5);
        if (stale.Count > 0)
        {
            Add("temp-residue", stale.Count > 20 ? "warning" : "info",
                $"{stale.Count} temp entries older than 24 hours look like previous tool runs (vmcp-*, testhost*, MSBuildTemp*, NuGetScratch*).",
                "disk_usage action=clean targets=[\"temp-stale\"] dryRun=false confirm=true",
                0, stale.Sample);
        }

        if (policy.Enabled && policy.Root is not null && SameRoot(Path.GetPathRoot(policy.Root), SystemDriveRoot()))
        {
            Add("policy-on-system-drive", "warning",
                $"The disk policy root is {policy.Root}, on the system drive — the redirect is active but it is not moving data off the drive that fills.",
                "Re-run guard with a root on another drive: disk_usage action=guard root=\"D:\\\\vmcp-tmp\" apply=true",
                0, policy.Root);
        }

        return findings;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  clean
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<object> CleanAsync(
        string[]? targets, double olderThanHours, bool dryRun, bool confirm, string? tempRoot,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!dryRun && !confirm)
        {
            return new
            {
                error = "clean deletes files: a real deletion needs confirm=true together with dryRun=false.",
                hint = "Run it with the default dryRun=true first: it returns the entries and the bytes it would delete.",
                action = "clean",
            };
        }

        var requested = (targets is { Length: > 0 } ? targets : ["temp-stale"])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .ToArray();
        var specs = BuildCleanTargets(tempRoot);
        if (!string.IsNullOrWhiteSpace(tempRoot))
        {
            string overrideRoot;
            try { overrideRoot = Path.GetFullPath(tempRoot); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new { action = "clean", error = ex.Message, root = tempRoot };
            }

            if (!IsCleanRootAllowed(overrideRoot))
            {
                return new
                {
                    action = "clean",
                    error = "The root override must be the system temp folder, a folder under it, or a folder under the active disk-policy root.",
                    root = overrideRoot,
                    hint = "Clean a named folder by pointing at its temp subfolder, or apply the policy first: disk_usage action=guard root=\"D:\\\\vmcp-tmp\" apply=true.",
                };
            }
        }

        var selected = ExpandTargets(requested, specs);
        var hours = Math.Clamp(olderThanHours, 0, 24 * 365);
        var budgetSeconds = Math.Clamp(timeoutSeconds, 5, 300);
        var started = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

        var outcomes = new List<CleanOutcome>();
        foreach (var id in selected)
        {
            if (!specs.TryGetValue(id, out var spec))
            {
                outcomes.Add(new CleanOutcome(id, string.Empty, 0, 0, [], [], ["unknown target"], false,
                    "Known targets: " + string.Join(", ", specs.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))));
                continue;
            }

            CleanOutcome outcome;
            try
            {
                outcome = spec.Kind switch
                {
                    "nuget" => await CleanNuGetAsync(spec, dryRun, budgetSeconds, cancellationToken),
                    "log" => CleanLog(spec, dryRun),
                    "build-output" => CleanBuildOutputs(spec, dryRun, budget.Token),
                    "policy-root" => CleanContents(spec, dryRun, hours, budget.Token, IsManagedPolicyFolder),
                    _ => CleanContents(spec, dryRun, hours, budget.Token),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                outcome = new CleanOutcome(spec.Id, spec.Path ?? string.Empty, 0, 0, [], [], [ex.Message], false, spec.Note);
            }

            outcomes.Add(outcome);
        }

        var freed = outcomes.Sum(outcome => outcome.FreedBytes);
        return new
        {
            action = "clean",
            dryRun,
            olderThanHours = hours,
            partial = budget.IsCancellationRequested,
            elapsedMs = started.ElapsedMilliseconds,
            totalFreedBytes = freed,
            totalFreedMB = RoundMb(freed),
            targets = outcomes.Select(outcome => new
            {
                target = outcome.Target,
                path = outcome.Path,
                freedBytes = outcome.FreedBytes,
                freedMB = RoundMb(outcome.FreedBytes),
                entries = outcome.Deleted,
                sample = outcome.Sample,
                skipped = outcome.Skipped,
                errors = outcome.Errors,
                partial = outcome.Partial,
                note = outcome.Note,
            }).ToArray(),
            note = dryRun
                ? "Nothing was deleted. Pass dryRun=false and confirm=true to delete the entries above."
                : "Deletion attempted; entries that were locked or protected are listed under skipped/errors.",
        };
    }

    private static string[] ExpandTargets(
        string[] requested, IReadOnlyDictionary<string, CleanTargetSpec> specs)
    {
        var expanded = new List<string>();
        foreach (var target in requested)
        {
            if (string.Equals(target, "all-safe", StringComparison.OrdinalIgnoreCase))
                expanded.AddRange(SafeCleanTargets.Where(specs.ContainsKey));
            else if (string.Equals(target, "all-reclaimable", StringComparison.OrdinalIgnoreCase))
                expanded.AddRange(SafeCleanTargets.Concat(ReclaimExtraCleanTargets).Where(specs.ContainsKey));
            else
                expanded.Add(target);
        }

        return expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Dictionary<string, CleanTargetSpec> BuildCleanTargets(string? tempRootOverride)
    {
        var map = new Dictionary<string, CleanTargetSpec>(StringComparer.OrdinalIgnoreCase);
        var temp = string.IsNullOrWhiteSpace(tempRootOverride) ? Path.GetTempPath() : Path.GetFullPath(tempRootOverride);

        map["temp-stale"] = new("temp-stale", "contents", temp, null, "Stale entries only: the cutoff is olderThanHours (default 24).", "safe");
        map["temp-all"] = new("temp-all", "contents", temp, null, "Every entry in the temp root, including fresh ones.", "safe");
        map["temp-legacy"] = new("temp-legacy", "contents",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), null,
            "The Local\\Temp folder when TEMP points elsewhere.", "safe");

        if (OperatingSystem.IsWindows())
        {
            map["windows-temp"] = new("windows-temp", "contents",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), null,
                "System temp; may need elevation.", "safe");
            map["crash-dumps"] = new("crash-dumps", "contents",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), null,
                "Process dumps.", "safe");
            map["wer"] = new("wer", "contents",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft", "Windows", "WER"), null,
                "Windows Error Reporting queue; may need elevation.", "safe");
        }

        map["nuget-http-cache"] = new("nuget-http-cache", "nuget", null, "http-cache", "Cleared through dotnet nuget locals.", "safe");
        map["nuget-plugins-cache"] = new("nuget-plugins-cache", "nuget", null, "plugins-cache", "Cleared through dotnet nuget locals.", "safe");
        map["nuget-temp"] = new("nuget-temp", "nuget", null, "temp", "Cleared through dotnet nuget locals.", "safe");
        map["nuget-packages"] = new("nuget-packages", "nuget", null, "global-packages",
            "Every project re-downloads its packages on the next restore.", "reinstall");

        map["visualmcp-tools"] = new("visualmcp-tools", "contents",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualMCP", "tools"), null,
            "Ghidra and netcoredbg are downloaded again on demand.", "reinstall");

        map["mcp-log"] = new("mcp-log", "log",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "mcp-servers", "VisualMCP", "debug.log"), null,
            "Truncated in place; the logger keeps appending.", "safe");

        var workspaceRoot = ResolveWorkspaceRoot();
        var store = Path.Combine(workspaceRoot, ".visualMCP");
        map["workspace-profiling"] = new("workspace-profiling", "contents", Path.Combine(store, "profiling"), null,
            "Traces, dumps and reports; re-collected on demand.", "safe");
        map["workspace-binlogs"] = new("workspace-binlogs", "contents", Path.Combine(store, "binlogs"), null,
            "MSBuild binary logs.", "safe");
        map["workspace-temp"] = new("workspace-temp", "contents", Path.Combine(store, "tmp"), null,
            "Repository-local scratch.", "safe");
        map["workspace-decompiled"] = new("workspace-decompiled", "contents", Path.Combine(store, "decompiled"), null,
            "Decompiled assemblies; re-decompiled on demand.", "derived");
        map["workspace-symbols"] = new("workspace-symbols", "contents", Path.Combine(store, "profiling", "symbols"), null,
            "Downloaded PDBs; fetched again when profiling needs them.", "reinstall");
        map["test-results"] = new("test-results", "contents", Path.Combine(workspaceRoot, "TestResults"), null,
            "TRX and coverage output.", "safe");
        map["artifacts"] = new("artifacts", "contents", Path.Combine(workspaceRoot, "artifacts"), null,
            "Build output produced with --artifacts-path.", "safe");
        map["solution-build-output"] = new("solution-build-output", "build-output", workspaceRoot, null,
            "Every bin and obj under the workspace; the next build recreates them.", "safe");

        var activePolicy = DiskPolicy.Current;
        if (activePolicy.Enabled && activePolicy.Root is { Length: > 0 } policyRoot)
            map["policy-root"] = new("policy-root", "policy-root", policyRoot, null,
                "Children of the policy root except the redirected temp/NuGet/.NET folders, filtered by age.", "safe");

        return map;
    }

    private static async Task<CleanOutcome> CleanNuGetAsync(
        CleanTargetSpec spec, bool dryRun, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var locals = await NuGetLocalsAsync(cancellationToken);
        var path = locals.TryGetValue(spec.NuGetCache!, out var folder) ? folder : string.Empty;
        var bytes = path.Length > 0 ? MeasureDirectory(path, cancellationToken).Bytes : 0;

        if (dryRun)
            return new CleanOutcome(spec.Id, path, bytes, 0, [], [], [], false,
                "Dry run: the cache was measured, not cleared.");

        var (exitCode, timedOut, _, stderr, _) = await ProcessRunner.RunAsync(
            "dotnet", $"nuget locals {spec.NuGetCache} --clear", Directory.GetCurrentDirectory(),
            Math.Clamp(timeoutSeconds, 10, 50));

        var ok = exitCode == 0 && !timedOut;
        return new CleanOutcome(spec.Id, path, ok ? bytes : 0, ok ? 1 : 0, [], [],
            ok ? [] : [LastLine(stderr) ?? (timedOut ? "dotnet nuget locals timed out" : $"exit code {exitCode}")],
            false, ok ? "Cache cleared through dotnet nuget locals." : "The NuGet CLI refused or timed out.");
    }

    private static CleanOutcome CleanLog(CleanTargetSpec spec, bool dryRun)
    {
        var path = spec.Path!;
        if (!File.Exists(path))
            return new CleanOutcome(spec.Id, path, 0, 0, [], [], [], false, "The log does not exist.");

        var bytes = new FileInfo(path).Length;
        if (dryRun)
            return new CleanOutcome(spec.Id, path, bytes, 1, [path], [], [], false, "Dry run: the log would be truncated.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(0);
            return new CleanOutcome(spec.Id, path, bytes, 1, [path], [], [], false,
                "Truncated in place; the logger keeps appending.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CleanOutcome(spec.Id, path, 0, 0, [], [], [$"held open: {ex.Message}"], false,
                "It is truncated on the next start if the handle cannot be shared now.");
        }
    }

    private static CleanOutcome CleanBuildOutputs(CleanTargetSpec spec, bool dryRun, CancellationToken token)
    {
        var directories = FindBuildOutputs(spec.Path!, 4, token);
        long bytes = 0;
        var deleted = 0;
        var sample = new List<string>();
        var errors = new List<string>();

        foreach (var directory in directories)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var (size, _, _) = MeasureDirectory(directory, token);
                if (!dryRun) DeleteEntry(directory);
                bytes += size;
                deleted++;
                if (sample.Count < 20) sample.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{directory}: {ex.Message}");
            }
        }

        return new CleanOutcome(spec.Id, spec.Path!, bytes, deleted, sample, [], errors, token.IsCancellationRequested,
            "bin and obj are recreated by the next build (restore runs again).");
    }

    private static CleanOutcome CleanContents(
        CleanTargetSpec spec, bool dryRun, double olderThanHours, CancellationToken token,
        Func<string, bool>? skip = null)
    {
        var root = spec.Path!;
        if (!Directory.Exists(root))
            return new CleanOutcome(spec.Id, root, 0, 0, [], [], [], false, "The folder does not exist.");

        if (IsProtected(root))
            return new CleanOutcome(spec.Id, root, 0, 0, [], [], ["refusing to clean a protected folder"], false, spec.Note);

        if (LooksLikeWorkspaceRoot(root))
            return new CleanOutcome(spec.Id, root, 0, 0, [], [],
                ["refusing to clean a folder that looks like a repository/workspace root (.git, .sln or .csproj at its top)"],
                false, spec.Note);

        var cutoff = olderThanHours > 0 ? DateTime.UtcNow.AddHours(-olderThanHours) : DateTime.MinValue;
        long bytes = 0;
        var deleted = 0;
        var sample = new List<string>();
        var skipped = new List<object>();
        var errors = new List<string>();

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (token.IsCancellationRequested) break;
                if (skip is not null && skip(entry)) continue;

                try
                {
                    var isDirectory = Directory.Exists(entry);
                    var lastWrite = isDirectory ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry);
                    if (lastWrite >= cutoff) continue;

                    var size = isDirectory ? MeasureDirectory(entry, token).Bytes : new FileInfo(entry).Length;
                    if (!dryRun) DeleteEntry(entry);

                    bytes += size;
                    deleted++;
                    if (sample.Count < 20) sample.Add(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new { path = entry, reason = ex.Message });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            errors.Add(ex.Message);
        }

        return new CleanOutcome(spec.Id, root, bytes, deleted, sample, skipped, errors, token.IsCancellationRequested, spec.Note);
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // git object files are read-only on Windows; clear the bit and retry once.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }

                Directory.Delete(path, recursive: true);
                return;
            }
        }

        if (!File.Exists(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  guard
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private object Guard(string? requestedRoot, bool apply)
    {
        var tempPathBefore = Path.GetTempPath();
        var current = DiskPolicy.Current;
        var currentEnvironment = DiskPolicy.ManagedVariables.ToDictionary(
            name => name, name => Environment.GetEnvironmentVariable(name));

        var resolved = ResolveGuardRoot(requestedRoot);
        if (resolved is null)
        {
            return new
            {
                action = "guard",
                applied = false,
                error = "No root to redirect to: pass one, or set VISUALMCP_TEMP.",
                hint = "disk_usage action=guard root=\"D:\\\\vmcp-tmp\" apply=true",
            };
        }

        string fullRoot;
        try { fullRoot = Path.GetFullPath(resolved); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new { action = "guard", applied = false, error = ex.Message, root = resolved };
        }

        if (IsProtected(fullRoot))
            return new
            {
                action = "guard",
                applied = false,
                error = "Refusing a protected folder as the policy root (drive root, Windows, Program Files, ProgramData, the user profile or LocalAppData).",
                root = fullRoot,
            };

        var workspaceRoot = ResolveWorkspaceRoot();
        if (SamePath(fullRoot, workspaceRoot) || IsUnder(fullRoot, workspaceRoot) || IsUnder(workspaceRoot, fullRoot))
            return new
            {
                action = "guard",
                applied = false,
                error = "The policy root must be outside the workspace and must not contain it: a clean of that root would be able to delete the solution.",
                root = fullRoot,
                workspace = workspaceRoot,
            };

        if (LooksLikeWorkspaceRoot(fullRoot))
            return new
            {
                action = "guard",
                applied = false,
                error = "The policy root looks like a repository/workspace root (.git, .sln or .csproj at its top). Choose a dedicated folder such as D:\\vmcp-tmp.",
                root = fullRoot,
            };

        var state = DiskPolicy.Build(fullRoot);
        var created = new List<string>();

        string? policyPath = apply ? null : current.PolicyPath ?? DiskPolicy.FindPolicyFile(ResolveWorkspaceRoot());
        if (apply)
        {
            foreach (var folder in state.Variables
                         .Where(pair => DiskPolicy.IsPathVariable(pair.Key))
                         .Select(pair => pair.Value)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    created.Add(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    created.Add($"(could not create: {folder} — {ex.Message})");
                }
            }

            policyPath = DiskPolicy.Save(ResolveWorkspaceRoot(), state);
            DiskPolicy.ApplyToCurrentProcess(state with { PolicyPath = policyPath });
        }

        return new
        {
            action = "guard",
            applied = apply,
            root = fullRoot,
            onSystemDrive = SameRoot(Path.GetPathRoot(fullRoot), SystemDriveRoot()),
            policyPath,
            persisted = apply,
            tempPathBefore,
            tempPathAfter = Path.GetTempPath(),
            variables = state.Variables,
            currentEnvironment,
            createdFolders = created,
            note = apply
                ? "TEMP/TMP, the NuGet package and HTTP caches, the .NET CLI home and bundle extraction now point under the root for this process and every child process. The policy file is read at the next start, so the redirect survives a restart."
                : "Nothing was changed. The variables above are what apply=true would set in this process and persist.",
            hint = apply ? null : "Dry check: pass apply=true to apply the redirect and persist it.",
        };
    }

    private string? ResolveGuardRoot(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        var current = DiskPolicy.Current;
        if (current.Enabled && !string.IsNullOrWhiteSpace(current.Root)) return current.Root;

        var fromEnvironment = Environment.GetEnvironmentVariable(DiskPolicy.TempVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        var workspaceRoot = ResolveWorkspaceRoot();
        var workspaceDrive = Path.GetPathRoot(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(workspaceDrive) && !SameRoot(workspaceDrive, SystemDriveRoot()))
            return Path.Combine(workspaceDrive!, "vmcp-tmp");

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed && !SameRoot(drive.Name, SystemDriveRoot()))
                    return Path.Combine(drive.Name, "vmcp-tmp");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  shared helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private string ResolveWorkspaceRoot()
    {
        var solutionPath = _workspace.LoadedSolutionPath;
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            try { solutionPath = _workspace.DiscoverSolutionPath(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }

        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var directory = Path.GetDirectoryName(solutionPath);
            if (!string.IsNullOrWhiteSpace(directory)) return directory!;
        }

        return Directory.GetCurrentDirectory();
    }

    private static async Task<Dictionary<string, string>> NuGetLocalsAsync(CancellationToken cancellationToken)
    {
        var locals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (exitCode, timedOut, stdout, _, _) = await ProcessRunner.RunAsync(
                "dotnet", "nuget locals all --list", Directory.GetCurrentDirectory(), 12);

            if (exitCode != 0 || timedOut) return locals;

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"');
                if (key is not ("http-cache" or "global-packages" or "temp" or "plugins-cache")) continue;
                if (value.Length == 0) continue;

                try { locals[key] = Path.GetFullPath(value); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No dotnet on PATH: the caller falls back to the default package folder.
        }

        return locals;
    }

    private static List<string> FindBuildOutputs(string root, int maxDepth, CancellationToken token)
    {
        var results = new List<string>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            if (token.IsCancellationRequested) break;
            var (directory, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name is "bin" or "obj")
                {
                    results.Add(child);
                    continue;
                }

                if (name is ".git" or ".vs" or ".idea" or ".visualMCP" or "node_modules" or "packages" or "TestResults" or "artifacts")
                    continue;

                queue.Enqueue((child, depth + 1));
            }
        }

        return results;
    }

    private static (int Count, string[] Sample) CountStaleTemp(string tempPath, double olderThanHours, int sampleSize)
    {
        var cutoff = DateTime.UtcNow.AddHours(-olderThanHours);
        var count = 0;
        var sample = new List<string>();

        try
        {
            if (!Directory.Exists(tempPath)) return (0, []);

            foreach (var entry in Directory.EnumerateFileSystemEntries(tempPath))
            {
                var name = Path.GetFileName(entry);
                if (!StaleTempPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

                DateTime lastWrite;
                try
                {
                    lastWrite = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (lastWrite >= cutoff) continue;
                count++;
                if (sample.Count < sampleSize) sample.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }

        return (count, sample.ToArray());
    }

    private static List<DriveSnapshot> ReadDrives()
    {
        var systemRoot = SystemDriveRoot();
        var workspaceRoot = Path.GetPathRoot(Directory.GetCurrentDirectory());
        var drives = new List<DriveSnapshot>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) continue;

                drives.Add(new DriveSnapshot(
                    drive.Name,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel,
                    drive.DriveType.ToString(),
                    drive.TotalSize,
                    drive.TotalFreeSpace,
                    SameRoot(drive.Name, systemRoot),
                    SameRoot(drive.Name, workspaceRoot)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        return drives;
    }

    private static object ShapeDrive(DriveSnapshot drive) => new
    {
        name = drive.Name,
        label = drive.Label,
        type = drive.Type,
        totalBytes = drive.TotalBytes,
        freeBytes = drive.FreeBytes,
        totalGB = RoundGb(drive.TotalBytes),
        freeGB = RoundGb(drive.FreeBytes),
        usedPercent = drive.TotalBytes == 0 ? 0 : Math.Round((drive.TotalBytes - drive.FreeBytes) * 100.0 / drive.TotalBytes, 1),
        isSystem = drive.IsSystem,
        isWorkspace = drive.IsWorkspace,
    };

    private static object ShapeFolder(FolderScan scan, bool deep) => new
    {
        id = scan.Target.Id,
        label = scan.Target.Label,
        category = scan.Target.Category,
        path = scan.Target.Path,
        exists = scan.Exists,
        bytes = scan.Bytes,
        mb = RoundMb(scan.Bytes),
        files = scan.Files,
        risk = scan.Target.Risk,
        cleanTarget = scan.Target.CleanTarget,
        note = scan.Target.Note,
        partial = scan.Partial,
        children = deep && scan.Children is not null
            ? scan.Children.Select(child => new
            {
                name = child.Name,
                path = child.Path,
                isDirectory = child.IsDirectory,
                bytes = child.Bytes,
                mb = RoundMb(child.Bytes),
                files = child.Files,
            }).ToArray()
            : null,
    };

    private static string? SuggestRoot(string workspaceRoot, IReadOnlyList<DriveSnapshot> drives, DriveSnapshot? system)
    {
        var workspaceDrive = Path.GetPathRoot(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(workspaceDrive) && !SameRoot(workspaceDrive, system?.Name))
            return Path.Combine(workspaceDrive!, "vmcp-tmp");

        var alternative = drives.FirstOrDefault(drive => !drive.IsSystem);
        return alternative is null ? null : Path.Combine(alternative.Name, "vmcp-tmp");
    }

    private static string SystemDriveRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var root = Path.GetPathRoot(windows);
            if (!string.IsNullOrWhiteSpace(root)) return root!;
        }

        return Path.GetPathRoot("/") ?? "/";
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool SameRoot(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// True for one of the directories a policy owns (<c>temp</c>, <c>nuget-packages</c>, …): a clean of the
    /// policy ROOT must not delete them, only the leftovers sitting next to them.
    /// </summary>
    private static bool IsManagedPolicyFolder(string entry)
    {
        var state = DiskPolicy.Current;
        if (!state.Enabled) return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));

        foreach (var (name, value) in state.Variables)
        {
            if (!DiskPolicy.IsPathVariable(name)) continue;
            if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Refuses a folder that carries the markers of a repository or a solution: a stale-temp sweep must
    /// never be able to delete source. Cheap enough to run before every contents clean.
    /// </summary>
    private static bool LooksLikeWorkspaceRoot(string root)
    {
        try
        {
            if (Directory.Exists(Path.Combine(root, ".git"))) return true;
            if (Directory.EnumerateFiles(root, "*.sln").Any()) return true;
            if (Directory.EnumerateFiles(root, "*.slnx").Any()) return true;
            if (Directory.EnumerateFiles(root, "*.csproj").Any()) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An unreadable folder cannot be proven safe; the protected-root checks above still apply.
        }

        return false;
    }

    private static bool IsCleanRootAllowed(string root)
    {
        var temp = Path.GetTempPath();
        if (SamePath(root, temp) || IsUnder(root, temp)) return true;

        var policyRoot = DiskPolicy.Current.Root;
        if (policyRoot is not null && (SamePath(root, policyRoot) || IsUnder(root, policyRoot))) return true;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.Equals(name, "temp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Length > parent.Length &&
               full.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsProtected(string path)
    {        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (SamePath(full, Path.GetPathRoot(full))) return true;

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (SamePath(full, root)) return true;
        }

        return false;
    }

    private static string? LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[^1];
    }

    private static double RoundMb(long bytes) => Math.Round(bytes / 1024d / 1024d, 1);

    private static double RoundGb(long bytes) => Math.Round(bytes / 1024d / 1024d / 1024d, 2);

    private sealed record ScanTarget(
        string Id, string Label, string Category, string Path, bool IsFile, string Risk, string? CleanTarget, string Note);

    private sealed record FolderScan(
        ScanTarget Target, bool Exists, long Bytes, long Files, bool Partial, List<FolderChild>? Children);

    private sealed record FolderChild(string Name, string Path, bool IsDirectory, long Bytes, long Files, bool Partial);

    private sealed record DriveSnapshot(
        string Name, string Label, string Type, long TotalBytes, long FreeBytes, bool IsSystem, bool IsWorkspace);

    private sealed record CleanTargetSpec(string Id, string Kind, string? Path, string? NuGetCache, string Note, string Risk);

    private sealed record CleanOutcome(
        string Target, string Path, long FreedBytes, int Deleted, List<string> Sample, List<object> Skipped,
        List<string> Errors, bool Partial, string Note);
}
