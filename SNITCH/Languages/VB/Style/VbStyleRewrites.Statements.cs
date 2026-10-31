using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.VB.Style;

// The statement-shaped rules: a branch fold, an unreachable tail, a Select Case that names its enum, and
// the two LINQ chains. Each of them replaces one statement (or a run of them) with one statement.

/// <summary>
/// IDE0035: statements after an unconditional jump in the same list cannot run, so they are removed —
/// up to the end of the list, because a label can be the target of a <c>GoTo</c> that lands further down.
/// </summary>
public sealed class UnreachableCodeRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0035", "Remove unreachable code", "statements after Return, Throw, Exit or Continue are removed");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not StatementSyntax statement || node.Parent is null) return null;

        var siblings = VbBlocks.StatementsOf(node.Parent);
        var index = VbStyleFacts.IndexOf(siblings, statement);
        if (index <= 0) return null;

        if (!IsJump((StatementSyntax)siblings[index - 1])) return null;

        var last = statement;
        for (var next = index; next < siblings.Count; next++)
        {
            if (siblings[next] is LabelStatementSyntax) break;
            last = (StatementSyntax)siblings[next];
        }

        return Removal(statement, last);
    }

    private static bool IsJump(StatementSyntax statement) => statement is
        ReturnStatementSyntax or ThrowStatementSyntax or ExitStatementSyntax or ContinueStatementSyntax
        or StopOrEndStatementSyntax or GoToStatementSyntax or YieldStatementSyntax;
}

/// <summary>
/// IDE0046: <c>If c Then Return a Else Return b</c> → <c>Return If(c, a, b)</c>. Both forms evaluate the
/// condition once and exactly one branch, so the fold is a pure shape change.
/// </summary>
public sealed class ConditionalReturnRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0046", "Convert to conditional expression",
        "If c Then Return a Else Return b End If → Return If(c, a, b)");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (ConditionalReturnRewrite.Branches(node) is not { } branches) return null;
        var (condition, whenTrue, whenFalse) = branches;
        if (whenTrue is not ReturnStatementSyntax first || whenFalse is not ReturnStatementSyntax second) return null;
        if (first.Expression is null || second.Expression is null) return null;

        return MatchStatement(node,
            "Return If(" + condition + ", " + first.Expression + ", " + second.Expression + ")");
    }

    /// <summary>The one-statement, one-branch shape both conditional rules fold.</summary>
    internal static (ExpressionSyntax Condition, StatementSyntax WhenTrue, StatementSyntax WhenFalse)? Branches(SyntaxNode node)
    {
        switch (node)
        {
            case MultiLineIfBlockSyntax block
                when block.ElseIfBlocks.Count == 0 &&
                     block.Statements.Count == 1 &&
                     block.ElseBlock is { Statements.Count: 1 } elseBlock:
                return (block.IfStatement.Condition, block.Statements[0], elseBlock.Statements[0]);

            case SingleLineIfStatementSyntax single
                when single.Statements.Count == 1 &&
                     single.ElseClause is { Statements.Count: 1 } clause:
                return (single.Condition, single.Statements[0], clause.Statements[0]);

            default:
                return null;
        }
    }
}

/// <summary>IDE0045: the same fold for an assignment, when both branches store into the same target.</summary>
public sealed class ConditionalAssignmentRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0045", "Convert to conditional expression",
        "If c Then x = a Else x = b End If → x = If(c, a, b)");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (ConditionalReturnRewrite.Branches(node) is not { } branches) return null;
        var (condition, whenTrue, whenFalse) = branches;
        if (whenTrue is not AssignmentStatementSyntax first || whenFalse is not AssignmentStatementSyntax second) return null;
        if (!first.IsKind(SyntaxKind.SimpleAssignmentStatement) || !second.IsKind(SyntaxKind.SimpleAssignmentStatement))
            return null;

        var target = context.Model.GetSymbolInfo(first.Left, context.CancellationToken).Symbol;
        var other = context.Model.GetSymbolInfo(second.Left, context.CancellationToken).Symbol;
        if (target is null || !SymbolEqualityComparer.Default.Equals(target, other)) return null;

        return MatchStatement(node,
            first.Left + " = If(" + condition + ", " + first.Right + ", " + second.Right + ")");
    }
}

/// <summary>
/// IDE0010: a <c>Select Case</c> over an enum that names only some of its values gets one case per
/// missing member. A <c>Case Else</c>, a range or a relational case already answers for the values this
/// rule would add, so those selects are left alone.
/// </summary>
public sealed class MissingSelectCasesRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0010", "Add missing cases to a Select Case",
        "Select Case s … End Select → one Case per enum member nothing names yet");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not SelectBlockSyntax select) return null;
        if (context.Model.GetTypeInfo(select.SelectStatement.Expression, context.CancellationToken).Type
            is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration)
            return null;

        var named = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        foreach (var block in select.CaseBlocks)
        {
            foreach (var clause in block.CaseStatement.Cases)
            {
                // Case Else, Case 1 To 5 and Case Is > x already answer for more than one value.
                if (clause is not SimpleCaseClauseSyntax simple) return null;

                if (context.Model.GetSymbolInfo(simple.Value, context.CancellationToken).Symbol is IFieldSymbol field &&
                    SymbolEqualityComparer.Default.Equals(field.ContainingType, enumeration))
                    named.Add(field);
            }
        }

        var missing = enumeration.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && !named.Contains(field))
            .ToList();
        if (missing.Count == 0) return null;

        var text = select.ToString();
        var end = select.EndSelectStatement.SpanStart - select.SpanStart;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var indent = select.CaseBlocks.Count > 0
            ? IndentationOf(context, select.CaseBlocks[0])
            : IndentationOf(context, select) + "    ";

        var cases = string.Concat(missing.Select(field =>
            indent + "Case " + enumeration.Name + "." + field.Name + newLine));

        return MatchStatement(select, text[..end] + cases + text[end..]);
    }

    private static string IndentationOf(StyleContext context, SyntaxNode node)
    {
        var line = context.Model.SyntaxTree.GetText().Lines.GetLineFromPosition(node.SpanStart);
        return new string(line.ToString().TakeWhile(char.IsWhiteSpace).ToArray());
    }
}

/// <summary>
/// IDE0017: a local created empty and filled member by member becomes one object initializer. The
/// assignments are consecutive and name the local, so folding them into the creation keeps the order in
/// which the values are evaluated.
/// </summary>
public sealed class ObjectInitializerRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0017", "Use an object initializer",
        "Dim x = New C() : x.A = 1 → Dim x = New C() With {.A = 1}");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not LocalDeclarationStatementSyntax declaration || declaration.Parent is null) return null;
        if (declaration.Declarators.Count != 1) return null;

        var declarator = declaration.Declarators[0];
        if (declarator.Names.Count != 1) return null;
        if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation) return null;
        if (creation.Initializer is not null) return null;

        if (context.Model.GetDeclaredSymbol(declarator.Names[0], context.CancellationToken) is not ILocalSymbol local)
            return null;

        var siblings = VbBlocks.StatementsOf(declaration.Parent);
        var index = VbStyleFacts.IndexOf(siblings, declaration);
        if (index < 0) return null;

        var initializers = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StatementSyntax? last = null;

        for (var next = index + 1; next < siblings.Count; next++)
        {
            if (siblings[next] is not AssignmentStatementSyntax assignment) break;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)) break;
            if (assignment.Left is not MemberAccessExpressionSyntax member || !member.IsKind(SyntaxKind.SimpleMemberAccessExpression)) break;
            if (member.Expression is not IdentifierNameSyntax receiver ||
                !SymbolEqualityComparer.Default.Equals(
                    context.Model.GetSymbolInfo(receiver, context.CancellationToken).Symbol, local))
                break;

            var name = member.Name.Identifier.ValueText;
            if (name.Length == 0 || !names.Add(name)) break;

            initializers.Add("." + name + " = " + assignment.Right);
            last = assignment;
        }

        if (last is null || initializers.Count == 0) return null;

        var text = declaration.ToString();
        var creationStart = creation.SpanStart - declaration.SpanStart;
        var replacement = text[..creationStart] + creation + " With {" + string.Join(", ", initializers) + "}";

        return new StyleMatch(
            Rule, node, ParseExecutableStatement(replacement).WithTriviaFrom(node),
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            node.ToString(), replacement, Last: last);
    }
}

/// <summary>
/// IDE0059: an assignment whose value is overwritten before anything reads it. The verdict comes from
/// Roslyn's data-flow analysis, not from matching text: a statement that mentions the variable is not
/// necessarily a read, and one that does not mention it can still read it through a closure.
/// </summary>
public sealed class UnnecessaryAssignmentRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0059", "Remove unnecessary value assignment",
        "x = a : x = b → x = b");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AssignmentStatementSyntax statement) return null;
        if (!statement.IsKind(SyntaxKind.SimpleAssignmentStatement)) return null;
        if (statement.Left is not IdentifierNameSyntax target) return null;
        if (!VbStyleFacts.IsSideEffectFree(statement.Right)) return null;

        if (model.GetSymbolInfo(target, cancellationToken).Symbol is not ILocalSymbol local) return null;
        if (VbStyleFacts.Reads(statement, local, model, cancellationToken)) return null;
        if (!VbStyleFacts.IsOverwrittenBeforeRead(statement, local, model, cancellationToken)) return null;

        return Removal(statement);
    }
}

/// <summary>
/// IDE0070: a hand-written multiply-and-add hash fold becomes <c>HashCode.Combine</c>. Only the exact
/// shape is recognised — a seed local, one <c>hash * constant + value.GetHashCode()</c> per value, then
/// <c>Return hash</c> — because anything else has a meaning this rule would not preserve.
/// </summary>
public sealed class HashCodeCombineRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0070", "Use System.HashCode.Combine",
        "hash = hash * 31 + name.GetHashCode() … → Return HashCode.Combine(name, …)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MethodBlockSyntax method) return null;
        if (!string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, "GetHashCode", StringComparison.Ordinal))
            return null;
        if (method.SubOrFunctionStatement.ParameterList?.Parameters.Count is not (0 or null)) return null;
        if (method.SubOrFunctionStatement.Modifiers.All(token => !token.IsKind(SyntaxKind.OverridesKeyword))) return null;
        if (model.Compilation.GetTypeByMetadataName("System.HashCode") is null) return null;

        var statements = method.Statements;
        if (statements.Count < 3) return null;

        var start = 0;
        if (statements[0] is LocalDeclarationStatementSyntax seed)
        {
            if (seed.Declarators.Count != 1 || seed.Declarators[0].Initializer?.Value is not LiteralExpressionSyntax)
                return null;
            start = 1;
        }

        if (statements.Count - start < 2) return null;

        var values = new List<string>();

        for (var index = start; index < statements.Count - 1; index++)
        {
            if (statements[index] is not AssignmentStatementSyntax assignment) return null;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)) return null;
            if (assignment.Left is not IdentifierNameSyntax accumulator) return null;
            if (assignment.Right is not BinaryExpressionSyntax addition || !addition.IsKind(SyntaxKind.AddExpression))
                return null;
            if (addition.Left is not BinaryExpressionSyntax product || !product.IsKind(SyntaxKind.MultiplyExpression))
                return null;
            if (product.Left is not IdentifierNameSyntax same || same.Identifier.ValueText != accumulator.Identifier.ValueText)
                return null;
            if (product.Right is not LiteralExpressionSyntax) return null;
            if (addition.Right is not InvocationExpressionSyntax call) return null;
            if (call.Expression is not MemberAccessExpressionSyntax member ||
                !string.Equals(member.Name.Identifier.ValueText, "GetHashCode", StringComparison.Ordinal) ||
                call.ArgumentList.Arguments.Count != 0)
                return null;

            values.Add(member.Expression.ToString());
        }

        if (values.Count is < 2 or > 8) return null;
        if (statements[^1] is not ReturnStatementSyntax) return null;

        var body = "Return HashCode.Combine(" + string.Join(", ", values) + ")";
        var newLine = method.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var text = method.SubOrFunctionStatement + newLine + body + newLine + method.EndSubOrFunctionStatement;

        return MatchMember(node, text);
    }
}

/// <summary>
/// IDE0120: <c>xs.Where(predicate).Any()</c> → <c>xs.Any(predicate)</c>. The check is semantic: the
/// operators have to be <c>System.Linq.Enumerable</c>'s, or a user type that defines its own keeps its
/// shape.
/// </summary>
public sealed class LinqWhereChainRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0120", "Simplify LINQ chain", "xs.Where(f).Any() → xs.Any(f)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax anyInvocation) return null;
        if (anyInvocation.ArgumentList.Arguments.Count != 0) return null;
        if (anyInvocation.Expression is not MemberAccessExpressionSyntax anyMember) return null;
        if (!string.Equals(anyMember.Name.Identifier.ValueText, "Any", StringComparison.Ordinal)) return null;
        if (anyMember.Expression is not InvocationExpressionSyntax whereInvocation) return null;
        if (whereInvocation.Expression is not MemberAccessExpressionSyntax whereMember) return null;
        if (!string.Equals(whereMember.Name.Identifier.ValueText, "Where", StringComparison.Ordinal)) return null;
        if (whereInvocation.ArgumentList.Arguments.Count != 1) return null;

        if (!IsEnumerable(model.GetSymbolInfo(anyInvocation, cancellationToken).Symbol)) return null;
        if (!IsEnumerable(model.GetSymbolInfo(whereInvocation, cancellationToken).Symbol)) return null;

        var predicate = whereInvocation.ArgumentList.Arguments[0].ToString();
        return MatchExpression(node, whereMember.Expression + ".Any(" + predicate + ")");
    }

    internal static bool IsEnumerable(ISymbol? symbol) =>
        symbol is IMethodSymbol { IsExtensionMethod: true } method &&
        string.Equals(method.ContainingType?.ToDisplayString(), "System.Linq.Enumerable", StringComparison.Ordinal);
}

/// <summary>
/// IDE0121: <c>xs.Where(Function(o) TypeOf o Is T).Cast(Of T)()</c> → <c>xs.OfType(Of T)()</c>. The
/// predicate has to be exactly the type test, or the rewrite would drop the rest of the condition.
/// </summary>
public sealed class LinqTypeCastRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0121", "Simplify LINQ chain",
        "xs.Where(Function(o) TypeOf o Is T).Cast(Of T)() → xs.OfType(Of T)()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax castInvocation) return null;
        if (castInvocation.ArgumentList.Arguments.Count != 0) return null;
        if (castInvocation.Expression is not MemberAccessExpressionSyntax castMember) return null;
        if (castMember.Name is not GenericNameSyntax generic ||
            !string.Equals(generic.Identifier.ValueText, "Cast", StringComparison.Ordinal))
            return null;
        if (generic.TypeArgumentList.Arguments.Count != 1) return null;

        if (castMember.Expression is not InvocationExpressionSyntax whereInvocation) return null;
        if (whereInvocation.Expression is not MemberAccessExpressionSyntax whereMember) return null;
        if (!string.Equals(whereMember.Name.Identifier.ValueText, "Where", StringComparison.Ordinal)) return null;
        if (whereInvocation.ArgumentList.Arguments.Count != 1) return null;

        if (whereInvocation.ArgumentList.Arguments[0] is not SimpleArgumentSyntax { Expression: SingleLineLambdaExpressionSyntax lambda })
            return null;
        if (lambda.Body is not TypeOfExpressionSyntax typeCheck) return null;
        if (!typeCheck.IsKind(SyntaxKind.TypeOfIsExpression)) return null;

        if (!LinqWhereChainRewrite.IsEnumerable(model.GetSymbolInfo(castInvocation, cancellationToken).Symbol))
            return null;
        if (!LinqWhereChainRewrite.IsEnumerable(model.GetSymbolInfo(whereInvocation, cancellationToken).Symbol))
            return null;

        // The tested type and the cast target have to be the same type, or OfType would filter differently.
        var tested = model.GetTypeInfo(typeCheck.Type, cancellationToken).Type;
        var target = model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Symbol as ITypeSymbol;
        if (tested is null || target is null || !SymbolEqualityComparer.Default.Equals(tested, target)) return null;

        return MatchExpression(node,
            whereMember.Expression + ".OfType(Of " + generic.TypeArgumentList.Arguments[0] + ")()");
    }
}
