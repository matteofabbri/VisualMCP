using System.Xml;
using System.Xml.Linq;

namespace Snitch.Languages.Xaml;

/// <summary>
/// One XAML file scanned into the facts the rules need: the class it declares, the names and
/// resource keys it defines, the resources it references, the event handlers it names, the styles
/// with their BasedOn chain, and every binding with the data type that was in scope where it was
/// written. Kept as plain data so the rules stay testable without I/O.
/// </summary>
public sealed class XamlDocument
{
    public XamlDocument(string filePath, string relativePath, string? project, XDocument xml)
    {
        FilePath = filePath;
        RelativePath = relativePath;
        Project = project;
        Xml = xml;
    }

    public string FilePath { get; }
    public string RelativePath { get; }
    public string? Project { get; }
    public XDocument Xml { get; }

    /// <summary>x:Class — the code-behind type, when the file declares one.</summary>
    public string? ClassName { get; init; }

    /// <summary>Local name of the root element (Window, Page, UserControl, ResourceDictionary, Application, …).</summary>
    public string RootName { get; init; } = string.Empty;

    /// <summary>Every x:Name in the file, with its line.</summary>
    public List<XamlName> Names { get; } = [];

    /// <summary>Every x:Key in the file, with its line.</summary>
    public List<XamlKey> Keys { get; } = [];

    /// <summary>Every event-handler-looking attribute (name, value, line).</summary>
    public List<XamlHandler> Handlers { get; } = [];

    /// <summary>Every StaticResource/DynamicResource reference (key, line).</summary>
    public List<XamlResourceReference> ResourceReferences { get; } = [];

    /// <summary>Styles with a key, an optional BasedOn key and their TargetType.</summary>
    public List<XamlStyle> Styles { get; } = [];

    /// <summary>Every ElementName / x:Reference / Storyboard.TargetName use, so a name's uses can be listed.</summary>
    public List<XamlNameReference> NameReferences { get; } = [];

    /// <summary>Every binding in the file, with the data type that was in scope where it was written.</summary>
    public List<XamlBinding> Bindings { get; } = [];

    /// <summary>Every Setter, with the Style it belongs to (its key and TargetType, when it has them).</summary>
    public List<XamlSetter> Setters { get; } = [];

    /// <summary>Every type name the markup states explicitly: TargetType, DataType, x:DataType.</summary>
    public List<XamlTypeReference> TypeReferences { get; } = [];

    /// <summary>Elements with local name DataTemplate and no x:DataType / DataType attribute.</summary>
    public List<int> TemplatesWithoutDataType { get; } = [];

    /// <summary>prefix → XML namespace URI, as declared in this file (a root declaration dominates).</summary>
    public Dictionary<string, string> Namespaces { get; } = new(StringComparer.Ordinal);

    /// <summary>The type a design-time <c>d:DataContext="{d:DesignInstance Type=…}"</c> declares, when any.</summary>
    public string? DesignDataContextType { get; set; }

    public int ElementCount { get; init; }

    /// <summary>A page/view/component root that must name its code-behind type.</summary>
    public bool RequiresClass => RootName is "Window" or "Page" or "UserControl" or "ContentView" or "ContentPage" or "Shell";

    /// <summary>True when the file is an Avalonia document by extension.</summary>
    public bool IsAvaloniaFile => RelativePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One x:Name declaration.</summary>
public sealed record XamlName(string Name, int Line);

/// <summary>One x:Key declaration.</summary>
public sealed record XamlKey(string Key, int Line, string Owner);

/// <summary>One event handler cited by the markup.</summary>
public sealed record XamlHandler(string Attribute, string Method, int Line);

/// <summary>One resource reference from markup.</summary>
public sealed record XamlResourceReference(string Key, int Line, string Kind);

/// <summary>One keyed style, the key it is based on and the type it targets.</summary>
public sealed record XamlStyle(string Key, string? BasedOnKey, int Line)
{
    /// <summary>The TargetType attribute as written, when the style declares one.</summary>
    public string? TargetType { get; init; }

    /// <summary>The type inside <c>BasedOn="{StaticResource {x:Type Button}}"</c>, when that is how it is written.</summary>
    public string? BasedOnType { get; init; }
}

/// <summary>One use of an x:Name (ElementName=, x:Reference, Storyboard.TargetName).</summary>
public sealed record XamlNameReference(string Name, int Line, string Kind);

/// <summary>
/// One binding as the markup writes it. <see cref="DataTypeName"/> is the x:DataType/DataType that was
/// in scope at the element (a template's own type included); resolving it is the validator's job,
/// because that is the only part that needs Roslyn.
/// </summary>
public sealed record XamlBinding(
    string Element,
    string? ElementName,
    string Property,
    string Kind,
    string? Path,
    string? Mode,
    string? SourceElementName,
    string? RelativeSource,
    string? SourceType,
    int Line,
    int Column,
    string? DataTypeName)
{
    /// <summary>The TargetType of the ControlTemplate this binding is written inside, when it is.</summary>
    public string? TemplateTargetType { get; init; }

    /// <summary>The text shown for the path: a binding with none binds the data context itself.</summary>
    public string PathText => Path ?? "(the data context)";
}

/// <summary>One Setter: its property (null when the required attribute is missing), its style and that style's type.</summary>
public sealed record XamlSetter(string? Property, string? Value, int Line, string? StyleKey, string? TargetType);

/// <summary>One explicitly named type reference in the markup.</summary>
public sealed record XamlTypeReference(string Name, int Line, string Kind);

/// <summary>One XAML finding, shaped like the Razor ones so the two layers read the same.</summary>
public sealed record XamlFinding(
    string RuleId,
    string Category,
    string Title,
    string Severity,
    string Message,
    string Suggestion,
    string FilePath,
    string RelativePath,
    int Line,
    string? Snippet);

/// <summary>The rule table: ids, categories and severities, in one place.</summary>
public static class XamlRuleCatalog
{
    public static readonly string[] Severities = ["Low", "Medium", "High", "Critical"];

    /// <summary>Every rule id, in order — the catalogue <c>capability_report</c> reports.</summary>
    public static readonly string[] Ids =
    [
        "XAML0001", "XAML0002", "XAML0003", "XAML0004", "XAML0005", "XAML0006", "XAML0007", "XAML0008", "XAML0009",
        "XAML0010", "XAML0011", "XAML0012", "XAML0013", "XAML0014", "XAML0015",
    ];

    public static int Rank(string? severity)
    {
        var index = Array.FindIndex(Severities, s => s.Equals(severity, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    public static (string Title, string Category, string Severity) Describe(string ruleId) => ruleId switch
    {
        "XAML0001" => ("Duplicate x:Name", "Markup", "High"),
        "XAML0002" => ("Missing x:Class", "Markup", "Medium"),
        "XAML0003" => ("Code-behind type not found", "Markup", "High"),
        "XAML0004" => ("Event handler has no method", "Markup", "High"),
        "XAML0005" => ("Resource key is not defined", "Markup", "Medium"),
        "XAML0006" => ("DataTemplate without DataType", "Maintainability", "Low"),
        "XAML0007" => ("x:Name collides with a member", "Markup", "Medium"),
        "XAML0008" => ("Invalid XAML", "Markup", "High"),
        "XAML0009" => ("Duplicate x:Key", "Markup", "Medium"),
        "XAML0010" => ("Binding path not found on the data type", "Binding", "High"),
        "XAML0011" => ("Setter without Property", "Binding", "High"),
        "XAML0012" => ("Setter property not on the TargetType", "Binding", "High"),
        "XAML0013" => ("x:Bind is not supported by this dialect", "Binding", "High"),
        "XAML0014" => ("Element name is not declared", "Binding", "High"),
        "XAML0015" => ("TargetType does not resolve", "Binding", "Medium"),
        _ => ("XAML", "Markup", "Low"),
    };

    /// <summary>Every category that has at least one rule.</summary>
    public static string[] Categories => Ids
        .Select(id => Describe(id).Category)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>What a code-behind type offers, so the rules can answer without Roslyn types.</summary>
public sealed class XamlTypeFacts
{
    public XamlTypeFacts(string fullName, HashSet<string> memberNames, HashSet<string> declaredMemberNames,
        string? filePath, int? line)
    {
        FullName = fullName;
        MemberNames = memberNames;
        DeclaredMemberNames = declaredMemberNames;
        FilePath = filePath;
        Line = line;
    }

    public string FullName { get; }

    /// <summary>Member names declared on the type and its base types (event handlers live here).</summary>
    public HashSet<string> MemberNames { get; }

    /// <summary>Only the members this type declares itself — what an x:Name can collide with.</summary>
    public HashSet<string> DeclaredMemberNames { get; }

    public string? FilePath { get; }
    public int? Line { get; }
}
