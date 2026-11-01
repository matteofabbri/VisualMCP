using System.Text.Json;
using ModelContextProtocol;
using Snitch.Kubernetes;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The typed results of <see cref="IKubernetesService"/> must serialize to what the client always
/// received: camelCase members, a null member omitted, and — where a branch answered with a different
/// JSON type for the same key — that type preserved (a single <c>server</c> object beside a folder's
/// <c>server</c> array, a failed forward's string <c>output</c> beside a live session's array).
/// </summary>
public sealed class KubernetesResultSerializationTests
{
    private static JsonElement Json(object result)
    {
        // Same options and same static type the MCP server serializes a tool result with.
        string json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void A_status_refusal_carries_the_error_and_the_flags_that_were_set()
    {
        JsonElement root = Json(new KubernetesStatusResult { Available = false, Error = "kubectl was not found" });

        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("kubectl was not found", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("clientVersion", out _));
        Assert.False(root.TryGetProperty("clusterReachable", out _));
    }

    [Fact]
    public void A_summary_list_keeps_the_row_shape_and_omits_the_raw_output()
    {
        JsonElement root = Json(new KubernetesGetResult
        {
            Kind = "pods",
            NamespaceName = "shop",
            AllNamespaces = false,
            Count = 1,
            Items = new[]
            {
                new KubernetesResourceSummary(
                    "Pod", "api-7f9", "shop", "Running", 2, 1, null, "2026-10-31T10:00:00Z"),
            },
            Command = "kubectl get pods",
            Note = "summary",
        });

        Assert.Equal("pods", root.GetProperty("kind").GetString());
        Assert.False(root.GetProperty("allNamespaces").GetBoolean());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.False(root.TryGetProperty("raw", out _));
        Assert.False(root.TryGetProperty("selector", out _));

        JsonElement row = root.GetProperty("items")[0];
        Assert.Equal("api-7f9", row.GetProperty("name").GetString());
        Assert.Equal(2, row.GetProperty("restarts").GetInt32());
        Assert.False(row.TryGetProperty("desired", out _));
    }

    [Fact]
    public void The_client_own_format_returns_raw_and_no_rows()
    {
        JsonElement root = Json(new KubernetesGetResult
        {
            Kind = "pods",
            AllNamespaces = false,
            Format = "yaml",
            Raw = "kind: Pod",
        });

        Assert.Equal("kind: Pod", root.GetProperty("raw").GetString());
        Assert.Equal("yaml", root.GetProperty("format").GetString());
        Assert.False(root.TryGetProperty("items", out _));
        Assert.False(root.TryGetProperty("count", out _));
    }

    [Fact]
    public void A_helm_lint_answer_keeps_the_counts_and_the_finding_shape()
    {
        JsonElement root = Json(new KubernetesHelmResult
        {
            Action = "lint",
            Succeeded = true,
            Lint = new KubernetesHelmLintResult
            {
                Summary = "1 chart(s) linted, 1 chart(s) failed",
                Counts = new Dictionary<string, int> { ["error"] = 1, ["warning"] = 0 },
                Findings = new[] { new KubernetesHelmLintFinding("error", "Service.spec.ports[0]") },
                Linter = "helm lint",
            },
        });

        JsonElement lint = root.GetProperty("lint");
        Assert.Equal(1, lint.GetProperty("counts").GetProperty("error").GetInt32());
        Assert.Equal("error", lint.GetProperty("findings")[0].GetProperty("severity").GetString());
        Assert.Equal("Service.spec.ports[0]", lint.GetProperty("findings")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void One_manifest_validated_offline_is_its_own_answer()
    {
        JsonElement root = Json(new ManifestValidationResult
        {
            Source = "deployment.yaml",
            DocumentCount = 1,
            Counts = new ManifestFindingCounts { Error = 0, Warning = 1, Info = 0 },
            Findings = new[] { new ManifestFinding("K8S016", "warning", "not pinned", 0, "spec.template.spec.containers[0].image", "pin it") },
            Documents = new[] { new ManifestDocument(0, "apps/v1", "Deployment", "api", "shop", "deployment.yaml") },
            Note = "offline",
        });

        Assert.Equal(1, root.GetProperty("documentCount").GetInt32());
        Assert.Equal(0, root.GetProperty("counts").GetProperty("error").GetInt32());
        Assert.Equal("K8S016", root.GetProperty("findings")[0].GetProperty("ruleId").GetString());
        Assert.Equal("apps/v1", root.GetProperty("documents")[0].GetProperty("apiVersion").GetString());
        Assert.False(root.TryGetProperty("files", out _));
        Assert.False(root.TryGetProperty("offline", out _));
    }

    [Fact]
    public void One_manifest_with_a_server_dry_run_wraps_an_offline_answer_and_one_server_object()
    {
        JsonElement root = Json(new KubernetesServerValidateResult
        {
            Offline = new ManifestValidationResult { DocumentCount = 1 },
            Server = new KubernetesServerValidationResult { File = "deployment.yaml", Ran = true, Valid = true },
        });

        Assert.Equal(1, root.GetProperty("offline").GetProperty("documentCount").GetInt32());
        Assert.True(root.GetProperty("server").GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("server").ValueKind);
        Assert.False(root.TryGetProperty("files", out _));
    }

    [Fact]
    public void A_folder_with_a_server_dry_run_reports_the_server_answers_as_an_array()
    {
        JsonElement root = Json(new KubernetesFolderValidateResult
        {
            Files = new[] { "a.yaml", "b.yaml" },
            Results = new[] { new ManifestValidationResult { DocumentCount = 1 } },
            Server = new[] { new KubernetesServerValidationResult { Ran = true, Valid = true } },
            Note = "per file",
        });

        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.Equal(1, root.GetProperty("results")[0].GetProperty("documentCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("server").ValueKind);
        Assert.True(root.GetProperty("server")[0].GetProperty("valid").GetBoolean());
        Assert.Equal("per file", root.GetProperty("note").GetString());
    }

    [Fact]
    public void A_failed_forward_keeps_output_a_string_and_a_live_session_an_array()
    {
        JsonElement failure = Json(new KubernetesPortForwardStartFailureResult
        {
            Action = "start",
            Target = "svc/api",
            Succeeded = false,
            Output = "kubectl exited",
        });

        Assert.Equal(JsonValueKind.String, failure.GetProperty("output").ValueKind);
        Assert.Equal("kubectl exited", failure.GetProperty("output").GetString());
        Assert.False(failure.TryGetProperty("forwarding", out _));

        JsonElement status = Json(new KubernetesPortForwardStatusResult
        {
            Action = "status",
            SessionId = "pf-1",
            Output = new[] { "Forwarding from 127.0.0.1:8080" },
        });

        Assert.Equal(JsonValueKind.Array, status.GetProperty("output").ValueKind);
        Assert.Equal("pf-1", status.GetProperty("sessionId").GetString());
        Assert.False(status.TryGetProperty("stopped", out _));
    }

    [Fact]
    public void Stopping_a_session_reports_only_what_the_stop_answered()
    {
        JsonElement root = Json(new KubernetesPortForwardStatusResult
        {
            Action = "stop",
            SessionId = "pf-1",
            Target = "svc/api",
            Stopped = true,
        });

        Assert.True(root.GetProperty("stopped").GetBoolean());
        Assert.False(root.TryGetProperty("output", out _));
        Assert.False(root.TryGetProperty("alive", out _));
    }
}
