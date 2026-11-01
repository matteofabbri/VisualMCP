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
    public static Task<KubernetesStatusResult> KubernetesStatus(
        IKubernetesService k8s,
        [Description("Timeout in seconds (default 30, max 900).")] int timeoutSeconds = 30)
        => k8s.StatusAsync(timeoutSeconds);

    [McpServerTool(Name = "k8s_context"), Description(
        "The kubeconfig contexts: action=current (the selected context), action=list (all of them, the table " +
        "kubectl prints) or action=use (switch to `name`). Changing context changes which cluster every later call " +
        "talks to. Use this INSTEAD OF shell 'kubectl config ...'.")]
    public static Task<KubernetesContextResult> KubernetesContext(
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
    public static Task<KubernetesGetResult> KubernetesGet(
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
    public static Task<KubernetesDescribeResult> KubernetesDescribe(
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
    public static Task<KubernetesLogsResult> KubernetesLogs(
        IKubernetesService k8s,
        [Description("Pod name.")] string pod,
        [Description("Container name, when the pod has several.")] string? container = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("How many trailing lines (default 200, max 10000).")] int tail = 200,
        [Description("Only logs newer than this, e.g. '10m' or '2026-10-31T10:00:00Z'.")] string? since = null,
        [Description("Logs of the previous container instance (the one that crashed).")] bool previous = false,
        [Description("Timeout in seconds (default 45).")] int timeoutSeconds = 45)
        => k8s.LogsAsync(pod, container, namespaceName, tail, since, previous, timeoutSeconds);

    [McpServerTool(Name = "k8s_kustomize"), Description(
        "Render a kustomization WITHOUT touching a cluster: 'kubectl kustomize' is the client-side build step, so " +
        "this answers what an overlay produces before anything is applied (the rendered YAML and the document " +
        "count). A local directory is checked and made absolute; a git/URL kustomization reference is passed " +
        "through. Use this INSTEAD OF shell 'kubectl kustomize'. Read-only.")]
    public static Task<KubernetesKustomizeResult> KubernetesKustomize(
        IKubernetesService k8s,
        [Description("A kustomization directory on disk, or a kustomize git/URL reference.")] string path,
        [Description("Override the namespace of the rendered resources.")] string? namespaceName = null,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.KustomizeAsync(path, namespaceName, timeoutSeconds);

    [McpServerTool(Name = "k8s_validate"), Description(
        "Validate a Kubernetes manifest OFFLINE — no cluster, no kubectl. Pass file (a manifest or a folder of " +
        ".yaml/.yml) or content (the manifest itself). Rules K8S001–K8S023: a missing apiVersion/kind/metadata.name, " +
        "a pod template with no containers, a container with no image or no name, a workload with no selector, a " +
        "selector that does not match its pod template's labels, a Service with no ports or no selector, a CronJob " +
        "with no schedule, an HPA with no target or minReplicas > maxReplicas, a PVC with no storage request, an " +
        "unpinned image (:latest or no tag), the same kind/namespace/name twice, a name that is not a DNS label, a " +
        "cluster-scoped kind carrying a namespace, a ConfigMap or Secret with no data, and a volume naming a " +
        "ConfigMap/Secret that is not in the input. A table of the built-in kinds, not the OpenAPI schema: a custom " +
        "resource is reported as having no built-in schema. Pass server=true to ALSO send the same text to the " +
        "configured cluster with kubectl apply --dry-run=server, which is the authority for a CRD's OpenAPI schema " +
        "and an admission webhook; a missing kubectl or an unreachable cluster is reported, not raised. Read-only.")]
    public static Task<KubernetesValidateResult> KubernetesValidate(
        IKubernetesService k8s,
        [Description("A manifest on disk, or a folder of .yaml/.yml files.")] string? file = null,
        [Description("The manifest text itself.")] string? content = null,
        [Description("Also ask the configured cluster to dry-run it (kubectl apply --dry-run=server).")] bool server = false)
        => k8s.ValidateAsync(file, content, server);

    [McpServerTool(Name = "k8s_port_forward"), Description(
        "kubectl port-forward as a session: action=start (target pod/name, svc/name or deployment/name, ports " +
        "'8080:80') launches the forward detached in this server process, waits for its 'Forwarding from' line and " +
        "returns the sessionId, the local port and the pid; action=list shows the live sessions, status one of " +
        "them, stop ends it (the process tree is killed). The forward ends when the server does. Use this INSTEAD OF " +
        "shell 'kubectl port-forward'.")]
    public static Task<KubernetesPortForwardResult> KubernetesPortForward(
        IKubernetesService k8s,
        [Description("start | list | status | stop")] string action = "list",
        [Description("The session id from action=start, for status and stop.")] string? sessionId = null,
        [Description("The target, for start: pod/name, svc/name, deployment/name or statefulset/name.")] string? target = null,
        [Description("The ports, for start: '8080:80', '8080:80/udp' or a comma-separated list.")] string? ports = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Seconds to wait for the forward to report it is listening (default 10).")] int timeoutSeconds = 10)
        => k8s.PortForwardAsync(action, sessionId, target, ports, namespaceName, timeoutSeconds);
}

[McpServerToolType]
public static class KubernetesChangeTools
{
    [McpServerTool(Name = "k8s_apply"), Description(
        "Apply a Kubernetes manifest: pass the file on disk (`file`) or the manifest itself (`content`). dryRun " +
        "defaults to true and runs the SERVER-side validation ('kubectl apply --dry-run=server'), which catches what " +
        "the API server would reject without changing anything; set dryRun=false AND confirm=true to really apply. " +
        "Use this INSTEAD OF shell 'kubectl apply'.")]
    public static Task<KubernetesApplyResult> KubernetesApply(
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
    public static Task<KubernetesDeleteResult> KubernetesDelete(
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
    public static Task<KubernetesRolloutResult> KubernetesRollout(
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
        "get-values, template (render a chart with valuesFile/setValues, which is offline and safe), lint (check a " +
        "chart's templates, values and Chart.yaml with no cluster) or repo-list. Reports that helm is missing instead " +
        "of failing the call. Use this INSTEAD OF shell 'helm ...'.")]
    public static Task<KubernetesHelmResult> KubernetesHelm(
        IKubernetesService k8s,
        [Description("list | status | history | get-values | template | lint | repo-list")] string action = "list",
        [Description("Release name (required by status, history, get-values, template).")] string? release = null,
        [Description("Chart path or reference, for action=template and action=lint.")] string? chart = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Values file for action=template and action=lint.")] string? valuesFile = null,
        [Description("--set assignments for action=template and action=lint, e.g. ['image.tag=1.2.3'].")] string[]? setValues = null,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.HelmAsync(action, release, chart, namespaceName, valuesFile, setValues, timeoutSeconds);

    [McpServerTool(Name = "k8s_exec"), Description(
        "Run ONE command inside a pod's container ('kubectl exec': the part after '--'). It changes state inside " +
        "the container, so it refuses unless confirm=true. No shell is involved, so a pipe or a redirection needs " +
        "an explicit '/bin/sh -c \"…\"'. Use this INSTEAD OF shell 'kubectl exec'; for logs use k8s_logs.")]
    public static Task<KubernetesExecResult> KubernetesExec(
        IKubernetesService k8s,
        [Description("Pod name.")] string pod,
        [Description("The command and its arguments, e.g. 'ls -la /app'.")] string command,
        [Description("Container name, when the pod has several.")] string? container = null,
        [Description("Namespace; omit for the current context's namespace.")] string? namespaceName = null,
        [Description("Must be true: the command runs inside the container.")] bool confirm = false,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => k8s.ExecAsync(pod, container, namespaceName, command, confirm, timeoutSeconds);
}
