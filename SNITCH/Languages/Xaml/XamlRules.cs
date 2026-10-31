namespace Snitch.Languages.Xaml;

/// <summary>
/// The XAML rules. Each one answers a question the markup itself can answer — is a name declared
/// twice, does the code-behind type exist, is an event handler a method, is a resource key defined,
/// does a template declare its type — plus, when a <see cref="XamlValidation"/> is supplied, the ones
/// that need the C# behind the markup: does a binding path exist on its data type, does a Setter name a
/// property of its TargetType, does an ElementName point at a declared name, does the dialect support
/// the extension, does an explicitly named type resolve.
/// </summary>
public static class XamlRules
{
    public static IEnumerable<XamlFinding> Run(
        XamlDocument document,
        XamlTypeFacts? codeBehind,
        bool codeBehindResolvable,
        IReadOnlySet<string> projectKeys,
        XamlValidation? validation = null)
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

        if (validation is not null)
            findings.AddRange(BindingRules(document, validation));

        return findings;
    }

    /// <summary>The rules that need a resolved type behind the markup.</summary>
    private static IEnumerable<XamlFinding> BindingRules(XamlDocument document, XamlValidation validation)
    {
        var findings = new List<XamlFinding>();

        // XAML0010 — a binding path whose segment does not exist on the data type. The rule that
        // answers "is this binding going to fail at run time", and it exists only when the type resolved.
        foreach (var check in validation.Bindings.Where(candidate => candidate.Failed))
        {
            var path = check.Binding.PathText;
            findings.Add(XamlAnalysis.Finding("XAML0010", document.FilePath, document.RelativePath,
                check.Binding.Line,
                $"{check.Binding.Property}=\"{{{check.Binding.Kind} {path}}}\" reads '{check.MissingMember}', but '{check.DataType}' " +
                $"(the data type from {check.DataTypeSource}) declares no such public member.",
                $"Add '{check.MissingMember}' to '{check.DataType}', or fix the path. The check followed " +
                string.Join(" → ", check.Steps.Select(step => step.TypeName is null ? step.Name : $"{step.TypeName}.{step.Name}")) + ".",
                null));
        }

        // XAML0011 — a Setter with no Property: the required attribute is what the setter sets.
        foreach (var setter in document.Setters.Where(setter => string.IsNullOrWhiteSpace(setter.Property)))
            findings.Add(XamlAnalysis.Finding("XAML0011", document.FilePath, document.RelativePath, setter.Line,
                "The Setter declares no Property, so it sets nothing and the XAML compiler rejects it.",
                "Add Property=\"…\" with the property the setter should assign.",
                null));

        // XAML0012 — a Setter that names a property the TargetType does not have.
        foreach (var check in validation.Setters.Where(candidate =>
                     candidate.TargetResolved && !candidate.PropertyFound &&
                     !string.IsNullOrWhiteSpace(candidate.Setter.Property)))
        {
            findings.Add(XamlAnalysis.Finding("XAML0012", document.FilePath, document.RelativePath,
                check.Setter.Line,
                $"The Setter sets '{check.Setter.Property}', which '{check.Setter.TargetType}' does not declare. " +
                "A Setter can only assign a property of the type its Style targets.",
                "Use a property of the TargetType (or an attached property written as Owner.Property), or change the TargetType.",
                null));
        }

        // XAML0013 — x:Bind in a dialect that does not compile it. Dialect-aware, and only when the
        // dialect was actually detected: an unknown project is not accused of anything.
        if (validation.Dialect is not (XamlDialect.Unknown or XamlDialect.WinUi or XamlDialect.Uwp))
        {
            var dialectName = validation.Dialect switch
            {
                XamlDialect.Wpf => "WPF",
                XamlDialect.Maui => ".NET MAUI",
                XamlDialect.Avalonia => "Avalonia",
                _ => validation.Dialect.ToString(),
            };

            foreach (var binding in document.Bindings.Where(candidate => candidate.Kind == "x:Bind"))
                findings.Add(XamlAnalysis.Finding("XAML0013", document.FilePath, document.RelativePath, binding.Line,
                    $"{{x:Bind}} is compiled by WinUI 3 and UWP only: this project is {dialectName}.",
                    validation.Dialect switch
                    {
                        XamlDialect.Maui => "Use {Binding} (add x:DataType for a compiled binding in MAUI).",
                        XamlDialect.Avalonia => "Use {Binding} or {CompiledBinding}.",
                        _ => "Use {Binding} (WPF has no x:Bind).",
                    },
                    null));
        }

        // XAML0014 — ElementName / x:Reference / Storyboard.TargetName that names nothing declared in
        // this file. Namescopes are per file, so this check needs no project and no compilation.
        var declared = document.Names.Select(name => name.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in document.NameReferences)
        {
            if (declared.Contains(reference.Name)) continue;
            if (reference.Name is "True" or "False") continue;

            findings.Add(XamlAnalysis.Finding("XAML0014", document.FilePath, document.RelativePath, reference.Line,
                $"{reference.Kind} '{reference.Name}' names no element in this file: x:Name namescopes do not cross files.",
                "Declare x:Name on the target element in this file, or fix the name.",
                null));
        }

        // XAML0015 — an explicitly prefixed or dotted type name that does not resolve.
        foreach (var check in validation.TypeReferences.Where(candidate => !candidate.Resolved))
            findings.Add(XamlAnalysis.Finding("XAML0015", document.FilePath, document.RelativePath,
                check.Reference.Line,
                $"{check.Reference.Kind} '{check.Reference.Name}' does not resolve to a type in the project's compilation.",
                "Check the xmlns prefix declaration and the type name, and that the type's assembly is referenced.",
                null));

        return findings;
    }
}
