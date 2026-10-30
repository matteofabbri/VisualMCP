using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Metacode.Frontends;

internal sealed partial class CSharpLowering
{
    private MetacodeExpression Expression(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return Literal(literal);

            case InterpolatedStringExpressionSyntax interpolated:
                return Interpolation(interpolated);

            case IdentifierNameSyntax identifier:
                return new MetacodeIdentifier(identifier.Identifier.Text) { Origin = OriginOf(identifier) };

            case GenericNameSyntax generic:
                return new MetacodeIdentifier(generic.ToString()) { Origin = OriginOf(generic) };

            case PredefinedTypeSyntax predefined:
                return new MetacodeIdentifier(predefined.Keyword.Text) { Origin = OriginOf(predefined) };

            case ThisExpressionSyntax:
                return new MetacodeIdentifier("this") { Origin = OriginOf(expression) };

            case BaseExpressionSyntax:
                return new MetacodeIdentifier("base") { Origin = OriginOf(expression) };

            case ParenthesizedExpressionSyntax parenthesized:
                return Expression(parenthesized.Expression);

            case MemberAccessExpressionSyntax member:
                return new MetacodeMemberAccess(Expression(member.Expression), member.Name.Identifier.Text)
                {
                    Origin = OriginOf(member),
                };

            case ConditionalAccessExpressionSyntax conditionalAccess:
                return ConditionalAccess(conditionalAccess);

            case InvocationExpressionSyntax invocation:
                return Invocation(invocation);

            case ElementAccessExpressionSyntax elementAccess:
                return new MetacodeIndex(
                    Expression(elementAccess.Expression),
                    elementAccess.ArgumentList.Arguments.Select(a => Expression(a.Expression)).ToList())
                {
                    Origin = OriginOf(elementAccess),
                };

            case BinaryExpressionSyntax binary:
                return Binary(binary);

            case PrefixUnaryExpressionSyntax prefix:
                return Unary(prefix, prefix.Kind(), postfix: false);

            case PostfixUnaryExpressionSyntax postfix:
                return Unary(postfix, postfix.Kind(), postfix: true);

            case AssignmentExpressionSyntax assignment:
                return new MetacodeAssignmentExpression(
                    Expression(assignment.Left),
                    AssignmentOperator(assignment.Kind()),
                    Expression(assignment.Right)) { Origin = OriginOf(assignment) };

            case ConditionalExpressionSyntax conditional:
                return new MetacodeConditional(
                    Expression(conditional.Condition),
                    Expression(conditional.WhenTrue),
                    Expression(conditional.WhenFalse)) { Origin = OriginOf(conditional) };

            case ObjectCreationExpressionSyntax creation:
                return ObjectCreation(creation.Type, creation.ArgumentList, creation.Initializer, OriginOf(creation));

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return ObjectCreation(null, implicitCreation.ArgumentList, implicitCreation.Initializer, OriginOf(implicitCreation));

            case AnonymousObjectCreationExpressionSyntax anonymousObject:
                return new MetacodeAnonymousObject(AnonymousMembers(anonymousObject)) { Origin = OriginOf(anonymousObject) };

            case WithExpressionSyntax withExpression:
                return new MetacodeWithExpression(
                    Expression(withExpression.Expression),
                    InitializerMembers(withExpression.Initializer)) { Origin = OriginOf(withExpression) };

            case ArrayCreationExpressionSyntax array:
                return new MetacodeNewArray(
                    TypeRef(array.Type.ElementType),
                    Sizes: Sizes(array.Type),
                    Elements: array.Initializer?.Expressions.Select(Expression).ToList()) { Origin = OriginOf(array) };

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return new MetacodeNewArray(null, Elements: implicitArray.Initializer.Expressions.Select(Expression).ToList())
                {
                    Origin = OriginOf(implicitArray),
                };

            case CastExpressionSyntax cast:
                return new MetacodeCast(TypeRef(cast.Type), Expression(cast.Expression)) { Origin = OriginOf(cast) };

            case IsPatternExpressionSyntax pattern:
                return Pattern(pattern);

            case LambdaExpressionSyntax lambda:
                return Lambda(lambda);

            case AnonymousMethodExpressionSyntax anonymous:
                return AnonymousMethod(anonymous);

            case AwaitExpressionSyntax awaitExpression:
                return new MetacodeAwait(Expression(awaitExpression.Expression)) { Origin = OriginOf(awaitExpression) };

            case DefaultExpressionSyntax defaultExpression:
                return new MetacodeLiteral(
                    MetacodeLiteralKind.Default,
                    "default(" + TypeRef(defaultExpression.Type).Render() + ")") { Origin = OriginOf(defaultExpression) };

            case TypeOfExpressionSyntax typeOf:
                return new MetacodeTypeOf(TypeRef(typeOf.Type)) { Origin = OriginOf(typeOf) };

            case CheckedExpressionSyntax checkedExpression:
                return Expression(checkedExpression.Expression);

            case RangeExpressionSyntax range:
                return new MetacodeRange(
                    range.LeftOperand is null ? null : Expression(range.LeftOperand),
                    range.RightOperand is null ? null : Expression(range.RightOperand)) { Origin = OriginOf(range) };

            case TupleExpressionSyntax tuple:
                return new MetacodeCollection(
                    MetacodeCollectionKind.Tuple,
                    tuple.Arguments.Select(a => Expression(a.Expression)).ToList()) { Origin = OriginOf(tuple) };

            case CollectionExpressionSyntax collection:
                return new MetacodeCollection(MetacodeCollectionKind.Array, CollectionElements(collection)) { Origin = OriginOf(collection) };

            case SwitchExpressionSyntax switchExpression:
                return SwitchExpression(switchExpression);

            case InitializerExpressionSyntax initializer:
                return new MetacodeCollection(MetacodeCollectionKind.Array, initializer.Expressions.Select(Expression).ToList())
                {
                    Origin = OriginOf(initializer),
                };

            case RefExpressionSyntax reference:
                return Expression(reference.Expression);

            case DeclarationExpressionSyntax declaration:
                return new MetacodeIdentifier(declaration.Designation.ToString()) { Origin = OriginOf(declaration) };

            default:
                return OpaqueExpression(expression, "unmodelled expression: " + expression.Kind());
        }
    }

    private static MetacodeExpression Literal(LiteralExpressionSyntax literal)
    {
        var origin = OriginOf(literal);
        return literal.Kind() switch
        {
            SyntaxKind.NumericLiteralExpression => new MetacodeLiteral(MetacodeLiteralKind.Number, literal.Token.Text) { Origin = origin },
            SyntaxKind.StringLiteralExpression => new MetacodeLiteral(MetacodeLiteralKind.String, literal.Token.Text) { Origin = origin },
            SyntaxKind.CharacterLiteralExpression => new MetacodeLiteral(MetacodeLiteralKind.Character, literal.Token.Text) { Origin = origin },
            SyntaxKind.TrueLiteralExpression => MetacodeLiteral.True with { Origin = origin },
            SyntaxKind.FalseLiteralExpression => MetacodeLiteral.False with { Origin = origin },
            SyntaxKind.NullLiteralExpression => MetacodeLiteral.Null with { Origin = origin },
            SyntaxKind.DefaultLiteralExpression => new MetacodeLiteral(MetacodeLiteralKind.Default, "default") { Origin = origin },
            _ => new MetacodeLiteral(MetacodeLiteralKind.Raw, literal.Token.Text) { Origin = origin },
        };
    }

    private MetacodeExpression Interpolation(InterpolatedStringExpressionSyntax node)
    {
        var parts = new List<MetacodeInterpolationPart>(node.Contents.Count);
        foreach (var content in node.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new MetacodeInterpolationPart(text.TextToken.ValueText, null));
                    break;
                case InterpolationSyntax interpolation:
                    parts.Add(new MetacodeInterpolationPart(null, Expression(interpolation.Expression)));
                    break;
            }
        }

        return new MetacodeInterpolatedString(parts) { Origin = OriginOf(node) };
    }

    private MetacodeExpression Invocation(InvocationExpressionSyntax node) =>
        new MetacodeInvocation(Expression(node.Expression), Arguments(node.ArgumentList)) { Origin = OriginOf(node) };

    private MetacodeExpression ConditionalAccess(ConditionalAccessExpressionSyntax node) =>
        BindConditional(Expression(node.Expression), node.WhenNotNull);

    private MetacodeExpression BindConditional(MetacodeExpression receiver, ExpressionSyntax whenNotNull)
    {
        switch (whenNotNull)
        {
            case MemberBindingExpressionSyntax member:
                return new MetacodeMemberAccess(receiver, member.Name.Identifier.Text, NullConditional: true) { Origin = OriginOf(member) };

            case ElementBindingExpressionSyntax element:
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.LossyLowering, "a null-conditional index is rendered as a plain index", element));
                return new MetacodeIndex(receiver, element.ArgumentList.Arguments.Select(a => Expression(a.Expression)).ToList())
                {
                    Origin = OriginOf(element),
                };

            case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax member:
                return new MetacodeInvocation(
                    new MetacodeMemberAccess(receiver, member.Name.Identifier.Text, NullConditional: true) { Origin = OriginOf(member) },
                    Arguments(invocation.ArgumentList)) { Origin = OriginOf(invocation) };

            case MemberAccessExpressionSyntax memberAccess:
                return new MetacodeMemberAccess(BindConditional(receiver, memberAccess.Expression), memberAccess.Name.Identifier.Text)
                {
                    Origin = OriginOf(memberAccess),
                };

            case InvocationExpressionSyntax invocation:
                return new MetacodeInvocation(BindConditional(receiver, invocation.Expression), Arguments(invocation.ArgumentList))
                {
                    Origin = OriginOf(invocation),
                };

            default:
                return OpaqueExpression(whenNotNull, "unmodelled conditional access: " + whenNotNull.Kind());
        }
    }

    private MetacodeExpression Binary(BinaryExpressionSyntax node)
    {
        var origin = OriginOf(node);
        switch (node.Kind())
        {
            case SyntaxKind.IsExpression:
                return new MetacodeTypeCheck(Expression(node.Left), TypeOf(node.Right), MetacodeTypeCheckKind.Is) { Origin = origin };
            case SyntaxKind.AsExpression:
                return new MetacodeTypeCheck(Expression(node.Left), TypeOf(node.Right), MetacodeTypeCheckKind.As) { Origin = origin };
        }

        var @operator = node.Kind() switch
        {
            SyntaxKind.AddExpression => MetacodeBinaryOperator.Add,
            SyntaxKind.SubtractExpression => MetacodeBinaryOperator.Subtract,
            SyntaxKind.MultiplyExpression => MetacodeBinaryOperator.Multiply,
            SyntaxKind.DivideExpression => MetacodeBinaryOperator.Divide,
            SyntaxKind.ModuloExpression => MetacodeBinaryOperator.Modulo,
            SyntaxKind.EqualsExpression => MetacodeBinaryOperator.Equal,
            SyntaxKind.NotEqualsExpression => MetacodeBinaryOperator.NotEqual,
            SyntaxKind.LessThanExpression => MetacodeBinaryOperator.LessThan,
            SyntaxKind.LessThanOrEqualExpression => MetacodeBinaryOperator.LessThanOrEqual,
            SyntaxKind.GreaterThanExpression => MetacodeBinaryOperator.GreaterThan,
            SyntaxKind.GreaterThanOrEqualExpression => MetacodeBinaryOperator.GreaterThanOrEqual,
            SyntaxKind.LogicalAndExpression => MetacodeBinaryOperator.LogicalAnd,
            SyntaxKind.LogicalOrExpression => MetacodeBinaryOperator.LogicalOr,
            SyntaxKind.BitwiseAndExpression => MetacodeBinaryOperator.BitwiseAnd,
            SyntaxKind.BitwiseOrExpression => MetacodeBinaryOperator.BitwiseOr,
            SyntaxKind.ExclusiveOrExpression => MetacodeBinaryOperator.BitwiseXor,
            SyntaxKind.LeftShiftExpression => MetacodeBinaryOperator.ShiftLeft,
            SyntaxKind.RightShiftExpression => MetacodeBinaryOperator.ShiftRight,
            SyntaxKind.CoalesceExpression => MetacodeBinaryOperator.NullCoalesce,
            _ => (MetacodeBinaryOperator?)null,
        };

        return @operator is null
            ? OpaqueExpression(node, "unmodelled binary operator: " + node.Kind())
            : new MetacodeBinary(@operator.Value, Expression(node.Left), Expression(node.Right)) { Origin = origin };
    }

    private MetacodeExpression Unary(ExpressionSyntax node, SyntaxKind kind, bool postfix)
    {
        var origin = OriginOf(node);
        var operand = node switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfixExpression => postfixExpression.Operand,
            _ => null,
        };

        if (operand is null)
            return OpaqueExpression(node, "unmodelled unary operator: " + kind);

        var @operator = kind switch
        {
            SyntaxKind.LogicalNotExpression => MetacodeUnaryOperator.Not,
            SyntaxKind.UnaryMinusExpression => MetacodeUnaryOperator.Negate,
            SyntaxKind.UnaryPlusExpression => MetacodeUnaryOperator.Plus,
            SyntaxKind.BitwiseNotExpression => MetacodeUnaryOperator.BitwiseNot,
            SyntaxKind.PreIncrementExpression or SyntaxKind.PostIncrementExpression => MetacodeUnaryOperator.Increment,
            SyntaxKind.PreDecrementExpression or SyntaxKind.PostDecrementExpression => MetacodeUnaryOperator.Decrement,
            SyntaxKind.SuppressNullableWarningExpression => (MetacodeUnaryOperator?)null,
            _ => (MetacodeUnaryOperator?)null,
        };

        if (kind == SyntaxKind.SuppressNullableWarningExpression)
            return Expression(operand);

        if (kind == SyntaxKind.IndexExpression)
            return new MetacodeIndexFromEnd(Expression(operand)) { Origin = origin };

        return @operator is null
            ? OpaqueExpression(node, "unmodelled unary operator: " + kind)
            : new MetacodeUnary(@operator.Value, Expression(operand), postfix) { Origin = origin };
    }

    private MetacodeExpression Pattern(IsPatternExpressionSyntax node)
    {
        var origin = OriginOf(node);
        var operand = Expression(node.Expression);

        switch (node.Pattern)
        {
            case ConstantPatternSyntax constant:
                return new MetacodeBinary(MetacodeBinaryOperator.Equal, operand, Expression(constant.Expression)) { Origin = origin };

            case UnaryPatternSyntax { Pattern: ConstantPatternSyntax constant }:
                return new MetacodeBinary(MetacodeBinaryOperator.NotEqual, operand, Expression(constant.Expression)) { Origin = origin };

            case UnaryPatternSyntax { Pattern: TypePatternSyntax typePattern }:
                return new MetacodeTypeCheck(operand, TypeRef(typePattern.Type), MetacodeTypeCheckKind.IsNot) { Origin = origin };

            case UnaryPatternSyntax { Pattern: DeclarationPatternSyntax declaration }:
                return new MetacodeTypeCheck(operand, TypeRef(declaration.Type), MetacodeTypeCheckKind.IsNot, declaration.Designation.ToString())
                {
                    Origin = origin,
                };

            case TypePatternSyntax typePattern:
                return new MetacodeTypeCheck(operand, TypeRef(typePattern.Type)) { Origin = origin };

            case DeclarationPatternSyntax declaration:
                return new MetacodeTypeCheck(operand, TypeRef(declaration.Type), MetacodeTypeCheckKind.Is, declaration.Designation.ToString())
                {
                    Origin = origin,
                };

            default:
                // A recursive, or/and/not or relational pattern has no structured node: its text is kept,
                // exactly as a switch arm's pattern is, and the operand stays a real expression.
                return new MetacodePatternMatch(operand, MetacodePatternText.Normalize(node.Pattern.ToString()))
                {
                    Origin = origin,
                };
        }
    }

    private MetacodeExpression Lambda(LambdaExpressionSyntax node)
    {
        var parameters = node switch
        {
            SimpleLambdaExpressionSyntax simple => new List<MetacodeParameter>
            {
                new(simple.Parameter.Identifier.Text, simple.Parameter.Type is null ? null : TypeRef(simple.Parameter.Type)),
            },
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(Parameter).ToList(),
            _ => new List<MetacodeParameter>(),
        };

        if (node.ExpressionBody is not null)
            return new MetacodeLambda(parameters, Expression: Expression(node.ExpressionBody)) { Origin = OriginOf(node) };

        return node.Block is null
            ? OpaqueExpression(node, "a lambda without a body")
            : new MetacodeLambda(parameters, Body: Block(node.Block)) { Origin = OriginOf(node) };
    }

    private MetacodeExpression AnonymousMethod(AnonymousMethodExpressionSyntax node)
    {
        var parameters = node.ParameterList is null ? null : Parameters(node.ParameterList);
        return new MetacodeLambda(parameters, Body: Block(node.Block)) { Origin = OriginOf(node) };
    }

    private MetacodeExpression ObjectCreation(
        TypeSyntax? type,
        ArgumentListSyntax? arguments,
        InitializerExpressionSyntax? initializer,
        MetacodeOrigin origin)
    {
        List<MetacodeExpression>? elements = null;
        List<MetacodeMemberInitializer>? members = null;

        if (initializer is not null)
        {
            if (initializer.IsKind(SyntaxKind.CollectionInitializerExpression) || initializer.IsKind(SyntaxKind.ArrayInitializerExpression))
                elements = initializer.Expressions.Select(Expression).ToList();
            else
                members = InitializerMembers(initializer);
        }

        return new MetacodeObjectCreation(
            type is null ? new MetacodeTypeRef("new") : TypeRef(type),
            Arguments(arguments),
            elements,
            members) { Origin = origin };
    }

    private List<MetacodeMemberInitializer> InitializerMembers(InitializerExpressionSyntax initializer) =>
        initializer.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Select(assignment => new MetacodeMemberInitializer(assignment.Left.ToString(), Expression(assignment.Right)))
            .ToList();

    /// <summary>
    /// An anonymous object's members: <c>{ A = x, y }</c> keeps <c>A</c> and, for the shorthand, the
    /// name of the expression it reads (<c>y</c>).
    /// </summary>
    private IReadOnlyList<MetacodeMemberInitializer> AnonymousMembers(AnonymousObjectCreationExpressionSyntax node) =>
        node.Initializers.Select(initializer => initializer.NameEquals is null
                ? new MetacodeMemberInitializer(initializer.Expression.ToString(), Expression(initializer.Expression))
                : new MetacodeMemberInitializer(initializer.NameEquals.Name.Identifier.Text, Expression(initializer.Expression)))
            .ToList();

    private MetacodeExpression SwitchExpression(SwitchExpressionSyntax node)
    {
        var arms = node.Arms.Select(arm => new MetacodeSwitchArm(
            arm.Pattern.ToString(),
            arm.WhenClause is null ? null : Expression(arm.WhenClause.Condition),
            Expression(arm.Expression))).ToList();

        return new MetacodeSwitchExpression(Expression(node.GoverningExpression), arms) { Origin = OriginOf(node) };
    }

    private List<MetacodeExpression> CollectionElements(CollectionExpressionSyntax node)
    {
        var elements = new List<MetacodeExpression>(node.Elements.Count);
        foreach (var element in node.Elements)
        {
            switch (element)
            {
                case ExpressionElementSyntax expression:
                    elements.Add(Expression(expression.Expression));
                    break;
                case SpreadElementSyntax spread:
                    elements.Add(new MetacodeSpread(Expression(spread.Expression)) { Origin = OriginOf(spread) });
                    break;
                default:
                    elements.Add(OpaqueExpression(element, "unmodelled collection element: " + element.Kind()));
                    break;
            }
        }

        return elements;
    }

    private List<MetacodeExpression> Sizes(ArrayTypeSyntax type) =>
        type.RankSpecifiers
            .SelectMany(rank => rank.Sizes)
            .Where(size => size is not OmittedArraySizeExpressionSyntax)
            .Select(Expression)
            .ToList();

    private static MetacodeTypeRef TypeOf(ExpressionSyntax expression) =>
        expression is TypeSyntax type ? TypeRef(type) : new MetacodeTypeRef(expression.ToString());

    private MetacodeExpression OpaqueExpression(SyntaxNode node, string reason)
    {
        _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedExpression, reason, node));
        return new MetacodeOpaqueExpression(MetacodeLanguage.CSharp, node.ToString(), reason) { Origin = OriginOf(node) };
    }
}
