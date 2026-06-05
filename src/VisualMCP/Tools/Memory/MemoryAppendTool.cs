using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Memory;

namespace VisualMCP.Tools.Memory;

[McpServerToolType]
public static class MemoryAppendTool
{
    [McpServerTool(Name = "memory_append"), Description(
        "Append text to a memory file for the current project (creating it if needed), under " +
        "~/.claude/projects/<slug>/memory/. Use this to add a note/fact without rewriting the whole file. " +
        "A newline is inserted before the appended content if the file doesn't end with one.")]
    public static object MemoryAppend(
        [Description("Memory file name (e.g. 'MEMORY.md' or 'decisions'); '.md' is added if missing.")] string file,
        [Description("Text to append.")] string content,
        [Description("Optional: project root path. Defaults to the server's current project directory.")] string? projectPath = null)
        => MemoryImpl.Append(file, content, projectPath);
}
