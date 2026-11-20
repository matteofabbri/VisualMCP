using System.Text.Json;
using ModelContextProtocol;
using Snitch.GitHub;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The typed results of <see cref="IGitHubService"/> must serialize to what the client always
/// received: camelCase members, a null member omitted, the gate's companion key beside its error,
/// and the branch-protection record keeping GitHub's lowercase <c>protected</c> key.
/// </summary>
public sealed class GitHubResultSerializationTests
{
    private static JsonElement Json(object result)
    {
        // Same options and same static type the MCP server serializes a tool result with.
        string json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void A_gh_invocation_keeps_the_command_shape_and_omits_what_is_absent()
    {
        JsonElement root = Json(new GitHubCommandResult
        {
            Command = "gh repo view matteofabbri/VisualMCP",
            WorkingDirectory = "/repo",
            ExitCode = 0,
            Succeeded = true,
            ElapsedMs = 240,
            Stdout = "repository text",
            Stderr = "",
        });

        Assert.Equal("gh repo view matteofabbri/VisualMCP", root.GetProperty("command").GetString());
        Assert.Equal("/repo", root.GetProperty("workingDirectory").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal(240, root.GetProperty("elapsedMs").GetInt32());
        Assert.Equal("repository text", root.GetProperty("stdout").GetString());
        Assert.Equal("", root.GetProperty("stderr").GetString());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("data", out _));
        Assert.False(root.TryGetProperty("note", out _));
    }

    [Fact]
    public void Parsed_json_data_is_the_value_itself_not_a_string()
    {
        JsonElement data = JsonDocument.Parse("[{\"nameWithOwner\":\"a/b\"}]").RootElement.Clone();
        JsonElement root = Json(new GitHubCommandResult
        {
            Command = "gh repo list",
            ExitCode = 0,
            Succeeded = true,
            Data = data,
        });

        Assert.True(root.TryGetProperty("data", out var payload));
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        Assert.Equal("a/b", payload[0].GetProperty("nameWithOwner").GetString());
    }

    [Fact]
    public void A_timeout_carries_the_error_beside_the_command()
    {
        JsonElement root = Json(new GitHubCommandResult
        {
            Error = "gh timed out after 120s.",
            Command = "gh pr list",
        });

        Assert.Equal("gh timed out after 120s.", root.GetProperty("error").GetString());
        Assert.Equal("gh pr list", root.GetProperty("command").GetString());
        Assert.False(root.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public void A_confirm_gate_keeps_the_companion_key_beside_the_error()
    {
        JsonElement merge = Json(new GitHubGateResult
        {
            Error = "Merging is visible to others. Pass confirm=true to proceed.",
            Number = 12,
        });
        JsonElement secret = Json(new GitHubGateResult
        {
            Error = "Writing a repository secret changes remote configuration. Pass confirm=true to proceed.",
            Name = "NUGET_API_KEY",
        });

        Assert.Equal(12, merge.GetProperty("number").GetInt32());
        Assert.Equal("NUGET_API_KEY", secret.GetProperty("name").GetString());
        Assert.False(merge.TryGetProperty("repo", out _));
        Assert.False(secret.TryGetProperty("tag", out _));
    }

    [Fact]
    public void The_rules_view_keeps_its_counts_and_the_zero_branch_note()
    {
        JsonElement root = Json(new GitHubBranchProtectionResult
        {
            Branch = "main",
            RulesetCount = 0,
            Rules = JsonDocument.Parse("[]").RootElement.Clone(),
            Note = "No repository ruleset applies to this branch.",
        });

        Assert.Equal("main", root.GetProperty("branch").GetString());
        Assert.Equal(0, root.GetProperty("rulesetCount").GetInt32());
        Assert.Equal(0, root.GetProperty("rules").GetArrayLength());
        Assert.False(root.TryGetProperty("protected", out _));
        Assert.False(root.TryGetProperty("protectedBranch", out _));
    }

    [Fact]
    public void The_protection_detail_keeps_github_lowercase_protected_key()
    {
        JsonElement root = Json(new GitHubBranchProtectionResult
        {
            Branch = "main",
            Protected = true,
            EnforceAdmins = JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone(),
        });

        // GitHub spells the key lowercase; the anonymous object spelled it @protected for the same result.
        Assert.True(root.GetProperty("protected").GetBoolean());
        Assert.False(root.TryGetProperty("Protected", out _));
        Assert.Equal(true, root.GetProperty("enforceAdmins").GetProperty("enabled").GetBoolean());
        Assert.False(root.TryGetProperty("requiredStatusChecks", out _));
    }

    [Fact]
    public void An_unreadable_check_carries_the_error_and_the_hint_together()
    {
        JsonElement root = Json(new GitHubBranchProtectionResult
        {
            Branch = "main",
            ProtectedBranch = true,
            Configured = false,
            Error = "gh api could not read the required status checks: HTTP 403",
            Hint = "Reading the protection rules needs a token with admin access to the repository.",
        });

        Assert.False(root.GetProperty("configured").GetBoolean());
        Assert.StartsWith("gh api could not read", root.GetProperty("error").GetString());
        Assert.Contains("admin access", root.GetProperty("hint").GetString());
        Assert.False(root.TryGetProperty("strict", out _));
        Assert.False(root.TryGetProperty("contexts", out _));
    }
}
