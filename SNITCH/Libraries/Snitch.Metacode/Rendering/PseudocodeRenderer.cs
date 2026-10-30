using System.Text;

namespace Snitch.Metacode.Rendering;

public sealed record PseudocodeOptions
{
    public static PseudocodeOptions Default { get; } = new();

    public string Indent { get; init; } = "  ";

    /// <summary>Prefix every declaration and statement with <c>// @path:line</c>.</summary>
    public bool ShowOrigin { get; init; }
}

/// <summary>
/// Prints a <see cref="MetacodeDocument"/> as canonical pseudocode. The output is a small language of
/// its own — uppercase keywords, one statement per line, operator precedence preserved — and
/// <see cref="Frontends.PseudocodeFrontend"/> reads it back, so the text is an interchange format and
/// not just a picture.
/// </summary>
public sealed class PseudocodeRenderer
{
    private readonly PseudocodeOptions _options;

    public PseudocodeRenderer(PseudocodeOptions? options = null) => _options = options ?? PseudocodeOptions.Default;

    public string Render(MetacodeDocument document)
    {
        // Each module text already ends with a newline; the join adds the blank line between modules.
        var modules = document.Modules.Select(m => ModuleText(m, 0)).Where(t => t.Length > 0).ToList();
        return modules.Count == 0 ? string.Empty : string.Join("\n", modules);
    }

    /// <summary>
    /// One declaration on its own, with the rules of a whole document: a type prints its members nested,
    /// a member prints only itself. It is what an index that stores one tree per declaration needs.
    /// </summary>
    public string Render(MetacodeDeclaration declaration) => DeclarationText(declaration, 0);

    /// <summary>A list of declarations at the top level, in the order given.</summary>
    public string Render(IReadOnlyList<MetacodeDeclaration> declarations) => DeclarationsText(declarations, 0);

    private string ModuleText(MetacodeModule module, int depth)
    {
        var keyword = module.Kind == MetacodeModuleKind.Namespace ? "NAMESPACE" : "MODULE";
        var text = new StringBuilder();
        Line(text, depth, $"{keyword} {module.Name}");
        text.Append(DeclarationsText(module.Declarations, depth + 1));
        Line(text, depth, $"END {keyword}");
        return text.ToString();
    }

    private string DeclarationsText(IReadOnlyList<MetacodeDeclaration> declarations, int depth)
    {
        var text = new StringBuilder();
        foreach (var declaration in declarations)
            text.Append(DeclarationText(declaration, depth));
        return text.ToString();
    }

    private string DeclarationText(MetacodeDeclaration declaration, int depth)
    {
        var text = new StringBuilder();
        text.Append(OriginText(declaration.Origin, depth));

        switch (declaration)
        {
            case MetacodeTypeDeclaration type:
            {
                var keyword = TypeKeyword(type.Kind);
                var header = Modifiers(type.Modifiers) + keyword + " " + type.Name;
                if (type.TypeParameters is { Count: > 0 })
                    header += "<" + string.Join(", ", type.TypeParameters.Select(TypeParameter)) + ">";
                if (type.BaseTypes is { Count: > 0 })
                    header += " EXTENDS " + string.Join(", ", type.BaseTypes.Select(b => b.Render()));

                DecoratorLines(text, type.Decorators, depth);
                Line(text, depth, header);
                text.Append(DeclarationsText(type.Members, depth + 1));
                Line(text, depth, $"END {keyword}");
                break;
            }

            case MetacodeFunction function:
                text.Append(FunctionText(function, depth));
                break;

            case MetacodeEvent @event:
            {
                var header = Modifiers(@event.Modifiers) + "EVENT " + @event.Name;
                if (@event.Type is not null)
                    header += " : " + @event.Type.Render();
                Line(text, depth, header);
                break;
            }

            case MetacodeTypeAlias alias:
                Line(text, depth, "ALIAS " + alias.Name + (alias.Target is null ? string.Empty : " = " + alias.Target.Render()));
                break;

            case MetacodeField field:
            {
                var header = Modifiers(field.Modifiers);
                if (!field.Modifiers.HasFlag(MetacodeModifiers.Const))
                    header += "FIELD ";
                header += field.Name;
                if (field.Type is not null)
                    header += " : " + field.Type.Render();
                if (field.Initializer is not null)
                    header += " = " + Expression(field.Initializer, 0, depth);
                Line(text, depth, header);
                break;
            }

            case MetacodeProperty property:
                text.Append(PropertyText(property, depth));
                break;

            case MetacodeEnumMember member:
                Line(text, depth, member.Value is null ? member.Name : member.Name + " = " + Expression(member.Value, 0, depth));
                break;

            case MetacodeOpaqueDeclaration opaque:
                Line(text, depth, OpaqueText(opaque.Language, opaque.Text, opaque.Reason));
                break;
        }

        return text.ToString();
    }

    private string FunctionText(MetacodeFunction function, int depth)
    {
        var keyword = FunctionKeyword(function.Kind);
        var header = Modifiers(function.Modifiers) + keyword + " " + function.Name;
        if (function.TypeParameters is { Count: > 0 })
            header += "<" + string.Join(", ", function.TypeParameters.Select(TypeParameter)) + ">";
        header += "(" + string.Join(", ", function.ParameterList.Select(p => ParameterText(p, depth))) + ")";
        if (function.ReturnType is not null && function.ReturnType.Name != "void")
            header += " : " + function.ReturnType.Render();

        var text = new StringBuilder();
        DecoratorLines(text, function.Decorators, depth);
        Line(text, depth, header);
        text.Append(BlockText(function.Body, depth + 1));
        Line(text, depth, $"END {EndKeyword(function.Kind)}");
        return text.ToString();
    }

    /// <summary>Python decorators, one <c>@name</c> line each, above the declaration they decorate.</summary>
    private void DecoratorLines(StringBuilder text, IReadOnlyList<string>? decorators, int depth)
    {
        foreach (var decorator in DecoratorsOf(decorators))
            Line(text, depth, "@" + decorator);
    }

    private static IReadOnlyList<string> DecoratorsOf(IReadOnlyList<string>? decorators) =>
        decorators ?? Array.Empty<string>();

    private string PropertyText(MetacodeProperty property, int depth)
    {
        var text = new StringBuilder();
        var header = Modifiers(property.Modifiers) + "PROPERTY " + property.Name;
        if (property.Parameters is { Count: > 0 })
            header += "[" + string.Join(", ", property.Parameters.Select(p => ParameterText(p, depth))) + "]";
        if (property.Type is not null)
            header += " : " + property.Type.Render();
        if (property.Initializer is not null)
            header += " = " + Expression(property.Initializer, 0, depth);

        Line(text, depth, header);
        text.Append(AccessorText("GET", property.Getter, depth + 1));
        text.Append(AccessorText("SET", property.Setter, depth + 1));
        Line(text, depth, "END PROPERTY");
        return text.ToString();
    }

    private string AccessorText(string keyword, MetacodeAccessor? accessor, int depth)
    {
        if (accessor is null)
            return string.Empty;

        var text = new StringBuilder();
        var header = Modifiers(accessor.Modifiers) + keyword;
        if (accessor.Body is null)
        {
            Line(text, depth, header + " AUTO");
            return text.ToString();
        }

        if (accessor.ParameterName is not null)
            header += "(" + accessor.ParameterName + ")";

        Line(text, depth, header);
        text.Append(BlockText(accessor.Body, depth + 1));
        Line(text, depth, "END " + keyword);
        return text.ToString();
    }

    private string BlockText(MetacodeBlock? block, int depth)
    {
        if (block is null)
            return string.Empty;

        var text = new StringBuilder();
        foreach (var statement in block.Statements)
            text.Append(StatementText(statement, depth));
        return text.ToString();
    }

    private string StatementText(MetacodeStatement statement, int depth)
    {
        var text = new StringBuilder();
        text.Append(OriginText(statement.Origin, depth));

        switch (statement)
        {
            case MetacodeBlock block:
                text.Append(BlockText(block, depth));
                break;

            case MetacodeLocalVariable variable:
            {
                var header = (variable.IsConstant ? "CONST " : "DECLARE ") + variable.Name;
                if (variable.Type is { IsDynamic: false })
                    header += " : " + variable.Type.Render();
                if (variable.Initializer is not null)
                    header += " = " + Expression(variable.Initializer, 0, depth);
                Line(text, depth, header);
                break;
            }

            case MetacodeAssignment assignment:
                Line(text, depth, InlineStatementText(assignment, depth));
                break;

            case MetacodeExpressionStatement expressionStatement:
                Line(text, depth, Expression(expressionStatement.Expression, 0, depth));
                break;

            case MetacodeReturn @return:
                Line(text, depth, @return.Value is null ? "RETURN" : "RETURN " + Expression(@return.Value, 0, depth));
                break;

            case MetacodeIf @if:
                text.Append(IfChainText(@if, depth));
                break;

            case MetacodeWhile @while:
                Line(text, depth, "WHILE " + Expression(@while.Condition, 0, depth) + " DO");
                text.Append(BlockText(@while.Body, depth + 1));
                Line(text, depth, "END WHILE");
                break;

            case MetacodeDoWhile doWhile:
                Line(text, depth, "REPEAT");
                text.Append(BlockText(doWhile.Body, depth + 1));
                Line(text, depth, "UNTIL " + Expression(doWhile.Condition, 0, depth));
                break;

            case MetacodeForEach forEach:
            {
                var header = (forEach.IsAwait ? "AWAIT FOREACH " : "FOREACH ") + forEach.VariableName;
                if (forEach.VariableType is { IsDynamic: false })
                    header += " : " + forEach.VariableType.Render();
                header += " IN " + Expression(forEach.Iterable, 0, depth) + " DO";
                Line(text, depth, header);
                text.Append(BlockText(forEach.Body, depth + 1));
                Line(text, depth, "END FOREACH");
                break;
            }

            case MetacodeFor @for:
            {
                var header = "FOR " + (@for.Initializer is null ? string.Empty : InlineStatementText(@for.Initializer, depth)) + "; "
                             + (@for.Condition is null ? string.Empty : Expression(@for.Condition, 0, depth)) + "; "
                             + (@for.Increment is null ? string.Empty : InlineStatementText(@for.Increment, depth)) + " DO";
                Line(text, depth, header);
                text.Append(BlockText(@for.Body, depth + 1));
                Line(text, depth, "END FOR");
                break;
            }

            case MetacodeSwitch @switch:
                Line(text, depth, "SWITCH " + Expression(@switch.Subject, 0, depth));
                foreach (var @case in @switch.Cases)
                {
                    if (@case.IsDefault)
                    {
                        Line(text, depth + 1, "DEFAULT");
                    }
                    else
                    {
                        var labels = string.Join(", ", (@case.Labels ?? Array.Empty<MetacodeExpression>()).Select(l => Expression(l, 0, depth)));
                        Line(text, depth + 1, "CASE " + labels);
                    }

                    text.Append(BlockText(@case.Body, depth + 2));
                }

                Line(text, depth, "END SWITCH");
                break;

            case MetacodeBreak:
                Line(text, depth, "BREAK");
                break;

            case MetacodeContinue:
                Line(text, depth, "CONTINUE");
                break;

            case MetacodeGoto @goto:
                Line(text, depth, "GOTO " + @goto.Label);
                break;

            case MetacodeLabel label:
                Line(text, depth, "LABEL " + label.Label);
                text.Append(StatementText(label.Statement, depth + 1));
                Line(text, depth, "END LABEL");
                break;

            case MetacodeThrow @throw:
                Line(text, depth, @throw.Exception is null ? "THROW" : "THROW " + Expression(@throw.Exception, 0, depth));
                break;

            case MetacodeTry @try:
                Line(text, depth, "TRY");
                text.Append(BlockText(@try.Body, depth + 1));
                foreach (var @catch in @try.Catches)
                {
                    var catchHeader = "CATCH";
                    if (@catch.ExceptionType is not null || @catch.VariableName is not null)
                    {
                        var inner = @catch.ExceptionType?.Render() ?? string.Empty;
                        if (@catch.VariableName is not null)
                            inner += (inner.Length > 0 ? " " : string.Empty) + @catch.VariableName;
                        catchHeader += " (" + inner + ")";
                    }

                    if (@catch.Filter is not null)
                        catchHeader += " WHEN " + Expression(@catch.Filter, 0, depth);

                    Line(text, depth, catchHeader);
                    text.Append(BlockText(@catch.Body, depth + 1));
                }

                if (@try.Else is not null)
                {
                    Line(text, depth, "ELSE");
                    text.Append(BlockText(@try.Else, depth + 1));
                }

                if (@try.Finally is not null)
                {
                    Line(text, depth, "FINALLY");
                    text.Append(BlockText(@try.Finally, depth + 1));
                }

                Line(text, depth, "END TRY");
                break;

            case MetacodeUsing @using:
            {
                var header = "USING ";
                if (@using.Declaration is not null)
                {
                    header += @using.Declaration.Name;
                    if (@using.Declaration.Type is { IsDynamic: false })
                        header += " : " + @using.Declaration.Type.Render();
                    if (@using.Declaration.Initializer is not null)
                        header += " = " + Expression(@using.Declaration.Initializer, 0, depth);
                }
                else if (@using.Resource is not null)
                {
                    header += Expression(@using.Resource, 0, depth);
                }

                Line(text, depth, header + " DO");
                text.Append(BlockText(@using.Body, depth + 1));
                Line(text, depth, "END USING");
                break;
            }

            case MetacodeLocalFunction localFunction:
                text.Append(FunctionText(localFunction.Function with { Kind = MetacodeFunctionKind.LocalFunction }, depth));
                break;

            case MetacodeLock @lock:
                Line(text, depth, "LOCK " + Expression(@lock.Target, 0, depth) + " DO");
                text.Append(BlockText(@lock.Body, depth + 1));
                Line(text, depth, "END LOCK");
                break;

            case MetacodeYield @yield:
                Line(text, depth, @yield.Value is null ? "YIELD BREAK" : "YIELD RETURN " + Expression(@yield.Value, 0, depth));
                break;

            case MetacodeImport import:
                Line(text, depth, ImportText(import));
                break;

            case MetacodeComment comment:
                foreach (var line in comment.Text.Replace("\r", string.Empty).Split('\n'))
                    Line(text, depth, "// " + line);
                break;

            case MetacodeOpaqueStatement opaque:
                Line(text, depth, OpaqueText(opaque.Language, opaque.Text, opaque.Reason));
                break;
        }

        return text.ToString();
    }

    private string IfChainText(MetacodeIf @if, int depth, bool leadingElse = false)
    {
        var text = new StringBuilder();
        Line(text, depth, (leadingElse ? "ELSE IF " : "IF ") + Expression(@if.Condition, 0, depth) + " THEN");
        text.Append(BlockText(@if.Then, depth + 1));

        if (@if.Else is null)
        {
            Line(text, depth, "END IF");
            return text.ToString();
        }

        if (@if.Else.Statements.Count == 1 && @if.Else.Statements[0] is MetacodeIf nested)
        {
            text.Append(IfChainText(nested, depth, leadingElse: true));
            return text.ToString();
        }

        Line(text, depth, "ELSE");
        text.Append(BlockText(@if.Else, depth + 1));
        Line(text, depth, "END IF");
        return text.ToString();
    }

    /// <summary>A statement printed on one line, for the three parts of a <c>for</c> header or a <c>switch</c> label.</summary>
    private string InlineStatementText(MetacodeStatement statement, int depth) => statement switch
    {
        MetacodeLocalVariable variable => (variable.IsConstant ? "CONST " : "DECLARE ") + variable.Name
                                          + (variable.Type is { IsDynamic: false } type ? " : " + type.Render() : string.Empty)
                                          + (variable.Initializer is null ? string.Empty : " = " + Expression(variable.Initializer, 0, depth)),
        MetacodeAssignment assignment => Expression(assignment.Target, 17, depth) + " "
                                         + AssignmentOperator(assignment.Operator) + " "
                                         + Expression(assignment.Value, 0, depth),
        MetacodeExpressionStatement expressionStatement => Expression(expressionStatement.Expression, 0, depth),
        _ => OpaqueText(statement.Origin.Language, "statement", "not expressible inline"),
    };

    private string Expression(MetacodeExpression expression, int minimumPrecedence, int depth)
    {
        var precedence = Precedence(expression);
        var parenthesize = precedence < minimumPrecedence;
        var prefix = parenthesize ? "(" : string.Empty;
        var suffix = parenthesize ? ")" : string.Empty;

        var body = expression switch
        {
            MetacodeLiteral literal => literal.Text,
            MetacodeIdentifier identifier => identifier.Name,
            MetacodeMemberAccess access => Expression(access.Target, 17, depth) + (access.NullConditional ? "?." : ".") + access.Member,
            MetacodeInvocation invocation => Expression(invocation.Callee, 17, depth) + "("
                                             + string.Join(", ", invocation.ArgumentList.Select(a => ArgumentText(a, depth))) + ")",
            MetacodeBinary binary => BinaryText(binary, depth),
            MetacodeUnary unary => UnaryText(unary, depth),
            MetacodeConditional conditional => Expression(conditional.Condition, 4, depth) + " ? "
                                               + Expression(conditional.WhenTrue, 4, depth) + " : "
                                               + Expression(conditional.WhenFalse, 3, depth),
            MetacodeIndex index => Expression(index.Target, 17, depth) + "["
                                   + string.Join(", ", index.Indices.Select(i => Expression(i, 0, depth))) + "]",
            MetacodeObjectCreation creation => ObjectCreationText(creation, depth),
            MetacodeWithExpression withExpression => Expression(withExpression.Target, 17, depth)
                                                      + " WITH { " + string.Join(", ", withExpression.Initializers.Select(m =>
                                                          m.Name + " = " + Expression(m.Value, 0, depth))) + " }",
            MetacodeAnonymousObject anonymous => "ANON { " + string.Join(", ", anonymous.Members.Select(m =>
                m.Name + " = " + Expression(m.Value, 0, depth))) + " }",
            MetacodePatternMatch match => Expression(match.Operand, 11, depth) + " MATCHES (" + match.Pattern + ")",
            MetacodePatternText pattern => "PATTERN (" + pattern.Pattern + ")",
            MetacodeSpread spread => "SPREAD " + Expression(spread.Operand, 16, depth),
            MetacodeNewArray array => NewArrayText(array, depth),
            MetacodeCast cast => "CAST(" + cast.Type.Render() + ", " + Expression(cast.Operand, 0, depth) + ")",
            MetacodeTypeCheck check => Expression(check.Operand, 11, depth) + (check.Kind switch
            {
                MetacodeTypeCheckKind.IsNot => " IS NOT ",
                MetacodeTypeCheckKind.As => " AS ",
                _ => " IS ",
            }) + check.Type.Render() + (check.Binding is null ? string.Empty : " " + check.Binding),
            MetacodeCollection collection => CollectionText(collection, depth),
            MetacodeDictionary dictionary => "{ " + string.Join(", ", dictionary.Entries.Select(e =>
                Expression(e.Key, 0, depth) + ": " + Expression(e.Value, 0, depth))) + " }",
            MetacodeComprehension comprehension => ComprehensionText(comprehension, depth),
            MetacodeLambda lambda => LambdaText(lambda, depth),
            MetacodeAwait awaitExpression => "AWAIT " + Expression(awaitExpression.Operand, 16, depth),
            MetacodeRange range => RangeText(range, depth),
            MetacodeIndexFromEnd fromEnd => "^" + Expression(fromEnd.Operand, 16, depth),
            MetacodeInterpolatedString interpolated => InterpolationText(interpolated, depth),
            MetacodeAssignmentExpression assignment => Expression(assignment.Target, 17, depth) + " "
                                                       + AssignmentOperator(assignment.Operator) + " "
                                                       + Expression(assignment.Value, 1, depth),
            MetacodeTypeOf typeOf => "typeof(" + typeOf.Type.Render() + ")",
            MetacodeSwitchExpression switchExpression => Expression(switchExpression.Subject, 17, depth) + " SWITCH { "
                                                         + string.Join(", ", switchExpression.Arms.Select(arm =>
                                                             arm.Pattern
                                                             + (arm.Guard is null ? string.Empty : " WHEN " + Expression(arm.Guard, 0, depth))
                                                             + " => " + Expression(arm.Value, 0, depth))) + " }",
            MetacodeOpaqueExpression opaque => OpaqueText(opaque.Language, opaque.Text, opaque.Reason),
            _ => "RAW[?] expression",
        };

        return prefix + body + suffix;
    }

    private string BinaryText(MetacodeBinary binary, int depth)
    {
        var precedence = Precedence(binary);
        var rightAssociative = binary.Operator is MetacodeBinaryOperator.Power or MetacodeBinaryOperator.NullCoalesce;
        return Expression(binary.Left, rightAssociative ? precedence + 1 : precedence, depth)
               + " " + BinaryOperator(binary.Operator) + " "
               + Expression(binary.Right, rightAssociative ? precedence : precedence + 1, depth);
    }

    private string UnaryText(MetacodeUnary unary, int depth) => unary.Operator switch
    {
        MetacodeUnaryOperator.Not => "NOT " + Expression(unary.Operand, 16, depth),
        // A doubled sign would read as ++/--, so the operand of a minus keeps a parenthesis of its own.
        MetacodeUnaryOperator.Negate => "-" + Expression(unary.Operand, 17, depth),
        MetacodeUnaryOperator.Plus => "+" + Expression(unary.Operand, 16, depth),
        MetacodeUnaryOperator.BitwiseNot => "~" + Expression(unary.Operand, 16, depth),
        MetacodeUnaryOperator.Increment => unary.IsPostfix
            ? Expression(unary.Operand, 17, depth) + "++"
            : "++" + Expression(unary.Operand, 17, depth),
        MetacodeUnaryOperator.Decrement => unary.IsPostfix
            ? Expression(unary.Operand, 17, depth) + "--"
            : "--" + Expression(unary.Operand, 17, depth),
        // Pointer access binds like a prefix operator and prints the way C prints it.
        MetacodeUnaryOperator.Dereference => "*" + Expression(unary.Operand, 17, depth),
        MetacodeUnaryOperator.AddressOf => "&" + Expression(unary.Operand, 17, depth),
        _ => Expression(unary.Operand, 16, depth),
    };

    private string ObjectCreationText(MetacodeObjectCreation creation, int depth)
    {
        var text = new StringBuilder();
        // A target-typed `new()` has no type of its own; the declaration around it carries the type.
        text.Append(creation.Type.Name == "new" ? "NEW" : "NEW " + creation.Type.Render());
        text.Append('(').Append(string.Join(", ", (creation.Arguments ?? Array.Empty<MetacodeArgument>()).Select(a => ArgumentText(a, depth)))).Append(')');
        if (creation.CollectionElements is { Count: > 0 })
            text.Append(" { ").Append(string.Join(", ", creation.CollectionElements.Select(e => Expression(e, 0, depth)))).Append(" }");
        if (creation.MemberInitializers is { Count: > 0 })
            text.Append(" { ").Append(string.Join(", ", creation.MemberInitializers.Select(m => m.Name + " = " + Expression(m.Value, 0, depth)))).Append(" }");
        return text.ToString();
    }

    private string NewArrayText(MetacodeNewArray array, int depth)
    {
        var text = new StringBuilder("NEW ");
        if (array.ElementType is not null)
            text.Append(array.ElementType.Render());
        var items = array.Elements ?? array.Sizes ?? Array.Empty<MetacodeExpression>();
        text.Append('[').Append(string.Join(", ", items.Select(e => Expression(e, 0, depth)))).Append(']');
        return text.ToString();
    }

    private string CollectionText(MetacodeCollection collection, int depth)
    {
        var (open, close) = collection.Kind switch
        {
            MetacodeCollectionKind.Set => ("{", "}"),
            MetacodeCollectionKind.Tuple => ("(", ")"),
            _ => ("[", "]"),
        };

        return open + string.Join(", ", collection.Items.Select(i => Expression(i, 0, depth))) + close;
    }

    /// <summary>
    /// A comprehension prints inside the brackets of the container it builds, with its clauses in
    /// source order: <c>[x * 2 FOR x IN xs IF x > 0]</c>.
    /// </summary>
    private string ComprehensionText(MetacodeComprehension comprehension, int depth)
    {
        var (open, close) = comprehension.Kind switch
        {
            MetacodeComprehensionKind.Set or MetacodeComprehensionKind.Dictionary => ("{", "}"),
            MetacodeComprehensionKind.Generator => ("(", ")"),
            _ => ("[", "]"),
        };

        var text = new StringBuilder(open);
        text.Append(Expression(comprehension.Element, 0, depth));
        if (comprehension.Kind == MetacodeComprehensionKind.Dictionary && comprehension.Value is not null)
            text.Append(" : ").Append(Expression(comprehension.Value, 0, depth));

        foreach (var clause in comprehension.Clauses)
        {
            if (clause.Kind == MetacodeComprehensionClauseKind.If)
            {
                text.Append(" IF ").Append(Expression(clause.Expression, 0, depth));
                continue;
            }

            text.Append(" FOR ").Append(string.Join(", ", clause.Targets)).Append(" IN ")
                .Append(Expression(clause.Expression, 0, depth));
        }

        return text.Append(close).ToString();
    }

    private static string ImportText(MetacodeImport import)
    {
        var names = string.Join(", ", import.NameList.Select(n => n.Alias is null ? n.Name : n.Name + " AS " + n.Alias));
        return import.IsFrom ? "FROM " + import.Module + " IMPORT " + names : "IMPORT " + names;
    }

    private string RangeText(MetacodeRange range, int depth)
    {
        // No brackets of its own: an index supplies them, so 1..3 inside a[i] reads a[1..3].
        var text = string.Empty;
        if (range.Start is not null)
            text += Expression(range.Start, 0, depth);
        text += "..";
        if (range.End is not null)
            text += Expression(range.End, 0, depth);
        if (range.Step is not null)
            text += " : " + Expression(range.Step, 0, depth);
        return text;
    }

    private string InterpolationText(MetacodeInterpolatedString interpolated, int depth)
    {
        var text = new StringBuilder("$\"");
        foreach (var part in interpolated.Parts)
        {
            if (part.Text is not null)
                text.Append(part.Text
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("{", "{{")
                    .Replace("}", "}}"));
            else if (part.Expression is not null)
                text.Append('{').Append(Expression(part.Expression, 0, depth)).Append('}');
        }

        return text.Append('"').ToString();
    }

    private string LambdaText(MetacodeLambda lambda, int depth)
    {
        var text = new StringBuilder();
        text.Append('(')
            .Append(string.Join(", ", (lambda.Parameters ?? Array.Empty<MetacodeParameter>()).Select(p => ParameterText(p, depth))))
            .Append(") => ");

        if (lambda.Expression is not null)
        {
            text.Append(Expression(lambda.Expression, 2, depth));
            return text.ToString();
        }

        if (lambda.Body is null || lambda.Body.Statements.Count == 0)
            return text.Append("{}").ToString();

        text.Append("{\n");
        text.Append(BlockText(lambda.Body, depth + 1));
        text.Append(Indent(depth)).Append('}');
        return text.ToString();
    }

    private string ParameterText(MetacodeParameter parameter, int depth)
    {
        var prefix = parameter.Kind switch
        {
            MetacodeParameterKind.Ref => "ref ",
            MetacodeParameterKind.Out => "out ",
            MetacodeParameterKind.In => "in ",
            MetacodeParameterKind.Params => "params ",
            MetacodeParameterKind.Keyword => "*",
            _ => string.Empty,
        };

        var text = prefix + parameter.Name;
        if (parameter.Type is not null && !parameter.Type.IsDynamic)
            text += " : " + parameter.Type.Render();
        if (parameter.DefaultValue is not null)
            text += " = " + Expression(parameter.DefaultValue, 0, depth);
        return text;
    }

    private string ArgumentText(MetacodeArgument argument, int depth)
    {
        var prefix = argument.Kind switch
        {
            MetacodeParameterKind.Ref => "ref ",
            MetacodeParameterKind.Out => "out ",
            MetacodeParameterKind.In => "in ",
            _ => string.Empty,
        };

        return prefix + (argument.Name is null ? string.Empty : argument.Name + ": ") + Expression(argument.Value, 0, depth);
    }

    private static string TypeParameter(MetacodeTypeParameter parameter) =>
        parameter.Constraints is { Count: > 0 }
            ? parameter.Name + " : " + string.Join(" + ", parameter.Constraints)
            : parameter.Name;

    private static int Precedence(MetacodeExpression expression) => expression switch
    {
        MetacodeAssignmentExpression => 1,
        MetacodeLambda => 2,
        MetacodeConditional => 3,
        MetacodeBinary binary => binary.Operator switch
        {
            MetacodeBinaryOperator.NullCoalesce => 4,
            MetacodeBinaryOperator.LogicalOr => 5,
            MetacodeBinaryOperator.LogicalAnd => 6,
            MetacodeBinaryOperator.BitwiseOr => 7,
            MetacodeBinaryOperator.BitwiseXor => 8,
            MetacodeBinaryOperator.BitwiseAnd => 9,
            MetacodeBinaryOperator.Equal or MetacodeBinaryOperator.NotEqual
                or MetacodeBinaryOperator.Contains or MetacodeBinaryOperator.NotContains => 10,
            MetacodeBinaryOperator.LessThan or MetacodeBinaryOperator.LessThanOrEqual
                or MetacodeBinaryOperator.GreaterThan or MetacodeBinaryOperator.GreaterThanOrEqual => 11,
            MetacodeBinaryOperator.ShiftLeft or MetacodeBinaryOperator.ShiftRight => 12,
            MetacodeBinaryOperator.Add or MetacodeBinaryOperator.Subtract => 13,
            MetacodeBinaryOperator.Multiply or MetacodeBinaryOperator.Divide
                or MetacodeBinaryOperator.IntegerDivide or MetacodeBinaryOperator.Modulo => 14,
            MetacodeBinaryOperator.Power => 15,
            _ => 13,
        },
        MetacodeTypeCheck or MetacodePatternMatch => 11,
        MetacodeUnary or MetacodeAwait or MetacodeCast or MetacodeSpread => 16,
        _ => 17,
    };

    private static string BinaryOperator(MetacodeBinaryOperator @operator) => @operator switch
    {
        MetacodeBinaryOperator.Add => "+",
        MetacodeBinaryOperator.Subtract => "-",
        MetacodeBinaryOperator.Multiply => "*",
        MetacodeBinaryOperator.Divide => "/",
        MetacodeBinaryOperator.IntegerDivide => "DIV",
        MetacodeBinaryOperator.Modulo => "%",
        MetacodeBinaryOperator.Power => "**",
        MetacodeBinaryOperator.Equal => "==",
        MetacodeBinaryOperator.NotEqual => "!=",
        MetacodeBinaryOperator.LessThan => "<",
        MetacodeBinaryOperator.LessThanOrEqual => "<=",
        MetacodeBinaryOperator.GreaterThan => ">",
        MetacodeBinaryOperator.GreaterThanOrEqual => ">=",
        MetacodeBinaryOperator.LogicalAnd => "AND",
        MetacodeBinaryOperator.LogicalOr => "OR",
        MetacodeBinaryOperator.BitwiseAnd => "&",
        MetacodeBinaryOperator.BitwiseOr => "|",
        MetacodeBinaryOperator.BitwiseXor => "^",
        MetacodeBinaryOperator.ShiftLeft => "<<",
        MetacodeBinaryOperator.ShiftRight => ">>",
        MetacodeBinaryOperator.NullCoalesce => "??",
        MetacodeBinaryOperator.Contains => "IN",
        MetacodeBinaryOperator.NotContains => "NOT IN",
        _ => "?",
    };

    private static string AssignmentOperator(MetacodeAssignmentOperator @operator) => @operator switch
    {
        MetacodeAssignmentOperator.Assign => "=",
        MetacodeAssignmentOperator.Add => "+=",
        MetacodeAssignmentOperator.Subtract => "-=",
        MetacodeAssignmentOperator.Multiply => "*=",
        MetacodeAssignmentOperator.Divide => "/=",
        MetacodeAssignmentOperator.Modulo => "%=",
        MetacodeAssignmentOperator.Power => "**=",
        MetacodeAssignmentOperator.BitwiseAnd => "&=",
        MetacodeAssignmentOperator.BitwiseOr => "|=",
        MetacodeAssignmentOperator.BitwiseXor => "^=",
        MetacodeAssignmentOperator.ShiftLeft => "<<=",
        MetacodeAssignmentOperator.ShiftRight => ">>=",
        MetacodeAssignmentOperator.Coalesce => "??=",
        _ => "=",
    };

    private static string TypeKeyword(MetacodeTypeKind kind) => kind switch
    {
        MetacodeTypeKind.Class => "CLASS",
        MetacodeTypeKind.Struct => "STRUCT",
        MetacodeTypeKind.Interface => "INTERFACE",
        MetacodeTypeKind.Enum => "ENUM",
        MetacodeTypeKind.Record => "RECORD",
        MetacodeTypeKind.Union => "UNION",
        MetacodeTypeKind.Trait => "TRAIT",
        _ => "CLASS",
    };

    private static string FunctionKeyword(MetacodeFunctionKind kind) => kind switch
    {
        MetacodeFunctionKind.Function => "FUNCTION",
        MetacodeFunctionKind.Method => "METHOD",
        MetacodeFunctionKind.Constructor => "CONSTRUCTOR",
        MetacodeFunctionKind.Destructor => "DESTRUCTOR",
        MetacodeFunctionKind.Operator => "OPERATOR",
        MetacodeFunctionKind.LocalFunction => "LOCAL FUNCTION",
        _ => "FUNCTION",
    };

    private static string EndKeyword(MetacodeFunctionKind kind) =>
        kind == MetacodeFunctionKind.LocalFunction ? "FUNCTION" : FunctionKeyword(kind);

    private static string Modifiers(MetacodeModifiers modifiers)
    {
        if (modifiers == MetacodeModifiers.None)
            return string.Empty;

        var parts = new List<string>(4);
        Add(MetacodeModifiers.Public, "PUBLIC");
        Add(MetacodeModifiers.Protected, "PROTECTED");
        Add(MetacodeModifiers.Internal, "INTERNAL");
        Add(MetacodeModifiers.Private, "PRIVATE");
        Add(MetacodeModifiers.New, "NEW");
        Add(MetacodeModifiers.Static, "STATIC");
        Add(MetacodeModifiers.Abstract, "ABSTRACT");
        Add(MetacodeModifiers.Virtual, "VIRTUAL");
        Add(MetacodeModifiers.Override, "OVERRIDE");
        Add(MetacodeModifiers.Sealed, "SEALED");
        Add(MetacodeModifiers.ReadOnly, "READONLY");
        Add(MetacodeModifiers.Const, "CONST");
        Add(MetacodeModifiers.Async, "ASYNC");
        Add(MetacodeModifiers.Partial, "PARTIAL");
        Add(MetacodeModifiers.Extern, "EXTERN");
        Add(MetacodeModifiers.Unsafe, "UNSAFE");
        return string.Join(" ", parts) + " ";

        void Add(MetacodeModifiers flag, string text)
        {
            if (modifiers.HasFlag(flag))
                parts.Add(text);
        }
    }

    public static string LanguageTag(MetacodeLanguage language) => language switch
    {
        MetacodeLanguage.CSharp => "cs",
        MetacodeLanguage.VisualBasic => "vb",
        MetacodeLanguage.Python => "py",
        MetacodeLanguage.Cpp => "cpp",
        MetacodeLanguage.Java => "java",
        MetacodeLanguage.JavaScript => "js",
        MetacodeLanguage.TypeScript => "ts",
        MetacodeLanguage.Rust => "rs",
        MetacodeLanguage.Go => "go",
        MetacodeLanguage.Sql => "sql",
        MetacodeLanguage.Pseudocode => "pseudo",
        MetacodeLanguage.Razor => "razor",
        _ => "?",
    };

    public static MetacodeLanguage LanguageFromTag(string tag) => tag switch
    {
        "cs" => MetacodeLanguage.CSharp,
        "vb" => MetacodeLanguage.VisualBasic,
        "py" => MetacodeLanguage.Python,
        "cpp" => MetacodeLanguage.Cpp,
        "java" => MetacodeLanguage.Java,
        "js" => MetacodeLanguage.JavaScript,
        "ts" => MetacodeLanguage.TypeScript,
        "rs" => MetacodeLanguage.Rust,
        "go" => MetacodeLanguage.Go,
        "sql" => MetacodeLanguage.Sql,
        "pseudo" => MetacodeLanguage.Pseudocode,
        "razor" => MetacodeLanguage.Razor,
        _ => MetacodeLanguage.Unknown,
    };

    private static string OpaqueText(MetacodeLanguage language, string text, string reason)
    {
        var payload = text.Replace("\r", string.Empty).Replace("\n", "\\n");
        return $"RAW[{LanguageTag(language)}] {payload}" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : " // " + reason.Replace("\r", string.Empty).Replace("\n", " "));
    }

    private string OriginText(MetacodeOrigin origin, int depth) =>
        _options.ShowOrigin && origin.IsKnown ? Indent(depth) + $"// @{origin.Location}\n" : string.Empty;

    private string Indent(int depth) => string.Concat(Enumerable.Repeat(_options.Indent, depth));

    private void Line(StringBuilder text, int depth, string content)
    {
        text.Append(Indent(depth));
        text.Append(content);
        text.Append('\n');
    }
}
