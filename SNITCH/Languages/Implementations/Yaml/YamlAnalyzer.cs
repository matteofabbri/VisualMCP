using System.Text.RegularExpressions;

namespace Snitch.Languages.Yaml;

/// <summary>
/// YAML as a scanned language: this is an indentation-aware line walk, not a full YAML parser. It
/// handles what a manifest in a repository actually contains — block mappings, sequence items, block
/// scalars, comments, anchors and aliases — and reports the mistakes that change the meaning of a file
/// silently: a tab in the indentation, a key declared twice, a colon glued to its key, the YAML 1.1
/// booleans, an alias with no anchor, an unclosed flow collection. On top of the line rules it reads the
/// identity of a Kubernetes manifest — kind, apiVersion, metadata.name — and the containers it declares,
/// which is what `yaml_files` shows and what YAML0009–YAML0015 judge: an identity with one half missing,
/// an unnamed object, a container with no image, two containers sharing a name, and on a GitHub Actions
/// workflow or a compose file, a job or service with nothing to run and a workflow with no trigger. The
/// judgement is still structural: no schema is fetched and no chart is rendered.
/// </summary>
public static class YamlAnalyzer
{
    private static readonly RegexOptions Options = RegexOptions.Compiled;

    private static readonly string[] YamlExtensions = [".yml", ".yaml"];

    public static bool IsYaml(string path) =>
        YamlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["YAML0001"] = "Tab in the indentation (YAML forbids it)",
        ["YAML0002"] = "The same key is declared twice in one mapping",
        ["YAML0003"] = "Trailing whitespace",
        ["YAML0004"] = "Unquoted YAML 1.1 boolean (yes/no/on/off)",
        ["YAML0005"] = "A colon glued to its key (key:value)",
        ["YAML0006"] = "A value containing ': ' outside quotes (it parses as a nested mapping)",
        ["YAML0007"] = "Unbalanced flow collection ([ ] or { })",
        ["YAML0008"] = "An alias (*name) with no anchor (&name)",
        ["YAML0009"] = "A Kubernetes manifest with only one of kind / apiVersion",
        ["YAML0010"] = "A metadata block with no name",
        ["YAML0011"] = "A container with no image",
        ["YAML0012"] = "Two containers in one list with the same name",
        ["YAML0013"] = "A GitHub Actions job with neither runs-on nor uses",
        ["YAML0014"] = "A compose service with neither image nor build",
        ["YAML0015"] = "A GitHub Actions workflow with jobs but no on trigger",
    };

    /// <summary>
    /// The kinds this scanner is willing to call a manifest. A file that declares neither apiVersion nor one of
    /// these is left alone: a YAML document with a `kind` key is not necessarily Kubernetes.
    /// </summary>
    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "Pod", "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "ReplicationController", "Job", "CronJob",
        "Service", "Ingress", "ConfigMap", "Secret", "Namespace", "ServiceAccount", "Role", "ClusterRole",
        "RoleBinding", "ClusterRoleBinding", "PersistentVolume", "PersistentVolumeClaim",
        "HorizontalPodAutoscaler", "NetworkPolicy", "CustomResourceDefinition", "StorageClass", "ResourceQuota",
        "LimitRange", "PodDisruptionBudget",
    };

    /// <summary>The kinds that own a container list; the container rules only apply inside one.</summary>
    private static readonly HashSet<string> WorkloadKinds = new(StringComparer.Ordinal)
    {
        "Pod", "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "ReplicationController", "Job", "CronJob",
    };

    private static readonly Regex KeyPattern = new(
        @"^(?<key>(?:""[^""]*""|'[^']*'|[^\s:#][^:#]*?))\s*:(?:\s+(?<value>.*))?$", Options);

    private static readonly Regex GluedColon = new(@"^[^#\s""'][^:]*:[^\s]", Options);
    private static readonly Regex Anchor = new(@"&(?<name>[A-Za-z0-9_-]+)", Options);
    private static readonly Regex Alias = new(@"\*(?<name>[A-Za-z0-9_-]+)", Options);
    private static readonly Regex Yaml11Boolean = new(@"^(?:y|n|yes|no|on|off)$", Options | RegexOptions.IgnoreCase);

    public static YamlAnalysis AnalyzeText(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var findings = new List<YamlFinding>();
        var topLevel = new List<string>();
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new List<(string Name, int Line, int Column)>();
        var stack = new List<(int Indent, HashSet<string> Keys)>();

        string? kind = null;
        string? apiVersion = null;
        string? name = null;
        var documentCount = 1;
        var sawDocumentMarker = false;

        var inBlockScalar = false;
        var blockScalarIndent = -1;

        // Per-document identity: kind and apiVersion, and the metadata block that must name the object.
        string? documentKind = null;
        string? documentApiVersion = null;
        var documentKindLine = 0;
        var documentApiVersionLine = 0;

        var metadataIndent = -1;
        var metadataLine = 0;
        var metadataNameSeen = false;
        var metadataMissingName = false;
        var metadataMissingNameLine = 0;

        // The containers of a workload document: one list, each item judged when it closes.
        var containersIndent = -1;
        var containerItemIndent = -1;
        var containerOpen = false;
        var containerStartLine = 0;
        var containerHasImage = false;
        string? containerName = null;
        var containerNameLine = 0;
        var containerNames = new HashSet<string>(StringComparer.Ordinal);

        // A GitHub Actions workflow: a top-level jobs block, each job judged when it closes, and the
        // on trigger that must exist for the workflow to run at all.
        var jobsIndent = -1;
        var jobsLine = 0;
        var jobIndent = -1;
        var jobOpen = false;
        var jobStartLine = 0;
        var jobHasRunner = false;
        var documentOn = false;
        var documentHasJobs = false;
        var workflowLooksLikeOne = false;

        // A compose file: a top-level services block, each service judged when it closes.
        var servicesIndent = -1;
        var serviceIndent = -1;
        var serviceOpen = false;
        var serviceStartLine = 0;
        var serviceHasSource = false;

        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var line = index + 1;

            if (raw.Trim().Length == 0)
            {
                inBlockScalar = false;
                continue;
            }

            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;

            if (indent < raw.Length && raw[indent] == '\t')
            {
                Add("YAML0001", "error", "Lexical", "A tab in the indentation is not valid YAML.",
                    line, indent + 1, "Use spaces: most parsers reject the file outright.");
            }

            if (inBlockScalar)
            {
                if (indent > blockScalarIndent) continue;
                inBlockScalar = false;
            }

            var content = raw[indent..].TrimEnd();

            if (content.Length == 0 || content.StartsWith('#')) continue;

            if (content is "---" or "...")
            {
                if (content == "---")
                {
                    if (sawDocumentMarker && index > 0) documentCount++;
                    sawDocumentMarker = true;
                }

                CloseDocument();
                stack.Clear();
                continue;
            }

            if (raw.Length > raw.TrimEnd().Length)
                Add("YAML0003", "info", "Maintainability", "The line ends with whitespace.",
                    line, raw.TrimEnd().Length + 1, "Trim it: it is invisible in a diff and some editors strip it on save.");

            var sequenceItem = false;
            var keyIndent = indent;
            if (content.StartsWith("- ") || content == "-")
            {
                sequenceItem = true;
                keyIndent = indent + 2;
                content = content.Length > 2 ? content[2..].TrimStart() : string.Empty;
                if (content.Length == 0) continue;
            }

            if (GluedColon.IsMatch(content))
            {
                Add("YAML0005", "error", "Correctness",
                    "The colon is not followed by a space, so the whole text is one scalar, not a key and a value.",
                    line, keyIndent + 1, "Write 'key: value' (a space after the colon).");
                continue;
            }

            var match = KeyPattern.Match(content);
            if (!match.Success)
            {
                // A bare scalar (a sequence value, a document body) closes the mapping levels it dedents past.
                while (stack.Count > 0 && stack[^1].Indent > keyIndent) stack.RemoveAt(stack.Count - 1);
                continue;
            }

            var key = Clean(match.Groups["key"].Value);
            var rawValue = (match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty).Trim();
            var value = Clean(rawValue);

            while (stack.Count > 0 && stack[^1].Indent > keyIndent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0 || stack[^1].Indent < keyIndent)
                stack.Add((keyIndent, new HashSet<string>(StringComparer.Ordinal)));

            if (!stack[^1].Keys.Add(key))
                Add("YAML0002", "warning", "Correctness",
                    $"The key '{key}' is declared again in the same mapping; the later value wins.",
                    line, keyIndent + 1, "Rename one of the keys, or merge the two entries.");

            if (keyIndent == 0 && !sequenceItem && !topLevel.Contains(key, StringComparer.Ordinal))
                topLevel.Add(key);

            // A key at or above the metadata block closes it: a sibling or a parent, not a member.
            if (metadataIndent >= 0 && keyIndent <= metadataIndent) CloseMetadata();

            if (key == "kind" && keyIndent == 0 && !sequenceItem && documentKind is null)
            {
                documentKind = value;
                documentKindLine = line;
                kind ??= value;
            }

            if (key == "apiVersion" && keyIndent == 0 && !sequenceItem && documentApiVersion is null)
            {
                documentApiVersion = value;
                documentApiVersionLine = line;
                apiVersion ??= value;
            }

            if (key == "metadata" && !sequenceItem && value.Length == 0)
            {
                metadataIndent = keyIndent;
                metadataLine = line;
                metadataNameSeen = false;
            }
            else if (key == "name" && metadataIndent >= 0 && keyIndent > metadataIndent)
            {
                metadataNameSeen = true;
                name ??= value;
            }

            // A GitHub Actions workflow: `on` and `jobs` are top-level keys, and every job under `jobs` is
            // a mapping key one level deeper. A job is judged when the next job (or the end) closes it.
            if (key == "on" && keyIndent == 0 && !sequenceItem)
                documentOn = true;

            if (key == "jobs" && keyIndent == 0 && !sequenceItem && value.Length == 0)
            {
                CloseJobs();
                jobsIndent = keyIndent;
                jobsLine = line;
                documentHasJobs = true;
            }
            else if (jobsIndent >= 0)
            {
                if (!sequenceItem && keyIndent <= jobsIndent)
                {
                    CloseJobs();
                }
                else if (!sequenceItem && keyIndent > jobsIndent)
                {
                    if (jobIndent < 0 || keyIndent == jobIndent)
                    {
                        CloseJob();
                        jobOpen = true;
                        jobStartLine = line;
                        jobIndent = keyIndent;
                        jobHasRunner = false;
                    }
                    else if (jobOpen && keyIndent > jobIndent)
                    {
                        if (key is "runs-on" or "uses")
                        {
                            // The key's presence is the runner decision; a `runs-on:` whose value is a
                            // block sequence on the next lines is valid, so its empty scalar still counts.
                            jobHasRunner = true;
                            workflowLooksLikeOne = true;
                        }
                        else if (key == "steps")
                        {
                            workflowLooksLikeOne = true;
                        }
                    }
                }
            }

            // A compose file: `services` is top-level and every service is a key one level deeper.
            if (key == "services" && keyIndent == 0 && !sequenceItem && value.Length == 0)
            {
                CloseServices();
                servicesIndent = keyIndent;
            }
            else if (servicesIndent >= 0)
            {
                if (!sequenceItem && keyIndent <= servicesIndent)
                {
                    CloseServices();
                }
                else if (!sequenceItem && keyIndent > servicesIndent)
                {
                    if (serviceIndent < 0 || keyIndent == serviceIndent)
                    {
                        CloseService();
                        serviceOpen = true;
                        serviceStartLine = line;
                        serviceIndent = keyIndent;
                        serviceHasSource = false;
                    }
                    else if (serviceOpen && keyIndent > serviceIndent)
                    {
                        if (key == "image" ? value.Length > 0 : key is "build" or "extends")
                            serviceHasSource = true;
                    }
                }
            }

            // A container list: the item keys sit at one indent, and anything at or above the list closes it.
            if (key == "containers" && !sequenceItem && value.Length == 0)
            {
                CloseContainers();
                containersIndent = keyIndent;
            }
            else if (containersIndent >= 0)
            {
                if (sequenceItem && (containerItemIndent < 0
                        ? keyIndent > containersIndent
                        : keyIndent == containerItemIndent))
                {
                    CloseContainer();
                    containerOpen = true;
                    containerStartLine = line;
                    containerItemIndent = keyIndent;
                }
                else if (!sequenceItem && keyIndent <= containersIndent)
                {
                    CloseContainers();
                }
            }

            if (containerOpen && keyIndent == containerItemIndent)
            {
                if (key == "name")
                {
                    containerName = value;
                    containerNameLine = line;
                }
                else if (key == "image" && value.Length > 0)
                {
                    containerHasImage = true;
                }
            }

            if (value.StartsWith('|') || value.StartsWith('>'))
            {
                inBlockScalar = true;
                blockScalarIndent = keyIndent;
            }

            // A quoted scalar is text the author already decided about: the boolean and the colon-shape
            // rules only apply to a bare value.
            var wasQuoted = rawValue.Length >= 2 && rawValue[0] is '"' or '\'' && rawValue[^1] == rawValue[0];

            if (!wasQuoted && Yaml11Boolean.IsMatch(value))
                Add("YAML0004", "info", "Correctness",
                    $"'{value}' is a boolean in YAML 1.1 and a string in YAML 1.2; the two parsers disagree.",
                    line, keyIndent + 1, "Quote it when it must stay a string.");

            if (!wasQuoted && UnquotedColon(value))
                Add("YAML0006", "warning", "Correctness",
                    "The value contains ': ', so a parser reads what follows as a nested mapping.",
                    line, keyIndent + 1, "Quote the value.");

            var flow = FlowBalance(value);
            if (flow != 0)
                Add("YAML0007", "error", "Correctness",
                    "A flow collection is not closed on this line.", line, keyIndent + 1,
                    "Close the [ ] or { } collection, or continue it on the next line consistently.");

            foreach (Match anchor in Anchor.Matches(value)) anchors.Add(anchor.Groups["name"].Value);
            foreach (Match alias in Alias.Matches(value))
                aliases.Add((alias.Groups["name"].Value, line, keyIndent + 1 + alias.Index));
        }

        foreach (var (aliasName, aliasLine, aliasColumn) in
                 aliases.Where(candidate => !anchors.Contains(candidate.Name)))
            Add("YAML0008", "warning", "Correctness",
                $"The alias '*{aliasName}' has no matching '&{aliasName}' anchor in this file.",
                aliasLine, aliasColumn, "Declare the anchor, or fix the name.");

        CloseDocument();

        return new YamlAnalysis(
            lines.Length,
            documentCount,
            topLevel,
            kind,
            apiVersion,
            name,
            findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList());

        void CloseContainer()
        {
            if (containerOpen && WorkloadKinds.Contains(documentKind ?? string.Empty))
            {
                if (!containerHasImage)
                    Add("YAML0011", "error", "Correctness",
                        "The container has no image, so a pod built from this manifest has nothing to run.",
                        containerStartLine, 1, "Add image: repository/name:tag.");

                if (containerName is not null && !containerNames.Add(containerName))
                    Add("YAML0012", "warning", "Correctness",
                        $"A second container in this list is also called '{containerName}'.",
                        containerNameLine, 1, "Give each container a distinct name.");
            }

            containerOpen = false;
            containerHasImage = false;
            containerName = null;
            containerNameLine = 0;
        }

        void CloseContainers()
        {
            CloseContainer();
            containersIndent = -1;
            containerItemIndent = -1;
            containerNames.Clear();
        }

        void CloseJob()
        {
            // Only a job in a workflow is judged: an `on` trigger or another job with steps/runs-on proves
            // the document is one, so an unrelated top-level `jobs` mapping is left alone.
            if (jobOpen && !jobHasRunner && (documentOn || workflowLooksLikeOne))
                Add("YAML0013", "warning", "Correctness",
                    "The job has neither runs-on nor uses, so GitHub Actions cannot choose a runner for it.",
                    jobStartLine, 1, "Add runs-on: ubuntu-latest, or uses: for a reusable workflow.");

            jobOpen = false;
            jobIndent = -1;
            jobHasRunner = false;
        }

        void CloseJobs()
        {
            CloseJob();
            jobsIndent = -1;
        }

        void CloseService()
        {
            if (serviceOpen && !serviceHasSource)
                Add("YAML0014", "warning", "Correctness",
                    "The service has neither image nor build, so compose has nothing to run.",
                    serviceStartLine, 1, "Add image: name:tag, or build: ./path.");

            serviceOpen = false;
            serviceIndent = -1;
            serviceHasSource = false;
        }

        void CloseServices()
        {
            CloseService();
            servicesIndent = -1;
        }

        void CloseMetadata()
        {
            if (metadataIndent < 0) return;

            if (!metadataNameSeen)
            {
                metadataMissingName = true;
                metadataMissingNameLine = metadataLine;
            }

            metadataIndent = -1;
            metadataLine = 0;
            metadataNameSeen = false;
        }

        void CloseDocument()
        {
            CloseContainers();
            CloseMetadata();
            CloseJobs();
            CloseServices();

            if (documentHasJobs && !documentOn && workflowLooksLikeOne)
                Add("YAML0015", "warning", "Correctness",
                    "The workflow declares jobs but no on trigger, so it never runs.",
                    jobsLine, 1, "Add on: push (or the events that should run it).");

            var manifest = documentApiVersion is not null || KnownKinds.Contains(documentKind ?? string.Empty);
            if (manifest)
            {
                if (documentKind is null)
                    Add("YAML0009", "error", "Correctness",
                        "The manifest declares apiVersion but no kind, so Kubernetes cannot tell what object it is.",
                        documentApiVersionLine, 1, "Add kind: Deployment (or the object's kind).");
                else if (documentApiVersion is null)
                    Add("YAML0009", "error", "Correctness",
                        "The manifest declares kind but no apiVersion, so it cannot be submitted to a cluster.",
                        documentKindLine, 1, "Add the apiVersion for that kind, e.g. apps/v1.");
            }

            if (manifest && metadataMissingName)
                Add("YAML0010", "warning", "Correctness",
                    "The metadata block has no name, so two objects from this manifest are indistinguishable.",
                    metadataMissingNameLine, 1, "Add metadata.name.");

            documentKind = null;
            documentApiVersion = null;
            documentKindLine = 0;
            documentApiVersionLine = 0;
            metadataMissingName = false;
            metadataMissingNameLine = 0;

            documentOn = false;
            documentHasJobs = false;
            workflowLooksLikeOne = false;
            jobsLine = 0;
        }

        void Add(string rule, string severity, string category, string message, int line, int column, string fix) =>
            findings.Add(new YamlFinding(rule, severity, category, message, filePath, line, column,
                Snippet(lines, line), fix));
    }

    // ── value helpers ──────────────────────────────────────────────────────────

    /// <summary>The key or value without its quotes and without a trailing comment.</summary>
    private static string Clean(string text)
    {
        var value = text.Trim();

        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '#' && index > 0 && char.IsWhiteSpace(value[index - 1]))
            {
                value = value[..index].TrimEnd();
                break;
            }
        }

        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            value = value[1..^1];

        return value;
    }

    /// <summary>True when the value holds a <c>: </c> outside quotes (a nested mapping in disguise).</summary>
    private static bool UnquotedColon(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            if (character is '"' or '\'') { quote = character; continue; }
            if (character == ':' && index + 1 < value.Length && value[index + 1] == ' ') return true;
        }

        return false;
    }

    /// <summary>0 when every bracket opened in the value is closed in it, otherwise the imbalance.</summary>
    private static int FlowBalance(string value)
    {
        var balance = 0;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            switch (character)
            {
                case '"' or '\'':
                    quote = character;
                    break;
                case '[' or '{':
                    balance++;
                    break;
                case ']' or '}':
                    balance--;
                    break;
            }
        }

        // An empty value and a value that starts a collection are both fine; only the leftover count matters.
        return value.TrimStart() is "" or "|" or ">" ? 0 : balance;
    }

    private static string Snippet(string[] lines, int line)
    {
        if (line < 1 || line > lines.Length) return string.Empty;
        var snippet = lines[line - 1].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }
}

/// <summary>One YAML finding.</summary>
public sealed record YamlFinding(
    string RuleId, string Severity, string Category, string Message, string File, int Line, int Column,
    string Snippet, string? Fix);

/// <summary>The facts a scan of one YAML file produces; Kind/ApiVersion/Name identify a manifest.</summary>
public sealed record YamlAnalysis(
    int Lines, int Documents, IReadOnlyList<string> TopLevelKeys, string? Kind, string? ApiVersion, string? Name,
    IReadOnlyList<YamlFinding> Findings);
