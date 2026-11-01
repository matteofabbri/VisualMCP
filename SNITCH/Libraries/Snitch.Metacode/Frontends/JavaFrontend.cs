using System.Text;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads Java source and lowers it into the shared model. The producer this exists for is
/// <see cref="Decompilation.JadxService"/>: JADX decompiles a .class/.dex/.jar/.apk into .java files,
/// and this frontend is what turns those files into the same trees the solution's own code lives in.
/// <para>
/// It is a recursive-descent reader over the Java grammar, not a compiler: it resolves nothing and
/// type-checks nothing, exactly like the other frontends. A construct it has no node for — an
/// anonymous class body, a labeled <c>break</c>, a local class — is kept verbatim as
/// <see cref="MetacodeOpaqueStatement"/> / <see cref="MetacodeOpaqueExpression"/> with a diagnostic,
/// and the rest of the file still lowers, so one awkward statement costs that statement and not the
/// class.
/// </para>
/// </summary>
public sealed class JavaFrontend : ILanguageFrontend
{
    public string Name => "Java";

    public MetacodeLanguage Language => MetacodeLanguage.Java;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Java
        || (source.Language is null && source.Path is not null
            && source.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options) =>
        Translate(source.Text, source.Path ?? source.Name);

    /// <summary>The same translation for a caller that already has the text (the JADX service does).</summary>
    public MetacodeDocument Translate(string text, string document)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        var unit = new JavaParser(text, document, diagnostics).Parse();

        var stem = Path.GetFileNameWithoutExtension(document);
        var name = string.IsNullOrWhiteSpace(stem) ? document : stem;
        var module = new MetacodeModule(unit.Name, unit.Declarations);

        return new MetacodeDocument(name, MetacodeLanguage.Java, new[] { module }, diagnostics);
    }
}

/// <summary>One parsed compilation unit: the package it declares and the types in it.</summary>
internal sealed record JavaUnit(string Name, IReadOnlyList<MetacodeDeclaration> Declarations);

internal sealed class JavaParseException : Exception
{
    public JavaParseException(string message) : base(message)
    {
    }
}

internal enum JavaModifierContext
{
    Type,
    Member,
    Local,
    Parameter,
}

/// <summary>
/// The recursive-descent reader. Every statement and every member is parsed inside a recovery point:
/// a construct the reader cannot place becomes an opaque node carrying its own text, and parsing
/// continues at the next statement, so a decompiled file never loses everything because of one line.
/// </summary>
internal sealed class JavaParser
{
    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "protected", "private", "static", "abstract", "final", "native", "synchronized",
        "transient", "volatile", "strictfp", "default", "sealed",
    };

    private static readonly HashSet<string> PrimitiveWords = new(StringComparer.Ordinal)
    {
        "void", "boolean", "byte", "char", "short", "int", "long", "float", "double",
    };

    /// <summary>The words a member access may name after a dot (`Class.class`, `Outer.this`, `Foo.super`).</summary>
    private static readonly HashSet<string> MemberWords = new(StringComparer.Ordinal)
    {
        "class", "this", "super", "new",
    };

    private readonly string _text;
    private readonly string _document;
    private readonly List<JavaToken> _tokens;
    private readonly List<MetacodeDiagnostic> _diagnostics;
    private int _index;

    public JavaParser(string text, string document, List<MetacodeDiagnostic> diagnostics)
    {
        _text = text;
        _document = document;
        _diagnostics = diagnostics;
        _tokens = JavaLexer.Tokenize(text);
    }

    // ── compilation unit ──────────────────────────────────────────────────────────────

    public JavaUnit Parse()
    {
        string? packageName = null;
        var declarations = new List<MetacodeDeclaration>();

        try
        {
            if (Match("package"))
            {
                packageName = ParseQualifiedName();
                Expect(";");
            }

            while (!AtEnd)
            {
                if (Match(";")) continue;

                if (Is("import"))
                {
                    SkipImport();
                    continue;
                }

                var start = _index;
                var startToken = Current;
                try
                {
                    declarations.Add(ParseTypeDeclaration());
                }
                catch (JavaParseException exception)
                {
                    Error(MetacodeDiagnosticCodes.ParseError, exception.Message, startToken);
                    RecoverDeclaration();
                }

                if (_index == start) Advance();
            }
        }
        catch (JavaParseException exception)
        {
            Error(MetacodeDiagnosticCodes.ParseError, exception.Message, Current);
        }

        var name = string.IsNullOrWhiteSpace(packageName) ? DefaultModuleName() : packageName!;
        return new JavaUnit(name, declarations);
    }

    private void SkipImport()
    {
        Advance();                       // import
        if (Is("static")) Advance();
        ParseQualifiedName();
        Match("*");
        Expect(";");
    }

    private string DefaultModuleName()
    {
        var stem = Path.GetFileNameWithoutExtension(_document);
        return string.IsNullOrWhiteSpace(stem) ? "java" : stem;
    }

    // ── declarations ─────────────────────────────────────────────────────────────────

    private MetacodeDeclaration ParseTypeDeclaration()
    {
        var start = Current;
        var (modifiers, annotations) = ParseHeader(JavaModifierContext.Type);

        // An annotation type: @interface Foo { … }.
        if (Is("@") && Peek(1).Text == "interface")
        {
            Advance();
            Advance();
            return ParseTypeAfterKeyword(start, "interface", modifiers, annotations);
        }

        if (!Is("class") && !Is("interface") && !Is("enum") && !Is("record"))
            throw new JavaParseException($"expected a type declaration at {Current.Line}:{Current.Column}");

        var keyword = Current.Text;
        Advance();
        return ParseTypeAfterKeyword(start, keyword, modifiers, annotations);
    }

    private MetacodeTypeDeclaration ParseTypeAfterKeyword(
        JavaToken start, string keyword, MetacodeModifiers modifiers, List<string> annotations)
    {
        var nameToken = ExpectName();
        var name = nameToken.Text;
        var typeParameters = Is("<") ? ParseTypeParameters() : null;

        // A record's header is its component list: the components are the fields, and the canonical
        // constructor assigns them.
        var recordComponents = new List<MetacodeParameter>();
        if (keyword == "record" && Is("("))
        {
            Advance();
            while (!Is(")"))
            {
                recordComponents.Add(ParseParameter());
                if (!Match(",")) break;
            }

            Expect(")");
        }

        var baseTypes = new List<MetacodeTypeRef>();
        if (Match("extends"))
        {
            baseTypes.Add(ParseType());
            while (Match(",")) baseTypes.Add(ParseType());
        }

        if (Match("implements"))
        {
            baseTypes.Add(ParseType());
            while (Match(",")) baseTypes.Add(ParseType());
        }

        if (keyword == "record" && Match("permits"))
        {
            ParseType();
            while (Match(",")) ParseType();
        }

        var kind = keyword switch
        {
            "interface" => MetacodeTypeKind.Interface,
            "enum" => MetacodeTypeKind.Enum,
            "record" => MetacodeTypeKind.Record,
            _ => MetacodeTypeKind.Class,
        };

        var members = ParseTypeBody(name, kind, recordComponents);

        return new MetacodeTypeDeclaration(
            name,
            kind,
            members,
            modifiers,
            baseTypes.Count == 0 ? null : baseTypes,
            typeParameters,
            annotations.Count == 0 ? null : annotations)
        {
            Origin = Origin(start),
        };
    }

    private List<MetacodeDeclaration> ParseTypeBody(
        string typeName, MetacodeTypeKind kind, List<MetacodeParameter> recordComponents)
    {
        Expect("{");
        var members = new List<MetacodeDeclaration>();

        if (kind == MetacodeTypeKind.Enum)
            ParseEnumConstants(typeName, members);

        // A record's components are its state: a read-only field each, plus the canonical constructor.
        foreach (var component in recordComponents)
            members.Add(new MetacodeField(component.Name, component.Type, MetacodeModifiers.ReadOnly | MetacodeModifiers.Private));

        if (recordComponents.Count > 0)
        {
            var assignments = recordComponents
                .Select(component => (MetacodeStatement)new MetacodeAssignment(
                    new MetacodeMemberAccess(new MetacodeIdentifier("this"), component.Name),
                    MetacodeAssignmentOperator.Assign,
                    new MetacodeIdentifier(component.Name)))
                .ToList();

            members.Add(new MetacodeFunction(
                typeName,
                MetacodeFunctionKind.Constructor,
                recordComponents,
                Body: new MetacodeBlock(assignments)));
        }

        while (!AtEnd && !Is("}"))
        {
            var start = _index;
            var startToken = Current;
            try
            {
                ParseMember(typeName, members);
            }
            catch (JavaParseException exception)
            {
                Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    $"kept as opaque: {exception.Message}", startToken);
                members.Add(new MetacodeOpaqueDeclaration(MetacodeLanguage.Java, RecoverText(startToken), exception.Message)
                {
                    Origin = Origin(startToken),
                });
            }

            if (_index == start) Advance();
        }

        Expect("}");
        return members;
    }

    private void ParseEnumConstants(string enumName, List<MetacodeDeclaration> members)
    {
        while (!AtEnd && !Is("}") && !Is(";"))
        {
            var start = Current;
            SkipAnnotations();

            var nameToken = Current;
            if (nameToken.Kind != JavaTokenKind.Identifier) break;
            Advance();

            IReadOnlyList<MetacodeArgument>? arguments = null;
            if (Is("(")) arguments = ParseArguments();

            // A constant with a class body (`A { … }`) is an anonymous subclass; the constant stays
            // visible, the body is reported rather than silently dropped.
            if (Is("{"))
            {
                var bodyStart = Current;
                SkipBalancedBlock();
                Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    $"enum constant '{nameToken.Text}' has a class body, kept as opaque", bodyStart);
            }

            var value = arguments is null
                ? null
                : (MetacodeExpression)new MetacodeObjectCreation(MetacodeTypeRef.Of(enumName), arguments);

            members.Add(new MetacodeEnumMember(nameToken.Text, value) { Origin = Origin(start) });

            if (!Match(",")) break;
        }

        Match(";");
    }

    private void ParseMember(string ownerName, List<MetacodeDeclaration> members)
    {
        var start = Current;

        if (Match(";")) return;

        // Initializer blocks: `<clinit>` and `<init>`, which is exactly what the JVM calls them.
        if (Is("static") && Peek(1).Text == "{")
        {
            Advance();
            var body = ParseBlock();
            members.Add(new MetacodeFunction("<clinit>", MetacodeFunctionKind.Function,
                Modifiers: MetacodeModifiers.Static, Body: body) { Origin = Origin(start) });
            return;
        }

        if (Is("{"))
        {
            var body = ParseBlock();
            members.Add(new MetacodeFunction("<init>", MetacodeFunctionKind.Constructor, Body: body) { Origin = Origin(start) });
            return;
        }

        var (modifiers, annotations) = ParseHeader(JavaModifierContext.Member);

        if (Is("@") && Peek(1).Text == "interface")
        {
            Advance();
            Advance();
            members.Add(ParseTypeAfterKeyword(start, "interface", modifiers, annotations));
            return;
        }

        if (Is("class") || Is("interface") || Is("enum") || Is("record"))
        {
            var keyword = Current.Text;
            Advance();
            members.Add(ParseTypeAfterKeyword(start, keyword, modifiers, annotations));
            return;
        }

        var typeParameters = Is("<") ? ParseTypeParameters() : null;

        // Constructor: the type name followed directly by the parameter list.
        if (Current.Kind == JavaTokenKind.Identifier && Current.Text == ownerName && Peek(1).Text == "(")
        {
            members.Add(ParseConstructor(start, modifiers, annotations));
            return;
        }

        var type = ParseType();
        var nameToken = ExpectName();

        if (Is("("))
        {
            members.Add(ParseMethod(start, nameToken.Text, type, typeParameters, modifiers, annotations));
            return;
        }

        ParseFieldDeclarators(nameToken, type, modifiers, members);
    }

    private MetacodeFunction ParseConstructor(
        JavaToken start, MetacodeModifiers modifiers, List<string> annotations)
    {
        var name = Current.Text;
        Advance();
        var parameters = ParseParameters();
        SkipThrows();
        var body = Is("{") ? ParseBlock() : null;
        if (body is null) Expect(";");

        if (annotations.Contains("Override", StringComparer.Ordinal))
            modifiers |= MetacodeModifiers.Override;

        return new MetacodeFunction(
            name, MetacodeFunctionKind.Constructor, parameters, Body: body, Modifiers: modifiers,
            Decorators: annotations.Count == 0 ? null : annotations)
        {
            Origin = Origin(start),
        };
    }

    private MetacodeFunction ParseMethod(
        JavaToken start, string name, MetacodeTypeRef returnType,
        IReadOnlyList<MetacodeTypeParameter>? typeParameters,
        MetacodeModifiers modifiers, List<string> annotations)
    {
        var parameters = ParseParameters();
        SkipThrows();
        var body = Is("{") ? ParseBlock() : null;

        if (body is null)
        {
            Expect(";");
            // An interface or abstract method has no body; saying so is what makes the signature read
            // like the declaration it is.
            if (!modifiers.HasFlag(MetacodeModifiers.Extern)) modifiers |= MetacodeModifiers.Abstract;
        }

        if (annotations.Contains("Override", StringComparer.Ordinal))
            modifiers |= MetacodeModifiers.Override;

        return new MetacodeFunction(
            name, MetacodeFunctionKind.Method, parameters, returnType, body, modifiers, typeParameters,
            annotations.Count == 0 ? null : annotations)
        {
            Origin = Origin(start),
        };
    }

    private void ParseFieldDeclarators(
        JavaToken firstName, MetacodeTypeRef type, MetacodeModifiers modifiers, List<MetacodeDeclaration> members)
    {
        var name = firstName;
        while (true)
        {
            MetacodeExpression? initializer = null;
            if (Match("=")) initializer = ParseExpression();

            members.Add(new MetacodeField(name.Text, type, FieldModifiers(modifiers), initializer)
            {
                Origin = Origin(name),
            });

            if (!Match(",")) break;
            name = ExpectName();
        }

        Expect(";");
    }

    private static MetacodeModifiers FieldModifiers(MetacodeModifiers modifiers)
    {
        // `final` on a field is read-only; the member context turned it into Sealed (which is what it
        // means for a type or a method).
        if (modifiers.HasFlag(MetacodeModifiers.Sealed))
            modifiers = (modifiers & ~MetacodeModifiers.Sealed) | MetacodeModifiers.ReadOnly;

        return modifiers;
    }

    // ── types and parameters ─────────────────────────────────────────────────────────

    private IReadOnlyList<MetacodeParameter> ParseParameters()
    {
        Expect("(");
        var parameters = new List<MetacodeParameter>();
        while (!Is(")"))
        {
            parameters.Add(ParseParameter());
            if (!Match(",")) break;
        }

        Expect(")");
        return parameters;
    }

    private MetacodeParameter ParseParameter()
    {
        ParseHeader(JavaModifierContext.Parameter);
        var type = ParseType();

        // C-style arrays on the parameter name (`String args[]`) still happen in decompiled code.
        var nameToken = ExpectName();
        while (Is("[") && Peek(1).Text == "]")
        {
            Advance();
            Advance();
            type = type with { IsArray = true };
        }

        return new MetacodeParameter(nameToken.Text, type) { Origin = Origin(nameToken) };
    }

    private void SkipThrows()
    {
        if (!Match("throws")) return;

        ParseType();
        while (Match(",")) ParseType();
    }

    private IReadOnlyList<MetacodeTypeParameter> ParseTypeParameters()
    {
        Expect("<");
        var parameters = new List<MetacodeTypeParameter>();

        while (!Is(">"))
        {
            SkipAnnotations();
            var name = ExpectName().Text;
            List<string>? constraints = null;

            if (Match("extends"))
            {
                constraints = new List<string> { ParseType().Render() };
                while (Match("&")) constraints.Add(ParseType().Render());
            }

            parameters.Add(new MetacodeTypeParameter(name, constraints));
            if (!Match(",")) break;
        }

        Expect(">");
        return parameters;
    }

    private MetacodeTypeRef ParseType(bool allowArrays = true)
    {
        SkipAnnotations();

        var name = IsPrimitive(Current.Text) ? Take().Text : ParseQualifiedName();
        IReadOnlyList<MetacodeTypeRef>? arguments = Is("<") ? ParseTypeArguments() : null;

        var type = new MetacodeTypeRef(name, arguments);

        if (!allowArrays) return type;

        while (Is("[") && Peek(1).Text == "]")
        {
            Advance();
            Advance();
            type = type with { IsArray = true };
        }

        return type;
    }

    private IReadOnlyList<MetacodeTypeRef> ParseTypeArguments()
    {
        Expect("<");
        var arguments = new List<MetacodeTypeRef>();

        while (!Is(">"))
        {
            if (Is("?"))
            {
                Advance();
                var wildcard = "?";
                if (Is("extends") || Is("super"))
                {
                    var bound = Current.Text;
                    Advance();
                    wildcard += " " + bound + " " + ParseType().Render();
                }

                arguments.Add(MetacodeTypeRef.Of(wildcard));
            }
            else
            {
                arguments.Add(ParseType());
            }

            if (!Match(",")) break;
        }

        Expect(">");
        return arguments;
    }

    private string ParseQualifiedName()
    {
        var nameToken = ExpectName();
        var builder = new StringBuilder(nameToken.Text);

        while (Is(".") && Peek(1).Kind == JavaTokenKind.Identifier)
        {
            Advance();
            builder.Append('.').Append(Current.Text);
            Advance();
        }

        return builder.ToString();
    }

    // ── statements ──────────────────────────────────────────────────────────────────

    private MetacodeBlock ParseBlock()
    {
        Expect("{");
        var statements = new List<MetacodeStatement>();

        while (!AtEnd && !Is("}"))
        {
            var start = _index;
            var startToken = Current;
            try
            {
                var statement = ParseStatement();
                if (statement is not null) statements.Add(statement);
            }
            catch (JavaParseException exception)
            {
                Warn(MetacodeDiagnosticCodes.UnsupportedStatement,
                    $"kept as opaque: {exception.Message}", startToken);
                statements.Add(new MetacodeOpaqueStatement(MetacodeLanguage.Java, RecoverText(startToken), exception.Message)
                {
                    Origin = Origin(startToken),
                });
            }

            if (_index == start) Advance();
        }

        Expect("}");
        return new MetacodeBlock(statements);
    }

    private MetacodeBlock ParseBody()
    {
        if (Is("{")) return ParseBlock();

        var statement = ParseStatement();
        return statement is null ? MetacodeBlock.Empty : MetacodeBlock.Of(statement);
    }

    private MetacodeStatement? ParseStatement()
    {
        var start = Current;

        if (Is("{")) return ParseBlock();
        if (Match(";")) return null;

        if (Is("if")) { Advance(); return ParseIf(start); }
        if (Is("while")) { Advance(); return ParseWhile(start); }
        if (Is("do")) { Advance(); return ParseDoWhile(start); }
        if (Is("for")) { Advance(); return ParseFor(start); }
        if (Is("switch")) { Advance(); return ParseSwitch(start); }
        if (Is("try")) { Advance(); return ParseTry(start); }
        if (Is("synchronized")) { Advance(); return ParseSynchronized(start); }

        if (Is("return"))
        {
            Advance();
            var value = Is(";") ? null : ParseExpression();
            Expect(";");
            return new MetacodeReturn(value) { Origin = Origin(start) };
        }

        if (Is("throw"))
        {
            Advance();
            var value = ParseExpression();
            Expect(";");
            return new MetacodeThrow(value) { Origin = Origin(start) };
        }

        if (Is("yield"))
        {
            Advance();
            var value = Is(";") ? null : ParseExpression();
            Expect(";");
            return new MetacodeYield(value) { Origin = Origin(start) };
        }

        if (Is("break"))
        {
            Advance();
            if (!Is(";"))
            {
                // `break label;` — the model has no labeled break, and a GOTO would say something false.
                var label = Current.Text;
                Advance();
                Expect(";");
                throw new JavaParseException($"labeled 'break {label}' is not modelled");
            }

            Expect(";");
            return new MetacodeBreak { Origin = Origin(start) };
        }

        if (Is("continue"))
        {
            Advance();
            if (!Is(";"))
            {
                var label = Current.Text;
                Advance();
                Expect(";");
                throw new JavaParseException($"labeled 'continue {label}' is not modelled");
            }

            Expect(";");
            return new MetacodeContinue { Origin = Origin(start) };
        }

        if (Is("assert")) throw new JavaParseException("assert statements are not modelled");

        // A label wrapping the statement it names: `outer: while (…)`.
        if (Current.Kind == JavaTokenKind.Identifier && Peek(1).Text == ":")
        {
            var label = Current.Text;
            Advance();
            Advance();
            var inner = ParseStatement();
            return inner is null ? null : new MetacodeLabel(label, inner) { Origin = Origin(start) };
        }

        if (IsTypeDeclarationStart())
            throw new JavaParseException("local class declarations are not modelled");

        if (LooksLikeLocalDeclaration()) return ParseLocalDeclaration();

        var expression = ParseExpression();
        Expect(";");

        return expression is MetacodeAssignmentExpression assignment
            ? new MetacodeAssignment(assignment.Target, assignment.Operator, assignment.Value) { Origin = Origin(start) }
            : new MetacodeExpressionStatement(expression) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseIf(JavaToken start)
    {
        Expect("(");
        var condition = ParseExpression();
        Expect(")");

        var then = ParseBody();
        MetacodeBlock? @else = null;

        if (Match("else"))
            @else = ParseBody();

        return new MetacodeIf(condition, then, @else) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseWhile(JavaToken start)
    {
        Expect("(");
        var condition = ParseExpression();
        Expect(")");
        return new MetacodeWhile(condition, ParseBody()) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseDoWhile(JavaToken start)
    {
        var body = ParseBody();
        Expect("while");
        Expect("(");
        var condition = ParseExpression();
        Expect(")");
        Expect(";");
        return new MetacodeDoWhile(body, condition) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseFor(JavaToken start)
    {
        Expect("(");

        // The enhanced form: `for (Type name : iterable)`.
        if (LooksLikeLocalDeclaration())
        {
            var save = _index;
            ParseHeader(JavaModifierContext.Local);
            var type = ParseType();
            var nameToken = ExpectName();

            if (Is(":"))
            {
                Advance();
                var iterable = ParseExpression();
                Expect(")");
                return new MetacodeForEach(nameToken.Text, type, iterable, ParseBody()) { Origin = Origin(start) };
            }

            _index = save;
        }

        MetacodeStatement? initializer = null;
        if (Is(";"))
        {
            Advance();
        }
        else if (LooksLikeLocalDeclaration())
        {
            initializer = ParseLocalDeclaration();
        }
        else
        {
            initializer = new MetacodeExpressionStatement(ParseExpression());
            Expect(";");
        }

        MetacodeExpression? condition = null;
        if (!Is(";")) condition = ParseExpression();
        Expect(";");

        MetacodeStatement? increment = null;
        if (!Is(")")) increment = new MetacodeExpressionStatement(ParseExpression());
        Expect(")");

        return new MetacodeFor(initializer, condition, increment, ParseBody()) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseSwitch(JavaToken start)
    {
        Expect("(");
        var subject = ParseExpression();
        Expect(")");
        Expect("{");

        var cases = new List<MetacodeSwitchCase>();

        while (!AtEnd && !Is("}"))
        {
            var labels = new List<MetacodeExpression>();
            var isDefault = false;

            if (Match("default"))
            {
                isDefault = true;
            }
            else
            {
                Expect("case");
                do
                {
                    labels.Add(ParseExpression());
                }
                while (Match(","));
            }

            if (Match("->"))
            {
                if (Is("{"))
                {
                    cases.Add(new MetacodeSwitchCase(labels, ParseBlock(), isDefault));
                }
                else
                {
                    var value = ParseExpression();
                    Expect(";");
                    cases.Add(new MetacodeSwitchCase(labels,
                        new MetacodeBlock(new MetacodeStatement[] { new MetacodeExpressionStatement(value) }), isDefault));
                }

                continue;
            }

            Expect(":");

            var statements = new List<MetacodeStatement>();
            while (!AtEnd && !Is("}") && !Is("case") && !Is("default"))
            {
                var statement = ParseStatement();
                if (statement is not null) statements.Add(statement);
            }

            cases.Add(new MetacodeSwitchCase(labels, new MetacodeBlock(statements), isDefault));
        }

        Expect("}");
        return new MetacodeSwitch(subject, cases) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseTry(JavaToken start)
    {
        var resources = new List<MetacodeLocalVariable>();

        // try-with-resources: each resource is closed when the block ends, which is a Using.
        if (Is("("))
        {
            Advance();
            while (!Is(")"))
            {
                ParseHeader(JavaModifierContext.Local);
                var type = ParseType();
                var name = ExpectName().Text;
                Expect("=");
                var initializer = ParseExpression();
                resources.Add(new MetacodeLocalVariable(name, type, initializer));

                if (!Match(";")) break;
            }

            Expect(")");
        }

        MetacodeStatement body = ParseBlock();
        var catches = new List<MetacodeCatch>();

        while (Is("catch"))
        {
            Advance();
            Expect("(");

            var exceptionType = ParseType();
            while (Match("|")) exceptionType = MetacodeTypeRef.Of(exceptionType.Render() + " | " + ParseType().Render());

            var variable = ExpectName().Text;
            Expect(")");

            catches.Add(new MetacodeCatch(exceptionType, variable, ParseBlock()));
        }

        var @finally = Is("finally") ? ParseFinally() : null;

        for (var index = resources.Count - 1; index >= 0; index--)
            body = new MetacodeUsing(resources[index], null, new MetacodeBlock(new[] { body }));

        return new MetacodeTry(new MetacodeBlock(new[] { body }), catches, @finally) { Origin = Origin(start) };
    }

    private MetacodeBlock ParseFinally()
    {
        Expect("finally");
        return ParseBlock();
    }

    private MetacodeStatement ParseSynchronized(JavaToken start)
    {
        Expect("(");
        var target = ParseExpression();
        Expect(")");
        return new MetacodeLock(target, ParseBody()) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseLocalDeclaration()
    {
        var start = Current;
        var (modifiers, _) = ParseHeader(JavaModifierContext.Local);
        var type = ParseType();

        var variables = new List<MetacodeStatement>();
        while (true)
        {
            var name = ExpectName().Text;
            MetacodeExpression? initializer = null;
            if (Match("=")) initializer = ParseExpression();

            variables.Add(new MetacodeLocalVariable(name, type, initializer, modifiers.HasFlag(MetacodeModifiers.ReadOnly))
            {
                Origin = Origin(start),
            });

            if (!Match(",")) break;
        }

        Expect(";");
        return variables.Count == 1 ? variables[0] : new MetacodeBlock(variables);
    }

    // ── expressions ────────────────────────────────────────────────────────────────

    private MetacodeExpression ParseExpression() => ParseAssignment();

    private MetacodeExpression ParseAssignment()
    {
        var left = ParseConditional();

        if (TryReadAssignment(out var @operator))
        {
            var value = ParseAssignment();
            return new MetacodeAssignmentExpression(left, @operator, value);
        }

        return left;
    }

    private MetacodeExpression ParseConditional()
    {
        var condition = ParseBinary(0);
        if (!Match("?")) return condition;

        var whenTrue = ParseExpression();
        Expect(":");
        var whenFalse = ParseConditional();
        return new MetacodeConditional(condition, whenTrue, whenFalse);
    }

    private MetacodeExpression ParseBinary(int level)
    {
        if (level > 9) return ParseUnary();

        var left = ParseBinary(level + 1);

        while (true)
        {
            if (level == 6 && Is("instanceof"))
            {
                Advance();
                var type = ParseType();
                string? binding = null;

                // Java 16 pattern: `x instanceof Foo foo`.
                if (Current.Kind == JavaTokenKind.Identifier && TextAt(_index + 1) is ")" or ";" or "," or "&&" or "||" or "?" or ":")
                {
                    binding = Current.Text;
                    Advance();
                }

                left = new MetacodeTypeCheck(left, type, MetacodeTypeCheckKind.Is, binding);
                continue;
            }

            if (!TryReadBinary(level, out var @operator)) break;

            var right = ParseBinary(level + 1);
            left = new MetacodeBinary(@operator, left, right);
        }

        return left;
    }

    private bool TryReadBinary(int level, out MetacodeBinaryOperator @operator)
    {
        @operator = MetacodeBinaryOperator.Add;

        switch (level)
        {
            case 0 when Match("||"): @operator = MetacodeBinaryOperator.LogicalOr; return true;
            case 1 when Match("&&"): @operator = MetacodeBinaryOperator.LogicalAnd; return true;
            case 2 when Match("|"): @operator = MetacodeBinaryOperator.BitwiseOr; return true;
            case 3 when Match("^"): @operator = MetacodeBinaryOperator.BitwiseXor; return true;
            case 4 when Match("&"): @operator = MetacodeBinaryOperator.BitwiseAnd; return true;
            case 5 when Match("=="): @operator = MetacodeBinaryOperator.Equal; return true;
            case 5 when Match("!="): @operator = MetacodeBinaryOperator.NotEqual; return true;
            case 6:
                if (!TryConsumeAngleOperator(shiftOnly: false, out var comparison)) return false;
                @operator = comparison switch
                {
                    "<" => MetacodeBinaryOperator.LessThan,
                    "<=" => MetacodeBinaryOperator.LessThanOrEqual,
                    ">" => MetacodeBinaryOperator.GreaterThan,
                    _ => MetacodeBinaryOperator.GreaterThanOrEqual,
                };
                return true;
            case 7:
                if (!TryConsumeAngleOperator(shiftOnly: true, out var shift) || shift is not ("<<" or ">>" or ">>>"))
                    return false;
                @operator = shift == "<<" ? MetacodeBinaryOperator.ShiftLeft : MetacodeBinaryOperator.ShiftRight;
                return true;
            case 8 when Match("+"): @operator = MetacodeBinaryOperator.Add; return true;
            case 8 when Match("-"): @operator = MetacodeBinaryOperator.Subtract; return true;
            case 9 when Match("*"): @operator = MetacodeBinaryOperator.Multiply; return true;
            case 9 when Match("/"): @operator = MetacodeBinaryOperator.Divide; return true;
            case 9 when Match("%"): @operator = MetacodeBinaryOperator.Modulo; return true;
            default: return false;
        }
    }

    private MetacodeExpression ParseUnary()
    {
        if (Is("!") || Is("~") || Is("-") || Is("+") || Is("++") || Is("--"))
        {
            var text = Current.Text;
            Advance();

            var @operator = text switch
            {
                "!" => MetacodeUnaryOperator.Not,
                "~" => MetacodeUnaryOperator.BitwiseNot,
                "-" => MetacodeUnaryOperator.Negate,
                "+" => MetacodeUnaryOperator.Plus,
                "++" => MetacodeUnaryOperator.Increment,
                _ => MetacodeUnaryOperator.Decrement,
            };

            return new MetacodeUnary(@operator, ParseUnary());
        }

        return ParsePostfix();
    }

    private MetacodeExpression ParsePostfix()
    {
        var expression = ParsePrimary();

        while (true)
        {
            if (Is("."))
            {
                Advance();

                // An explicit type argument on a call: `obj.<String>map(…)`.
                if (Is("<")) SkipBalancedAngle();

                var member = ExpectMemberName();

                if (Is("<")) SkipBalancedAngle();

                expression = Is("(")
                    ? new MetacodeInvocation(new MetacodeMemberAccess(expression, member), ParseArguments())
                    : new MetacodeMemberAccess(expression, member);
                continue;
            }

            if (Is("["))
            {
                Advance();
                var indices = new List<MetacodeExpression>();
                while (!Is("]"))
                {
                    indices.Add(ParseExpression());
                    if (!Match(",")) break;
                }

                Expect("]");
                expression = new MetacodeIndex(expression, indices);
                continue;
            }

            if (Is("("))
            {
                expression = new MetacodeInvocation(expression, ParseArguments());
                continue;
            }

            if (Is("++") || Is("--"))
            {
                var @operator = Current.Text == "++" ? MetacodeUnaryOperator.Increment : MetacodeUnaryOperator.Decrement;
                Advance();
                expression = new MetacodeUnary(@operator, expression, IsPostfix: true);
                continue;
            }

            if (Is("::"))
            {
                Advance();
                if (Is("<")) SkipBalancedAngle();
                var target = Is("new") ? "new" : ExpectMemberName();
                Warn(MetacodeDiagnosticCodes.UnsupportedExpression,
                    $"kept as opaque: method reference '::{target}'", Current);
                expression = new MetacodeOpaqueExpression(MetacodeLanguage.Java, "::" + target, "method reference");
                continue;
            }

            break;
        }

        return expression;
    }

    private MetacodeExpression ParsePrimary()
    {
        var start = Current;

        if (start.Kind == JavaTokenKind.Number) { Advance(); return MetacodeLiteral.Number(start.Text); }
        if (start.Kind == JavaTokenKind.String) { Advance(); return MetacodeLiteral.String(start.Text); }
        if (start.Kind == JavaTokenKind.Character)
        {
            Advance();
            return new MetacodeLiteral(MetacodeLiteralKind.Character, start.Text);
        }

        if (start.Text == "true") { Advance(); return MetacodeLiteral.True; }
        if (start.Text == "false") { Advance(); return MetacodeLiteral.False; }
        if (start.Text == "null") { Advance(); return MetacodeLiteral.Null; }

        if (start.Text == "this" || start.Text == "super")
        {
            Advance();
            return new MetacodeIdentifier(start.Text);
        }

        if (start.Text == "new")
        {
            Advance();
            return ParseNew(start);
        }

        // An array initializer with no `new`: `int[] xs = { 1, 2 };`.
        if (start.Text == "{")
            return new MetacodeNewArray(null, Elements: ParseArrayInitializer());

        if (start.Text == "(")
            return ParseParenthesizedOrLambdaOrCast();

        if (start.Text == "switch")
            throw new JavaParseException("switch expressions are not modelled");

        if (start.Kind == JavaTokenKind.Identifier)
        {
            // Single-parameter lambda: `x -> …`.
            if (Peek(1).Text == "->")
            {
                Advance();
                Advance();
                return ParseLambdaBody(new[] { new MetacodeParameter(start.Text) });
            }

            Advance();
            return new MetacodeIdentifier(start.Text);
        }

        throw new JavaParseException($"unexpected '{start.Text}' in an expression at {start.Line}:{start.Column}");
    }

    private MetacodeExpression ParseParenthesizedOrLambdaOrCast()
    {
        var close = MatchingParenthesis(_index);
        if (close > 0 && TextAt(close + 1) == "->")
            return ParseLambda(close);

        if (IsCastShape())
        {
            Advance();
            var type = ParseType();
            Expect(")");
            return new MetacodeCast(type, ParseUnary());
        }

        Advance();
        var expression = ParseExpression();
        Expect(")");
        return expression;
    }

    private MetacodeExpression ParseLambda(int closeIndex)
    {
        Advance();                       // (
        var parameters = new List<MetacodeParameter>();

        while (_index < closeIndex && !Is(")"))
        {
            ParseHeader(JavaModifierContext.Parameter);

            if (TryScanType(_index, out var afterType) && _tokens[afterType].Kind == JavaTokenKind.Identifier)
            {
                var type = ParseType();
                parameters.Add(new MetacodeParameter(ExpectName().Text, type));
            }
            else
            {
                parameters.Add(new MetacodeParameter(ExpectName().Text));
            }

            if (!Match(",")) break;
        }

        Expect(")");
        Expect("->");
        return ParseLambdaBody(parameters);
    }

    private MetacodeExpression ParseLambdaBody(IReadOnlyList<MetacodeParameter> parameters) =>
        Is("{")
            ? new MetacodeLambda(parameters, ParseBlock())
            : new MetacodeLambda(parameters, Expression: ParseExpression());

    private MetacodeExpression ParseNew(JavaToken start)
    {
        var type = ParseType(allowArrays: false);

        if (Is("["))
        {
            var sizes = new List<MetacodeExpression>();
            while (Is("["))
            {
                Advance();
                if (Is("]"))
                {
                    Advance();           // an unspecified dimension: `new int[5][]`
                    continue;
                }

                sizes.Add(ParseExpression());
                Expect("]");
            }

            var elements = Is("{") ? ParseArrayInitializer() : null;
            return new MetacodeNewArray(type, sizes, elements);
        }

        if (Is("{"))
            return new MetacodeNewArray(type, Elements: ParseArrayInitializer());

        var arguments = ParseArguments();

        if (Is("{"))
        {
            SkipBalancedBlock();
            var text = Slice(start, Previous);
            Warn(MetacodeDiagnosticCodes.UnsupportedExpression,
                "kept as opaque: anonymous class body", start);
            return new MetacodeOpaqueExpression(MetacodeLanguage.Java, text, "anonymous class body");
        }

        return new MetacodeObjectCreation(type, arguments);
    }

    private IReadOnlyList<MetacodeExpression> ParseArrayInitializer()
    {
        Expect("{");
        var elements = new List<MetacodeExpression>();

        while (!AtEnd && !Is("}"))
        {
            elements.Add(Is("{")
                ? new MetacodeNewArray(null, Elements: ParseArrayInitializer())
                : ParseExpression());

            if (!Match(",")) break;
        }

        Expect("}");
        return elements;
    }

    private IReadOnlyList<MetacodeArgument> ParseArguments()
    {
        Expect("(");
        var arguments = new List<MetacodeArgument>();

        while (!Is(")"))
        {
            arguments.Add(MetacodeArgument.Of(ParseExpression()));
            if (!Match(",")) break;
        }

        Expect(")");
        return arguments;
    }

    // ── lookahead and recovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether the tokens at the cursor are `[final] Type name …` — the shape of a local declaration.
    /// It is a scan, not a parse: nothing is consumed and nothing throws.
    /// </summary>
    private bool LooksLikeLocalDeclaration()
    {
        var save = _index;
        try
        {
            while (Is("@") && Peek(1).Kind == JavaTokenKind.Identifier) SkipAnnotation();
            if (Is("final")) Advance();

            if (!TryScanType(_index, out var afterType)) return false;
            if (_tokens[afterType].Kind != JavaTokenKind.Identifier) return false;

            var afterName = afterType + 1;
            var next = TextAt(afterName);
            return next is "=" or ";" or "," or ":" || (next == "[" && TextAt(afterName + 1) == "]");
        }
        finally
        {
            _index = save;
        }
    }

    /// <summary>A type-shaped run of tokens starting at <paramref name="position"/>, without consuming.</summary>
    private bool TryScanType(int position, out int afterType)
    {
        afterType = position;

        if (afterType >= _tokens.Count) return false;

        var token = _tokens[afterType];
        if (IsPrimitive(token.Text))
        {
            afterType++;
        }
        else if (token.Kind == JavaTokenKind.Identifier)
        {
            afterType++;
            while (TextAt(afterType) == "." && TokenAt(afterType + 1).Kind == JavaTokenKind.Identifier)
                afterType += 2;
        }
        else
        {
            return false;
        }

        if (TextAt(afterType) == "<")
        {
            var depth = 0;
            while (afterType < _tokens.Count)
            {
                var text = TextAt(afterType);
                if (text == "<") depth++;
                else if (text == ">" && --depth == 0) { afterType++; break; }
                else if (text == ";") return false;

                afterType++;
            }

            if (depth != 0) return false;
        }

        while (TextAt(afterType) == "[" && TextAt(afterType + 1) == "]")
            afterType += 2;

        return true;
    }

    private bool IsTypeDeclarationStart()
    {
        var save = _index;
        try
        {
            ParseHeader(JavaModifierContext.Member);
            return Is("class") || Is("interface") || Is("enum") || Is("record");
        }
        catch (JavaParseException)
        {
            return false;
        }
        finally
        {
            _index = save;
        }
    }

    /// <summary>
    /// `(Type) value` and not `(value)` followed by an operator: the content must look like a type and
    /// the token after the `)` must start an operand. A primitive makes it unambiguous — `int` is never
    /// a variable name.
    /// </summary>
    private bool IsCastShape()
    {
        var close = MatchingParenthesis(_index);
        if (close < 0 || close == _index + 1) return false;

        var contentStart = _index + 1;
        if (IsPrimitive(TokenAt(contentStart).Text)) return true;

        if (!TryScanType(contentStart, out var afterType) || afterType != close) return false;

        var next = TokenAt(close + 1);
        return next.Kind is JavaTokenKind.Identifier or JavaTokenKind.Number or JavaTokenKind.String or JavaTokenKind.Character
               || next.Text is "(" or "new" or "!" or "~" or "this" or "super";
    }

    private int MatchingParenthesis(int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < _tokens.Count; index++)
        {
            var text = _tokens[index].Text;
            if (text == "(") depth++;
            else if (text == ")" && --depth == 0) return index;
        }

        return -1;
    }

    /// <summary>
    /// Reads `&lt;`- and `&gt;`-prefixed operators from single-character tokens: Java's `>>` in a type
    /// argument list closes two generics, so the lexer cannot merge them and the parser does.
    /// </summary>
    private bool TryConsumeAngleOperator(bool shiftOnly, out string @operator)
    {
        @operator = string.Empty;
        if (!Is("<") && !Is(">")) return false;

        var save = _index;
        var character = Current.Text[0];

        Advance();                       // first character

        if (Is(character.ToString()) && AdjacentToPrevious())
        {
            Advance();                   // second character

            var triple = false;
            if (character == '>' && Is(">") && AdjacentToPrevious())
            {
                Advance();
                triple = true;
            }

            if (!shiftOnly)
            {
                _index = save;
                return false;
            }

            // `>>=` and `<<=` are assignments, not shifts: leave them for TryReadAssignment rather
            // than consuming the run and then choking on the `=`.
            if (Is("=") && AdjacentToPrevious())
            {
                _index = save;
                return false;
            }

            @operator = new string(character, triple ? 3 : 2);
            return true;
        }

        if (!shiftOnly && Is("=") && AdjacentToPrevious())
        {
            Advance();
            @operator = character.ToString() + "=";
            return true;
        }

        if (shiftOnly)
        {
            _index = save;
            return false;
        }

        @operator = character.ToString();
        return true;
    }

    private bool TryReadAssignment(out MetacodeAssignmentOperator @operator)
    {
        @operator = MetacodeAssignmentOperator.Assign;

        if (Match("=")) return true;
        if (Match("+=")) { @operator = MetacodeAssignmentOperator.Add; return true; }
        if (Match("-=")) { @operator = MetacodeAssignmentOperator.Subtract; return true; }
        if (Match("*=")) { @operator = MetacodeAssignmentOperator.Multiply; return true; }
        if (Match("/=")) { @operator = MetacodeAssignmentOperator.Divide; return true; }
        if (Match("%=")) { @operator = MetacodeAssignmentOperator.Modulo; return true; }
        if (Match("&=")) { @operator = MetacodeAssignmentOperator.BitwiseAnd; return true; }
        if (Match("|=")) { @operator = MetacodeAssignmentOperator.BitwiseOr; return true; }
        if (Match("^=")) { @operator = MetacodeAssignmentOperator.BitwiseXor; return true; }

        // <<=, >>= and >>>= are spelled with single-character tokens for the same reason as the shifts.
        if (!Is("<") && !Is(">")) return false;

        var save = _index;
        var character = Current.Text[0];
        Advance();

        if (Is(character.ToString()) && AdjacentToPrevious())
        {
            Advance();
            if (character == '>' && Is(">") && AdjacentToPrevious()) Advance();
            if (Match("="))
            {
                @operator = character == '<' ? MetacodeAssignmentOperator.ShiftLeft : MetacodeAssignmentOperator.ShiftRight;
                return true;
            }
        }

        _index = save;
        return false;
    }

    private bool AdjacentToPrevious()
    {
        if (_index == 0) return false;

        var previous = _tokens[_index - 1];
        return Current.Offset == previous.Offset + previous.Text.Length;
    }

    private void SkipAnnotation()
    {
        Expect("@");
        ParseQualifiedName();
        if (Is("(")) SkipBalanced('(', ')');
    }

    private void SkipAnnotations()
    {
        while (Is("@") && Peek(1).Kind == JavaTokenKind.Identifier)
            SkipAnnotation();
    }

    private string ParseAnnotation()
    {
        Expect("@");
        var builder = new StringBuilder(ParseQualifiedName());

        if (Is("("))
        {
            var start = _index;
            SkipBalanced('(', ')');
            builder.Append(Slice(_tokens[start], Previous));
        }

        return builder.ToString();
    }

    private (MetacodeModifiers Modifiers, List<string> Annotations) ParseHeader(JavaModifierContext context)
    {
        var modifiers = MetacodeModifiers.None;
        var annotations = new List<string>();

        while (!AtEnd)
        {
            if (Is("@") && Peek(1).Kind == JavaTokenKind.Identifier)
            {
                annotations.Add(ParseAnnotation());
                continue;
            }

            var word = Current.Text;
            if (!ModifierWords.Contains(word)) break;

            modifiers |= MapModifier(word, context);
            Advance();
        }

        return (modifiers, annotations);
    }

    private static MetacodeModifiers MapModifier(string word, JavaModifierContext context) => word switch
    {
        "public" => MetacodeModifiers.Public,
        "protected" => MetacodeModifiers.Protected,
        "private" => MetacodeModifiers.Private,
        "static" => MetacodeModifiers.Static,
        "abstract" => MetacodeModifiers.Abstract,
        "final" => context is JavaModifierContext.Local or JavaModifierContext.Parameter
            ? MetacodeModifiers.ReadOnly
            : MetacodeModifiers.Sealed,
        "native" => MetacodeModifiers.Extern,
        "default" => MetacodeModifiers.Virtual,
        "sealed" => MetacodeModifiers.Sealed,
        _ => MetacodeModifiers.None,
    };

    private void SkipBalancedBlock() => SkipBalanced('{', '}');

    private void SkipBalancedAngle() => SkipBalanced('<', '>');

    private void SkipBalanced(char open, char close)
    {
        if (!Is(open.ToString()))
            return;

        var depth = 0;
        while (!AtEnd)
        {
            var text = Current.Text;
            if (text.Length == 1 && text[0] == open)
            {
                depth++;
            }
            else if (text.Length == 1 && text[0] == close && --depth == 0)
            {
                Advance();
                return;
            }

            Advance();
        }
    }

    /// <summary>
    /// Where a construct the reader could not place ends: the closing `;` of the statement, or the
    /// closing `}` of its own block (leaving the enclosing block's brace alone).
    /// </summary>
    private string RecoverText(JavaToken start)
    {
        var depth = 0;
        while (!AtEnd)
        {
            var text = Current.Text;

            if (text == "{")
            {
                depth++;
                Advance();
                continue;
            }

            if (text == "}")
            {
                if (depth == 0) break;

                depth--;
                Advance();
                if (depth == 0) break;
                continue;
            }

            if (text == ";" && depth == 0)
            {
                Advance();
                break;
            }

            Advance();
        }

        return Slice(start, Previous);
    }

    private void RecoverDeclaration()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var text = Current.Text;

            if (text == "{")
            {
                depth++;
            }
            else if (text == "}")
            {
                if (depth == 0)
                {
                    Advance();
                    return;
                }

                if (--depth == 0)
                {
                    Advance();
                    return;
                }
            }
            else if (text == ";" && depth == 0)
            {
                Advance();
                return;
            }

            Advance();
        }
    }

    // ── tokens ──────────────────────────────────────────────────────────────────────

    private bool AtEnd => Current.Kind == JavaTokenKind.End;

    private JavaToken Current => TokenAt(_index);

    private JavaToken Previous => TokenAt(_index - 1);

    private JavaToken Peek(int offset) => TokenAt(_index + offset);

    private JavaToken TokenAt(int index) => _tokens[Math.Clamp(index, 0, _tokens.Count - 1)];

    private string TextAt(int index) => TokenAt(index).Text;

    private static bool IsPrimitive(string text) => PrimitiveWords.Contains(text);

    private bool Is(string text) => Current.Text == text;

    private bool Match(string text)
    {
        if (!Is(text)) return false;
        _index++;
        return true;
    }

    private JavaToken Take()
    {
        var token = Current;
        if (token.Kind != JavaTokenKind.End) _index++;
        return token;
    }

    private void Advance() => _index = Math.Min(_index + 1, _tokens.Count - 1);

    private void Expect(string text)
    {
        if (Match(text)) return;

        throw new JavaParseException($"expected '{text}' but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private JavaToken ExpectName()
    {
        if (Current.Kind != JavaTokenKind.Identifier)
            throw new JavaParseException($"expected a name but found '{Current.Text}' at {Current.Line}:{Current.Column}");

        var token = Current;
        Advance();
        return token;
    }

    /// <summary>
    /// A name after a dot: an identifier, or one of the words Java allows there — <c>Class.class</c>,
    /// <c>Outer.this</c>, <c>Foo.super</c>, <c>Type::new</c>.
    /// </summary>
    private string ExpectMemberName()
    {
        if (Current.Kind == JavaTokenKind.Identifier || MemberWords.Contains(Current.Text))
        {
            var name = Current.Text;
            Advance();
            return name;
        }

        throw new JavaParseException($"expected a member name but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private string Slice(JavaToken start, JavaToken end)
    {
        var from = Math.Clamp(start.Offset, 0, _text.Length);
        var to = Math.Clamp(end.Offset + end.Text.Length, from, _text.Length);
        return _text[from..to].Trim();
    }

    private MetacodeOrigin Origin(JavaToken start) =>
        new(MetacodeLanguage.Java, _document, start.Line, start.Column, Previous.Line, Previous.Column);

    private void Error(string code, string message, JavaToken at) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Error, message,
            new MetacodeOrigin(MetacodeLanguage.Java, _document, at.Line, at.Column, at.Line, at.Column)));

    private void Warn(string code, string message, JavaToken at) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Warning, message,
            new MetacodeOrigin(MetacodeLanguage.Java, _document, at.Line, at.Column, at.Line, at.Column)));
}

internal enum JavaTokenKind
{
    Identifier,
    Keyword,
    Number,
    String,
    Character,
    Symbol,
    End,
}

internal readonly record struct JavaToken(JavaTokenKind Kind, string Text, int Line, int Column, int Offset);

/// <summary>
/// The Java lexer: identifiers, keywords, literals (including text blocks) and punctuation. `&lt;` and
/// `&gt;` are always single-character tokens, because `&gt;&gt;` in `Map&lt;K, List&lt;V&gt;&gt;` closes two
/// generic argument lists and not a shift — the parser merges the run when it is an operator.
/// </summary>
internal static class JavaLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float",
        "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long", "native",
        "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp",
        "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void",
        "volatile", "while", "record", "yield", "sealed", "permits", "var",
    };

    private static readonly string[] Operators =
    {
        "...", "==", "!=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "->", "::",
    };

    public static List<JavaToken> Tokenize(string text)
    {
        var tokens = new List<JavaToken>();
        var index = 0;
        var line = 1;
        var column = 1;

        while (index < text.Length)
        {
            var character = text[index];

            if (character == '\r')
            {
                index++;
                continue;
            }

            if (character == '\n')
            {
                index++;
                line++;
                column = 1;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                index++;
                column++;
                continue;
            }

            // Comments: a JADX file is full of them, and none of them is code.
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                column += 2;
                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/'))
                {
                    if (text[index] == '\n') { line++; column = 1; }
                    else column++;
                    index++;
                }

                index = Math.Min(index + 2, text.Length);
                column += 2;
                continue;
            }

            var start = index;
            var startLine = line;
            var startColumn = column;

            if (character == '"')
            {
                // Text block: """ … """.
                if (index + 2 < text.Length && text[index + 1] == '"' && text[index + 2] == '"')
                {
                    index += 3;
                    while (index + 2 < text.Length && !(text[index] == '"' && text[index + 1] == '"' && text[index + 2] == '"'))
                    {
                        if (text[index] == '\n') { line++; column = 1; }
                        else column++;
                        index++;
                    }

                    index = Math.Min(index + 3, text.Length);
                    column += 3;
                }
                else
                {
                    index++;
                    while (index < text.Length && text[index] != '"')
                    {
                        if (text[index] == '\\') index++;
                        index++;
                    }

                    index = Math.Min(index + 1, text.Length);
                }

                tokens.Add(new JavaToken(JavaTokenKind.String, text[start..index], startLine, startColumn, start));
                column += index - start;
                continue;
            }

            if (character == '\'')
            {
                index++;
                while (index < text.Length && text[index] != '\'')
                {
                    if (text[index] == '\\') index++;
                    index++;
                }

                index = Math.Min(index + 1, text.Length);
                tokens.Add(new JavaToken(JavaTokenKind.Character, text[start..index], startLine, startColumn, start));
                column += index - start;
                continue;
            }

            if (char.IsDigit(character) || (character == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
            {
                index = ScanNumber(text, index);
                tokens.Add(new JavaToken(JavaTokenKind.Number, text[start..index], startLine, startColumn, start));
                column += index - start;
                continue;
            }

            if (char.IsLetter(character) || character is '_' or '$')
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$'))
                    index++;

                var word = text[start..index];
                tokens.Add(new JavaToken(Keywords.Contains(word) ? JavaTokenKind.Keyword : JavaTokenKind.Identifier,
                    word, startLine, startColumn, start));
                column += index - start;
                continue;
            }

            var matched = false;
            foreach (var @operator in Operators)
            {
                if (index + @operator.Length > text.Length) continue;
                if (!text.AsSpan(index, @operator.Length).SequenceEqual(@operator)) continue;

                tokens.Add(new JavaToken(JavaTokenKind.Symbol, @operator, startLine, startColumn, start));
                index += @operator.Length;
                column += @operator.Length;
                matched = true;
                break;
            }

            if (matched) continue;

            tokens.Add(new JavaToken(JavaTokenKind.Symbol, character.ToString(), startLine, startColumn, start));
            index++;
            column++;
        }

        tokens.Add(new JavaToken(JavaTokenKind.End, string.Empty, line, column, text.Length));
        return tokens;
    }

    private static int ScanNumber(string text, int index)
    {
        if (text[index] == '0' && index + 1 < text.Length && (text[index + 1] is 'x' or 'X'))
        {
            index += 2;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
            return index;
        }

        if (text[index] == '0' && index + 1 < text.Length && (text[index + 1] is 'b' or 'B'))
        {
            index += 2;
            while (index < text.Length && (text[index] is '0' or '1' or '_')) index++;
            return index;
        }

        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_')) index++;

        if (index < text.Length && text[index] == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1]))
        {
            index++;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_')) index++;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var save = index;
            index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            if (index < text.Length && char.IsDigit(text[index]))
            {
                while (index < text.Length && char.IsDigit(text[index])) index++;
            }
            else
            {
                index = save;
            }
        }

        if (index < text.Length && "fFdDlL".IndexOf(text[index]) >= 0) index++;
        return index;
    }
}
