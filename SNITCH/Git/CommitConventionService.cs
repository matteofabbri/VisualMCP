using System.Text.RegularExpressions;
using Snitch.Workspace;

namespace Snitch.Git;

/// <summary>
/// Lints commit messages against Conventional Commits — one message, the last commit, or every commit
/// of a range — so a repository can enforce the convention locally and in CI without a Node toolchain.
/// </summary>
public interface ICommitConventionService
{
    /// <summary>Lints one message without touching git (used as the gate on a commit).</summary>
    CommitLintResult Check(string? message, string? types = null, int maxSubjectLength = 72, bool requireScope = false);

    /// <summary>
    /// Lints <paramref name="message"/>, or the message of <paramref name="revision"/> (HEAD by default),
    /// or every commit of <paramref name="range"/> (e.g. <c>origin/main..HEAD</c>).
    /// </summary>
    Task<object> LintAsync(
        string? message,
        string? revision,
        string? range,
        string? types,
        int maxSubjectLength,
        bool requireScope,
        bool allowMergeCommits,
        string? repoPath);
}

/// <summary>Default <see cref="ICommitConventionService"/>: the pure linter plus the git reads.</summary>
public sealed class CommitConventionService : ICommitConventionService
{
    private const string RecordSeparator = "\u001e";
    private const string FieldSeparator = "\u001f";

    // The ref travels through a command line, so it is checked instead of escaped: a range and a
    // revision are made of these characters, and a name with a shell metacharacter is refused.
    private static readonly Regex SafeRef = new(@"^[A-Za-z0-9._/~^@{}\-]+$", RegexOptions.CultureInvariant);

    private readonly IRoslynWorkspace? _workspace;

    public CommitConventionService(IRoslynWorkspace? workspace) => _workspace = workspace;

    public CommitLintResult Check(string? message, string? types = null, int maxSubjectLength = 72, bool requireScope = false) =>
        CommitConvention.Lint(message, BuildRules(types, maxSubjectLength, requireScope, allowMergeCommits: true));

    public async Task<object> LintAsync(
        string? message, string? revision, string? range, string? types,
        int maxSubjectLength, bool requireScope, bool allowMergeCommits, string? repoPath)
    {
        var rules = BuildRules(types, maxSubjectLength, requireScope, allowMergeCommits);

        if (!string.IsNullOrWhiteSpace(message))
            return Report("message", [new CommitText(null, message!)], rules);

        if (!string.IsNullOrWhiteSpace(range) && !SafeRef.IsMatch(range!))
            return new { error = $"'{range}' is not a revision range this tool passes to git." };

        if (!string.IsNullOrWhiteSpace(revision) && !SafeRef.IsMatch(revision!))
            return new { error = $"'{revision}' is not a revision this tool passes to git." };

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath, _workspace?.LoadedSolutionPath);
        if (error is not null) return error;

        if (!string.IsNullOrWhiteSpace(range))
        {
            var log = await GitCli.RunAsync(repoDir!,
                $"log --format=%x1e%h%x1f%B \"{range}\"", 60);
            if (log.timedOut) return new { error = "git log timed out." };
            if (log.exitCode != 0) return new { error = $"git log failed for '{range}': {log.stderr.Trim()}" };

            var commits = ParseRecords(log.stdout);
            return Report($"range:{range}", commits, rules);
        }

        var rev = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision!;
        var single = await GitCli.RunAsync(repoDir!, $"log -1 --format=%x1e%h%x1f%B \"{rev}\"", 30);
        if (single.timedOut) return new { error = "git log timed out." };
        if (single.exitCode != 0)
            return new { error = $"git log failed for '{rev}': {single.stderr.Trim()}", hint = "An empty repository has no HEAD to lint." };

        return Report($"revision:{rev}", ParseRecords(RecordSeparator + single.stdout), rules);
    }

    private static object Report(string source, IReadOnlyList<CommitText> commits, CommitConventionRules rules)
    {
        var results = commits.Select(commit =>
        {
            var lint = CommitConvention.Lint(commit.Message, rules);
            return new
            {
                hash = commit.Hash,
                subject = lint.Subject,
                conforming = lint.Conforming,
                exempt = lint.Exempt,
                exemption = lint.Exemption,
                type = lint.Parsed?.Type,
                scope = lint.Parsed?.Scope,
                description = lint.Parsed?.Description,
                breaking = lint.Parsed?.Breaking ?? false,
                footers = lint.Parsed?.Footers,
                violations = lint.Violations,
            };
        }).ToList();

        var conforming = results.All(result => result.conforming);

        return new
        {
            conforming,
            checkedCommits = results.Count,
            source,
            types = rules.Types is { Count: > 0 } ? rules.Types : CommitConvention.DefaultTypes,
            maxSubjectLength = rules.MaxSubjectLength,
            commits = results,
            violations = results
                .SelectMany(result => result.violations.Select(violation => new
                {
                    hash = result.hash,
                    rule = violation.Rule,
                    line = violation.Line,
                    message = violation.Message,
                    hint = violation.Hint,
                }))
                .ToList(),
            hint = conforming
                ? null
                : "The header is '<type>(<scope>): <description>' with one of the types above, a lowercase type and no trailing full stop.",
        };
    }

    private static List<CommitText> ParseRecords(string stdout)
    {
        var commits = new List<CommitText>();

        foreach (var chunk in stdout.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = chunk.TrimStart('\n', '\r');
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf(FieldSeparator, StringComparison.Ordinal);
            if (separator < 0) continue;

            var hash = trimmed[..separator].Trim();
            var message = trimmed[(separator + 1)..].TrimEnd('\n', '\r');
            commits.Add(new CommitText(hash.Length == 0 ? null : hash, message));
        }

        return commits;
    }

    private static CommitConventionRules BuildRules(string? types, int maxSubjectLength, bool requireScope, bool allowMergeCommits)
    {
        var list = (types ?? string.Empty)
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new CommitConventionRules(
            list.Count == 0 ? null : list,
            maxSubjectLength <= 0 ? 72 : maxSubjectLength,
            requireScope,
            allowMergeCommits);
    }

    private sealed record CommitText(string? Hash, string Message);
}
