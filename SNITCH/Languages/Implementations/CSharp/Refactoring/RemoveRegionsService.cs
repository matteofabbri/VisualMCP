using Microsoft.CodeAnalysis;
using System.ComponentModel;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

public interface IRemoveRegionsService
{
    Task<object> RemoveRegions(string? projectName, string? filePath, bool dryRun);
}

public sealed class RemoveRegionsService : IRemoveRegionsService
{
    private readonly IRoslynWorkspace _workspace;
    public RemoveRegionsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RemoveRegions(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Dry run — report which files would change without writing (default: false)")] bool dryRun = false)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<object>();
        int totalRegions = 0;

        foreach (var project in projects)
        {
            var docs = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular &&
                            (filePath is null || string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var document in docs)
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null || document.FilePath is null) continue;

                // Collect all #region and #endregion directives, in either language: C# writes
                // "#region …", VB writes "#Region ""…"" / #End Region", and both end '#endregion'.
                var directives = root.DescendantTrivia()
                    .Where(IsRegionDirective)
                    .ToList();

                if (directives.Count == 0) continue;

                totalRegions += directives.Count(IsRegionStart);

                // Remove directive lines from the source text
                // Work on raw text: remove the entire line for each directive
                var text = (await document.GetTextAsync()).ToString();
                var lines = text.Split('\n').ToList();

                var linesToRemove = new HashSet<int>();
                foreach (var trivia in directives)
                {
                    var lineSpan = trivia.GetLocation().GetLineSpan();
                    linesToRemove.Add(lineSpan.StartLinePosition.Line);
                }

                var newLines = lines
                    .Select((line, idx) => (line, idx))
                    .Where(x => !linesToRemove.Contains(x.idx))
                    .Select(x => x.line)
                    .ToList();

                var newText = string.Join('\n', newLines);

                results.Add(new
                {
                    document.FilePath,
                    RegionsRemoved = directives.Count(IsRegionStart),
                });

                if (!dryRun)
                    await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText);
            }
        }

        return new
        {
            dryRun,
            projectFilter = projectName ?? "all",
            fileFilter = filePath ?? "all",
            filesModified = results.Count,
            totalRegionsRemoved = totalRegions,
            files = results,
        };
    }

    /// <summary>Whether a trivia is a region or an end-region directive, in C# or in Visual Basic.</summary>
    private static bool IsRegionDirective(SyntaxTrivia trivia)
    {
        if (!trivia.IsDirective) return false;

        var text = CompactDirective(trivia);
        return text.StartsWith("#region", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegionStart(SyntaxTrivia trivia) =>
        CompactDirective(trivia).StartsWith("#region", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>#End Region</c> and <c>#endregion</c> are the same directive written two ways.</summary>
    private static string CompactDirective(SyntaxTrivia trivia) =>
        string.Concat(trivia.ToString().TrimStart().Where(character => !char.IsWhiteSpace(character)));
}
