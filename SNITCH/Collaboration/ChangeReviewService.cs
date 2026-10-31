using System.Text.RegularExpressions;
using Snitch.Execution;
using Snitch.Git;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Workspace;

namespace Snitch.Collaboration;

/// <summary>
/// A review brief for a change: what the diff touches, the signals a reviewer looks for first (a
/// deleted test, a source change with no test next to it, a large diff, a new TODO), the worst unit
/// in each changed C# file, and optionally the inspection findings that land in the changed files.
/// It is a workflow over data the server already has, not a second analysis engine.
/// </summary>
public interface IChangeReviewService
{
    Task<object> ReviewAsync(string baseRef, string? pathFilter, int maxFiles, bool includeInspections, string? repoPath, int timeoutSeconds);
}

public sealed class ChangeReviewService : IChangeReviewService
{
    private static readonly Regex HunkHeader =
        new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    private static readonly Regex TodoMarker =
        new(@"\b(TODO|FIXME|HACK|XXX)\b", RegexOptions.Compiled);

    private readonly IRoslynWorkspace _workspace;
    private readonly IInspectCodeService? _inspections;

    public ChangeReviewService(IRoslynWorkspace workspace, IInspectCodeService? inspections = null)
    {
        _workspace = workspace;
        _inspections = inspections;
    }

    public async Task<object> ReviewAsync(string baseRef, string? pathFilter, int maxFiles, bool includeInspections,
        string? repoPath, int timeoutSeconds)
    {
        var reference = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        if (reference.StartsWith('-')) return new { error = $"'{baseRef}' is not a revision or range." };

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath, _workspace.LoadedSolutionPath);
        if (error is not null) return error;

        var suffix = string.IsNullOrWhiteSpace(pathFilter) ? "" : $" -- {Q(pathFilter!)}";

        var names = await GitCli.RunAsync(repoDir!, $"diff --name-status -M {Q(reference)}{suffix}", 60);
        if (names.exitCode != 0) return new { error = $"git diff failed: {names.stderr.Trim()}", baseRef = reference };

        var numbers = await GitCli.RunAsync(repoDir!, $"diff --numstat -M {Q(reference)}{suffix}", 60);
        if (numbers.exitCode != 0) return new { error = $"git diff --numstat failed: {numbers.stderr.Trim()}", baseRef = reference };

        var diff = await GitCli.RunAsync(repoDir!, $"diff -M {Q(reference)}{suffix}", 120);
        if (diff.timedOut) return new { error = "git diff timed out." };

        var stats = ParseNumStat(numbers.stdout);
        var files = new List<ChangedFile>();

        foreach (var raw in names.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2) continue;

            var parts = line.Split('\t');
            var status = parts[0].Trim();
            var path = parts.Length > 1 ? parts[^1] : "";
            if (string.IsNullOrWhiteSpace(path)) continue;

            var key = path.Trim('"');
            stats.TryGetValue(key, out var counts);
            files.Add(new ChangedFile(key, status, counts.Added, counts.Deleted));
        }

        var todos = ParseTodos(diff.stdout, out var diffFiles);
        var sourceFiles = files.Where(f => KindOf(f.Path) == "source").ToList();
        var testFiles = files.Where(f => KindOf(f.Path) == "test").ToList();
        var deleted = files.Where(f => f.Status.StartsWith('D')).ToList();
        var renamed = files.Where(f => f.Status.StartsWith('R') || f.Status.StartsWith('C')).ToList();

        var checklist = BuildChecklist(files, sourceFiles, testFiles, deleted, todos);
        var complexity = await ComplexityAsync(repoDir!, sourceFiles.Take(25).ToList());

        var inspections = includeInspections
            ? await InspectionsAsync(repoDir!, files, timeoutSeconds)
            : null;

        var limit = Math.Clamp(maxFiles, 1, 500);
        var additions = files.Sum(f => f.Added);
        var deletions = files.Sum(f => f.Deleted);

        return new
        {
            repoDir,
            baseRef = reference,
            range = $"{reference}..working tree",
            fileCount = files.Count,
            additions,
            deletions,
            summary = new
            {
                source = sourceFiles.Count,
                tests = testFiles.Count,
                docs = files.Count(f => KindOf(f.Path) == "docs"),
                ui = files.Count(f => KindOf(f.Path) == "ui"),
                config = files.Count(f => KindOf(f.Path) == "config"),
                deleted = deleted.Count,
                renamed = renamed.Count,
                binary = files.Count(f => f.Binary),
            },
            checklist,
            todos = todos.Select(t => new { file = t.File, line = t.Line, marker = t.Marker, text = t.Text }).ToList(),
            diffFiles = diffFiles.Count,
            files = files.Take(limit).Select(f => new
            {
                path = f.Path,
                status = DescribeStatus(f.Status),
                additions = f.Added,
                deletions = f.Deleted,
                kind = KindOf(f.Path),
                binary = f.Binary,
                complexity = complexity.TryGetValue(f.Path, out var worst) ? worst : null,
            }).ToList(),
            inspections,
            verdict = Verdict(files.Count, checklist, additions, deletions),
            nextSteps = new[]
            {
                "Open the files the checklist names, then the changed methods complexity lists.",
                "confirm the change is covered: run_affected_tests runs exactly the tests that reference the changed symbols.",
                "After review, find_breaking_changes compares the public surface with this baseRef.",
            },
        };
    }

    // ── files and checklist ──────────────────────────────────────────────────
    private sealed record ChangedFile(string Path, string Status, int Added, int Deleted)
    {
        public bool Binary => Added < 0;
    }

    private static (int Added, int Deleted) ParseNumStatValue(string added, string deleted) =>
        int.TryParse(added, out var a) && int.TryParse(deleted, out var d) ? (a, d) : (-1, -1);

    private static Dictionary<string, (int Added, int Deleted)> ParseNumStat(string stdout)
    {
        var map = new Dictionary<string, (int Added, int Deleted)>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var path = parts[^1].Trim('"');

            // A rename prints "old => new", or "dir/{old => new}/file" when only part of the name
            // changed; the other listing (name-status) has the new path, so match on that.
            var braced = Regex.Match(path, @"\{(?<from>[^{}]*) => (?<to>[^{}]*)\}");
            if (braced.Success)
            {
                path = path[..braced.Index] + braced.Groups["to"].Value + path[(braced.Index + braced.Length)..];
            }
            else
            {
                var arrow = path.IndexOf(" => ", StringComparison.Ordinal);
                if (arrow >= 0) path = path[(arrow + 4)..];
            }

            map[path.Trim().Trim('"')] = ParseNumStatValue(parts[0], parts[1]);
        }

        return map;
    }

    private static List<object> BuildChecklist(
        List<ChangedFile> files, List<ChangedFile> sourceFiles, List<ChangedFile> testFiles,
        List<ChangedFile> deleted, List<TodoNote> todos)
    {
        var checklist = new List<object>();

        var deletedTests = deleted.Where(f => KindOf(f.Path) == "test").ToList();
        if (deletedTests.Count > 0)
            checklist.Add(new
            {
                severity = "high",
                kind = "deleted-test",
                message = $"{deletedTests.Count} test file(s) deleted: {string.Join(", ", deletedTests.Select(f => f.Path).Take(5))}.",
                files = deletedTests.Select(f => f.Path).ToList(),
            });

        if (sourceFiles.Count > 0 && testFiles.Count == 0)
            checklist.Add(new
            {
                severity = "warning",
                kind = "no-test-change",
                message = $"{sourceFiles.Count} source file(s) changed and no test file did — check that the change is covered, or say why not.",
                files = sourceFiles.Select(f => f.Path).Take(5).ToList(),
            });

        if (deletedTests.Count == 0 && deleted.Count > 0)
            checklist.Add(new
            {
                severity = "warning",
                kind = "deleted-files",
                message = $"{deleted.Count} file(s) deleted: {string.Join(", ", deleted.Select(f => f.Path).Take(5))}.",
                files = deleted.Select(f => f.Path).ToList(),
            });

        var big = files.Where(f => f.Added + f.Deleted >= 400).OrderByDescending(f => f.Added + f.Deleted).ToList();
        if (big.Count > 0)
            checklist.Add(new
            {
                severity = "warning",
                kind = "large-change",
                message = $"{big.Count} file(s) change 400+ lines; a reviewer reads these first: {string.Join(", ", big.Select(f => $"{f.Path} (+{f.Added}/-{f.Deleted})").Take(5))}.",
                files = big.Select(f => f.Path).ToList(),
            });

        if (todos.Count > 0)
            checklist.Add(new
            {
                severity = "info",
                kind = "new-todo",
                message = $"{todos.Count} TODO/FIXME marker(s) were added by this change.",
                files = todos.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            });

        return checklist;
    }

    private static string Verdict(int fileCount, List<object> checklist, int additions, int deletions)
    {
        if (fileCount == 0) return "Nothing to review: the diff against the base is empty.";

        var risky = checklist.Count;
        return risky == 0
            ? $"{fileCount} file(s), +{additions}/-{deletions}: no risk signal in the diff — review the changed methods and run the affected tests."
            : $"{fileCount} file(s), +{additions}/-{deletions}: {risky} signal(s) to look at first — the checklist lists them with their files.";
    }

    // ── new markers in added lines ──────────────────────────────────────────
    public sealed record TodoNote(string File, int Line, string Marker, string Text);

    /// <summary>Every TODO/FIXME/HACK/XXX that appears on an added line, with its line in the new file.</summary>
    public static List<TodoNote> ParseTodos(string diffText, out List<string> files)
    {
        var todos = new List<TodoNote>();
        files = new List<string>();

        string? file = null;
        var newLine = 0;

        foreach (var raw in diffText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                file = line["+++ b/".Length..];
                if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeader.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var start)) newLine = start;
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (file is not null)
                {
                    var marker = TodoMarker.Match(line);
                    if (marker.Success)
                        todos.Add(new TodoNote(file, newLine, marker.Groups[1].Value, line[1..].Trim()));
                }

                newLine++;
            }
            else if (line.StartsWith(' ') && file is not null)
            {
                newLine++;
            }
        }

        return todos;
    }

    // ── complexity of the changed files ─────────────────────────────────────
    private async Task<Dictionary<string, object>> ComplexityAsync(string repoDir, List<ChangedFile> sourceFiles)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count == 0 || !sourceFiles.Any(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            return result;

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return result;

        var documents = solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => document.FilePath is not null)
            .ToDictionary(document => Path.GetFullPath(document.FilePath!), document => document, StringComparer.OrdinalIgnoreCase);

        foreach (var file in sourceFiles.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var absolute = Path.GetFullPath(Path.Combine(repoDir, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!documents.TryGetValue(absolute, out var document))
            {
                // The file may be deleted (D) or outside the loaded solution: both are legitimately absent.
                continue;
            }

            var root = await document.GetSyntaxRootAsync();
            if (root is null) continue;

            var model = await document.GetSemanticModelAsync();
            var worst = ComplexityAnalyzer.Units(root)
                .Select(unit => ComplexityAnalyzer.Analyze(unit, model, document.FilePath))
                .Where(metrics => metrics.Cyclomatic >= 8 || metrics.Cognitive >= 8)
                .OrderByDescending(metrics => metrics.Cognitive)
                .ThenByDescending(metrics => metrics.Cyclomatic)
                .Take(3)
                .ToList();

            if (worst.Count == 0) continue;

            result[file.Path] = worst.Select(metrics => new
            {
                method = metrics.Name,
                line = metrics.Line,
                cyclomatic = metrics.Cyclomatic,
                cognitive = metrics.Cognitive,
                risk = metrics.Risk,
                recommendation = "complexity_report on this method shows the decision points; reduce_complexity can lower it.",
            }).ToList();
        }

        return result;
    }

    // ── inspections on the changed files ────────────────────────────────────
    private async Task<object> InspectionsAsync(string repoDir, List<ChangedFile> files, int timeoutSeconds)
    {
        if (_inspections is null)
            return new { ran = false, reason = "The inspection engine is not available in this host." };

        var changed = new HashSet<string>(
            files.Select(f => Path.GetFullPath(Path.Combine(repoDir, f.Path.Replace('/', Path.DirectorySeparatorChar)))),
            StringComparer.OrdinalIgnoreCase);

        var report = await _inspections.RunAsync(
            projectName: null, filePath: null, packs: "", minSeverity: "warning",
            ids: null, excludeIds: null, enableAll: true, maxResults: 3000,
            timeoutSeconds: Math.Clamp(timeoutSeconds, 10, 300));

        var findings = report.Findings
            .Where(finding => changed.Contains(Path.GetFullPath(finding.File)))
            .Take(100)
            .Select(finding => new
            {
                id = finding.InspectionId,
                severity = finding.Severity,
                message = finding.Message,
                file = finding.File,
                line = finding.Line,
            })
            .ToList();

        return new
        {
            ran = true,
            state = report.State,
            changedFileFindings = findings.Count,
            findings,
            note = findings.Count == 0
                ? "No inspection finding lands in the changed files."
                : "inspect_code reports these with fixes; apply_code_fix applies the ones that ship one.",
        };
    }

    // ── classification ──────────────────────────────────────────────────────
    /// <summary>test | source | ui | docs | config | other — what a reviewer groups by first.</summary>
    public static string KindOf(string path)
    {
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileName(lower);
        var segments = lower.Split('/');

        if (segments.Any(segment => segment is "test" or "tests" or "testing" or "specs") ||
            name.Contains(".tests.", StringComparison.Ordinal) ||
            name.EndsWith("tests.cs", StringComparison.Ordinal) ||
            name.EndsWith("test.cs", StringComparison.Ordinal))
            return "test";

        var extension = Path.GetExtension(lower);
        return extension switch
        {
            ".cs" or ".vb" => "source",
            ".razor" or ".cshtml" or ".xaml" => "ui",
            ".md" or ".markdown" or ".txt" or ".rst" => "docs",
            ".json" or ".jsonc" or ".yml" or ".yaml" or ".xml" or ".props" or ".targets" or
            ".csproj" or ".vbproj" or ".sln" or ".slnx" or ".editorconfig" or ".config" => "config",
            _ => "other",
        };
    }

    private static string DescribeStatus(string status) => status switch
    {
        "M" => "modified",
        "A" => "added",
        "D" => "deleted",
        var s when s.StartsWith('R') => "renamed",
        var s when s.StartsWith('C') => "copied",
        "T" => "type-changed",
        _ => status,
    };

    private static string Q(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
