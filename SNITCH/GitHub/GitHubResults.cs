using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snitch.GitHub;

/// <summary>
/// What one gh invocation returns: the command and where it ran, the parsed JSON when <c>--json</c>
/// was asked for, the truncated streams, and the failure facts (timeout, non-zero exit, gh missing).
/// Every member is nullable and the MCP serializer drops nulls, so each branch keeps its shape.
/// </summary>
public sealed record GitHubCommandResult
{
    public string? Error { get; init; }
    public string? Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? ExitCode { get; init; }
    public bool? Succeeded { get; init; }
    public int? ElapsedMs { get; init; }
    public JsonElement? Data { get; init; }
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// A refusal before anything ran: the confirm guards answer with the error and the key that names
/// what the caller must confirm (a pull request number, a repository, a tag, a secret, a run).
/// </summary>
public sealed record GitHubGateResult
{
    public string? Error { get; init; }
    public int? Number { get; init; }
    public string? Repo { get; init; }
    public string? Tag { get; init; }
    public string? Name { get; init; }
    public string? RunId { get; init; }
    public string? Workflow { get; init; }
}

/// <summary>
/// The branch-protection answer, for all three actions and every branch of each: the rulesets view,
/// the required-status-checks view (found and not configured), the protection detail, and the
/// refusals (no admin token, not protected). One record, nullable members, nulls dropped — the
/// <c>protected</c> key keeps its lowercase GitHub spelling.
/// </summary>
public sealed record GitHubBranchProtectionResult
{
    public string? Error { get; init; }
    public string? Hint { get; init; }
    public string? Note { get; init; }
    public string? Branch { get; init; }
    public int? RulesetCount { get; init; }
    public JsonElement? Rules { get; init; }
    public bool? ProtectedBranch { get; init; }
    public bool? Configured { get; init; }
    public bool? Strict { get; init; }
    public List<string>? Contexts { get; init; }
    public JsonElement? Checks { get; init; }
    public JsonElement? Data { get; init; }

    [JsonPropertyName("protected")]
    public bool? Protected { get; init; }

    public JsonElement? RequiredStatusChecks { get; init; }
    public JsonElement? PullRequestReviews { get; init; }
    public JsonElement? EnforceAdmins { get; init; }
    public JsonElement? Restrictions { get; init; }
}
