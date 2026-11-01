using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The Visual Basic stubs: a <c>Sub</c>/<c>Function</c>/<c>Property</c>/<c>Event</c> block with the
/// signature taken from the symbol and a body that throws. VB differs from C# in two ways that matter
/// here — a member implements an interface through the <c>Implements I.Member</c> clause, and an
/// abstract member is satisfied with the <c>Overrides</c> keyword — so the same symbol renders as a
/// different shape.
/// </summary>
public sealed partial class ImplementMembersService
{
    private static bool IsVisualBasicTypeDeclaration(SyntaxNode node) =>
        node is ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax;

    private static string? VisualBasicTypeNameOf(SyntaxNode node) => node switch
    {
        TypeBlockSyntax block => block.BlockStatement.Identifier.ValueText,
        _ => null,
    };

    private static int VisualBasicEndBlockPosition(SyntaxNode type) =>
        ((TypeBlockSyntax)type).EndBlockStatement.SpanStart;

    private static SyntaxNode? VisualBasicFirstMember(SyntaxNode type) =>
        ((TypeBlockSyntax)type).Members.FirstOrDefault();

    private static string RenderVisualBasic(ISymbol member, INamedTypeSymbol type, SemanticModel model, int position)
    {
        const string Throws = "Throw New System.NotImplementedException()";

        string Type(ITypeSymbol symbol) => symbol.ToMinimalDisplayString(model, position);

        var accessibility = AccessibilityKeyword(member.DeclaredAccessibility);
        var clause = member.ContainingType?.TypeKind == TypeKind.Interface
            ? ImplementsClause(member, type, model, position)
            : " Overrides";

        switch (member)
        {
            case IMethodSymbol method:
            {
                var parameters = string.Join(", ", method.Parameters.Select(Parameter));
                var typeParameters = method.TypeParameters.Length == 0
                    ? string.Empty
                    : "(Of " + string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name)) + ")";

                return method.ReturnsVoid
                    ? $"{accessibility} Sub {method.Name}{typeParameters}({parameters}){clause}\n    {Throws}\nEnd Sub"
                    : $"{accessibility} Function {method.Name}{typeParameters}({parameters}) As {Type(method.ReturnType)}{clause}\n    {Throws}\nEnd Function";
            }

            case IPropertySymbol property:
            {
                var parameters = property.Parameters.Length == 0
                    ? string.Empty
                    : "(" + string.Join(", ", property.Parameters.Select(Parameter)) + ")";

                var name = property.IsIndexer ? "Item" : property.Name;
                var defaultKeyword = property.IsIndexer ? "Default " : string.Empty;
                var hasGet = property.GetMethod is not null;
                var hasSet = property.SetMethod is not null;
                var mode = hasGet && hasSet ? string.Empty : hasGet ? "ReadOnly " : "WriteOnly ";

                var body = new StringBuilder();
                body.Append($"{accessibility} {defaultKeyword}{mode}Property {name}{parameters} As {Type(property.Type)}{clause}");
                if (hasGet) body.Append("\n    Get\n        ").Append(Throws).Append("\n    End Get");
                if (hasSet) body.Append("\n    Set(value As ").Append(Type(property.Type)).Append(")\n        ").Append(Throws).Append("\n    End Set");
                body.Append("\nEnd Property");
                return body.ToString();
            }

            case IEventSymbol @event:
                return $"{accessibility} Event {@event.Name} As {Type(@event.Type)}{clause}";

            default:
                return $"{accessibility} ReadOnly Property {member.Name} As Object\n" +
                       $"    Get\n        {Throws}\n    End Get\nEnd Property";
        }

        string Parameter(IParameterSymbol parameter)
        {
            var optional = parameter.IsOptional ? "Optional " : string.Empty;
            var byRef = parameter.RefKind == RefKind.None ? string.Empty : "ByRef ";
            var defaultValue = parameter.IsOptional ? " = Nothing" : string.Empty;
            return $"{optional}{byRef}{parameter.Name} As {Type(parameter.Type)}{defaultValue}";
        }
    }

    /// <summary>
    /// <c>Implements I.Member</c>, naming every interface of the type that declares a member with the
    /// same signature: two interfaces can share one member, and VB lets a single stub satisfy both.
    /// </summary>
    private static string ImplementsClause(
        ISymbol member, INamedTypeSymbol type, SemanticModel model, int position)
    {
        var signature = Signature(member);
        var owners = new List<string>();

        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var candidate in @interface.GetMembers())
            {
                if (Signature(candidate) != signature) continue;
                owners.Add($"{@interface.ToMinimalDisplayString(model, position)}.{candidate.Name}");
                break;
            }
        }

        return owners.Count == 0 ? string.Empty : " Implements " + string.Join(", ", owners);
    }

    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => "Private",
        Accessibility.Protected => "Protected",
        Accessibility.Internal => "Friend",
        Accessibility.ProtectedOrInternal => "Protected Friend",
        Accessibility.ProtectedAndInternal => "Private Protected",
        _ => "Public",
    };
}
