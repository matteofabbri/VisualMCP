using Snitch.Git;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Conventional Commits as data: the header, the scope, the breaking marker, the footers and every
/// violation with its rule id. The parser is pure; the service adds the git reads (HEAD and a range).
/// </summary>
public sealed class CommitConventionTests
{
    // ── the header ──────────────────────────────────────────────────────────
    [Fact]
    public void A_feat_with_a_scope_parses_into_its_parts()
    {
        var result = CommitConvention.Lint("feat(api): add the export endpoint");

        Assert.True(result.Conforming);
        Assert.False(result.Exempt);
        Assert.Equal("feat", result.Parsed!.Type);
        Assert.Equal("api", result.Parsed.Scope);
        Assert.Equal("add the export endpoint", result.Parsed.Description);
        Assert.False(result.Parsed.Breaking);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void A_type_out_of_the_set_is_a_type_enum_violation()
    {
        var result = CommitConvention.Lint("feature(api): add the endpoint");

        Assert.False(result.Conforming);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("type-enum", violation.Rule);
        Assert.Contains("feat", violation.Message);
    }

    [Fact]
    public void An_uppercase_type_is_rejected_because_the_set_is_case_sensitive()
    {
        var result = CommitConvention.Lint("Feat: add the endpoint");

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "type-enum");
    }

    [Fact]
    public void A_header_without_the_colon_and_space_is_a_header_format_violation()
    {
        var result = CommitConvention.Lint("fix:add the port");

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "header-format");
    }

    [Fact]
    public void A_description_ending_with_a_full_stop_is_flagged()
    {
        var result = CommitConvention.Lint("docs: explain the setup.");

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "subject-full-stop");
    }

    [Fact]
    public void A_header_over_the_limit_is_flagged_with_its_length()
    {
        var result = CommitConvention.Lint("fix: " + new string('x', 80));

        Assert.False(result.Conforming);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("header-max-length", violation.Rule);
        Assert.Contains("85", violation.Message);
    }

    [Fact]
    public void A_custom_type_list_replaces_the_default()
    {
        var rules = new CommitConventionRules(Types: ["wip", "release"]);

        Assert.True(CommitConvention.Lint("release: ship 2.0", rules).Conforming);
        Assert.False(CommitConvention.Lint("feat: something", rules).Conforming);
    }

    [Fact]
    public void A_required_scope_is_flagged_when_it_is_missing()
    {
        var rules = new CommitConventionRules(RequireScope: true);

        var result = CommitConvention.Lint("fix: repair the parser", rules);

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "scope-empty");
    }

    // ── breaking changes and footers ────────────────────────────────────────
    [Fact]
    public void The_bang_marks_a_breaking_change()
    {
        var result = CommitConvention.Lint("refactor(core)!: drop the old overload");

        Assert.True(result.Conforming);
        Assert.True(result.Parsed!.Breaking);
        Assert.Equal("core", result.Parsed.Scope);
    }

    [Fact]
    public void A_BREAKING_CHANGE_footer_marks_a_breaking_change_and_is_parsed()
    {
        const string message = """
            feat(api): rename the field

            BREAKING CHANGE: clients must send `direction` instead of `side`.
            Refs: #42
            """;

        var result = CommitConvention.Lint(message);

        Assert.True(result.Conforming);
        Assert.True(result.Parsed!.Breaking);
        Assert.Equal(2, result.Parsed.Footers.Count);
        Assert.Equal("Refs", result.Parsed.Footers[1].Token);
        Assert.Equal("#42", result.Parsed.Footers[1].Value);
    }

    [Fact]
    public void A_BREAKING_CHANGE_trailer_without_the_colon_is_flagged()
    {
        const string message = "feat: drop the flag\n\nBREAKING CHANGE clients must migrate";

        var result = CommitConvention.Lint(message);

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "breaking-change-format");
    }

    // ── body ─────────────────────────────────────────────────────────────────
    [Fact]
    public void A_body_needs_the_blank_line_after_the_header()
    {
        var result = CommitConvention.Lint("fix: repair the parser\nbecause it crashed");

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "body-leading-blank");
    }

    [Fact]
    public void A_body_after_the_blank_line_is_kept()
    {
        var result = CommitConvention.Lint("fix: repair the parser\n\nThe grammar missed a case.");

        Assert.True(result.Conforming);
        Assert.Equal("The grammar missed a case.", result.Parsed!.Body);
    }

    // ── exemptions and edges ────────────────────────────────────────────────
    [Fact]
    public void A_merge_commit_is_exempt_and_says_why()
    {
        var result = CommitConvention.Lint("Merge branch 'main' into feature");

        Assert.True(result.Conforming);
        Assert.True(result.Exempt);
        Assert.Equal("merge commit", result.Exemption);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void A_quoted_revert_written_by_git_is_exempt()
    {
        var result = CommitConvention.Lint("Revert \"feat(api): add the endpoint\"\n\nThis reverts commit abc123.");

        Assert.True(result.Conforming);
        Assert.True(result.Exempt);
        Assert.Equal("revert of a commit", result.Exemption);
    }

    [Fact]
    public void A_merge_exemption_can_be_turned_off()
    {
        var rules = new CommitConventionRules(AllowMergeCommits: false);

        var result = CommitConvention.Lint("Merge branch 'main'", rules);

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "header-format");
    }

    [Fact]
    public void An_empty_message_is_a_violation_not_an_exception()
    {
        var result = CommitConvention.Lint("   \n\n  ");

        Assert.False(result.Conforming);
        Assert.Equal("subject-empty", Assert.Single(result.Violations).Rule);
    }

    [Fact]
    public void A_body_alone_after_the_header_does_not_exempt_the_header()
    {
        var result = CommitConvention.Lint("random scribble\n\nRefs: #1");

        Assert.False(result.Conforming);
        Assert.Contains(result.Violations, v => v.Rule == "header-format");
    }

    // ── the service ─────────────────────────────────────────────────────────
    [Fact]
    public async Task The_service_lints_a_message_without_a_repository()
    {
        var service = new CommitConventionService(workspace: null);

        var result = await service.LintAsync("feat(cli): add commit_lint", null, null, null, 72, false, true, null);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("conforming").GetBoolean());
        Assert.Equal("message", json.GetProperty("source").GetString());
        Assert.Equal(1, json.GetProperty("checkedCommits").GetInt32());
    }

    [Fact]
    public async Task The_service_lints_HEAD_in_a_repository()
    {
        using var repo = new TestGitRepo();
        repo.Commit("feat(api): add the endpoint");

        var service = new CommitConventionService(repo.Workspace);

        var result = await service.LintAsync(null, null, null, null, 72, false, true, repo.Directory);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("conforming").GetBoolean());
        Assert.Equal("revision:HEAD", json.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("commits")[0].GetProperty("hash").GetString()));
        Assert.Equal("feat", json.GetProperty("commits")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_service_lints_a_range_and_names_the_bad_commit()
    {
        using var repo = new TestGitRepo();
        var first = repo.Commit("feat: the first change");
        repo.Commit("this message has no type");

        var service = new CommitConventionService(repo.Workspace);

        var result = await service.LintAsync(null, null, $"{first}..HEAD", null, 72, false, true, repo.Directory);

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("conforming").GetBoolean());
        Assert.Equal(1, json.GetProperty("checkedCommits").GetInt32());
        Assert.Equal("header-format", json.GetProperty("violations")[0].GetProperty("rule").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("violations")[0].GetProperty("hash").GetString()));
    }

    [Fact]
    public async Task The_service_refuses_a_revision_with_a_shell_metacharacter()
    {
        var service = new CommitConventionService(workspace: null);

        var result = await service.LintAsync(null, "HEAD; rm -rf /", null, null, 72, false, true, null);

        Assert.Contains("not a revision", ResultAssert.Error(result));
    }

    [Fact]
    public void The_gate_checks_one_message_with_the_same_rules()
    {
        var service = new CommitConventionService(workspace: null);

        Assert.True(service.Check("fix: repair the parser").Conforming);
        Assert.False(service.Check("repaired the parser").Conforming);
    }
}
