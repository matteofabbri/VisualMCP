using System.Text.RegularExpressions;

namespace Snitch.Execution;

/// <summary>
/// Edits one MSBuild property inside a project file or a shared props file, as text: the value is
/// replaced where it is already written, an unconditioned <c>PropertyGroup</c> receives the new
/// element when it is not, and nothing else in the file is reformatted. A property that only exists
/// under a <c>Condition</c> is reported instead of being rewritten, because guessing which condition
/// the caller meant would silently change how the project evaluates.
/// </summary>
public static class MsBuildPropertyFile
{
    private static readonly Regex GroupPattern = new(
        "<PropertyGroup(?<attrs>\\s[^>]*)?>(?<body>.*?)</PropertyGroup>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant);

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>
    /// Sets <paramref name="propertyName"/> to <paramref name="value"/>. Returns false (with
    /// <paramref name="note"/> explaining why) when the property exists only under a condition.
    /// </summary>
    public static bool TrySet(
        string text, string propertyName, string value, out string updated, out string? previous, out string? note)
    {
        updated = text;
        previous = null;
        note = null;

        if (!IsValidName(propertyName)) { note = $"'{propertyName}' is not a valid MSBuild property name."; return false; }

        var escaped = Escape(value);
        var element = $"{propertyName}>{escaped}</{propertyName}>";
        var conditionedMatch = (Match?)null;

        foreach (Match group in GroupPattern.Matches(text).Cast<Match>())
        {
            var condition = group.Groups["attrs"].Value;
            var body = group.Groups["body"];
            var existing = FindElement(body.Value, propertyName);
            if (!existing.Success) continue;

            var isConditioned = condition.Contains("Condition", StringComparison.OrdinalIgnoreCase);
            if (isConditioned) { conditionedMatch = existing; continue; }

            previous = ElementValue(existing);

            var absolute = body.Index + existing.Index;
            var replacement = existing.Groups["selfClosing"].Success
                ? $"<{element}"
                : $"<{propertyName}{(existing.Groups["attrs"].Success ? existing.Groups["attrs"].Value : "")}>{escaped}</{propertyName}>";

            updated = string.Concat(text.AsSpan(0, absolute), replacement, text.AsSpan(absolute + existing.Length));
            return true;
        }

        if (conditionedMatch is not null)
        {
            note = $"'{propertyName}' is defined only inside a PropertyGroup with a Condition; set it there by hand, " +
                   "because choosing a condition for the caller would change how the project evaluates.";
            return false;
        }

        updated = Insert(text, propertyName, escaped, out previous);
        return true;
    }

    /// <summary>Removes the property from the first unconditioned group that declares it.</summary>
    public static bool TryRemove(string text, string propertyName, out string updated, out string? removed, out string? note)
    {
        updated = text;
        removed = null;
        note = null;

        if (!IsValidName(propertyName)) { note = $"'{propertyName}' is not a valid MSBuild property name."; return false; }

        foreach (Match group in GroupPattern.Matches(text).Cast<Match>())
        {
            if (group.Groups["attrs"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase)) continue;

            var body = group.Groups["body"];
            var existing = FindElement(body.Value, propertyName);
            if (!existing.Success) continue;

            removed = ElementValue(existing);
            updated = RemoveElement(text, body.Index + existing.Index, existing.Length);
            return true;
        }

        note = $"'{propertyName}' is not written in an unconditioned PropertyGroup of this file.";
        return false;
    }

    // ── internal shape ───────────────────────────────────────────────────────

    private static Match FindElement(string body, string propertyName)
    {
        var name = Regex.Escape(propertyName);
        return Regex.Match(
            body,
            $@"<{name}(?<attrs>\s[^>]*)?>(?<value>[^<]*)</{name}>|<{name}(?<attrs>\s[^>]*)?(?<selfClosing>\s*/>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ElementValue(Match element) =>
        element.Groups["value"].Success ? element.Groups["value"].Value : null;

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Insert(string text, string propertyName, string escapedValue, out string? previous)
    {
        previous = null;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var unit = DetectIndentUnit(text);
        var element = $"<{propertyName}>{escapedValue}</{propertyName}>";

        // The first unconditioned group receives it, at the end of its body: the shape of the file
        // stays what its author wrote.
        foreach (Match group in GroupPattern.Matches(text).Cast<Match>())
        {
            if (group.Groups["attrs"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase)) continue;

            var body = group.Groups["body"];
            var groupIndent = IndentOfLine(text, group.Index);
            var childIndent = DetectChildIndent(body.Value, groupIndent, unit);

            var trimmed = body.Value.TrimEnd(' ', '\t', '\r', '\n');
            var insertAt = body.Index + trimmed.Length;

            // Whatever separated the last element from the closing tag belongs after the new line.
            var closingWhitespace = body.Value[trimmed.Length..];
            if (!closingWhitespace.Contains('\n')) closingWhitespace = newline + groupIndent;

            var insertion = newline + childIndent + element + closingWhitespace;
            return string.Concat(text.AsSpan(0, insertAt), insertion, text.AsSpan(insertAt));
        }

        var rootIndent = DetectIndentUnit(text);
        var block = newline +
                    rootIndent + "<PropertyGroup>" + newline +
                    rootIndent + unit + element + newline +
                    rootIndent + "</PropertyGroup>" + newline;

        var close = text.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        if (close < 0) close = text.Length;
        else close = text.LastIndexOf('\n', close - 1) + 1;

        return string.Concat(text.AsSpan(0, close), block, text.AsSpan(close));
    }

    private static string RemoveElement(string text, int start, int length)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var prefix = text.AsSpan(lineStart, start - lineStart);
        var ownLine = prefix.Trim(" \t\r").Length == 0;

        if (!ownLine) return text.Remove(start, length);

        var end = start + length;
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return text.Remove(lineStart, end - lineStart);
    }

    private static string IndentOfLine(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var line = text[lineStart..index];
        return line.All(c => c is ' ' or '\t') ? line.TrimEnd('\r') : "";
    }

    private static string DetectChildIndent(string body, string groupIndent, string unit)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t', '\r');
            if (trimmed.Length == 0 || !trimmed.StartsWith('<') && !trimmed.StartsWith('!')) continue;

            var indent = line[..(line.Length - trimmed.Length + (line.EndsWith('\r') ? 1 : 0))];
            if (indent.Length > groupIndent.Length) return indent;
        }

        return groupIndent + unit;
    }

    private static string DetectIndentUnit(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t', '\r');
            if (trimmed.Length == 0 || trimmed == line || !trimmed.StartsWith('<')) continue;

            var spaces = line[..(line.Length - trimmed.Length)].Count(c => c == ' ');
            if (spaces is >= 1 and <= 8) return new string(' ', spaces);
        }

        return "  ";
    }
}
