using System.Xml;
using System.Xml.Linq;

namespace Snitch.Languages.Xaml;

/// <summary>
/// One XAML file scanned into the facts the rules need: the class it declares, the names and
/// resource keys it defines, the resources it references, the event handlers it names and the
/// styles with their BasedOn chain. Kept as plain data so the rules stay testable without I/O.
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

    /// <summary>Styles with a key and an optional BasedOn key.</summary>
    public List<XamlStyle> Styles { get; } = [];

    /// <summary>Every {Binding ElementName=X} / x:Reference X, so a name's uses can be listed.</summary>
    public List<XamlNameReference> NameReferences { get; } = [];

    /// <summary>Elements with local name DataTemplate and no x:DataType / DataType attribute.</summary>
    public List<int> TemplatesWithoutDataType { get; } = [];

    public int ElementCount { get; init; }

    /// <summary>A page/view/component root that must name its code-behind type.</summary>
    public bool RequiresClass => RootName is "Window" or "Page" or "UserControl" or "ContentView" or "ContentPage" or "Shell";
}

/// <summary>One x:Name declaration.</summary>
public sealed record XamlName(string Name, int Line);

/// <summary>One x:Key declaration.</summary>
public sealed record XamlKey(string Key, int Line, string Owner);

/// <summary>One event handler cited by the markup.</summary>
public sealed record XamlHandler(string Attribute, string Method, int Line);

/// <summary>One resource reference from markup.</summary>
public sealed record XamlResourceReference(string Key, int Line, string Kind);

/// <summary>One keyed style and the key it is based on.</summary>
public sealed record XamlStyle(string Key, string? BasedOnKey, int Line);

/// <summary>One use of an x:Name (ElementName=, x:Reference, …).</summary>
public sealed record XamlNameReference(string Name, int Line, string Kind);

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
        ["XAML0001", "XAML0002", "XAML0003", "XAML0004", "XAML0005", "XAML0006", "XAML0007", "XAML0008", "XAML0009"];

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
        _ => ("XAML", "Markup", "Low"),
    };
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
