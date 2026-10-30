namespace VisualMCP.Dashboard;

/// <summary>
/// Where the web dashboard listens, what it reads, and whether it runs at all.
/// </summary>
/// <remarks>
/// The dashboard is an observer: it binds to loopback, never writes to the solution and
/// never logs through the MCP transport's stdout. <c>VISUALMCP_DASHBOARD=off</c> turns it
/// off, <c>VISUALMCP_DASHBOARD_PORT</c> changes the preferred port, and
/// <c>VISUALMCP_DASHBOARD_HOST</c> changes the interface (127.0.0.1 by default — exposing
/// the dashboard on a LAN interface is a deliberate choice, not a default).
/// </remarks>
public sealed class DashboardOptions
{
    /// <summary>The <c>context</c> row that remembers the last port the dashboard bound.</summary>
    public const string PortKey = "dashboard.port";

    /// <summary>The port asked for first, before the remembered one and a random one.</summary>
    public const int DefaultPort = 4677;

    /// <summary>False when <c>VISUALMCP_DASHBOARD</c> is off/0/false/no.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The first port to try; 0 means "let the OS choose immediately".</summary>
    public int PreferredPort { get; init; } = DefaultPort;

    /// <summary>The interface to bind. Loopback by default, so the dashboard is not exposed.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// The log file the live log page tails. Null disables that page's feed and leaves
    /// the process/store pages working.
    /// </summary>
    public string? LogPath { get; init; }

    /// <summary>Reads the overrides from the environment; everything else is the default.</summary>
    public static DashboardOptions FromEnvironment(string? logPath = null)
    {
        var flag = Environment.GetEnvironmentVariable("VISUALMCP_DASHBOARD");
        var enabled = !(string.Equals(flag, "off", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(flag, "0", StringComparison.Ordinal)
                     || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(flag, "no", StringComparison.OrdinalIgnoreCase));

        var port = int.TryParse(Environment.GetEnvironmentVariable("VISUALMCP_DASHBOARD_PORT"), out var parsed)
                   && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;

        var host = Environment.GetEnvironmentVariable("VISUALMCP_DASHBOARD_HOST");
        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

        return new DashboardOptions
        {
            Enabled = enabled,
            PreferredPort = port,
            Host = host,
            LogPath = logPath,
        };
    }

    /// <summary>
    /// The ports to try, in order, before falling back to an OS-assigned one: the preferred
    /// port, then the port the dashboard last bound successfully (kept in the solution store).
    /// The stored port is what makes a second instance land on a different but stable port
    /// instead of jumping around on every start.
    /// </summary>
    public static IReadOnlyList<int> Candidates(int preferred, int? stored)
    {
        var ports = new List<int>(2);
        if (preferred is > 0 and <= 65535) ports.Add(preferred);
        if (stored is > 0 and <= 65535 && !ports.Contains(stored.Value)) ports.Add(stored.Value);
        return ports;
    }
}
