namespace Snitch.Platform;

/// <summary>
/// A compact line diff for tool previews: the common prefix and suffix are dropped and only the
/// changed lines are printed. It is what a caller needs to recognise an edit, not a patch format —
/// enough to see the change, capped so one result cannot flood a response.
/// </summary>
public static class TextDiff
{
    public static string Lines(string oldText, string newText, int maxLines = 12)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var removed = oldLines.Length - suffix - prefix;
        var added = newLines.Length - suffix - prefix;

        var builder = new System.Text.StringBuilder();
        builder.Append("@@ line ").Append(prefix + 1).AppendLine(" @@");
        for (var i = 0; i < Math.Min(removed, maxLines); i++)
            builder.Append("- ").Append(oldLines[prefix + i].TrimEnd('\r')).AppendLine();
        for (var i = 0; i < Math.Min(added, maxLines); i++)
            builder.Append("+ ").Append(newLines[prefix + i].TrimEnd('\r')).AppendLine();
        if (removed > maxLines || added > maxLines)
            builder.Append("… (preview truncated)");

        return builder.ToString().TrimEnd('\n');
    }
}
