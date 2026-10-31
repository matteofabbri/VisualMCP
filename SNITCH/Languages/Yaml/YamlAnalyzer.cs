using System.Text.RegularExpressions;

namespace Snitch.Languages.Yaml;

/// <summary>
/// YAML as a scanned language: this is an indentation-aware line walk, not a full YAML parser. It
/// handles what a manifest in a repository actually contains — block mappings, sequence items, block
/// scalars, comments, anchors and aliases — and reports the mistakes that change the meaning of a file
/// silently: a tab in the indentation, a key declared twice, a colon glued to its key, the YAML 1.1
/// booleans, an alias with no anchor, an unclosed flow collection. It also reads the few keys a
/// Kubernetes manifest is identified by, which is what `yaml_files` shows.
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
        var metadataIndent = -1;

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

            if (key == "kind" && kind is null && value.Length > 0) kind = value;
            if (key == "apiVersion" && apiVersion is null && value.Length > 0) apiVersion = value;
            if (key == "metadata") metadataIndent = keyIndent;
            if (key == "name" && name is null && metadataIndent >= 0 && keyIndent > metadataIndent) name = value;

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

        return new YamlAnalysis(
            lines.Length,
            documentCount,
            topLevel,
            kind,
            apiVersion,
            name,
            findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList());

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
