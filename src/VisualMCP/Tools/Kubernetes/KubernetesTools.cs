using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Kubernetes;

namespace VisualMCP.Tools.Kubernetes;

[McpServerToolType]
public static class KubernetesTools
{
    [McpServerTool(Name = "k8s_status"), Description(
        "Kubernetes availability in one call: whether kubectl is installed, its client version, the server version, " +
        "the current context and whether a cluster actually answers, plus the helm version when helm is present. " +
        "Use it FIRST when a cluster call fails — it says which half is missing. Use this INSTEAD OF shell " +
        "'kubectl version' / 'kubectl config current-context'. Read-only.")]
    public static Task<object> KubernetesStatus(
        IKubernetesService k8s,
        [Description("Timeout in seconds (default 30, max 900).")] int timeoutSeconds = 30)
        => k8s.StatusAsync(timeoutSeconds);

    [McpServerTool(Name = "k8s_context"), Description(
        "The kubeconfig contexts: action=current (the selected context), action=list (all of them, the table " +
        "kubectl prints) or action=use (switch to `name`). Changing context changes which cluster every later call " +
        "talks to. Use this INSTEAD OF shell 'kubectl config ...'.")]
    public static Task<object> KubernetesContext(
        IKubernetesService k8s,
        [Description("current | list | use")] string action = "current",
        [Description("Context name, for action=use.")] string? name = null,
        [Description("Timeout in seconds (default 30).")] int timeoutSeconds = 30)
        => k8s.ContextAsync(action, name, timeoutSeconds);

    [McpServerTool(Name = "k8s_get"), Description(
        "List Kubernetes resources as data: pods, deployments, services, statefulsets, nodes, events, configmaps, " +
        "ingresses, … (comma-separated kinds such as 'pods,deployments' are accepted). By default the answer is a " +
        "summary parsed from 'kubectl get -o json' — name, namespace, phase or N/M ready, restarts, age — which is " +
        "what makes a list readable; pass output=yaml|json|wide|name|jsonpath=… to get the client's own view " +
        "instead. Use this INSTEAD OF shell 'kubectl get'. Read-only.")]
    public static Task<object> KubernetesGet(
        IKubernetesService k8s,
        [Description("Resource kind, e.g. pods, deployments, services, statefulsets, nodes.")] string kind,
        [Description("One resource name; omit to list them all.")] string? name = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("All namespaces (kubectl --all-namespaces).")] bool allNamespaces = false,
        [Description("Label selector, e.g. 'app=api,tier=web'.")] string? selector = null,
        [Description("summary (default) | yaml | json | wide | name | jsonpath=... | go-template=...")] string? output = null,
        [Description("Timeout in seconds (default 60).")] int timeoutSeconds = 60)
        => k8s.GetAsync(kind, name, namespaceName, allNamespaces, selector, output, timeoutSeconds);

    [McpServerTool(Name = "k8s_describe"), Description(
        "The full 'kubectl describe' text of one resource, or of every resource of a kind: events, conditions, " +
        "containers, volumes, the scheduler's decisions. This is the call that explains WHY a pod is not ready. " +
        "Use this INSTEAD OF shell 'kubectl describe'. Read-only.")]
    public static Task<object> KubernetesDescribe(
        IKubernetesService k8s,
        [Description("Resource kind, e.g. pod, deployment.")] string kind,
        [Description("Resource name; omit to describe every resource of the kind.")] string? name = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("All namespaces (kubectl --all-namespaces).")] bool allNamespaces = false,
        [Description("Timeout in seconds (default 60).")] int timeoutSeconds = 60)
        => k8s.DescribeAsync(kind, name, namespaceName, allNamespaces, timeoutSeconds);

    [McpServerTool(Name = "k8s_logs"), Description(
        "A pod's logs (stdout and stderr, tail-limited): --tail, --since and --previous for the container that " +
        "crashed are exposed. Use this INSTEAD OF shell 'kubectl logs'. Read-only.")]
    public static Task<object> KubernetesLogs(
        IKubernetesService k8s,
        [Description("Pod name.")] string pod,
        [Description("Container name, when the pod has several.")] string? container = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("How many trailing lines (default 200, max 10000).")] int tail = 200,
        [Description("Only logs newer than this, e.g. '10m' or '2026-10-31T10:00:00Z'.")] string? since = null,
        [Description("Logs of the previous container instance (the one that crashed).")] bool previous = false,
        [Description("Timeout in seconds (default 45).")] int timeoutSeconds = 45)
        => k8s.LogsAsync(pod, container, namespaceName, tail, since, previous, timeoutSeconds);
}

[McpServerToolType]
public static class KubernetesChangeTools
{
    [McpServerTool(Name = "k8s_apply"), Description(
        "Apply a Kubernetes manifest: pass the file on disk (`file`) or the manifest itself (`content`). dryRun " +
        "defaults to true and runs the SERVER-side validation ('kubectl apply --dry-run=server'), which catches what " +
        "the API server would reject without changing anything; set dryRun=false AND confirm=true to really apply. " +
        "Use this INSTEAD OF shell 'kubectl apply'.")]
    public static Task<object> KubernetesApply(
        IKubernetesService k8s,
        [Description("Path of the manifest (yaml/json, or a directory of them).")] string? file = null,
        [Description("The manifest text itself; written to a temporary file and removed afterwards.")] string? content = null,
        [Description("Namespace; omit for the manifest's own or the context's.")] string? namespaceName = null,
        [Description("Server-side validation only (default true).")] bool dryRun = true,
        [Description("Must be true to apply when dryRun=false: the cluster changes.")] bool confirm = false,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.ApplyAsync(file, content, namespaceName, dryRun, confirm, timeoutSeconds);

    [McpServerTool(Name = "k8s_delete"), Description(
        "Delete one Kubernetes resource. Destructive and irreversible, so it refuses unless confirm=true; " +
        "ignoreNotFound makes a re-run idempotent. Use this INSTEAD OF shell 'kubectl delete'.")]
    public static Task<object> KubernetesDelete(
        IKubernetesService k8s,
        [Description("Resource kind, e.g. pod, deployment, job.")] string kind,
        [Description("Resource name.")] string name,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Do not fail when the resource is already gone.")] bool ignoreNotFound = false,
        [Description("Must be true: the resource is removed from the cluster.")] bool confirm = false,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.DeleteAsync(kind, name, namespaceName, ignoreNotFound, confirm, timeoutSeconds);

    [McpServerTool(Name = "k8s_rollout"), Description(
        "The rollout lifecycle of a workload: action=status waits for the deployment to finish rolling, action=restart " +
        "rolls it (a new ReplicaSet), action=undo goes back a revision (optionally `toRevision`). restart and undo " +
        "change the cluster and require confirm=true. Use this INSTEAD OF shell 'kubectl rollout ...'.")]
    public static Task<object> KubernetesRollout(
        IKubernetesService k8s,
        [Description("status | restart | undo")] string action = "status",
        [Description("Workload kind: deployment, statefulset, daemonset.")] string kind = "deployment",
        [Description("Workload name.")] string? name = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Revision to roll back to, for action=undo.")] int? toRevision = null,
        [Description("Must be true for restart/undo: the pods are replaced.")] bool confirm = false,
        [Description("Timeout in seconds (default 180).")] int timeoutSeconds = 180)
        => k8s.RolloutAsync(action, kind, name ?? string.Empty, namespaceName, toRevision, confirm, timeoutSeconds);

    [McpServerTool(Name = "k8s_helm"), Description(
        "Helm releases through the helm CLI: action=list (all releases in a namespace, JSON), status, history, " +
        "get-values, template (render a chart with valuesFile/setValues, which is offline and safe) or repo-list. " +
        "Reports that helm is missing instead of failing the call. Use this INSTEAD OF shell 'helm ...'.")]
    public static Task<object> KubernetesHelm(
        IKubernetesService k8s,
        [Description("list | status | history | get-values | template | repo-list")] string action = "list",
        [Description("Release name (required by status, history, get-values, template).")] string? release = null,
        [Description("Chart path or reference, for action=template.")] string? chart = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Values file for action=template.")] string? valuesFile = null,
        [Description("--set assignments for action=template, e.g. ['image.tag=1.2.3'].")] string[]? setValues = null,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.HelmAsync(action, release, chart, namespaceName, valuesFile, setValues, timeoutSeconds);
}
