using System.Diagnostics;

namespace Snitch.Execution;

/// <summary>
/// Resolves and runs an external tool the way a shell would, without a shell when it can.
/// On Windows a number of ecosystem tools (npm, pnpm, yarn, the project-local tsc and eslint)
/// are <c>.cmd</c> shims, which CreateProcess cannot start directly: those go through
/// <c>cmd.exe /d /s /c</c>, everything else is started by its resolved path. A missing tool is a
/// structured result (exit code 127 and a message), never an exception.
/// </summary>
public static class ExternalTool
{
    private static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat", ".com"];
    private static readonly string[] UnixExtensions = [""];

    /// <summary>The extensions a bare tool name may carry on this platform, in lookup order.</summary>
    public static string[] Extensions => OperatingSystem.IsWindows() ? WindowsExtensions : UnixExtensions;

    /// <summary>
    /// Finds an executable by name. Lookup order: the caller's search directories (project-local
    /// tools live in <c>node_modules/.bin</c>), the working directory, then <c>PATH</c>. A name
    /// that already contains a directory separator is looked up as a path and never through PATH.
    /// </summary>
    public static string? Resolve(string name, string? workingDirectory = null,
        IEnumerable<string>? searchDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar) ||
            Path.IsPathRooted(name))
            return ResolvePath(name);

        foreach (var directory in SearchDirectories(workingDirectory, searchDirectories))
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }

    /// <summary>True when <see cref="Resolve"/> finds the tool.</summary>
    public static bool Exists(string name, string? workingDirectory = null,
        IEnumerable<string>? searchDirectories = null) =>
        Resolve(name, workingDirectory, searchDirectories) is not null;

    /// <summary>
    /// Runs a tool found by <see cref="Resolve"/>. When the tool is missing the result is
    /// <c>(127, false, "", "&lt;name&gt; was not found …", zero)</c>, so a caller can report it
    /// instead of failing the whole call.
    /// </summary>
    public static Task<(int exitCode, bool timedOut, string stdout, string stderr, TimeSpan elapsed)>
        RunAsync(string name, string args, string? workingDirectory, int timeoutSeconds,
            IEnumerable<string>? searchDirectories = null)
    {
        var resolved = Resolve(name, workingDirectory, searchDirectories);
        if (resolved is null)
            return Task.FromResult((127, false, string.Empty,
                $"{name} was not found on PATH" +
                (searchDirectories is null ? "." : " or in the project's tool directory."),
                TimeSpan.Zero));

        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory!;

        // A .cmd/.bat shim is a batch file: cmd.exe runs it and owns the quoting of the first
        // token, so the path is wrapped once more than the arguments are.
        if (OperatingSystem.IsWindows() &&
            (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            return ProcessRunner.RunAsync("cmd.exe", $"/d /s /c \"\"{resolved}\" {args}\"", directory, timeoutSeconds);

        return ProcessRunner.RunAsync(resolved, args, directory, timeoutSeconds);
    }

    /// <summary>The version line a tool prints for a <c>--version</c>-style probe, or null when it is missing.</summary>
    public static async Task<string?> VersionAsync(string name, string arguments = "--version",
        string? workingDirectory = null, IEnumerable<string>? searchDirectories = null)
    {
        var resolved = Resolve(name, workingDirectory, searchDirectories);
        if (resolved is null) return null;

        var (exitCode, timedOut, stdout, stderr, _) =
            await RunAsync(name, arguments, workingDirectory, 20, searchDirectories);
        if (timedOut) return "(timed out)";

        var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return exitCode == 0 || !string.IsNullOrWhiteSpace(line) ? line : "(not available)";
    }

    private static IEnumerable<string> SearchDirectories(string? workingDirectory,
        IEnumerable<string>? searchDirectories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            string full;
            try { full = Path.GetFullPath(directory); }
            catch { return; }
            if (!Directory.Exists(full)) return;
            if (seen.Add(full)) ordered.Add(full);
        }

        foreach (var directory in searchDirectories ?? []) Add(directory);
        Add(workingDirectory);
        Add(Directory.GetCurrentDirectory());

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            Add(directory.Trim('"'));

        return ordered;
    }

    private static string? ResolvePath(string name)
    {
        var full = Path.GetFullPath(name);

        foreach (var extension in Extensions)
            if (File.Exists(full + extension)) return full + extension;

        return File.Exists(full) ? full : null;
    }
}
