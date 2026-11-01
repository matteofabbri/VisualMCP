using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The Visual Basic half of documentation generation: which declarations can carry a <c>'''</c> block,
/// what a member is called, and where a declaration's symbol lives (VB attaches a field's symbol to its
/// declarator, not to the statement that declares it).
/// </summary>
public sealed partial class GenerateDocsService
{
    private static bool IsVisualBasicDocumentable(SyntaxNode node) => node is
        MethodBlockSyntax or ConstructorBlockSyntax or OperatorBlockSyntax or
        PropertyBlockSyntax or PropertyStatementSyntax or FieldDeclarationSyntax or
        EventStatementSyntax or EventBlockSyntax or DeclareStatementSyntax or
        ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax or InterfaceBlockSyntax or
        EnumBlockSyntax or EnumMemberDeclarationSyntax or DelegateStatementSyntax;

    private static bool IsVisualBasicTypeDeclaration(SyntaxNode node) => node is
        ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax or InterfaceBlockSyntax or
        EnumBlockSyntax or DelegateStatementSyntax;

    private static IEnumerable<SyntaxNode> VisualBasicMemberDeclarations(SyntaxNode type) => type switch
    {
        TypeBlockSyntax block => block.Members.Cast<SyntaxNode>(),
        EnumBlockSyntax @enum => @enum.Members.Cast<SyntaxNode>(),
        _ => [],
    };

    // RS1039 claims GetDeclaredSymbol(ModifiedIdentifierSyntax) always returns null; it does not — the
    // identifier inside a field declarator is the node VB attaches the field symbol to (probed).
#pragma warning disable RS1039
    private static ISymbol? VisualBasicDeclaredSymbol(
        SemanticModel model, SyntaxNode declaration, CancellationToken cancellationToken)
    {
        // A field's symbol belongs to its name, not to the statement: `Private _a, _b As Integer`
        // declares two symbols and the statement is not one of them.
        if (declaration is FieldDeclarationSyntax field)
        {
            var name = field.Declarators.SelectMany(declarator => declarator.Names).FirstOrDefault();
            return name is null ? null : model.GetDeclaredSymbol(name, cancellationToken);
        }

        return model.GetDeclaredSymbol(declaration, cancellationToken);
    }
#pragma warning restore RS1039

    private static string? VisualBasicNameOf(SyntaxNode node) => node switch
    {
        MethodBlockSyntax method            => method.SubOrFunctionStatement.Identifier.ValueText,
        ConstructorBlockSyntax constructor  => constructor.SubNewStatement.NewKeyword.Text,
        OperatorBlockSyntax @operator       => "Operator " + @operator.OperatorStatement.OperatorToken.Text,
        PropertyBlockSyntax property        => property.PropertyStatement.Identifier.ValueText,
        PropertyStatementSyntax property    => property.Identifier.ValueText,
        FieldDeclarationSyntax field        => field.Declarators.FirstOrDefault()?.Names.FirstOrDefault()?.Identifier.ValueText,
        EventStatementSyntax @event         => @event.Identifier.ValueText,
        EventBlockSyntax @event             => @event.EventStatement.Identifier.ValueText,
        DeclareStatementSyntax declare      => declare.Identifier.ValueText,
        TypeBlockSyntax nested              => nested.BlockStatement.Identifier.ValueText,
        EnumBlockSyntax @enum               => @enum.EnumStatement.Identifier.ValueText,
        EnumMemberDeclarationSyntax member  => member.Identifier.ValueText,
        DelegateStatementSyntax @delegate   => @delegate.Identifier.ValueText,
        _                                   => null,
    };
}
