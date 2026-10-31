using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Metacode.Frontends;

internal sealed partial class VisualBasicLowering
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

            case QualifiedNameSyntax qualified:
                return new MetacodeIdentifier(qualified.ToString()) { Origin = OriginOf(qualified) };

            case MeExpressionSyntax:
                return new MetacodeIdentifier("this") { Origin = OriginOf(expression) };

            case MyBaseExpressionSyntax:
                return new MetacodeIdentifier("base") { Origin = OriginOf(expression) };

            case MyClassExpressionSyntax myClass:
                _diagnostics.Add(Diagnostic(
                    MetacodeDiagnosticCodes.LossyLowering,
                    "MyClass suppresses virtual dispatch, which the pseudocode model cannot express",
                    myClass));
                return new MetacodeIdentifier("this") { Origin = OriginOf(expression) };

            case ParenthesizedExpressionSyntax parenthesized:
                return Expression(parenthesized.Expression);

            case MemberAccessExpressionSyntax member:
                return Member(member);

            case InvocationExpressionSyntax invocation:
                return new MetacodeInvocation(Expression(invocation.Expression), Arguments(invocation.ArgumentList))
                {
                    Origin = OriginOf(invocation),
                };

            case BinaryExpressionSyntax binary:
                return Binary(binary);

            case UnaryExpressionSyntax unary:
                return Unary(unary);

            case CastExpressionSyntax cast:
                return Cast(cast);

            case TypeOfExpressionSyntax typeOf:
                return new MetacodeTypeCheck(
                    Expression(typeOf.Expression),
                    TypeRef(typeOf.Type),
                    typeOf.Kind() == SyntaxKind.TypeOfIsNotExpression ? MetacodeTypeCheckKind.IsNot : MetacodeTypeCheckKind.Is)
                {
                    Origin = OriginOf(typeOf),
                };

            case GetTypeExpressionSyntax getType:
                return new MetacodeTypeOf(TypeRef(getType.Type)) { Origin = OriginOf(getType) };

            case TernaryConditionalExpressionSyntax conditional:
                return new MetacodeConditional(
                    Expression(conditional.Condition),
                    Expression(conditional.WhenTrue),
                    Expression(conditional.WhenFalse)) { Origin = OriginOf(conditional) };

            case BinaryConditionalExpressionSyntax conditional:
                return new MetacodeBinary(
                    MetacodeBinaryOperator.NullCoalesce,
                    Expression(conditional.FirstExpression),
                    Expression(conditional.SecondExpression)) { Origin = OriginOf(conditional) };

            case ObjectCreationExpressionSyntax creation:
                return ObjectCreation(creation);

            case AnonymousObjectCreationExpressionSyntax anonymous:
                return new MetacodeAnonymousObject(InitializerMembers(anonymous.Initializer)) { Origin = OriginOf(anonymous) };

            case ArrayCreationExpressionSyntax array:
                return ArrayCreation(array);

            case CollectionInitializerSyntax collection:
                return new MetacodeCollection(
                    MetacodeCollectionKind.List,
                    collection.Initializers.Select(Expression).ToList()) { Origin = OriginOf(collection) };

            case LambdaExpressionSyntax lambda:
                return Lambda(lambda);

            case AwaitExpressionSyntax awaitExpression:
                return new MetacodeAwait(Expression(awaitExpression.Expression)) { Origin = OriginOf(awaitExpression) };

            case NameOfExpressionSyntax nameOf:
                return new MetacodeLiteral(
                    MetacodeLiteralKind.String,
                    "\"" + nameOf.Argument + "\"") { Origin = OriginOf(nameOf) };

            case ConditionalAccessExpressionSyntax conditionalAccess:
                return ConditionalAccess(conditionalAccess);
        }

        _diagnostics.Add(Diagnostic(
            MetacodeDiagnosticCodes.UnsupportedExpression,
            "unmodelled expression: " + expression.Kind(),
            expression));
        return new MetacodeOpaqueExpression(
            MetacodeLanguage.VisualBasic,
            expression.ToString(),
            "unmodelled expression: " + expression.Kind()) { Origin = OriginOf(expression) };
    }

    private static MetacodeExpression Literal(LiteralExpressionSyntax literal) => literal.Kind() switch
    {
        SyntaxKind.NothingLiteralExpression => MetacodeLiteral.Null,
        SyntaxKind.TrueLiteralExpression => MetacodeLiteral.True,
        SyntaxKind.FalseLiteralExpression => MetacodeLiteral.False,
        SyntaxKind.NumericLiteralExpression => MetacodeLiteral.Number(literal.Token.Text),
        SyntaxKind.StringLiteralExpression => MetacodeLiteral.String(literal.Token.Text),
        SyntaxKind.CharacterLiteralExpression => new MetacodeLiteral(MetacodeLiteralKind.Character, literal.Token.Text),
        _ => new MetacodeLiteral(MetacodeLiteralKind.Raw, literal.Token.Text),
    };

    private MetacodeExpression Interpolation(InterpolatedStringExpressionSyntax interpolated)
    {
        var parts = new List<MetacodeInterpolationPart>(interpolated.Contents.Count);
        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new MetacodeInterpolationPart(text.TextToken.Text, null) { Origin = OriginOf(text) });
                    break;

                case InterpolationSyntax interpolation:
                    parts.Add(new MetacodeInterpolationPart(null, Expression(interpolation.Expression)) { Origin = OriginOf(interpolation) });
                    break;
            }
        }

        return new MetacodeInterpolatedString(parts) { Origin = OriginOf(interpolated) };
    }

    private MetacodeExpression Member(MemberAccessExpressionSyntax member)
    {
        // `a!b` is the dictionary access: the member name is the key, a string.
        if (member.OperatorToken.IsKind(SyntaxKind.ExclamationToken))
        {
            return new MetacodeIndex(
                Expression(member.Expression),
                new[]
                {
                    new MetacodeLiteral(MetacodeLiteralKind.String, "\"" + member.Name.Identifier.Text + "\"")
                    {
                        Origin = OriginOf(member.Name),
                    },
                }) { Origin = OriginOf(member) };
        }

        return new MetacodeMemberAccess(Expression(member.Expression), member.Name.Identifier.Text) { Origin = OriginOf(member) };
    }

    private MetacodeExpression ConditionalAccess(ConditionalAccessExpressionSyntax conditionalAccess)
    {
        var target = Expression(conditionalAccess.Expression);

        switch (conditionalAccess.WhenNotNull)
        {
            case MemberAccessExpressionSyntax member:
                return new MetacodeMemberAccess(target, member.Name.Identifier.Text, NullConditional: true)
                {
                    Origin = OriginOf(conditionalAccess),
                };

            case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member:
                return new MetacodeInvocation(
                    new MetacodeMemberAccess(target, member.Name.Identifier.Text, NullConditional: true) { Origin = OriginOf(member) },
                    Arguments(invocation.ArgumentList)) { Origin = OriginOf(conditionalAccess) };
        }

        _diagnostics.Add(Diagnostic(
            MetacodeDiagnosticCodes.UnsupportedExpression,
            "conditional access: " + conditionalAccess.WhenNotNull.Kind(),
            conditionalAccess));
        return new MetacodeOpaqueExpression(
            MetacodeLanguage.VisualBasic,
            conditionalAccess.ToString(),
            "conditional access: " + conditionalAccess.WhenNotNull.Kind()) { Origin = OriginOf(conditionalAccess) };
    }

    private MetacodeExpression Binary(BinaryExpressionSyntax binary)
    {
        if (binary.Kind() == SyntaxKind.LikeExpression)
        {
            _diagnostics.Add(Diagnostic(
                MetacodeDiagnosticCodes.UnsupportedExpression,
                "the Visual Basic Like operator is a pattern match with its own wildcard syntax",
                binary));
            return new MetacodeOpaqueExpression(
                MetacodeLanguage.VisualBasic,
                binary.ToString(),
                "the Visual Basic Like operator is not modelled") { Origin = OriginOf(binary) };
        }

        return new MetacodeBinary(
            BinaryOperator(binary.Kind()),
            Expression(binary.Left),
            Expression(binary.Right)) { Origin = OriginOf(binary) };
    }

    private static MetacodeBinaryOperator BinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression => MetacodeBinaryOperator.Add,
        SyntaxKind.SubtractExpression => MetacodeBinaryOperator.Subtract,
        SyntaxKind.MultiplyExpression => MetacodeBinaryOperator.Multiply,
        SyntaxKind.DivideExpression => MetacodeBinaryOperator.Divide,
        SyntaxKind.IntegerDivideExpression => MetacodeBinaryOperator.IntegerDivide,
        SyntaxKind.ModuloExpression => MetacodeBinaryOperator.Modulo,
        SyntaxKind.ExponentiateExpression => MetacodeBinaryOperator.Power,
        SyntaxKind.ConcatenateExpression => MetacodeBinaryOperator.Add,
        SyntaxKind.EqualsExpression => MetacodeBinaryOperator.Equal,
        SyntaxKind.NotEqualsExpression => MetacodeBinaryOperator.NotEqual,
        SyntaxKind.LessThanExpression => MetacodeBinaryOperator.LessThan,
        SyntaxKind.LessThanOrEqualExpression => MetacodeBinaryOperator.LessThanOrEqual,
        SyntaxKind.GreaterThanExpression => MetacodeBinaryOperator.GreaterThan,
        SyntaxKind.GreaterThanOrEqualExpression => MetacodeBinaryOperator.GreaterThanOrEqual,
        SyntaxKind.AndAlsoExpression => MetacodeBinaryOperator.LogicalAnd,
        SyntaxKind.OrElseExpression => MetacodeBinaryOperator.LogicalOr,
        SyntaxKind.AndExpression => MetacodeBinaryOperator.BitwiseAnd,
        SyntaxKind.OrExpression => MetacodeBinaryOperator.BitwiseOr,
        SyntaxKind.ExclusiveOrExpression => MetacodeBinaryOperator.BitwiseXor,
        SyntaxKind.LeftShiftExpression => MetacodeBinaryOperator.ShiftLeft,
        SyntaxKind.RightShiftExpression => MetacodeBinaryOperator.ShiftRight,
        SyntaxKind.IsExpression => MetacodeBinaryOperator.Equal,
        SyntaxKind.IsNotExpression => MetacodeBinaryOperator.NotEqual,
        _ => MetacodeBinaryOperator.Add,
    };

    private MetacodeExpression Unary(UnaryExpressionSyntax unary)
    {
        var operand = Expression(unary.Operand);
        var lowered = unary.Kind() switch
        {
            SyntaxKind.NotExpression => MetacodeUnaryOperator.Not,
            SyntaxKind.UnaryMinusExpression => MetacodeUnaryOperator.Negate,
            SyntaxKind.UnaryPlusExpression => MetacodeUnaryOperator.Plus,
            SyntaxKind.AddressOfExpression => MetacodeUnaryOperator.AddressOf,
            _ => (MetacodeUnaryOperator?)null,
        };

        if (lowered is null)
        {
            _diagnostics.Add(Diagnostic(
                MetacodeDiagnosticCodes.UnsupportedExpression,
                "unmodelled unary operator: " + unary.Kind(),
                unary));
            return new MetacodeOpaqueExpression(
                MetacodeLanguage.VisualBasic,
                unary.ToString(),
                "unmodelled unary operator: " + unary.Kind()) { Origin = OriginOf(unary) };
        }

        return new MetacodeUnary(lowered.Value, operand) { Origin = OriginOf(unary) };
    }

    private MetacodeExpression Cast(CastExpressionSyntax cast)
    {
        var operand = Expression(cast.Expression);
        var type = TypeRef(cast.Type);

        // `TryCast(x, T)` is `x as T`: a check that yields null when the type does not match.
        return cast.Kind() == SyntaxKind.TryCastExpression
            ? new MetacodeTypeCheck(operand, type, MetacodeTypeCheckKind.As) { Origin = OriginOf(cast) }
            : new MetacodeCast(type, operand) { Origin = OriginOf(cast) };
    }

    private MetacodeExpression ObjectCreation(ObjectCreationExpressionSyntax creation)
    {
        IReadOnlyList<MetacodeExpression>? elements = null;
        IReadOnlyList<MetacodeMemberInitializer>? members = null;

        switch (creation.Initializer)
        {
            case ObjectMemberInitializerSyntax memberInitializer:
                members = InitializerMembers(memberInitializer);
                break;
            case ObjectCollectionInitializerSyntax collectionInitializer:
                elements = collectionInitializer.Initializer.Initializers.Select(Expression).ToList();
                break;
        }

        return new MetacodeObjectCreation(
            TypeRef(creation.Type),
            Arguments(creation.ArgumentList),
            elements,
            members) { Origin = OriginOf(creation) };
    }

    private IReadOnlyList<MetacodeMemberInitializer> InitializerMembers(ObjectMemberInitializerSyntax initializer) =>
        initializer.Initializers
            .OfType<NamedFieldInitializerSyntax>()
            .Select(member => new MetacodeMemberInitializer(
                member.Name.Identifier.Text,
                Expression(member.Expression)) { Origin = OriginOf(member) })
            .ToList();

    private MetacodeExpression ArrayCreation(ArrayCreationExpressionSyntax array)
    {
        var sizes = array.ArrayBounds?.Arguments
            .OfType<SimpleArgumentSyntax>()
            .Select(argument => Expression(argument.Expression))
            .ToList();

        if (sizes is { Count: > 0 })
        {
            // `New Integer(4)` is five elements: the bound is the upper index, not the length.
            _diagnostics.Add(Diagnostic(
                MetacodeDiagnosticCodes.LossyLowering,
                "a Visual Basic array bound is an upper index; the model stores it as a length",
                array));
        }

        var elementType = array.Type is ArrayTypeSyntax arrayType ? TypeRef(arrayType.ElementType) : TypeRef(array.Type);

        return new MetacodeNewArray(
            elementType,
            sizes,
            array.Initializer?.Initializers.Select(Expression).ToList()) { Origin = OriginOf(array) };
    }

    private MetacodeExpression Lambda(LambdaExpressionSyntax lambda)
    {
        var parameters = Parameters(lambda.SubOrFunctionHeader.ParameterList);

        switch (lambda)
        {
            case MultiLineLambdaExpressionSyntax multiLine:
                return new MetacodeLambda(parameters, Block(multiLine.Statements)) { Origin = OriginOf(lambda) };

            case SingleLineLambdaExpressionSyntax singleLine:
                return singleLine.Body is ExpressionSyntax expression
                    ? new MetacodeLambda(parameters, null, Expression(expression)) { Origin = OriginOf(lambda) }
                    : new MetacodeLambda(parameters, Block(new[] { (StatementSyntax)singleLine.Body })) { Origin = OriginOf(lambda) };
        }

        _diagnostics.Add(Diagnostic(
            MetacodeDiagnosticCodes.UnsupportedExpression,
            "unmodelled lambda shape",
            lambda));
        return new MetacodeOpaqueExpression(
            MetacodeLanguage.VisualBasic,
            lambda.ToString(),
            "unmodelled lambda shape") { Origin = OriginOf(lambda) };
    }
}
