using System.Runtime.InteropServices;

namespace Snitch.Platform;

/// <summary>Which operating system family this process runs on.</summary>
public enum OperatingSystemFamily
{
    Windows,
    Linux,
    MacOs,
    Unknown,
}

/// <summary>
/// What the host machine is. One place to ask "are we on Windows?" — everything that behaves
/// differently by platform (line endings, path comparison, executable suffix, shells) reads it from
/// here instead of scattering <c>RuntimeInformation</c> checks around the services.
/// </summary>
public static class HostSystem
{
    public static OperatingSystemFamily Family { get; } = Detect();

    public static bool IsWindows => Family == OperatingSystemFamily.Windows;
    public static bool IsLinux => Family == OperatingSystemFamily.Linux;
    public static bool IsMacOs => Family == OperatingSystemFamily.MacOs;

    /// <summary>The newline a file written on this machine conventionally uses.</summary>
    public static string DefaultNewLine => IsWindows ? LineEndings.CrLf : LineEndings.Lf;

    /// <summary>Windows and (by default) macOS file systems ignore case in paths.</summary>
    public static bool IsCaseInsensitiveFileSystem => IsWindows || IsMacOs;

    /// <summary>Comparison to use when the two paths may differ only in case.</summary>
    public static StringComparison PathComparison =>
        IsCaseInsensitiveFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Comparison to use when the two paths must match exactly.</summary>
    public static StringComparison ExactPathComparison => StringComparison.Ordinal;

    /// <summary>".exe" on Windows, empty elsewhere.</summary>
    public static string ExecutableSuffix => IsWindows ? ".exe" : string.Empty;

    /// <summary>Human-readable description, for reports and diagnostics.</summary>
    public static string Describe() =>
        $"{Family} {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture})";

    private static OperatingSystemFamily Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OperatingSystemFamily.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return OperatingSystemFamily.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OperatingSystemFamily.MacOs;
        return OperatingSystemFamily.Unknown;
    }
}
