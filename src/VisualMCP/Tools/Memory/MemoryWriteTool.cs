using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Memory;

namespace VisualMCP.Tools.Memory;

[McpServerToolType]
public static class MemoryWriteTool
{
    [McpServerTool(Name = "memory_write"), Description(
        "Write (create or overwrite) a memory file for the current project, under " +
        "~/.claude/projects/<slug>/memory/. Use this INSTEAD OF editing the memory files directly (which the " +
        "editor flags as sensitive). Use 'MEMORY.md' for the index, or a topic name for a note. The plugin " +
        "creates the memory directory if needed.")]
    public static object MemoryWrite(
        [Description("Memory file name (e.g. 'MEMORY.md' or 'architecture'); '.md' is added if missing.")] string file,
        [Description("Full content to write to the file.")] string content,
        [Description("Optional: project root path. Defaults to the server's current project directory.")] string? projectPath = null)
        => MemoryImpl.Write(file, content, projectPath);
}
