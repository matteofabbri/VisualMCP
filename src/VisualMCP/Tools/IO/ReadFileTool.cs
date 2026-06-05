using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.IO;

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
        => IoImpl.ReadFile(path, fromLine, toLine);
}
