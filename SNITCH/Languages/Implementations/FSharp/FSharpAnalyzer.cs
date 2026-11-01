using System.Text.RegularExpressions;

namespace Snitch.Languages.FSharp;

/// <summary>
/// F# is not a Roslyn language, so this layer is a scanner like the SQL, JS/TS and YAML ones: comments
/// and literals are masked, the declarations that survive are read (modules, types, functions, opens)
/// and the rules describe the shapes that are known to bite — partial accesses that throw, unchecked
/// casts, null comparison, swallowed exceptions. It is a pattern scan over an indentation-sensitive
/// language: it never resolves a symbol and never type-checks an expression.
/// </summary>
public static class FSharpAnalyzer
{
    private static readonly RegexOptions Options = RegexOptions.Compiled;

    private static readonly string[] FSharpExtensions = [".fs", ".fsi", ".fsx"];

    public static bool IsFSharp(string path) =>
        FSharpExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["FS0001"] = "Tab in the indentation (F# is indentation-sensitive and expects spaces)",
        ["FS0002"] = "Option.get / .Value on a partial access that throws on None",
        ["FS0003"] = "List.head / Seq.head / List.item and friends throw on an empty or short collection",
        ["FS0004"] = "Unchecked.defaultof / unbox without a type test",
        ["FS0005"] = "Comparison with null (F# uses isNull / Option.ofObj)",
        ["FS0006"] = "A catch-all 'with _ ->' that swallows the exception",
        ["FS0007"] = "A mutable binding",
        ["FS0008"] = "printf/printfn left in the source",
        ["FS0009"] = "failwith in a library module (an exception where a Result would be checked)",
        ["FS0010"] = "An 'open' that appears twice",
    };

    private static readonly Regex OptionValue = new(@"\bOption\s*\.\s*(?:get|Value)\b", Options);
    private static readonly Regex PartialList = new(
        @"\b(?:List|Seq|Array)\s*\.\s*(?:head|tail|last|item|reduce|max|min)\b|\bArray\s*\.\s*get\b|\bSeq\s*\.\s*exactlyOne\b",
        Options);
    private static readonly Regex Unchecked = new(@"\bUnchecked\s*\.\s*defaultof\b|\bunbox\b(?!\s*:)", Options);
    private static readonly Regex NullComparison = new(@"(?:=|<>)\s*null\b(?!s)|\bnull\s*(?:=|<>)(?!=)", Options);
    private static readonly Regex SwallowedCatch = new(@"\bwith\s+_\s*->", Options);
    private static readonly Regex MutableBinding = new(@"\blet\s+mutable\b", Options);
    private static readonly Regex PrintStatement = new(@"\bprintfn?\b|\bprintf\b", Options);
    private static readonly Regex FailWith = new(@"\bfailwith\b|\bfailwithf\b", Options);

    private static readonly Regex ModuleDeclaration = new(@"^(?:namespace|module)\s+(?<name>[\w.]+)", Options);
    private static readonly Regex TypeDeclaration = new(@"^type\s+(?<name>[A-Za-z_][\w']*)", Options);
    private static readonly Regex FunctionBinding = new(
        @"^(?:let|and)\s+(?:rec\s+|inline\s+|private\s+|internal\s+|public\s+|mutable\s+)*(?<name>[A-Za-z_][\w']*)\s*(?:<[^>]*>)?\s*(?:\(|:|=|[\w']|$)",
        Options);
    private static readonly Regex OpenDeclaration = new(@"^open\s+(?:type\s+)?(?<name>[\w.]+)", Options);

    public static FSharpAnalysis AnalyzeText(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var masked = MaskLines(lines);
        var findings = new List<FSharpFinding>();
        var modules = new List<string>();
        var types = new List<string>();
        var functions = new List<string>();
        var opens = new List<string>();

        void Add(string rule, string severity, string category, string message, int line, int column, string snippet,
            string fix) =>
            findings.Add(new FSharpFinding(rule, severity, category, message, filePath, line, column, snippet, fix));

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var maskedLine = masked[index];
            var line = index + 1;

            if (Regex.IsMatch(raw, @"^\s*\t"))
            {
                var indent = raw.Length - raw.TrimStart('\t', ' ').Length;
                Add("FS0001", "error", "Lexical", "A tab in the indentation breaks the offside rule.",
                    line, indent + 1, Snippet(raw), "Indent with spaces.");
            }

            var trimmed = maskedLine.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
            var indentation = maskedLine.Length - trimmed.Length;

            foreach (Match match in ModuleDeclaration.Matches(trimmed))
                if (!modules.Contains(match.Groups["name"].Value)) modules.Add(match.Groups["name"].Value);

            foreach (Match match in TypeDeclaration.Matches(trimmed))
                if (!types.Contains(match.Groups["name"].Value)) types.Add(match.Groups["name"].Value);

            foreach (Match match in FunctionBinding.Matches(trimmed))
                if (indentation == 0 && !functions.Contains(match.Groups["name"].Value))
                    functions.Add(match.Groups["name"].Value);

            foreach (Match match in OpenDeclaration.Matches(trimmed))
            {
                var name = match.Groups["name"].Value;
                if (opens.Contains(name))
                    Add("FS0010", "info", "Maintainability", $"'{name}' is opened again.", line, indentation + 1,
                        Snippet(raw), "Remove the duplicate open.");
                else opens.Add(name);
            }

            void Scan(string rule, string severity, string category, Regex regex, string message, string fix)
            {
                foreach (Match match in regex.Matches(maskedLine))
                    Add(rule, severity, category, message, line, indentation + match.Index + 1, Snippet(raw), fix);
            }

            Scan("FS0002", "warning", "Correctness", OptionValue,
                "An option is read through a partial access, which throws on None.",
                "Match on the option (Some/None) or use Option.defaultValue.");
            Scan("FS0003", "warning", "Correctness", PartialList,
                "This collection access throws when the collection is empty or too short.",
                "Match on the list (head :: tail) or use tryHead / Option.ofSeq.");
            Scan("FS0004", "warning", "Correctness", Unchecked,
                "The cast is unchecked, so a runtime type mismatch is not reported by the compiler.",
                "Use a type test (:? T) or a typed conversion.");
            Scan("FS0005", "info", "Correctness", NullComparison,
                "Comparing with null is not the F# idiom and is unreliable for option-like values.",
                "Use isNull / not (isNull x), or Option.ofObj.");
            Scan("FS0006", "warning", "Correctness", SwallowedCatch,
                "A catch-all that ignores the exception hides the failure.",
                "Match the exception type, log it, or rethrow.");
            Scan("FS0007", "info", "Maintainability", MutableBinding,
                "A mutable binding makes the value change over time.", "Prefer a functional update or a ref cell that is owned.");
            Scan("FS0008", "info", "Maintainability", PrintStatement,
                "A console print ships to production.", "Route the message through a logger.");
            Scan("FS0009", "info", "Correctness", FailWith,
                "failwith raises an exception on the failure path.",
                "Consider a Result, or keep it for a genuinely exceptional state.");
        }

        return new FSharpAnalysis(
            lines.Length, modules, types, functions, opens,
            findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList());
    }

    /// <summary>
    /// The source split into lines with comments and literals blanked and every offset preserved. The
    /// symbol table scans this text, so an identifier inside a string is never read as a name.
    /// </summary>
    internal static string[] MaskedLines(string text) =>
        MaskLines((text ?? string.Empty).Replace("\r\n", "\n").Split('\n'));

    /// <summary>
    /// Blanks comments and literals, keeping the line structure: block comments <c>(* … *)</c> (nested),
    /// line comments, ordinary, verbatim and triple-quoted strings, and character literals.
    /// </summary>
    private static string[] MaskLines(string[] lines)
    {
        var result = new string[lines.Length];
        var depth = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var source = lines[index];
            var chars = source.ToCharArray();
            var position = 0;

            while (position < source.Length)
            {
                if (depth > 0)
                {
                    if (position + 1 < source.Length && source[position] == '(' && source[position + 1] == '*')
                    {
                        depth++;
                        chars[position] = chars[position + 1] = ' ';
                        position += 2;
                        continue;
                    }

                    if (position + 1 < source.Length && source[position] == '*' && source[position + 1] == ')')
                    {
                        depth--;
                        chars[position] = chars[position + 1] = ' ';
                        position += 2;
                        continue;
                    }

                    chars[position] = ' ';
                    position++;
                    continue;
                }

                if (position + 1 < source.Length && source[position] == '(' && source[position + 1] == '*')
                {
                    depth = 1;
                    chars[position] = chars[position + 1] = ' ';
                    position += 2;
                    continue;
                }

                if (position + 1 < source.Length && source[position] == '/' && source[position + 1] == '/')
                {
                    for (var rest = position; rest < chars.Length; rest++) chars[rest] = ' ';
                    break;
                }

                if (position + 2 < source.Length && source[position] == '"' &&
                    source[position + 1] == '"' && source[position + 2] == '"')
                {
                    var end = source.IndexOf("\"\"\"", position + 3, StringComparison.Ordinal);
                    var stop = end < 0 ? chars.Length : end + 3;
                    for (var rest = position; rest < stop && rest < chars.Length; rest++) chars[rest] = ' ';
                    position = stop;
                    continue;
                }

                if (source[position] == '@' && position + 1 < source.Length && source[position + 1] == '"')
                {
                    var end = position + 2;
                    while (end < source.Length)
                    {
                        if (source[end] == '"')
                        {
                            if (end + 1 < source.Length && source[end + 1] == '"') { end += 2; continue; }
                            end++;
                            break;
                        }

                        end++;
                    }

                    for (var rest = position; rest < end && rest < chars.Length; rest++) chars[rest] = ' ';
                    position = end;
                    continue;
                }

                if (source[position] == '"')
                {
                    var end = position + 1;
                    while (end < source.Length)
                    {
                        if (source[end] == '\\') { end += 2; continue; }
                        if (source[end] == '"') { end++; break; }
                        end++;
                    }

                    for (var rest = position; rest < end && rest < chars.Length; rest++) chars[rest] = ' ';
                    position = end;
                    continue;
                }

                if (source[position] == '\'' && position + 2 < source.Length && source[position + 2] == '\'' &&
                    source[position + 1] != '\\')
                {
                    chars[position] = chars[position + 1] = chars[position + 2] = ' ';
                    position += 3;
                    continue;
                }

                position++;
            }

            result[index] = new string(chars);
        }

        return result;
    }

    private static string Snippet(string line)
    {
        var snippet = line.Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }
}

/// <summary>One F# finding.</summary>
public sealed record FSharpFinding(
    string RuleId, string Severity, string Category, string Message, string File, int Line, int Column,
    string Snippet, string? Fix);

/// <summary>What a scan of one F# file found: the declarations and the findings.</summary>
public sealed record FSharpAnalysis(
    int Lines, IReadOnlyList<string> Modules, IReadOnlyList<string> Types, IReadOnlyList<string> Functions,
    IReadOnlyList<string> Opens, IReadOnlyList<FSharpFinding> Findings);
