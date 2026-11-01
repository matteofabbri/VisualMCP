using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Storage;

/// <summary>
/// Undo for the edits this server made. The history already records what changed and its hashes; this
/// service restores the text that was there before — refusing when the file moved on since, because
/// then the "undo" would silently overwrite somebody else's work.
/// </summary>
public interface IEditHistoryService
{
    Task<RevertEditResult> RevertAsync(long editId, CancellationToken cancellationToken = default);
}

public sealed class EditHistoryService : IEditHistoryService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore _store;

    public EditHistoryService(IRoslynWorkspace workspace, IVisualMcpStore store)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<RevertEditResult> RevertAsync(long editId, CancellationToken cancellationToken = default)
    {
        var edit = _store.Edit(editId);
        if (edit is null) return new RevertEditResult { Error = $"No edit #{editId} in the history." };
        if (!File.Exists(edit.File))
            return new RevertEditResult { Error = $"The file no longer exists: {edit.File}", EditId = editId };

        var previous = _store.EditBeforeText(editId);
        if (previous is null)
            return new RevertEditResult
            {
                Error = "That edit was recorded without its previous text, so it cannot be reverted.",
                EditId = editId,
                Hint = "Only edits written after the history started keeping content are revertible.",
            };

        var current = await File.ReadAllTextAsync(edit.File, cancellationToken);

        // The guard: the file must still be exactly what that edit left behind.
        if (VisualMcpStore.ContentHash(current) != edit.AfterHash)
            return new RevertEditResult
            {
                Error = "The file changed after that edit: reverting would overwrite newer work.",
                EditId = editId,
                File = edit.File,
                Hint = "Read edit_history for that file to see what happened since.",
            };

        Platform.FileText.Write(edit.File, previous);

        // Keep the model in step with the file, so the next tool call does not read the reverted text.
        var document = _workspace.CurrentSolution?.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(candidate.FilePath, edit.File, StringComparison.OrdinalIgnoreCase));

        if (document is not null)
            _workspace.TryUpdateDocument(document.Id, SourceText.From(previous));

        // The revert is itself a write: it goes into the history like any other.
        _store.RecordEdit("revert_edit", edit.File, edit.Rules, current, previous);

        return new RevertEditResult
        {
            Reverted = editId,
            File = edit.File,
            RestoredChars = previous.Length,
            DiscardedChars = current.Length,
            UndoneTool = edit.Tool,
            UndoneRules = edit.Rules,
            AppliedAt = edit.AppliedAt.ToString("O"),
            Note = "The file now holds the text it had before that edit; the revert is recorded as a new edit.",
        };
    }
}
