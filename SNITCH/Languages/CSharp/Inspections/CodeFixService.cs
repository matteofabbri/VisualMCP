using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>What to fix: one diagnostic, identified the way <c>inspect_code</c> reports it.</summary>
public sealed record CodeFixRequest(
    string DiagnosticId,
    string FilePath,
    int Line,
    int? Column = null,
    string? FixTitle = null,
    bool DryRun = true,
    int MaxChangedFiles = 5,
    string? FixAllScope = null);

public interface ICodeFixService
{
    /// <summary>
    /// Runs the code fix of the inspection pack that reports <paramref name="diagnosticId"/> at the
    /// given location. With <see cref="CodeFixRequest.DryRun"/> the files are left untouched and the
    /// change is returned as a preview.
    /// </summary>
    Task<object> ApplyAsync(CodeFixRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The repair half of <see cref="InspectCodeService"/>: the same packs that report a diagnostic also
/// ship its <see cref="CodeFixProvider"/>, so the fix runs in-process instead of being re-invented.
/// </summary>
public sealed class CodeFixService : ICodeFixService
{
    private readonly IRoslynWorkspace _workspace;

    public CodeFixService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ApplyAsync(CodeFixRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DiagnosticId))
            return new { error = "A diagnostic id is required (e.g. RCS1060)." };
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return new { error = "A file path is required." };
        if (request.Line < 1)
            return new { error = "Line must be 1-based." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try
        {
            path = Path.GetFullPath(request.FilePath);
        }
        catch (Exception ex)
        {
            return new { error = $"Invalid path '{request.FilePath}': {ex.Message}" };
        }

        var document = solution.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(d => d.FilePath is not null &&
                                 string.Equals(Path.GetFullPath(d.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return new { error = $"{path} is not a document of the loaded solution." };

        var library = InspectionPackCatalog.Library;
        if (library.Packs.Count == 0)
            return new { error = "No inspection packs are loaded: " + string.Join(" ", library.Warnings) };

        var id = request.DiagnosticId;
        var packs = library.Packs
            .Where(pack => pack.Analyzers.Any(analyzer => analyzer.SupportedDiagnostics
                .Any(descriptor => string.Equals(descriptor.Id, id, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (packs.Count == 0)
            return new { error = $"No loaded inspection pack reports '{id}'.", packs = library.Packs.Select(p => p.Pack.Id).ToList() };

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return new { error = $"Project '{document.Project.Name}' produced no compilation." };

        var analyzers = packs.SelectMany(pack => pack.Analyzers).ToImmutableArray();

        // The packs keep most of their rules off by default (Roslynator disables almost all of them),
        // so the diagnostic the caller saw from inspect_code is reported only once they are enabled —
        // exactly the same two steps inspect_code takes, otherwise the fix finds nothing.
        compilation = InspectCodeService.EnableAllRules(
            compilation, InspectCodeService.DormantRules(analyzers));

        var diagnostics = await compilation
            .WithAnalyzers(analyzers, document.Project.AnalyzerOptions)
            .GetAnalyzerDiagnosticsAsync(cancellationToken);

        var candidates = diagnostics
            .Where(diagnostic => string.Equals(diagnostic.Id, id, StringComparison.OrdinalIgnoreCase))
            .Where(diagnostic => MatchesLocation(diagnostic, path, request.Line, request.Column))
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToList();

        if (candidates.Count == 0)
            return new
            {
                error = $"'{id}' is not reported at {Path.GetFileName(path)}:{request.Line} right now. " +
                        "Run inspect_code first: the diagnostic may have moved or been fixed already.",
            };

        if (candidates.Count > 1 && request.Column is null)
            return new
            {
                error = $"{candidates.Count} '{id}' diagnostics are reported on that line; pass column to pick one.",
                candidates = candidates.Select(Describe).ToList(),
            };

        var diagnostic = request.Column is null
            ? candidates[0]
            : candidates.FirstOrDefault(c => c.Location.GetLineSpan().StartLinePosition.Character + 1 == request.Column) ?? candidates[0];

        var fixers = packs.SelectMany(pack => pack.Fixers).ToList();
        var fixer = fixers.FirstOrDefault(f => f.FixableDiagnosticIds
            .Any(fixableId => string.Equals(fixableId, id, StringComparison.OrdinalIgnoreCase)));
        if (fixer is null)
            return new
            {
                error = $"No code fix provider in the loaded packs can fix '{id}'.",
                availableFixerIds = fixers.SelectMany(f => f.FixableDiagnosticIds).Distinct().Take(30).ToList(),
            };

        var actions = ImmutableArray.CreateBuilder<CodeAction>();
        var context = new CodeFixContext(
            document, diagnostic, (action, _) => actions.Add(action), cancellationToken);
        await fixer.RegisterCodeFixesAsync(context);

        if (actions.Count == 0)
            return new { error = $"The provider for '{id}' offered no fix at that location.", diagnostic = Describe(diagnostic) };

        var chosen = request.FixTitle is null
            ? actions[0]
            : actions.FirstOrDefault(action => action.Title.Contains(request.FixTitle, StringComparison.OrdinalIgnoreCase)) ?? actions[0];

        CodeAction applied = chosen;
        FixAllScope scope = default;
        var requestedScope = request.FixAllScope;
        var fixAll = !string.IsNullOrEmpty(requestedScope);

        if (fixAll)
        {
            scope = requestedScope!.ToLowerInvariant() switch
            {
                "document" => FixAllScope.Document,
                "project" => FixAllScope.Project,
                "solution" => FixAllScope.Solution,
                _ => (FixAllScope)(-1),
            };

            if ((int)scope < 0)
                return new { error = $"Unknown fixAllScope '{requestedScope}': use document, project or solution." };

            var fixAllProvider = fixer.GetFixAllProvider();
            if (fixAllProvider is null)
                return new { error = $"The provider for '{id}' has no FixAll support; apply the single fix instead." };

            if (chosen.EquivalenceKey is not { Length: > 0 } equivalenceKey)
                return new
                {
                    error = $"The fix '{chosen.Title}' exposes no equivalence key, so it cannot be repeated with FixAll. " +
                            "Apply the single fix instead.",
                    fixes = actions.Select(action => action.Title).ToList(),
                };

            // The diagnostic ids come from the provider; the diagnostics themselves come from a
            // DiagnosticProvider, which we back with the same analyzer packs used for the finding.
            var fixAllContext = new FixAllContext(
                document, fixer, scope, equivalenceKey, fixer.FixableDiagnosticIds,
                new PackDiagnosticProvider(analyzers, id), cancellationToken);

            var fixAllAction = await fixAllProvider.GetFixAsync(fixAllContext);
            if (fixAllAction is null)
                return new { error = $"FixAll ({scope}) produced no action for '{id}'." };

            applied = fixAllAction;
        }

        var operations = await applied.GetOperationsAsync(cancellationToken);
        var changedSolution = operations
            .OfType<ApplyChangesOperation>()
            .Select(operation => operation.ChangedSolution)
            .FirstOrDefault();
        if (changedSolution is null)
            return new { error = $"The fix '{chosen.Title}' produced no solution change.", fixes = actions.Select(a => a.Title).ToList() };

        var changes = await CollectChangesAsync(solution, changedSolution, cancellationToken);
        if (changes.Count == 0)
            return new { error = $"The fix '{chosen.Title}' reported success but no document text changed." };

        if (changes.Count > request.MaxChangedFiles)
            return new
            {
                error = $"The fix would change {changes.Count} files, more than maxChangedFiles={request.MaxChangedFiles}. " +
                        "Raise maxChangedFiles if that is intended.",
                files = changes.Select(change => change.File).ToList(),
            };

        if (!request.DryRun)
        {
            foreach (var change in changes)
                WritePreservingBom(change.File, change.NewText);
        }

        return new
        {
            diagnosticId = id,
            file = path,
            line = request.Line,
            fix = fixAll ? $"{chosen.Title} (FixAll: {scope})" : chosen.Title,
            alternativeFixes = actions.Select(action => action.Title).ToList(),
            dryRun = request.DryRun,
            applied = !request.DryRun,
            changedFiles = changes.Select(change => new
            {
                file = change.File,
                oldLines = CountLines(change.OldText),
                newLines = CountLines(change.NewText),
                preview = Preview(change.OldText, change.NewText),
            }).ToList(),
            note = request.DryRun
                ? "Preview only: pass dryRun=false to write these files."
                : "Files written and the in-memory solution updated; re-run inspect_code to re-analyze.",
        };
    }

    /// <summary>
    /// Supplies the diagnostics a FixAll pass must cover: it re-runs the packs on each project it is
    /// asked about and keeps the result, because FixAll asks for the same project several times.
    /// </summary>
    private sealed class PackDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
        private readonly string _id;
        private readonly Dictionary<ProjectId, ImmutableArray<Diagnostic>> _cache = new();
        private readonly object _lock = new();

        public PackDiagnosticProvider(ImmutableArray<DiagnosticAnalyzer> analyzers, string id)
        {
            _analyzers = analyzers;
            _id = id;
        }

        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
            Document document, CancellationToken cancellationToken)
        {
            var path = document.FilePath;
            if (path is null) return Array.Empty<Diagnostic>();

            var all = await ForProjectAsync(document.Project, cancellationToken);
            return all.Where(diagnostic => diagnostic.Location.SourceTree?.FilePath is { } file &&
                                           string.Equals(Path.GetFullPath(file), Path.GetFullPath(path),
                                                         StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
            Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
            Project project, CancellationToken cancellationToken) =>
            await ForProjectAsync(project, cancellationToken);

        private async Task<ImmutableArray<Diagnostic>> ForProjectAsync(Project project, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(project.Id, out var cached)) return cached;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) return ImmutableArray<Diagnostic>.Empty;

            compilation = InspectCodeService.EnableAllRules(
                compilation, InspectCodeService.DormantRules(_analyzers));

            var diagnostics = await compilation
                .WithAnalyzers(_analyzers, project.AnalyzerOptions)
                .GetAnalyzerDiagnosticsAsync(cancellationToken);

            var matching = diagnostics.Where(diagnostic => diagnostic.Id == _id).ToImmutableArray();
            lock (_lock) _cache[project.Id] = matching;
            return matching;
        }
    }

    private static bool MatchesLocation(Diagnostic diagnostic, string path, int line, int? column)
    {
        var span = diagnostic.Location.GetLineSpan();
        if (span.Path is null) return false;
        if (!string.Equals(Path.GetFullPath(span.Path), path, StringComparison.OrdinalIgnoreCase)) return false;
        if (span.StartLinePosition.Line + 1 != line) return false;
        return column is null || span.StartLinePosition.Character + 1 == column;
    }

    private static object Describe(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new
        {
            id = diagnostic.Id,
            severity = diagnostic.Severity.ToString(),
            message = diagnostic.GetMessage(),
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
        };
    }

    private static async Task<List<(string File, string OldText, string NewText)>> CollectChangesAsync(
        Solution original, Solution changed, CancellationToken cancellationToken)
    {
        var changes = new List<(string, string, string)>();

        foreach (var projectId in changed.ProjectIds)
        {
            var newProject = changed.GetProject(projectId);
            var oldProject = original.GetProject(projectId);
            if (newProject is null || oldProject is null) continue;

            foreach (var newDocument in newProject.Documents)
            {
                if (newDocument.FilePath is null) continue;
                var oldDocument = oldProject.GetDocument(newDocument.Id);
                if (oldDocument?.FilePath is null) continue;

                SourceText oldText;
                SourceText newText;
                try
                {
                    oldText = await oldDocument.GetTextAsync(cancellationToken);
                    newText = await newDocument.GetTextAsync(cancellationToken);
                }
                catch (Exception)
                {
                    continue;
                }

                if (oldText.ContentEquals(newText)) continue;
                changes.Add((newDocument.FilePath, oldText.ToString(), newText.ToString()));
            }
        }

        return changes;
    }

    /// <summary>Writes the file without changing its byte-order-mark convention.</summary>
    private static void WritePreservingBom(string path, string text)
    {
        var hasBom = false;
        try
        {
            var head = File.ReadAllBytes(path);
            hasBom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (IOException)
        {
            // Unreadable file: fall through and write without a BOM.
        }

        File.WriteAllText(path, text, new UTF8Encoding(hasBom));
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Split('\n').Length;

    /// <summary>Common prefix/suffix line diff, capped so one preview cannot flood the result.</summary>
    private static string Preview(string oldText, string newText, int maxLines = 10)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var builder = new StringBuilder();
        builder.Append("@@ line ").Append(prefix + 1).AppendLine(" @@");

        var removed = oldLines.Length - suffix - prefix;
        var added = newLines.Length - suffix - prefix;
        for (var i = 0; i < Math.Min(removed, maxLines); i++)
            builder.Append("- ").Append(oldLines[prefix + i].TrimEnd('\r')).AppendLine();
        for (var i = 0; i < Math.Min(added, maxLines); i++)
            builder.Append("+ ").Append(newLines[prefix + i].TrimEnd('\r')).AppendLine();
        if (removed > maxLines || added > maxLines)
            builder.Append("… (preview truncated)");

        return builder.ToString().TrimEnd('\n');
    }
}
