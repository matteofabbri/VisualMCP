using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Sql;

namespace VisualMCP.Tools.Sql;

[McpServerToolType]
public static class SqlTools
{
    [McpServerTool(Name = "sql_files"), Description(
        "What the solution's SQL is: every .sql file under the solution (or under `path`), with its line and " +
        "statement counts, the statement verbs (SELECT/INSERT/UPDATE/DELETE/DDL), the tables it names and how many " +
        "findings it carries. Use it to find the schema scripts before reading them; analyze_sql lists the findings " +
        "themselves. Build output, node_modules and VCS folders are skipped. Read-only.")]
    public static Task<object> SqlFiles(
        ISqlService sql,
        [Description("A .sql file or a directory (default: the solution directory).")] string? path = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500)
        => sql.InventoryAsync(path, maxFiles);

    [McpServerTool(Name = "analyze_sql"), Description(
        "Pattern-based SQL diagnostics over the solution's .sql files — the SQL half of analyze_razor_pages/" +
        "analyze_xaml. Rules: SQL0001 unterminated string/comment/bracket, SQL0002 unbalanced parentheses, SQL0003 " +
        "SELECT *, SQL0004 UPDATE/DELETE without WHERE, SQL0005 DROP without IF EXISTS, SQL0006 dynamic SQL by " +
        "concatenation (injection shape), SQL0007 a transaction never closed, SQL0008 LIKE with a leading wildcard, " +
        "SQL0009 ADD … NOT NULL without DEFAULT, SQL0010 INSERT column/value count mismatch, and — when the scanned " +
        "files carry their DDL — SQL0011 a table the scan does not declare and SQL0012 a column the declared table " +
        "does not have. Every finding has rule, severity, file:line:column, the line and a fix. It masks comments " +
        "and literals first, so a keyword inside a comment or a string does not fire. A pattern scanner, not a " +
        "dialect parser: the schema it checks is the one the scan reads, never a live database. Read-only.")]
    public static Task<object> AnalyzeSql(
        ISqlService sql,
        [Description("A .sql file or a directory (default: the solution directory).")] string? path = null,
        [Description("Minimum severity: info (default), warning or error.")] string minSeverity = "info",
        [Description("Only this rule id, e.g. SQL0004.")] string? rule = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500,
        [Description("Maximum findings to return (default 200; the total is always reported).")] int maxFindings = 200,
        [Description("auto (default: check names against the scanned CREATE TABLE/ALTER TABLE DDL) or off.")] string schema = "auto")
        => sql.AnalyzeAsync(path, minSeverity, rule, maxFiles, maxFindings, schema);
}
