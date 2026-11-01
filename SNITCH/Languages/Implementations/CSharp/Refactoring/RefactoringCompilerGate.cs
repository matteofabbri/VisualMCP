using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.Gate;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The compiler verdict for a refactoring, shared by the newer services so each one does not repeat
/// the "count the errors before, count them after, refuse when the count grew" loop.
/// </summary>
public sealed record CompilerGate(
    int Before,
    int After,
    IReadOnlyList<string> NewErrors,
    IReadOnlyList<string> Errors)
{
    public int Added => After - Before;

    public bool Passed => Added <= 0;
}

public static class RefactoringCompilerGate
{
    /// <summary>
    /// Compiles every affected project in <paramref name="before"/> and <paramref name="after"/> and
    /// reports the error counts plus the messages that did not exist before.
    /// </summary>
    /// <remarks>
    /// The verdict for <paramref name="before"/> is whatever <see cref="CompilationCache"/> already holds
    /// for that project state, and the verdict for <paramref name="after"/> is taken by replacing only the
    /// documents that actually changed, so a repeated gate does not recompile an unchanged solution.
    /// </remarks>
    public static async Task<CompilerGate> CompareAsync(
        Solution before,
        Solution after,
        IEnumerable<ProjectId> projectIds,
        CancellationToken cancellationToken = default)
    {
        var beforeCount = 0;
        var beforeMessages = new HashSet<string>(StringComparer.Ordinal);
        var afterMessages = new List<string>();

        foreach (var projectId in projectIds.Distinct())
        {
            var beforeProject = before.GetProject(projectId);
            var afterProject = after.GetProject(projectId);
            if (beforeProject is null || afterProject is null) continue;

            var baseline = await CompilationCache.Shared.GetAsync(beforeProject, cancellationToken);
            beforeCount += baseline.Errors.Count;
            foreach (var message in baseline.Messages) beforeMessages.Add(message);

            // Splicing is only equivalent to compiling the after solution when the document set is the
            // same; a file added or removed falls back to a real compilation of that project.
            var changes = await ChangedDocumentsAsync(beforeProject, afterProject, cancellationToken);
            var projected = changes is null
                ? await CompilationCache.Shared.GetAsync(afterProject, cancellationToken)
                : baseline.With(changes);

            afterMessages.AddRange(projected.Messages);
        }

        var newErrors = afterMessages
            .Where(message => !beforeMessages.Contains(message))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new CompilerGate(beforeCount, afterMessages.Count, newErrors, afterMessages.Take(8).ToList());
    }

    /// <summary>The documents whose text differs, or null when the two projects do not hold the same files.</summary>
    private static async Task<IReadOnlyList<(Document Document, SourceText Text)>?> ChangedDocumentsAsync(
        Project beforeProject,
        Project afterProject,
        CancellationToken cancellationToken)
    {
        var beforeByName = beforeProject.Documents.ToDictionary(document => document.Name, StringComparer.Ordinal);
        var afterByName = afterProject.Documents.ToDictionary(document => document.Name, StringComparer.Ordinal);
        if (beforeByName.Count != afterByName.Count || !beforeByName.Keys.All(afterByName.ContainsKey))
            return null;

        var changes = new List<(Document, SourceText)>();
        foreach (var (name, afterDocument) in afterByName)
        {
            var beforeText = await beforeByName[name].GetTextAsync(cancellationToken);
            var afterText = await afterDocument.GetTextAsync(cancellationToken);
            if (!beforeText.ContentEquals(afterText))
                changes.Add((afterDocument, afterText));
        }

        return changes;
    }

    /// <summary>The refusal body a service returns when the projected edit does not compile.</summary>
    public static object Refusal(CompilerGate gate, string what) => new
    {
        error = $"Refused: {what} would add {gate.Added} compiler error(s) ({gate.Before} -> {gate.After}).",
        errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
    };
}
