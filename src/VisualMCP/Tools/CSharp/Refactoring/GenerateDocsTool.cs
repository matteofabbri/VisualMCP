using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class GenerateDocsTool
{
    [McpServerTool(Name = "generate_docs"), Description(
        "ReSharper's 'Generate XML documentation' (GhostDoc style): writes the <summary> plus the " +
        "<param>/<typeparam>/<returns> entries a declaration is missing, with a sentence derived from " +
        "the name — 'CalculateTotal' becomes 'Calculates total.'. It never touches a declaration that " +
        "already has a doc comment and only inserts comment lines. With no symbolName it documents every " +
        "undocumented public type in the file (and, with includeMembers, their public members). Dry run " +
        "by default.")]
    public static Task<object> GenerateDocs(
        IGenerateDocsService svc,
        [Description("The C# file to document")] string filePath,
        [Description("Optional: the type or member to document; omit to cover the whole file")] string? symbolName = null,
        [Description("When symbolName is a type, also document its public members (default true)")] bool includeMembers = true,
        [Description("Preview only (default true). Pass false to write the file.")] bool dryRun = true)
        => svc.GenerateDocs(filePath, symbolName, includeMembers, dryRun);
}
