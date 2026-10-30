using ModelContextProtocol.Protocol;

namespace VisualMCP.Formatting;

/// <summary>
/// Results that ARE code or text (file contents, matches, symbol source) must reach the model
/// as a text block, not as a JSON-serialized object. Serializing them escapes every newline
/// and quote, so the model reads one long escaped line instead of source code.
/// </summary>
internal static class ToolText
{
    /// <summary>Upper bound for a single tool result; beyond this the model is told to narrow the query.</summary>
    internal const int MaxChars = 200_000;

    public static CallToolResult Ok(string text) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = Cap(text) } },
    };

    public static CallToolResult Fail(string message) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
        IsError = true,
    };

    private static string Cap(string text) => text.Length <= MaxChars
        ? text
        : text[..MaxChars] + "\n… output truncated (" + text.Length + " chars): narrow the query or the line range.";
}
