using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.VB.Style;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Dead code in Visual Basic: the same three questions the C# half asks — a statement after an
/// unconditional jump, a condition the compiler can fold, a <c>Select Case</c> arm that cannot match a
/// constant selector — read from VB's block syntax. VB has no <c>BlockSyntax</c> base type, so the
/// statement lists come from <see cref="VbBlocks"/>.
/// </summary>
public sealed partial class FindDeadCodeService
{
    private static void CollectVisualBasic(
        Document document, Project project, SyntaxNode root, SemanticModel model, List<object> findings)
    {
        FindStatementsAfterJumpVisualBasic(document, project, root, findings);
        FindConstantConditionsVisualBasic(document, project, root, model, findings);
        FindDeadSelectArmsVisualBasic(document, project, root, model, findings);
    }

    private static void FindStatementsAfterJumpVisualBasic(
        Document document, Project project, SyntaxNode root, List<object> findings)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            var statements = VbBlocks.StatementsOf(node);
            if (statements.Count == 0) continue;

            for (var index = 0; index < statements.Count; index++)
            {
                if (!IsJump(statements[index])) continue;

                var jump = JumpName(statements[index]);
                for (var later = index + 1; later < statements.Count; later++)
                {
                    var dead = statements[later];
                    findings.Add(new
                    {
                        Kind = $"statement after {jump}",
                        Detail = $"This statement is unreachable; it follows an unconditional {jump}.",
                        Project = project.Name,
                        document.FilePath,
                        Line = dead.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }

                // One jump per statement list: everything after the first is already dead.
                break;
            }
        }
    }

    private static void FindConstantConditionsVisualBasic(
        Document document, Project project, SyntaxNode root, SemanticModel model, List<object> findings)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MultiLineIfBlockSyntax @if:
                {
                    var condition = model.GetConstantValue(@if.IfStatement.Condition);
                    if (condition is { HasValue: true, Value: false })
                        Report("always-false if condition",
                            "Condition is always false; the If body is dead code.", @if, document, project, findings);
                    else if (condition is { HasValue: true, Value: true } && @if.ElseBlock is not null)
                        Report("always-true if condition",
                            "Condition is always true; the Else block is dead code.", @if.ElseBlock, document, project, findings);
                    break;
                }

                case ElseIfBlockSyntax elseIf:
                {
                    var condition = model.GetConstantValue(elseIf.ElseIfStatement.Condition);
                    if (condition is { HasValue: true, Value: false })
                        Report("always-false if condition",
                            "Condition is always false; this ElseIf block is dead code.", elseIf, document, project, findings);
                    break;
                }

                case SingleLineIfStatementSyntax singleLine:
                {
                    var condition = model.GetConstantValue(singleLine.Condition);
                    if (condition is { HasValue: true, Value: false })
                        Report("always-false if condition",
                            "Condition is always false; the If body is dead code.", singleLine, document, project, findings);
                    else if (condition is { HasValue: true, Value: true } && singleLine.ElseClause is not null)
                        Report("always-true if condition",
                            "Condition is always true; the Else branch is dead code.", singleLine.ElseClause, document, project, findings);
                    break;
                }
            }
        }
    }

    private static void FindDeadSelectArmsVisualBasic(
        Document document, Project project, SyntaxNode root, SemanticModel model, List<object> findings)
    {
        foreach (var select in root.DescendantNodes().OfType<SelectBlockSyntax>())
        {
            var selector = model.GetConstantValue(select.SelectStatement.Expression);
            if (!selector.HasValue) continue;

            foreach (var block in select.CaseBlocks)
            {
                if (CaseCanMatch(block.CaseStatement, selector.Value, model)) continue;

                Report("dead switch arm",
                    $"The Select Case selector is constant ({selector.Value}); this arm can never match.",
                    block.CaseStatement, document, project, findings);
            }
        }
    }

    /// <summary>
    /// Whether a case clause can match a known selector. A range or a relational clause is not
    /// evaluated — the answer would be "maybe", and a dead-arm finding must be certain.
    /// </summary>
    private static bool CaseCanMatch(CaseStatementSyntax statement, object? selector, SemanticModel model)
    {
        foreach (var clause in statement.Cases)
        {
            switch (clause)
            {
                case ElseCaseClauseSyntax:
                    return true;

                case SimpleCaseClauseSyntax simple:
                    var value = model.GetConstantValue(simple.Value);
                    if (value.HasValue && Equals(value.Value, selector)) return true;
                    break;

                default:
                    return true;
            }
        }

        return false;
    }

    private static void Report(
        string kind, string detail, SyntaxNode node, Document document, Project project, List<object> findings) =>
        findings.Add(new
        {
            Kind = kind,
            Detail = detail,
            Project = project.Name,
            document.FilePath,
            Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
        });

    private static bool IsJump(SyntaxNode node) => node
        is ReturnStatementSyntax or ThrowStatementSyntax or ExitStatementSyntax
        or ContinueStatementSyntax or GoToStatementSyntax or StopOrEndStatementSyntax;

    private static string JumpName(SyntaxNode node) => node switch
    {
        ReturnStatementSyntax => "return",
        ThrowStatementSyntax => "throw",
        ExitStatementSyntax => "exit",
        ContinueStatementSyntax => "continue",
        GoToStatementSyntax => "goto",
        StopOrEndStatementSyntax => "end",
        _ => "jump",
    };
}
