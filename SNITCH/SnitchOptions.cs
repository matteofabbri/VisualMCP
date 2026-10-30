namespace Snitch;

/// <summary>
/// Host-supplied identity and feature configuration for the Snitch library.
/// Keeping these out of the code lets Snitch run on behalf of any host application
/// rather than being hard-wired to a single one. Register via
/// <see cref="SnitchServiceCollectionExtensions.AddSnitch"/>.
/// </summary>
public sealed class SnitchOptions
{
    /// <summary>
    /// Short identifier for the host application. Used as the prefix for temporary
    /// files Snitch writes (e.g. <c>{app}-cmd-…</c>) and as the basis for default
    /// user-agents. Default: <c>"snitch"</c>.
    /// </summary>
    public string ApplicationName { get; set; } = "snitch";

    /// <summary>
    /// OS process image base name (without extension) of the running host, used by
    /// the stop-server feature to locate and terminate the live
    /// instance. Falls back to <see cref="ApplicationName"/> when not set.
    /// </summary>
    public string? ProcessName { get; set; }

    /// <summary>Defaults for the file-download feature.</summary>
    public SnitchDownloadOptions Download { get; set; } = new();

    /// <summary>The process name to act on, defaulting to <see cref="ApplicationName"/>.</summary>
    public string EffectiveProcessName =>
        string.IsNullOrWhiteSpace(ProcessName) ? ApplicationName : ProcessName!;

    /// <summary>Lower-cased, filesystem-safe prefix for temp files.</summary>
    public string TempFilePrefix => ApplicationName.ToLowerInvariant();
}

/// <summary>Configuration for the file-download capability.</summary>
public sealed class SnitchDownloadOptions
{
    /// <summary>User-agent sent when the caller does not specify one.</summary>
    public string DefaultUserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Snitch/1.0";
}
