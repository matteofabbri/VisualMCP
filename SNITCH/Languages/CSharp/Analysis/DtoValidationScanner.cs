using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The DTO half of missing validation. A public class or record whose members are only settable properties
/// is a payload someone binds from the outside, and every one of those settable properties is an input with
/// no contract. The recognition is deliberately narrow: a type with behaviour, a base class or fewer than two
/// settable properties is not a DTO, and a property counts as validated only when it carries
/// <c>System.ComponentModel.DataAnnotations</c> — the root namespace, not the Schema mapping attributes —
/// or a subclass of <c>ValidationAttribute</c>, so a custom validator counts.
/// </summary>
public static class DtoValidationScanner
{
    private static readonly HashSet<string> ValidationAttributeNames = new(StringComparer.Ordinal)
    {
        "Required", "StringLength", "MaxLength", "MinLength", "Range", "RegularExpression",
        "EmailAddress", "Phone", "Url", "CreditCard", "Compare", "CustomValidation",
        "FileExtensions", "AllowedValues", "Base64String",
    };

    private const string ValidationAttributeFullName = "System.ComponentModel.DataAnnotations.ValidationAttribute";

    public static DtoValidationFinding? Scan(
        TypeDeclarationSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type) return null;

        if (type.TypeKind != TypeKind.Class) return null;
        if (type.IsStatic || type.IsAbstract) return null;
        if (type.DeclaredAccessibility != Accessibility.Public) return null;
        if (type.ContainingType is not null) return null;

        // A base class other than object means inherited behaviour or an attribute/exception, not a payload.
        if (type.BaseType is { } baseType && baseType.SpecialType != SpecialType.System_Object) return null;

        if (type.GetMembers().OfType<IMethodSymbol>().Any(IsBehaviour)) return null;

        var properties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic &&
                               !property.IsIndexer &&
                               property.DeclaredAccessibility == Accessibility.Public &&
                               property.SetMethod is not null)
            .ToList();

        if (properties.Count < 2) return null;

        var unvalidated = new List<string>();
        var validated = new List<string>();

        foreach (var property in properties)
        {
            if (property.GetAttributes().Any(IsValidationAttribute))
                validated.Add(property.Name);
            else
                unvalidated.Add(property.Name);
        }

        var line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var file = declaration.SyntaxTree.FilePath ?? "(inline)";

        return new DtoValidationFinding(
            type.ToDisplayString(),
            file,
            line,
            unvalidated.OrderBy(name => name, StringComparer.Ordinal).ToList(),
            validated.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    private static bool IsBehaviour(IMethodSymbol method)
    {
        if (method.IsImplicitlyDeclared) return false;

        // The members a record synthesises are part of its data shape, not behaviour. Operators are not
        // consulted at all: a record emits equality operators, and a payload with a conversion is still data.
        if (method.Name is "Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "PrintMembers") return false;

        return method.MethodKind == MethodKind.Ordinary;
    }

    private static bool IsValidationAttribute(AttributeData attribute)
    {
        var attributeType = attribute.AttributeClass;
        if (attributeType is null) return false;

        for (var current = attributeType; current is not null; current = current.BaseType)
            if (string.Equals(current.ToDisplayString(), ValidationAttributeFullName, StringComparison.Ordinal))
                return true;

        // Subclasses are covered above; the direct list covers [Required] itself and the net8+ additions.
        return string.Equals(
                   attributeType.ContainingNamespace?.ToDisplayString(),
                   "System.ComponentModel.DataAnnotations",
                   StringComparison.Ordinal) &&
               ValidationAttributeNames.Contains(attributeType.Name);
    }
}
