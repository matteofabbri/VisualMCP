using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.IO;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class MovePathTool
{
    [McpServerTool(Name = "move_path"), Description(
        "Move or rename a file or directory (System.IO File.Move / Directory.Move), creating the destination's " +
        "parent folders as needed. Use this INSTEAD OF a shell 'mv' / 'Move-Item' / 'git mv'. Language-agnostic. " +
        "NOTE: this is a physical move only — for renaming a C# symbol/type/namespace and updating all " +
        "references, use apply_rename (semantic, ReSharper-style).")]
    public static object MovePath(
        [Description("Source file or directory path.")] string source,
        [Description("Destination path (new location/name).")] string destination,
        [Description("Overwrite an existing destination FILE (default: false). Directory moves never overwrite.")] bool overwrite = false)
        => IoImpl.MovePath(source, destination, overwrite);
}
