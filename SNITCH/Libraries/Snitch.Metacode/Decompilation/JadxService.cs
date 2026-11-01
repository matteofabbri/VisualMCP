using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Snitch.Platform;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Where JADX lives and how long it may take. Every value has a default that works on this machine and
/// can be pointed elsewhere by the host.
/// </summary>
public sealed record JadxOptions
{
    public static JadxOptions Default { get; } = new();

    /// <summary>The <c>bin/jadx</c> launcher itself (<c>jadx.bat</c> on Windows).</summary>
    public string? CliPath { get; init; }

    /// <summary>The JADX installation directory; <c>bin/jadx</c> is looked up inside it.</summary>
    public string? JadxHome { get; init; }

    /// <summary>
    /// Where the installer writes (and looks for) JADX: the per-user tools folder, shared with Ghidra so
    /// one directory holds every external tool this product installs.
    /// </summary>
    public string? ToolsDirectory { get; init; }

    /// <summary>
    /// Download and extract the latest JADX release when no installation is found (default true). A
    /// machine that manages JADX itself sets this to false and points <see cref="JadxHome"/> at it.
    /// </summary>
    public bool AutoInstall { get; init; } = true;

    /// <summary>
    /// The JDK to hand to JADX through <c>JAVA_HOME</c> and <c>PATH</c> (JADX is a Java program: a
    /// machine with no JRE on PATH cannot run it). Null lets the first attempt use the environment, and a
    /// failure that names Java triggers one retry with a discovered JDK.
    /// </summary>
    public string? JavaHome { get; init; }

    /// <summary>Wall-clock budget for the whole decompile run.</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>Processing threads; 0 keeps JADX's own default (half the cores).</summary>
    public int Threads { get; init; }

    /// <summary>Where the decompiled sources, the log and JADX's own work directory live.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Decode resources too (default false: this reads code, and resources are megabytes of noise).</summary>
    public bool IncludeResources { get; init; }

    /// <summary>How many decompiled .java files one run may hand back; an APK can produce tens of thousands.</summary>
    public int MaxSourceFiles { get; init; } = 5000;

    /// <summary>The per-user tools folder the installer owns, shared with the other external tools.</summary>
    public static string DefaultToolsDirectory { get; } = GhidraOptions.DefaultToolsDirectory;
}

/// <summary>
/// One decompiled source file: where JADX wrote it, its path inside the archive (the package path), and
/// the type it declares — the file's own name, which is how Java names the public type of a unit.
/// </summary>
public sealed record JadxSourceFile(string Path, string RelativePath, string TypeName);

/// <summary>
/// What one JADX run produced: the sources it wrote, where its output and log are, and — when it failed
/// — the exact reason instead of an exception.
/// </summary>
public sealed record JadxAnalysis(
    string ArchivePath,
    string? OutputDirectory,
    IReadOnlyList<JadxSourceFile> Sources,
    string? LogPath,
    string? CliPath,
    string? JavaHomeUsed,
    TimeSpan Elapsed,
    int ExitCode,
    bool TimedOut,
    string? Error = null,
    int Truncated = 0)
{
    public bool IsError => Error is not null || Sources.Count == 0;

    public int Files => Sources.Count;
}

/// <summary>Runs JADX on a Java binary and hands back the Java sources it decompiled.</summary>
public interface IJadxService
{
    /// <summary>The JADX launcher this machine resolves to, or null when JADX is not installed.</summary>
    string? CliPath { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Decompiles one .class/.dex/.jar/.apk/.aar/… into Java sources. Never throws for an expected
    /// failure (no JADX, no JDK, a timeout): the reason comes back in <see cref="JadxAnalysis.Error"/>
    /// with the log path.
    /// </summary>
    Task<JadxAnalysis> DecompileAsync(
        string archivePath,
        JadxOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// JADX as an external process: an output directory per archive (keyed on its content), the CLI's
/// <c>--output-format java</c> output collected backwards, a wall-clock timeout that kills the process
/// tree, and the JDK problem handled the way the Ghidra path handles it — a retry with a discovered
/// <c>JAVA_HOME</c> once the run itself says Java is the problem.
/// </summary>
public sealed class JadxService : IJadxService
{
    private readonly JadxOptions _options;
    private readonly IJadxInstaller _installer;

    public JadxService(JadxOptions? options = null, IJadxInstaller? installer = null)
    {
        _options = options ?? JadxOptions.Default;
        _installer = installer ?? new JadxInstaller();
    }

    public string? CliPath => Resolve(_options)?.Cli;

    public bool IsAvailable => CliPath is not null;

    public async Task<JadxAnalysis> DecompileAsync(
        string archivePath,
        JadxOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= _options;
        var started = Stopwatch.StartNew();

        JadxAnalysis Fail(string error, string? output = null, string? log = null, int exitCode = -1,
            bool timedOut = false, string? javaHome = null, string? cli = null) =>
            new(archivePath, output, Array.Empty<JadxSourceFile>(), log, cli, javaHome, started.Elapsed, exitCode, timedOut, error);

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return Fail($"Java archive not found: {archivePath}");

        if (!JavaArchiveDetector.IsJavaBinary(archivePath))
            return Fail($"{Path.GetFileName(archivePath)} is not Java bytecode (no class, dex or class archive detected).");

        var location = Resolve(options);
        if (location is null && options.AutoInstall)
        {
            // A machine that has never seen JADX gets it now: the tools folder is this process's own, so
            // the install needs no elevation and no PATH change.
            var installation = await _installer.EnsureInstalledAsync(options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (installation.Installed)
                location = Resolve(options with { CliPath = installation.CliPath });
        }

        if (location is null)
        {
            return Fail(
                "JADX was not found and could not be installed. Install the cross-platform jadx-<version>.zip, " +
                "set JADX_HOME (or JADX_CLI), or pass JadxOptions.JadxHome / CliPath.");
        }

        var cli = location.Value.Cli;

        var outputDirectory = options.OutputDirectory ?? DefaultOutputDirectory(archivePath);
        var sourcesDirectory = Path.Combine(outputDirectory, "sources");
        var logPath = Path.Combine(outputDirectory, "jadx.log");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            if (Directory.Exists(sourcesDirectory))
                Directory.Delete(sourcesDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            return Fail($"Cannot prepare '{outputDirectory}': {exception.Message}", outputDirectory, cli: cli);
        }

        var arguments = new List<string>
        {
            "-d", Quote(outputDirectory),
            "--output-format", "java",
            "--log-level", "error",
        };

        if (!options.IncludeResources)
            arguments.Add("--no-res");

        if (options.Threads > 0)
        {
            arguments.Add("-j");
            arguments.Add(Math.Clamp(options.Threads, 1, 64).ToString());
        }

        arguments.Add(Quote(Path.GetFullPath(archivePath)));

        var commandLine = string.Join(" ", arguments);

        var javaHome = options.JavaHome;
        var run = await RunAsync(cli, commandLine, outputDirectory, options.TimeoutSeconds, javaHome, cancellationToken);

        // JADX is a Java program: when the run says it could not start a JVM, retry once with a JDK
        // picked from the machine, exactly through JAVA_HOME and PATH.
        if (!run.TimedOut && run.ExitCode != 0 && javaHome is null && LooksLikeJavaProblem(run.Output))
        {
            javaHome = JavaRuntimeLocator.FindJavaHome();
            if (javaHome is not null)
            {
                run = await RunAsync(cli, commandLine, outputDirectory, options.TimeoutSeconds, javaHome, cancellationToken);
            }
        }

        try { File.WriteAllText(logPath, run.Output, new UTF8Encoding(false)); }
        catch { /* the log is a convenience, not the result */ }

        if (run.TimedOut)
        {
            return Fail(
                $"JADX did not finish within {options.TimeoutSeconds}s (the process tree was killed). The log is at {logPath}.",
                outputDirectory, logPath, run.ExitCode, timedOut: true, javaHome, cli);
        }

        var sources = CollectSources(sourcesDirectory, options.MaxSourceFiles, out var truncated, out var collectError);
        if (collectError is not null)
            return Fail(collectError, outputDirectory, logPath, run.ExitCode, javaHome: javaHome, cli: cli);

        if (sources.Count == 0)
        {
            var tail = Tail(run.Output, 12);
            var jdk = javaHome is null && LooksLikeJavaProblem(run.Output)
                ? " No JDK was found on this machine: JADX needs a JRE/JDK (JAVA_HOME, or a JDK under the usual install roots)."
                : string.Empty;
            return Fail(
                $"JADX exited with code {run.ExitCode} and produced no .java source. Last output: {tail}{jdk}",
                outputDirectory, logPath, run.ExitCode, javaHome: javaHome, cli: cli);
        }

        return new JadxAnalysis(
            archivePath,
            outputDirectory,
            sources,
            logPath,
            cli,
            javaHome,
            started.Elapsed,
            run.ExitCode,
            false,
            Truncated: truncated);
    }

    // ------------------------------------------------------------------ process

    private sealed record RunResult(int ExitCode, bool TimedOut, string Output);

    private static async Task<RunResult> RunAsync(
        string launcher,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        string? javaHome,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        // A .bat file is not an executable image: Windows starts it through the command interpreter, and
        // the extra quotes are cmd's own rule for a command line that itself begins with a quote.
        if (HostSystem.IsWindows && launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            info.Arguments = "/c \"" + Quote(launcher) + " " + arguments + "\"";
        }
        else
        {
            info.FileName = launcher;
            info.Arguments = arguments;
        }

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            info.Environment["JAVA_HOME"] = javaHome;
            var bin = Path.Combine(javaHome, "bin");
            if (Directory.Exists(bin))
            {
                var existingPath = info.Environment.TryGetValue("PATH", out var path) ? path : string.Empty;
                info.Environment["PATH"] = bin + Path.PathSeparator + existingPath;
            }
        }

        // JADX writes progress to stdout and stderr from several threads; a queue keeps the lines
        // without a lock around a StringBuilder.
        var output = new ConcurrentQueue<string>();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new RunResult(-1, false, "Cannot start " + launcher + ": " + exception.Message);
        }

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

        // The exit also has to drain the asynchronous readers, or the last lines are missing from the log.
        try { process.WaitForExit(1000); } catch { /* best effort */ }

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return new RunResult(exitCode, timedOut, string.Join(Environment.NewLine, output));
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ output

    /// <summary>The .java files JADX wrote under <c>&lt;output&gt;/sources</c>, capped at <paramref name="maxFiles"/>.</summary>
    private static List<JadxSourceFile> CollectSources(
        string sourcesDirectory, int maxFiles, out int truncated, out string? error)
    {
        truncated = 0;
        error = null;

        if (!Directory.Exists(sourcesDirectory))
        {
            error = $"JADX wrote no source directory (looked for '{sourcesDirectory}').";
            return [];
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourcesDirectory, "*.java", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The decompiled sources could not be read: {exception.Message}";
            return [];
        }

        var limit = Math.Max(1, maxFiles);
        if (files.Count > limit)
        {
            truncated = files.Count - limit;
            files = files.Take(limit).ToList();
        }

        return files.Select(file =>
        {
            var relative = Path.GetRelativePath(sourcesDirectory, file).Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(file);
            return new JadxSourceFile(file, relative, name);
        }).ToList();
    }

    // ------------------------------------------------------------------ locations

    /// <summary>Where a launcher was found: the script and which rule answered.</summary>
    internal readonly record struct JadxLocation(string Cli, string Home, string Source);

    /// <summary>
    /// An explicit path wins; then JadxHome; then the environment; then the tools folder this machine
    /// uses. The launcher name comes from <see cref="HostSystem"/>, with the other spelling accepted as a
    /// fallback, because an archive unpacked on another platform is still an installation.
    /// </summary>
    internal static JadxLocation? Resolve(JadxOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CliPath) && File.Exists(options.CliPath))
            return FromCli(Path.GetFullPath(options.CliPath), "explicit");

        var cli = Environment.GetEnvironmentVariable("JADX_CLI");
        if (!string.IsNullOrWhiteSpace(cli) && File.Exists(cli))
            return FromCli(Path.GetFullPath(cli), "environment");

        foreach (var (home, source) in new[]
                 {
                     (options.JadxHome, "jadxHome"),
                     (Environment.GetEnvironmentVariable("JADX_HOME"), "environment"),
                 })
        {
            var location = For(home, source);
            if (location is not null) return location;
        }

        var tools = string.IsNullOrWhiteSpace(options.ToolsDirectory)
            ? JadxOptions.DefaultToolsDirectory
            : options.ToolsDirectory;

        if (Directory.Exists(tools))
        {
            foreach (var directory in Directory.GetDirectories(tools, "jadx-*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var location = For(directory, "tools");
                if (location is not null) return location;
            }
        }

        return null;

        static JadxLocation? For(string? home, string source)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) return null;

            var candidate = Candidate(home);
            return candidate is null ? null : new JadxLocation(candidate, home, source);
        }

        static JadxLocation? FromCli(string cli, string source)
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(cli));
            return home is null ? null : new JadxLocation(cli, home, source);
        }

        static string? Candidate(string home)
        {
            var names = HostSystem.IsWindows
                ? new[] { "jadx.bat", "jadx" }
                : new[] { "jadx", "jadx.bat" };

            foreach (var name in names)
            {
                var path = Path.Combine(home, "bin", name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }
    }

    private static bool LooksLikeJavaProblem(string output) =>
        output.Contains("JAVA_HOME", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Unable to locate a Java", StringComparison.OrdinalIgnoreCase)
        || output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
        || output.Contains("requires Java", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Java runtime", StringComparison.OrdinalIgnoreCase)
        || output.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase)
        || output.Contains("java: command not found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);

    /// <summary>One output directory per archive content, so the same jar is not decompiled twice.</summary>
    private static string DefaultOutputDirectory(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        var hash = SHA256.HashData(stream);
        var name = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "vmcp-jadx", name);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Tail(string text, int lines)
    {
        var all = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", all.TakeLast(lines));
    }
}
