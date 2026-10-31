using Microsoft.CodeAnalysis;

namespace Snitch.Languages.Xaml;

/// <summary>One type reference and whether it resolved to a type in the project's compilation.</summary>
public sealed record XamlTypeCheck(XamlTypeReference Reference, bool Resolved, string? FullName);

/// <summary>One Setter, its style's TargetType and whether that type declares the property.</summary>
public sealed record XamlSetterCheck(XamlSetter Setter, bool TargetResolved, bool PropertyFound);

/// <summary>
/// What the Roslyn half of the XAML layer found: the check of every binding, whether every explicitly
/// named type resolved, and whether every Setter names a property of its TargetType. Built once per
/// file and consumed by <see cref="XamlRules"/>, so the rules stay pure data-in/data-out.
/// </summary>
public sealed class XamlValidation
{
    public XamlDialect Dialect { get; init; } = XamlDialect.Unknown;
    public List<XamlBindingCheck> Bindings { get; } = [];
    public List<XamlTypeCheck> TypeReferences { get; } = [];
    public List<XamlSetterCheck> Setters { get; } = [];

    /// <summary>True when a resolver was available: without one, the Roslyn half reports nothing.</summary>
    public bool TypesChecked { get; init; }
}

/// <summary>
/// Checks what a markup scanner cannot: a binding path against the type behind it, a Setter against
/// its TargetType, a named type against the compilation. It only reports what it could resolve — an
/// unknown DataContext or a simple type name with no dialect table is "not checked", never "wrong".
/// </summary>
public static class XamlBindingValidator
{
    public static async Task<XamlValidation> ValidateAsync(XamlDocument document, XamlTypeResolver? resolver,
        XamlDialect dialect)
    {
        var validation = new XamlValidation { Dialect = dialect, TypesChecked = resolver is not null };

        var dataContext = resolver is null ? null : await resolver.DataContextAsync(document);
        var codeBehind = resolver is null ? null : await resolver.CodeBehindAsync(document);

        foreach (var binding in document.Bindings)
            validation.Bindings.Add(await CheckBindingAsync(document, resolver, binding, dataContext, codeBehind));

        if (resolver is not null)
        {
            foreach (var reference in document.TypeReferences)
            {
                var resolvable = reference.Name.Contains(':') || reference.Name.Contains('.');
                if (!resolvable) continue;

                var symbol = await resolver.ResolveTypeAsync(document, reference.Name);
                validation.TypeReferences.Add(new XamlTypeCheck(reference, symbol is not null, symbol?.ToDisplayString()));
            }

            foreach (var setter in document.Setters)
            {
                var property = setter.Property;
                var hasTarget = !string.IsNullOrWhiteSpace(setter.TargetType);
                var checkable = hasTarget && !string.IsNullOrWhiteSpace(property) &&
                                !property.Contains('.') && !property.Contains('(');

                var symbol = checkable
                    ? await resolver.ResolveTypeAsync(document, setter.TargetType)
                    : null;

                var found = symbol is null || !checkable || resolver.HasMember(symbol, property!);
                validation.Setters.Add(new XamlSetterCheck(setter, symbol is not null, found));
            }
        }

        return validation;
    }

    private static async Task<XamlBindingCheck> CheckBindingAsync(XamlDocument document, XamlTypeResolver? resolver,
        XamlBinding binding, XamlDataContext? dataContext, INamedTypeSymbol? codeBehind)
    {
        var notChecked = (string reason) => new XamlBindingCheck(binding, "not-checked", null, null, [], null, reason);

        if (binding.SourceElementName is not null)
            return notChecked($"the source is the element '{binding.SourceElementName}', not the data context");

        if (binding.RelativeSource is not null)
            return notChecked($"the source is RelativeSource={binding.RelativeSource}");

        if (binding.SourceType is not null)
            return notChecked($"the source is an explicit object ('{binding.SourceType}')");

        if (binding.Kind == "MultiBinding")
            return notChecked("a MultiBinding's children carry their own paths; the object is not itself typed");

        if (resolver is null)
            return notChecked("no solution is loaded, so no type can be resolved");

        ITypeSymbol? type = null;
        string? source = null;

        switch (binding.Kind)
        {
            case "TemplateBinding":
                if (!string.IsNullOrWhiteSpace(binding.TemplateTargetType))
                {
                    type = await resolver.ResolveTypeAsync(document, binding.TemplateTargetType);
                    source = "ControlTemplate.TargetType";
                }
                break;

            case "x:Bind":
                if (!string.IsNullOrWhiteSpace(binding.DataTypeName))
                {
                    type = await resolver.ResolveTypeAsync(document, binding.DataTypeName);
                    source = "x:DataType";
                }
                else if (codeBehind is not null)
                {
                    type = codeBehind;
                    source = "x:Bind binds the page type";
                }
                break;

            case "CompiledBinding":
                if (!string.IsNullOrWhiteSpace(binding.DataTypeName))
                {
                    type = await resolver.ResolveTypeAsync(document, binding.DataTypeName);
                    source = "x:DataType";
                }
                break;

            default:
                if (!string.IsNullOrWhiteSpace(binding.DataTypeName))
                {
                    type = await resolver.ResolveTypeAsync(document, binding.DataTypeName);
                    source = "x:DataType / DataType";
                }
                else if (dataContext is not null)
                {
                    type = dataContext.Symbol;
                    source = dataContext.Source;
                }
                break;
        }

        if (type is null)
        {
            var reason = dataContext is { } unresolved
                ? $"{unresolved.Source} names '{unresolved.TypeName}', which did not resolve to one type"
                : "no x:DataType and the markup names no data context type";
            return notChecked(reason);
        }

        var typeName = type.ToDisplayString();
        var steps = new List<XamlPathStep>();

        var segments = XamlMarkup.Segments(binding.Path);
        if (segments.Count == 0)
            return new XamlBindingCheck(binding, "valid", typeName, source, steps, null,
                "the binding has no path: it binds the data context itself");

        var current = type;
        foreach (var segment in segments)
        {
            if (!segment.Checkable)
            {
                steps.Add(new XamlPathStep(segment.Name, current.ToDisplayString(), true));
                return new XamlBindingCheck(binding, "valid", typeName, source, steps, null,
                    $"the path steps through '{segment.Name}', which is an attached property, an indexer or a method call");
            }

            if (current is null)
            {
                steps.Add(new XamlPathStep(segment.Name, null, true));
                break;
            }

            var member = resolver.MemberType(current, segment.Name);
            steps.Add(new XamlPathStep(segment.Name, current.ToDisplayString(), member is not null));

            if (member is null)
                return new XamlBindingCheck(binding, "missing-member", typeName, source, steps, segment.Name,
                    $"'{segment.Name}' is not a public member of '{current.ToDisplayString()}'");

            current = member;
        }

        return new XamlBindingCheck(binding, "valid", typeName, source, steps, null, null);
    }
}
