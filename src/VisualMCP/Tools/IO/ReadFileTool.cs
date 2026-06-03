using ModelContextProtocol.Server;
using System.ComponentModel;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ReadFileTool
{
    [McpServerTool, Description(
        "Read a source file (optionally a line range) and return its content with line numbers. " +
        "For locating symbols or their usages, prefer find_symbol / find_references / find_callers over reading and scanning files by hand — " +
        "they understand the code semantically. Use this when you simply need to see a file's text.")]
    public static object ReadFile(
        [Description("Absolute or relative path to the file")] string path,
        [Description("First line to read (1-based, optional)")] int? fromLine = null,
        [Description("Last line to read (1-based, optional)")] int? toLine = null)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var lines = File.ReadAllLines(path);
        int start = Math.Max(0, (fromLine ?? 1) - 1);
        int end = Math.Min(lines.Length, toLine ?? lines.Length);

        var numbered = lines[start..end]
            .Select((l, i) => $"{start + i + 1,5}: {l}")
            .ToList();

        return new
        {
            path,
            totalLines = lines.Length,
            returnedLines = numbered.Count,
            content = string.Join("\n", numbered)
        };
    }
}
