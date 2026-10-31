using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.ContextActions;

namespace VisualMCP.Analysis;

/// <summary>One tool this build advertises: what it is called, how the client sees it, where it lives.</summary>
public sealed record ToolEntry(string Area, string Name, string Implementation, string WireName);

/// <summary>
/// The names this build answers to, read from the same attributes the MCP host reads. One source for
/// <c>capability_report</c> (what EXISTS) and for <c>board_audit</c> (which open cards are already
/// done): a card that names a tool which exists here is a closure candidate, and the two answers can
/// never disagree about the catalogue.
/// </summary>
public static class ToolCatalogue
{
    /// <summary>Every tool of this build, grouped by its area, in a stable order.</summary>
    public static IReadOnlyList<ToolEntry> Entries(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(method =>
                {
                    var declared = method.GetCustomAttribute<McpServerToolAttribute>()?.Name;
                    return new ToolEntry(
                        AreaOf(type.Namespace),
                        declared ?? method.Name,
                        type.Name,
                        declared ?? SnakeCase(method.Name));
                }))
            .OrderBy(tool => tool.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<ToolEntry> Entries() => Entries(typeof(ToolCatalogue).Assembly);

    /// <summary>
    /// The names a card can write and the client can call. A tool with an explicit attribute Name has one
    /// name; one named after its method is also advertised in snake_case (<c>FindSymbol</c> →
    /// <c>find_symbol</c>), so both spellings count.
    /// </summary>
    public static IReadOnlyList<string> ToolNames() => Entries()
        .SelectMany(tool => new[] { tool.Name, tool.WireName })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Everything a card can name and expect to find here. Not every capability is a tool: the ~40
    /// context actions live inside <c>context_action</c> and are named by their id, so a card asking
    /// for "foreach_to_for" is satisfied by an id, not by a tool.
    /// </summary>
    public static IReadOnlyList<string> KnownNames() => ToolNames()
        .Concat(ContextActionService.Catalogue.Select(entry => entry.Id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>"CSharp.Refactoring" for namespace VisualMCP.Tools.CSharp.Refactoring.</summary>
    public static string AreaOf(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "other";
        const string prefix = "VisualMCP.Tools.";
        var trimmed = ns.StartsWith(prefix, StringComparison.Ordinal) ? ns[prefix.Length..] : ns;
        var firstDot = trimmed.IndexOf('.');
        return firstDot < 0 ? trimmed : trimmed[..firstDot];
    }

    /// <summary>
    /// The name the MCP host derives when a tool declares no Name: PascalCase to snake_case, with an
    /// acronym kept together (<c>SignalRConnect</c> → <c>signal_r_connect</c>, <c>GetXmlDocs</c> →
    /// <c>get_xml_docs</c>). Both spellings are accepted; a wrong alias can only add a name, never hide one.
    /// </summary>
    public static string SnakeCase(string name)
    {
        var first = Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1");
        var second = Regex.Replace(first, "(?<=[A-Z])([A-Z])(?=[a-z])", "_$1");
        return second.ToLowerInvariant();
    }
}

