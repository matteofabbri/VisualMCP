using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Yaml;

namespace VisualMCP.Tools.Yaml;

[McpServerToolType]
public static class YamlTools
{
    [McpServerTool(Name = "yaml_files"), Description(
        "What the solution's YAML is: every .yml/.yaml file under the solution (or under `path`), with its line " +
        "count, how many documents it holds, its top-level keys, and — when it is a Kubernetes manifest — its kind, " +
        "apiVersion and metadata name. node_modules, dist, build, coverage, vendor and the generated chart folders " +
        "are skipped. Use it to find the manifests and the CI configuration; analyze_yaml lists the findings. " +
        "Read-only.")]
    public static Task<object> YamlFiles(
        IYamlService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500)
        => svc.InventoryAsync(path, maxFiles);

    [McpServerTool(Name = "analyze_yaml"), Description(
        "Pattern-based YAML diagnostics over the solution's .yml/.yaml files. Rules: YAML0001 a tab in the " +
        "indentation, YAML0002 a key declared twice in one mapping, YAML0003 trailing whitespace, YAML0004 an " +
        "unquoted yes/no/on/off (a boolean in YAML 1.1, a string in 1.2), YAML0005 a colon glued to its key " +
        "('key:value'), YAML0006 a value containing ': ' that a parser reads as a nested mapping, YAML0007 an " +
        "unbalanced [ ] or { } flow collection, YAML0008 an alias (*name) with no anchor (&name). It masks " +
        "comments, block scalars and quoted text first, so a 'key: value' inside them does not fire. An " +
        "indentation-aware line scan, not a YAML parser: it does not validate a schema or a chart. Read-only.")]
    public static Task<object> AnalyzeYaml(
        IYamlService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Minimum severity: info (default), warning or error.")] string minSeverity = "info",
        [Description("Only this rule id, e.g. YAML0002.")] string? rule = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500,
        [Description("Maximum findings to return (default 200; the total is always reported).")] int maxFindings = 200)
        => svc.AnalyzeAsync(path, minSeverity, rule, maxFiles, maxFindings);
}
