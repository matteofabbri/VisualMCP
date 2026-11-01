using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// The statement lists of Visual Basic, for the shared service. C# has one <c>BlockSyntax</c> that owns
/// every statement list; VB has no such public base type, so this is the one place that knows the block
/// kinds. Each entry is a node that owns a <c>Statements</c> list, not a node that *is* a statement list
/// (<c>CaseBlockSyntax</c> is one, <c>SynclockBlockSyntax</c> is the other half of the same shape).
/// <para>
/// A kind that is missing here is a kind where a statement rewrite silently does nothing, because the
/// service only splices a match when <see cref="StatementsOf"/> answers with a non-empty list. The list
/// covers every block that can hold executable statements in VB 15+, plus the type and namespace blocks
/// whose member list is the same <c>SyntaxList(Of StatementSyntax)</c> — which is what lets a rule fix a
/// member's header (modifiers, accessibility) or remove an unused member as one splice.
/// </para>
/// </summary>
internal static class VbBlocks
{
    public static IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent) => parent switch
    {
        MethodBlockBaseSyntax block => Nodes(block.Statements),
        MultiLineIfBlockSyntax block => Nodes(block.Statements),
        ElseIfBlockSyntax block => Nodes(block.Statements),
        ElseBlockSyntax block => Nodes(block.Statements),
        SingleLineIfStatementSyntax block => Nodes(block.Statements),
        SingleLineElseClauseSyntax block => Nodes(block.Statements),
        ForOrForEachBlockSyntax block => Nodes(block.Statements),
        WhileBlockSyntax block => Nodes(block.Statements),
        DoLoopBlockSyntax block => Nodes(block.Statements),
        UsingBlockSyntax block => Nodes(block.Statements),
        SyncLockBlockSyntax block => Nodes(block.Statements),
        WithBlockSyntax block => Nodes(block.Statements),
        TryBlockSyntax block => Nodes(block.Statements),
        CatchBlockSyntax block => Nodes(block.Statements),
        FinallyBlockSyntax block => Nodes(block.Statements),
        CaseBlockSyntax block => Nodes(block.Statements),
        // A type and a namespace hold members, and a VB member IS a statement here (a method block, a
        // field declaration). The member-level rules — modifier order, accessibility, readonly, an
        // unused member being removed — are spliced through the same list executable statements use.
        TypeBlockSyntax block => Nodes(block.Members),
        NamespaceBlockSyntax block => Nodes(block.Members),
        _ => [],
    };

    public static SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements)
    {
        var list = SyntaxFactory.List(statements.Select(statement => (StatementSyntax)statement));

        return parent switch
        {
            MethodBlockBaseSyntax block => block.WithStatements(list),
            MultiLineIfBlockSyntax block => block.WithStatements(list),
            ElseIfBlockSyntax block => block.WithStatements(list),
            ElseBlockSyntax block => block.WithStatements(list),
            SingleLineIfStatementSyntax block => block.WithStatements(list),
            SingleLineElseClauseSyntax block => block.WithStatements(list),
            ForOrForEachBlockSyntax block => block.WithStatements(list),
            WhileBlockSyntax block => block.WithStatements(list),
            DoLoopBlockSyntax block => block.WithStatements(list),
            UsingBlockSyntax block => block.WithStatements(list),
            SyncLockBlockSyntax block => block.WithStatements(list),
            WithBlockSyntax block => block.WithStatements(list),
            TryBlockSyntax block => block.WithStatements(list),
            CatchBlockSyntax block => block.WithStatements(list),
            FinallyBlockSyntax block => block.WithStatements(list),
            CaseBlockSyntax block => block.WithStatements(list),
            TypeBlockSyntax block => block.WithMembers(list),
            NamespaceBlockSyntax block => block.WithMembers(list),
            _ => parent,
        };
    }

    private static IReadOnlyList<SyntaxNode> Nodes(SyntaxList<StatementSyntax> statements) =>
        statements.Select(statement => (SyntaxNode)statement).ToList();
}
