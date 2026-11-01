namespace Snitch.Kubernetes;

/// <summary>
/// The typed results <see cref="IKubernetesService"/> returns — one record per operation, shared by
/// every branch of it.
/// </summary>
/// <remarks>
/// Which members carry meaning depends on the branch that produced the result: a refusal sets
/// <see cref="KubernetesStatusResult.Error"/> and leaves the success members unset. Every member is
/// nullable on purpose. The MCP serializer drops a null member, so a result reaches the client as
/// exactly the shape that branch used to return as an anonymous object.
/// </remarks>
public sealed record KubernetesStatusResult
{
    /// <summary>Whether kubectl is installed and answered.</summary>
    public bool? Available { get; init; }

    /// <summary>The client version, e.g. <c>v1.34.1</c>.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>The server version, or <c>null</c> when no cluster answered.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Whether the configured cluster answered at all.</summary>
    public bool? ClusterReachable { get; init; }

    /// <summary>The selected kubeconfig context, or <c>null</c> when none is set.</summary>
    public string? CurrentContext { get; init; }

    /// <summary>Why reading the context failed, when it did.</summary>
    public string? ContextError { get; init; }

    /// <summary>The kubeconfig file the client reads.</summary>
    public string? Kubeconfig { get; init; }

    /// <summary>Whether that file exists on disk.</summary>
    public bool? KubeconfigExists { get; init; }

    /// <summary>The helm version, when helm is installed.</summary>
    public string? HelmVersion { get; init; }

    /// <summary>A sentence explaining why the cluster is unreachable.</summary>
    public string? Reason { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.ContextAsync"/> did for one action.</summary>
public sealed record KubernetesContextResult
{
    /// <summary>The action that ran: <c>current</c>, <c>list</c> or <c>use</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The context name, for <c>current</c> and <c>use</c>.</summary>
    public string? Context { get; init; }

    /// <summary>Whether kubectl accepted the action.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The table <c>kubectl config get-contexts</c> printed, for <c>list</c>.</summary>
    public string? Output { get; init; }

    /// <summary>A suggestion when the action had nothing to report.</summary>
    public string? Hint { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.GetAsync"/> listed or returned.</summary>
public sealed record KubernetesGetResult
{
    /// <summary>The kind that was asked for, e.g. <c>pods</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>The single resource name, when one was asked for.</summary>
    public string? Name { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>Whether all namespaces were listed.</summary>
    public bool? AllNamespaces { get; init; }

    /// <summary>The label selector that narrowed the call.</summary>
    public string? Selector { get; init; }

    /// <summary>The requested output format, for the client's own view.</summary>
    public string? Format { get; init; }

    /// <summary>The client's own output, when a format other than <c>summary</c> was asked for.</summary>
    public string? Raw { get; init; }

    /// <summary>How many rows the summary holds.</summary>
    public int? Count { get; init; }

    /// <summary>One row per resource, for the summary format.</summary>
    public IReadOnlyList<KubernetesResourceSummary>? Items { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>The raw output of a failed call.</summary>
    public string? Output { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.DescribeAsync"/> printed.</summary>
public sealed record KubernetesDescribeResult
{
    /// <summary>The kind that was described.</summary>
    public string? Kind { get; init; }

    /// <summary>The single resource name, when one was asked for.</summary>
    public string? Name { get; init; }

    /// <summary>Whether kubectl accepted the call.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>The describe text.</summary>
    public string? Output { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.LogsAsync"/> read.</summary>
public sealed record KubernetesLogsResult
{
    /// <summary>The pod the logs came from.</summary>
    public string? Pod { get; init; }

    /// <summary>The container that was asked for, when one was named.</summary>
    public string? Container { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>How many trailing lines were requested.</summary>
    public int? Tail { get; init; }

    /// <summary>The time window that was asked for, e.g. <c>10m</c>.</summary>
    public string? Since { get; init; }

    /// <summary>Whether the previous container instance was read.</summary>
    public bool? Previous { get; init; }

    /// <summary>Whether the pod produced no output at all.</summary>
    public bool? Empty { get; init; }

    /// <summary>The log text.</summary>
    public string? Logs { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.KustomizeAsync"/> rendered.</summary>
public sealed record KubernetesKustomizeResult
{
    /// <summary>The action that ran: <c>kustomize</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The kustomization that was rendered.</summary>
    public string? Path { get; init; }

    /// <summary>The namespace override that was applied.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>Whether the render succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>How many YAML documents the render produced.</summary>
    public int? Documents { get; init; }

    /// <summary>The rendered YAML, or the failure text.</summary>
    public string? Rendered { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>Whether kubectl is installed at all.</summary>
    public bool? Available { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.ExecAsync"/> ran and what the container printed.</summary>
public sealed record KubernetesExecResult
{
    /// <summary>The pod the command ran in.</summary>
    public string? Pod { get; init; }

    /// <summary>The container that was asked for, when one was named.</summary>
    public string? Container { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>Whether the command was accepted.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The container's exit code, or <c>null</c> when the call timed out.</summary>
    public int? ExitCode { get; init; }

    /// <summary>What the command printed on stdout.</summary>
    public string? Stdout { get; init; }

    /// <summary>What it printed on stderr.</summary>
    public string? Stderr { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>Whether kubectl is installed at all.</summary>
    public bool? Available { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.ApplyAsync"/> did.</summary>
public sealed record KubernetesApplyResult
{
    /// <summary>The manifest file that was applied, when it came from disk.</summary>
    public string? File { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>Whether the call was a server-side dry run.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Whether the caller confirmed the change.</summary>
    public bool? Confirmed { get; init; }

    /// <summary>Whether the API server accepted the manifest.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>What the API server answered.</summary>
    public string? Output { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.DeleteAsync"/> removed.</summary>
public sealed record KubernetesDeleteResult
{
    /// <summary>The kind that was deleted.</summary>
    public string? Kind { get; init; }

    /// <summary>The resource name that was deleted.</summary>
    public string? Name { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>Whether the resource was deleted.</summary>
    public bool? Deleted { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>What the API server answered.</summary>
    public string? Output { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.RolloutAsync"/> did.</summary>
public sealed record KubernetesRolloutResult
{
    /// <summary>The rollout action that ran: <c>status</c>, <c>restart</c> or <c>undo</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The workload kind.</summary>
    public string? Kind { get; init; }

    /// <summary>The workload name.</summary>
    public string? Name { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>The revision an <c>undo</c> went back to.</summary>
    public int? ToRevision { get; init; }

    /// <summary>Whether kubectl accepted the action.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The kubectl command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>What kubectl answered.</summary>
    public string? Output { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IKubernetesService.HelmAsync"/> did.</summary>
public sealed record KubernetesHelmResult
{
    /// <summary>The helm action that ran.</summary>
    public string? Action { get; init; }

    /// <summary>The release that was asked for.</summary>
    public string? Release { get; init; }

    /// <summary>The chart that was asked for.</summary>
    public string? Chart { get; init; }

    /// <summary>The namespace that narrowed the call.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>Whether helm accepted the action.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The helm command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>What helm printed.</summary>
    public string? Raw { get; init; }

    /// <summary>The findings of <c>helm lint</c>, when that action ran.</summary>
    public KubernetesHelmLintResult? Lint { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>Whether helm is installed at all.</summary>
    public bool? Available { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>The <c>helm lint</c> findings, counted by severity.</summary>
public sealed record KubernetesHelmLintResult
{
    /// <summary>The summary line helm printed at the end.</summary>
    public string? Summary { get; init; }

    /// <summary>How many findings each severity has.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    /// <summary>The findings themselves, capped at 200.</summary>
    public IReadOnlyList<KubernetesHelmLintFinding>? Findings { get; init; }

    /// <summary>What the linter proves, and what it does not.</summary>
    public string? Linter { get; init; }
}

/// <summary>One <c>helm lint</c> finding.</summary>
/// <param name="Severity">The bracketed severity helm printed: <c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Message">The finding text.</param>
public sealed record KubernetesHelmLintFinding(string Severity, string Message);

/// <summary>
/// Base of every answer <see cref="IKubernetesService.ValidateAsync"/> gives.
/// </summary>
/// <remarks>
/// The method answers four shapes: one manifest validated offline (the
/// <see cref="ManifestValidationResult"/> itself), one manifest validated offline and on the server, a
/// folder validated offline, and a folder validated both ways. <c>server</c> is one object in the
/// single-manifest case and an array in the folder case, so those two live in their own records; the
/// declared type is this base and the serializer writes the runtime type.
/// </remarks>
public record KubernetesValidateResult
{
    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>One manifest validated offline and then by the cluster's own dry run.</summary>
public sealed record KubernetesServerValidateResult : ManifestValidationResult
{
    /// <summary>The offline answer.</summary>
    public ManifestValidationResult? Offline { get; init; }

    /// <summary>The API server's own dry run.</summary>
    public KubernetesServerValidationResult? Server { get; init; }
}

/// <summary>A folder of manifests validated, each file on its own.</summary>
public sealed record KubernetesFolderValidateResult : ManifestValidationResult
{
    /// <summary>The files that were validated.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>One offline answer per file.</summary>
    public IReadOnlyList<ManifestValidationResult>? Results { get; init; }

    /// <summary>One server dry-run answer per file, when <c>server</c> was set.</summary>
    public IReadOnlyList<KubernetesServerValidationResult>? Server { get; init; }
}

/// <summary>What <c>kubectl apply --dry-run=server</c> answered about one manifest.</summary>
public sealed record KubernetesServerValidationResult
{
    /// <summary>The manifest that was sent.</summary>
    public string? File { get; init; }

    /// <summary>Whether the cluster was asked at all.</summary>
    public bool? Ran { get; init; }

    /// <summary>Whether the API server accepted the manifest.</summary>
    public bool? Valid { get; init; }

    /// <summary>The exit code kubectl returned.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether kubectl ran out of time.</summary>
    public bool? TimedOut { get; init; }

    /// <summary>What the API server answered.</summary>
    public string? Output { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>Why the dry run could not run, when it could not.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Base of every answer <see cref="IKubernetesService.PortForwardAsync"/> gives.
/// </summary>
/// <remarks>
/// The branches differ in more than their values — <c>output</c> is a string when the forward failed to
/// start and an array of lines for a live session — so each shape has its own record and the declared
/// type is this base; the serializer writes the runtime type.
/// </remarks>
public record KubernetesPortForwardResult
{
    /// <summary>The action that ran: <c>start</c>, <c>list</c>, <c>status</c> or <c>stop</c>.</summary>
    public string? Action { get; init; }

    /// <summary>Whether kubectl is installed at all.</summary>
    public bool? Available { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>The live port-forward sessions.</summary>
public sealed record KubernetesPortForwardSessionsResult : KubernetesPortForwardResult
{
    /// <summary>The live sessions, oldest first.</summary>
    public IReadOnlyList<KubernetesPortForwardSession>? Sessions { get; init; }
}

/// <summary>One port-forward session: its state, or the fact that it was stopped.</summary>
public sealed record KubernetesPortForwardStatusResult : KubernetesPortForwardResult
{
    /// <summary>The session the action acted on.</summary>
    public string? SessionId { get; init; }

    /// <summary>The target the forward points at.</summary>
    public string? Target { get; init; }

    /// <summary>The port mapping that was asked for.</summary>
    public string? Ports { get; init; }

    /// <summary>The namespace the target lives in.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>The local port the forward bound.</summary>
    public int? LocalPort { get; init; }

    /// <summary>The pid of the kubectl process that owns the forward.</summary>
    public int? Pid { get; init; }

    /// <summary>Whether that process is still running.</summary>
    public bool? Alive { get; init; }

    /// <summary>When the forward started, in UTC.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>The last lines kubectl printed for the session.</summary>
    public IReadOnlyList<string>? Output { get; init; }

    /// <summary>Whether <c>stop</c> ended the session.</summary>
    public bool? Stopped { get; init; }
}

/// <summary>A port-forward that came up.</summary>
public sealed record KubernetesPortForwardStartResult : KubernetesPortForwardResult
{
    /// <summary>The session id, used by <c>status</c> and <c>stop</c>.</summary>
    public string? SessionId { get; init; }

    /// <summary>The target the forward points at.</summary>
    public string? Target { get; init; }

    /// <summary>The port mapping that was asked for.</summary>
    public string? Ports { get; init; }

    /// <summary>The namespace the target lives in.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>The local port the forward bound.</summary>
    public int? LocalPort { get; init; }

    /// <summary>The pid of the kubectl process that owns the forward.</summary>
    public int? Pid { get; init; }

    /// <summary>The lines kubectl printed when the forward came up.</summary>
    public IReadOnlyList<string>? Forwarding { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }
}

/// <summary>A port-forward that exited before it forwarded anything.</summary>
public sealed record KubernetesPortForwardStartFailureResult : KubernetesPortForwardResult
{
    /// <summary>The target the forward was asked for.</summary>
    public string? Target { get; init; }

    /// <summary>The port mapping that was asked for.</summary>
    public string? Ports { get; init; }

    /// <summary>Whether the forward came up.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>What kubectl printed before it exited.</summary>
    public string? Output { get; init; }
}

/// <summary>One live port-forward session.</summary>
public sealed record KubernetesPortForwardSession
{
    /// <summary>The session id, used by <c>status</c> and <c>stop</c>.</summary>
    public string? SessionId { get; init; }

    /// <summary>The target the forward points at.</summary>
    public string? Target { get; init; }

    /// <summary>The port mapping that was asked for.</summary>
    public string? Ports { get; init; }

    /// <summary>The namespace the target lives in.</summary>
    public string? NamespaceName { get; init; }

    /// <summary>The local port the forward bound.</summary>
    public int? LocalPort { get; init; }

    /// <summary>The pid of the kubectl process that owns the forward.</summary>
    public int? Pid { get; init; }

    /// <summary>Whether that process is still running.</summary>
    public bool? Alive { get; init; }

    /// <summary>When the forward started, in UTC.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>The last lines kubectl printed for the session.</summary>
    public IReadOnlyList<string>? Output { get; init; }
}
