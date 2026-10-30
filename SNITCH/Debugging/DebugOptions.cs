namespace Snitch.Debugging;

/// <summary>Where the debugger looks for its adapter and how long it waits for the debuggee.</summary>
public sealed class DebugOptions
{
    /// <summary>
    /// The tools directory shared by the external tools (Ghidra, netcoredbg, …):
    /// <c>%LOCALAPPDATA%\VisualMCP\tools</c> on Windows, the equivalent local data folder elsewhere.
    /// </summary>
    public string ToolsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualMCP", "tools");

    /// <summary>An explicit netcoredbg path; when null the tools directory and the environment are used.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Default wait for the debuggee to reach a stop after launch, in seconds.</summary>
    public int LaunchTimeoutSeconds { get; set; } = 30;
}
