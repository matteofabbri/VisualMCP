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
/// The one exception is explicit and opt-in: <see cref="ExtractSchema"/> reads the DDL the scan
/// itself carries, and a statement is checked against <em>that</em> model — never against a live
/// database, and never against a table the scanned files do not define.
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
        ["SQL0011"] = "A table the statement names is not declared by the DDL in the scanned files",
        ["SQL0012"] = "A column the statement names is not declared on the table it reads",
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

    private static readonly Regex CreateTable = new(
        @"\bCREATE\s+(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>" +
        @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*)(?:\.(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*))*)", Options);

    private static readonly Regex AlterTable = new(
        @"\bALTER\s+TABLE\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*)" +
        @"(?:\.(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*))*)\s+(?<rest>[\s\S]*?)(?=;|\z)", Options);

    private static readonly Regex LeadingIdentifier = new(
        @"^\s*(?<name>\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*)", Options);

    private static readonly Regex CteName = new(@"\b(?:WITH|,)\s*(?<name>[A-Za-z_]\w*)\s+AS\s*\(", Options);
    private static readonly Regex TrailingAlias = new(@"\s+AS\s+[A-Za-z_]\w*\s*$", Options);
    private static readonly Regex FromWithAlias = new(
        @"\bFROM\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*)(?:\.(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_#@][\w#@$]*))*)" +
        @"\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)", Options);

    private static readonly HashSet<string> NonColumnWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AS", "ON", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT",
        "END", "NULL", "TRUE", "FALSE", "DEFAULT", "VALUES", "SET", "FROM", "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "CROSS", "OUTER", "AND", "OR", "NOT", "IN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE",
    };

    private static readonly HashSet<string> ConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "INDEX", "KEY", "EXCLUDE", "LIKE",
    };

    /// <summary>Masked text plus the literals it contained, offsets preserved.</summary>
    private sealed record MaskResult(char[] Masked, List<Literal> Literals, List<(int Offset, string Message)> Lexical);

    private readonly record struct Literal(string Value, int Offset);

    /// <summary>Analyzes one file's text. <paramref name="filePath"/> is only echoed in the findings.</summary>
    public static SqlAnalysis AnalyzeText(string text, string filePath = "(text)", SqlSchema? schema = null)
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

            if (schema is not null && !schema.IsEmpty)
                ValidateAgainstSchema(statementText, verb, statementTables, schema, Add);
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

    // ── schema model ─────────────────────────────────────────────────────────
    /// <summary>
    /// Reads the DDL the scanned text carries: every <c>CREATE TABLE</c> (and the columns an
    /// <c>ALTER TABLE … ADD</c> appends) becomes the model a statement is checked against. It is the
    /// schema of the <em>scan</em>, not of a database: a table the files do not declare stays unknown
    /// and is reported as such, never guessed.
    /// </summary>
    public static void ExtractSchemaInto(SqlSchema schema, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var masked = new string(Mask(text).Masked);

        foreach (Match create in CreateTable.Matches(masked))
        {
            var open = masked.IndexOf('(', create.Index + create.Length);
            if (open < 0) continue;
            if (masked[(create.Index + create.Length)..open].Trim().Length != 0) continue;

            var close = MatchingClose(masked, open);
            if (close < 0) continue;

            var table = schema.Declare(UnquoteTable(create.Groups["table"].Value), LineOf(masked, create.Index));
            foreach (var part in SplitTopLevel(masked[(open + 1)..close]))
            {
                var column = ColumnName(part);
                if (column is not null) table.AddColumn(column);
            }
        }

        foreach (Match alter in AlterTable.Matches(masked))
        {
            var rest = alter.Groups["rest"].Value;
            var add = Regex.Match(rest, @"\bADD\b(?:\s+COLUMN\b)?", Options);
            if (!add.Success) continue;

            var table = schema.Find(UnquoteTable(alter.Groups["table"].Value));
            if (table is null) continue;

            foreach (var part in SplitTopLevel(rest[(add.Index + add.Length)..]))
            {
                var column = ColumnName(part);
                if (column is not null) table.AddColumn(column);
            }
        }
    }

    private static void ValidateAgainstSchema(
        string statement, string verb, List<string> tables, SqlSchema schema, Action<string, string, string, string, int, string?> add)
    {
        var ctes = CteName.Matches(statement).Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var known = new List<SqlSchema.Table>();
        foreach (var name in tables)
        {
            if (IsExternalTableName(name) || ctes.Contains(LastSegment(name))) continue;

            var table = schema.Find(name);
            if (table is null)
            {
                add("SQL0011", "warning", "Correctness",
                    $"The table '{name}' is not declared by the DDL in the scanned files, so the statement is not "
                    + "checked against a schema for it.",
                    IndexOf(statement, name), "Scan the script that creates it, or check the name.");
                continue;
            }

            known.Add(table);
        }

        // A join cannot attribute a bare column to one table, and two SELECTs mean a subquery owns
        // some of the names: in both cases the column check stays silent rather than guessing.
        if (known.Count != 1) return;
        var target = known[0];
        if (target.Columns.Count == 0) return;
        if (Regex.Matches(statement, @"\bSELECT\b", Options).Count > 1) return;

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.Name, LastSegment(target.Name) };
        var fromAlias = FromWithAlias.Match(statement);
        if (fromAlias.Success && !NonColumnWords.Contains(fromAlias.Groups["alias"].Value))
            aliases.Add(fromAlias.Groups["alias"].Value);

        foreach (var (column, index) in CandidateColumns(statement, verb, aliases))
        {
            if (column.Length == 0 || column.StartsWith('@') || NonColumnWords.Contains(column)) continue;
            if (target.HasColumn(column)) continue;

            add("SQL0012", "warning", "Correctness",
                $"'{column}' is not a column of '{target.Name}' as the scanned DDL declares it.",
                index, "Check the name, or add the column to the schema script.");
        }
    }

    /// <summary>The names a statement is likely to treat as columns, for the common statement shapes.</summary>
    private static IEnumerable<(string Column, int Index)> CandidateColumns(
        string statement, string verb, HashSet<string> aliases)
    {
        switch (verb)
        {
            case "SELECT":
            {
                var select = Regex.Match(statement, @"\bSELECT\b", Options);
                var from = Regex.Match(statement, @"\bFROM\b", Options);
                if (!select.Success || !from.Success || from.Index <= select.Index) yield break;

                var projection = statement[(select.Index + select.Length)..from.Index];
                if (projection.Contains('*')) yield break;

                foreach (var part in SplitTopLevel(projection))
                {
                    var text = Regex.Replace(part.Trim(), @"^(?:DISTINCT|ALL|TOP\s+\d+)\s+", string.Empty, Options);
                    if (EndingIdentifier(text, aliases) is { } column) yield return (column, IndexOf(statement, column));
                }

                break;
            }

            case "INSERT":
            {
                var insert = InsertInto.Match(statement);
                if (!insert.Success) yield break;

                var open = statement.IndexOf('(', insert.Index + insert.Length);
                if (open < 0) yield break;
                if (statement[(insert.Index + insert.Length)..open].Trim().Length != 0) yield break;

                var close = MatchingClose(statement, open);
                if (close < 0) yield break;

                foreach (var part in SplitTopLevel(statement[(open + 1)..close]))
                {
                    var identifier = LeadingIdentifier.Match(part.Trim());
                    if (!identifier.Success) continue;
                    var column = Unquote(identifier.Groups["name"].Value);
                    yield return (column, IndexOf(statement, column));
                }

                break;
            }

            case "UPDATE":
            {
                var set = Regex.Match(statement, @"\bSET\b", Options);
                if (!set.Success) yield break;

                var afterSet = statement[(set.Index + set.Length)..];
                var where = Regex.Match(afterSet, @"\bWHERE\b", Options);
                var body = where.Success ? afterSet[..where.Index] : afterSet;

                foreach (var part in SplitTopLevel(body))
                {
                    var equals = part.IndexOf('=');
                    if (equals <= 0) continue;

                    var left = part[..equals].Trim();
                    if (EndingIdentifier(left, aliases) is { } column) yield return (column, IndexOf(statement, column));
                }

                break;
            }
        }
    }

    private static string? EndingIdentifier(string expression, HashSet<string> aliases)
    {
        var text = expression.Trim();
        var alias = TrailingAlias.Match(text);
        if (alias.Success) text = text[..alias.Index].Trim();

        if (text.Length == 0 || text.Contains('(')) return null;

        var match = Regex.Match(text,
            @"(?:(?<q>\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$]*)\s*\.\s*)?(?<c>\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$]*)\s*$",
            Options);
        if (!match.Success) return null;

        if (match.Groups["q"].Success)
        {
            var qualifier = Unquote(match.Groups["q"].Value);
            if (!aliases.Contains(qualifier)) return null;
        }

        return Unquote(match.Groups["c"].Value);
    }

    private static string? ColumnName(string definition)
    {
        var text = definition.Trim();
        if (text.Length == 0) return null;

        var first = LeadingIdentifier.Match(text);
        if (!first.Success) return null;

        var name = Unquote(first.Groups["name"].Value);
        return ConstraintKeywords.Contains(name) ? null : name;
    }

    private static string Unquote(string identifier)
    {
        var text = identifier.Trim();
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        else if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return text.Trim();
    }

    private static string UnquoteTable(string name) =>
        string.Join(".", name.Split('.').Select(Unquote));

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private static bool IsExternalTableName(string name) =>
        name.StartsWith('#') || name.StartsWith('@') ||
        name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(string statement, string text)
    {
        var index = statement.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        return Math.Max(0, index);
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var index = 0; index < offset && index < text.Length; index++)
            if (text[index] == '\n') line++;
        return line;
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

/// <summary>One table the scanned DDL declares.</summary>
public sealed record SqlTableSchema(string Name, string? Schema, IReadOnlyList<string> Columns, int Line);

/// <summary>
/// The schema a scan carries: the <c>CREATE TABLE</c> statements (and <c>ALTER TABLE … ADD</c>
/// columns) of the files it reads. It is what makes SQL0011/SQL0012 possible without a database —
/// and the reason they say "not declared by the scanned DDL" rather than "does not exist".
/// </summary>
public sealed class SqlSchema
{
    internal sealed class Table(string name, string? schemaName, int line)
    {
        private readonly HashSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _ordered = [];

        public string Name { get; } = name;
        public string? SchemaName { get; } = schemaName;
        public int Line { get; } = line;
        public IReadOnlyList<string> Columns => _ordered;

        public void AddColumn(string column)
        {
            if (column.Length == 0 || !_columns.Add(column)) return;
            _ordered.Add(column);
        }

        public bool HasColumn(string column) => _columns.Contains(column);

        public SqlTableSchema Snapshot() => new(Name, SchemaName, _ordered, Line);
    }

    private readonly Dictionary<string, Table> _byFull = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Table> _bySimple = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Table> _tables = [];

    public bool IsEmpty => _tables.Count == 0;
    public int TableCount => _tables.Count;
    public int ColumnCount => _tables.Sum(table => table.Columns.Count);
    public IReadOnlyList<SqlTableSchema> Tables => _tables.Select(table => table.Snapshot()).ToList();

    internal Table Declare(string fullName, int line)
    {
        if (_byFull.TryGetValue(fullName, out var existing)) return existing;

        var simple = SimpleName(fullName);
        var schemaName = fullName.Contains('.') ? fullName[..fullName.LastIndexOf('.')] : null;

        var table = new Table(simple, schemaName, line);
        _byFull[fullName] = table;

        if (_bySimple.TryGetValue(simple, out var other) && !ReferenceEquals(other, table))
            _ambiguous.Add(simple);
        else
            _bySimple[simple] = table;

        _tables.Add(table);
        return table;
    }

    internal Table? Find(string rawName)
    {
        var full = string.Join(".", rawName.Split('.').Select(UnquotePart));
        if (_byFull.TryGetValue(full, out var direct)) return direct;

        var simple = SimpleName(full);
        if (_ambiguous.Contains(simple)) return null;
        return _bySimple.TryGetValue(simple, out var bySimple) ? bySimple : null;
    }

    private static string SimpleName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName[(dot + 1)..];
    }

    private static string UnquotePart(string part)
    {
        var text = part.Trim();
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        else if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return text.Trim();
    }
}

/// <summary>Discovery and orchestration over the solution's <c>.sql</c> files.</summary>
public interface ISqlService
{
    /// <summary>What the <c>.sql</c> files are: statements, verbs, tables and finding counts.</summary>
    Task<object> InventoryAsync(string? path, int maxFiles);

    /// <summary>The findings of the rule set, filtered by severity and rule id.</summary>
    Task<object> AnalyzeAsync(string? path, string minSeverity, string? rule, int maxFiles, int maxFindings,
        string? schema);
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

    public async Task<object> AnalyzeAsync(string? path, string minSeverity, string? rule, int maxFiles, int maxFindings,
        string? schema = "auto")
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

        var schemaMode = string.IsNullOrWhiteSpace(schema) ? "auto" : schema.Trim().ToLowerInvariant();
        if (schemaMode is not ("auto" or "off"))
            return new { error = "schema must be auto (validate against the scanned DDL) or off." };
        var useSchema = schemaMode == "auto";

        maxFiles = Math.Clamp(maxFiles, 1, 5_000);
        maxFindings = Math.Clamp(maxFindings, 1, 5_000);

        var (root, files, truncated) = Discover(path, maxFiles);
        var model = new SqlSchema();
        var texts = new List<(string File, string Text)>();

        foreach (var file in files)
        {
            var text = await ReadTextAsync(file);
            if (text is null) continue;
            texts.Add((file, text));
            if (useSchema) SqlAnalyzer.ExtractSchemaInto(model, text);
        }

        var findings = new List<SqlFinding>();
        var analyzed = 0;

        foreach (var (file, text) in texts)
        {
            var analysis = SqlAnalyzer.AnalyzeText(text, file, useSchema ? model : null);
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
            schema = new
            {
                enabled = useSchema,
                tableCount = model.TableCount,
                columnCount = model.ColumnCount,
                tables = model.Tables.Take(200).ToList(),
                note = useSchema
                    ? "SQL0011/SQL0012 are checked against the DDL the scanned files declare, not against a database. " +
                      "A table the scan does not define is reported as undeclared, never as invalid."
                    : "Schema validation is off: only the lexical and pattern rules ran.",
            },
            findings = shown,
            truncated = findings.Count > shown.Count,
            note = "A pattern scan, not a dialect parser: it proves the shape of a statement. When the scanned files " +
                   "carry their DDL, the table and column names are also checked against that model (SQL0011/SQL0012).",
        };
    }

    private async Task<SqlAnalysis?> AnalyzeFileAsync(string file)
    {
        var text = await ReadTextAsync(file);
        return text is null ? null : SqlAnalyzer.AnalyzeText(text, file);
    }

    private static async Task<string?> ReadTextAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2_000_000) return null;
            return await File.ReadAllTextAsync(file);
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
