namespace Snitch.Kubernetes;

/// <summary>One finding of the offline manifest validation.</summary>
public sealed record ManifestFinding(string RuleId, string Severity, string Message, int Document, string Path, string? Fix);

/// <summary>One document a manifest file holds: its identity and the paths the scan read.</summary>
public sealed record ManifestDocument(
    int Index, string? ApiVersion, string? Kind, string? Name, string? NamespaceName, string? File);

/// <summary>
/// Offline validation of Kubernetes manifests: the required fields of the built-in kinds, the
/// selector/template agreement a Deployment needs, the image tag a container should pin, a name that is not
/// a DNS label and a duplicate resource in the same input. It reads the YAML itself, so it needs neither a
/// cluster nor kubectl.
/// <para>
/// It is a table of the built-in kinds, not the Kubernetes OpenAPI schema: a custom resource is reported as
/// having no built-in schema rather than as invalid, a field the table does not name is not checked, and
/// the server-side validation (<c>k8s_apply</c> with <c>dryRun=true</c>) remains the authority for an
/// admission controller's rules.
/// </para>
/// </summary>
public static class KubernetesManifestValidator
{
    private static readonly Dictionary<string, string> Rules = new(StringComparer.Ordinal)
    {
        ["K8S001"] = "A document has no apiVersion",
        ["K8S002"] = "A document has no kind",
        ["K8S003"] = "A document has no metadata.name",
        ["K8S004"] = "A kind with no built-in schema is reported, not invalid",
        ["K8S005"] = "A pod template has no containers",
        ["K8S006"] = "A container has no image",
        ["K8S007"] = "A container has no name",
        ["K8S008"] = "A workload with replicas has no spec.selector",
        ["K8S009"] = "The selector does not match the pod template's labels",
        ["K8S010"] = "A Service has no spec.ports",
        ["K8S011"] = "A port has no port number",
        ["K8S012"] = "A CronJob has no spec.schedule",
        ["K8S013"] = "A HorizontalPodAutoscaler has no spec.scaleTargetRef",
        ["K8S014"] = "minReplicas is greater than maxReplicas",
        ["K8S015"] = "A PersistentVolumeClaim has no storage request",
        ["K8S016"] = "A container image is not pinned (no tag or digest, or :latest)",
        ["K8S017"] = "The same kind/namespace/name appears twice in the input",
        ["K8S018"] = "A name is not a DNS-1123 label",
        ["K8S019"] = "A cluster-scoped kind carries a metadata.namespace",
        ["K8S020"] = "A Service has neither selector nor an ExternalName",
        ["K8S021"] = "A ConfigMap has neither data nor binaryData",
        ["K8S022"] = "A Secret has neither data nor stringData",
        ["K8S023"] = "A volume references a Secret or ConfigMap that is not in the input",
    };

    /// <summary>The rule catalogue: id to what the rule proves.</summary>
    public static IReadOnlyDictionary<string, string> Catalogue => Rules;

    public static object Validate(string yaml, string? source)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new { source, documentCount = 0, findings = Array.Empty<ManifestFinding>(), error = "The manifest is empty." };

        var documents = ReadDocuments(yaml);
        var findings = new List<ManifestFinding>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var configMaps = new HashSet<string>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new List<(int Document, string Namespace, string Kind, string Name)>();

        var summaries = new List<ManifestDocument>();

        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var apiVersion = document.Get("apiVersion");
            var kind = document.Get("kind");
            var name = document.Get("metadata.name");
            var namespaceName = document.Get("metadata.namespace");

            summaries.Add(new ManifestDocument(index, apiVersion, kind, name, namespaceName, source));

            if (apiVersion is null)
                findings.Add(Find("K8S001", "error", index, "apiVersion", "The document has no apiVersion.",
                    "Add 'apiVersion: v1' or 'apps/v1' … to the top level."));
            if (kind is null)
                findings.Add(Find("K8S002", "error", index, "kind", "The document has no kind.",
                    "Add the resource kind, e.g. 'kind: Deployment'."));
            if (name is null)
                findings.Add(Find("K8S003", "error", index, "metadata.name", "The resource has no metadata.name.",
                "Name the resource; kubectl cannot create an object without one."));

            if (name is not null && !IsDnsLabel(name))
                findings.Add(Find("K8S018", "warning", index, "metadata.name",
                    $"'{name}' is not a DNS-1123 label (lowercase letters, digits and '-', at most 253 characters).",
                    "Rename the resource; the API server rejects an invalid name."));

            if (kind is null) continue;

            // Identity: kind/namespace/name, which is what makes a duplicate a real conflict.
            if (name is not null &&
                !identities.Add($"{kind}/{namespaceName ?? "default"}/{name}"))
                findings.Add(Find("K8S017", "warning", index, $"metadata.name",
                    $"{kind} '{name}' appears more than once in this input.",
                    "Keep one definition; 'kubectl apply -f' applies the last one silently."));

            if (IsClusterScoped(kind) && namespaceName is not null)
                findings.Add(Find("K8S019", "warning", index, "metadata.namespace",
                    $"{kind} is cluster-scoped, so metadata.namespace has no effect.",
                    "Remove metadata.namespace."));

            if (kind == "ConfigMap") configMaps.Add($"{namespaceName ?? "default"}/{name}");
            if (kind == "Secret") secrets.Add($"{namespaceName ?? "default"}/{name}");

            ValidateKind(document, index, kind, name, namespaceName, findings, volumes);
        }

        foreach (var (index, ns, kind, name) in volumes)
        {
            if (kind == "ConfigMap" && !configMaps.Contains($"{ns}/{name}") && configMaps.Count > 0)
                findings.Add(Find("K8S023", "info", index, "spec.volumes",
                    $"The volume names ConfigMap '{name}', which is not in this input.",
                    "Define it in the same file, or ignore this if it is created elsewhere."));
            if (kind == "Secret" && !secrets.Contains($"{ns}/{name}") && secrets.Count > 0)
                findings.Add(Find("K8S023", "info", index, "spec.volumes",
                    $"The volume names Secret '{name}', which is not in this input.",
                    "Define it in the same file, or ignore this if it is created elsewhere."));
        }

        return new
        {
            source,
            documentCount = documents.Count,
            counts = new
            {
                error = findings.Count(finding => finding.Severity == "error"),
                warning = findings.Count(finding => finding.Severity == "warning"),
                info = findings.Count(finding => finding.Severity == "info"),
            },
            byRule = findings.GroupBy(finding => finding.RuleId)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            documents = summaries,
            findings = findings.OrderBy(finding => finding.Document)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToList(),
            note = "A table of the built-in kinds, not the Kubernetes OpenAPI schema: a custom resource " +
                   "names no schema here, and a field the table does not name is not checked. " +
                   "k8s_apply with dryRun=true is the server-side authority.",
        };
    }

    private static void ValidateKind(
        ManifestPaths document, int index, string kind, string? name, string? namespaceName,
        List<ManifestFinding> findings, List<(int Document, string Namespace, string Kind, string Name)> volumes)
    {
        switch (kind)
        {
            case "Pod":
                ValidatePodSpec(document, index, "spec", findings);
                CollectVolumes(document, index, "spec", namespaceName, volumes);
                break;

            case "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "Job":
            {
                ValidatePodSpec(document, index, "spec.template.spec", findings);
                CollectVolumes(document, index, "spec.template.spec", namespaceName, volumes);
                if (kind is not ("Job" or "DaemonSet"))
                {
                    if (document.Get("spec.selector.matchLabels") is null &&
                        document.Get("spec.selector.matchExpressions") is null)
                        findings.Add(Find("K8S008", "error", index, "spec.selector",
                            $"{kind} has no spec.selector.matchLabels.",
                            "Add a selector that matches the pod template's labels."));
                    else
                        CheckSelectorMatchesTemplate(document, index, kind, findings);
                }

                break;
            }

            case "CronJob":
                if (document.Get("spec.schedule") is null)
                    findings.Add(Find("K8S012", "error", index, "spec.schedule",
                        "CronJob has no spec.schedule.", "Add a cron expression, e.g. '0 3 * * *'."));
                ValidatePodSpec(document, index, "spec.jobTemplate.spec.template.spec", findings);
                break;

            case "Service":
                ValidateService(document, index, findings);
                break;

            case "HorizontalPodAutoscaler":
            {
                if (document.Get("spec.scaleTargetRef.name") is null)
                    findings.Add(Find("K8S013", "error", index, "spec.scaleTargetRef",
                        "The autoscaler has no spec.scaleTargetRef.",
                        "Name the workload to scale (apiVersion, kind, name)."));
                var minimum = document.Get("spec.minReplicas");
                var maximum = document.Get("spec.maxReplicas");
                if (int.TryParse(minimum, out var min) && int.TryParse(maximum, out var max) && min > max)
                    findings.Add(Find("K8S014", "error", index, "spec.minReplicas",
                        $"minReplicas ({min}) is greater than maxReplicas ({max}).",
                        "Make the minimum smaller than or equal to the maximum."));
                break;
            }

            case "PersistentVolumeClaim":
                if (document.Get("spec.resources.requests.storage") is null)
                    findings.Add(Find("K8S015", "error", index, "spec.resources.requests.storage",
                        "The claim asks for no storage.", "Add 'resources: requests: storage: 1Gi'."));
                break;

            case "ConfigMap":
                if (document.Get("data") is null && document.Get("binaryData") is null)
                    findings.Add(Find("K8S021", "info", index, "data",
                        "The ConfigMap carries no data.", "Add the keys it should expose, or remove it."));
                break;

            case "Secret":
                if (document.Get("data") is null && document.Get("stringData") is null)
                    findings.Add(Find("K8S022", "info", index, "data",
                        "The Secret carries no data.", "Add data or stringData."));
                break;

            // Built-in kinds this table has no field rule for: their identity is checked and their
            // fields are not, which is not the same as "no schema".
            case "Ingress" or "IngressClass" or "Namespace" or "ServiceAccount" or "Role" or "RoleBinding"
                or "ClusterRole" or "ClusterRoleBinding" or "NetworkPolicy" or "PodDisruptionBudget"
                or "StorageClass" or "PriorityClass" or "LimitRange" or "ResourceQuota" or "Endpoints"
                or "EndpointSlice" or "PersistentVolume" or "Node" or "CustomResourceDefinition"
                or "ValidatingWebhookConfiguration" or "MutatingWebhookConfiguration" or "APIService":
                break;

            default:
                findings.Add(Find("K8S004", "info", index, "kind",
                    $"'{kind}' has no built-in schema in this validator; its fields are not checked.",
                    "A CustomResourceDefinition kind is validated by its own schema on the API server."));
                break;
        }

        _ = name;
    }

    private static void ValidatePodSpec(ManifestPaths document, int index, string prefix, List<ManifestFinding> findings)
    {
        var containers = document.ListPaths(prefix + ".containers");
        if (containers.Count == 0)
        {
            findings.Add(Find("K8S005", "error", index, prefix + ".containers",
                "The pod template has no containers.", "Add at least one container with a name and an image."));
            return;
        }

        foreach (var container in containers)
        {
            var name = document.Get(container + ".name");
            var image = document.Get(container + ".image");
            if (name is null)
                findings.Add(Find("K8S007", "error", index, container,
                    "A container has no name.", "Name every container; the name is how logs and exec target it."));
            if (image is null)
                findings.Add(Find("K8S006", "error", index, container,
                    "A container has no image.", "Set 'image: repository/name:tag'."));
            else if (!IsPinned(image))
                findings.Add(Find("K8S016", "warning", index, container + ".image",
                    $"The image '{image}' is not pinned to a version (no tag or digest, or ':latest').",
                    "Pin a version tag or a digest so a rollout is reproducible."));
        }
    }

    private static void CheckSelectorMatchesTemplate(ManifestPaths document, int index, string kind,
        List<ManifestFinding> findings)
    {
        var labels = document.PathsWithPrefix("spec.selector.matchLabels.")
            .ToDictionary(path => path["spec.selector.matchLabels.".Length..], path => document.Get(path)!);

        foreach (var (key, value) in labels)
        {
            var template = document.Get("spec.template.metadata.labels." + key);
            if (template is null)
                findings.Add(Find("K8S009", "error", index, "spec.template.metadata.labels." + key,
                    $"The selector requires label '{key}', which the pod template does not set.",
                    "Add the label to the template, or remove it from the selector."));
            else if (!string.Equals(template, value, StringComparison.Ordinal))
                findings.Add(Find("K8S009", "error", index, "spec.template.metadata.labels." + key,
                    $"The selector expects {key}={value}, the template has {key}={template}.",
                    "Make the template's labels match the selector."));
        }

        _ = kind;
    }

    private static void ValidateService(ManifestPaths document, int index, List<ManifestFinding> findings)
    {
        var type = document.Get("spec.type");
        if (string.Equals(type, "ExternalName", StringComparison.Ordinal))
            return;

        var ports = document.ListPaths("spec.ports");
        if (ports.Count == 0)
            findings.Add(Find("K8S010", "warning", index, "spec.ports",
                "The Service exposes no ports.", "Add 'ports: - port: 80'."));
        else
            foreach (var port in ports)
                if (document.Get(port + ".port") is null)
                    findings.Add(Find("K8S011", "error", index, port,
                        "A Service port has no port number.", "Add 'port: <number>'."));

        if (document.Get("spec.selector") is null)
            findings.Add(Find("K8S020", "warning", index, "spec.selector",
                "The Service has no selector, so it selects no pods.",
                "Add a selector that matches the pods it should route to, or use an ExternalName."));
    }

    private static void CollectVolumes(
        ManifestPaths document, int index, string prefix, string? namespaceName,
        List<(int Document, string Namespace, string Kind, string Name)> volumes)
    {
        foreach (var volume in document.ListPaths(prefix + ".volumes"))
        {
            var configMap = document.Get(volume + ".configMap.name");
            if (configMap is not null) volumes.Add((index, namespaceName ?? "default", "ConfigMap", configMap));
            var secret = document.Get(volume + ".secret.secretName");
            if (secret is not null) volumes.Add((index, namespaceName ?? "default", "Secret", secret));
        }
    }

    // ── the identity that decides the built-in validation ───────────────────────────

    private static bool IsClusterScoped(string kind) =>
        kind is "Namespace" or "Node" or "PersistentVolume" or "ClusterRole" or "ClusterRoleBinding"
            or "CustomResourceDefinition" or "StorageClass" or "PriorityClass" or "IngressClass"
            or "ValidatingWebhookConfiguration" or "MutatingWebhookConfiguration" or "APIService";

    private static bool IsDnsLabel(string name) =>
        name.Length <= 253 &&
        name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) ||
                              character is '-' or '.');

    private static bool IsPinned(string image)
    {
        var withoutDigest = image.Split('@')[0];
        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');
        if (image.Contains('@')) return true;
        if (lastColon <= lastSlash) return false;
        return withoutDigest[(lastColon + 1)..] is not ("latest" or "");
    }

    private static ManifestFinding Find(string rule, string severity, int document, string path, string message,
        string? fix) => new(rule, severity, message, document, path, fix);

    // ── a small YAML path reader ────────────────────────────────────────────────────

    /// <summary>
    /// The paths a YAML document holds, as <c>spec.template.spec.containers[0].image</c> → value. It reads
    /// the block-mapping and block-sequence forms a manifest is written in; flow collections, anchors,
    /// aliases and a block scalar's content are not interpreted (and the scalar is skipped so its text is
    /// never read as a path).
    /// </summary>
    private sealed class ManifestPaths
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _listItems = new(StringComparer.Ordinal);

        /// <summary>
        /// The value of a path, or an empty string when the path is a mapping that holds only children (a
        /// key with nothing after its colon), or null when the path is absent.
        /// </summary>
        public string? Get(string path)
        {
            if (_values.TryGetValue(path, out var value)) return value;
            return _keys.Contains(path) ? string.Empty : null;
        }

        public IReadOnlyList<string> ListPaths(string prefix) =>
            _listItems.TryGetValue(prefix, out var items) ? items : Array.Empty<string>();

        public IEnumerable<string> PathsWithPrefix(string prefix) =>
            _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal));

        public static ManifestPaths Read(IReadOnlyList<string> lines)
        {
            var paths = new ManifestPaths();
            var stack = new List<(int Indent, string Path)>();
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            var blockScalarIndent = -1;

            foreach (var raw in lines)
            {
                var line = StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var indent = line.Length - line.TrimStart().Length;
                if (blockScalarIndent >= 0)
                {
                    if (indent > blockScalarIndent) continue;
                    blockScalarIndent = -1;
                }

                var trimmed = line.TrimStart();

                if (trimmed.StartsWith('-'))
                {
                    while (stack.Count > 0 &&
                           (stack[^1].Indent > indent ||
                            (stack[^1].Indent == indent && stack[^1].Path.EndsWith(']'))))
                        stack.RemoveAt(stack.Count - 1);

                    var parent = stack.Count == 0 ? string.Empty : stack[^1].Path;
                    var index = counters.TryGetValue(parent, out var seen) ? seen : 0;
                    counters[parent] = index + 1;
                    var itemPath = $"{parent}[{index}]";

                    if (!paths._listItems.TryGetValue(parent, out var items))
                    {
                        items = new List<string>();
                        paths._listItems[parent] = items;
                    }

                    items.Add(itemPath);

                    var rest = trimmed[1..].Trim();
                    if (rest.Length == 0)
                    {
                        stack.Add((indent, itemPath));
                        continue;
                    }

                    if (TryKeyValue(rest, out var itemKey, out var itemValue))
                    {
                        paths._values[itemPath + "." + itemKey] = itemValue;
                        stack.Add((indent, itemPath));
                    }
                    else
                    {
                        paths._values[itemPath] = rest;
                    }

                    continue;
                }

                if (!TryKeyValue(trimmed, out var key, out var value)) continue;
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);

                var parentPath = stack.Count == 0 ? string.Empty : stack[^1].Path;
                var path = parentPath.Length == 0 ? key : parentPath + "." + key;

                if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
                {
                    blockScalarIndent = indent;
                    paths._values[path] = string.Empty;
                    paths._keys.Add(path);
                    continue;
                }

                if (value.Length == 0)
                {
                    paths._keys.Add(path);
                    stack.Add((indent, path));
                    continue;
                }

                paths._values[path] = value;
                paths._keys.Add(path);
            }

            return paths;
        }

        private static bool TryKeyValue(string text, out string key, out string value)
        {
            key = value = string.Empty;
            var colon = text.IndexOf(':');
            if (colon <= 0) return false;

            key = text[..colon].Trim();
            if (key.Length == 0 || key.Contains(' ')) return false;

            value = text[(colon + 1)..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                value = value[1..^1];

            return true;
        }

        private static string StripComment(string line)
        {
            var quote = '\0';
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (quote != '\0')
                {
                    if (character == quote) quote = '\0';
                    continue;
                }

                if (character is '"' or '\'') { quote = character; continue; }
                if (character == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1])))
                    return line[..index];
            }

            return line;
        }
    }

    /// <summary>The documents of a multi-document YAML stream, each read into its paths.</summary>
    private static List<ManifestPaths> ReadDocuments(string yaml)
    {
        var documents = new List<ManifestPaths>();
        var buffer = new List<string>();

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimEnd() is "---" or "...")
            {
                if (buffer.Count > 0)
                {
                    documents.Add(ManifestPaths.Read(buffer));
                    buffer.Clear();
                }

                continue;
            }

            buffer.Add(raw);
        }

        if (buffer.Count > 0) documents.Add(ManifestPaths.Read(buffer));

        // A stream that is only separators holds no document.
        return documents.Where(document => document.PathsWithPrefix(string.Empty).Any()).ToList();
    }
}
