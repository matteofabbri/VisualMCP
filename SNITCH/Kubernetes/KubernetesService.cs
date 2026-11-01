using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Snitch.Execution;

namespace Snitch.Kubernetes;

/// <summary>
/// Kubernetes through the ecosystem's own client: <c>kubectl</c> for the cluster and <c>helm</c>
/// for releases. Every call returns a structured result — the command, its exit code, what the
/// cluster answered — and a missing client or an unreachable cluster is a value, not an exception.
/// The cluster is never modified unless the caller passes <c>confirm=true</c>.
/// </summary>
public interface IKubernetesService
{
    /// <summary>Client/server versions, the current context and whether a cluster answers.</summary>
    Task<KubernetesStatusResult> StatusAsync(int timeoutSeconds);

    Task<KubernetesContextResult> ContextAsync(string action, string? name, int timeoutSeconds);
    Task<KubernetesGetResult> GetAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        string? selector, string? output, int timeoutSeconds);
    Task<KubernetesDescribeResult> DescribeAsync(string kind, string? name, string? namespaceName, bool allNamespaces, int timeoutSeconds);
    Task<KubernetesLogsResult> LogsAsync(string pod, string? container, string? namespaceName, int tail, string? since,
        bool previous, int timeoutSeconds);
    Task<KubernetesKustomizeResult> KustomizeAsync(string path, string? namespaceName, int timeoutSeconds);
    Task<KubernetesExecResult> ExecAsync(string pod, string? container, string? namespaceName, string command, bool confirm,
        int timeoutSeconds);
    Task<KubernetesApplyResult> ApplyAsync(string? file, string? content, string? namespaceName, bool dryRun, bool confirm,
        int timeoutSeconds);
    Task<KubernetesDeleteResult> DeleteAsync(string kind, string name, string? namespaceName, bool ignoreNotFound, bool confirm,
        int timeoutSeconds);
    Task<KubernetesRolloutResult> RolloutAsync(string action, string kind, string name, string? namespaceName, int? toRevision,
        bool confirm, int timeoutSeconds);
    Task<KubernetesHelmResult> HelmAsync(string action, string? release, string? chart, string? namespaceName,
        string? valuesFile, string[]? setValues, int timeoutSeconds);

    /// <summary>Validation of a manifest: the built-in kinds' required fields, and — with <c>server=true</c> — the cluster's own dry run.</summary>
    Task<KubernetesValidateResult> ValidateAsync(string? file, string? content, bool server = false);

    /// <summary>Port-forward sessions: start, list, status, stop.</summary>
    Task<KubernetesPortForwardResult> PortForwardAsync(string action, string? sessionId, string? target, string? ports,
        string? namespaceName, int timeoutSeconds);
}

/// <summary>One row of a <c>kubectl get</c>, reduced to what makes a list readable.</summary>
public sealed record KubernetesResourceSummary(
    string? Kind, string? Name, string? Namespace, string? Status, int? Restarts, int? Ready, int? Desired,
    string? CreatedAt);

public sealed class KubernetesService : IKubernetesService
{
    private static readonly string[] RolloutActions = ["status", "restart", "undo"];
    private static readonly string[] HelmActions = ["list", "status", "history", "get-values", "template", "lint", "repo-list"];

    // ── status ───────────────────────────────────────────────────────────────
    public async Task<KubernetesStatusResult> StatusAsync(int timeoutSeconds)
    {
        if (!ExternalTool.Exists("kubectl"))
            return new KubernetesStatusResult
            {
                Available = false,
                Error = "kubectl was not found on PATH. Install it (https://kubernetes.io/docs/tasks/tools/) " +
                        "and let it see a kubeconfig; helm is optional and reported separately.",
                HelmVersion = await ExternalTool.VersionAsync("helm", "version --short"),
            };

        var timeout = Clamp(timeoutSeconds);
        var client = await KubectlAsync("version --client -o json", timeout);
        var server = await KubectlAsync("version -o json", timeout);
        var context = await KubectlAsync("config current-context", timeout);
        var helm = await ExternalTool.VersionAsync("helm", "version --short");

        var clientVersion = ReadVersion(client.Stdout, "clientVersion");
        var serverVersion = ReadVersion(server.Stdout, "serverVersion");

        var kubeconfig = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            kubeconfig = Path.Combine(home, ".kube", "config");
        }

        return new KubernetesStatusResult
        {
            Available = true,
            ClientVersion = clientVersion,
            ServerVersion = serverVersion,
            ClusterReachable = serverVersion is not null,
            CurrentContext = context.Succeeded ? FirstLine(context.Stdout) : null,
            ContextError = context.Succeeded ? null : FirstLine(context.Stderr),
            Kubeconfig = kubeconfig,
            KubeconfigExists = kubeconfig is not null && File.Exists(kubeconfig.Split(Path.PathSeparator)[0]),
            HelmVersion = helm,
            Reason = serverVersion is null
                ? (string.IsNullOrWhiteSpace(server.Stderr)
                    ? "The cluster did not answer: no current context is set, or the API server is unreachable."
                    : FirstLine(server.Stderr))
                : null,
        };
    }

    // ── contexts ─────────────────────────────────────────────────────────────
    public async Task<KubernetesContextResult> ContextAsync(string action, string? name, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        switch (verb)
        {
            case "current":
            {
                var run = await KubectlAsync("config current-context", Clamp(timeoutSeconds));
                return new KubernetesContextResult
                {
                    Action = verb,
                    Succeeded = run.Succeeded,
                    Context = run.Succeeded ? FirstLine(run.Stdout) : null,
                    Error = run.Succeeded ? null : FirstLine(run.Stderr),
                    Hint = run.Succeeded
                        ? null
                        : "No context is selected. 'kubectl config get-contexts' lists the available ones.",
                };
            }
            case "list":
            {
                var run = await KubectlAsync("config get-contexts", Clamp(timeoutSeconds));
                return new KubernetesContextResult
                {
                    Action = verb,
                    Succeeded = run.Succeeded,
                    Output = ProcessRunner.Truncate(run.Text, 12_000),
                    Error = run.Succeeded ? null : FirstLine(run.Stderr),
                };
            }
            case "use":
            {
                if (!IsSafeToken(name))
                    return new KubernetesContextResult { Error = "The use action needs a context name." };
                var run = await KubectlAsync($"config use-context {Quote(name!)}", Clamp(timeoutSeconds));
                return new KubernetesContextResult
                {
                    Action = verb,
                    Context = name,
                    Succeeded = run.Succeeded,
                    Output = ProcessRunner.Truncate(run.Text, 4_000),
                    Error = run.Succeeded ? null : FirstLine(run.Stderr),
                };
            }
            default:
                return new KubernetesContextResult { Error = $"Unknown action '{action}'. Use one of: current, list, use." };
        }
    }

    // ── get / describe ───────────────────────────────────────────────────────
    public async Task<KubernetesGetResult> GetAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        string? selector, string? output, int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new KubernetesGetResult { Error = "A resource kind is required, e.g. pods, deployments, services (comma-separated kinds are allowed)." };
        if (!string.IsNullOrWhiteSpace(name) && !IsSafeToken(name)) return new KubernetesGetResult { Error = $"'{name}' is not a resource name." };
        if (!string.IsNullOrWhiteSpace(selector) && !IsSafeSelector(selector!))
            return new KubernetesGetResult { Error = "The selector must be a label selector such as 'app=api,tier=web'." };
        if (!IsSafeOutput(output)) return new KubernetesGetResult { Error = $"'{output}' is not an output format." };

        var format = string.IsNullOrWhiteSpace(output) ? "summary" : output!.Trim();
        var args = $"get {kind}";
        if (!string.IsNullOrWhiteSpace(name)) args += $" {Quote(name!)}";
        args += format == "summary" ? " -o json" : $" -o {Quote(format)}";
        args += NamespaceArgs(namespaceName, allNamespaces);
        if (!string.IsNullOrWhiteSpace(selector)) args += $" --selector {Quote(selector!)}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        if (!run.Succeeded)
            return new KubernetesGetResult
            {
                Error = $"kubectl get failed: {FirstLine(run.Stderr)}",
                Command = $"kubectl {args}",
                Output = ProcessRunner.Truncate(run.Text, 8_000),
            };

        if (format != "summary")
            return new KubernetesGetResult
            {
                Kind = kind,
                Name = name,
                NamespaceName = namespaceName,
                AllNamespaces = allNamespaces,
                Format = format,
                Command = $"kubectl {args}",
                Raw = ProcessRunner.Truncate(run.Stdout, 32_000),
            };

        var items = SummarizeItems(run.Stdout);
        return new KubernetesGetResult
        {
            Kind = kind,
            Name = name,
            NamespaceName = namespaceName,
            AllNamespaces = allNamespaces,
            Selector = selector,
            Count = items.Count,
            Items = items,
            Command = $"kubectl {args}",
            Note = "Summary of 'kubectl get -o json'; pass output=yaml|json|wide|name|jsonpath=… for the client's own view.",
        };
    }

    public async Task<KubernetesDescribeResult> DescribeAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new KubernetesDescribeResult { Error = "A resource kind is required, e.g. pods, deployments." };
        if (!string.IsNullOrWhiteSpace(name) && !IsSafeToken(name)) return new KubernetesDescribeResult { Error = $"'{name}' is not a resource name." };

        var args = $"describe {kind}";
        if (!string.IsNullOrWhiteSpace(name)) args += $" {Quote(name!)}";
        args += NamespaceArgs(namespaceName, allNamespaces);

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new KubernetesDescribeResult
        {
            Kind = kind,
            Name = name,
            Succeeded = run.Succeeded,
            Command = $"kubectl {args}",
            Output = ProcessRunner.Truncate(run.Text, 32_000),
            Error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    // ── logs ─────────────────────────────────────────────────────────────────
    public async Task<KubernetesLogsResult> LogsAsync(string pod, string? container, string? namespaceName, int tail, string? since,
        bool previous, int timeoutSeconds)
    {
        if (!IsSafeToken(pod)) return new KubernetesLogsResult { Error = "A pod name is required." };
        if (!string.IsNullOrWhiteSpace(container) && !IsSafeToken(container)) return new KubernetesLogsResult { Error = $"'{container}' is not a container name." };
        if (!string.IsNullOrWhiteSpace(since) && !IsSafeToken(since)) return new KubernetesLogsResult { Error = "since must look like '10m' or '2026-10-31T10:00:00Z'." };

        var args = $"logs {Quote(pod)}";
        if (!string.IsNullOrWhiteSpace(container)) args += $" -c {Quote(container!)}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        args += $" --tail {Math.Clamp(tail, 1, 10_000)}";
        if (!string.IsNullOrWhiteSpace(since)) args += $" --since {Quote(since!)}";
        if (previous) args += " --previous";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        var text = (run.Stdout + "\n" + run.Stderr).Trim();
        return new KubernetesLogsResult
        {
            Pod = pod,
            Container = container,
            NamespaceName = namespaceName,
            Tail = tail,
            Since = since,
            Previous = previous,
            Empty = string.IsNullOrWhiteSpace(text),
            Logs = ProcessRunner.Truncate(text, 64_000),
            Command = $"kubectl {args}",
            Error = run.Succeeded || !string.IsNullOrWhiteSpace(run.Stdout) ? null : FirstLine(run.Stderr),
        };
    }

    // ── kustomize (offline render) ───────────────────────────────────────────
    /// <summary>
    /// Renders a kustomization without touching a cluster: <c>kubectl kustomize</c> is the client-side
    /// build step, so this answers what the overlay produces before anything is applied.
    /// </summary>
    public async Task<KubernetesKustomizeResult> KustomizeAsync(string path, string? namespaceName, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new KubernetesKustomizeResult { Error = "A kustomization directory (or a git/URL reference) is required." };
        if (path.Contains('\r') || path.Contains('\n'))
            return new KubernetesKustomizeResult { Error = "The path must be a single line." };
        if (!string.IsNullOrWhiteSpace(namespaceName) && !IsSafeToken(namespaceName))
            return new KubernetesKustomizeResult { Error = $"'{namespaceName}' is not a namespace name." };

        if (!ExternalTool.Exists("kubectl"))
            return new KubernetesKustomizeResult { Available = false, Error = "kubectl was not found on PATH: kustomize is a kubectl subcommand." };

        // A local path is checked and made absolute; anything else (a git reference) is passed through.
        var target = path;
        if (Directory.Exists(path) || File.Exists(path)) target = Path.GetFullPath(path);

        var args = $"kustomize {Quote(target)}";
        if (!string.IsNullOrWhiteSpace(namespaceName)) args += $" --namespace {Quote(namespaceName!)}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        var rendered = run.Succeeded ? run.Stdout.Trim() : null;
        return new KubernetesKustomizeResult
        {
            Action = "kustomize",
            Path = target,
            NamespaceName = namespaceName,
            Succeeded = run.Succeeded,
            Command = $"kubectl {args}",
            Documents = rendered is null ? 0 : CountDocuments(rendered),
            Rendered = rendered is null ? ProcessRunner.Truncate(run.Text, 4_000) : ProcessRunner.Truncate(rendered, 64_000),
            Error = run.Succeeded ? null : FirstLine(run.Stderr),
            Note = "A client-side render: no cluster was contacted. Apply the result with k8s_apply (dryRun first).",
        };
    }

    // ── exec ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Runs one command inside a container. It changes state inside the pod, so it needs
    /// <c>confirm=true</c>; the command goes after <c>--</c> exactly as the shell would pass it,
    /// except that no shell is involved, so redirection and pipes do not work.
    /// </summary>
    public async Task<KubernetesExecResult> ExecAsync(string pod, string? container, string? namespaceName, string command,
        bool confirm, int timeoutSeconds)
    {
        if (!IsSafeToken(pod)) return new KubernetesExecResult { Error = "A pod name is required." };
        if (!string.IsNullOrWhiteSpace(container) && !IsSafeToken(container))
            return new KubernetesExecResult { Error = $"'{container}' is not a container name." };
        if (string.IsNullOrWhiteSpace(command))
            return new KubernetesExecResult { Error = "A command is required: the part kubectl receives after '--'." };
        if (command.Contains('\r') || command.Contains('\n'))
            return new KubernetesExecResult { Error = "The command must be a single line." };
        if (!confirm)
            return new KubernetesExecResult { Error = "exec runs a command inside the container (it can change state). Pass confirm=true to proceed." };

        if (!ExternalTool.Exists("kubectl"))
            return new KubernetesExecResult { Available = false, Error = "kubectl was not found on PATH." };

        var args = $"exec {Quote(pod)}";
        if (!string.IsNullOrWhiteSpace(container)) args += $" -c {Quote(container!)}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        args += $" -- {command}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new KubernetesExecResult
        {
            Pod = pod,
            Container = container,
            NamespaceName = namespaceName,
            Command = $"kubectl {args}",
            Succeeded = run.Succeeded,
            ExitCode = run.TimedOut ? (int?)null : run.ExitCode,
            Stdout = ProcessRunner.Truncate(run.Stdout, 32_000),
            Stderr = ProcessRunner.Truncate(run.Stderr, 8_000),
            Error = run.Succeeded ? null : FirstLine(run.Stderr),
            Note = "kubectl exec runs the program directly: a pipe or a redirection inside it needs a shell " +
                   "(/bin/sh -c \"…\").",
        };
    }

    // ── apply / delete / rollout (the state-changing half) ───────────────────
    public async Task<KubernetesApplyResult> ApplyAsync(string? file, string? content, string? namespaceName, bool dryRun,
        bool confirm, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(file) == string.IsNullOrWhiteSpace(content))
            return new KubernetesApplyResult { Error = "Pass exactly one of file (a manifest on disk) or content (the manifest itself)." };

        if (!string.IsNullOrWhiteSpace(file) && !File.Exists(file))
            return new KubernetesApplyResult { Error = $"Manifest not found: {file}" };

        if (!dryRun && !confirm)
            return new KubernetesApplyResult
            {
                Error = "Apply changes the cluster. Pass confirm=true, or keep dryRun=true for a server-side validation.",
                DryRun = dryRun,
            };

        string? temporary = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                temporary = Path.Combine(Path.GetTempPath(),
                    $"vmcp-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.yaml");
                await File.WriteAllTextAsync(temporary, content!);
                file = temporary;
            }

            var args = $"apply -f {Quote(file!)}";
            args += NamespaceArgs(namespaceName, allNamespaces: false);
            args += dryRun ? " --dry-run=server" : string.Empty;

            var run = await KubectlAsync(args, Clamp(timeoutSeconds));
            return new KubernetesApplyResult
            {
                File = temporary is null ? file : null,
                NamespaceName = namespaceName,
                DryRun = dryRun,
                Confirmed = confirm,
                Succeeded = run.Succeeded,
                Command = $"kubectl {args}",
                Output = ProcessRunner.Truncate(run.Text, 12_000),
                Error = run.Succeeded ? null : FirstLine(run.Stderr),
            };
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* best-effort */ }
        }
    }

    public async Task<KubernetesDeleteResult> DeleteAsync(string kind, string name, string? namespaceName, bool ignoreNotFound,
        bool confirm, int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new KubernetesDeleteResult { Error = "A resource kind is required, e.g. pods, deployments." };
        if (!IsSafeToken(name)) return new KubernetesDeleteResult { Error = "A resource name is required." };
        if (!confirm)
            return new KubernetesDeleteResult { Error = "Delete removes cluster state that cannot be recovered. Pass confirm=true to proceed." };

        var args = $"delete {kind} {Quote(name)}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (ignoreNotFound) args += " --ignore-not-found";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new KubernetesDeleteResult
        {
            Kind = kind,
            Name = name,
            NamespaceName = namespaceName,
            Deleted = run.Succeeded,
            Command = $"kubectl {args}",
            Output = ProcessRunner.Truncate(run.Text, 4_000),
            Error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    public async Task<KubernetesRolloutResult> RolloutAsync(string action, string kind, string name, string? namespaceName,
        int? toRevision, bool confirm, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!RolloutActions.Contains(verb))
            return new KubernetesRolloutResult { Error = $"Unknown action '{action}'. Use one of: {string.Join(", ", RolloutActions)}." };
        if (!IsSafeToken(kind)) return new KubernetesRolloutResult { Error = "A workload kind is required, e.g. deployment, statefulset, daemonset." };
        if (!IsSafeToken(name)) return new KubernetesRolloutResult { Error = "A workload name is required." };
        if (verb is "restart" or "undo" && !confirm)
            return new KubernetesRolloutResult { Error = $"{verb} changes the cluster (it rolls the pods). Pass confirm=true to proceed." };

        var args = $"rollout {verb} {kind}/{name}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (verb == "undo" && toRevision is > 0) args += $" --to-revision {toRevision.Value}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new KubernetesRolloutResult
        {
            Action = verb,
            Kind = kind,
            Name = name,
            NamespaceName = namespaceName,
            ToRevision = toRevision,
            Succeeded = run.Succeeded,
            Command = $"kubectl {args}",
            Output = ProcessRunner.Truncate(run.Text, 8_000),
            Error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    // ── helm ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Helm through its own CLI. <c>lint</c> and <c>template</c> are the offline half: a chart is checked
    /// and rendered without a cluster, which is the chart validation the raw manifest tools cannot give.
    /// </summary>
    public async Task<KubernetesHelmResult> HelmAsync(string action, string? release, string? chart, string? namespaceName,
        string? valuesFile, string[]? setValues, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!HelmActions.Contains(verb))
            return new KubernetesHelmResult { Error = $"Unknown action '{action}'. Use one of: {string.Join(", ", HelmActions)}." };

        // The arguments are checked first, so a malformed call is reported as such whether or not helm is installed.
        if (verb is "status" or "history" or "get-values" or "template" && !IsSafeToken(release))
            return new KubernetesHelmResult { Error = $"The {verb} action needs a release name." };
        if (verb is "template" or "lint" && !IsSafeToken(chart))
            return new KubernetesHelmResult { Error = $"The {verb} action needs a chart (a local path or a repo/chart reference)." };

        if (!ExternalTool.Exists("helm"))
            return new KubernetesHelmResult
            {
                Available = false,
                Error = "helm was not found on PATH. Install it (https://helm.sh/docs/intro/install/) for release " +
                        "listing, status and history; kubectl covers the raw resources without it.",
            };

        var args = verb switch
        {
            "list" => "list",
            "repo-list" => "repo list",
            "status" => $"status {Quote(release!)}",
            "history" => $"history {Quote(release!)}",
            "get-values" => $"get values {Quote(release!)}",
            "lint" => $"lint {Quote(chart!)}",
            _ => $"template {Quote(release!)} {Quote(chart!)}",
        };

        if (verb is "list" or "status" or "history" or "get-values")
            args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (verb is "list" or "history") args += " -o json";
        if (!string.IsNullOrWhiteSpace(valuesFile))
        {
            if (!File.Exists(valuesFile)) return new KubernetesHelmResult { Error = $"Values file not found: {valuesFile}" };
            args += $" -f {Quote(valuesFile!)}";
        }
        foreach (var value in setValues ?? [])
        {
            if (!IsSafeToken(value) || !value.Contains('='))
                return new KubernetesHelmResult { Error = $"'{value}' is not a --set assignment (key=value)." };
            args += $" --set {Quote(value)}";
        }

        var run = await ExternalTool.RunAsync("helm", args, Directory.GetCurrentDirectory(), Clamp(timeoutSeconds));
        var succeeded = !run.timedOut && run.exitCode == 0;

        return new KubernetesHelmResult
        {
            Action = verb,
            Release = release,
            Chart = chart,
            NamespaceName = namespaceName,
            Succeeded = succeeded,
            Command = $"helm {args}",
            Raw = ProcessRunner.Truncate(run.stdout, 32_000),
            Lint = verb == "lint" ? SummarizeLintOutput(run.stdout + "\n" + run.stderr) : null,
            Error = succeeded ? null : FirstLine(run.stderr),
            Note = verb == "lint"
                ? "chart lints require no cluster: the chart's templates, values and Chart.yaml are checked locally. " +
                  "For a manifest, kubectl apply --dry-run=server validates it against the API server (k8s_apply, dryRun=true)."
                : null,
        };
    }

    /// <summary>
    /// The <c>helm lint</c> findings: its <c>[INFO]</c>/<c>[WARNING]</c>/<c>[ERROR]</c> lines, counted by
    /// severity, plus the summary line it prints at the end.
    /// </summary>
    public static KubernetesHelmLintResult SummarizeLintOutput(string output)
    {
        var findings = new List<KubernetesHelmLintFinding>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? summary = null;

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r').Trim();
            if (text.Length == 0) continue;

            var severity = text.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ? "error"
                : text.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase) ? "warning"
                : text.StartsWith("[INFO]", StringComparison.OrdinalIgnoreCase) ? "info"
                : null;

            if (severity is null)
            {
                if (text.Contains("linted", StringComparison.OrdinalIgnoreCase)) summary = text;
                continue;
            }

            counts[severity] = counts.TryGetValue(severity, out var seen) ? seen + 1 : 1;
            if (findings.Count < 200)
                findings.Add(new KubernetesHelmLintFinding(severity, text[text.IndexOf(']')..].TrimStart(' ', ']').Trim()));
        }

        return new KubernetesHelmLintResult
        {
            Summary = summary,
            Counts = counts,
            Findings = findings,
            Linter = "helm lint: templates render, values are valid for the chart, Chart.yaml is well formed. " +
                     "It does not validate a rendered manifest against the Kubernetes API schema.",
        };
    }

    // ── offline manifest validation ──────────────────────────────────────────
    /// <summary>
    /// Validates a manifest without a cluster: the required fields of the built-in kinds, the
    /// selector/template agreement a Deployment needs, a pinned image, a name that is a DNS label, and the
    /// duplicate resources an apply would silently collapse. <c>file</c> may be one manifest or a folder
    /// of them; <c>content</c> is the manifest itself. With <c>server</c> the same text is also sent to the
    /// configured cluster through <c>kubectl apply --dry-run=server</c>, which is the authority for a CRD's
    /// OpenAPI schema and an admission webhook.
    /// </summary>
    public async Task<KubernetesValidateResult> ValidateAsync(string? file, string? content, bool server = false)
    {
        if (string.IsNullOrWhiteSpace(file) == string.IsNullOrWhiteSpace(content))
            return new KubernetesValidateResult { Error = "Pass exactly one of file (a manifest or a folder of them) or content (the manifest itself)." };

        var sources = new List<(string Name, string Text)>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            sources.Add(("(content)", content!));
        }
        else if (File.Exists(file))
        {
            sources.Add((Path.GetFullPath(file!), await File.ReadAllTextAsync(file!)));
        }
        else if (Directory.Exists(file))
        {
            var files = Directory.EnumerateFiles(file!, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            if (files.Count == 0)
                return new KubernetesValidateResult { Error = $"No .yaml or .yml file in {file}." };

            foreach (var path in files)
                sources.Add((path, await File.ReadAllTextAsync(path)));
        }
        else
        {
            return new KubernetesValidateResult { Error = $"Not found: {file}" };
        }

        var results = sources
            .Select(source => KubernetesManifestValidator.Validate(source.Text, source.Name))
            .ToList();

        if (server)
        {
            var serverResults = new List<KubernetesServerValidationResult>();
            foreach (var source in sources)
                serverResults.Add(await ValidateOnServerAsync(source));

            if (results.Count == 1 && !Directory.Exists(file))
                return new KubernetesServerValidateResult { Offline = results[0], Server = serverResults[0] };

            return new KubernetesFolderValidateResult
            {
                Files = sources.Select(source => source.Name).ToList(),
                Results = results,
                Server = serverResults,
                Note = "Each file was validated on its own offline; the server result is the API server's own " +
                       "dry run (OpenAPI schema and admission chain).",
            };
        }

        // One manifest (text or a single file) is answered on its own; a folder is a set, so the wrapper
        // keeps the per-file answers apart.
        if (results.Count == 1 && !Directory.Exists(file)) return results[0];

        return new KubernetesFolderValidateResult
        {
            Files = sources.Select(source => source.Name).ToList(),
            Results = results,
            Note = "Each file was validated on its own; K8S023 (a volume naming a ConfigMap or Secret) is " +
                   "checked within one file.",
        };
    }

    /// <summary>
    /// Writes the manifest to the store's temporary folder and asks the cluster to dry-run it. A missing
    /// kubectl or an unreachable cluster is a structured result, not a failure of the call.
    /// </summary>
    private static async Task<KubernetesServerValidationResult> ValidateOnServerAsync((string Name, string Text) source)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".visualMCP", "tmp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"k8s-validate-{Guid.NewGuid():N}.yaml");

        try
        {
            await File.WriteAllTextAsync(path, source.Text);
            var run = await KubectlAsync($"apply --dry-run=server -f \"{path}\"", 60);

            if (run.ExitCode == 127)
                return new KubernetesServerValidationResult { File = source.Name, Ran = false, Error = run.Stderr.Trim() };

            var output = string.IsNullOrWhiteSpace(run.Stdout) ? run.Stderr : run.Stdout;
            return new KubernetesServerValidationResult
            {
                File = source.Name,
                Ran = true,
                Valid = run.ExitCode == 0 && !run.TimedOut,
                ExitCode = run.ExitCode,
                TimedOut = run.TimedOut,
                Output = output.Length > 4_000 ? output[..4_000] : output,
                Note = run.ExitCode == 0
                    ? "The cluster accepted it: the OpenAPI schema and the admission chain were applied."
                    : "The cluster refused it: the output is the API server's own message — a CRD's schema, " +
                      "an admission webhook or a missing namespace.",
            };
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    // ── port-forward sessions ────────────────────────────────────────────────
    private sealed class PortForwardSession
    {
        public required string Id { get; init; }
        public required string Target { get; init; }
        public required string Ports { get; init; }
        public string? NamespaceName { get; init; }
        public required Process Process { get; init; }
        public required ConcurrentQueue<string> Output { get; init; }
        public DateTime StartedAt { get; init; }

        public bool Alive
        {
            get
            {
                try { return !Process.HasExited; }
                catch { return false; }
            }
        }
    }

    private readonly ConcurrentDictionary<string, PortForwardSession> _portForwards = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>kubectl port-forward</c> as a session: <c>start</c> launches it detached, waits for its first
    /// "Forwarding from" line and returns the id, the local port and the pid; <c>list</c>, <c>status</c> and
    /// <c>stop</c> act on that session. The forward lives in this server process, so it ends when the
    /// server does.
    /// </summary>
    public Task<KubernetesPortForwardResult> PortForwardAsync(string action, string? sessionId, string? target, string? ports,
        string? namespaceName, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        switch (verb)
        {
            case "list":
                PruneFinishedForwards();
                return Task.FromResult<KubernetesPortForwardResult>(new KubernetesPortForwardSessionsResult
                {
                    Action = verb,
                    Sessions = _portForwards.Values
                        .OrderBy(session => session.StartedAt)
                        .Select(session => new KubernetesPortForwardSession
                        {
                            SessionId = session.Id,
                            Target = session.Target,
                            Ports = session.Ports,
                            NamespaceName = session.NamespaceName,
                            LocalPort = FirstLocalPort(session.Ports),
                            Pid = session.Process.Id,
                            Alive = session.Alive,
                            StartedAt = session.StartedAt,
                            Output = session.Output.ToArray().TakeLast(10).ToList(),
                        })
                        .ToList(),
                });

            case "status":
            case "stop":
            {
                if (string.IsNullOrWhiteSpace(sessionId) || !_portForwards.TryGetValue(sessionId!, out var session))
                    return Task.FromResult<KubernetesPortForwardResult>(
                        new KubernetesPortForwardResult { Error = $"No port-forward session '{sessionId}'." });

                if (verb == "stop")
                {
                    StopForward(session);
                    return Task.FromResult<KubernetesPortForwardResult>(new KubernetesPortForwardStatusResult
                    {
                        Action = verb,
                        SessionId = session.Id,
                        Target = session.Target,
                        Stopped = true,
                    });
                }

                return Task.FromResult<KubernetesPortForwardResult>(new KubernetesPortForwardStatusResult
                {
                    Action = verb,
                    SessionId = session.Id,
                    Target = session.Target,
                    Ports = session.Ports,
                    NamespaceName = session.NamespaceName,
                    LocalPort = FirstLocalPort(session.Ports),
                    Pid = session.Process.Id,
                    Alive = session.Alive,
                    StartedAt = session.StartedAt,
                    Output = session.Output.ToArray().TakeLast(20).ToList(),
                });
            }

            case "start":
                return StartForwardAsync(target, ports, namespaceName, timeoutSeconds);

            default:
                return Task.FromResult<KubernetesPortForwardResult>(
                    new KubernetesPortForwardResult { Error = $"Unknown action '{action}'. Use start, list, status or stop." });
        }
    }

    private async Task<KubernetesPortForwardResult> StartForwardAsync(string? target, string? ports, string? namespaceName,
        int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.Contains('/') || !IsSafeToken(target))
            return new KubernetesPortForwardResult { Error = "A target is required: pod/name, svc/name, deployment/name or statefulset/name." };
        if (!IsSafePorts(ports))
            return new KubernetesPortForwardResult { Error = "The ports must look like 8080:80, 8080:80/udp or a comma-separated list of those." };
        if (!string.IsNullOrWhiteSpace(namespaceName) && !IsSafeToken(namespaceName))
            return new KubernetesPortForwardResult { Error = $"'{namespaceName}' is not a namespace name." };
        if (!ExternalTool.Exists("kubectl"))
            return new KubernetesPortForwardResult { Available = false, Error = "kubectl was not found on PATH." };

        var startInfo = new ProcessStartInfo("kubectl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("port-forward");
        startInfo.ArgumentList.Add(target!);
        startInfo.ArgumentList.Add(ports!);
        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(namespaceName!);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception exception)
        {
            return new KubernetesPortForwardResult { Error = $"kubectl could not be started: {exception.Message}" };
        }

        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 3, 60));
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) break;
            if (output.Any(line => line.Contains("Forwarding from", StringComparison.OrdinalIgnoreCase))) break;
            await Task.Delay(100);
        }

        if (process.HasExited)
        {
            var text = string.Join(Environment.NewLine, output.ToArray());
            process.Dispose();
            return new KubernetesPortForwardStartFailureResult
            {
                Action = "start",
                Target = target,
                Ports = ports,
                Succeeded = false,
                Output = ProcessRunner.Truncate(text, 4_000),
                Error = string.IsNullOrWhiteSpace(text)
                    ? "kubectl port-forward exited before it forwarded anything."
                    : FirstLine(text),
            };
        }

        var sessionId = "pf-" + Guid.NewGuid().ToString("N")[..8];
        var session = new PortForwardSession
        {
            Id = sessionId,
            Target = target!,
            Ports = ports!,
            NamespaceName = namespaceName,
            Process = process,
            Output = output,
            StartedAt = DateTime.UtcNow,
        };

        _portForwards[sessionId] = session;
        return new KubernetesPortForwardStartResult
        {
            Action = "start",
            SessionId = sessionId,
            Target = target,
            Ports = ports,
            NamespaceName = namespaceName,
            LocalPort = FirstLocalPort(ports!),
            Pid = process.Id,
            Forwarding = output.ToArray().ToList(),
            Note = "The forward runs in this server process and ends when it does; k8s_port_forward " +
                   "action=stop ends it earlier. The local port is bound on 127.0.0.1 by kubectl.",
        };
    }

    private static int? FirstLocalPort(string ports)
    {
        var first = ports.Split(',')[0].Split(':')[0].Split('/')[0];
        return int.TryParse(first, out var port) ? port : null;
    }

    private void StopForward(PortForwardSession session)
    {
        try { session.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* already gone */ }

        try { session.Process.Dispose(); }
        catch (InvalidOperationException) { /* nothing to dispose */ }

        _portForwards.TryRemove(session.Id, out _);
    }

    private void PruneFinishedForwards()
    {
        foreach (var session in _portForwards.Values)
            if (!session.Alive)
                _portForwards.TryRemove(session.Id, out _);
    }

    private static bool IsSafePorts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (var part in value.Split(','))
        {
            var segments = part.Split('/');
            if (segments.Length > 2) return false;
            if (segments.Length == 2 && segments[1] is not ("tcp" or "udp" or "sctp")) return false;

            var numbers = segments[0].Split(':');
            if (numbers.Length is < 1 or > 2) return false;
            foreach (var number in numbers)
                if (!int.TryParse(number, out var port) || port is < 1 or > 65_535)
                    return false;
        }

        return true;
    }

    // ── parsing ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Reduces the JSON of <c>kubectl get -o json</c> (a list, a single object, or a List) to
    /// rows: identity, phase or readiness, restart count and age. Unknown shapes are skipped.
    /// </summary>
    public static IReadOnlyList<KubernetesResourceSummary> SummarizeItems(string json)
    {
        var rows = new List<KubernetesResourceSummary>();
        if (string.IsNullOrWhiteSpace(json)) return rows;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray()) AddRow(rows, item);
            }
            else
            {
                AddRow(rows, root);
            }
        }
        catch (JsonException)
        {
            // kubectl printed a warning instead of a document: the caller reports the raw text.
        }

        return rows;
    }

    /// <summary>The <c>gitVersion</c> under <paramref name="section"/> in <c>kubectl version -o json</c>.</summary>
    public static string? ReadVersion(string json, string section)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(section, out var element) &&
                   element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty("gitVersion", out var version)
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddRow(List<KubernetesResourceSummary> rows, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        var metadata = item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta : (JsonElement?)null;
        var status = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object ? st : (JsonElement?)null;
        var spec = item.TryGetProperty("spec", out var sp) && sp.ValueKind == JsonValueKind.Object ? sp : (JsonElement?)null;

        var restarts = 0;
        var readyContainers = 0;
        var hasContainers = false;
        if (status is { } s && s.TryGetProperty("containerStatuses", out var containers) &&
            containers.ValueKind == JsonValueKind.Array)
        {
            foreach (var container in containers.EnumerateArray())
            {
                hasContainers = true;
                if (container.TryGetProperty("restartCount", out var restart) && restart.TryGetInt32(out var count))
                    restarts += count;
                if (container.TryGetProperty("ready", out var isReady) && isReady.ValueKind == JsonValueKind.True)
                    readyContainers++;
            }
        }

        int? desired = spec is { } sp2 && sp2.TryGetProperty("replicas", out var replicas) && replicas.TryGetInt32(out var d)
            ? d
            : (int?)null;

        var phase = status is { } s2 && s2.TryGetProperty("phase", out var p) ? p.GetString() : null;
        int? readyReplicas = status is { } s3 && s3.TryGetProperty("readyReplicas", out var r) && r.TryGetInt32(out var rr)
            ? rr
            : (int?)null;

        var state = phase;
        if (desired is not null)
            state = $"{readyReplicas ?? 0}/{desired.Value} ready";

        rows.Add(new KubernetesResourceSummary(
            Kind: Str(item, "kind"),
            Name: metadata is { } m ? Str(m, "name") : null,
            Namespace: metadata is { } m2 ? Str(m2, "namespace") : null,
            Status: state,
            Restarts: restarts == 0 ? null : restarts,
            Ready: desired is null && hasContainers ? readyContainers : null,
            Desired: desired,
            CreatedAt: metadata is { } m3 ? Str(m3, "creationTimestamp") : null));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static async Task<ToolRun> KubectlAsync(string args, int timeoutSeconds)
    {
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ExternalTool.RunAsync("kubectl", args, Directory.GetCurrentDirectory(), timeoutSeconds);
        return new ToolRun(exitCode, timedOut, stdout, stderr, elapsed);
    }

    private static int Clamp(int timeoutSeconds) => Math.Clamp(timeoutSeconds, 5, 900);

    private static string NamespaceArgs(string? namespaceName, bool allNamespaces)
    {
        if (allNamespaces) return " --all-namespaces";
        return string.IsNullOrWhiteSpace(namespaceName) ? string.Empty : $" -n {Quote(namespaceName!)}";
    }

    /// <summary>A kind list such as <c>pods,deployments</c>: no flags, no quotes.</summary>
    private static bool IsSafeKind(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.TrimStart().StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ',' or ':');

    /// <summary>A name, namespace, context or container: no flags, no quotes, no shell metacharacters.</summary>
    private static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.TrimStart().StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@' or '+');

    private static bool IsSafeSelector(string value) =>
        !value.TrimStart().StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ',' or '!' or '(' or ')' or ' ');

    private static bool IsSafeOutput(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.All(c => c is not ('\r' or '\n' or '\0'));

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>How many YAML documents a rendered stream holds (the '---' separators, plus one).</summary>
    public static int CountDocuments(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return 0;
        var separators = yaml.Split('\n').Count(line => line.TrimEnd('\r').Trim() == "---");
        return separators + 1;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private sealed record ToolRun(int ExitCode, bool TimedOut, string Stdout, string Stderr, TimeSpan Elapsed)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
        public string Text => (Stdout + "\n" + Stderr).Trim();
    }
}
