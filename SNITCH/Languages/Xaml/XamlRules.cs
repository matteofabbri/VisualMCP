namespace Snitch.Languages.Xaml;

/// <summary>
/// The XAML rules. Each one answers a question the markup itself can answer: is a name declared
/// twice, does the code-behind type exist, is an event handler a method, is a resource key defined,
/// does a template declare its type. Compilation (bindings, tag helpers, styles at runtime) is out
/// of reach and documented as such.
/// </summary>
public static class XamlRules
{
    public static IEnumerable<XamlFinding> Run(
        XamlDocument document,
        XamlTypeFacts? codeBehind,
        bool codeBehindResolvable,
        IReadOnlySet<string> projectKeys)
    {
        var findings = new List<XamlFinding>();

        // XAML0001 — duplicate x:Name: the second declaration silently loses to the first.
        foreach (var duplicate in document.Names
                     .GroupBy(name => name.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var first = duplicate.First();
            foreach (var repeated in duplicate.Skip(1))
                findings.Add(XamlAnalysis.Finding("XAML0001", document.FilePath, document.RelativePath,
                    repeated.Line,
                    $"x:Name '{duplicate.Key}' is declared twice (first at line {first.Line}); one generated field overwrites the other.",
                    "Rename one of the elements: x:Name must be unique in a XAML file.",
                    null));
        }

        // XAML0002 — a page/view without x:Class has no code-behind to bind its event handlers.
        if (document.RequiresClass && string.IsNullOrWhiteSpace(document.ClassName))
            findings.Add(XamlAnalysis.Finding("XAML0002", document.FilePath, document.RelativePath, 1,
                $"The root element is '{document.RootName}' but the file declares no x:Class, so no code-behind is associated with it.",
                "Add x:Class=\"Namespace.TypeName\" (and the matching partial class), or turn the file into a ResourceDictionary.",
                null));

        // XAML0003 — x:Class that does not resolve.
        if (!string.IsNullOrWhiteSpace(document.ClassName) && codeBehindResolvable && codeBehind is null)
            findings.Add(XamlAnalysis.Finding("XAML0003", document.FilePath, document.RelativePath, 1,
                $"x:Class '{document.ClassName}' does not resolve to a type in the loaded solution.",
                "Check the namespace and the type name, and that the code-behind file is part of the project.",
                null));

        // XAML0004 — an event handler the code-behind does not declare.
        if (codeBehind is not null)
        {
            foreach (var handler in document.Handlers)
            {
                var method = handler.Method.Split('.').Last();
                if (codeBehind.MemberNames.Contains(method)) continue;

                findings.Add(XamlAnalysis.Finding("XAML0004", document.FilePath, document.RelativePath,
                    handler.Line,
                    $"'{handler.Attribute}' points at '{handler.Method}', but '{codeBehind.FullName}' declares no such method in its hierarchy.",
                    "Add the handler method to the code-behind (matching the event's delegate signature), or fix the name in the markup.",
                    null));
            }
        }

        // XAML0005 — a StaticResource/DynamicResource key nothing in the project defines.
        if (projectKeys.Count > 0)
        {
            foreach (var reference in document.ResourceReferences)
            {
                if (projectKeys.Contains(reference.Key)) continue;
                if (reference.Key.StartsWith("System.", StringComparison.Ordinal)) continue;

                findings.Add(XamlAnalysis.Finding("XAML0005", document.FilePath, document.RelativePath,
                    reference.Line,
                    $"{{{reference.Kind} {reference.Key}}} is not defined by any x:Key in the project's XAML files.",
                    "Define the key in a ResourceDictionary merged into the application, or fix the key name. " +
                    "A key coming from a referenced assembly's theme dictionary is not visible to this scan.",
                    null));
            }
        }

        // XAML0006 — a template whose binding type cannot be checked.
        foreach (var line in document.TemplatesWithoutDataType)
            findings.Add(XamlAnalysis.Finding("XAML0006", document.FilePath, document.RelativePath, line,
                "The DataTemplate declares neither DataType nor x:DataType.",
                "Add DataType (WPF) or x:DataType (WinUI/MAUI) so bindings inside the template are checked by the compiler.",
                null));

        // XAML0007 — an x:Name that collides with a member the code-behind declares itself.
        if (codeBehind is not null)
        {
            foreach (var name in document.Names)
            {
                if (!codeBehind.DeclaredMemberNames.Contains(name.Name)) continue;

                findings.Add(XamlAnalysis.Finding("XAML0007", document.FilePath, document.RelativePath,
                    name.Line,
                    $"x:Name '{name.Name}' collides with a member '{codeBehind.FullName}' declares: the generated field and that member cannot both exist.",
                    "Rename the element or the member.",
                    null));
            }
        }

        // XAML0009 — the same x:Key twice in one dictionary: the second wins silently.
        foreach (var duplicate in document.Keys
                     .GroupBy(key => key.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var first = duplicate.First();
            foreach (var repeated in duplicate.Skip(1))
                findings.Add(XamlAnalysis.Finding("XAML0009", document.FilePath, document.RelativePath,
                    repeated.Line,
                    $"x:Key '{duplicate.Key}' is defined twice (first at line {first.Line}); one definition is unreachable.",
                    "Give the resources distinct keys, or remove the duplicate.",
                    null));
        }

        return findings;
    }
}
