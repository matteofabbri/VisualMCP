using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.IO;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ReadFileTool
{
    [McpServerTool, Description(
        "Read a source file (optionally a line range) and return its content with line numbers. " +
        "For locating symbols or their usages, prefer find_symbol / find_references / find_callers over reading and scanning files by hand — " +
        "they understand the code semantically. If you want the BODY of a declaration, read_symbol returns it directly. " +
        "If you are reading a file because a search told you where something is, use search_code instead: same search, but the hit " +
        "comes back already resolved to the declaration that contains it. " +
        "Use this when you simply need to see a file's text. To read several known files at once, use read_files.")]
    public static CallToolResult ReadFile(
        IIoService io,
        [Description("Absolute or relative path to the file")] string path,
        [Description("First line to read (1-based, optional)")] int? fromLine = null,
        [Description("Last line to read (1-based, optional)")] int? toLine = null)
    {
        var result = io.ReadFile(path, fromLine, toLine);
        if (result.Error is not null) return ToolText.Fail(result.Error);

        var start = Math.Max(1, fromLine ?? 1);
        int returned = result.ReturnedLines ?? 0;
        int total = result.TotalLines ?? 0;
        var range = returned == 0
            ? $"no lines in range; the file has {total} lines"
            : $"lines {start}-{start + returned - 1} of {total}";

        return ToolText.Ok($"{result.Path} ({range})\n\n{result.Content}");
    }
}
