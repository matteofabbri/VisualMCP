namespace VisualMCP.Startup;

/// <summary>
/// Carries the project memory that sits in another client's store (Claude Code's
/// <c>~/.claude/projects/&lt;slug&gt;/memory</c>) into the store the current client reads.
/// </summary>
public interface IMemoryImporter
{
    /// <summary>
    /// Runs the pass, or returns the result of the pass this process already ran. Idempotent and safe
    /// to call concurrently.
    /// </summary>
    Task<MemoryImportReport> RunAsync();

    /// <summary>
    /// Returns the report once per process if this pass moved something, so the client is told what changed
    /// and that the removed <c>memory_*</c> tools must not be called; null afterwards.
    /// </summary>
    Task<MemoryImportReport?> TakeNoticeAsync();
}
