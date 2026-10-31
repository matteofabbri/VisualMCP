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
    Task<object> StatusAsync(int timeoutSeconds);

    Task<object> ContextAsync(string action, string? name, int timeoutSeconds);
    Task<object> GetAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        string? selector, string? output, int timeoutSeconds);
    Task<object> DescribeAsync(string kind, string? name, string? namespaceName, bool allNamespaces, int timeoutSeconds);
    Task<object> LogsAsync(string pod, string? container, string? namespaceName, int tail, string? since,
        bool previous, int timeoutSeconds);
    Task<object> ApplyAsync(string? file, string? content, string? namespaceName, bool dryRun, bool confirm,
        int timeoutSeconds);
    Task<object> DeleteAsync(string kind, string name, string? namespaceName, bool ignoreNotFound, bool confirm,
        int timeoutSeconds);
    Task<object> RolloutAsync(string action, string kind, string name, string? namespaceName, int? toRevision,
        bool confirm, int timeoutSeconds);
    Task<object> HelmAsync(string action, string? release, string? chart, string? namespaceName,
        string? valuesFile, string[]? setValues, int timeoutSeconds);
}

/// <summary>One row of a <c>kubectl get</c>, reduced to what makes a list readable.</summary>
public sealed record KubernetesResourceSummary(
    string? Kind, string? Name, string? Namespace, string? Status, int? Restarts, int? Ready, int? Desired,
    string? CreatedAt);

public sealed class KubernetesService : IKubernetesService
{
    private static readonly string[] RolloutActions = ["status", "restart", "undo"];
    private static readonly string[] HelmActions = ["list", "status", "history", "get-values", "template", "repo-list"];

    // ── status ───────────────────────────────────────────────────────────────
    public async Task<object> StatusAsync(int timeoutSeconds)
    {
        if (!ExternalTool.Exists("kubectl"))
            return new
            {
                available = false,
                error = "kubectl was not found on PATH. Install it (https://kubernetes.io/docs/tasks/tools/) " +
                        "and let it see a kubeconfig; helm is optional and reported separately.",
                helmVersion = await ExternalTool.VersionAsync("helm", "version --short"),
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

        return new
        {
            available = true,
            clientVersion,
            serverVersion,
            clusterReachable = serverVersion is not null,
            currentContext = context.Succeeded ? FirstLine(context.Stdout) : null,
            contextError = context.Succeeded ? null : FirstLine(context.Stderr),
            kubeconfig,
            kubeconfigExists = kubeconfig is not null && File.Exists(kubeconfig.Split(Path.PathSeparator)[0]),
            helmVersion = helm,
            reason = serverVersion is null
                ? (string.IsNullOrWhiteSpace(server.Stderr)
                    ? "The cluster did not answer: no current context is set, or the API server is unreachable."
                    : FirstLine(server.Stderr))
                : null,
        };
    }

    // ── contexts ─────────────────────────────────────────────────────────────
    public async Task<object> ContextAsync(string action, string? name, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        switch (verb)
        {
            case "current":
            {
                var run = await KubectlAsync("config current-context", Clamp(timeoutSeconds));
                return new
                {
                    action = verb,
                    succeeded = run.Succeeded,
                    context = run.Succeeded ? FirstLine(run.Stdout) : null,
                    error = run.Succeeded ? null : FirstLine(run.Stderr),
                    hint = run.Succeeded
                        ? null
                        : "No context is selected. 'kubectl config get-contexts' lists the available ones.",
                };
            }
            case "list":
            {
                var run = await KubectlAsync("config get-contexts", Clamp(timeoutSeconds));
                return new
                {
                    action = verb,
                    succeeded = run.Succeeded,
                    output = ProcessRunner.Truncate(run.Text, 12_000),
                    error = run.Succeeded ? null : FirstLine(run.Stderr),
                };
            }
            case "use":
            {
                if (!IsSafeToken(name))
                    return new { error = "The use action needs a context name." };
                var run = await KubectlAsync($"config use-context {Quote(name!)}", Clamp(timeoutSeconds));
                return new
                {
                    action = verb,
                    context = name,
                    succeeded = run.Succeeded,
                    output = ProcessRunner.Truncate(run.Text, 4_000),
                    error = run.Succeeded ? null : FirstLine(run.Stderr),
                };
            }
            default:
                return new { error = $"Unknown action '{action}'. Use one of: current, list, use." };
        }
    }

    // ── get / describe ───────────────────────────────────────────────────────
    public async Task<object> GetAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        string? selector, string? output, int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new { error = "A resource kind is required, e.g. pods, deployments, services (comma-separated kinds are allowed)." };
        if (!string.IsNullOrWhiteSpace(name) && !IsSafeToken(name)) return new { error = $"'{name}' is not a resource name." };
        if (!string.IsNullOrWhiteSpace(selector) && !IsSafeSelector(selector!))
            return new { error = "The selector must be a label selector such as 'app=api,tier=web'." };
        if (!IsSafeOutput(output)) return new { error = $"'{output}' is not an output format." };

        var format = string.IsNullOrWhiteSpace(output) ? "summary" : output!.Trim();
        var args = $"get {kind}";
        if (!string.IsNullOrWhiteSpace(name)) args += $" {Quote(name!)}";
        args += format == "summary" ? " -o json" : $" -o {Quote(format)}";
        args += NamespaceArgs(namespaceName, allNamespaces);
        if (!string.IsNullOrWhiteSpace(selector)) args += $" --selector {Quote(selector!)}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        if (!run.Succeeded)
            return new
            {
                error = $"kubectl get failed: {FirstLine(run.Stderr)}",
                command = $"kubectl {args}",
                output = ProcessRunner.Truncate(run.Text, 8_000),
            };

        if (format != "summary")
            return new
            {
                kind,
                name,
                namespaceName,
                allNamespaces,
                format,
                command = $"kubectl {args}",
                raw = ProcessRunner.Truncate(run.Stdout, 32_000),
            };

        var items = SummarizeItems(run.Stdout);
        return new
        {
            kind,
            name,
            namespaceName,
            allNamespaces,
            selector,
            count = items.Count,
            items,
            command = $"kubectl {args}",
            note = "Summary of 'kubectl get -o json'; pass output=yaml|json|wide|name|jsonpath=… for the client's own view.",
        };
    }

    public async Task<object> DescribeAsync(string kind, string? name, string? namespaceName, bool allNamespaces,
        int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new { error = "A resource kind is required, e.g. pods, deployments." };
        if (!string.IsNullOrWhiteSpace(name) && !IsSafeToken(name)) return new { error = $"'{name}' is not a resource name." };

        var args = $"describe {kind}";
        if (!string.IsNullOrWhiteSpace(name)) args += $" {Quote(name!)}";
        args += NamespaceArgs(namespaceName, allNamespaces);

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new
        {
            kind,
            name,
            succeeded = run.Succeeded,
            command = $"kubectl {args}",
            output = ProcessRunner.Truncate(run.Text, 32_000),
            error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    // ── logs ─────────────────────────────────────────────────────────────────
    public async Task<object> LogsAsync(string pod, string? container, string? namespaceName, int tail, string? since,
        bool previous, int timeoutSeconds)
    {
        if (!IsSafeToken(pod)) return new { error = "A pod name is required." };
        if (!string.IsNullOrWhiteSpace(container) && !IsSafeToken(container)) return new { error = $"'{container}' is not a container name." };
        if (!string.IsNullOrWhiteSpace(since) && !IsSafeToken(since)) return new { error = "since must look like '10m' or '2026-10-31T10:00:00Z'." };

        var args = $"logs {Quote(pod)}";
        if (!string.IsNullOrWhiteSpace(container)) args += $" -c {Quote(container!)}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        args += $" --tail {Math.Clamp(tail, 1, 10_000)}";
        if (!string.IsNullOrWhiteSpace(since)) args += $" --since {Quote(since!)}";
        if (previous) args += " --previous";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        var text = (run.Stdout + "\n" + run.Stderr).Trim();
        return new
        {
            pod,
            container,
            namespaceName,
            tail,
            since,
            previous,
            empty = string.IsNullOrWhiteSpace(text),
            logs = ProcessRunner.Truncate(text, 64_000),
            command = $"kubectl {args}",
            error = run.Succeeded || !string.IsNullOrWhiteSpace(run.Stdout) ? null : FirstLine(run.Stderr),
        };
    }

    // ── apply / delete / rollout (the state-changing half) ───────────────────
    public async Task<object> ApplyAsync(string? file, string? content, string? namespaceName, bool dryRun,
        bool confirm, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(file) == string.IsNullOrWhiteSpace(content))
            return new { error = "Pass exactly one of file (a manifest on disk) or content (the manifest itself)." };

        if (!string.IsNullOrWhiteSpace(file) && !File.Exists(file))
            return new { error = $"Manifest not found: {file}" };

        if (!dryRun && !confirm)
            return new
            {
                error = "Apply changes the cluster. Pass confirm=true, or keep dryRun=true for a server-side validation.",
                dryRun,
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
            return new
            {
                file = temporary is null ? file : null,
                namespaceName,
                dryRun,
                confirmed = confirm,
                succeeded = run.Succeeded,
                command = $"kubectl {args}",
                output = ProcessRunner.Truncate(run.Text, 12_000),
                error = run.Succeeded ? null : FirstLine(run.Stderr),
            };
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* best-effort */ }
        }
    }

    public async Task<object> DeleteAsync(string kind, string name, string? namespaceName, bool ignoreNotFound,
        bool confirm, int timeoutSeconds)
    {
        if (!IsSafeKind(kind)) return new { error = "A resource kind is required, e.g. pods, deployments." };
        if (!IsSafeToken(name)) return new { error = "A resource name is required." };
        if (!confirm)
            return new { error = "Delete removes cluster state that cannot be recovered. Pass confirm=true to proceed." };

        var args = $"delete {kind} {Quote(name)}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (ignoreNotFound) args += " --ignore-not-found";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new
        {
            kind,
            name,
            namespaceName,
            deleted = run.Succeeded,
            command = $"kubectl {args}",
            output = ProcessRunner.Truncate(run.Text, 4_000),
            error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    public async Task<object> RolloutAsync(string action, string kind, string name, string? namespaceName,
        int? toRevision, bool confirm, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!RolloutActions.Contains(verb))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", RolloutActions)}." };
        if (!IsSafeToken(kind)) return new { error = "A workload kind is required, e.g. deployment, statefulset, daemonset." };
        if (!IsSafeToken(name)) return new { error = "A workload name is required." };
        if (verb is "restart" or "undo" && !confirm)
            return new { error = $"{verb} changes the cluster (it rolls the pods). Pass confirm=true to proceed." };

        var args = $"rollout {verb} {kind}/{name}";
        args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (verb == "undo" && toRevision is > 0) args += $" --to-revision {toRevision.Value}";

        var run = await KubectlAsync(args, Clamp(timeoutSeconds));
        return new
        {
            action = verb,
            kind,
            name,
            namespaceName,
            toRevision,
            succeeded = run.Succeeded,
            command = $"kubectl {args}",
            output = ProcessRunner.Truncate(run.Text, 8_000),
            error = run.Succeeded ? null : FirstLine(run.Stderr),
        };
    }

    // ── helm ─────────────────────────────────────────────────────────────────
    public async Task<object> HelmAsync(string action, string? release, string? chart, string? namespaceName,
        string? valuesFile, string[]? setValues, int timeoutSeconds)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!HelmActions.Contains(verb))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", HelmActions)}." };

        if (!ExternalTool.Exists("helm"))
            return new
            {
                available = false,
                error = "helm was not found on PATH. Install it (https://helm.sh/docs/intro/install/) for release " +
                        "listing, status and history; kubectl covers the raw resources without it.",
            };

        if (verb is "status" or "history" or "get-values" or "template" && !IsSafeToken(release))
            return new { error = $"The {verb} action needs a release name." };
        if (verb == "template" && !IsSafeToken(chart))
            return new { error = "The template action needs a chart (a local path or a repo/chart reference)." };

        var args = verb switch
        {
            "list" => "list",
            "repo-list" => "repo list",
            "status" => $"status {Quote(release!)}",
            "history" => $"history {Quote(release!)}",
            "get-values" => $"get values {Quote(release!)}",
            _ => $"template {Quote(release!)} {Quote(chart!)}",
        };

        if (verb is "list" or "status" or "history" or "get-values")
            args += NamespaceArgs(namespaceName, allNamespaces: false);
        if (verb is "list" or "history") args += " -o json";
        if (!string.IsNullOrWhiteSpace(valuesFile))
        {
            if (!File.Exists(valuesFile)) return new { error = $"Values file not found: {valuesFile}" };
            args += $" -f {Quote(valuesFile!)}";
        }
        foreach (var value in setValues ?? [])
        {
            if (!IsSafeToken(value) || !value.Contains('='))
                return new { error = $"'{value}' is not a --set assignment (key=value)." };
            args += $" --set {Quote(value)}";
        }

        var run = await ExternalTool.RunAsync("helm", args, Directory.GetCurrentDirectory(), Clamp(timeoutSeconds));
        return new
        {
            action = verb,
            release,
            chart,
            namespaceName,
            succeeded = !run.timedOut && run.exitCode == 0,
            command = $"helm {args}",
            raw = ProcessRunner.Truncate(run.stdout, 32_000),
            error = run.timedOut || run.exitCode != 0 ? FirstLine(run.stderr) : null,
        };
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
