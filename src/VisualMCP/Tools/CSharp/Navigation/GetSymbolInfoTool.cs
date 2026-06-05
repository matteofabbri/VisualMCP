using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class GetSymbolInfoTool
{
    [McpServerTool, Description(
        "When you have a file + line (and optional column) and need to know exactly what symbol is there — its resolved type, kind and docs (the Visual Studio hover) — use this INSTEAD OF inferring it from surrounding text. " +
        "Roslyn's semantic model gives the true binding, including the inferred type of 'var' and overload resolution. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> GetSymbolInfo(
        [Description("Absolute path to the source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based, default: 1)")] int column = 1)
        => NavigationImpl.GetSymbolInfoAsync(filePath, line, column);
}
