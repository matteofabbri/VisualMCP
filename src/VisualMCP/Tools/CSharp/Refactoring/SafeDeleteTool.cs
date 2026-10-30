using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class SafeDeleteTool
{
    [McpServerTool, Description(
        "When you want to remove a type/method/property/field, use this INSTEAD OF deleting by hand: it first verifies via Roslyn that the symbol has zero references in the solution, " +
        "and refuses (listing every reference) if any exist — the ReSharper Safe Delete behaviour that prevents breaking the build. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> SafeDelete(
        ISafeDeleteService svc,
        [Description("Symbol name to delete (class, method, property, field, etc.)")] string symbolName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null,
        [Description("Dry run — verify safety without deleting (default: false)")] bool dryRun = false)
        => svc.SafeDelete(symbolName, containingType, dryRun);
}
