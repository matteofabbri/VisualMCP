using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Xaml;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class XamlPagesTool
{
    [McpServerTool(Name = "xaml_pages"), Description(
        "Inventory the XAML files of the solution (.xaml/.axaml: WPF, WinUI 3, MAUI, Avalonia, UWP): " +
        "what each one is (window, page, userControl, maui, resourceDictionary, application), the x:Class " +
        "code-behind type resolved through Roslyn (with its file:line), and the markup counts (elements, " +
        "x:Name, x:Key, event handlers, resource references, styles, templates without DataType). Read-only. " +
        "For problems call analyze_xaml; for an x:Name/resource key/handler and what it points to call " +
        "xaml_navigate.")]
    public static Task<object> XamlPages(
        IXamlPagesService xaml,
        [Description("Only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file (absolute, or relative to the solution).")] string? path = null,
        [Description("Maximum files to return (default 200, max 5000).")] int maxFiles = 200)
        => xaml.ListAsync(projectName, path, maxFiles);
}

[McpServerToolType]
public static class AnalyzeXamlTool
{
    [McpServerTool(Name = "analyze_xaml"), Description(
        "Find problems in the XAML markup, each with ruleId, severity, file:line and a fix: XAML0001 duplicate " +
        "x:Name, XAML0002 missing x:Class on a page/view root, XAML0003 x:Class that does not resolve to a type " +
        "in the solution, XAML0004 an event handler with no method in the code-behind, XAML0005 a " +
        "StaticResource/DynamicResource key no x:Key in the project defines, XAML0006 a DataTemplate without " +
        "DataType/x:DataType, XAML0007 an x:Name colliding with a member the code-behind declares, XAML0008 " +
        "invalid XML, XAML0009 duplicate x:Key. Read-only. This is the XAML half of the C# analysis: it reads " +
        "the markup and resolves the C# through Roslyn — it does not compile the XAML, so bindings and styles " +
        "are not proved.")]
    public static Task<object> AnalyzeXaml(
        IAnalyzeXamlService xaml,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file.")] string? path = null,
        [Description("Only this category: Markup or Maintainability.")] string? category = null,
        [Description("Only this rule id, e.g. XAML0004.")] string? rule = null,
        [Description("Minimum severity: Low (default), Medium, High, Critical.")] string minSeverity = "Low",
        [Description("Maximum findings to return (default 200, max 5000). The total is always reported.")] int maxFindings = 200)
        => xaml.AnalyzeAsync(projectName, path, category, rule, minSeverity, maxFindings);
}

[McpServerToolType]
public static class XamlNavigateTool
{
    [McpServerTool(Name = "xaml_navigate"), Description(
        "Follow one XAML name to everything attached to it — the navigation a markup scanner lacks. " +
        "action=name: every x:Name declaration of that name, every ElementName/x:Reference use, and the " +
        "code-behind type (with file:line) that gets the generated field. action=resource: every x:Key " +
        "definition and every StaticResource/DynamicResource use. action=style: the style's definitions, its " +
        "BasedOn chain and the styles derived from it. action=handler: the markup and the method the " +
        "code-behind must declare. Read-only.")]
    public static Task<object> XamlNavigate(
        IXamlNavigationService xaml,
        [Description("name | resource | style | handler")] string action,
        [Description("The x:Name, x:Key, style key or handler method to follow.")] string name,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file.")] string? path = null,
        [Description("Maximum results per list (default 200).")] int maxResults = 200)
        => xaml.NavigateAsync(action, name, projectName, path, maxResults);
}
