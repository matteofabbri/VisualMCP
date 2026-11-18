using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Storage.Schema;

namespace Snitch.Storage;

/// <summary>
/// Cross-process background leases, kept in the same <c>context</c> table as the other small facts.
/// A client starts one server per session, and two sessions on the same workspace share this
/// database: without a lease both would run the metacode watcher over the same files. The owner is
/// a pid plus the process start time (so a recycled pid is not mistaken for the old owner), and the
/// heartbeat is what makes a crashed owner's lease stealable.
/// </summary>
public sealed partial class VisualMcpStore
{
    /// <summary>Where the lease rows live: one <c>context</c> key per named lease.</summary>
    private const string LeasePrefix = "lease.";

    private static readonly long OwnStartTicks = ReadOwnStartTicks();

    public bool TryAcquireBackgroundLease(string name, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var connection = Connection();
        if (connection is null) return false;

        lock (_gate)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var key = LeasePrefix + name;

                using var context = VisualMcpSchema.CreateContext(connection);
                var fact = context.ContextFacts.FirstOrDefault(row => row.Key == key);
                var owner = LeaseOwner.Parse(fact?.Value);

                // A live owner that has beaten recently keeps it. Our own row is renewed; a dead
                // owner, a stale heartbeat or an unreadable value is taken over.
                if (owner is not null && !owner.IsSelf() && owner.IsAlive() &&
                    now.UtcDateTime.Ticks - owner.HeartbeatTicks < ttl.Ticks)
                    return false;

                var value = LeaseOwner.Mine(now).Serialize();
                if (fact is null)
                {
                    context.ContextFacts.Add(new ContextFactRow
                    {
                        Key = key,
                        Value = value,
                        UpdatedAt = now,
                    });
                }
                else
                {
                    fact.Value = value;
                    fact.UpdatedAt = now;
                }

                context.SaveChanges();
                return true;
            }
            catch (Exception ex) when (ex is SqliteException or DbUpdateException)
            {
                // No lease means no background work, never a failed tool call.
                return false;
            }
        }
    }

    public void ReleaseBackgroundLease(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var connection = Connection();
        if (connection is null) return;

        lock (_gate)
        {
            try
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                var key = LeasePrefix + name;
                var fact = context.ContextFacts.FirstOrDefault(row => row.Key == key);
                if (fact is null) return;

                var owner = LeaseOwner.Parse(fact.Value);
                if (owner is not null && !owner.IsSelf()) return;   // not ours to drop

                context.ContextFacts.Remove(fact);
                context.SaveChanges();
            }
            catch (Exception ex) when (ex is SqliteException or DbUpdateException)
            {
                // The next owner steals it once the heartbeat goes stale.
            }
        }
    }

    private static long ReadOwnStartTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <summary>The stored lease: who owns it, which incarnation of that pid, and its last heartbeat.</summary>
    private sealed record LeaseOwner(int Pid, long StartedTicks, long HeartbeatTicks)
    {
        public static LeaseOwner Mine(DateTimeOffset heartbeat) =>
            new(Environment.ProcessId, OwnStartTicks, heartbeat.UtcDateTime.Ticks);

        public string Serialize() =>
            string.Create(CultureInfo.InvariantCulture, $"{Pid}|{StartedTicks}|{HeartbeatTicks}");

        public static LeaseOwner? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var parts = value.Split('|');
            if (parts.Length != 3) return null;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var started)) return null;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var heartbeat)) return null;

            return new LeaseOwner(pid, started, heartbeat);
        }

        /// <summary>This process, in this incarnation — the start time is what defeats a recycled pid.</summary>
        public bool IsSelf() =>
            Pid == Environment.ProcessId && (StartedTicks == 0 || StartedTicks == OwnStartTicks);

        public bool IsAlive()
        {
            try
            {
                using var process = Process.GetProcessById(Pid);
                if (StartedTicks == 0) return true;

                // Windows can report the start time a little differently across sessions; a two-second
                // window is enough to tell an incarnation apart without depending on sub-second precision.
                var started = process.StartTime.ToUniversalTime().Ticks;
                return Math.Abs(started - StartedTicks) < TimeSpan.FromSeconds(2).Ticks;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
