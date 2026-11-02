using System.Diagnostics;
using System.Globalization;

namespace VisualMCP.Dashboard;

/// <summary>
/// One dashboard instance that bound a port on this workspace, as kept in a
/// <c>dashboard.port.&lt;pid&gt;</c> row of the store's <c>context</c> table. Each process writes only
/// its own row, so two live servers on the same workspace no longer overwrite each other's port; the
/// pid plus its start time is what makes a recycled pid recognizable.
/// </summary>
public sealed record DashboardPort(int Port, int Pid, long StartedTicks)
{
    /// <summary>The entry for this process, binding <paramref name="port"/>.</summary>
    public static DashboardPort Mine(int port)
    {
        long started = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // A start time we cannot read costs only the pid-reuse check.
        }

        return new DashboardPort(port, Environment.ProcessId, started);
    }

    public string Serialize() => string.Create(CultureInfo.InvariantCulture, $"{Port}|{Pid}|{StartedTicks}");

    public static DashboardPort? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split('|');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return null;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var started)) return null;

        return new DashboardPort(port, pid, started);
    }

    /// <summary>True when that process is still running in that incarnation (so its port is taken).</summary>
    public bool IsAlive()
    {
        try
        {
            using var process = Process.GetProcessById(Pid);
            if (StartedTicks == 0) return true;

            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - StartedTicks) < TimeSpan.FromSeconds(2).Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
