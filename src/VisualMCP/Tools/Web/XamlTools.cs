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
        "code-behind type resolved through Roslyn (with its file:line), the DIALECT each project writes " +
        "(WPF / WinUI 3 / .NET MAUI / Avalonia / UWP, detected from UseWPF, UseMaui, UseWinUI, WindowsAppSDK, " +
        "Avalonia or the UWP target platform, reported together with the signals behind the answer), the target " +
        "frameworks a MAUI project multi-targets, and the markup counts (elements, x:Name, x:Key, event handlers, " +
        "resource references, styles, templates without DataType, bindings, setters). Read-only. For problems call " +
        "analyze_xaml; for a binding and what it reads call xaml_bindings; for the element tree call xaml_tree.")]
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
        "Find problems in the XAML markup, each with ruleId, severity, file:line and a fix. Markup rules: XAML0001 " +
        "duplicate x:Name, XAML0002 missing x:Class on a page/view root, XAML0003 x:Class that does not resolve to a " +
        "type, XAML0004 an event handler with no method, XAML0005 a StaticResource/DynamicResource key nothing " +
        "defines, XAML0006 a DataTemplate without DataType/x:DataType, XAML0007 an x:Name colliding with a member, " +
        "XAML0008 invalid XML, XAML0009 duplicate x:Key. Bindings, resolved through Roslyn: XAML0010 a binding path " +
        "whose segment does not exist on its data type (x:DataType/DataType in scope, or the DataContext the " +
        "code-behind assigns), XAML0011 a Setter without Property, XAML0012 a Setter property the Style's TargetType " +
        "does not declare, XAML0013 {x:Bind} in a WPF/MAUI/Avalonia project, XAML0014 an ElementName/x:Reference/" +
        "Storyboard.TargetName that names no element, XAML0015 a prefixed or dotted TargetType/DataType that does not " +
        "resolve. Read-only. It does not compile the XAML: a binding is checked only when the type behind it resolved, " +
        "and nothing is reported for a type or project it could not resolve.")]
    public static Task<object> AnalyzeXaml(
        IAnalyzeXamlService xaml,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file.")] string? path = null,
        [Description("Only this category: Markup, Binding or Maintainability.")] string? category = null,
        [Description("Only this rule id, e.g. XAML0010.")] string? rule = null,
        [Description("Minimum severity: Low (default), Medium, High, Critical.")] string minSeverity = "Low",
        [Description("Maximum findings to return (default 200, max 5000). The total is always reported.")] int maxFindings = 200)
        => xaml.AnalyzeAsync(projectName, path, category, rule, minSeverity, maxFindings);
}

[McpServerToolType]
public static class XamlBindingsTool
{
    [McpServerTool(Name = "xaml_bindings"), Description(
        "The binding inspector: every {Binding}/{x:Bind}/{CompiledBinding}/{TemplateBinding} a XAML file (or the whole " +
        "solution) declares — the target element and property, the path, the mode, the source (ElementName / " +
        "RelativeSource / Source), the data type the path is checked against and where that type came from (x:DataType, " +
        "a DataTemplate's DataType, a DataContext the code-behind assigns) — with the path walked member by member and a " +
        "status of valid / missing-member / not-checked (with the reason). Static: there is no running application, so a " +
        "path is checked against the type the markup names, not against a live object. Read-only.")]
    public static Task<object> XamlBindings(
        IXamlBindingsService xaml,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file.")] string? path = null,
        [Description("Resolve the data types through Roslyn and check every path (default true); false lists the bindings without type-checking.")] bool validate = true,
        [Description("Maximum bindings to return (default 500, max 5000). The total is always reported.")] int maxBindings = 500)
        => xaml.ListAsync(projectName, path, validate, maxBindings);
}

[McpServerToolType]
public static class XamlTreeTool
{
    [McpServerTool(Name = "xaml_tree"), Description(
        "The element tree ONE XAML file declares — each element with its x:Name, line, attributes and children, as " +
        "nested data plus an indented text rendering. This is the static half of a visual tree (what the framework will " +
        "instantiate): no layout, no styles applied, no templates expanded, no rendering. Read-only. The line numbers it " +
        "reports are what xaml_extract_style and xaml_promote_resource take.")]
    public static Task<object> XamlTree(
        IXamlTreeService xaml,
        [Description("The .xaml/.axaml file to read (absolute, or relative to the solution).")] string path,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("How deep to expand the tree (default 12, max 64).")] int maxDepth = 12)
        => xaml.TreeAsync(path, projectName, maxDepth);
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
        "code-behind must declare. Read-only. To RENAME one of them use xaml_rename.")]
    public static Task<object> XamlNavigate(
        IXamlNavigationService xaml,
        [Description("name | resource | style | handler")] string action,
        [Description("The x:Name, x:Key, style key or handler method to follow.")] string name,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("A folder or a single .xaml/.axaml file.")] string? path = null,
        [Description("Maximum results per list (default 200).")] int maxResults = 200)
        => xaml.NavigateAsync(action, name, projectName, path, maxResults);
}

[McpServerToolType]
public static class XamlRenameTool
{
    [McpServerTool(Name = "xaml_rename"), Description(
        "Rename an x:Name or a resource x:Key across markup and C#. kind=name renames the declaration and every " +
        "ElementName / x:Reference / Storyboard.TargetName that resolves it in the same file (an x:Name namescope is " +
        "the file), plus the identifiers the code-behind uses for the generated field — an identifier that resolves to " +
        "a real symbol is left alone and reported. kind=resource renames the x:Key and every StaticResource / " +
        "DynamicResource use across the project's XAML files, plus the string literals the code-behind passes to a " +
        "resource lookup. The result must still parse as XML and the new name must not already exist, or nothing is " +
        "written. Preview by default; every write is recorded in the edit history (revert_edit).")]
    public static Task<object> XamlRename(
        IXamlEditService xaml,
        [Description("The .xaml/.axaml file that declares the name or key.")] string path,
        [Description("The current x:Name or x:Key.")] string oldName,
        [Description("The new name (letters, digits, underscores).")] string newName,
        [Description("auto (default), name or resource.")] string kind = "auto",
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("true (default) previews only; pass false to write.")] bool dryRun = true)
        => xaml.RenameAsync(new XamlRenameRequest
        {
            Path = path,
            OldName = oldName,
            NewName = newName,
            Kind = kind,
            ProjectName = projectName,
            DryRun = dryRun,
        });
}

[McpServerToolType]
public static class XamlExtractStyleTool
{
    [McpServerTool(Name = "xaml_extract_style"), Description(
        "Extract an element's attributes into a keyed Style and reference it from the element: one <Setter> per " +
        "attribute (x:*, Name, Style and event handlers stay where they are), the Style goes into the nearest " +
        "<X.Resources> of the element's tree — created after the root's start tag when the file has none — and the " +
        "element gains Style=\"{StaticResource Key}\". Verified: the file must still parse as XML and the key must not " +
        "exist already, or nothing is written. Preview by default; the write is recorded in the edit history.")]
    public static Task<object> XamlExtractStyle(
        IXamlEditService xaml,
        [Description("The .xaml/.axaml file to edit.")] string path,
        [Description("The line the element's start tag begins on (xaml_tree reports it).")] int line,
        [Description("The resource key for the new Style.")] string key,
        [Description("Style.TargetType; defaults to the element's own name.")] string? targetType = null,
        [Description("true (default) previews only; pass false to write.")] bool dryRun = true)
        => xaml.ExtractStyleAsync(new XamlExtractStyleRequest
        {
            Path = path,
            Line = line,
            Key = key,
            TargetType = targetType,
            DryRun = dryRun,
        });
}

[McpServerToolType]
public static class XamlPromoteResourceTool
{
    [McpServerTool(Name = "xaml_promote_resource"), Description(
        "Promote a literal attribute value into a resource and replace the attribute with {StaticResource Key}: a " +
        "double becomes <sys:Double>/<x:Double>, a string <sys:String>/<x:String>, a color <Color>, a brush " +
        "<SolidColorBrush>, in the dialect's own syntax (WPF needs the sys: namespace, which is added when missing). " +
        "The kind is inferred from the attribute name (Width/Height/FontSize/Opacity/Duration are numbers, " +
        "Foreground/Background/Fill/Stroke brushes, Text/Content/Title strings) or passed explicitly. Verified: the " +
        "file must still parse as XML and the key must not exist already. Preview by default; the write is recorded in " +
        "the edit history.")]
    public static Task<object> XamlPromoteResource(
        IXamlEditService xaml,
        [Description("The .xaml/.axaml file to edit.")] string path,
        [Description("The line the element's start tag begins on.")] int line,
        [Description("The attribute holding the literal, e.g. Width or Foreground.")] string attribute,
        [Description("The resource key to create.")] string key,
        [Description("auto (default), double, string, color or brush.")] string kind = "auto",
        [Description("static (default) or dynamic: which reference replaces the literal.")] string resourceKind = "static",
        [Description("true (default) previews only; pass false to write.")] bool dryRun = true)
        => xaml.PromoteResourceAsync(new XamlPromoteRequest
        {
            Path = path,
            Line = line,
            Attribute = attribute,
            Key = key,
            Kind = kind,
            ResourceKind = resourceKind,
            DryRun = dryRun,
        });
}

[McpServerToolType]
public static class FormatXamlTool
{
    [McpServerTool(Name = "format_xaml"), Description(
        "Normalise the indentation of one XAML file: the leading whitespace of every tag line is set to the element's " +
        "depth times the indent unit (detected from the file, or 2/4/tab/a number of spaces) and trailing whitespace is " +
        "removed — nothing else. Attribute layout, quoting, comments and text content are left byte for byte: the " +
        "document's element/attribute signature must be identical after the rewrite or nothing is written. Preview by " +
        "default; the write is recorded in the edit history.")]
    public static Task<object> FormatXaml(
        IXamlEditService xaml,
        [Description("The .xaml/.axaml file to format.")] string path,
        [Description("auto (default: the file's own unit), 2, 4, tab, or a number of spaces (1-12).")] string indent = "auto",
        [Description("true (default) previews only; pass false to write.")] bool dryRun = true)
        => xaml.FormatAsync(new XamlFormatRequest { Path = path, Indent = indent, DryRun = dryRun });
}
