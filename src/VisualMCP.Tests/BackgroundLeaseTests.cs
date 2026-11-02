using System.Globalization;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The cross-process lease that keeps one metacode watcher per workspace. It is a <c>context</c> row,
/// so a second server on the same store sees it; the pid plus the process start time is what tells a
/// live owner from a crashed one whose pid may have been reused.
/// </summary>
public sealed class BackgroundLeaseTests : IDisposable
{
    private const string Lease = "metacode-watch";

    private readonly FakeWorkspace _workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
    private readonly VisualMcpStore _store;

    public BackgroundLeaseTests() => _store = new VisualMcpStore(_workspace);

    public void Dispose()
    {
        _store.Dispose();
        _workspace.Dispose();
    }

    [Fact]
    public void The_owner_renews_its_own_lease()
    {
        Assert.True(_store.TryAcquireBackgroundLease(Lease, TimeSpan.FromMinutes(1)));
        Assert.True(_store.TryAcquireBackgroundLease(Lease, TimeSpan.FromMinutes(1)));

        var owner = _store.Get("lease." + Lease)!.Value;
        Assert.StartsWith(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|", owner);
    }

    [Fact]
    public void A_dead_owners_lease_is_taken_over()
    {
        // A pid that is not running: the heartbeat is fresh, but the process is gone.
        _store.Set("lease." + Lease,
            "999999|0|" + DateTimeOffset.UtcNow.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));

        Assert.True(_store.TryAcquireBackgroundLease(Lease, TimeSpan.FromMinutes(1)));
        Assert.StartsWith(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|",
            _store.Get("lease." + Lease)!.Value);
    }

    [Fact]
    public void Releasing_does_not_drop_another_processes_lease()
    {
        var foreign = "999999|0|" + DateTimeOffset.UtcNow.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        _store.Set("lease." + Lease, foreign);

        _store.ReleaseBackgroundLease(Lease);

        Assert.Equal(foreign, _store.Get("lease." + Lease)!.Value);
    }
}
