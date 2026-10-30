using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Reflection;

namespace VisualMCP.Tools.CSharp.Reflection;

[McpServerToolType]
public static class InspectAssembliesTool
{
    [McpServerTool(Name = "inspect_assemblies"), Description(
        "Inspect SEVERAL compiled .NET assemblies in ONE call: the public API of each (types, base types, interfaces and " +
        "members with readable signatures), read from metadata without loading or locking the files. Use this INSTEAD OF " +
        "calling inspect_assembly once per assembly — a set of related DLLs (a framework's split assemblies, a package " +
        "plus its dependencies, the output folder of a build) is one round-trip. An assembly that cannot be read comes " +
        "back as an error entry and the rest still succeed. For a single assembly inspect_assembly is equivalent.")]
    public static object InspectAssemblies(
        IInspectAssemblyService inspect,
        [Description("Paths to the .dll/.exe files to inspect.")] string[] paths,
        [Description("Optional: only include types whose full name contains this substring (case-insensitive).")] string? typeFilter = null,
        [Description("Include each type's members (default: true). Set false for types-only outlines.")] bool includeMembers = true,
        [Description("Maximum types per assembly (default: 200).")] int maxTypes = 200)
    {
        if (paths is null || paths.Length == 0)
            return new { error = "inspect_assemblies needs at least one assembly path." };

        var results = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(path => new { path, result = inspect.Run(path.Trim(), typeFilter, includeMembers, maxTypes) })
            .ToList();

        return new
        {
            count = results.Count,
            assemblies = results,
            omitted = Math.Max(0, paths.Count(path => !string.IsNullOrWhiteSpace(path)) - results.Count),
        };
    }
}
