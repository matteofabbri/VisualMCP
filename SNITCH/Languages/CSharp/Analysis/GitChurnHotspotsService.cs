using Snitch.Git;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// "Where does a refactoring pay first?" — the CodeScene question, answered from data this server
/// already has. A file is a hotspot when it changes often (git history) and is hard to read
/// (cyclomatic complexity from Roslyn): either signal alone points at the wrong file. Churn is read
/// from <c>git log --numstat</c> over a bounded window; complexity comes from the same ranking
/// <c>complexity_report</c> uses, so the two numbers describe the same code.
/// </summary>
public interface IGitChurnHotspotsService
{
    Task<object> AnalyzeAsync(
        string? repoPath = null,
        string? projectName = null,
        string? since = null,
        string? path = null,
        int top = 25,
        int minCommits = 3,
        int maxCommits = 2000,
        CancellationToken cancellationToken = default);
}

public sealed class GitChurnHotspotsService : IGitChurnHotspotsService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IComplexityReportService _report;

    public GitChurnHotspotsService(IRoslynWorkspace workspace, IComplexityReportService report)
    {
        _workspace = workspace;
        _report = report;
    }

    public async Task<object> AnalyzeAsync(
        string? repoPath = null,
        string? projectName = null,
        string? since = null,
        string? path = null,
        int top = 25,
        int minCommits = 3,
        int maxCommits = 2000,
        CancellationToken cancellationToken = default)
    {
        if (top < 1) top = 1;
        if (top > 500) top = 500;
        if (minCommits < 1) minCommits = 1;
        if (maxCommits < 1) maxCommits = 1;
        since = string.IsNullOrWhiteSpace(since) ? "1 year ago" : since!.Trim();

        var (repoDir, resolveError) = await GitCli.ResolveRepoAsync(repoPath, _workspace.LoadedSolutionPath);
        if (resolveError is not null) return resolveError;

        // Rename detection off on purpose: a rename is a delete plus an add, which is churn.
        var arguments = $"log --no-merges --no-renames --numstat --pretty=format:%x00%H " +
                        $"--max-count={maxCommits} --since=\"{since}\"";

        if (!string.IsNullOrWhiteSpace(path)) arguments += $" -- \"{path!.Trim()}\"";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, arguments, 60);
        if (timedOut)
            return new { error = $"git log timed out after 60s reading the churn of {repoDir}. Narrow it with path or since." };

        if (exitCode != 0)
        {
            // A repository with no commit yet is not a failure: there is no history to combine.
            var empty = stdout.Length == 0 &&
                        (stderr.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase) ||
                         stderr.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
                         stderr.Contains("ambiguous argument 'HEAD'", StringComparison.OrdinalIgnoreCase) ||
                         stderr.Contains("non \u00e8 ancora stato effettuato alcun commit", StringComparison.OrdinalIgnoreCase));

            if (!empty)
                return new
                {
                    tool = "git_churn_hotspots",
                    repo = repoDir,
                    commitsScanned = 0,
                    hotspots = Array.Empty<object>(),
                    error = $"git log failed: {stderr.Trim()}",
                };
        }

        var churn = Parse(stdout, out var commitsScanned);

        if (churn.Count == 0)
            return new
            {
                tool = "git_churn_hotspots",
                repo = repoDir,
                since,
                commitsScanned,
                filesChanged = 0,
                top = Array.Empty<object>(),
                summary = commitsScanned == 0
                    ? $"No commit in the last '{since}'. Widen the window with since."
                    : $"No file in the solution was touched by the {commitsScanned} commit(s) in the window.",
                note = "Churn × complexity: a file that changes often and is hard to read is where a refactoring pays first.",
            };

        var ranking = await _report.RankMetricsAsync(projectName, null, 1, cancellationToken);
        if (ranking.Error is not null) return new { error = ranking.Error };

        var metricsByFile = ranking.Metrics
            .Where(metric => metric.FilePath is not null)
            .GroupBy(metric => Path.GetFullPath(metric.FilePath!))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(metric => metric.Cyclomatic).ToList(),
                StringComparer.FromComparison(Snitch.Platform.HostSystem.PathComparison));

        var candidates = new List<Hotspot>();

        foreach (var (relative, churned) in churn)
        {
            if (churned.Commits < minCommits) continue;

            var full = Path.GetFullPath(Path.Combine(repoDir!, relative));
            if (!metricsByFile.TryGetValue(full, out var metrics)) continue;

            var worst = metrics[0];
            candidates.Add(new Hotspot(
                full,
                relative,
                churned.Commits,
                churned.Added,
                churned.Deleted,
                metrics.Count,
                worst.Cyclomatic,
                worst.Cognitive,
                worst.Name,
                worst.Line,
                worst.Risk));
        }

        if (candidates.Count == 0)
            return new
            {
                tool = "git_churn_hotspots",
                repo = repoDir,
                since,
                commitsScanned,
                filesChanged = churn.Count,
                minCommits,
                top = Array.Empty<object>(),
                summary = $"No file reaches {minCommits} change(s) and carries complexity. Lower minCommits or widen since.",
                note = "Churn × complexity: a file that changes often and is hard to read is where a refactoring pays first.",
            };

        var ranked = candidates
            .Select(candidate => candidate with { Score = Math.Round(candidate.Commits * (double)candidate.MaxCyclomatic, 1) })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Commits)
            .ToList();

        var total = ranked.Sum(candidate => candidate.Score);
        var shown = ranked.Take(top).ToList();

        return new
        {
            tool = "git_churn_hotspots",
            repo = repoDir,
            since,
            commitsScanned,
            filesChanged = churn.Count,
            filesInSolution = candidates.Count,
            minCommits,
            score = "commits touching the file × the cyclomatic complexity of its worst method",
            hotspots = shown.Select(candidate => new
            {
                file = candidate.RelativePath,
                fullPath = candidate.FullPath,
                score = candidate.Score,
                share = total <= 0 ? 0 : Math.Round(candidate.Score / total * 100, 1),
                commits = candidate.Commits,
                added = candidate.Added,
                deleted = candidate.Deleted,
                changedLines = candidate.Added + candidate.Deleted,
                methods = candidate.Methods,
                maxCyclomatic = candidate.MaxCyclomatic,
                maxCognitive = candidate.MaxCognitive,
                worstMethod = candidate.WorstMethod,
                worstLine = candidate.WorstLine,
                risk = candidate.Risk,
            }).ToList(),
            findings = Findings(shown, ranked.Count, since),
            next = shown.Count == 0
                ? "Nothing to prioritise."
                : $"complexity_report on {shown[0].RelativePath} shows the decision points of its worst method; " +
                  "fix_complexity lowers it (preview by default), complexity_report ranks the whole solution.",
            note = "Churn reading only: this is git history joined with the static model, not a runtime measurement.",
        };
    }

    private static List<string> Findings(IReadOnlyList<Hotspot> shown, int fileCount, string since)
    {
        var findings = new List<string>();

        if (shown.Count == 0)
        {
            findings.Add("No file in the solution combines repeated change and complexity in the window.");
            return findings;
        }

        var first = shown[0];
        findings.Add(
            $"Start with {first.RelativePath}: {first.Commits} changes since '{since}' and " +
            $"{first.WorstMethod} at line {first.WorstLine} carries cyclomatic {first.MaxCyclomatic}.");

        if (shown.Count > 1)
        {
            var second = shown[1];
            findings.Add(
                $"Then {second.RelativePath} ({second.Commits} changes, worst cyclomatic {second.MaxCyclomatic}).");
        }

        var dominant = shown.Count(candidate => candidate.Score >= 0.5 * first.Score);
        if (dominant == 1 && fileCount > 1)
            findings.Add("One file owns most of the risk: concentrating there is enough for this window.");

        if (fileCount > shown.Count)
            findings.Add($"{fileCount - shown.Count} more file(s) qualify; raise top to see them.");

        return findings;
    }

    /// <summary>
    /// Per-file additions, deletions and the number of commits that touched the file. The commit
    /// marker is NUL because a path may contain anything git allows.
    /// </summary>
    private static Dictionary<string, Churn> Parse(string stdout, out int commits)
    {
        commits = 0;
        var churn = new Dictionary<string, Churn>(StringComparer.Ordinal);
        var seenInCommit = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line[0] == '\0')
            {
                commits++;
                seenInCommit.Clear();
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var added = int.TryParse(parts[0], out var a) ? a : 0;
            var deleted = int.TryParse(parts[1], out var d) ? d : 0;
            var path = parts[2];

            var current = churn.TryGetValue(path, out var existing) ? existing : new Churn(0, 0, 0);
            churn[path] = current with { Added = current.Added + added, Deleted = current.Deleted + deleted };

            if (seenInCommit.Add(path))
                churn[path] = churn[path] with { Commits = churn[path].Commits + 1 };
        }

        return churn;
    }

    private sealed record Churn(int Commits, int Added, int Deleted);

    private sealed record Hotspot(
        string FullPath,
        string RelativePath,
        int Commits,
        int Added,
        int Deleted,
        int Methods,
        int MaxCyclomatic,
        int MaxCognitive,
        string WorstMethod,
        int WorstLine,
        string Risk)
    {
        public double Score { get; init; }
    }
}
