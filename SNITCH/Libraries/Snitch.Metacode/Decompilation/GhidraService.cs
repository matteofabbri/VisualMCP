using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Snitch.Platform;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Where Ghidra lives and how long it may take. Every value has a default that works on this machine
/// and can be pointed elsewhere by the host.
/// </summary>
public sealed record GhidraOptions
{
    public static GhidraOptions Default { get; } = new();

    /// <summary>The <c>analyzeHeadless</c> script itself (with <c>.bat</c> on Windows, no extension elsewhere).</summary>
    public string? HeadlessPath { get; init; }

    /// <summary>The Ghidra installation directory; <c>support/analyzeHeadless</c> is looked up inside it.</summary>
    public string? GhidraHome { get; init; }

    /// <summary>
    /// Where the installer writes (and looks for) Ghidra: <c>%LOCALAPPDATA%\VisualMCP\tools</c> on
    /// Windows, the equivalent local data folder elsewhere. Null keeps the per-user default.
    /// </summary>
    public string? ToolsDirectory { get; init; }

    /// <summary>
    /// Download and extract the latest Ghidra release when no installation is found (default true).
    /// A machine that manages Ghidra itself sets this to false and points <see cref="GhidraHome"/> at it.
    /// </summary>
    public bool AutoInstall { get; init; } = true;

    /// <summary>
    /// The JDK to hand to Ghidra through <c>JAVA_HOME_OVERRIDE</c>. Null lets the first attempt use the
    /// environment, and a failure that names Java triggers one retry with a discovered JDK.
    /// </summary>
    public string? JavaHome { get; init; }

    /// <summary>Wall-clock budget for the whole headless run (import + analysis + script).</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Ghidra's own per-file analysis budget, passed as <c>-analysisTimeoutPerFile</c>.</summary>
    public int AnalysisTimeoutSeconds { get; init; } = 240;

    /// <summary>Where the snapshot, its log and the temporary Ghidra project live.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// Keep the Ghidra project after the run (useful when a script run has to be repeated). It is the
    /// same decision as Ghidra's <c>-deleteProject</c>: with this set, that option is not passed.
    /// </summary>
    public bool KeepProject { get; init; }

    /// <summary>The per-user tools folder the installer owns, shared with the other external tools.</summary>
    public static string DefaultToolsDirectory { get; } = ComputeToolsDirectory();

    private static string ComputeToolsDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(local, "VisualMCP", "tools");
    }
}

/// <summary>
/// What one headless run produced: the snapshot, where its JSON and its log are, and — when it failed —
/// the exact reason instead of an exception. <see cref="ToMetacode"/> turns the snapshot into the model.
/// </summary>
public sealed record GhidraAnalysis(
    string BinaryPath,
    GhidraSnapshot? Snapshot,
    string? OutputJsonPath,
    string? LogPath,
    string? ProjectDirectory,
    string? HeadlessPath,
    string? JavaHomeUsed,
    TimeSpan Elapsed,
    int ExitCode,
    bool TimedOut,
    string? Error = null)
{
    public bool IsError => Error is not null || Snapshot is null;

    public int Functions => Snapshot?.Functions.Count ?? 0;

    /// <summary>The recovered functions as metacode — the whole point of running Ghidra here.</summary>
    public MetacodeDocument ToMetacode(MetacodeOptions? options = null) =>
        Snapshot is null
            ? new MetacodeDocument(
                Path.GetFileName(BinaryPath),
                MetacodeLanguage.Cpp,
                Array.Empty<MetacodeModule>(),
                new[]
                {
                    new MetacodeDiagnostic(
                        MetacodeDiagnosticCodes.ParseError,
                        MetacodeDiagnosticSeverity.Error,
                        Error ?? "No Ghidra snapshot was produced.",
                        MetacodeOrigin.Unknown),
                })
            : new GhidraFrontend().Translate(Snapshot with { BinaryPath = BinaryPath }, options);
}

/// <summary>Runs Ghidra headless on a native binary and hands back the snapshot it exports.</summary>
public interface IGhidraService
{
    /// <summary>The <c>analyzeHeadless</c> script this machine resolves to, or null when Ghidra is not installed.</summary>
    string? HeadlessPath { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Import, analyze and export one binary. Never throws for an expected failure (no Ghidra, a timeout,
    /// a script error): the reason comes back in <see cref="GhidraAnalysis.Error"/> with the log path.
    /// </summary>
    Task<GhidraAnalysis> AnalyzeAsync(string binaryPath, GhidraOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ghidra headless as an external process: a temporary project, the export script written from this
/// assembly, a wall-clock timeout that kills the process tree, and the JDK problem handled the way the
/// previous native pipeline did — <c>JAVA_HOME_OVERRIDE</c>, set for the child process only, and only
/// after Ghidra itself complained about Java.
/// </summary>
public sealed class GhidraService : IGhidraService
{
    private readonly GhidraOptions _options;
    private readonly IGhidraInstaller _installer;

    public GhidraService(GhidraOptions? options = null, IGhidraInstaller? installer = null)
    {
        _options = options ?? GhidraOptions.Default;
        _installer = installer ?? new GhidraInstaller();
    }

    public string? HeadlessPath => Resolve(_options)?.Headless;

    public bool IsAvailable => HeadlessPath is not null;

    public async Task<GhidraAnalysis> AnalyzeAsync(
        string binaryPath,
        GhidraOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= _options;
        var started = Stopwatch.StartNew();

        GhidraAnalysis Fail(string error, string? log = null, string? project = null, int exitCode = -1, bool timedOut = false,
            string? javaHome = null, string? headless = null, string? json = null) =>
            new(binaryPath, null, json, log, project, headless, javaHome, started.Elapsed, exitCode, timedOut, error);

        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
            return Fail($"Binary not found: {binaryPath}");

        var location = Resolve(options);
        if (location is null && options.AutoInstall)
        {
            // A machine that has never seen Ghidra gets it now: the tools folder is this process's own,
            // so the install needs no elevation and no PATH change.
            var installation = await _installer.EnsureInstalledAsync(options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (installation.Installed)
                location = Resolve(options with { HeadlessPath = installation.HeadlessPath });
        }

        if (location is null)
        {
            return Fail(
                "Ghidra was not found and could not be installed. " +
                "Install it, set GHIDRA_INSTALL_DIR, or pass GhidraOptions.GhidraHome / HeadlessPath.");
        }

        var headless = location.Value.Headless;

        var outputDirectory = options.OutputDirectory ?? DefaultOutputDirectory(binaryPath);
        var projectDirectory = Path.Combine(outputDirectory, "project");
        var scriptsDirectory = Path.Combine(outputDirectory, "scripts");
        var jsonPath = Path.Combine(outputDirectory, "functions.json");
        var logPath = Path.Combine(outputDirectory, "ghidra.log");

        try
        {
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(scriptsDirectory);
            File.WriteAllText(Path.Combine(scriptsDirectory, ExportScript.FileName), ExportScript.Source, new UTF8Encoding(false));
            if (File.Exists(jsonPath))
                File.Delete(jsonPath);
        }
        catch (Exception exception)
        {
            return Fail($"Cannot prepare '{outputDirectory}': {exception.Message}");
        }

        var projectName = "vmcp_" + SafeName(Path.GetFileNameWithoutExtension(binaryPath));
        var arguments = new List<string>
        {
            Quote(projectDirectory),
            Quote(projectName),
            "-import", Quote(Path.GetFullPath(binaryPath)),
            "-scriptPath", Quote(scriptsDirectory),
            "-postScript", ExportScript.FileName, Quote(jsonPath),
            "-analysisTimeoutPerFile", options.AnalysisTimeoutSeconds.ToString(),
            // A kept project still holds the program from the previous run; without this Ghidra
            // refuses the import ("Found conflicting program file in project") and no snapshot is made.
            "-overwrite",
        };

        // `-deleteProject` is Ghidra's own cleanup: passing it while the caller asked to keep the
        // project would delete exactly what KeepProject exists to preserve.
        if (!options.KeepProject)
            arguments.Add("-deleteProject");

        var commandLine = string.Join(" ", arguments);

        var javaHome = options.JavaHome;
        var run = await RunAsync(headless, commandLine, outputDirectory, options.TimeoutSeconds, javaHome, cancellationToken);

        // Ghidra's launcher is the thing that knows whether the JDK is usable. When it says it is not,
        // retry once with a JDK picked from the machine, exactly through JAVA_HOME_OVERRIDE.
        if (run.TimedOut == false && run.ExitCode != 0 && javaHome is null && LooksLikeJavaProblem(run.Output))
        {
            javaHome = FindJavaHome();
            if (javaHome is not null)
            {
                run = await RunAsync(headless, commandLine, outputDirectory, options.TimeoutSeconds, javaHome, cancellationToken);
            }
        }

        File.WriteAllText(logPath, run.Output, new UTF8Encoding(false));

        if (!options.KeepProject)
            TryDelete(projectDirectory);

        if (run.TimedOut)
        {
            return Fail(
                $"Ghidra headless did not finish within {options.TimeoutSeconds}s (the process tree was killed). The log is at {logPath}.",
                logPath, projectDirectory, run.ExitCode, timedOut: true, javaHome, headless, jsonPath);
        }

        if (!File.Exists(jsonPath))
        {
            var tail = Tail(run.Output, 12);
            return Fail(
                $"Ghidra headless exited with code {run.ExitCode} and wrote no snapshot. Last output: {tail}",
                logPath, projectDirectory, run.ExitCode, javaHome: javaHome, headless: headless);
        }

        try
        {
            var snapshot = GhidraSnapshot.Parse(File.ReadAllText(jsonPath));
            return new GhidraAnalysis(
                binaryPath,
                snapshot with { BinaryPath = Path.GetFullPath(binaryPath) },
                jsonPath,
                logPath,
                projectDirectory,
                headless,
                javaHome,
                started.Elapsed,
                run.ExitCode,
                false);
        }
        catch (Exception exception)
        {
            return Fail($"The snapshot at {jsonPath} could not be read: {exception.Message}",
                logPath, projectDirectory, run.ExitCode, javaHome: javaHome, headless: headless, json: jsonPath);
        }
    }

    // ------------------------------------------------------------------ process

    private sealed record RunResult(int ExitCode, bool TimedOut, string Output);

    private static async Task<RunResult> RunAsync(
        string headless,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        string? javaHome,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(headless, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            // JAVA_HOME_OVERRIDE is the variable Ghidra's launch script honours; PATH is widened so the
            // launcher's own `java` probe finds the same JDK.
            info.Environment["JAVA_HOME_OVERRIDE"] = javaHome;
            info.Environment["JAVA_HOME"] = javaHome;
            var bin = Path.Combine(javaHome, "bin");
            if (Directory.Exists(bin))
            {
                var existingPath = info.Environment.TryGetValue("PATH", out var path) ? path : string.Empty;
                info.Environment["PATH"] = bin + Path.PathSeparator + existingPath;
            }
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new RunResult(-1, false, "Cannot start " + headless + ": " + exception.Message);
        }

        // The launcher ends with a "press any key" pause on some failures: an immediate EOF keeps it going.
        try { process.StandardInput.Close(); } catch { /* best effort */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }

            if (cancellationToken.IsCancellationRequested)
                throw;
        }

        try { process.WaitForExit(1000); } catch { /* best effort */ }

        string text;
        lock (output) text = output.ToString();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return new RunResult(exitCode, timedOut, text);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ locations

    /// <summary>Where a launcher was found: the script, the home it belongs to, and which rule answered.</summary>
    internal readonly record struct GhidraLocation(string Headless, string Home, string Source);

    /// <summary>
    /// An explicit path wins; then GhidraHome; then the environment; then the tools folder this machine
    /// uses. The launcher name comes from <see cref="HostSystem"/>: a batch file on Windows, a shell
    /// script elsewhere, with the other spelling accepted as a fallback.
    /// </summary>
    internal static GhidraLocation? Resolve(GhidraOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HeadlessPath) && File.Exists(options.HeadlessPath))
            return FromHeadless(Path.GetFullPath(options.HeadlessPath), "explicit");

        foreach (var (home, source) in new[]
                 {
                     (options.GhidraHome, "ghidraHome"),
                     (Environment.GetEnvironmentVariable("GHIDRA_INSTALL_DIR"), "environment"),
                     (Environment.GetEnvironmentVariable("GHIDRA_HOME"), "environment"),
                 })
        {
            var location = For(home, source);
            if (location is not null) return location;
        }

        // The folder the installer owns: <LOCALAPPDATA>/VisualMCP/tools/ghidra_<version>_PUBLIC.
        var tools = string.IsNullOrWhiteSpace(options.ToolsDirectory)
            ? GhidraOptions.DefaultToolsDirectory
            : options.ToolsDirectory;

        if (Directory.Exists(tools))
        {
            foreach (var directory in Directory.GetDirectories(tools, "ghidra_*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var location = For(directory, "tools");
                if (location is not null) return location;
            }
        }

        return null;

        static GhidraLocation? For(string? home, string source)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) return null;

            var candidate = Candidate(home);
            return candidate is null ? null : new GhidraLocation(candidate, home, source);
        }

        static GhidraLocation? FromHeadless(string headless, string source)
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(headless));
            return home is null ? null : new GhidraLocation(headless, home, source);
        }

        static string? Candidate(string home)
        {
            var names = HostSystem.IsWindows
                ? new[] { "analyzeHeadless.bat", "analyzeHeadless" }
                : new[] { "analyzeHeadless", "analyzeHeadless.bat" };

            foreach (var name in names)
            {
                var path = Path.Combine(home, "support", name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }
    }

    /// <summary>
    /// A JDK for <c>JAVA_HOME_OVERRIDE</c> (a Ghidra 12 needs a modern one, and a stale JAVA_HOME is the
    /// usual reason it refuses to start). The JADX path asks the same question, so the answer lives in
    /// <see cref="JavaRuntimeLocator"/>.
    /// </summary>
    internal static string? FindJavaHome() => JavaRuntimeLocator.FindJavaHome();

    private static bool LooksLikeJavaProblem(string output) =>
        output.Contains("JAVA_HOME", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Unable to locate a Java", StringComparison.OrdinalIgnoreCase)
        || output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
        || output.Contains("requires Java", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Java runtime", StringComparison.OrdinalIgnoreCase);

    private static string DefaultOutputDirectory(string binaryPath)
    {
        using var stream = File.OpenRead(binaryPath);
        var hash = SHA256.HashData(stream);
        var name = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "vmcp-ghidra", name);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 ? "binary" : safe;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Tail(string text, int lines)
    {
        var all = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", all.TakeLast(lines));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A Ghidra that is still holding its project open must not fail the analysis.
        }
    }
}

/// <summary>
/// The Ghidra script the service writes into the temporary project: it walks the analyzed program and
/// writes one JSON object per function, with the decompiled C. The shape is the one the previous
/// native pipeline used, so <see cref="GhidraSnapshot"/> reads old and new snapshots alike.
/// </summary>
internal static class ExportScript
{
    public const string FileName = "ExportNativeGraph.java";

    public const string Source = """
        // ExportNativeGraph.java — writes a JSON snapshot of the current program.
        // Invoked by Snitch.Metacode as: -postScript ExportNativeGraph.java <out.json>
        import ghidra.app.decompiler.DecompInterface;
        import ghidra.app.decompiler.DecompileOptions;
        import ghidra.app.decompiler.DecompileResults;
        import ghidra.app.script.GhidraScript;
        import ghidra.program.model.address.Address;
        import ghidra.program.model.listing.Function;
        import ghidra.program.model.listing.FunctionManager;
        import java.io.FileOutputStream;
        import java.io.OutputStreamWriter;
        import java.io.Writer;
        import java.util.ArrayList;
        import java.util.Iterator;
        import java.util.List;

        public class ExportNativeGraph extends GhidraScript {

            private static final int MAX_FUNCTIONS = 20000;
            private static final int MAX_DECOMPILED = 8000;

            @Override
            public void run() throws Exception {
                String[] args = getScriptArgs();
                if (args.length < 1) {
                    println("Usage: ExportNativeGraph <out.json>");
                    return;
                }

                DecompInterface decompiler = new DecompInterface();
                decompiler.setOptions(new DecompileOptions());
                decompiler.openProgram(currentProgram);

                StringBuilder sb = new StringBuilder(1 << 20);
                sb.append("{");
                sb.append("\"image\":").append(q(currentProgram.getName())).append(",");
                sb.append("\"language\":").append(q(currentProgram.getLanguageID().toString())).append(",");
                // Program has no getEntryPoint(); entry points live in the symbol table.
                String entry = "";
                Iterator<Address> entryPoints = currentProgram.getSymbolTable().getExternalEntryPointIterator();
                if (entryPoints.hasNext()) entry = entryPoints.next().toString();

                String entryAddress = entry;
                sb.append("\"entry\":").append(q(entryAddress)).append(",");
                sb.append("\"imageBase\":").append(q(currentProgram.getImageBase().toString())).append(",");
                sb.append("\"functions\":[");

                FunctionManager functions = currentProgram.getFunctionManager();
                int count = 0;
                for (Function function : functions.getFunctions(true)) {
                    if (count >= MAX_FUNCTIONS) break;
                    if (count > 0) sb.append(",");
                    count++;

                    String decompiled = "";
                    try {
                        DecompileResults result = decompiler.decompileFunction(function, 45, monitor);
                        if (result != null && result.decompileCompleted()
                                && result.getDecompiledFunction() != null) {
                            decompiled = result.getDecompiledFunction().getC();
                            if (decompiled.length() > MAX_DECOMPILED)
                                decompiled = decompiled.substring(0, MAX_DECOMPILED) + "\n// ...(truncated)";
                        }
                    } catch (Exception ignored) { }

                    sb.append("{");
                    sb.append("\"entry\":").append(q(function.getEntryPoint().toString())).append(",");
                    sb.append("\"name\":").append(q(function.getName())).append(",");
                    sb.append("\"signature\":").append(q(function.getPrototypeString(true, true))).append(",");
                    sb.append("\"size\":").append(function.getBody().getNumAddresses()).append(",");
                    sb.append("\"decompiled\":").append(q(decompiled)).append(",");

                    List<String> calls = new ArrayList<>();
                    try {
                        for (Function callee : function.getCalledFunctions(monitor))
                            calls.add(callee.getEntryPoint().toString());
                    } catch (Exception ignored) { }

                    sb.append("\"calls\":[");
                    for (int i = 0; i < calls.size(); i++) {
                        if (i > 0) sb.append(",");
                        sb.append(q(calls.get(i)));
                    }
                    sb.append("]}");
                }

                sb.append("]}");
                decompiler.dispose();

                try (Writer writer = new OutputStreamWriter(
                        new FileOutputStream(args[0]), java.nio.charset.StandardCharsets.UTF_8)) {
                    writer.write(sb.toString());
                }
                println("ExportNativeGraph: " + count + " function(s) -> " + args[0]);
            }

            private static String q(String value) {
                if (value == null) return "null";
                StringBuilder b = new StringBuilder(value.length() + 2);
                b.append('"');
                for (int i = 0; i < value.length(); i++) {
                    char c = value.charAt(i);
                    switch (c) {
                        case '"':  b.append("\\\""); break;
                        case '\\': b.append("\\\\"); break;
                        case '\n': b.append("\\n"); break;
                        case '\r': b.append("\\r"); break;
                        case '\t': b.append("\\t"); break;
                        default:
                            if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
                            else b.append(c);
                    }
                }
                return b.append('"').toString();
            }
        }
        """;
}
