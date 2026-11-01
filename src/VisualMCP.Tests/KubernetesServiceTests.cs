using Snitch.Kubernetes;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// k8s_*: the JSON shapes kubectl actually prints (a list with items, a single object, version
/// documents) and the guards that keep a call from changing a cluster by accident.
/// </summary>
public sealed class KubernetesServiceTests
{
    private readonly KubernetesService _service = new();

    [Fact]
    public void AListIsReducedToNameNamespaceStateAndRestarts()
    {
        const string json = """
            {
              "kind": "List",
              "items": [
                {
                  "kind": "Pod",
                  "metadata": { "name": "api-7f9", "namespace": "shop", "creationTimestamp": "2026-10-31T10:00:00Z" },
                  "status": {
                    "phase": "Running",
                    "containerStatuses": [
                      { "ready": true, "restartCount": 2 },
                      { "ready": false, "restartCount": 1 }
                    ]
                  }
                },
                {
                  "kind": "Deployment",
                  "metadata": { "name": "api", "namespace": "shop" },
                  "spec": { "replicas": 3 },
                  "status": { "readyReplicas": 2 }
                }
              ]
            }
            """;

        var rows = KubernetesService.SummarizeItems(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("api-7f9", rows[0].Name);
        Assert.Equal("shop", rows[0].Namespace);
        Assert.Equal("Running", rows[0].Status);
        Assert.Equal(3, rows[0].Restarts);
        Assert.Equal(1, rows[0].Ready);

        Assert.Equal("api", rows[1].Name);
        Assert.Equal("2/3 ready", rows[1].Status);
        Assert.Equal(3, rows[1].Desired);
        Assert.Null(rows[1].Restarts);
    }

    [Fact]
    public void ASingleObjectIsOneRow()
    {
        var rows = KubernetesService.SummarizeItems(
            """{ "kind": "Pod", "metadata": { "name": "x" }, "status": { "phase": "Pending" } }""");

        var row = Assert.Single(rows);
        Assert.Equal("Pending", row.Status);
        Assert.Null(row.Restarts);
    }

    [Fact]
    public void SomethingThatIsNotAJsonDocumentYieldsNoRows()
    {
        Assert.Empty(KubernetesService.SummarizeItems("error: the server doesn't have a resource type \"podz\""));
        Assert.Empty(KubernetesService.SummarizeItems(string.Empty));
    }

    [Fact]
    public void TheVersionDocumentIsReadInBothHalves()
    {
        const string json = """
            {
              "clientVersion": { "gitVersion": "v1.34.1" },
              "serverVersion": { "gitVersion": "v1.33.0" }
            }
            """;

        Assert.Equal("v1.34.1", KubernetesService.ReadVersion(json, "clientVersion"));
        Assert.Equal("v1.33.0", KubernetesService.ReadVersion(json, "serverVersion"));
        Assert.Null(KubernetesService.ReadVersion("""{ "clientVersion": { "gitVersion": "v1.34.1" } }""", "serverVersion"));
        Assert.Null(KubernetesService.ReadVersion("not json", "clientVersion"));
    }

    [Fact]
    public async Task DeleteRefusesWithoutConfirm()
    {
        var error = ResultAssert.Error(await _service.DeleteAsync("pod", "api-7f9", null, ignoreNotFound: true, confirm: false, 30));

        Assert.Contains("confirm=true", error);
    }

    [Fact]
    public async Task ApplyRefusesToChangeTheClusterWithoutConfirm()
    {
        var error = ResultAssert.Error(
            await _service.ApplyAsync(file: null, content: "kind: Pod", namespaceName: null, dryRun: false, confirm: false, 30));

        Assert.Contains("confirm=true", error);
    }

    [Fact]
    public async Task ApplyNeedsExactlyOneSource()
    {
        Assert.Contains("exactly one", ResultAssert.Error(
            await _service.ApplyAsync(null, null, null, dryRun: true, confirm: false, 30)));

        Assert.Contains("exactly one", ResultAssert.Error(
            await _service.ApplyAsync("a.yaml", "kind: Pod", null, dryRun: true, confirm: false, 30)));

        Assert.Contains("Manifest not found", ResultAssert.Error(
            await _service.ApplyAsync("does-not-exist.yaml", null, null, dryRun: true, confirm: false, 30)));
    }

    [Fact]
    public async Task RolloutRefusesRestartAndUndoWithoutConfirm()
    {
        Assert.Contains("confirm=true", ResultAssert.Error(
            await _service.RolloutAsync("restart", "deployment", "api", null, null, confirm: false, 30)));

        Assert.Contains("confirm=true", ResultAssert.Error(
            await _service.RolloutAsync("undo", "deployment", "api", null, 2, confirm: false, 30)));
    }

    [Fact]
    public async Task AFlagIsNotAResourceName()
    {
        Assert.Contains("resource kind", ResultAssert.Error(
            await _service.GetAsync("--namespace=prod", null, null, false, null, null, 30)));

        Assert.Contains("pod name", ResultAssert.Error(
            await _service.LogsAsync("--all-pods", null, null, 100, null, false, 30)));

        Assert.Contains("selector", ResultAssert.Error(
            await _service.GetAsync("pods", null, null, false, "--all", null, 30)));
    }

    [Fact]
    public async Task ExecRefusesWithoutConfirmAndWithoutACommand()
    {
        Assert.Contains("confirm=true", ResultAssert.Error(
            await _service.ExecAsync("api-7f9", null, null, "ls -la", confirm: false, 30)));

        Assert.Contains("command", ResultAssert.Error(
            await _service.ExecAsync("api-7f9", null, null, "  ", confirm: true, 30)));

        Assert.Contains("pod name", ResultAssert.Error(
            await _service.ExecAsync("--all-pods", null, null, "ls", confirm: true, 30)));
    }

    [Fact]
    public async Task KustomizeNeedsAPathAndRendersTheDocumentCount()
    {
        Assert.Contains("directory", ResultAssert.Error(await _service.KustomizeAsync("", null, 30)));

        Assert.Equal(3, KubernetesService.CountDocuments("""
            apiVersion: v1
            kind: ConfigMap
            ---
            apiVersion: v1
            kind: Service
            ---
            apiVersion: apps/v1
            kind: Deployment
            """));
        Assert.Equal(1, KubernetesService.CountDocuments("kind: Pod"));
        Assert.Equal(0, KubernetesService.CountDocuments(" "));
    }

    [Fact]
    public async Task AnUnknownHelmActionAndAKnownOneWithNoHelmAreBothStructured()
    {
        Assert.Contains("Unknown action", ResultAssert.Error(
            await _service.HelmAsync("upgrade", "api", null, null, null, null, 30)));

        var result = ResultAssert.Json(await _service.HelmAsync("list", null, null, null, null, null, 30));
        // helm is optional: either it answers, or the call reports that it is missing.
        Assert.True(result.TryGetProperty("available", out _) || result.TryGetProperty("succeeded", out _));
    }

    [Fact]
    public void HelmLintOutputIsSummarizedBySeverity()
    {
        // The shape helm lint prints: bracketed severities, then a summary line.
        var summary = ResultAssert.Json(KubernetesService.SummarizeLintOutput("""
            ==> Linting ./chart
            [INFO] Chart.yaml: icon is recommended
            [WARNING] templates/deployment.yaml: object name does not conform to Kubernetes naming requirements
            [ERROR] templates/service.yaml: unable to build kubernetes objects from release manifest: error validating "": error validating data: ValidationError(Service.spec.ports[0]): missing required field "port"
            1 chart(s) linted, 1 chart(s) failed
            """));

        Assert.Equal(1, summary.GetProperty("counts").GetProperty("info").GetInt32());
        Assert.Equal(1, summary.GetProperty("counts").GetProperty("warning").GetInt32());
        Assert.Equal(1, summary.GetProperty("counts").GetProperty("error").GetInt32());
        Assert.Contains("1 chart(s) linted", summary.GetProperty("summary").GetString());
        Assert.Contains(summary.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("severity").GetString() == "error" &&
                       finding.GetProperty("message").GetString()!.Contains("ports[0]"));

        var clean = ResultAssert.Json(KubernetesService.SummarizeLintOutput("1 chart(s) linted, 0 chart(s) failed"));
        Assert.Empty(clean.GetProperty("findings").EnumerateArray());
        Assert.Contains("0 chart(s) failed", clean.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task HelmLintNeedsAChartAndNoRelease()
    {
        // The chart check runs before helm is looked up, so it answers the same with or without helm installed.
        Assert.Contains("chart", ResultAssert.Error(
            await _service.HelmAsync("lint", "api", null, null, null, null, 30)));
    }
}
