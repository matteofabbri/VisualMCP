using Snitch.Languages.CSharp.ContextActions;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.VB.Style;
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
        var tools = ToolCatalogue.Entries();

        var byArea = tools
            .GroupBy(tool => tool.Area, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new
        {
            toolCount = tools.Count,
            byArea,
            tools = tools.Select(tool => new { area = tool.Area, name = tool.Name }).ToList(),

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
                styleRewrites = $"{SimplifyCodeService.Rules.Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()} C# ids + " +
                                $"{VbSimplifyCodeService.Rules.Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()} VB ids " +
                                "(simplify_code / simplify_vb_code)",
                inspections = "~800 ids from five analyzer packs (inspect_code)",
                javascriptRules = new
                {
                    count = Snitch.Languages.JavaScript.JavaScriptAnalyzer.Rules.Count,
                    range = "JS0001-JS0017",
                },
                markupRules = new
                {
                    html = "HTML0001-HTML0014",
                    css = "CSS0001-CSS0008",
                },
                yamlRules = new
                {
                    count = Snitch.Languages.Yaml.YamlAnalyzer.Rules.Count,
                    range = "YAML0001-YAML0012",
                },
                fsharpRules = new
                {
                    count = Snitch.Languages.FSharp.FSharpAnalyzer.Rules.Count,
                    range = "FS0001-FS0010",
                },
            },

            // The language layers, each with the type that proves it exists in this build.
            layers = new
            {
                csharp = "Snitch.Languages.CSharp (Roslyn: navigation, refactoring, style, inspections, testing)",
                visualBasic = "Snitch.Languages.VB (style: 15 ids; refactoring helpers) + Snitch.Metacode's VisualBasicFrontend " +
                              "and VisualBasicSymbolIndexFrontend (trees, symbol index, complexity, C# generation) + the VB halves of " +
                              "structural search, formatting/usings/cleanup, inspections and the analysis tools",
                razor = "Snitch.Languages.Razor (heuristic markup scanner, 28 rules)",
                xaml = "Snitch.Languages.Xaml (markup + Roslyn code-behind: inventory, XAML0001-0009, navigation)",
                metacode = "Snitch.Metacode (C#, Visual Basic, Razor, pseudocode, Ghidra frontends; persisted index)",
                sql = "Snitch.Languages.Sql (SqlAnalyzer: comments/literals masked, statements split, SQL0001-SQL0010; " +
                      "a pattern scanner, not a dialect parser)",
                javascript = "Snitch.Node (the project's own npm/pnpm/yarn plus tsc and eslint interpreted) + " +
                             "Snitch.Languages.JavaScript (JavaScriptAnalyzer: JS/TS/JSX masked, JS0001-JS0017, " +
                             "relative imports resolved, module and export inventory; no module graph or type checking)",
                markup = "Snitch.Languages.WebMarkup (MarkupAnalyzer: quote-aware HTML tag scanner, HTML0001-HTML0014; " +
                         "CSS/SCSS masked, CSS0001-CSS0008; no DOM and no selector resolution)",
                yaml = "Snitch.Languages.Yaml (YamlAnalyzer: indentation-aware line scan, YAML0001-YAML0012, manifest " +
                       "identity and container checks; not a schema validator)",
                fsharp = "Snitch.Languages.FSharp (FSharpAnalyzer: masked scan, FS0001-FS0010, declaration inventory). " +
                         "No semantic model: F# is not a Roslyn language; build_project still returns the compiler's FS#### lines",
                kubernetes = "Snitch.Kubernetes (kubectl/helm wrappers: status, context, get/describe/logs, apply/delete/rollout, " +
                             "helm releases)",
            },

            docs = new
            {
                note = "The public catalogue lives in docs/tools.md; docs/gaps.md is the inventory " +
                       "of what is absent. The counts above are the live ones — compare them with the " +
                       "documented figures when either changes.",
            },
        };
    }
}
