using Snitch.Platform;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// A JDK for the two Java-based tools this library runs (Ghidra and JADX): the one <c>JAVA_HOME</c>
/// points at, or the newest under the usual install roots. Both tools are Java programs, and a stale or
/// missing <c>JAVA_HOME</c> is the usual reason either refuses to start.
/// </summary>
internal static class JavaRuntimeLocator
{
    public static string? FindJavaHome()
    {
        var configured = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(Path.Combine(configured, "bin")))
            return configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var roots = HostSystem.IsWindows
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            }
            : new[]
            {
                // Linux distributions and the common SDK managers put their JDKs under /usr/lib/jvm and
                // the user's home; macOS uses the same folder names under a versioned JVM root.
                "/usr/lib/jvm",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sdkman", "candidates", "java"),
                "/Library/Java/JavaVirtualMachines",
            };

        var java = "bin" + Path.DirectorySeparatorChar + "java" + HostSystem.ExecutableSuffix;

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetDirectories(root))
            .Where(candidate => File.Exists(Path.Combine(candidate, java)) ||
                                // macOS keeps the real home one level deeper: <root>/<jdk>.jdk/Contents/Home.
                                File.Exists(Path.Combine(candidate, "Contents", "Home", java)))
            .Select(candidate => File.Exists(Path.Combine(candidate, "Contents", "Home", java))
                ? Path.Combine(candidate, "Contents", "Home")
                : candidate)
            // "jdk-25" sorts after "jdk-17" as a string only because of luck; compare the version.
            .OrderByDescending(candidate => VersionOf(Path.GetFileName(candidate)))
            .FirstOrDefault();
    }

    private static Version VersionOf(string name)
    {
        var digits = new string(name.Where(c => char.IsDigit(c) || c == '.' || c == '_').ToArray())
            .Replace('_', '.');
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }
}
