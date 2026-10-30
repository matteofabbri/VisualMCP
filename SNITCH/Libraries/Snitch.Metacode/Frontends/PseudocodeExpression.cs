using System.Text;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// The expression half of the pseudocode reader: a tokenizer and a precedence-climbing parser whose
/// table mirrors the renderer's, so <c>a + b * c</c> comes back as the same tree that produced it.
/// </summary>
internal sealed class PseudocodeExpression
{
    private readonly List<string> _tokens;
    private readonly List<MetacodeDiagnostic> _diagnostics;
    private readonly Dictionary<string, IReadOnlyList<MetacodeStatement>>? _blocks;
    private int _index;

    private PseudocodeExpression(
        List<string> tokens,
        List<MetacodeDiagnostic> diagnostics,
        Dictionary<string, IReadOnlyList<MetacodeStatement>>? blocks = null)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
        _blocks = blocks;
    }

    /// <summary>
    /// Parses one expression. When <paramref name="parseBlock"/> is given, a lambda whose body is a
    /// brace block — <c>(x) =&gt; { … }</c>, which the renderer prints across several lines — is cut out
    /// of the text, its statements are parsed by the caller's statement parser, and the lambda gets
    /// that block back. Without it a block body is unreadable, because statements have no place in an
    /// expression grammar.
    /// </summary>
    public static MetacodeExpression Parse(
        string text,
        List<MetacodeDiagnostic> diagnostics,
        Func<string, IReadOnlyList<MetacodeStatement>>? parseBlock = null)
    {
        Dictionary<string, IReadOnlyList<MetacodeStatement>>? blocks = null;
        if (parseBlock is not null && text.Contains("=>", StringComparison.Ordinal))
        {
            blocks = new Dictionary<string, IReadOnlyList<MetacodeStatement>>(StringComparer.Ordinal);
            text = ExtractBlocks(text, parseBlock, blocks);
        }

        var parser = new PseudocodeExpression(Tokenize(text), diagnostics, blocks);
        if (parser.AtEnd)
            return new MetacodeOpaqueExpression(MetacodeLanguage.Pseudocode, text, "empty expression");

        var expression = parser.Expression(0);
        if (!parser.AtEnd)
            parser.Report("unexpected '" + parser.Peek() + "' after the expression");

        return expression;
    }

    private bool AtEnd => _index >= _tokens.Count;

    private string? Peek() => _index < _tokens.Count ? _tokens[_index] : null;

    private string? PeekAt(int offset) => _index + offset < _tokens.Count ? _tokens[_index + offset] : null;

    private string? Take() => _index < _tokens.Count ? _tokens[_index++] : null;

    private bool TryTake(string token)
    {
        if (Peek() != token)
            return false;
        _index++;
        return true;
    }

    private void Expect(string token)
    {
        if (!TryTake(token))
            Report("expected '" + token + "'");
    }

    private void Report(string message) =>
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.ParseError, MetacodeDiagnosticSeverity.Error, message, new MetacodeOrigin(MetacodeLanguage.Pseudocode)));

    private void Warn(string code, string message) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Warning, message, new MetacodeOrigin(MetacodeLanguage.Pseudocode)));

    private MetacodeExpression Expression(int minimum)
    {
        var left = Unary();

        while (true)
        {
            var token = Peek();
            if (token is null)
                break;

            if (token == "?")
            {
                if (3 < minimum)
                    break;
                Take();
                var whenTrue = Expression(4);
                Expect(":");
                var whenFalse = Expression(3);
                left = new MetacodeConditional(left, whenTrue, whenFalse);
                continue;
            }

            if (token == "IS")
            {
                if (11 < minimum)
                    break;
                Take();
                var negated = TryTake("NOT");
                var type = Type();
                string? binding = null;
                if (Peek() is { } next && IsIdentifier(next) && !IsTypeKeyword(next))
                    binding = Take();
                left = new MetacodeTypeCheck(left, type, negated ? MetacodeTypeCheckKind.IsNot : MetacodeTypeCheckKind.Is, binding);
                continue;
            }

            if (token == "MATCHES")
            {
                if (11 < minimum)
                    break;
                Take();
                left = new MetacodePatternMatch(left, ParenthesizedPattern());
                continue;
            }

            if (token == "AS")
            {
                if (11 < minimum)
                    break;
                Take();
                left = new MetacodeTypeCheck(left, Type(), MetacodeTypeCheckKind.As);
                continue;
            }

            if (token == "NOT" && PeekAt(1) == "IN")
            {
                if (10 < minimum)
                    break;
                Take();
                Take();
                left = new MetacodeBinary(MetacodeBinaryOperator.NotContains, left, Expression(11));
                continue;
            }

            var @operator = BinaryOperator(token);
            if (@operator is null || @operator.Value.Precedence < minimum)
                break;

            Take();
            var right = Expression(@operator.Value.RightAssociative
                ? @operator.Value.Precedence
                : @operator.Value.Precedence + 1);
            left = new MetacodeBinary(@operator.Value.Operator, left, right);
        }

        return left;
    }

    private static (MetacodeBinaryOperator Operator, int Precedence, bool RightAssociative)? BinaryOperator(string token) => token switch
    {
        "??" => (MetacodeBinaryOperator.NullCoalesce, 4, true),
        "OR" or "||" => (MetacodeBinaryOperator.LogicalOr, 5, false),
        "AND" or "&&" => (MetacodeBinaryOperator.LogicalAnd, 6, false),
        "|" => (MetacodeBinaryOperator.BitwiseOr, 7, false),
        "^" => (MetacodeBinaryOperator.BitwiseXor, 8, false),
        "&" => (MetacodeBinaryOperator.BitwiseAnd, 9, false),
        "==" => (MetacodeBinaryOperator.Equal, 10, false),
        "!=" => (MetacodeBinaryOperator.NotEqual, 10, false),
        "IN" => (MetacodeBinaryOperator.Contains, 10, false),
        "<" => (MetacodeBinaryOperator.LessThan, 11, false),
        "<=" => (MetacodeBinaryOperator.LessThanOrEqual, 11, false),
        ">" => (MetacodeBinaryOperator.GreaterThan, 11, false),
        ">=" => (MetacodeBinaryOperator.GreaterThanOrEqual, 11, false),
        "<<" => (MetacodeBinaryOperator.ShiftLeft, 12, false),
        ">>" => (MetacodeBinaryOperator.ShiftRight, 12, false),
        "+" => (MetacodeBinaryOperator.Add, 13, false),
        "-" => (MetacodeBinaryOperator.Subtract, 13, false),
        "*" => (MetacodeBinaryOperator.Multiply, 14, false),
        "/" => (MetacodeBinaryOperator.Divide, 14, false),
        "%" => (MetacodeBinaryOperator.Modulo, 14, false),
        "DIV" => (MetacodeBinaryOperator.IntegerDivide, 14, false),
        "**" => (MetacodeBinaryOperator.Power, 15, true),
        _ => null,
    };

    private MetacodeExpression Unary()
    {
        var token = Peek();
        switch (token)
        {
            case "NOT":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Not, Unary());
            case "-":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Negate, Unary());
            case "+":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Plus, Unary());
            case "~":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.BitwiseNot, Unary());
            case "++":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Increment, Unary());
            case "--":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Decrement, Unary());
            // `*p` and `&x` in prefix position are pointer access, exactly as C reads them: the same
            // token is binary in `a * b` because the binary loop consumes it first.
            case "*":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.Dereference, Unary());
            case "&":
                Take();
                return new MetacodeUnary(MetacodeUnaryOperator.AddressOf, Unary());
            case "AWAIT":
                Take();
                return new MetacodeAwait(Unary());
            case "^":
                Take();
                return new MetacodeIndexFromEnd(Unary());
            case "NEW":
                Take();
                return New();
            case "SPREAD":
                Take();
                return new MetacodeSpread(Unary());
            case "CAST":
                return Cast();
            case "typeof":
                Take();
                Expect("(");
                var type = Type();
                Expect(")");
                return new MetacodeTypeOf(type);
        }

        return Postfix();
    }

    private MetacodeExpression Cast()
    {
        Take();
        Expect("(");
        var type = Type();
        Expect(",");
        var operand = Expression(0);
        Expect(")");
        return new MetacodeCast(type, operand);
    }

    private MetacodeExpression New()
    {
        // `NEW(...)` is a target-typed construction: the type comes from the declaration around it.
        var type = Peek() == "(" ? new MetacodeTypeRef("new") : Type();
        var arguments = TryTake("(") ? Arguments(")") : new List<MetacodeArgument>();
        var creation = new MetacodeObjectCreation(type, arguments);

        if (Peek() != "{")
            return creation;

        Take();
        var elements = new List<MetacodeExpression>();
        var members = new List<MetacodeMemberInitializer>();
        while (!AtEnd && Peek() != "}")
        {
            var start = _index;
            var key = Expression(0);
            if (TryTake("="))
            {
                members.Add(new MetacodeMemberInitializer(Text(start, _index - 2), Expression(0)));
            }
            else
            {
                elements.Add(key);
            }

            if (!TryTake(","))
                break;
        }

        Expect("}");
        return creation with
        {
            CollectionElements = elements.Count == 0 ? null : elements,
            MemberInitializers = members.Count == 0 ? null : members,
        };
    }

    private string Text(int from, int to)
    {
        var parts = new List<string>();
        for (var i = from; i <= to && i < _tokens.Count; i++)
            parts.Add(_tokens[i]);
        return string.Concat(parts);
    }

    private MetacodeExpression Postfix()
    {
        var expression = Primary();

        while (true)
        {
            var token = Peek();
            switch (token)
            {
                case ".":
                    Take();
                    expression = new MetacodeMemberAccess(expression, Take() ?? "?");
                    continue;
                case "?.":
                    Take();
                    expression = new MetacodeMemberAccess(expression, Take() ?? "?", NullConditional: true);
                    continue;
                case "(":
                    Take();
                    expression = new MetacodeInvocation(expression, Arguments(")"));
                    continue;
                case "[":
                    Take();
                    expression = new MetacodeIndex(expression, Indices("]"));
                    continue;
                case "++":
                    Take();
                    expression = new MetacodeUnary(MetacodeUnaryOperator.Increment, expression, IsPostfix: true);
                    continue;
                case "--":
                    Take();
                    expression = new MetacodeUnary(MetacodeUnaryOperator.Decrement, expression, IsPostfix: true);
                    continue;
                case "SWITCH":
                    Take();
                    expression = SwitchExpression(expression);
                    continue;
                case "WITH":
                    Take();
                    expression = With(expression);
                    continue;
                default:
                    return expression;
            }
        }
    }

    private MetacodeExpression SwitchExpression(MetacodeExpression subject)
    {
        Expect("{");
        var arms = new List<MetacodeSwitchArm>();
        while (!AtEnd && Peek() != "}")
        {
            var pattern = new List<string>();
            while (!AtEnd && Peek() is { } token && token is not "=>" and not "WHEN")
                pattern.Add(Take()!);

            MetacodeExpression? guard = null;
            if (TryTake("WHEN"))
                guard = Expression(0);

            Expect("=>");
            var value = Expression(0);
            arms.Add(new MetacodeSwitchArm(string.Join(" ", pattern), guard, value));
            if (!TryTake(","))
                break;
        }

        Expect("}");
        return new MetacodeSwitchExpression(subject, arms);
    }

    private MetacodeExpression With(MetacodeExpression target)
    {
        Expect("{");
        return new MetacodeWithExpression(target, MemberInitializers("}"));
    }

    private MetacodeExpression AnonymousObject()
    {
        Expect("{");
        return new MetacodeAnonymousObject(MemberInitializers("}"));
    }

    private List<MetacodeMemberInitializer> MemberInitializers(string close)
    {
        var members = new List<MetacodeMemberInitializer>();
        while (!AtEnd && Peek() != close)
        {
            var name = Take() ?? "?";
            Expect("=");
            members.Add(new MetacodeMemberInitializer(name, Expression(0)));
            if (!TryTake(","))
                break;
        }

        Expect(close);
        return members;
    }

    /// <summary>
    /// The text of a pattern, read from the balanced parentheses the renderer puts around it. The
    /// pattern grammar stays the source's, exactly as <see cref="MetacodeSwitchArm"/> keeps its own.
    /// </summary>
    private string ParenthesizedPattern()
    {
        if (!TryTake("("))
        {
            Report("expected '(' after a pattern");
            return string.Empty;
        }

        var parts = new List<string>();
        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            var token = Take()!;
            if (token == "(")
            {
                depth++;
            }
            else if (token == ")")
            {
                depth--;
                if (depth == 0)
                    break;
            }

            parts.Add(token);
        }

        if (depth != 0)
            Report("unbalanced pattern text");

        return MetacodePatternText.Normalize(string.Join(" ", parts));
    }

    private MetacodeExpression Primary()
    {
        var token = Take();
        if (token is null)
            return Opaque("<missing>");

        if (token.StartsWith("$\"", StringComparison.Ordinal))
            return Interpolated(token);

        if (token.StartsWith('"'))
            return new MetacodeLiteral(MetacodeLiteralKind.String, Unescape(token));
        if (token.StartsWith('\''))
            return new MetacodeLiteral(MetacodeLiteralKind.Character, token);

        if (IsNumber(token))
            return new MetacodeLiteral(MetacodeLiteralKind.Number, token);

        switch (token)
        {
            case "true":
                return MetacodeLiteral.True;
            case "false":
                return MetacodeLiteral.False;
            case "null":
                return MetacodeLiteral.Null;
            case "default":
                if (TryTake("("))
                {
                    var type = Type();
                    Expect(")");
                    return new MetacodeLiteral(MetacodeLiteralKind.Default, "default(" + type.Render() + ")");
                }

                return new MetacodeLiteral(MetacodeLiteralKind.Default, "default");
            case "(":
                return Grouped();
            case "[":
                return List();
            case "{":
                return Braced();
            case "RAW":
                return Raw();
            case "ANON":
                return AnonymousObject();
            case "PATTERN":
                return new MetacodePatternText(ParenthesizedPattern());
        }

        if (IsIdentifier(token))
            return new MetacodeIdentifier(token);

        return Opaque(token);
    }

    private MetacodeExpression Raw()
    {
        var payload = new List<string>();
        if (TryTake("["))
        {
            while (!AtEnd && Peek() != "]")
                Take();
            Expect("]");
        }

        // A RAW occupies the rest of the expression — the renderer never prints one inside a larger
        // expression — except for the ` // reason` it appends, which is the diagnostic's business.
        while (!AtEnd)
            payload.Add(Take()!);

        for (var i = 1; i < payload.Count; i++)
        {
            if (payload[i - 1] != "/" || payload[i] != "/")
                continue;

            payload.RemoveRange(i - 1, payload.Count - (i - 1));
            break;
        }

        var text = string.Join(" ", payload);
        // RAW is valid canonical text — the renderer prints it for a construct no node covers — so
        // reading it back is a warning, not a parse failure: the tree exists, that part of it is opaque.
        Warn(MetacodeDiagnosticCodes.LossyLowering, "an opaque expression is kept as text: " + text);
        return new MetacodeOpaqueExpression(MetacodeLanguage.Pseudocode, text, "opaque");
    }

    private MetacodeExpression Grouped()
    {
        // A parenthesised list of names followed by => is a lambda; anything else is a group or a tuple.
        if (IsLambda())
        {
            var parameters = Parameters();
            Expect("=>");
            if (_blocks is not null && Peek() is { } block && _blocks.TryGetValue(block, out var body))
            {
                Take();
                return new MetacodeLambda(parameters, Body: new MetacodeBlock(body));
            }

            return new MetacodeLambda(parameters, Expression: Expression(2));
        }

        var first = Expression(0);

        // `(x for x in xs)`: a generator expression, which only differs from a group by its clause.
        if (Peek() == "FOR")
            return Comprehension(MetacodeComprehensionKind.Generator, first, null, ")");

        if (!TryTake(","))
        {
            Expect(")");
            return first;
        }

        var items = new List<MetacodeExpression> { first };
        do
        {
            items.Add(Expression(0));
        }
        while (TryTake(","));

        Expect(")");
        return new MetacodeCollection(MetacodeCollectionKind.Tuple, items);
    }

    /// <summary>
    /// The clauses of a comprehension, in source order, then the closing bracket. Each
    /// <c>FOR</c> contributes its targets and its iterable; each <c>IF</c> a filter.
    /// </summary>
    private MetacodeExpression Comprehension(
        MetacodeComprehensionKind kind,
        MetacodeExpression element,
        MetacodeExpression? value,
        string close)
    {
        var clauses = new List<MetacodeComprehensionClause>();
        while (Peek() is "FOR" or "IF")
        {
            if (Take() == "FOR")
            {
                var targets = Targets();
                Expect("IN");
                clauses.Add(new MetacodeComprehensionClause(MetacodeComprehensionClauseKind.For, targets, Expression(0)));
                continue;
            }

            clauses.Add(new MetacodeComprehensionClause(MetacodeComprehensionClauseKind.If, null, Expression(0)));
        }

        Expect(close);
        return new MetacodeComprehension(kind, element, clauses, value);
    }

    /// <summary>The names between <c>FOR</c> and <c>IN</c>, one per comma at depth zero.</summary>
    private List<string> Targets()
    {
        var targets = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        while (!AtEnd)
        {
            var token = Peek()!;
            if (depth == 0 && token == "IN")
                break;

            Take();
            if (token is "(" or "[" or "{")
                depth++;
            else if (token is ")" or "]" or "}")
                depth--;

            if (token == "," && depth == 0)
            {
                if (current.Length > 0)
                    targets.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(token);
        }

        if (current.Length > 0)
            targets.Add(current.ToString());
        return targets;
    }

    private bool IsLambda()
    {
        var depth = 0;
        for (var i = _index - 1; i < _tokens.Count; i++)
        {
            if (_tokens[i] == "(")
                depth++;
            else if (_tokens[i] == ")" && --depth == 0)
                return i + 1 < _tokens.Count && _tokens[i + 1] == "=>";
        }

        return false;
    }

    private List<MetacodeParameter> Parameters()
    {
        var parameters = new List<MetacodeParameter>();
        if (TryTake(")"))
            return parameters;

        while (!AtEnd && Peek() != ")")
        {
            var kind = MetacodeParameterKind.Value;
            if (TryTake("ref"))
                kind = MetacodeParameterKind.Ref;
            else if (TryTake("out"))
                kind = MetacodeParameterKind.Out;
            else if (TryTake("in"))
                kind = MetacodeParameterKind.In;

            var name = Take() ?? "?";
            MetacodeTypeRef? type = null;
            if (TryTake(":"))
                type = Type();
            MetacodeExpression? @default = null;
            if (TryTake("="))
                @default = Expression(0);

            parameters.Add(new MetacodeParameter(name, type, @default, kind));
            if (!TryTake(","))
                break;
        }

        Expect(")");
        return parameters;
    }

    private MetacodeExpression List()
    {
        var items = new List<MetacodeExpression>();
        if (TryTake("]"))
            return new MetacodeCollection(MetacodeCollectionKind.Array, items);

        var value = Expression(0);
        if (Peek() == "FOR")
            return Comprehension(MetacodeComprehensionKind.List, value, null, "]");

        while (true)
        {
            if (TryTake(".."))
            {
                MetacodeExpression? end = null;
                if (Peek() is { } next && next is not "]" and not ",")
                    end = Expression(0);
                MetacodeExpression? step = null;
                if (TryTake(":"))
                    step = Expression(0);
                items.Add(new MetacodeRange(value, end, step));
            }
            else
            {
                items.Add(value);
            }

            if (!TryTake(","))
                break;
            if (AtEnd || Peek() == "]")
                break;

            value = Expression(0);
        }

        Expect("]");
        return items.Count == 1 && items[0] is MetacodeRange
            ? items[0]
            : new MetacodeCollection(MetacodeCollectionKind.Array, items);
    }

    private MetacodeExpression Braced()
    {
        var entries = new List<MetacodeDictionaryEntry>();
        var items = new List<MetacodeExpression>();
        var isDictionary = false;

        if (TryTake("}"))
            return new MetacodeCollection(MetacodeCollectionKind.Set, items);

        while (true)
        {
            var key = Expression(0);
            if (TryTake(":"))
            {
                var value = Expression(0);
                if (Peek() == "FOR")
                    return Comprehension(MetacodeComprehensionKind.Dictionary, key, value, "}");

                isDictionary = true;
                entries.Add(new MetacodeDictionaryEntry(key, value));
            }
            else if (Peek() == "FOR")
            {
                return Comprehension(MetacodeComprehensionKind.Set, key, null, "}");
            }
            else
            {
                items.Add(key);
            }

            if (!TryTake(","))
                break;
            if (AtEnd || Peek() == "}")
                break;
        }

        Expect("}");
        return isDictionary
            ? new MetacodeDictionary(entries)
            : new MetacodeCollection(MetacodeCollectionKind.Set, items);
    }

    private List<MetacodeArgument> Arguments(string close)
    {
        var arguments = new List<MetacodeArgument>();
        if (TryTake(close))
            return arguments;

        while (!AtEnd && Peek() != close)
        {
            var kind = MetacodeParameterKind.Value;
            if (TryTake("ref"))
                kind = MetacodeParameterKind.Ref;
            else if (TryTake("out"))
                kind = MetacodeParameterKind.Out;
            else if (TryTake("in"))
                kind = MetacodeParameterKind.In;

            string? name = null;
            if (Peek() is { } possible && IsIdentifier(possible) && PeekAt(1) == ":")
            {
                name = Take();
                Take();
            }

            arguments.Add(new MetacodeArgument(name, Expression(0), kind));
            if (!TryTake(","))
                break;
        }

        Expect(close);
        return arguments;
    }

    private List<MetacodeExpression> Indices(string close)
    {
        var indices = new List<MetacodeExpression>();
        if (TryTake(close))
            return indices;

        while (!AtEnd && Peek() != close)
        {
            var start = Expression(0);
            if (TryTake(".."))
            {
                MetacodeExpression? end = null;
                if (Peek() is { } next && next is not "]" and not ",")
                    end = Expression(0);
                MetacodeExpression? step = null;
                if (TryTake(":"))
                    step = Expression(0);
                indices.Add(new MetacodeRange(start, end, step));
            }
            else
            {
                indices.Add(start);
            }

            if (!TryTake(","))
                break;
        }

        Expect(close);
        return indices;
    }

    private MetacodeTypeRef Type()
    {
        var token = Take();
        if (token is null)
            return MetacodeTypeRef.Dynamic;

        var name = token;
        while (Peek() == "." && PeekAt(1) is { } segment && IsIdentifier(segment))
        {
            Take();
            name += "." + Take();
        }

        var arguments = new List<MetacodeTypeRef>();
        if (TryTake("<"))
        {
            while (!AtEnd && !TryCloseType())
            {
                arguments.Add(Type());
                if (!TryTake(","))
                {
                    TryCloseType();
                    break;
                }
            }
        }

        var isArray = false;
        while (Peek() == "[" && PeekAt(1) == "]")
        {
            Take();
            Take();
            isArray = true;
        }

        // A pointer level is one boolean in the model, so `char * *` reads as a pointer to char: the
        // extra stars are accepted (a decompiler writes them) and collapse to the level the model has.
        var isPointer = TryTake("*");
        while (TryTake("*"))
            isPointer = true;

        var isReference = TryTake("&");
        var isNullable = TryTake("?");
        return new MetacodeTypeRef(name, arguments.Count == 0 ? null : arguments, isArray, isNullable, isReference, isPointer);
    }

    private bool TryCloseType()
    {
        if (TryTake(">"))
            return true;

        // `List<List<int>>` tokenizes the two closers as one shift operator.
        if (Peek() == ">>")
        {
            _tokens[_index] = ">";
            return true;
        }

        return false;
    }

    private MetacodeExpression Interpolated(string token)
    {
        var content = token[2..^1];
        var parts = new List<MetacodeInterpolationPart>();
        var literal = new StringBuilder();

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                if (i + 1 < content.Length && content[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                if (literal.Length > 0)
                {
                    parts.Add(new MetacodeInterpolationPart(literal.ToString(), null));
                    literal.Clear();
                }

                var close = content.IndexOf('}', i);
                if (close < 0)
                    break;
                parts.Add(new MetacodeInterpolationPart(null, Parse(content[(i + 1)..close], _diagnostics)));
                i = close;
                continue;
            }

            if (content[i] == '}' && i + 1 < content.Length && content[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            if (content[i] == '\\' && i + 1 < content.Length)
            {
                literal.Append(content[++i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    var other => other,
                });
                continue;
            }

            literal.Append(content[i]);
        }

        if (literal.Length > 0)
            parts.Add(new MetacodeInterpolationPart(literal.ToString(), null));
        return new MetacodeInterpolatedString(parts);
    }

    private static string Unescape(string token)
    {
        var inner = token[1..^1];
        var text = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                text.Append(inner[++i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    var other => other,
                });
                continue;
            }

            text.Append(inner[i]);
        }

        return "\"" + text.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }

    private MetacodeExpression Opaque(string text)
    {
        Report("cannot read the expression fragment: " + text);
        return new MetacodeOpaqueExpression(MetacodeLanguage.Pseudocode, text, "unreadable");
    }

    /// <summary>
    /// Cuts every <c>=&gt; { … }</c> block out of the text, hands its body to
    /// <paramref name="parseBlock"/> and leaves one identifier in its place, which
    /// <see cref="Grouped"/> reads back as the lambda's body.
    /// </summary>
    private static string ExtractBlocks(
        string text,
        Func<string, IReadOnlyList<MetacodeStatement>> parseBlock,
        Dictionary<string, IReadOnlyList<MetacodeStatement>> blocks)
    {
        var output = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];

            if (c is '"' or '\'')
            {
                var start = index;
                index = SkipQuoted(text, index, c);
                output.Append(text, start, index - start);
                continue;
            }

            if (c == '=' && index + 1 < text.Length && text[index + 1] == '>')
            {
                var open = index + 2;
                while (open < text.Length && char.IsWhiteSpace(text[open]))
                    open++;

                if (open < text.Length && text[open] == '{')
                {
                    var close = MatchingBrace(text, open);
                    if (close > open)
                    {
                        var name = "_lambda_body_" + blocks.Count;
                        blocks[name] = parseBlock(text[(open + 1)..close]);
                        output.Append("=> ").Append(name);
                        index = close + 1;
                        continue;
                    }
                }
            }

            output.Append(c);
            index++;
        }

        return output.ToString();
    }

    /// <summary>The index just past the quote that closes the literal starting at <paramref name="start"/>.</summary>
    private static int SkipQuoted(string text, int start, char quote)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
                return index + 1;

            index++;
        }

        return text.Length;
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var index = open;
        while (index < text.Length)
        {
            var c = text[index];
            if (c is '"' or '\'')
            {
                index = SkipQuoted(text, index, c);
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return index;

            index++;
        }

        return -1;
    }

    private static bool IsTypeKeyword(string token) => token is "IS" or "AS" or "IN" or "AND" or "OR" or "NOT";

    private static bool IsIdentifier(string token) =>
        token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '@');

    private static bool IsNumber(string token) =>
        token.Length > 0 && (char.IsDigit(token[0]) || (token[0] == '.' && token.Length > 1 && char.IsDigit(token[1])));

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>(text.Length / 3);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '"' || (c == '$' && i + 1 < text.Length && text[i + 1] == '"')
                || (c == '\'' && i + 1 < text.Length && text.IndexOf('\'', i + 1) > i))
            {
                var start = i;
                var quote = c == '\'' ? '\'' : '"';
                if (c == '$')
                    i++;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(text[start..i]);
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i;
                if (c == '.')
                {
                    i++;
                    while (i < text.Length && char.IsDigit(text[i]))
                        i++;
                }
                else
                {
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_'))
                        i++;
                    if (i + 1 < text.Length && text[i] == '.' && text[i + 1] != '.' && char.IsDigit(text[i + 1]))
                    {
                        i++;
                        while (i < text.Length && char.IsDigit(text[i]))
                            i++;
                    }
                }

                // The suffix loop also swallows the digits of a hex literal (`0x1f`) and C's numeric
                // suffixes (`1U`, `1.5f`): a decompiler writes those, and splitting `0x100` into `0x`
                // and `100` would turn a valid number into a parse error.
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(text[start..i]);
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '@' or '$')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '$'))
                    i++;
                tokens.Add(text[start..i]);
                continue;
            }

            var matched = false;
            foreach (var op in MultiCharacterOperators)
            {
                if (i + op.Length > text.Length || string.CompareOrdinal(text, i, op, 0, op.Length) != 0)
                    continue;

                tokens.Add(op);
                i += op.Length;
                matched = true;
                break;
            }

            if (matched)
                continue;

            tokens.Add(c.ToString());
            i++;
        }

        return tokens;
    }

    private static readonly string[] MultiCharacterOperators =
    {
        "?.",
        "??",
        "=>",
        "**",
        "<<",
        ">>",
        "<=",
        ">=",
        "==",
        "!=",
        "&&",
        "||",
        "..",
        "++",
        "--",
    };
}
