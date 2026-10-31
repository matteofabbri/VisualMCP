using System.Text;
using System.Text.RegularExpressions;
using Snitch.Workspace;

namespace Snitch.Languages.Sql;

/// <summary>
/// SQL as a scanned language, not a parsed one: the dialects a .NET solution carries (T-SQL,
/// PostgreSQL, SQLite, MySQL) differ too much for one grammar to be correct, so this layer masks
/// comments and literals, splits the file into statements and applies the rules whose answer does
/// not depend on a schema. It is a pattern scanner in the same sense as the Razor and XAML layers:
/// every rule says what it proves, and none of them claims to validate a query against a database.
/// </summary>
public static class SqlAnalyzer
{
    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    /// <summary>The rule catalogue, id to what the rule proves.</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["SQL0001"] = "Unterminated string literal, block comment or bracket-quoted identifier",
        ["SQL0002"] = "Unbalanced parentheses",
        ["SQL0003"] = "SELECT * — the projection is not explicit",
        ["SQL0004"] = "UPDATE or DELETE without a WHERE clause",
        ["SQL0005"] = "DROP without IF EXISTS",
        ["SQL0006"] = "Dynamic SQL assembled by string concatenation",
        ["SQL0007"] = "A transaction is opened and never committed or rolled back",
        ["SQL0008"] = "LIKE with a leading wildcard (the index cannot be used)",
        ["SQL0009"] = "ALTER TABLE … ADD … NOT NULL without DEFAULT (fails on a non-empty table)",
        ["SQL0010"] = "INSERT column and value counts differ",
    };

    private static readonly Regex SelectStar = new(@"\bSELECT\s+(?:DISTINCT\s+|ALL\s+)?\*", Options);
    private static readonly Regex WhereClause = new(@"\bWHERE\b", Options);
    private static readonly Regex DropStatement = new(
        @"\bDROP\s+(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION|INDEX|DATABASE|SCHEMA|TRIGGER)\b(?!\s+IF\s+EXISTS)", Options);
    private static readonly Regex DynamicSql = new(
        @"\b(?:EXEC|EXECUTE|SP_EXECUTESQL)\b[^;]*\+|\bPREPARE\b[^;]*\bFROM\b[^;]*\+", Options);
    private static readonly Regex AlterNotNull = new(@"\bALTER\s+TABLE\b[^;]*\bADD\b[^;]*\bNOT\s+NULL\b", Options);
    private static readonly Regex HasDefault = new(@"\bDEFAULT\b", Options);
    private static readonly Regex InsertInto = new(@"\bINSERT\s+INTO\b", Options);
    private static readonly Regex BeginTransaction = new(@"\bBEGIN\s+(?:TRAN|TRANSACTION)\b", Options);
    private static readonly Regex EndTransaction = new(@"\b(?:COMMIT|ROLLBACK)\b", Options);
    private static readonly Regex LeadingWildcardLike = new(@"\b(?:I?LIKE)\s*$", Options);
    private static readonly Regex FirstWord = new(@"\A\s*(?:--[^\n]*\n\s*)*([A-Za-z]+)", Options);
    private static readonly Regex TableReference = new(
        @"\b(?:FROM|JOIN|UPDATE|INSERT\s+INTO|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?)\s+" +
        @"(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*)" +
        @"(?:\.(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*))*)", Options);

    /// <summary>Masked text plus the literals it contained, offsets preserved.</summary>
    private sealed record MaskResult(char[] Masked, List<Literal> Literals, List<(int Offset, string Message)> Lexical);

    private readonly record struct Literal(string Value, int Offset);

    /// <summary>Analyzes one file's text. <paramref name="filePath"/> is only echoed in the findings.</summary>
    public static SqlAnalysis AnalyzeText(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var lineStarts = LineStarts(text);
        var mask = Mask(text);
        var masked = new string(mask.Masked);

        var findings = new List<SqlFinding>();
        foreach (var lexical in mask.Lexical)
        {
            var (line, column) = Position(lineStarts, lexical.Offset);
            findings.Add(new SqlFinding("SQL0001", "error", "Lexical", lexical.Message, filePath, line, column,
                Snippet(text, lineStarts, line),
                lexical.Message.Contains("string", StringComparison.OrdinalIgnoreCase)
                    ? "Close the literal with a single quote ('' escapes one inside it)."
                    : "Close it: everything after an unterminated construct is not read as SQL."));
        }

        var statements = new List<SqlStatementFacts>();
        var verbs = new List<string>();
        var tables = new List<string>();

        foreach (var (start, segment) in Statements(masked))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            var verbMatch = FirstWord.Match(segment);
            var verb = verbMatch.Success ? verbMatch.Groups[1].Value.ToUpperInvariant() : "UNKNOWN";
            var (line, _) = Position(lineStarts, start + Math.Max(0, verbMatch.Success ? verbMatch.Groups[1].Index : 0));
            var statementTables = Tables(segment);
            statements.Add(new SqlStatementFacts(line, verb, statementTables));
            if (!verbs.Contains(verb)) verbs.Add(verb);
            foreach (var table in statementTables)
                if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
                    tables.Add(table);

            var statementText = segment;
            void Add(string rule, string severity, string category, string message, int index, string? fix)
            {
                var (findingLine, findingColumn) = Position(lineStarts, start + index);
                findings.Add(new SqlFinding(rule, severity, category, message, filePath, findingLine, findingColumn,
                    Snippet(text, lineStarts, findingLine), fix));
            }

            if (!ParenthesesBalanced(statementText, out var balanceAt))
                Add("SQL0002", "error", "Correctness", "The parentheses in this statement are unbalanced.",
                    balanceAt, "Balance the parentheses; an unclosed '(' silently changes the statement.");

            var star = SelectStar.Match(statementText);
            if (star.Success)
                Add("SQL0003", "info", "Maintainability", "SELECT * returns every column, including the ones added later.",
                    star.Index, "List the columns you read; a schema change then stops being a silent behaviour change.");

            if (verb is "UPDATE" or "DELETE" && !WhereClause.IsMatch(statementText))
                Add("SQL0004", "warning", "Safety",
                    $"{verb} without a WHERE clause affects every row of the table.",
                    Math.Max(0, verbMatch.Groups[1].Index),
                    verb == "DELETE" ? "Add a WHERE clause; use TRUNCATE when every row is intended."
                                     : "Add a WHERE clause, or the statement rewrites the whole table.");

            var drop = DropStatement.Match(statementText);
            if (drop.Success)
                Add("SQL0005", "info", "Safety", "The DROP has no IF EXISTS, so it fails when the object is absent.",
                    drop.Index, "Add IF EXISTS to make the script re-runnable.");

            var dynamicSql = DynamicSql.Match(statementText);
            if (dynamicSql.Success)
                Add("SQL0006", "warning", "Security",
                    "Dynamic SQL assembled by concatenation is the shape of an injection.",
                    dynamicSql.Index, "Pass parameters (sp_executesql / parameters) or quote identifiers with QUOTENAME.");

            var alter = AlterNotNull.Match(statementText);
            if (alter.Success && !HasDefault.IsMatch(statementText))
                Add("SQL0009", "warning", "Correctness",
                    "A NOT NULL column added without DEFAULT fails when the table already has rows.",
                    alter.Index, "Add a DEFAULT, or add the column nullable, backfill it and then tighten it.");

            if (verb == "INSERT" && InsertInto.IsMatch(statementText) &&
                InsertCounts(statementText) is { } counts && counts.Columns != counts.Values)
                Add("SQL0010", "warning", "Correctness",
                    $"INSERT lists {counts.Columns} column(s) but {counts.Values} value(s).",
                    InsertInto.Match(statementText).Index,
                    "Make the two lists the same length — the mismatch is an error the server rejects at run time.");
        }

        // File-level rules: literals (the wildcard LIKE cannot be seen in the masked text) and
        // transaction balance.
        foreach (var literal in mask.Literals)
        {
            if (!literal.Value.StartsWith('%')) continue;
            var before = masked[Math.Max(0, literal.Offset - 80)..literal.Offset];
            if (!LeadingWildcardLike.IsMatch(before)) continue;

            var (line, column) = Position(lineStarts, literal.Offset);
            findings.Add(new SqlFinding("SQL0008", "info", "Performance",
                "LIKE with a leading wildcard cannot use an index.", filePath, line, column,
                Snippet(text, lineStarts, line),
                "Search a prefix ('abc%'), or use a full-text index when the substring must be anywhere."));
        }

        var begins = BeginTransaction.Matches(masked);
        var ends = EndTransaction.Matches(masked);
        if (begins.Count > ends.Count)
        {
            var first = begins[0];
            var (line, column) = Position(lineStarts, first.Index);
            findings.Add(new SqlFinding("SQL0007", "warning", "Correctness",
                $"{begins.Count} transaction(s) opened but only {ends.Count} committed or rolled back — an open " +
                "transaction holds locks until the connection resets.", filePath, line, column,
                Snippet(text, lineStarts, line), "Close every BEGIN TRAN with COMMIT or ROLLBACK."));
        }

        return new SqlAnalysis(
            Lines: lineStarts.Length,
            Statements: statements.Count,
            Verbs: verbs,
            Tables: tables,
            StatementList: statements,
            Findings: findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList());
    }

    // ── masking ──────────────────────────────────────────────────────────────
    private static MaskResult Mask(string text)
    {
        var chars = text.ToCharArray();
        var literals = new List<Literal>();
        var lexical = new List<(int Offset, string Message)>();
        var index = 0;

        void Blank(int from, int to)
        {
            for (var position = from; position < to && position < chars.Length; position++)
                if (chars[position] != '\n' && chars[position] != '\r') chars[position] = ' ';
        }

        while (index < text.Length)
        {
            var current = text[index];

            // -- line comment
            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                var end = text.IndexOf('\n', index);
                if (end < 0) end = text.Length;
                Blank(index, end);
                index = end;
                continue;
            }

            // /* block comment */
            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    lexical.Add((index, "Unterminated block comment."));
                    Blank(index, text.Length);
                    break;
                }

                Blank(index, end + 2);
                index = end + 2;
                continue;
            }

            // $$ … $$ and $tag$ … $tag$ (PostgreSQL)
            if (current == '$' && DollarTag(text, index) is { } tag)
            {
                var end = text.IndexOf(tag, index + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    lexical.Add((index, "Unterminated dollar-quoted string."));
                    Blank(index, text.Length);
                    break;
                }

                Blank(index, end + tag.Length);
                index = end + tag.Length;
                continue;
            }

            // 'string', with N/E prefixes and '' escapes
            if (current == '\'' || ((current is 'N' or 'n' or 'E' or 'e') && index + 1 < text.Length && text[index + 1] == '\''))
            {
                var start = index;
                var contentStart = current == '\'' ? index + 1 : index + 2;
                var builder = new StringBuilder();
                var position = contentStart;
                var closed = false;

                while (position < text.Length)
                {
                    if (text[position] == '\'')
                    {
                        if (position + 1 < text.Length && text[position + 1] == '\'')
                        {
                            builder.Append('\'');
                            position += 2;
                            continue;
                        }

                        position++;
                        closed = true;
                        break;
                    }

                    builder.Append(text[position]);
                    position++;
                }

                if (!closed) lexical.Add((start, "Unterminated string literal."));
                Blank(start, position);
                literals.Add(new Literal(builder.ToString(), start));
                index = position;
                continue;
            }

            // [bracket identifier] (T-SQL)
            if (current == '[')
            {
                var end = text.IndexOf(']', index + 1);
                if (end < 0)
                {
                    lexical.Add((index, "Unterminated bracket-quoted identifier."));
                    Blank(index, text.Length);
                    break;
                }

                index = end + 1;
                continue;
            }

            // "quoted identifier" — keep the text (it may be a table name), skip the scanner over it
            if (current == '"')
            {
                var position = index + 1;
                while (position < text.Length)
                {
                    if (text[position] == '"')
                    {
                        if (position + 1 < text.Length && text[position + 1] == '"') { position += 2; continue; }
                        position++;
                        break;
                    }

                    position++;
                }

                index = position;
                continue;
            }

            index++;
        }

        return new MaskResult(chars, literals, lexical);
    }

    private static string? DollarTag(string text, int index)
    {
        if (text[index] != '$') return null;
        if (index + 1 < text.Length && text[index + 1] == '$') return "$$";

        var position = index + 1;
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_')) position++;
        if (position == index + 1 || position >= text.Length || text[position] != '$') return null;
        return text[index..(position + 1)];
    }

    private static IEnumerable<(int Start, string Text)> Statements(string masked)
    {
        var start = 0;
        for (var index = 0; index < masked.Length; index++)
        {
            if (masked[index] != ';') continue;
            yield return (start, masked[start..index]);
            start = index + 1;
        }

        if (start < masked.Length) yield return (start, masked[start..]);
    }

    // ── statement facts ──────────────────────────────────────────────────────
    private static List<string> Tables(string statement)
    {
        var tables = new List<string>();
        foreach (Match match in TableReference.Matches(statement))
        {
            var name = match.Groups["table"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var upper = name.ToUpperInvariant();
            if (upper is "SELECT" or "VALUES" or "SET" or "WHERE" or "ON" or "AS" or "WITH" or "LATERAL") continue;
            if (!tables.Contains(name, StringComparer.OrdinalIgnoreCase)) tables.Add(name);
        }

        return tables;
    }

    private static bool ParenthesesBalanced(string statement, out int problemIndex)
    {
        var depth = 0;
        for (var index = 0; index < statement.Length; index++)
        {
            switch (statement[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth < 0)
                    {
                        problemIndex = index;
                        return false;
                    }

                    break;
            }
        }

        problemIndex = depth > 0 ? statement.LastIndexOf('(') : 0;
        return depth == 0;
    }

    private static (int Columns, int Values)? InsertCounts(string statement)
    {
        var insert = InsertInto.Match(statement);
        if (!insert.Success) return null;

        var open = statement.IndexOf('(', insert.Index + insert.Length);
        if (open < 0) return null;
        var close = MatchingClose(statement, open);
        if (close < 0) return null;

        var after = statement[(close + 1)..];
        var values = Regex.Match(after, @"^\s*VALUES\s*\(", Options);
        if (!values.Success) return null;

        var valuesOpen = close + 1 + values.Index + values.Length - 1;
        var valuesClose = MatchingClose(statement, valuesOpen);
        if (valuesClose < 0) return null;

        return (SplitTopLevel(statement[(open + 1)..close]).Count,
                SplitTopLevel(statement[(valuesOpen + 1)..valuesClose]).Count);
    }

    private static int MatchingClose(string text, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '(') depth++;
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0) return index;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '(') depth++;
            else if (text[index] == ')') depth--;
            else if (text[index] == ',' && depth == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        parts.Add(text[start..]);
        return parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
    }

    // ── positions ────────────────────────────────────────────────────────────
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
public sealed record SqlFinding(
    string RuleId, string Severity, string Category, string Message, string File, int Line, int Column,
    string Snippet, string? Fix);

/// <summary>What one statement is and which tables it names.</summary>
public sealed record SqlStatementFacts(int Line, string Verb, IReadOnlyList<string> Tables);

/// <summary>The facts a scan of one file produces.</summary>
public sealed record SqlAnalysis(
    int Lines, int Statements, IReadOnlyList<string> Verbs, IReadOnlyList<string> Tables,
    IReadOnlyList<SqlStatementFacts> StatementList, IReadOnlyList<SqlFinding> Findings);

/// <summary>Discovery and orchestration over the solution's <c>.sql</c> files.</summary>
public interface ISqlService
{
    /// <summary>What the <c>.sql</c> files are: statements, verbs, tables and finding counts.</summary>
    Task<object> InventoryAsync(string? path, int maxFiles);

    /// <summary>The findings of the rule set, filtered by severity and rule id.</summary>
    Task<object> AnalyzeAsync(string? path, string minSeverity, string? rule, int maxFiles, int maxFindings);
}

public sealed class SqlService : ISqlService
{
    private static readonly string[] ExcludedDirectories =
        ["bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build"];

    private readonly IRoslynWorkspace _workspace;

    public SqlService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InventoryAsync(string? path, int maxFiles)
    {
        maxFiles = Math.Clamp(maxFiles, 1, 5_000);
        var (root, files, truncated) = Discover(path, maxFiles);

        var entries = new List<object>();
        var totalStatements = 0;
        var totalFindings = 0;
        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;

            totalStatements += analysis.Statements;
            totalFindings += analysis.Findings.Count;
            entries.Add(new
            {
                file = Relative(root, file),
                lines = analysis.Lines,
                statements = analysis.Statements,
                verbs = analysis.Verbs,
                tableCount = analysis.Tables.Count,
                tables = analysis.Tables.Take(50).ToList(),
                findings = analysis.Findings.Count,
            });
        }

        return new
        {
            root,
            fileCount = entries.Count,
            truncated,
            maxFiles,
            totalStatements,
            totalFindings,
            files = entries,
            hint = "analyze_sql lists the findings themselves; sql_files only says what is there.",
        };
    }

    public async Task<object> AnalyzeAsync(string? path, string minSeverity, string? rule, int maxFiles, int maxFindings)
    {
        var severity = (minSeverity ?? "info").Trim().ToLowerInvariant();
        if (severity is not ("info" or "warning" or "error"))
            return new { error = "minSeverity must be info, warning or error." };

        var ruleId = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim().ToUpperInvariant();
        if (ruleId is not null && !SqlAnalyzer.Rules.ContainsKey(ruleId))
            return new
            {
                error = $"Unknown rule '{rule}'.",
                rules = SqlAnalyzer.Rules.Select(pair => new { id = pair.Key, proves = pair.Value }).ToList(),
            };

        maxFiles = Math.Clamp(maxFiles, 1, 5_000);
        maxFindings = Math.Clamp(maxFindings, 1, 5_000);

        var (root, files, truncated) = Discover(path, maxFiles);
        var findings = new List<SqlFinding>();
        var analyzed = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;
            analyzed++;

            foreach (var finding in analysis.Findings)
            {
                if (Rank(finding.Severity) < Rank(severity)) continue;
                if (ruleId is not null && finding.RuleId != ruleId) continue;
                findings.Add(finding with { File = Relative(root, file) });
            }
        }

        var shown = findings.Take(maxFindings).ToList();
        return new
        {
            root,
            filesAnalyzed = analyzed,
            filesTruncated = truncated,
            minSeverity = severity,
            rule = ruleId,
            findingCount = findings.Count,
            counts = new
            {
                error = findings.Count(finding => finding.Severity == "error"),
                warning = findings.Count(finding => finding.Severity == "warning"),
                info = findings.Count(finding => finding.Severity == "info"),
            },
            byRule = findings.GroupBy(finding => finding.RuleId)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            findings = shown,
            truncated = findings.Count > shown.Count,
            note = "A pattern scan, not a dialect parser: it proves the shape of a statement, never that a " +
                   "schema accepts it.",
        };
    }

    private async Task<SqlAnalysis?> AnalyzeFileAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2_000_000) return null;
            var text = await File.ReadAllTextAsync(file);
            return SqlAnalyzer.AnalyzeText(text, file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private (string Root, List<string> Files, bool Truncated) Discover(string? path, int maxFiles)
    {
        var root = ResolveRoot(path);
        var files = new List<string>();
        var truncated = false;

        if (File.Exists(root))
        {
            files.Add(root);
            return (Path.GetDirectoryName(root) ?? root, files, false);
        }

        if (!Directory.Exists(root))
            return (root, files, false);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".artifacts", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            string[] entries;
            try { entries = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (!string.Equals(Path.GetExtension(entry), ".sql", StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= maxFiles) { truncated = true; break; }
                files.Add(entry);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (root, files, truncated);
    }

    private string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solutionPath = _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return Path.GetDirectoryName(solutionPath)!;

        return Directory.GetCurrentDirectory();
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static int Rank(string severity) => severity switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };
}
