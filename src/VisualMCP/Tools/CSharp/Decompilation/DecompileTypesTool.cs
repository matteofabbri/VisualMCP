using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DecompileTypesTool
{
    [McpServerTool(Name = "decompile_types"), Description(
        "Decompile SEVERAL types of one assembly in ONE call: each type comes back as C# from the ILSpy engine. Use this " +
        "INSTEAD OF calling decompile_type once per type — reading an implementation that spans several types (an " +
        "interface plus its implementations, a facade plus its helpers) is a single round-trip. A type the engine cannot " +
        "read is reported in place and the others still come back. The C# is compacted by default (indentation, line " +
        "breaks and comments dropped, same tokens). Read-only; metadata + IL only.")]
    public static object DecompileTypes(
        IDecompileService decompile,
        [Description("Path to the .dll containing the types.")] string assemblyPath,
        [Description("Fully-qualified type names, e.g. ['System.Text.Json.JsonSerializer', 'System.Text.Json.JsonDocument'].")]
        string[] typeFullNames,
        [Description("Maximum characters per type (default: 20000).")] int maxChars = 20_000,
        [Description("Strip indentation, line breaks and comments (default: true). Pass false for ILSpy's readable formatting.")]
        bool compact = true)
    {
        if (typeFullNames is null || typeFullNames.Length == 0)
            return new { error = "decompile_types needs at least one type name." };

        var results = typeFullNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .Select(name => new { typeFullName = name.Trim(), result = decompile.DecompileType(assemblyPath, name.Trim(), maxChars, compact) })
            .ToList();

        return new { assemblyPath, count = results.Count, types = results };
    }
}
