using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The Visual Basic member order: <c>code_cleanup</c>'s reorder step. VB declares its members as
/// statements inside a class, structure or module block, so the order is applied to the block's
/// <c>Members</c> list with the same access-then-kind-then-name rule the C# rewriter uses.
/// <para>
/// The VB access keywords (<c>Public</c>, <c>Protected</c>, <c>Friend</c>) sit on each member's own
/// statement, not on a shared modifier list: <see cref="ModifiersOf"/> reads the one that belongs to
/// the member kind, and a member with no accessibility keyword takes VB's default of Public inside a
/// module and Private inside a class, which is why <c>Dim</c> fields sort with the private ones.
/// </para>
/// </summary>
public sealed partial class ReorderMembersService
{
    private sealed class VbMemberReorderRewriter : VisualBasicSyntaxRewriter
    {
        public override SyntaxNode? VisitClassBlock(ClassBlockSyntax node) =>
            ReorderType(base.VisitClassBlock(node) as ClassBlockSyntax ?? node);

        public override SyntaxNode? VisitStructureBlock(StructureBlockSyntax node) =>
            ReorderType(base.VisitStructureBlock(node) as StructureBlockSyntax ?? node);

        public override SyntaxNode? VisitModuleBlock(ModuleBlockSyntax node) =>
            ReorderType(base.VisitModuleBlock(node) as ModuleBlockSyntax ?? node);

        private static TypeBlockSyntax ReorderType(TypeBlockSyntax type)
        {
            var ordered = type.Members
                .OrderBy(AccessOrder)
                .ThenBy(KindOrder)
                .ThenBy(MemberName, StringComparer.Ordinal)
                .ToList();

            // If the order didn't change, skip
            if (ordered.SequenceEqual(type.Members)) return type;

            // The first member carries the blank lines and comments that open the type body.
            if (type.Members.Count > 0 && ordered[0] != type.Members[0])
            {
                ordered[0] = (StatementSyntax)ordered[0]
                    .WithLeadingTrivia(type.Members[0].GetLeadingTrivia());
            }

            return type.WithMembers(SyntaxFactory.List(ordered));
        }

        private static int AccessOrder(StatementSyntax statement)
        {
            var modifiers = ModifiersOf(statement);
            if (modifiers.Any(SyntaxKind.PublicKeyword)) return 0;
            if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return 1;
            if (modifiers.Any(SyntaxKind.FriendKeyword)) return 2;
            return 3; // private or VB's default
        }

        private static int KindOrder(StatementSyntax statement) => statement switch
        {
            FieldDeclarationSyntax { Modifiers: var modifiers } field when modifiers.Any(SyntaxKind.ConstKeyword)
                                                                              => 0,
            FieldDeclarationSyntax { Modifiers: var modifiers } field when modifiers.Any(SyntaxKind.SharedKeyword) &&
                                                                          modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                                                                              => 1,
            FieldDeclarationSyntax { Modifiers: var modifiers } field when modifiers.Any(SyntaxKind.SharedKeyword)
                                                                              => 2,
            FieldDeclarationSyntax { Modifiers: var modifiers } field when modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                                                                              => 3,
            FieldDeclarationSyntax                                               => 4,
            ConstructorBlockSyntax                                               => 5,
            PropertyBlockSyntax or PropertyStatementSyntax                       => 6,
            EventStatementSyntax or EventBlockSyntax                             => 7,
            OperatorBlockSyntax or DeclareStatementSyntax                        => 8,
            MethodBlockSyntax                                                    => 9,
            TypeBlockSyntax or EnumBlockSyntax or DelegateStatementSyntax        => 10,
            _                                                                    => 11,
        };

        private static string MemberName(StatementSyntax statement) => statement switch
        {
            MethodBlockSyntax method                => method.SubOrFunctionStatement.Identifier.Text,
            ConstructorBlockSyntax constructor      => constructor.SubNewStatement.NewKeyword.Text,
            PropertyBlockSyntax property            => property.PropertyStatement.Identifier.Text,
            PropertyStatementSyntax property        => property.Identifier.Text,
            FieldDeclarationSyntax field            => field.Declarators.FirstOrDefault()?.Names.FirstOrDefault()?.Identifier.Text ?? "",
            EventStatementSyntax @event             => @event.Identifier.Text,
            EventBlockSyntax @event                 => @event.EventStatement.Identifier.Text,
            TypeBlockSyntax nested                  => nested.BlockStatement.Identifier.Text,
            EnumBlockSyntax @enum                   => @enum.EnumStatement.Identifier.Text,
            DelegateStatementSyntax @delegate       => @delegate.Identifier.Text,
            _                                       => "",
        };

        /// <summary>The accessibility tokens of the statement that actually declares the member.</summary>
        private static SyntaxTokenList ModifiersOf(StatementSyntax statement) => statement switch
        {
            MethodBlockSyntax method                => method.SubOrFunctionStatement.Modifiers,
            ConstructorBlockSyntax constructor      => constructor.SubNewStatement.Modifiers,
            OperatorBlockSyntax @operator           => @operator.OperatorStatement.Modifiers,
            PropertyBlockSyntax property            => property.PropertyStatement.Modifiers,
            PropertyStatementSyntax property        => property.Modifiers,
            FieldDeclarationSyntax field            => field.Modifiers,
            EventStatementSyntax @event             => @event.Modifiers,
            EventBlockSyntax @event                 => @event.EventStatement.Modifiers,
            TypeBlockSyntax nested                  => nested.BlockStatement.Modifiers,
            EnumBlockSyntax @enum                   => @enum.EnumStatement.Modifiers,
            DelegateStatementSyntax @delegate       => @delegate.Modifiers,
            _                                       => default,
        };
    }
}
