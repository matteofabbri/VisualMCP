using System.Text.RegularExpressions;

namespace Snitch.Languages.JavaScript;

/// <summary>
/// JavaScript, TypeScript, JSX and TSX as a scanned language rather than a parsed one: the files are
/// masked (comments, strings and template literals blanked, offsets preserved) and the rules that need
/// no type information are applied over the mask. The rules are the shapes a reviewer looks for first —
/// dynamic code, HTML injection, loose equality, leftovers — and the JSX pass reads the raw text of a
/// tag the mask says is real code. It is a pattern scanner: it never claims to resolve a module, a type
/// or a binding.
/// </summary>
public static class JavaScriptAnalyzer
{
    private static readonly RegexOptions Options = RegexOptions.Compiled;

    private static readonly string[] ScriptExtensions =
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"];

    public static bool IsScript(string path) =>
        ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsTypeScript(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".tsx" or ".mts" or ".cts";

    /// <summary>True for a specifier the scanner can resolve against the file system: ./ and ../.</summary>
    public static bool IsRelativeSpecifier(string module) =>
        module is "." or ".." ||
        module.StartsWith("./", StringComparison.Ordinal) ||
        module.StartsWith("../", StringComparison.Ordinal);

    /// <summary>The rule catalogue, id to what the rule proves.</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["JS0001"] = "Unterminated string, template literal or block comment",
        ["JS0002"] = "eval(…) executes a string as code",
        ["JS0003"] = "new Function(…) compiles a string into a function",
        ["JS0004"] = "innerHTML / outerHTML assigned something that is not a literal",
        ["JS0005"] = "dangerouslySetInnerHTML in JSX",
        ["JS0006"] = "document.write(…) rewrites the document",
        ["JS0007"] = "Loose equality (== / !=) instead of === / !==",
        ["JS0008"] = "var declaration",
        ["JS0009"] = "console statement left in the source",
        ["JS0010"] = "debugger statement",
        ["JS0011"] = "Empty catch block swallows the error",
        ["JS0012"] = "@ts-ignore / @ts-nocheck suppresses the type checker",
        ["JS0013"] = "target=\"_blank\" without rel=\"noopener\"",
        ["JS0014"] = "JSX <img> without alt",
        ["JS0015"] = "Explicit any in a TypeScript annotation",
        ["JS0016"] = "setTimeout / setInterval given a string",
        ["JS0017"] = "A relative import that does not resolve to a file",
    };

    private static readonly Regex Eval = new(@"\beval\s*\(", Options);
    private static readonly Regex NewFunction = new(@"\bnew\s+Function\s*\(", Options);
    private static readonly Regex HtmlAssignment = new(@"\.(?:inner|outer)HTML\s*=\s*", Options);
    private static readonly Regex DangerousHtml = new(@"\bdangerouslySetInnerHTML\b", Options);
    private static readonly Regex DocumentWrite = new(@"\bdocument\s*\.\s*write(?:ln)?\s*\(", Options);
    private static readonly Regex LooseEquality = new(@"[^=!<>]==(?!=)|!=(?!=)", Options);
    private static readonly Regex VarDeclaration = new(@"\bvar\s+[A-Za-z_$]", Options);
    private static readonly Regex ConsoleCall = new(@"\bconsole\s*\.\s*(?:log|warn|error|info|debug|trace)\s*\(", Options);
    private static readonly Regex Debugger = new(@"\bdebugger\s*;?", Options);
    private static readonly Regex EmptyCatch = new(@"\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}", Options);
    private static readonly Regex TsSuppression = new(@"@ts-(?:ignore|nocheck)\b", Options);
    private static readonly Regex AnyAnnotation = new(@":\s*any\b|<any>|\bas\s+any\b", Options);
    private static readonly Regex TimedString = new(@"\b(?:setTimeout|setInterval)\s*\(\s*(?=[""']|`)", Options);

    private static readonly Regex ImportFrom = new(
        @"\bimport\s+(?:[\s\S]*?\s+from\s+)?[""'](?<module>[^""']+)[""']", Options);
    private static readonly Regex Require = new(@"\brequire\s*\(\s*[""'](?<module>[^""']+)[""']\s*\)", Options);
    private static readonly Regex DynamicImport = new(@"\bimport\s*\(\s*[""'](?<module>[^""']+)[""']", Options);
    private static readonly Regex ExportDeclaration = new(
        @"\bexport\s+(?:default\s+)?(?:declare\s+)?(?:const|let|var|class|function|async\s+function|interface|type|enum)\s+(?<name>[A-Za-z_$][\w$]*)",
        Options);
    private static readonly Regex FunctionDeclaration = new(@"\bfunction\s+[A-Za-z_$][\w$]*\s*\(", Options);
    private static readonly Regex ArrowBinding = new(
        @"\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>", Options);
    private static readonly Regex ClassDeclaration = new(@"\bclass\s+[A-Za-z_$][\w$]*", Options);
    private static readonly Regex JsxElement = new(@"<[A-Z][\w.]*\s[^<>]*>", Options);
    private static readonly Regex JsxClosing = new(@"</[A-Z]", Options);
    private static readonly Regex JsxImage = new(@"<img\b[^>]*>", Options);
    private static readonly Regex JsxAnchor = new(@"<a\b[^>]*>", Options);
    private static readonly Regex TargetBlank = new(@"target\s*=\s*[""']?_blank", Options | RegexOptions.IgnoreCase);

    /// <summary>Analyzes one file's text. The extension in <paramref name="filePath"/> decides JSX and TypeScript.</summary>
    public static JsAnalysis AnalyzeText(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var lineStarts = LineStarts(text);
        var masked = new string(MaskCore(text, out var lexicalIssues));
        var findings = new List<JsFinding>();

        foreach (var lexical in lexicalIssues)
        {
            var (line, column) = Position(lineStarts, lexical.Offset);
            findings.Add(new JsFinding("JS0001", "error", "Lexical", lexical.Message, filePath, line, column,
                Snippet(text, lineStarts, line), "Close it: everything after an unterminated construct is not read as code."));
        }

        void Add(string rule, string severity, string category, Regex regex, string message, string fix,
            Func<int, bool>? accept = null)
        {
            foreach (Match match in regex.Matches(masked))
            {
                if (accept is not null && !accept(match.Index)) continue;
                var (line, column) = Position(lineStarts, match.Index);
                findings.Add(new JsFinding(rule, severity, category, message, filePath, line, column,
                    Snippet(text, lineStarts, line), fix));
            }
        }

        void AddRaw(string rule, string severity, string category, Regex regex, string message, string fix)
        {
            foreach (Match match in regex.Matches(text))
            {
                var (line, column) = Position(lineStarts, match.Index);
                findings.Add(new JsFinding(rule, severity, category, message, filePath, line, column,
                    Snippet(text, lineStarts, line), fix));
            }
        }

        Add("JS0002", "error", "Security", Eval,
            "eval executes a string as code.", "Remove eval: parse the input, or map it to a known operation.");
        Add("JS0003", "error", "Security", NewFunction,
            "new Function compiles a string into code.", "Use a declared function; a string compiler is an injection point.");
        Add("JS0004", "warning", "Security", HtmlAssignment,
            "innerHTML/outerHTML is assigned a value that is not a literal.",
            "Assign textContent, or sanitise the value first (DOMPurify or equivalent).",
            index => !RightHandStartsLiteral(text, index));
        Add("JS0005", "warning", "Security", DangerousHtml,
            "dangerouslySetInnerHTML bypasses React's escaping.", "Render the content as elements, or sanitise it.");
        Add("JS0006", "warning", "Security", DocumentWrite,
            "document.write rewrites the document and can inject markup.", "Build the DOM node and append it.");
        Add("JS0007", "info", "Correctness", LooseEquality,
            "Loose equality coerces its operands.", "Use === or !== unless the coercion is the point.");
        Add("JS0008", "info", "Maintainability", VarDeclaration,
            "var is function-scoped and hoisted.", "Use const, or let when the value is reassigned.");
        Add("JS0009", "info", "Maintainability", ConsoleCall,
            "A console statement ships to production.", "Remove it, or route the message through a logger.");
        Add("JS0010", "warning", "Correctness", Debugger,
            "debugger halts execution when the developer tools are open.", "Remove it before committing.");
        Add("JS0011", "warning", "Correctness", EmptyCatch,
            "The catch block is empty, so the error is silently dropped.", "Handle it, rethrow it, or log it.");
        // The directive lives in a comment by definition, so this rule reads the raw text.
        AddRaw("JS0012", "info", "Maintainability", TsSuppression,
            "The type checker is suppressed on the next line or file.", "Fix the type, or narrow the suppression to a reason.");
        Add("JS0016", "warning", "Security", TimedString,
            "setTimeout/setInterval given a string evaluates it as code.", "Pass a function instead of a string.");

        if (IsTypeScript(filePath))
            Add("JS0015", "info", "Maintainability", AnyAnnotation,
                "`any` turns off type checking for the value.", "Name the type, or use unknown and narrow it.");

        Add("JS0013", "warning", "Security", JsxAnchor,
            "A link opens a new tab without rel=\"noopener\", so the opened page can reach window.opener.",
            "Add rel=\"noopener noreferrer\".",
            index => OpensNewTabWithoutRel(text, masked, index));

        Add("JS0014", "warning", "Accessibility", JsxImage,
            "An image has no alt text.", "Add alt=\"…\"; use alt=\"\" only for a decorative image.",
            index => !HasAttribute(masked, index, "alt"));

        // A module specifier is a string, so the mask blanked it: the patterns run on the raw text and
        // the keyword's own offset decides whether the match is code or a string that mentions it.
        var referenceMatches = new List<(Match Match, string Module)>();
        foreach (Match match in ImportFrom.Matches(text))
            if (IsVisible(masked, match.Index))
                referenceMatches.Add((match, match.Groups["module"].Value));
        foreach (Match match in Require.Matches(text))
            if (IsVisible(masked, match.Index))
                referenceMatches.Add((match, match.Groups["module"].Value));
        foreach (Match match in DynamicImport.Matches(text))
            if (IsVisible(masked, match.Index))
                referenceMatches.Add((match, match.Groups["module"].Value));

        var references = referenceMatches
            .Select(entry =>
            {
                var (line, column) = Position(lineStarts, entry.Match.Groups["module"].Index);
                return new JsImport(entry.Module, line, column, IsRelativeSpecifier(entry.Module));
            })
            .ToList();

        var imports = references
            .Select(reference => reference.Module)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var exports = ExportDeclaration.Matches(masked)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new JsAnalysis(
            Lines: lineStarts.Length,
            Imports: imports,
            Exports: exports,
            Functions: FunctionDeclaration.Matches(masked).Count + ArrowBinding.Matches(masked).Count,
            Classes: ClassDeclaration.Matches(masked).Count,
            Jsx: JsxElement.IsMatch(masked) || JsxClosing.IsMatch(masked),
            TypeScript: IsTypeScript(filePath),
            Findings: findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList(),
            ImportReferences: references);
    }

    // ── masking ────────────────────────────────────────────────────────────────

    /// <summary>Blanks comments, strings and template literals (delimiters kept), reporting lexical damage.</summary>
    private static char[] MaskCore(string text, out List<(int Offset, string Message)> lexical)
    {
        var chars = text.ToCharArray();
        var issues = new List<(int Offset, string Message)>();
        var index = 0;

        void Blank(int from, int to)
        {
            for (var position = from; position < to && position < chars.Length; position++)
                if (chars[position] != '\n' && chars[position] != '\r') chars[position] = ' ';
        }

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = text.IndexOf('\n', index);
                if (end < 0) end = text.Length;
                Blank(index, end);
                index = end;
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    issues.Add((index, "Unterminated block comment."));
                    Blank(index, text.Length);
                    break;
                }

                Blank(index, end + 2);
                index = end + 2;
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                var delimiter = current;
                var start = index;
                var position = index + 1;
                var closed = false;
                var depth = 0;

                while (position < text.Length)
                {
                    var character = text[position];
                    if (character == '\\')
                    {
                        position += 2;
                        continue;
                    }

                    if (delimiter == '`' && character == '$' && position + 1 < text.Length && text[position + 1] == '{')
                    {
                        depth++;
                        position += 2;
                        continue;
                    }

                    if (delimiter == '`' && character == '}' && depth > 0)
                    {
                        depth--;
                        position++;
                        continue;
                    }

                    if (character == delimiter && depth == 0)
                    {
                        position++;
                        closed = true;
                        break;
                    }

                    position++;
                }

                if (!closed)
                    issues.Add((start, delimiter == '`'
                        ? "Unterminated template literal."
                        : "Unterminated string literal."));

                Blank(start + 1, Math.Max(start + 1, position - 1));
                index = position;
                continue;
            }

            index++;
        }

        lexical = issues;
        return chars;
    }

    // ── JSX helpers (raw text, at an offset the mask says is code) ─────────────

    private static bool OpensNewTabWithoutRel(string raw, string masked, int index)
    {
        if (!IsVisible(masked, index) || masked[index] != '<') return false;
        var end = masked.IndexOf('>', index);
        if (end < 0) return false;

        var tag = raw[index..(end + 1)];
        return TargetBlank.IsMatch(tag) && !tag.Contains("rel=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAttribute(string masked, int index, string attribute)
    {
        if (!IsVisible(masked, index) || masked[index] != '<') return false;
        var end = masked.IndexOf('>', index);
        if (end < 0) return false;

        return masked[index..(end + 1)].Contains(attribute + "=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisible(string masked, int index) =>
        index >= 0 && index < masked.Length && masked[index] != ' ';

    // ── small helpers ──────────────────────────────────────────────────────────

    private static bool RightHandStartsLiteral(string text, int assignmentIndex)
    {
        var position = assignmentIndex;
        while (position < text.Length && text[position] != '=') position++;
        position++;
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        return position < text.Length && text[position] is '"' or '\'' or '`';
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n')
                starts.Add(index + 1);
        return starts.ToArray();
    }

    private static (int Line, int Column) Position(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        if (index < 0) index = ~index - 1;
        if (index < 0) index = 0;
        return (index + 1, offset - lineStarts[index] + 1);
    }

    private static string Snippet(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length) return string.Empty;
        var start = lineStarts[line - 1];
        var end = line < lineStarts.Length ? lineStarts[line] : text.Length;
        var snippet = text[start..end].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }
}

/// <summary>One finding, with the file and the position the caller reports.</summary>
public sealed record JsFinding(
    string RuleId, string Severity, string Category, string Message, string File, int Line, int Column,
    string Snippet, string? Fix);

/// <summary>An import specifier with the position it is written at; Relative is true for ./ and ../.</summary>
public sealed record JsImport(string Module, int Line, int Column, bool Relative);

/// <summary>The facts a scan of one file produces.</summary>
public sealed record JsAnalysis(
    int Lines, IReadOnlyList<string> Imports, IReadOnlyList<string> Exports, int Functions, int Classes,
    bool Jsx, bool TypeScript, IReadOnlyList<JsFinding> Findings, IReadOnlyList<JsImport> ImportReferences);
