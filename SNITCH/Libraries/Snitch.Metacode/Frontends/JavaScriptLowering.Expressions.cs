using Acornima;
using Acornima.Ast;

// Same reason as in JavaScriptFrontend.cs: `Snitch.Node` is a namespace, so the AST node is `AstNode`.
using AstNode = Acornima.Ast.Node;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// The expression half of the JavaScript lowering: values, operators, literals, lambdas and the pattern
/// text a destructuring declaration needs. Every case that has no model node returns
/// <see cref="MetacodeOpaqueExpression"/> with its own source text and a diagnostic, so an expression the
/// reader could not place is visible in the pseudocode instead of silently missing from it.
/// </summary>
internal sealed partial class JavaScriptLowering
{
    private MetacodeExpression Expression(AstNode node) => node switch
    {
        Identifier identifier => new MetacodeIdentifier(identifier.Name) { Origin = Origin(node) },
        PrivateIdentifier priv => new MetacodeIdentifier("#" + priv.Name) { Origin = Origin(node) },
        ThisExpression => new MetacodeIdentifier("this") { Origin = Origin(node) },
        Super => new MetacodeIdentifier("super") { Origin = Origin(node) },
        Literal literal => Literal(literal),

        // UpdateExpression derives from UnaryExpression: it has to be matched first.
        UpdateExpression update => new MetacodeUnary(
            update.Operator == Operator.Increment ? MetacodeUnaryOperator.Increment : MetacodeUnaryOperator.Decrement,
            Expression(update.Argument),
            IsPostfix: !update.Prefix) { Origin = Origin(node) },

        UnaryExpression unary => Unary(unary),
        BinaryExpression binary => Binary(binary),
        AssignmentExpression assignment => new MetacodeAssignmentExpression(
            Expression(assignment.Left),
            AssignmentOperator(assignment.Operator),
            Expression(assignment.Right)) { Origin = Origin(node) },
        ConditionalExpression conditional => new MetacodeConditional(
            Expression(conditional.Test),
            Expression(conditional.Consequent),
            Expression(conditional.Alternate)) { Origin = Origin(node) },

        TemplateLiteral template => Template(template),
        ArrayExpression array => new MetacodeCollection(MetacodeCollectionKind.Array, ArrayItems(array))
        {
            Origin = Origin(node),
        },
        ObjectExpression obj => ObjectLiteral(obj),
        FunctionExpression function => FunctionExpression(function),
        ArrowFunctionExpression arrow => Arrow(arrow),

        CallExpression call => Call(call),
        NewExpression @new => new MetacodeObjectCreation(
            MetacodeTypeRef.Of(Text(@new.Callee)),
            Arguments(@new.Arguments)) { Origin = Origin(node) },
        MemberExpression member => Member(member),

        ChainExpression chain => Expression(chain.Expression),
        ParenthesizedExpression parenthesized => Expression(parenthesized.Expression),
        SpreadElement spread => new MetacodeSpread(Expression(spread.Argument)) { Origin = Origin(node) },
        AwaitExpression awaitExpression => new MetacodeAwait(Expression(awaitExpression.Argument))
        {
            Origin = Origin(node),
        },

        ClassExpression => OpaqueExpression(node, "a class used as an expression has no node in the model"),
        SequenceExpression => OpaqueExpression(node, "the comma operator has no node in the model"),
        TaggedTemplateExpression => OpaqueExpression(node, "a tagged template has no node in the model"),
        ImportExpression => OpaqueExpression(node, "a dynamic import has no node in the model"),
        _ => OpaqueExpression(node, "an expression the model does not have"),
    };

    private MetacodeLiteral Literal(Literal literal) => literal switch
    {
        StringLiteral text => new(MetacodeLiteralKind.String, Raw(text)) { Origin = Origin(literal) },
        NumericLiteral number => new(MetacodeLiteralKind.Number, Raw(number)) { Origin = Origin(literal) },
        BigIntLiteral big => new(MetacodeLiteralKind.Number, Raw(big)) { Origin = Origin(literal) },
        BooleanLiteral boolean => MetacodeLiteral.Boolean(boolean.Value) with { Origin = Origin(literal) },
        NullLiteral => MetacodeLiteral.Null with { Origin = Origin(literal) },
        RegExpLiteral => new MetacodeLiteral(MetacodeLiteralKind.Raw, Raw(literal)) { Origin = Origin(literal) },
        _ => new MetacodeLiteral(MetacodeLiteralKind.Raw, Raw(literal)) { Origin = Origin(literal) },
    };

    /// <summary>
    /// A template with no substitutions is a string, and it is printed with the delimiters the source used.
    /// With substitutions it is the model's interpolated string, whose parts are the source chunks between
    /// the expressions — the chunk text is taken from the file, so escapes are already what the author
    /// wrote and are not re-interpreted.
    /// </summary>
    private MetacodeExpression Template(TemplateLiteral template)
    {
        if (template.Expressions.Count == 0)
            return new MetacodeLiteral(MetacodeLiteralKind.String, Text(template)) { Origin = Origin(template) };

        var parts = new List<MetacodeInterpolationPart>();
        for (var index = 0; index < template.Quasis.Count; index++)
        {
            var chunk = Chunk(Text(template.Quasis[index]));
            if (chunk.Length > 0)
                parts.Add(new MetacodeInterpolationPart(chunk, null) { Origin = Origin(template.Quasis[index]) });

            if (index < template.Expressions.Count)
            {
                parts.Add(new MetacodeInterpolationPart(null, Expression(template.Expressions[index]))
                {
                    Origin = Origin(template.Expressions[index]),
                });
            }
        }

        return new MetacodeInterpolatedString(parts) { Origin = Origin(template) };
    }

    /// <summary>The text of one quasi, without the backtick or <c>${</c> delimiters the range may include.</summary>
    private static string Chunk(string text)
    {
        var start = text.StartsWith('`') ? 1 : 0;
        var end = text.Length;
        if (end > start && text[end - 1] == '`' && (end < 2 || text[end - 2] != '\\'))
            end--;

        return text[start..end].Replace("\\`", "`").Replace("${", "{");
    }

    private List<MetacodeExpression> ArrayItems(ArrayExpression array)
    {
        var items = new List<MetacodeExpression>(array.Elements.Count);
        foreach (var element in array.Elements)
        {
            // A hole in `[1, , 3]` is not an expression, but it is not nothing either: it keeps its place.
            items.Add(element is null
                ? new MetacodeLiteral(MetacodeLiteralKind.Raw, string.Empty) { Origin = Origin(array) }
                : Expression(element));
        }

        return items;
    }

    /// <summary>
    /// An object literal is the model's dictionary. The method shorthand has as much shape as the function
    /// it declares, so it becomes a lambda property; a spread or an accessor has no node, and then the whole
    /// literal is kept as text rather than losing one of its members.
    /// </summary>
    private MetacodeExpression ObjectLiteral(ObjectExpression expression)
    {
        var entries = new List<MetacodeDictionaryEntry>();

        foreach (var property in expression.Properties)
        {
            if (property is not Property member)
                return OpaqueExpression(expression, "an object literal with a spread has no node in the model");

            if (member.Kind is PropertyKind.Get or PropertyKind.Set)
                return OpaqueExpression(expression, "an accessor in an object literal has no node in the model");

            var key = KeyExpression(member.Key, member.Computed);
            if (key is null)
                return OpaqueExpression(expression, "a computed key in an object literal has no node in the model");

            var value = member.Method && member.Value is FunctionExpression method
                ? (MetacodeExpression)FunctionExpression(method)
                : Expression(member.Value);

            entries.Add(new MetacodeDictionaryEntry(key, value) { Origin = Origin(member) });
        }

        return new MetacodeDictionary(entries) { Origin = Origin(expression) };
    }

    private MetacodeExpression? KeyExpression(Expression key, bool computed)
    {
        if (computed)
            return null;

        return key switch
        {
            Identifier identifier => new MetacodeIdentifier(identifier.Name) { Origin = Origin(key) },
            PrivateIdentifier priv => new MetacodeIdentifier("#" + priv.Name) { Origin = Origin(key) },
            StringLiteral text => new MetacodeLiteral(MetacodeLiteralKind.String, Raw(text)) { Origin = Origin(key) },
            NumericLiteral number => new MetacodeLiteral(MetacodeLiteralKind.Number, Raw(number)) { Origin = Origin(key) },
            _ => null,
        };
    }

    /// <summary>The name a class member is declared under, or null when it is not a name at all.</summary>
    private string? MemberName(Expression key, bool computed)
    {
        if (computed)
            return null;

        return key switch
        {
            Identifier identifier => identifier.Name,
            PrivateIdentifier priv => "#" + priv.Name,
            StringLiteral text => text.Value,
            NumericLiteral number => Raw(number),
            _ => null,
        };
    }

    private MetacodeLambda FunctionExpression(FunctionExpression function)
    {
        if (function.Generator)
            ReportLossy(function, "a generator function is lowered without its generator flag");

        if (function.Id is not null)
            ReportLossy(function, $"the name '{function.Id.Name}' of this function expression is dropped");

        return new MetacodeLambda(Parameters(function.Params), Block(function.Body)) { Origin = Origin(function) };
    }

    private MetacodeLambda Arrow(ArrowFunctionExpression arrow)
    {
        if (arrow.Async)
            ReportLossy(arrow, "an async arrow function is lowered without its async marker");

        return arrow.Body is Expression body
            ? new MetacodeLambda(Parameters(arrow.Params), null, Expression(body)) { Origin = Origin(arrow) }
            : new MetacodeLambda(Parameters(arrow.Params), Block(arrow.Body)) { Origin = Origin(arrow) };
    }

    private MetacodeExpression Call(CallExpression call)
    {
        if (call.Optional)
            ReportLossy(call, "an optional call is lowered without its `?.` marker");

        return new MetacodeInvocation(Expression(call.Callee), Arguments(call.Arguments)) { Origin = Origin(call) };
    }

    private List<MetacodeArgument> Arguments(NodeList<Expression> arguments)
    {
        var list = new List<MetacodeArgument>(arguments.Count);
        foreach (var argument in arguments)
        {
            // `f(…xs)` spreads the argument list; the model keeps the spread so the call does not read
            // like a call with one collection argument.
            list.Add(argument is SpreadElement spread
                ? new MetacodeArgument(null, new MetacodeSpread(Expression(spread.Argument))
                {
                    Origin = Origin(spread),
                })
                : MetacodeArgument.Of(Expression(argument)));
        }

        return list;
    }

    private MetacodeExpression Member(MemberExpression member)
    {
        if (member.Computed)
            return new MetacodeIndex(Expression(member.Object), new[] { Expression(member.Property) })
            {
                Origin = Origin(member),
            };

        var name = member.Property switch
        {
            Identifier identifier => identifier.Name,
            PrivateIdentifier priv => "#" + priv.Name,
            _ => Text(member.Property),
        };

        return new MetacodeMemberAccess(Expression(member.Object), name, member.Optional)
        {
            Origin = Origin(member),
        };
    }

    private MetacodeExpression Binary(BinaryExpression binary)
    {
        // `x instanceof T` asks the same question `x is T` does, and `in` is the model's CONTAINS.
        if (binary.Operator == Operator.InstanceOf)
        {
            return new MetacodeTypeCheck(
                Expression(binary.Left),
                MetacodeTypeRef.Of(Text(binary.Right)),
                MetacodeTypeCheckKind.Is) { Origin = Origin(binary) };
        }

        if (binary.Operator == Operator.UnsignedRightShift)
            ReportLossy(binary, "`>>>` is lowered as a signed shift");

        return new MetacodeBinary(
            BinaryOperator(binary.Operator),
            Expression(binary.Left),
            Expression(binary.Right)) { Origin = Origin(binary) };
    }

    private MetacodeExpression Unary(UnaryExpression unary)
    {
        var operand = Expression(unary.Argument);

        return unary.Operator switch
        {
            Operator.UnaryNegation => new MetacodeUnary(MetacodeUnaryOperator.Negate, operand) { Origin = Origin(unary) },
            Operator.UnaryPlus => new MetacodeUnary(MetacodeUnaryOperator.Plus, operand) { Origin = Origin(unary) },
            Operator.LogicalNot => new MetacodeUnary(MetacodeUnaryOperator.Not, operand) { Origin = Origin(unary) },
            Operator.BitwiseNot => new MetacodeUnary(MetacodeUnaryOperator.BitwiseNot, operand) { Origin = Origin(unary) },
            // `typeof x` and `typeof(T)` print the same way, and that is what the model calls TYPEOF.
            Operator.TypeOf => new MetacodeTypeOf(MetacodeTypeRef.Of(Text(unary.Argument))) { Origin = Origin(unary) },
            _ => OpaqueExpression(unary, $"the `{unary.Operator.ToString().ToLowerInvariant()}` operator has no node in the model"),
        };
    }

    private static MetacodeBinaryOperator BinaryOperator(Operator @operator) => @operator switch
    {
        Operator.Addition => MetacodeBinaryOperator.Add,
        Operator.Subtraction => MetacodeBinaryOperator.Subtract,
        Operator.Multiplication => MetacodeBinaryOperator.Multiply,
        Operator.Division => MetacodeBinaryOperator.Divide,
        Operator.Remainder => MetacodeBinaryOperator.Modulo,
        Operator.Exponentiation => MetacodeBinaryOperator.Power,
        Operator.Equality or Operator.StrictEquality => MetacodeBinaryOperator.Equal,
        Operator.Inequality or Operator.StrictInequality => MetacodeBinaryOperator.NotEqual,
        Operator.LessThan => MetacodeBinaryOperator.LessThan,
        Operator.LessThanOrEqual => MetacodeBinaryOperator.LessThanOrEqual,
        Operator.GreaterThan => MetacodeBinaryOperator.GreaterThan,
        Operator.GreaterThanOrEqual => MetacodeBinaryOperator.GreaterThanOrEqual,
        Operator.LogicalAnd => MetacodeBinaryOperator.LogicalAnd,
        Operator.LogicalOr => MetacodeBinaryOperator.LogicalOr,
        Operator.BitwiseAnd => MetacodeBinaryOperator.BitwiseAnd,
        Operator.BitwiseOr => MetacodeBinaryOperator.BitwiseOr,
        Operator.BitwiseXor => MetacodeBinaryOperator.BitwiseXor,
        Operator.LeftShift => MetacodeBinaryOperator.ShiftLeft,
        Operator.RightShift or Operator.UnsignedRightShift => MetacodeBinaryOperator.ShiftRight,
        Operator.NullishCoalescing => MetacodeBinaryOperator.NullCoalesce,
        Operator.In => MetacodeBinaryOperator.Contains,
        _ => MetacodeBinaryOperator.Add,
    };

    private static MetacodeAssignmentOperator AssignmentOperator(Operator @operator) => @operator switch
    {
        Operator.AdditionAssignment => MetacodeAssignmentOperator.Add,
        Operator.SubtractionAssignment => MetacodeAssignmentOperator.Subtract,
        Operator.MultiplicationAssignment => MetacodeAssignmentOperator.Multiply,
        Operator.DivisionAssignment => MetacodeAssignmentOperator.Divide,
        Operator.RemainderAssignment => MetacodeAssignmentOperator.Modulo,
        Operator.ExponentiationAssignment => MetacodeAssignmentOperator.Power,
        Operator.BitwiseAndAssignment => MetacodeAssignmentOperator.BitwiseAnd,
        Operator.BitwiseOrAssignment => MetacodeAssignmentOperator.BitwiseOr,
        Operator.BitwiseXorAssignment => MetacodeAssignmentOperator.BitwiseXor,
        Operator.LeftShiftAssignment => MetacodeAssignmentOperator.ShiftLeft,
        Operator.RightShiftAssignment or Operator.UnsignedRightShiftAssignment => MetacodeAssignmentOperator.ShiftRight,
        Operator.NullishCoalescingAssignment => MetacodeAssignmentOperator.Coalesce,
        _ => MetacodeAssignmentOperator.Assign,
    };

    /// <summary>
    /// A parameter is a name, and when it is not — a destructuring pattern, a rest element — its source
    /// text is the name. A default value is the model's own parameter default.
    /// </summary>
    private List<MetacodeParameter> Parameters(NodeList<AstNode> parameters)
    {
        var list = new List<MetacodeParameter>(parameters.Count);

        foreach (var parameter in parameters)
        {
            switch (parameter)
            {
                case Identifier identifier:
                    list.Add(new MetacodeParameter(identifier.Name) { Origin = Origin(parameter) });
                    continue;

                case AssignmentPattern { Left: Identifier target } assignment:
                    list.Add(new MetacodeParameter(target.Name, null, Expression(assignment.Right))
                    {
                        Origin = Origin(parameter),
                    });
                    continue;
            }

            var text = PatternText(parameter);
            list.Add(text.StartsWith("...", StringComparison.Ordinal)
                ? new MetacodeParameter(text[3..], null, null, MetacodeParameterKind.Params) { Origin = Origin(parameter) }
                : new MetacodeParameter(text) { Origin = Origin(parameter) });
        }

        return list;
    }

    /// <summary>
    /// The source text of a node, one line, for everything the model keeps as text: a destructuring
    /// pattern, an opaque construct, a type name it can only quote.
    /// </summary>
    private string PatternText(AstNode? node) => node is null ? string.Empty : OneLine(Text(node));

    private string Text(AstNode? node) =>
        node is null || node.Start < 0 || node.End > _text.Length || node.End <= node.Start
            ? string.Empty
            : _text[node.Start..node.End];

    private static string Raw(Literal literal) =>
        string.IsNullOrEmpty(literal.Raw) ? literal.Value?.ToString() ?? string.Empty : literal.Raw;

    // ── provenance and honest failures ──────────────────────────────────────────────

    private MetacodeOrigin Origin(AstNode node) => Origin(node.Location);

    private MetacodeOrigin Origin(Acornima.SourceLocation location) => location.Start.Line <= 0
        ? new MetacodeOrigin(MetacodeLanguage.JavaScript, _document)
        : new MetacodeOrigin(
            MetacodeLanguage.JavaScript,
            _document,
            location.Start.Line,
            location.Start.Column + 1,
            location.End.Line,
            location.End.Column + 1);

    private void ReportLossy(AstNode node, string message) =>
        _diagnostics.Add(new MetacodeDiagnostic(
            MetacodeDiagnosticCodes.LossyLowering,
            MetacodeDiagnosticSeverity.Warning,
            message,
            Origin(node)));

    private MetacodeStatement Opaque(AstNode node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(
            MetacodeDiagnosticCodes.UnsupportedStatement,
            MetacodeDiagnosticSeverity.Warning,
            reason,
            Origin(node)));

        return new MetacodeOpaqueStatement(MetacodeLanguage.JavaScript, OneLine(Text(node)), reason)
        {
            Origin = Origin(node),
        };
    }

    private MetacodeExpression OpaqueExpression(AstNode node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(
            MetacodeDiagnosticCodes.UnsupportedExpression,
            MetacodeDiagnosticSeverity.Warning,
            reason,
            Origin(node)));

        return new MetacodeOpaqueExpression(MetacodeLanguage.JavaScript, OneLine(Text(node)), reason)
        {
            Origin = Origin(node),
        };
    }

    private MetacodeDeclaration OpaqueDeclaration(AstNode node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(
            MetacodeDiagnosticCodes.UnsupportedDeclaration,
            MetacodeDiagnosticSeverity.Warning,
            reason,
            Origin(node)));

        return new MetacodeOpaqueDeclaration(MetacodeLanguage.JavaScript, OneLine(Text(node)), reason)
        {
            Origin = Origin(node),
        };
    }
}
