using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class CommitLintTool
{
    [McpServerTool(Name = "commit_lint"), Description(
        "Lint commit messages against Conventional Commits — '<type>(<scope>)!: <description>', a blank " +
        "line, a body, footers ('Refs: #12', 'BREAKING CHANGE: …') — and return the message as data " +
        "(type, scope, description, breaking, footers) with every violation, its rule id, its line and a " +
        "hint. Three inputs: message (lint a text, no repository needed), revision (the message of one " +
        "commit, HEAD by default) or range (every commit of e.g. origin/main..HEAD, the CI shape). Rules: " +
        "type-enum, header-format, header-max-length, scope-empty/scope-format, subject-full-stop, " +
        "body-leading-blank, breaking-change-format; merge and 'Revert \"…\"' messages are exempt and said " +
        "to be. The accepted types, the header length and whether a scope is required are arguments, " +
        "because they are a repository's choice. Read-only; use it before git_commit or on a range in CI.")]
    public static Task<object> CommitLint(
        ICommitConventionService conventions,
        [Description("A commit message to lint. When given, no repository is read.")] string? message = null,
        [Description("A revision whose commit message to lint (default: HEAD, when neither message nor range is given).")] string? revision = null,
        [Description("A revision range whose commits to lint, e.g. origin/main..HEAD.")] string? range = null,
        [Description("Comma-separated types to accept instead of the default " +
                     "feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert.")] string? types = null,
        [Description("Maximum header length in characters (default 72).")] int maxSubjectLength = 72,
        [Description("Require a scope in parentheses, e.g. 'fix(api): …'. Default: false.")] bool requireScope = false,
        [Description("Treat messages beginning with 'Merge ' as conforming (default: true).")] bool allowMergeCommits = true,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => conventions.LintAsync(message, revision, range, types, maxSubjectLength, requireScope, allowMergeCommits, repoPath);
}
