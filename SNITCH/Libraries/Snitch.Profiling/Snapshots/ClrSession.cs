using Microsoft.Diagnostics.Runtime;

namespace Snitch.Profiling.Snapshots;

/// <summary>
/// One ClrMD connection: a live process (attached or snapshotted) or a dump file. Everything that
/// needs the managed heap or the managed threads goes through this, so a dump and a live process
/// share the same analysis code.
/// </summary>
internal sealed class ClrSession : IDisposable
{
    private ClrSession(DataTarget target, ClrRuntime runtime)
    {
        Target = target;
        Runtime = runtime;
    }

    public DataTarget Target { get; }

    public ClrRuntime Runtime { get; }

    public string RuntimeVersion => Runtime.ClrInfo.Version.ToString();

    /// <summary>Attaches with the target suspended: the only safe mode for walking stacks.</summary>
    public static ClrSession AttachSuspended(int processId) =>
        Create(DataTarget.AttachToProcess(processId, suspend: true));

    /// <summary>
    /// Pauses the process only while its memory is copied, then walks the copy while the process
    /// keeps running. This is the right mode for a heap snapshot.
    /// </summary>
    public static ClrSession AttachSnapshot(int processId) =>
        Create(DataTarget.CreateSnapshotAndAttach(processId));

    public static ClrSession LoadDump(string dumpPath) =>
        Create(DataTarget.LoadDump(dumpPath));

    private static ClrSession Create(DataTarget target)
    {
        try
        {
            var info = target.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No CLR was found in the target. It is not a .NET process, or the target is a native-only process.");

            try
            {
                return new ClrSession(target, info.CreateRuntime());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not load the DAC for runtime {info.Version} ({info.Flavor}). " +
                    $"The DAC (mscordaccore.dll) must be available next to the target's runtime, or the dump needs the " +
                    $"matching runtime on this machine. {ex.Message}", ex);
            }
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { Runtime.Dispose(); } catch { /* the dump reader may already be closed */ }
        try { Target.Dispose(); } catch { /* best-effort */ }
    }
}
