using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class CopyTypeTool
{
    [McpServerTool(Name = "copy_type"), Description(
        "Duplicate a type under a new name (Rider's 'Copy type'): the declaration is copied right after the original with " +
        "its constructors renamed, so the copy compiles. References inside the copy keep pointing at the original — this " +
        "is a starting point for a divergent type, not a rename. GATED on the compiler: nothing is written if the copy " +
        "would add errors (e.g. the name is already taken in that scope). dryRun defaults to true.")]
    public static Task<object> CopyType(
        ICopyTypeService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Name of the type to copy.")] string typeName,
        [Description("Name for the copy.")] string newName,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.CopyType(filePath, typeName, newName, dryRun);
}
