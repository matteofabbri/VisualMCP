using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0066: a switch statement whose every section produces one value becomes a switch expression.
/// Only the two shapes that are a pure move are accepted: every section is <c>return value;</c>, or
/// every section is <c>target = value; break;</c> writing the same target. Anything else (fallthrough,
/// <c>goto case</c>, extra statements before the terminal) is left to the IDE.
/// </summary>
public sealed class SwitchExpressionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0066", "Convert switch statement to expression",
        "switch (x) { case 1: return a; default: return b; } → return x switch { 1 => a, _ => b };");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not SwitchStatementSyntax statement || statement.Sections.Count == 0) return null;
        if (ConditionalShape.HasComment(statement)) return null;

        var arms = new List<(string Pattern, ExpressionSyntax Value)>();
        string? target = null;
        var returns = false;
        var hasCatchAll = false;

        foreach (var section in statement.Sections)
        {
            if (!TryTerminal(section, model, cancellationToken, out var value, out var written, out var isReturn))
                return null;

            if (target is not null || returns)
            {
                // Every section must be the same shape and, for assignments, write the same target.
                if (isReturn != returns) return null;
            }

            if (isReturn) returns = true;
            else if (target is null) target = written;
            else if (written != target) return null;

            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        hasCatchAll = true;
                        arms.Add(("_", value));
                        break;

                    case CaseSwitchLabelSyntax constant:
                        arms.Add((constant.Value.ToString(), value));
                        break;

                    case CasePatternSwitchLabelSyntax pattern:
                        arms.Add((pattern.WhenClause is null
                            ? pattern.Pattern.ToString()
                            : $"{pattern.Pattern} when {pattern.WhenClause.Condition}", value));
                        break;

                    default:
                        return null;
                }
            }
        }

        // Without a catch-all the switch expression is not exhaustive, and the gate would refuse the
        // whole file for it.
        if (!hasCatchAll) return null;

        // A return statement ends the method: the switch must be the last thing in its block, or the
        // code after it is unreachable.
        if (returns && statement.Parent is BlockSyntax parent &&
            parent.Statements.IndexOf(statement) != parent.Statements.Count - 1)
        {
            return null;
        }

        var armText = string.Join(",\n", arms.Select(arm => $"    {arm.Pattern} => {arm.Value}"));
        var subject = $"{statement.Expression} switch\n{{\n{armText}\n}}";

        return MatchStatement(node, returns ? $"return {subject};" : $"{target} = {subject};");
    }

    /// <summary>
    /// The value a section produces, when the section is exactly one terminal statement. A section
    /// that falls through, jumps, or does anything before the value is not this shape.
    /// </summary>
    private static bool TryTerminal(
        SwitchSectionSyntax section,
        SemanticModel model,
        CancellationToken cancellationToken,
        out ExpressionSyntax value,
        out string? written,
        out bool isReturn)
    {
        value = null!;
        written = null;
        isReturn = false;

        var statements = section.Statements;

        if (statements.Count == 1 && statements[0] is ReturnStatementSyntax { Expression: { } returned })
        {
            value = returned;
            isReturn = true;
            return true;
        }

        if (statements[0] is not ExpressionStatementSyntax
            { Expression: AssignmentExpressionSyntax assignment }) return false;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;

        // `target = value;` followed by nothing (last section) or by exactly one `break;`.
        if (statements.Count == 2 && statements[1] is not BreakStatementSyntax) return false;
        if (statements.Count > 2) return false;

        if (model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is null) return false;

        value = assignment.Right;
        written = assignment.Left.ToString();
        return true;
    }
}
