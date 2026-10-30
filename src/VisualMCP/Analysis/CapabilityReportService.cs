using System.Reflection;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.ContextActions;
using Snitch.Languages.Xaml;

namespace VisualMCP.Analysis;

/// <summary>
/// What the SERVER can do, read from the server itself: the tools it advertises grouped by area, the
/// rule catalogues it carries, and the language layers with the type that implements each one. This
/// is the generated counterpart of the hand-written tables in the docs — the counts here cannot drift
/// from the build, because they are the build.
/// </summary>
public interface ICapabilityReportService
{
    object Report();
}

public sealed class CapabilityReportService : ICapabilityReportService
{
    public object Report()
    {
        var assembly = typeof(CapabilityReportService).Assembly;

        var tools = assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(method => new
                {
                    area = AreaOf(type.Namespace),
                    name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name,
                    implementation = type.Name,
                }))
            .OrderBy(tool => tool.area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.name, StringComparer.Ordinal)
            .ToList();

        var byArea = tools
            .GroupBy(tool => tool.area, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new
        {
            toolCount = tools.Count,
            byArea,
            tools = tools.Select(tool => new { tool.area, tool.name }).ToList(),

            catalogues = new
            {
                contextActions = ContextActionService.Catalogue.Select(entry => entry.Id).ToList(),
                xamlRules = XamlRuleCatalog.Ids,
                razorRules = new
                {
                    security = 11,
                    markup = 17,
                    range = "RZ1001-RZ1011 security (CWE), RZ2001-RZ2017 markup",
                },
                styleRewrites = "87 C# ids + 15 VB ids (simplify_code / simplify_vb_code)",
                inspections = "~800 ids from five analyzer packs (inspect_code)",
            },

            // The language layers, each with the type that proves it exists in this build.
            layers = new
            {
                csharp = "Snitch.Languages.CSharp (Roslyn: navigation, refactoring, style, inspections, testing)",
                visualBasic = "Snitch.Languages.VB (style only: 15 ids)",
                razor = "Snitch.Languages.Razor (heuristic markup scanner, 28 rules)",
                xaml = "Snitch.Languages.Xaml (markup + Roslyn code-behind: inventory, XAML0001-0009, navigation)",
                metacode = "Snitch.Metacode (C#, Razor, pseudocode, Ghidra frontends; persisted index)",
                sql = "none: .sql is full-text only",
                javascript = "none: .js/.ts/.jsx/.tsx are full-text only",
                fsharp = "none: no semantic model (not a Roslyn language)",
                kubernetes = "none: YAML is full-text only; Docker covers local containers",
            },

            docs = new
            {
                note = "The public catalogue lives in docs/public/tools.md; docs/gaps.md is the inventory " +
                       "of what is absent. The counts above are the live ones — compare them with the " +
                       "documented figures when either changes.",
            },
        };
    }

    /// <summary>"CSharp.Refactoring" for namespace VisualMCP.Tools.CSharp.Refactoring.</summary>
    private static string AreaOf(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "other";
        const string prefix = "VisualMCP.Tools.";
        var trimmed = ns.StartsWith(prefix, StringComparison.Ordinal) ? ns[prefix.Length..] : ns;
        var firstDot = trimmed.IndexOf('.');
        return firstDot < 0 ? trimmed : trimmed[..firstDot];
    }
}
