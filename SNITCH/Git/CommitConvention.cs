using System.Text.RegularExpressions;

namespace Snitch.Git;

/// <summary>One trailer of a commit message: <c>Refs: #12</c>, <c>Reviewed-by: Ana</c>, <c>BREAKING CHANGE: …</c>.</summary>
public sealed record ConventionalFooter(string Token, string Value);

/// <summary>A commit message that follows Conventional Commits, broken into the parts a client renders.</summary>
public sealed record ConventionalCommit(
    string Type,
    string? Scope,
    string Description,
    bool Breaking,
    string? Body,
    IReadOnlyList<ConventionalFooter> Footers);

/// <summary>One rule a message broke, with the line of the message it broke on (1-based).</summary>
public sealed record CommitViolation(string Rule, int Line, string Message, string? Hint = null);

/// <summary>
/// The verdict for one message. <see cref="Exempt"/> says the message was not judged because it is a
/// merge or a revert git wrote itself (<see cref="Exemption"/> names which), and it therefore counts
/// as conforming.
/// </summary>
public sealed record CommitLintResult(
    bool Conforming,
    bool Exempt,
    string? Exemption,
    string Subject,
    ConventionalCommit? Parsed,
    IReadOnlyList<CommitViolation> Violations);

/// <summary>
/// The team's rules. The defaults are the Conventional Commits specification's own types plus the
/// three GitHub uses (<c>ci</c>, <c>build</c>, <c>chore</c>); everything else is opt-in, because a
/// length, a scope requirement or a case preference is a repository's choice, not the spec's.
/// </summary>
public sealed record CommitConventionRules(
    IReadOnlyList<string>? Types = null,
    int MaxSubjectLength = 72,
    bool RequireScope = false,
    bool AllowMergeCommits = true,
    bool AllowRevertCommits = true);

/// <summary>
/// Lints a commit message against <see href="https://www.conventionalcommits.org/">Conventional
/// Commits</see>: <c>&lt;type&gt;(&lt;scope&gt;)!: &lt;description&gt;</c>, a blank line, a body and
/// footers. Pure: no git, no file system — <see cref="CommitConventionService"/> adds the reads.
/// </summary>
public static class CommitConvention
{
    /// <summary>
    /// The types a message may use by default. <c>revert</c> is in the set because a <c>git revert</c>
    /// message is conventional once the subject after the quoting is read; the raw
    /// <c>Revert "…"</c> form is exempt like a merge instead of rewritten.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTypes =
        ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];

    private static readonly Regex Header = new(
        @"^(?<type>[A-Za-z][A-Za-z0-9-]*)(?:\((?<scope>[^()\r\n]+)\))?(?<bang>!)?: (?<desc>\S.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Footer = new(
        @"^(?<token>[A-Za-z][A-Za-z0-9-]*|BREAKING CHANGE)(?:: | #)(?<value>.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Lints one message. A null or blank message is a violation, not an exception.</summary>
    public static CommitLintResult Lint(string? message, CommitConventionRules? rules = null)
    {
        rules ??= new CommitConventionRules();
        var types = rules.Types is { Count: > 0 } ? rules.Types : DefaultTypes;

        var text = (message ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        var subjectIndex = 0;
        while (subjectIndex < lines.Length && lines[subjectIndex].Trim().Length == 0) subjectIndex++;

        if (subjectIndex == lines.Length)
            return Refuse(string.Empty, [new CommitViolation("subject-empty", 1, "The message is empty.", "The header must be '<type>(<scope>): <description>'.")]);

        var subject = lines[subjectIndex].TrimEnd();
        var violations = new List<CommitViolation>();

        if (rules.AllowMergeCommits && IsMerge(subject))
            return Exempt(subject, "merge commit");

        if (rules.AllowRevertCommits && IsRevert(subject))
            return Exempt(subject, "revert of a commit");

        if (subject.Length > rules.MaxSubjectLength)
            violations.Add(new CommitViolation("header-max-length", subjectIndex + 1,
                $"The header is {subject.Length} characters; the limit is {rules.MaxSubjectLength}.",
                "Put the detail in the body and keep the header a short summary."));

        string? type = null, scope = null, description = null;
        var breakingBang = false;

        var header = Header.Match(subject);
        if (!header.Success)
        {
            violations.Add(new CommitViolation("header-format", subjectIndex + 1,
                "The header must be '<type>(<scope>): <description>'.",
                "e.g. 'feat(api): add the export endpoint' — note the colon and the single space after it."));
        }
        else
        {
            type = header.Groups["type"].Value;
            scope = header.Groups["scope"].Success ? header.Groups["scope"].Value.Trim() : null;
            description = header.Groups["desc"].Value.Trim();
            breakingBang = header.Groups["bang"].Success;

            if (!types.Contains(type, StringComparer.Ordinal))
                violations.Add(new CommitViolation("type-enum", subjectIndex + 1,
                    $"'{type}' is not one of the types this repository accepts: {string.Join(", ", types)}.",
                    "Pick the type that says why the change exists, not what files it touches."));

            if (scope is not null && scope.Length == 0)
                violations.Add(new CommitViolation("scope-format", subjectIndex + 1,
                    "The scope is empty.", "Write a noun ('api', 'parser') inside the parentheses, or drop them."));

            if (rules.RequireScope && scope is null)
                violations.Add(new CommitViolation("scope-empty", subjectIndex + 1,
                    "This repository requires a scope.", "Write the area in parentheses, e.g. 'fix(api): …'."));

            if (description is not null && description.EndsWith('.'))
                violations.Add(new CommitViolation("subject-full-stop", subjectIndex + 1,
                    "The description ends with a full stop.",
                    "The header is a title, not a sentence: drop the trailing full stop."));
        }

        var bodyLines = lines.Skip(subjectIndex + 1).ToArray();
        var body = string.Join('\n', bodyLines).Trim('\n');
        var hasBody = bodyLines.Any(line => line.Trim().Length > 0);

        if (hasBody && bodyLines.Length > 0 && bodyLines[0].Trim().Length > 0)
            violations.Add(new CommitViolation("body-leading-blank", subjectIndex + 2,
                "The body starts on the line after the header.",
                "Put one blank line between the header and the body: git treats the first paragraph as the subject."));

        var footers = new List<ConventionalFooter>();
        for (var i = 0; i < bodyLines.Length; i++)
        {
            var line = bodyLines[i].Trim();
            if (line.Length == 0) continue;

            var footer = Footer.Match(line);
            if (footer.Success)
            {
                footers.Add(new ConventionalFooter(footer.Groups["token"].Value, footer.Groups["value"].Value.Trim()));
                continue;
            }

            // A breaking-change trailer with no separator is the commonest malformation and the one that
            // silently loses the "this is a breaking change" signal: name it instead of ignoring it.
            if (line.StartsWith("BREAKING CHANGE", StringComparison.Ordinal) ||
                line.StartsWith("BREAKING-CHANGE", StringComparison.Ordinal))
                violations.Add(new CommitViolation("breaking-change-format", subjectIndex + 2 + i,
                    "A breaking-change trailer must be 'BREAKING CHANGE: <what breaks>'.",
                    "The colon and the single space are required for parsers to find it."));
        }

        var breakingFooter = footers.FirstOrDefault(f =>
            f.Token is "BREAKING CHANGE" or "BREAKING-CHANGE");

        var parsed = new ConventionalCommit(
            type ?? string.Empty, scope, description ?? string.Empty, breakingBang || breakingFooter is not null,
            body.Length == 0 ? null : body, footers);

        return violations.Count == 0
            ? new CommitLintResult(true, false, null, subject, parsed, violations)
            : Refuse(subject, violations, parsed);
    }

    private static bool IsMerge(string subject) =>
        subject.StartsWith("Merge ", StringComparison.Ordinal);

    private static bool IsRevert(string subject) =>
        subject.StartsWith("Revert \"", StringComparison.Ordinal);

    private static CommitLintResult Exempt(string subject, string exemption) =>
        new(true, true, exemption, subject, null, []);

    private static CommitLintResult Refuse(string subject, IReadOnlyList<CommitViolation> violations,
        ConventionalCommit? parsed = null) =>
        new(false, false, null, subject, parsed, violations);
}
