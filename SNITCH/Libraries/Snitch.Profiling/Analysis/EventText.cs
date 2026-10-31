using System.Text;
using System.Text.RegularExpressions;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// Small shared helpers for reading event names and SQL text. They live together because the three
/// provider analyzers (async, database, instrumentation) all compare event names the same way: the
/// name TraceEvent reports is <c>TaskName/OpcodeName</c> for a typed event and just the name for a
/// dynamically-created one, so punctuation and case have to be normalised before matching.
/// </summary>
internal static class EventText
{
    private static readonly Regex QuotedString = new(@"'(?:[^']|'')*'", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\w@.])-?\d+(?:\.\d+)?(?![\w.])", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex InList = new(
        @"\bIN\s*\(\s*\?(?:\s*,\s*\?){2,}\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SqlVerbs =
    [
        "select", "insert", "update", "delete", "merge", "exec", "execute", "with", "begin",
        "commit", "rollback", "create", "alter", "drop", "truncate", "pragma", "declare",
        "call", "values", "upsert",
    ];

    /// <summary>The event name with everything but letters and digits removed, lower-cased.</summary>
    public static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Whether a payload value is a SQL statement rather than a message or a command type.</summary>
    public static bool LooksLikeSql(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.TrimStart();
        if (trimmed.Length < 10) return false;

        foreach (var verb in SqlVerbs)
        {
            if (trimmed.StartsWith(verb, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == verb.Length || !char.IsLetter(trimmed[verb.Length])))
            {
                return true;
            }
        }

        return trimmed.Contains(" from ", StringComparison.OrdinalIgnoreCase) &&
               (trimmed.Contains(" select ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("select ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shape of a statement: literals replaced by <c>?</c> and whitespace collapsed, so the two
    /// executions of the same query with different arguments group together — which is what an N+1
    /// signal needs. Parameters (<c>@id</c>) are kept: the parameter list is part of the shape.
    /// </summary>
    public static string SqlShape(string text, int maxChars = 240)
    {
        var shape = Whitespace.Replace(text, " ").Trim();
        shape = QuotedString.Replace(shape, "?");
        shape = Number.Replace(shape, "?");
        shape = InList.Replace(shape, "IN (?)");
        return Truncate(shape, maxChars);
    }

    public static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
