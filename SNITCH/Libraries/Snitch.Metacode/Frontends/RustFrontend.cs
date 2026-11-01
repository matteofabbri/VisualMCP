using System.Text;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads Rust source and lowers it into the shared model. Rust is the one language here whose reader
/// works on the source itself — there is no AST dump this product could produce without a toolchain
/// (a <c>syn</c> dumper would need cargo, and <c>rustc -Zunpretty</c> a nightly compiler) — so, like
/// Java, it belongs in <see cref="MetacodeTranslator.Default"/> rather than behind an opt-in reader.
/// <para>
/// It is a recursive-descent reader over the Rust grammar, not a compiler: it resolves nothing and
/// type-checks nothing. A construct it has no node for — a macro definition, an <c>async</c> block, the
/// <c>?</c> operator — is kept verbatim as <see cref="MetacodeOpaqueStatement"/> /
/// <see cref="MetacodeOpaqueExpression"/> / <see cref="MetacodeOpaqueDeclaration"/> with a diagnostic,
/// and the rest of the file still lowers, so one awkward statement costs that statement and not the
/// function.
/// </para>
/// <para>
/// Two lowerings are choices rather than translations and are worth knowing when reading the output:
/// an <c>impl</c> block becomes a <c>CLASS</c> named after the type it implements (<c>impl Trait for
/// Foo</c> is <c>CLASS Foo EXTENDS Trait</c>), because Rust declares the type's members outside the
/// type; and an attribute is a decorator whose text is what stands inside the <c>#[…]</c>.
/// </para>
/// </summary>
public sealed class RustFrontend : ILanguageFrontend
{
    public string Name => "Rust";

    public MetacodeLanguage Language => MetacodeLanguage.Rust;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Rust
        || (source.Language is null && source.Path is not null
            && source.Path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options) =>
        Translate(source.Text, source.Path ?? source.Name);

    /// <summary>The same translation for a caller that already has the text.</summary>
    public MetacodeDocument Translate(string text, string document)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        var modules = new RustParser(text, document, diagnostics).Parse();

        var stem = Path.GetFileNameWithoutExtension(document);
        var name = string.IsNullOrWhiteSpace(stem) ? document : stem;
        return new MetacodeDocument(name, MetacodeLanguage.Rust, modules, diagnostics);
    }
}

internal sealed class RustParseException : Exception
{
    public RustParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// The recursive-descent reader. Every item and every statement is parsed inside a recovery point: a
/// construct the reader cannot place becomes an opaque node carrying its own text, and parsing
/// continues at the next statement, so a file never loses everything because of one line.
/// </summary>
internal sealed class RustParser
{
    /// <summary>The words a path segment may name besides an identifier (<c>Self::new</c>, <c>super::x</c>).</summary>
    private static readonly HashSet<string> PathWords = new(StringComparer.Ordinal)
    {
        "self", "Self", "super", "crate",
    };

    private readonly string _text;
    private readonly string _document;
    private readonly List<RustToken> _tokens;
    private readonly List<MetacodeDiagnostic> _diagnostics;
    private int _index;

    /// <summary>
    /// Whether <c>Path { … }</c> may start a struct literal here. It is false while the header of an
    /// <c>if</c>/<c>while</c>/<c>for</c>/<c>match</c> is parsed, because <c>if x { … }</c> opens a block
    /// and not a literal — the one place Rust's grammar is ambiguous.
    /// </summary>
    private bool _structLiteralAllowed = true;

    public RustParser(string text, string document, List<MetacodeDiagnostic> diagnostics)
    {
        _text = text;
        _document = document;
        _diagnostics = diagnostics;
        _tokens = RustLexer.Tokenize(text);
    }

    // ── compilation unit ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A file plus every <c>mod</c> block it declares, one <see cref="MetacodeModule"/> each. The file's
    /// own module comes first and is named after the file; a nested module is named
    /// <c>file::outer::inner</c>, the path Rust would use to reach it.
    /// </summary>
    public List<MetacodeModule> Parse()
    {
        var modules = new List<MetacodeModule>();
        var name = DefaultModuleName();

        List<MetacodeDeclaration> declarations;
        try
        {
            declarations = ParseItems(name, modules);
        }
        catch (RustParseException exception)
        {
            Error(MetacodeDiagnosticCodes.ParseError, exception.Message, Current);
            declarations = new List<MetacodeDeclaration>();
        }

        modules.Insert(0, new MetacodeModule(name, declarations));
        return modules;
    }

    private string DefaultModuleName()
    {
        var stem = Path.GetFileNameWithoutExtension(_document);
        return string.IsNullOrWhiteSpace(stem) ? "lib" : stem;
    }

    /// <summary>The items of a module body, up to (not including) the closing brace.</summary>
    private List<MetacodeDeclaration> ParseItems(string prefix, List<MetacodeModule> modules)
    {
        var declarations = new List<MetacodeDeclaration>();

        while (!AtEnd && !Is("}"))
        {
            var start = _index;
            var startToken = Current;
            try
            {
                ParseItem(prefix, modules, declarations);
            }
            catch (RustParseException exception)
            {
                Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    $"kept as opaque: {exception.Message}", startToken);
                declarations.Add(new MetacodeOpaqueDeclaration(
                    MetacodeLanguage.Rust, RecoverText(startToken), exception.Message)
                {
                    Origin = Origin(startToken),
                });
            }

            if (_index == start) Advance();
        }

        return declarations;
    }

    // ── items ────────────────────────────────────────────────────────────────────────

    private void ParseItem(string prefix, List<MetacodeModule> modules, List<MetacodeDeclaration> declarations)
    {
        var start = Current;

        // An inner attribute (`#![allow(dead_code)]`) configures the crate or module; it declares nothing.
        if (Is("#") && Peek(1).Text == "!" && Peek(2).Text == "[")
        {
            Advance();
            Advance();
            SkipBalancedBracket();
            return;
        }

        var attributes = ParseAttributes();

        if (Match(";")) return;

        // macro_rules! name { … }: a pattern-matching macro, which is not a function and not a type.
        if (Is("macro_rules") && Peek(1).Text == "!")
        {
            Advance();
            Advance();
            if (!Is("{")) ExpectName();
            SkipBalanced('{', '}');
            var text = Slice(start, Previous);
            Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                "kept as opaque: a macro_rules! definition has no node", start);
            declarations.Add(new MetacodeOpaqueDeclaration(
                MetacodeLanguage.Rust, text, "macro definition") { Origin = Origin(start) });
            return;
        }

        var visibility = ParseVisibility();
        var modifiers = ParseItemModifiers() | visibility;

        // `extern crate alloc;` and `error-chain` style declarations: nothing to model.
        if (Is("crate") && modifiers.HasFlag(MetacodeModifiers.Extern))
        {
            SkipToSemicolon();
            return;
        }

        // `extern "C" { fn … }`: declarations with no Rust body, still declarations.
        if (Is("{"))
        {
            Advance();
            declarations.AddRange(ParseItems(prefix, modules));
            Expect("}");
            return;
        }

        switch (Current.Text)
        {
            case "fn":
                declarations.Add(ParseFunction(start, modifiers, MetacodeFunctionKind.Function, attributes));
                return;

            case "struct":
                declarations.Add(ParseTypeDeclaration(start, "struct", MetacodeTypeKind.Struct, modifiers, attributes));
                return;

            case "union":
                declarations.Add(ParseTypeDeclaration(start, "union", MetacodeTypeKind.Union, modifiers, attributes));
                return;

            case "enum":
                declarations.Add(ParseEnum(start, modifiers, attributes));
                return;

            case "trait":
                declarations.Add(ParseTrait(start, modifiers, attributes));
                return;

            case "impl":
                declarations.Add(ParseImpl(start, modifiers, attributes));
                return;

            case "type":
                declarations.Add(ParseTypeAlias(start, attributes));
                return;

            case "static":
                declarations.Add(ParseStatic(start, modifiers));
                return;

            case "const":
                declarations.Add(ParseConstant(start, modifiers, attributes));
                return;

            case "mod":
                ParseModule(start, prefix, modules);
                return;

            case "use":
                // `use` is how Rust names a dependency, not a declaration the model has a node for.
                SkipToSemicolon();
                return;
        }

        throw new RustParseException($"expected an item but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private void ParseModule(RustToken start, string prefix, List<MetacodeModule> modules)
    {
        Expect("mod");
        var name = ExpectName().Text;
        var full = prefix + "::" + name;

        // `mod foo;` puts the module's contents in another file: the module exists, its items do not.
        if (Match(";"))
        {
            modules.Add(new MetacodeModule(full, Array.Empty<MetacodeDeclaration>()));
            Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                $"module '{name}' is declared out of line ('mod {name};'): its items live in another file", start);
            return;
        }

        Expect("{");
        var declarations = ParseItems(full, modules);
        Expect("}");
        modules.Add(new MetacodeModule(full, declarations));
    }

    private MetacodeFunction ParseFunction(
        RustToken start, MetacodeModifiers modifiers, MetacodeFunctionKind kind, List<string> attributes)
    {
        Expect("fn");
        var name = ExpectName().Text;
        var typeParameters = Is("<") ? ParseTypeParameters() : null;
        var parameters = ParseParameters();
        var returnType = Match("->") ? ParseType() : null;

        if (Is("where")) ApplyWhereClause(typeParameters);

        var body = Is("{") ? ParseBlock() : null;
        if (body is null)
        {
            Expect(";");
            // A trait method with no body is one an implementor must write.
            if (kind == MetacodeFunctionKind.Method && !modifiers.HasFlag(MetacodeModifiers.Extern))
                modifiers |= MetacodeModifiers.Abstract;
        }

        return new MetacodeFunction(
            name, kind, parameters, returnType, body, modifiers, typeParameters,
            attributes.Count == 0 ? null : attributes)
        {
            Origin = Origin(start),
        };
    }

    private IReadOnlyList<MetacodeParameter> ParseParameters()
    {
        Expect("(");
        var parameters = new List<MetacodeParameter>();

        while (!AtEnd && !Is(")"))
        {
            parameters.Add(ParseParameter());
            if (!Match(",")) break;
        }

        Expect(")");
        return parameters;
    }

    private MetacodeParameter ParseParameter()
    {
        var start = Current;
        SkipAttributes();

        // The receiver: `self`, `mut self`, `&self`, `&mut self`, `&'a mut self`, `self: Box<Self>`.
        if (Is("&"))
        {
            var save = _index;
            Advance();
            if (Current.Kind == RustTokenKind.Lifetime) Advance();
            Match("mut");
            if (Is("self"))
            {
                Advance();
                return new MetacodeParameter("self", MetacodeTypeRef.Of("Self") with { IsReference = true })
                {
                    Origin = Origin(start),
                };
            }

            _index = save;
        }

        // `mut self` takes the receiver by value and may change it.
        if (Is("mut") && Peek(1).Text == "self")
        {
            Advance();
            Advance();
            return new MetacodeParameter("self") { Origin = Origin(start) };
        }

        // Binding modes: `ref`, `mut`, and `ref mut`.
        while (Is("ref") || (Is("mut") && Peek(1).Text != "self")) Advance();

        if (Is("self"))
        {
            Advance();
            if (Match(":"))
            {
                var selfType = ParseType();
                return new MetacodeParameter("self", selfType) { Origin = Origin(start) };
            }

            return new MetacodeParameter("self") { Origin = Origin(start) };
        }

        // `name: T`, or a pattern (`(a, b): (i32, i32)`, `_: i32`) whose text becomes the name.
        string name;
        if (Current.Kind == RustTokenKind.Identifier && Peek(1).Text == ":")
        {
            name = Take().Text;
        }
        else
        {
            name = ReadPatternTextUntil(":");
        }

        Expect(":");
        var type = ParseType();
        return new MetacodeParameter(name, type) { Origin = Origin(start) };
    }

    private List<MetacodeTypeParameter> ParseTypeParameters()
    {
        Expect("<");
        var parameters = new List<MetacodeTypeParameter>();

        while (!AtEnd && !Is(">"))
        {
            SkipAttributes();

            if (Current.Kind == RustTokenKind.Lifetime)
            {
                parameters.Add(new MetacodeTypeParameter(Take().Text));
            }
            else if (Is("const"))
            {
                // A const generic parameter: `const N: usize`.
                Advance();
                var constName = ExpectName().Text;
                var constraints = new List<string>();
                if (Match(":")) constraints.Add(ParseType().Render());
                parameters.Add(new MetacodeTypeParameter(constName, constraints.Count == 0 ? null : constraints));
            }
            else
            {
                var name = ExpectName().Text;
                List<string>? constraints = null;

                if (Match(":"))
                {
                    constraints = new List<string> { ParseBound() };
                    while (Match("+")) constraints.Add(ParseBound());
                }

                if (Match("=")) ParseType();       // a default type parameter; the model has no slot for it

                parameters.Add(new MetacodeTypeParameter(name, constraints));
            }

            if (!Match(",")) break;
        }

        Expect(">");
        return parameters;
    }

    /// <summary>One bound of a type parameter or a <c>where</c> predicate: a lifetime, <c>?Sized</c> or a type.</summary>
    private string ParseBound()
    {
        if (Current.Kind == RustTokenKind.Lifetime) return Take().Text;

        var text = new StringBuilder();
        if (Match("?")) text.Append('?');
        text.Append(ParseType().Render());
        return text.ToString();
    }

    /// <summary>
    /// Reads <c>where T: Bound + Bound, 'a: 'b</c> and folds each predicate into the matching type
    /// parameter's constraints, so the <c>WHERE</c> of the source is not silently dropped.
    /// </summary>
    private void ApplyWhereClause(List<MetacodeTypeParameter>? typeParameters)
    {
        Expect("where");

        while (!AtEnd && !Is("{") && !Is(";") && !Is("="))
        {
            if (Match(",")) continue;

            var subject = ReadPatternTextUntil(":");

            // A higher-ranked predicate (`for<'a> Fn(&'a u8)`) has no parameter to attach to.
            if (subject.Length == 0)
            {
                Advance();
                continue;
            }

            Expect(":");
            var bounds = new List<string> { ParseBound() };
            while (Match("+")) bounds.Add(ParseBound());

            if (typeParameters is null) continue;

            for (var index = 0; index < typeParameters.Count; index++)
            {
                if (typeParameters[index].Name != subject) continue;

                var existing = typeParameters[index].Constraints ?? Array.Empty<string>();
                typeParameters[index] = typeParameters[index] with
                {
                    Constraints = existing.Concat(bounds).ToList(),
                };
                break;
            }
        }
    }

    private MetacodeTypeDeclaration ParseTypeDeclaration(
        RustToken start, string keyword, MetacodeTypeKind kind,
        MetacodeModifiers modifiers, List<string> attributes)
    {
        Expect(keyword);
        var name = ExpectName().Text;
        var typeParameters = Is("<") ? ParseTypeParameters() : null;

        if (Is("where")) ApplyWhereClause(typeParameters);

        var members = new List<MetacodeDeclaration>();

        if (Match(";"))
        {
            // A unit struct: it declares a name and nothing else.
        }
        else if (Is("("))
        {
            // A tuple struct: its fields are positional and are read as `.0`, `.1`, … — the names Rust
            // itself gives them.
            Advance();
            var position = 0;
            while (!AtEnd && !Is(")"))
            {
                SkipAttributes();
                var visibility = ParseVisibility();
                var type = ParseType();
                members.Add(new MetacodeField(position.ToString(), type, visibility) { Origin = Origin(start) });
                position++;
                if (!Match(",")) break;
            }

            Expect(")");
            Match(";");
        }
        else
        {
            Expect("{");
            while (!AtEnd && !Is("}"))
            {
                var fieldStart = Current;
                SkipAttributes();
                var visibility = ParseVisibility();
                var fieldName = ExpectName().Text;
                Expect(":");
                var type = ParseType();
                members.Add(new MetacodeField(fieldName, type, visibility) { Origin = Origin(fieldStart) });
                if (!Match(",")) break;
            }

            Expect("}");
        }

        return new MetacodeTypeDeclaration(
            name, kind, members, modifiers, null, typeParameters,
            attributes.Count == 0 ? null : attributes)
        {
            Origin = Origin(start),
        };
    }

    private MetacodeTypeDeclaration ParseEnum(
        RustToken start, MetacodeModifiers modifiers, List<string> attributes)
    {
        Expect("enum");
        var name = ExpectName().Text;
        var typeParameters = Is("<") ? ParseTypeParameters() : null;
        if (Is("where")) ApplyWhereClause(typeParameters);

        Expect("{");
        var members = new List<MetacodeDeclaration>();

        while (!AtEnd && !Is("}"))
        {
            var variantStart = Current;
            SkipAttributes();

            var variantName = ExpectName().Text;
            MetacodeExpression? payload = null;

            if (Is("("))
            {
                payload = ParseTuplePayload();
            }
            else if (Is("{"))
            {
                payload = ParseStructPayload();
            }

            if (Match("=")) payload = ParseExpression();      // an explicit discriminant

            members.Add(new MetacodeEnumMember(variantName, payload) { Origin = Origin(variantStart) });
            if (!Match(",")) break;
        }

        Expect("}");
        return new MetacodeTypeDeclaration(
            name, MetacodeTypeKind.Enum, members, modifiers, null, typeParameters,
            attributes.Count == 0 ? null : attributes)
        {
            Origin = Origin(start),
        };
    }

    /// <summary>
    /// <c>Some(T)</c> or <c>Rect(u32, u32)</c>: the payload's field types, written as the tuple Rust
    /// pattern-matches them against. The model has no variant-payload node, and a tuple of the types is
    /// what the reader needs to see; the alternative — the whole variant as opaque text — would hide the
    /// enum's shape.
    /// </summary>
    private MetacodeExpression ParseTuplePayload()
    {
        Expect("(");
        var types = new List<MetacodeExpression>();

        while (!AtEnd && !Is(")"))
        {
            types.Add(new MetacodeIdentifier(ParseType().Render()));
            if (!Match(",")) break;
        }

        Expect(")");
        return new MetacodeCollection(MetacodeCollectionKind.Tuple, types);
    }

    /// <summary><c>Move { x: i32, y: i32 }</c>: the payload's named fields.</summary>
    private MetacodeExpression ParseStructPayload()
    {
        Expect("{");
        var fields = new List<MetacodeMemberInitializer>();

        while (!AtEnd && !Is("}"))
        {
            SkipAttributes();
            var fieldName = ExpectName().Text;
            Expect(":");
            fields.Add(new MetacodeMemberInitializer(fieldName, new MetacodeIdentifier(ParseType().Render())));
            if (!Match(",")) break;
        }

        Expect("}");
        return new MetacodeAnonymousObject(fields);
    }

    private MetacodeTypeDeclaration ParseTrait(
        RustToken start, MetacodeModifiers modifiers, List<string> attributes)
    {
        Expect("trait");
        var name = ExpectName().Text;
        var typeParameters = Is("<") ? ParseTypeParameters() : null;

        // A trait alias: `trait Name = A + B;`.
        if (Match("="))
        {
            var target = ParseType();
            while (Match("+")) target = MetacodeTypeRef.Of(target.Render() + " + " + ParseType().Render());
            Match(";");
            return new MetacodeTypeDeclaration(
                name, MetacodeTypeKind.Trait, Array.Empty<MetacodeDeclaration>(), modifiers,
                new[] { target }, typeParameters, attributes.Count == 0 ? null : attributes)
            {
                Origin = Origin(start),
            };
        }

        var baseTypes = new List<MetacodeTypeRef>();
        if (Match(":"))
        {
            baseTypes.Add(MetacodeTypeRef.Of(ParseBound()));
            while (Match("+")) baseTypes.Add(MetacodeTypeRef.Of(ParseBound()));
        }

        if (Is("where")) ApplyWhereClause(typeParameters);

        var members = ParseAssociatedBody();
        return new MetacodeTypeDeclaration(
            name, MetacodeTypeKind.Trait, members, modifiers,
            baseTypes.Count == 0 ? null : baseTypes, typeParameters,
            attributes.Count == 0 ? null : attributes)
        {
            Origin = Origin(start),
        };
    }

    /// <summary>
    /// <c>impl Foo { … }</c> and <c>impl Bar for Foo { … }</c>. Rust keeps a type's members outside the
    /// type, so an impl block is the declaration that carries them: it becomes a <c>CLASS</c> named after
    /// the type it implements, and a trait impl names that trait in <c>EXTENDS</c>.
    /// </summary>
    private MetacodeTypeDeclaration ParseImpl(
        RustToken start, MetacodeModifiers modifiers, List<string> attributes)
    {
        Expect("impl");
        var typeParameters = Is("<") ? ParseTypeParameters() : null;

        var first = ParseType();
        var baseTypes = new List<MetacodeTypeRef>();
        var target = first;

        if (Match("for"))
        {
            baseTypes.Add(first);
            target = ParseType();
        }

        if (Is("where")) ApplyWhereClause(typeParameters);

        var members = ParseAssociatedBody();
        return new MetacodeTypeDeclaration(
            target.Render(), MetacodeTypeKind.Class, members, modifiers,
            baseTypes.Count == 0 ? null : baseTypes, typeParameters,
            attributes.Count == 0 ? null : attributes)
        {
            Origin = Origin(start),
        };
    }

    /// <summary>The <c>{ … }</c> of a trait or an impl: functions, associated types and constants.</summary>
    private List<MetacodeDeclaration> ParseAssociatedBody()
    {
        Expect("{");
        var members = new List<MetacodeDeclaration>();

        while (!AtEnd && !Is("}"))
        {
            var start = _index;
            var startToken = Current;
            try
            {
                ParseAssociatedMember(members);
            }
            catch (RustParseException exception)
            {
                Warn(MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    $"kept as opaque: {exception.Message}", startToken);
                members.Add(new MetacodeOpaqueDeclaration(
                    MetacodeLanguage.Rust, RecoverText(startToken), exception.Message)
                {
                    Origin = Origin(startToken),
                });
            }

            if (_index == start) Advance();
        }

        Expect("}");
        return members;
    }

    private void ParseAssociatedMember(List<MetacodeDeclaration> members)
    {
        var start = Current;
        var attributes = ParseAttributes();

        if (Match(";")) return;

        var visibility = ParseVisibility();
        var modifiers = ParseItemModifiers() | visibility;

        switch (Current.Text)
        {
            case "fn":
                members.Add(ParseFunction(start, modifiers, MetacodeFunctionKind.Method, attributes));
                return;
            case "type":
                members.Add(ParseTypeAlias(start, attributes));
                return;
            case "const":
                members.Add(ParseConstant(start, modifiers, attributes));
                return;
        }

        throw new RustParseException($"expected a member but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private MetacodeDeclaration ParseTypeAlias(RustToken start, List<string> attributes)
    {
        Expect("type");
        var name = ExpectName().Text;
        if (Is("<")) ParseTypeParameters();

        MetacodeTypeRef? target = null;
        if (Match(":"))
        {
            // `type Item: Display;` — an associated type's bound. It is the closest thing to a target.
            var bounds = new List<string> { ParseBound() };
            while (Match("+")) bounds.Add(ParseBound());
            target = MetacodeTypeRef.Of(string.Join(" + ", bounds));
        }
        else if (Match("="))
        {
            target = ParseType();
            if (Is("where")) SkipWhereClause();
        }

        Match(";");
        _ = attributes;
        return new MetacodeTypeAlias(name, target) { Origin = Origin(start) };
    }

    private MetacodeField ParseStatic(RustToken start, MetacodeModifiers modifiers)
    {
        Expect("static");
        Match("mut");
        var name = ExpectName().Text;
        Expect(":");
        var type = ParseType();
        var initializer = Match("=") ? ParseExpression() : null;
        Match(";");

        return new MetacodeField(name, type, modifiers | MetacodeModifiers.Static, initializer)
        {
            Origin = Origin(start),
        };
    }

    private MetacodeField ParseConstant(RustToken start, MetacodeModifiers modifiers, List<string> attributes)
    {
        Expect("const");
        var name = ExpectName().Text;
        Expect(":");
        var type = ParseType();
        var initializer = Match("=") ? ParseExpression() : null;
        Match(";");
        _ = attributes;

        return new MetacodeField(name, type, modifiers | MetacodeModifiers.Const, initializer)
        {
            Origin = Origin(start),
        };
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
            catch (RustParseException exception)
            {
                Warn(MetacodeDiagnosticCodes.UnsupportedStatement,
                    $"kept as opaque: {exception.Message}", startToken);
                statements.Add(new MetacodeOpaqueStatement(
                    MetacodeLanguage.Rust, RecoverText(startToken), exception.Message)
                {
                    Origin = Origin(startToken),
                });
            }

            if (_index == start) Advance();
        }

        Expect("}");
        return new MetacodeBlock(statements);
    }

    private MetacodeStatement? ParseStatement()
    {
        var start = Current;

        SkipAttributes();

        if (Is(";")) { Advance(); return null; }
        if (Is("{")) return ParseBlock();

        if (Is("let")) { Advance(); return ParseLet(start); }

        if (Is("return"))
        {
            Advance();
            var value = Is(";") ? null : ParseExpression();
            Expect(";");
            return new MetacodeReturn(value) { Origin = Origin(start) };
        }

        if (Is("break"))
        {
            Advance();
            var label = Current.Kind == RustTokenKind.Lifetime ? Take().Text : null;

            if (!Is(";"))
            {
                // Both a labeled break and a break that carries a value have no node, and turning the
                // value into a RETURN would say the function ends where the loop does.
                ParseExpression();
                Expect(";");
                throw new RustParseException(label is null
                    ? "'break' with a value has no node"
                    : $"'break {label}' has no node");
            }

            Expect(";");

            if (label is not null)
                Warn(MetacodeDiagnosticCodes.LossyLowering,
                    $"the model has no labeled break: '{label}' is dropped", start);

            return new MetacodeBreak { Origin = Origin(start) };
        }

        if (Is("continue"))
        {
            Advance();
            if (Current.Kind == RustTokenKind.Lifetime)
            {
                var label = Take().Text;
                Warn(MetacodeDiagnosticCodes.LossyLowering,
                    $"the model has no labeled continue: '{label}' is dropped", start);
            }

            Expect(";");
            return new MetacodeContinue { Origin = Origin(start) };
        }

        if (Is("if")) { Advance(); return ParseIf(start); }
        if (Is("while")) { Advance(); return ParseWhile(start); }
        if (Is("for")) { Advance(); return ParseFor(start); }
        if (Is("match")) { Advance(); return ParseMatch(start); }

        if (Is("loop"))
        {
            Advance();
            // `loop { … }` repeats unconditionally: the model says that with WHILE true.
            return new MetacodeWhile(MetacodeLiteral.True, ParseBlock()) { Origin = Origin(start) };
        }

        if (Is("unsafe") && Peek(1).Text == "{")
        {
            Advance();
            Warn(MetacodeDiagnosticCodes.LossyLowering,
                "the model has no marker for unsafety: the unsafe block is a plain block", start);
            return ParseBlock();
        }

        if (Is("use")) { SkipToSemicolon(); return null; }

        if (Current.Kind == RustTokenKind.Lifetime && Peek(1).Text == ":")
        {
            var label = Current.Text.TrimStart('\'');
            Advance();
            Advance();
            var inner = ParseStatement();
            return inner is null ? null : new MetacodeLabel(label, inner) { Origin = Origin(start) };
        }

        if (LooksLikeFunction())
        {
            var visibility = ParseVisibility();
            var modifiers = ParseItemModifiers() | visibility;
            var function = ParseFunction(start, modifiers, MetacodeFunctionKind.LocalFunction, new List<string>());
            return new MetacodeLocalFunction(function) { Origin = Origin(start) };
        }

        var expression = ParseExpression();

        // A block's last expression is its value, not a statement: `{ total }` returns total.
        if (Is("}"))
            return new MetacodeReturn(expression) { Origin = Origin(start) };

        Expect(";");

        return expression is MetacodeAssignmentExpression assignment
            ? new MetacodeAssignment(assignment.Target, assignment.Operator, assignment.Value) { Origin = Origin(start) }
            : new MetacodeExpressionStatement(expression) { Origin = Origin(start) };
    }

    /// <summary>Whether the tokens at the cursor are an item-level <c>fn</c> declared inside a body.</summary>
    private bool LooksLikeFunction()
    {
        var save = _index;
        try
        {
            ParseVisibility();
            ParseItemModifiers();
            return Is("fn");
        }
        catch (RustParseException)
        {
            return false;
        }
        finally
        {
            _index = save;
        }
    }

    private MetacodeStatement ParseLet(RustToken start)
    {
        Match("mut");
        Match("ref");

        var pattern = ReadPatternTextUntil(":", "=");

        // `let ref mut x` leaves the binding mode in front of the name; the model names the variable.
        if (pattern.StartsWith("mut ", StringComparison.Ordinal)) pattern = pattern[4..].Trim();

        MetacodeTypeRef? type = null;
        MetacodeExpression? initializer = null;

        if (Match(":")) type = ParseType();
        if (Match("=")) initializer = ParseExpression();

        if (Is("else"))
        {
            Advance();
            ParseBlock();
            Warn(MetacodeDiagnosticCodes.LossyLowering,
                "a let-else binding keeps the binding and drops the else block, which has no node", start);
        }

        Expect(";");
        return new MetacodeLocalVariable(pattern, type, initializer) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseIf(RustToken start)
    {
        var condition = Is("let") ? ParseLetCondition() : ParseCondition();
        var then = ParseBlock();
        MetacodeBlock? @else = null;

        if (Match("else"))
        {
            if (Is("if"))
            {
                Advance();
                @else = new MetacodeBlock(new[] { ParseIf(start) });
            }
            else
            {
                @else = ParseBlock();
            }
        }

        return new MetacodeIf(condition, then, @else) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseWhile(RustToken start)
    {
        var condition = Is("let") ? ParseLetCondition() : ParseCondition();
        return new MetacodeWhile(condition, ParseBlock()) { Origin = Origin(start) };
    }

    private MetacodeStatement ParseFor(RustToken start)
    {
        // The caller consumed the `for` keyword, as it does for every statement form.
        var pattern = ReadPatternTextUntil("in");
        Expect("in");
        var iterable = ParseCondition();
        var body = ParseBlock();

        // `for i in 0..n` is a counted loop: the same MetacodeFor Python's `range(n)` produces, so the
        // two print the same body. An inclusive range stays a FOREACH over an opaque range.
        if (iterable is MetacodeRange { Start: { } from, End: { } to } && IsIdentifier(pattern))
        {
            var counter = new MetacodeIdentifier(pattern);

            return new MetacodeFor(
                new MetacodeLocalVariable(pattern, null, from),
                new MetacodeBinary(MetacodeBinaryOperator.LessThan, counter, to),
                new MetacodeAssignment(counter, MetacodeAssignmentOperator.Add, MetacodeLiteral.Number("1")),
                body)
            {
                Origin = Origin(start),
            };
        }

        return new MetacodeForEach(pattern, null, iterable, body) { Origin = Origin(start) };
    }

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(character => char.IsLetterOrDigit(character) || character == '_');

    /// <summary><c>if let Some(x) = value</c>: the model's pattern match, used as a condition.</summary>
    private MetacodeExpression ParseLetCondition()
    {
        Expect("let");
        var pattern = ReadPatternTextUntil("=");
        Expect("=");
        var value = ParseCondition();
        return new MetacodePatternMatch(value, pattern);
    }

    private MetacodeStatement ParseMatch(RustToken start)
    {
        var subject = ParseCondition();
        Expect("{");
        var cases = new List<MetacodeSwitchCase>();

        while (!AtEnd && !Is("}"))
        {
            SkipAttributes();
            var pattern = ReadPatternTextUntil("=>");
            Expect("=>");

            MetacodeBlock body;
            if (Is("{"))
            {
                body = ParseBlock();
            }
            else
            {
                // An arm's value is what the arm produces: RETURN is how the model says that.
                body = new MetacodeBlock(new MetacodeStatement[] { new MetacodeReturn(ParseExpression()) });
            }

            var isDefault = pattern.Trim() is "_";
            cases.Add(new MetacodeSwitchCase(
                new[] { MatchLabel(pattern) }, body, isDefault));

            if (!Match(",")) break;
        }

        Expect("}");
        return new MetacodeSwitch(subject, cases) { Origin = Origin(start) };
    }

    /// <summary>
    /// A match arm's label: a literal pattern is the literal (as the C# frontend does for a constant
    /// label), everything else is pattern text. The guard, when there is one, is part of that text —
    /// the model's case has no guard slot.
    /// </summary>
    private static MetacodeExpression MatchLabel(string pattern)
    {
        var text = MetacodePatternText.Normalize(pattern);

        if (decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
            return MetacodeLiteral.Number(text);

        if (text is "true") return MetacodeLiteral.True;
        if (text is "false") return MetacodeLiteral.False;
        if (text.StartsWith('"')) return MetacodeLiteral.String(text);
        if (text.StartsWith('\'')) return new MetacodeLiteral(MetacodeLiteralKind.Character, text);

        return new MetacodePatternText(text);
    }

    // ── expressions ────────────────────────────────────────────────────────────────

    private MetacodeExpression ParseExpression() => ParseAssignment();

    private MetacodeExpression ParseAssignment()
    {
        var left = ParseRange();

        if (TryReadAssignment(out var @operator))
        {
            var value = ParseAssignment();
            return new MetacodeAssignmentExpression(left, @operator, value);
        }

        return left;
    }

    private MetacodeExpression ParseRange()
    {
        var startIndex = _index;
        var atStart = Current;

        if (Is("..") || Is("..="))
        {
            // `..end`: a range with no start.
            var inclusive = Is("..=");
            Advance();
            var end = CanStartExpression() ? ParseBinary(0) : null;

            if (inclusive) return OpaqueFrom(startIndex, "the inclusive range operator (..=) has no node", atStart);
            return new MetacodeRange(null, end);
        }

        var left = ParseBinary(0);

        if (Is("..") || Is("..="))
        {
            var inclusive = Is("..=");
            Advance();
            var right = CanStartExpression() ? ParseBinary(0) : null;

            if (inclusive) return OpaqueFrom(startIndex, "the inclusive range operator (..=) has no node", atStart);
            return new MetacodeRange(left, right) { Origin = Origin(atStart) };
        }

        return left;
    }

    /// <summary>Whether the cursor is on a token that can begin an expression.</summary>
    private bool CanStartExpression() => Current.Kind switch
    {
        RustTokenKind.Number or RustTokenKind.String or RustTokenKind.Character or RustTokenKind.Identifier => true,
        RustTokenKind.Keyword => Current.Text is
            "self" or "Self" or "super" or "crate" or "true" or "false" or "if" or "match" or "move" or "loop"
            or "while" or "for" or "unsafe" or "async",
        _ => Current.Text is "(" or "[" or "{" or "|" or "||" or "!" or "-" or "*" or "&" or "&&" or "..",
    };

    private MetacodeExpression ParseCondition()
    {
        var save = _structLiteralAllowed;
        _structLiteralAllowed = false;
        try
        {
            return ParseExpression();
        }
        finally
        {
            _structLiteralAllowed = save;
        }
    }

    private MetacodeExpression ParseBinary(int level)
    {
        if (level > 9) return ParseCast();

        var left = ParseBinary(level + 1);

        while (true)
        {
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
            case 2 when Match("=="): @operator = MetacodeBinaryOperator.Equal; return true;
            case 2 when Match("!="): @operator = MetacodeBinaryOperator.NotEqual; return true;
            case 3:
                if (!TryConsumeAngleOperator(shiftOnly: false, out var comparison)) return false;
                @operator = comparison switch
                {
                    "<" => MetacodeBinaryOperator.LessThan,
                    "<=" => MetacodeBinaryOperator.LessThanOrEqual,
                    ">" => MetacodeBinaryOperator.GreaterThan,
                    _ => MetacodeBinaryOperator.GreaterThanOrEqual,
                };
                return true;
            case 4 when Match("|"): @operator = MetacodeBinaryOperator.BitwiseOr; return true;
            case 5 when Match("^"): @operator = MetacodeBinaryOperator.BitwiseXor; return true;
            case 6 when Match("&"): @operator = MetacodeBinaryOperator.BitwiseAnd; return true;
            case 7:
                if (!TryConsumeAngleOperator(shiftOnly: true, out var shift) || shift is not ("<<" or ">>"))
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

    private MetacodeExpression ParseCast()
    {
        var expression = ParseUnary();

        while (Match("as"))
            expression = new MetacodeCast(ParseType(), expression);

        return expression;
    }

    private MetacodeExpression ParseUnary()
    {
        if (Is("!") || Is("-") || Is("*"))
        {
            var text = Current.Text;
            Advance();

            var @operator = text switch
            {
                "!" => MetacodeUnaryOperator.Not,
                "-" => MetacodeUnaryOperator.Negate,
                _ => MetacodeUnaryOperator.Dereference,
            };

            return new MetacodeUnary(@operator, ParseUnary());
        }

        // `&&x` is two address-ofs, not a logical and: in this position it cannot be an operator.
        if (Is("&&"))
        {
            Advance();
            return new MetacodeUnary(MetacodeUnaryOperator.AddressOf,
                new MetacodeUnary(MetacodeUnaryOperator.AddressOf, ParseUnary()));
        }

        if (Is("&"))
        {
            Advance();
            if (Current.Kind == RustTokenKind.Lifetime) Advance();
            Match("mut");
            return new MetacodeUnary(MetacodeUnaryOperator.AddressOf, ParseUnary());
        }

        return ParsePostfix();
    }

    private MetacodeExpression ParsePostfix()
    {
        var startIndex = _index;
        var startToken = Current;
        var expression = ParsePrimary();

        while (true)
        {
            if (Is("."))
            {
                Advance();

                if (Is("await"))
                {
                    Advance();
                    expression = new MetacodeAwait(expression);
                    continue;
                }

                // A tuple field: `.0`, `.1`. Rust names those fields by their position.
                if (Current.Kind == RustTokenKind.Number)
                {
                    var position = Take().Text;
                    expression = new MetacodeMemberAccess(expression, position);
                    continue;
                }

                var member = ExpectMemberName();

                // A turbofish on the method: `.collect::<Vec<_>>()`.
                if (Is("::") && Peek(1).Text == "<")
                {
                    Advance();
                    SkipBalanced('<', '>');
                }

                expression = Is("(")
                    ? new MetacodeInvocation(new MetacodeMemberAccess(expression, member), ParseArguments())
                    : new MetacodeMemberAccess(expression, member);
                continue;
            }

            if (Is("["))
            {
                Advance();
                var indices = new List<MetacodeExpression>();
                while (!AtEnd && !Is("]"))
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

            if (Is("?"))
            {
                // `value?` returns the error to the caller: an early return the model has no node for.
                Advance();
                return OpaqueFrom(startIndex, "the ? operator propagates an error and has no node", startToken);
            }

            break;
        }

        return expression;
    }

    private MetacodeExpression ParsePrimary()
    {
        var start = Current;

        if (start.Kind == RustTokenKind.Number) { Advance(); return MetacodeLiteral.Number(start.Text); }
        if (start.Kind == RustTokenKind.String) { Advance(); return MetacodeLiteral.String(start.Text); }
        if (start.Kind == RustTokenKind.Character)
        {
            Advance();
            return new MetacodeLiteral(MetacodeLiteralKind.Character, start.Text);
        }

        if (start.Text == "true") { Advance(); return MetacodeLiteral.True; }
        if (start.Text == "false") { Advance(); return MetacodeLiteral.False; }

        if (Is("move") || Is("|") || Is("||")) return ParseClosure(start);

        if (Is("if")) { Advance(); return ParseIfExpression(start); }
        if (Is("match")) { Advance(); return ParseMatchExpression(start); }

        if (Is("loop") || Is("while") || Is("for"))
            throw new RustParseException($"'{Current.Text}' used as a value has no node");

        if (Is("async") || (Is("move") && Peek(1).Text == "async"))
            throw new RustParseException("an async block has no node");

        if (Is("{"))
            throw new RustParseException("a block used as a value has no node");

        if (Is("unsafe") && Peek(1).Text == "{")
            throw new RustParseException("an unsafe block used as a value has no node");

        if (Is("(")) return ParseParenthesizedOrTuple();

        if (Is("[")) return ParseArrayLiteral();

        // A path: `value`, `self`, `Self::new`, `crate::inner::helper`, `std::mem::swap`. The keyword
        // segments are read by the path reader, so `self::foo()` keeps its `::` instead of stopping at `self`.
        if (Is("self") || Is("Self") || Is("super") || Is("crate") || Current.Kind == RustTokenKind.Identifier)
        {
            var path = ParsePathExpression();

            // A macro call: `println!(…)`, `vec![…]`, `matches!(…)`.
            if (Is("!") && Peek(1).Text is "(" or "[" or "{")
            {
                Advance();
                var (open, close) = Current.Text switch
                {
                    "[" => ("[", "]"),
                    "{" => ("{", "}"),
                    _ => ("(", ")"),
                };

                var name = PathText(path) ?? "macro";
                var arguments = ParseArguments(open, close);
                return new MetacodeInvocation(new MetacodeIdentifier(name + "!"), arguments);
            }

            if (_structLiteralAllowed && Is("{") && LooksLikeStructLiteral())
                return ParseStructLiteral(path);

            return path;
        }

        throw new RustParseException($"unexpected '{start.Text}' in an expression at {start.Line}:{start.Column}");
    }

    /// <summary>
    /// Whether the <c>{</c> at the cursor opens a struct literal (<c>Foo { a: 1 }</c>, <c>Foo { a }</c>,
    /// <c>Foo { ..base }</c>) rather than a block. It looks one token past the brace, which is all the
    /// grammar needs: a block starts with a statement, a literal with a field name or <c>..</c>.
    /// </summary>
    private bool LooksLikeStructLiteral()
    {
        var next = TextAt(_index + 1);
        if (next == "}") return true;
        if (next == "..") return true;
        if (next == "async") return false;
        if (_tokens[Math.Clamp(_index + 1, 0, _tokens.Count - 1)].Kind != RustTokenKind.Identifier) return false;

        var after = TextAt(_index + 2);
        return after is ":" or "," or "}";
    }

    private MetacodeExpression ParseStructLiteral(MetacodeExpression path)
    {
        var start = Current;
        var typeName = PathText(path) ?? "<anonymous>";
        Expect("{");

        var members = new List<MetacodeMemberInitializer>();
        MetacodeExpression? update = null;

        while (!AtEnd && !Is("}"))
        {
            if (Match(".."))
            {
                update = ParseExpression();
                break;
            }

            SkipAttributes();
            var name = ExpectMemberName();
            var value = Match(":") ? ParseExpression() : new MetacodeIdentifier(name);
            members.Add(new MetacodeMemberInitializer(name, value));

            if (!Match(",")) break;
        }

        Expect("}");

        // `Foo { a: 1, ..base }` is a copy of base with a replaced: the model's WITH.
        if (update is not null)
            return new MetacodeWithExpression(update, members) { Origin = Origin(start) };

        return new MetacodeObjectCreation(MetacodeTypeRef.Of(typeName), MemberInitializers: members)
        {
            Origin = Origin(start),
        };
    }

    private MetacodeExpression ParseParenthesizedOrTuple()
    {
        Expect("(");

        if (Match(")")) return new MetacodeCollection(MetacodeCollectionKind.Tuple, Array.Empty<MetacodeExpression>());

        var first = ParseExpression();
        if (!Is(","))
        {
            Expect(")");
            return first;
        }

        var items = new List<MetacodeExpression> { first };
        while (Match(","))
        {
            if (Is(")")) break;
            items.Add(ParseExpression());
        }

        Expect(")");
        return new MetacodeCollection(MetacodeCollectionKind.Tuple, items);
    }

    private MetacodeExpression ParseArrayLiteral()
    {
        Expect("[");
        var items = new List<MetacodeExpression>();

        while (!AtEnd && !Is("]"))
        {
            items.Add(ParseExpression());

            // `[0; 16]` repeats one value: the model's NEW ARRAY carries the size.
            if (Match(";"))
            {
                var size = ParseExpression();
                Expect("]");
                return new MetacodeNewArray(null, new[] { size }, new[] { items[0] });
            }

            if (!Match(",")) break;
        }

        Expect("]");
        return new MetacodeCollection(MetacodeCollectionKind.Array, items);
    }

    private MetacodeExpression ParseClosure(RustToken start)
    {
        Match("move");

        var parameters = new List<MetacodeParameter>();

        if (!Match("||"))
        {
            Expect("|");
            while (!AtEnd && !Is("|"))
            {
                SkipAttributes();
                Match("mut");
                var name = ExpectName().Text;
                MetacodeTypeRef? type = null;
                if (Match(":")) type = ParseType();
                parameters.Add(new MetacodeParameter(name, type));
                if (!Match(",")) break;
            }

            Expect("|");
        }

        if (Match("->")) ParseType();

        var lambda = Is("{")
            ? new MetacodeLambda(parameters, ParseBlock())
            : new MetacodeLambda(parameters, Expression: ParseExpression());

        return lambda with { Origin = Origin(start) };
    }

    /// <summary>
    /// <c>if condition { a } else { b }</c> where an expression is expected. The model states that with
    /// its conditional operator, which is exactly what the two arms are — when each is one expression.
    /// </summary>
    private MetacodeExpression ParseIfExpression(RustToken start)
    {
        var condition = Is("let") ? ParseLetCondition() : ParseCondition();
        var then = ParseBlock();

        if (!Match("else"))
            throw new RustParseException("an 'if' with no 'else' used as a value has no node");

        MetacodeExpression whenFalse;
        if (Is("if"))
        {
            Advance();
            whenFalse = ParseIfExpression(Current);
        }
        else if (SingleExpression(ParseBlock()) is { } value)
        {
            whenFalse = value;
        }
        else
        {
            throw new RustParseException("an 'if' whose else arm is not a single expression has no node");
        }

        return SingleExpression(then) is { } whenTrue
            ? new MetacodeConditional(condition, whenTrue, whenFalse) { Origin = Origin(start) }
            : throw new RustParseException("an 'if' used as a value whose then arm is not a single expression has no node");
    }

    private MetacodeExpression ParseMatchExpression(RustToken start)
    {
        var subject = ParseCondition();
        Expect("{");
        var arms = new List<MetacodeSwitchArm>();

        while (!AtEnd && !Is("}"))
        {
            SkipAttributes();
            var pattern = ReadPatternTextUntil("=>");
            Expect("=>");

            if (Is("{")) throw new RustParseException("a match arm with a block body used as a value has no node");

            var value = ParseExpression();
            arms.Add(new MetacodeSwitchArm(pattern.Trim(), null, value));

            if (!Match(",")) break;
        }

        Expect("}");
        return new MetacodeSwitchExpression(subject, arms) { Origin = Origin(start) };
    }

    /// <summary>The value a one-statement block evaluates to, or null when the block does more.</summary>
    private static MetacodeExpression? SingleExpression(MetacodeBlock block) =>
        block.Statements.Count == 1
            ? block.Statements[0] switch
            {
                MetacodeReturn { Value: { } value } => value,
                MetacodeExpressionStatement statement => statement.Expression,
                _ => null,
            }
            : null;

    private IReadOnlyList<MetacodeArgument> ParseArguments(string open = "(", string close = ")")
    {
        Expect(open);
        var arguments = new List<MetacodeArgument>();

        while (!AtEnd && !Is(close))
        {
            arguments.Add(MetacodeArgument.Of(ParseExpression()));
            if (!Match(",")) break;
        }

        Expect(close);
        return arguments;
    }

    // ── paths ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>std::mem::swap</c>, <c>Self::new</c>, <c>Vec::&lt;i32&gt;::new</c>: a chain of member accesses.
    /// The model has no module operator, so a path reads with the member access every language shares.
    /// </summary>
    private MetacodeExpression ParsePathExpression()
    {
        MetacodeExpression expression = new MetacodeIdentifier(ExpectPathSegment());

        while (Is("::"))
        {
            Advance();

            if (Is("<"))
            {
                SkipBalanced('<', '>');        // a turbofish on the segment just read
                continue;
            }

            var member = ExpectPathSegment();
            expression = new MetacodeMemberAccess(expression, member);
        }

        return expression;
    }

    private string ExpectPathSegment()
    {
        if (Current.Kind == RustTokenKind.Identifier || PathWords.Contains(Current.Text))
        {
            var text = Current.Text;
            Advance();
            return text;
        }

        throw new RustParseException($"expected a name but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private static string? PathText(MetacodeExpression expression) => expression switch
    {
        MetacodeIdentifier identifier => identifier.Name,
        MetacodeMemberAccess access when PathText(access.Target) is { } target => target + "::" + access.Member,
        _ => null,
    };

    // ── types ───────────────────────────────────────────────────────────────────────

    private MetacodeTypeRef ParseType()
    {
        if (Match("&"))
        {
            if (Current.Kind == RustTokenKind.Lifetime) Advance();
            Match("mut");
            return ParseType() with { IsReference = true };
        }

        if (Match("*"))
        {
            if (!Match("const")) Match("mut");
            return ParseType() with { IsPointer = true };
        }

        if (Is("["))
        {
            Advance();
            var element = ParseType();

            if (Match(";"))
            {
                // `[u8; 16]` keeps its length in the type name: the model's IsArray has no slot for it.
                var sizeStart = _index;
                ParseExpression();
                var size = Slice(TokenAt(sizeStart), Previous);
                Expect("]");
                return MetacodeTypeRef.Of(element.Render() + "[" + size + "]");
            }

            Expect("]");
            return element with { IsArray = true };
        }

        if (Is("("))
        {
            Advance();
            if (Match(")")) return MetacodeTypeRef.Of("()");

            var items = new List<MetacodeTypeRef> { ParseType() };
            while (Match(","))
            {
                if (Is(")")) break;
                items.Add(ParseType());
            }

            Expect(")");
            return MetacodeTypeRef.Of("(" + string.Join(", ", items.Select(item => item.Render())) + ")");
        }

        if (Match("!")) return MetacodeTypeRef.Of("!");

        if (Is("dyn") || Is("impl"))
        {
            var keyword = Take().Text;
            var bounds = new List<string> { ParseBound() };
            while (Match("+")) bounds.Add(ParseBound());
            return MetacodeTypeRef.Of(keyword + " " + string.Join(" + ", bounds));
        }

        var name = ParseTypePath();
        IReadOnlyList<MetacodeTypeRef>? arguments = null;

        if (Is("<")) arguments = ParseTypeArguments();

        // A function-trait bound: `Fn(i32) -> i32`.
        if (Is("("))
        {
            Advance();
            var parameters = new List<string>();
            while (!AtEnd && !Is(")"))
            {
                parameters.Add(ParseType().Render());
                if (!Match(",")) break;
            }

            Expect(")");
            var text = name + "(" + string.Join(", ", parameters) + ")";
            if (Match("->")) text += " -> " + ParseType().Render();
            return MetacodeTypeRef.Of(text);
        }

        return new MetacodeTypeRef(name, arguments);
    }

    private string ParseTypePath()
    {
        var builder = new StringBuilder(ExpectPathSegment());

        while (Is("::") && Peek(1).Text != "<")
        {
            Advance();
            builder.Append("::").Append(ExpectPathSegment());
        }

        return builder.ToString();
    }

    private IReadOnlyList<MetacodeTypeRef> ParseTypeArguments()
    {
        Expect("<");
        var arguments = new List<MetacodeTypeRef>();

        while (!AtEnd && !Is(">"))
        {
            if (Current.Kind == RustTokenKind.Lifetime)
            {
                arguments.Add(MetacodeTypeRef.Of(Take().Text));
            }
            else if (Is("const"))
            {
                Advance();
                var start = _index;
                ParseExpression();
                arguments.Add(MetacodeTypeRef.Of("const " + Slice(TokenAt(start), Previous)));
            }
            else
            {
                var type = ParseType();

                // An associated type binding: `Iterator<Item = u8>`.
                if (Match("=")) type = MetacodeTypeRef.Of(type.Render() + " = " + ParseType().Render());
                arguments.Add(type);
            }

            if (!Match(",")) break;
        }

        Expect(">");
        return arguments;
    }

    // ── attributes, visibility and modifiers ────────────────────────────────────────

    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();

        while (Is("#") && Peek(1).Text == "[")
        {
            Advance();
            Advance();
            attributes.Add(CaptureBalancedBracket());
        }

        return attributes;
    }

    private void SkipAttributes()
    {
        while (Is("#") && Peek(1).Text == "[")
        {
            Advance();
            Advance();
            SkipBalancedBracket();
        }
    }

    /// <summary>The text inside a <c>#[…]</c> whose opening bracket was already consumed.</summary>
    private string CaptureBalancedBracket()
    {
        var from = Current.Offset;
        var depth = 1;

        while (!AtEnd)
        {
            var text = Current.Text;
            if (text == "[") depth++;
            else if (text == "]" && --depth == 0) break;
            Advance();
        }

        var to = Current.Offset;
        Expect("]");
        return _text[Math.Clamp(from, 0, _text.Length)..Math.Clamp(to, 0, _text.Length)].Trim();
    }

    private void SkipBalancedBracket() => SkipBalanced('[', ']');

    private MetacodeModifiers ParseVisibility()
    {
        if (!Is("pub")) return MetacodeModifiers.None;

        Advance();
        if (Is("(")) SkipBalanced('(', ')');
        return MetacodeModifiers.Public;
    }

    /// <summary>The words that stand before an item's keyword and are not the item itself.</summary>
    private MetacodeModifiers ParseItemModifiers()
    {
        var modifiers = MetacodeModifiers.None;

        while (!AtEnd)
        {
            if (Is("const") && Peek(1).Text == "fn") { modifiers |= MetacodeModifiers.Const; Advance(); continue; }
            if (Is("async") && Peek(1).Text is "fn" or "unsafe") { modifiers |= MetacodeModifiers.Async; Advance(); continue; }
            if (Is("unsafe")) { modifiers |= MetacodeModifiers.Unsafe; Advance(); continue; }

            if (Is("extern"))
            {
                Advance();
                modifiers |= MetacodeModifiers.Extern;
                if (Current.Kind == RustTokenKind.String) Advance();
                continue;
            }

            break;
        }

        return modifiers;
    }

    // ── lookahead, recovery and tokens ──────────────────────────────────────────────

    /// <summary>
    /// Reads tokens until one of <paramref name="stops"/> at nesting depth 0, and returns their text. It
    /// is the pattern reader: a Rust pattern (<c>Some(x)</c>, <c>Foo { a: 1 }</c>, <c>0..=9</c>) is kept
    /// as text, the same choice <see cref="MetacodeSwitchArm"/> makes.
    /// </summary>
    private string ReadPatternTextUntil(params string[] stops)
    {
        var start = Current;
        var depth = 0;

        while (!AtEnd)
        {
            var text = Current.Text;

            if (depth == 0 && Array.IndexOf(stops, text) >= 0) break;

            if (text is "(" or "[" or "{")
            {
                depth++;
            }
            else if (text is ")" or "]" or "}")
            {
                if (depth == 0) break;
                depth--;
            }

            Advance();
        }

        return Slice(start, Previous);
    }

    private void SkipWhereClause()
    {
        Expect("where");
        while (!AtEnd && !Is("{") && !Is(";"))
        {
            var depth = 0;
            if (Is("<")) depth++;
            if (Is(">") && depth == 0) break;
            Advance();
        }
    }

    private void SkipToSemicolon()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var text = Current.Text;
            if (text is "(" or "[" or "{") depth++;
            else if (text is ")" or "]" or "}") { if (depth == 0) break; depth--; }
            else if (text == ";" && depth == 0) { Advance(); return; }

            Advance();
        }
    }

    private void SkipBalanced(char open, char close)
    {
        if (!Is(open.ToString())) return;

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
    /// Reads <c>&lt;</c>- and <c>&gt;</c>-prefixed operators from single-character tokens: <c>Vec&lt;Vec&lt;i32&gt;&gt;</c>
    /// closes two generic lists with two <c>&gt;</c> tokens, so the lexer cannot merge them and the
    /// parser does when the run is an operator.
    /// </summary>
    private bool TryConsumeAngleOperator(bool shiftOnly, out string @operator)
    {
        @operator = string.Empty;
        if (!Is("<") && !Is(">")) return false;

        var save = _index;
        var character = Current.Text[0];

        Advance();

        if (Is(character.ToString()) && AdjacentToPrevious())
        {
            Advance();

            if (!shiftOnly)
            {
                _index = save;
                return false;
            }

            if (Is("=") && AdjacentToPrevious())
            {
                _index = save;
                return false;
            }

            @operator = new string(character, 2);
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

        // <<= and >>= are spelled with single-character tokens for the same reason as the shifts.
        if (!Is("<") && !Is(">")) return false;

        var save = _index;
        var character = Current.Text[0];
        Advance();

        if (Is(character.ToString()) && AdjacentToPrevious())
        {
            Advance();
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

    /// <summary>
    /// Where a construct the reader could not place ends: the closing <c>;</c> of the item, or the
    /// closing <c>}</c> of its own block (leaving the enclosing block's brace alone).
    /// </summary>
    private string RecoverText(RustToken start)
    {
        var depth = 0;

        while (!AtEnd)
        {
            var text = Current.Text;

            if (text is "(" or "[" or "{")
            {
                depth++;
                Advance();
                continue;
            }

            if (text is ")" or "]" or "}")
            {
                if (depth == 0)
                {
                    if (text == "}") break;
                    Advance();
                    continue;
                }

                depth--;
                Advance();
                if (depth == 0 && text == "}") break;
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

    private bool AtEnd => Current.Kind == RustTokenKind.End;

    private RustToken Current => TokenAt(_index);

    private RustToken Previous => TokenAt(_index - 1);

    private RustToken Peek(int offset) => TokenAt(_index + offset);

    private RustToken TokenAt(int index) => _tokens[Math.Clamp(index, 0, _tokens.Count - 1)];

    private string TextAt(int index) => TokenAt(index).Text;

    private bool Is(string text) => Current.Text == text;

    private bool Match(string text)
    {
        if (!Is(text)) return false;
        _index++;
        return true;
    }

    private RustToken Take()
    {
        var token = Current;
        if (token.Kind != RustTokenKind.End) _index++;
        return token;
    }

    private void Advance() => _index = Math.Min(_index + 1, _tokens.Count - 1);

    private void Expect(string text)
    {
        if (Match(text)) return;

        throw new RustParseException($"expected '{text}' but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private RustToken ExpectName()
    {
        if (Current.Kind != RustTokenKind.Identifier)
            throw new RustParseException($"expected a name but found '{Current.Text}' at {Current.Line}:{Current.Column}");

        var token = Current;
        Advance();
        return token;
    }

    /// <summary>A name after a dot: an identifier, or a tuple index (<c>.0</c>).</summary>
    private string ExpectMemberName()
    {
        if (Current.Kind is RustTokenKind.Identifier or RustTokenKind.Keyword)
        {
            var name = Current.Text;
            Advance();
            return name;
        }

        throw new RustParseException($"expected a member name but found '{Current.Text}' at {Current.Line}:{Current.Column}");
    }

    private string Slice(RustToken start, RustToken end)
    {
        var from = Math.Clamp(start.Offset, 0, _text.Length);
        var to = Math.Clamp(end.Offset + end.Text.Length, from, _text.Length);
        return _text[from..to].Trim();
    }

    /// <summary>
    /// An expression the model has no node for, kept with the source text it was parsed from. The text
    /// runs from <paramref name="startIndex"/> to the last token consumed, so a reader sees the construct
    /// itself (<c>foo()?</c>) and not a fragment of it.
    /// </summary>
    private MetacodeExpression OpaqueFrom(int startIndex, string reason, RustToken at)
    {
        var text = Slice(TokenAt(startIndex), Previous);
        Warn(MetacodeDiagnosticCodes.UnsupportedExpression, $"kept as opaque: {reason}", at);
        return new MetacodeOpaqueExpression(MetacodeLanguage.Rust, text, reason) { Origin = Origin(at) };
    }

    private MetacodeOrigin Origin(RustToken start) =>
        new(MetacodeLanguage.Rust, _document, start.Line, start.Column, Previous.Line, Previous.Column);

    private void Error(string code, string message, RustToken at) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Error, message,
            new MetacodeOrigin(MetacodeLanguage.Rust, _document, at.Line, at.Column, at.Line, at.Column)));

    private void Warn(string code, string message, RustToken at) =>
        _diagnostics.Add(new MetacodeDiagnostic(code, MetacodeDiagnosticSeverity.Warning, message,
            new MetacodeOrigin(MetacodeLanguage.Rust, _document, at.Line, at.Column, at.Line, at.Column)));
}

internal enum RustTokenKind
{
    Identifier,
    Keyword,
    Number,
    String,
    Character,
    Lifetime,
    Symbol,
    End,
}

internal readonly record struct RustToken(RustTokenKind Kind, string Text, int Line, int Column, int Offset);

/// <summary>
/// The Rust lexer: identifiers, keywords, literals, lifetimes and punctuation. <c>&lt;</c> and
/// <c>&gt;</c> are always single-character tokens, because <c>Vec&lt;Vec&lt;i32&gt;&gt;</c> closes two
/// generic argument lists with two <c>&gt;</c> and not a shift — the parser merges the run when it is an
/// operator. A quote is a character literal when it closes within a character (<c>'a'</c>, <c>'\n'</c>)
/// and a lifetime otherwise (<c>'a</c>, <c>'static</c>).
/// </summary>
internal static class RustLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern",
        "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub",
        "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true", "type", "unsafe",
        "use", "where", "while", "union", "macro_rules",
    };

    /// <summary>Longest first: <c>..=</c> must win over <c>..</c>, and every two-character operator over its prefix.</summary>
    private static readonly string[] Operators =
    {
        "..=", "::", "->", "=>", "==", "!=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "..",
    };

    public static List<RustToken> Tokenize(string text)
    {
        var tokens = new List<RustToken>();
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

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n') index++;
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                // Rust block comments nest, unlike Java's.
                var depth = 0;
                while (index < text.Length)
                {
                    if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                        column += 2;
                        continue;
                    }

                    if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                    {
                        index += 2;
                        column += 2;
                        if (--depth == 0) break;
                        continue;
                    }

                    if (text[index] == '\n') { line++; column = 1; }
                    else column++;
                    index++;
                }

                continue;
            }

            var start = index;
            var startLine = line;
            var startColumn = column;

            // Raw and byte strings: r"…", r#"…"#, b"…", br#"…"#.
            if (TryScanPrefixedString(text, ref index))
            {
                tokens.Add(new RustToken(RustTokenKind.String, text[start..index], startLine, startColumn, start));
                AdvancePosition(text.AsSpan(start, index - start), ref line, ref column);
                continue;
            }

            // A raw identifier: r#type.
            if (character == 'r' && index + 1 < text.Length && text[index + 1] == '#'
                && index + 2 < text.Length && (char.IsLetter(text[index + 2]) || text[index + 2] == '_'))
            {
                index += 2;
                var nameStart = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                tokens.Add(new RustToken(RustTokenKind.Identifier, text[nameStart..index], startLine, startColumn, start));
                column += index - start;
                continue;
            }

            if (character == '\'')
            {
                if (IsCharacterLiteral(text, index))
                {
                    index++;
                    while (index < text.Length && text[index] != '\'')
                    {
                        if (text[index] == '\\') index++;
                        index++;
                    }

                    index = Math.Min(index + 1, text.Length);
                    tokens.Add(new RustToken(RustTokenKind.Character, text[start..index], startLine, startColumn, start));
                }
                else
                {
                    // A lifetime: 'a, 'static, '_.
                    index++;
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                    tokens.Add(new RustToken(RustTokenKind.Lifetime, text[start..index], startLine, startColumn, start));
                }

                AdvancePosition(text.AsSpan(start, index - start), ref line, ref column);
                continue;
            }

            if (character == '"')
            {
                index++;
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\') index++;
                    index++;
                }

                index = Math.Min(index + 1, text.Length);
                tokens.Add(new RustToken(RustTokenKind.String, text[start..index], startLine, startColumn, start));
                AdvancePosition(text.AsSpan(start, index - start), ref line, ref column);
                continue;
            }

            if (char.IsDigit(character))
            {
                index = ScanNumber(text, index);
                tokens.Add(new RustToken(RustTokenKind.Number, text[start..index], startLine, startColumn, start));
                column += index - start;
                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                    index++;

                var word = text[start..index];
                tokens.Add(new RustToken(
                    Keywords.Contains(word) ? RustTokenKind.Keyword : RustTokenKind.Identifier,
                    word, startLine, startColumn, start));
                column += index - start;
                continue;
            }

            var matched = false;
            foreach (var @operator in Operators)
            {
                if (index + @operator.Length > text.Length) continue;
                if (!text.AsSpan(index, @operator.Length).SequenceEqual(@operator)) continue;

                tokens.Add(new RustToken(RustTokenKind.Symbol, @operator, startLine, startColumn, start));
                index += @operator.Length;
                column += @operator.Length;
                matched = true;
                break;
            }

            if (matched) continue;

            tokens.Add(new RustToken(RustTokenKind.Symbol, character.ToString(), startLine, startColumn, start));
            index++;
            column++;
        }

        tokens.Add(new RustToken(RustTokenKind.End, string.Empty, line, column, text.Length));
        return tokens;
    }

    /// <summary>Moves the line and column over the text just consumed — a raw string may span lines.</summary>
    private static void AdvancePosition(ReadOnlySpan<char> consumed, ref int line, ref int column)
    {
        var newlines = 0;
        var lastNewline = -1;

        for (var index = 0; index < consumed.Length; index++)
        {
            if (consumed[index] != '\n') continue;
            newlines++;
            lastNewline = index;
        }

        if (newlines == 0)
        {
            column += consumed.Length;
            return;
        }

        line += newlines;
        column = consumed.Length - lastNewline;
    }

    /// <summary>
    /// A char literal closes within one character (<c>'a'</c>) or an escape (<c>'\n'</c>); anything else
    /// that starts with a quote is a lifetime (<c>'a</c>, <c>'static</c>). A surrogate pair counts as one
    /// character, which is why the astral case is checked explicitly.
    /// </summary>
    private static bool IsCharacterLiteral(string text, int index)
    {
        if (index + 1 >= text.Length) return false;

        var next = text[index + 1];
        if (next == '\\') return true;
        if (index + 2 < text.Length && text[index + 2] == '\'') return true;
        return char.IsHighSurrogate(next) && index + 3 < text.Length && text[index + 3] == '\'';
    }

    /// <summary>Consumes a raw (<c>r"…"</c>, <c>r#"…"#</c>) or byte (<c>b"…"</c>, <c>br#"…"#</c>) string.</summary>
    private static bool TryScanPrefixedString(string text, ref int index)
    {
        var cursor = index;

        if (cursor < text.Length && text[cursor] == 'b')
        {
            if (cursor + 1 < text.Length && text[cursor + 1] == 'r') cursor++;
            else if (cursor + 1 < text.Length && text[cursor + 1] == '"')
            {
                index = ScanQuoted(text, cursor + 1);
                return true;
            }
            else
            {
                return false;
            }
        }

        if (cursor >= text.Length || text[cursor] != 'r') return false;

        cursor++;
        var hashes = 0;
        while (cursor < text.Length && text[cursor] == '#')
        {
            hashes++;
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] != '"') return false;

        cursor++;
        while (cursor < text.Length)
        {
            if (text[cursor] != '"')
            {
                cursor++;
                continue;
            }

            var close = 0;
            while (cursor + 1 + close < text.Length && text[cursor + 1 + close] == '#' && close < hashes) close++;

            if (close == hashes)
            {
                index = cursor + 1 + hashes;
                return true;
            }

            cursor++;
        }

        index = text.Length;
        return true;
    }

    private static int ScanQuoted(string text, int quoteIndex)
    {
        var index = quoteIndex + 1;
        while (index < text.Length && text[index] != '"')
        {
            if (text[index] == '\\') index++;
            index++;
        }

        return Math.Min(index + 1, text.Length);
    }

    private static int ScanNumber(string text, int index)
    {
        if (index + 1 < text.Length && text[index] == '0')
        {
            var prefix = text[index + 1];
            if (prefix is 'x' or 'X') return ScanDigits(text, index + 2, char.IsAsciiHexDigit);
            if (prefix is 'o' or 'O') return ScanDigits(text, index + 2, character => character is >= '0' and <= '7');
            if (prefix is 'b' or 'B') return ScanDigits(text, index + 2, character => character is '0' or '1');
        }

        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_')) index++;

        // `1..10` is a range, and `1.foo()` is a method call: only a dot that cannot start one of those
        // belongs to the literal.
        if (index < text.Length && text[index] == '.'
            && (index + 1 >= text.Length || text[index + 1] != '.')
            && (index + 1 >= text.Length || !char.IsLetter(text[index + 1])))
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

        // A type suffix: 1u32, 2.5f64, 0usize.
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
        return index;
    }

    private static int ScanDigits(string text, int index, Func<char, bool> isDigit)
    {
        while (index < text.Length && (isDigit(text[index]) || text[index] == '_')) index++;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
        return index;
    }
}
