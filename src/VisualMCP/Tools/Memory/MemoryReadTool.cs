using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Memory;

namespace VisualMCP.Tools.Memory;

[McpServerToolType]
public static class MemoryReadTool
{
    [McpServerTool(Name = "memory_read"), Description(
        "Read the agent's persistent memory for the current project (the MEMORY.md index and topic .md files " +
        "under ~/.claude/projects/<slug>/memory/). Call at the start of work to recall prior context. Omit " +
        "'file' to get the index plus all memory files; pass 'file' to read one. Read-only.")]
    public static object MemoryRead(
        [Description("Optional: a specific memory file name (e.g. 'MEMORY.md' or 'build-notes'). Omit for the index + all files.")] string? file = null,
        [Description("Optional: project root path. Defaults to the server's current project directory.")] string? projectPath = null)
        => MemoryImpl.Read(file, projectPath);
}
