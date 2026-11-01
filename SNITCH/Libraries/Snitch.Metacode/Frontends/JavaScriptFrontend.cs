using Acornima;
using Acornima.Ast;

// `Node` is also a namespace in this assembly (`Snitch.Node`), and a namespace member of an enclosing
// namespace wins over a using-alias — so the AST node gets a name of its own.
using AstNode = Acornima.Ast.Node;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads JavaScript source and lowers it into the shared model. It is the JavaScript counterpart of
/// <see cref="CSharpFrontend"/> and <see cref="VisualBasicFrontend"/>: the grammar is not read here, it is
/// read by a real parser (Acornima, the .NET port of acorn, which implements ECMAScript), and what this
/// class does is the lowering — the same thing Roslyn does for C# and CPython's <c>ast</c> module does for
/// Python. That is why the frontend belongs in <see cref="MetacodeTranslator.Default"/>: it reads the
/// source directly, with no dump to produce first.
/// <para>
/// The parse is a module first and a classic script second, so an ES module and a CommonJS file are both
/// read; a file that parses as neither reports one <c>MC0001</c> with the parser's own line and column.
/// A construct the model has no node for — <c>with</c>, <c>debugger</c>, a labeled <c>break</c>, an object
/// spread, a sequence expression — is kept verbatim as <see cref="MetacodeOpaqueStatement"/> /
/// <see cref="MetacodeOpaqueExpression"/> with a diagnostic. The rest of the file still lowers, so one
/// awkward statement costs that statement and not the file.
/// </para>
/// </summary>
public sealed class JavaScriptFrontend : ILanguageFrontend
{
    private static readonly string[] Extensions = { ".js", ".mjs", ".cjs", ".jsx" };

    public string Name => "JavaScript";

    public MetacodeLanguage Language => MetacodeLanguage.JavaScript;

    /// <summary>
    /// Claims the JavaScript language and the extensions a JavaScript file is written with. A
    /// <c>.jsx</c> file is claimed even though this pipeline cannot read it: the parser reports where the
    /// JSX starts, which says more than "no frontend reads this".
    /// </summary>
    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.JavaScript
        || (source.Language is null
            && source.Path is not null
            && Extensions.Any(extension => source.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options) =>
        Translate(source.Text, source.Path ?? source.Name, options.IncludeComments);

    /// <summary>The same translation for a caller that already has the text of a file.</summary>
    public MetacodeDocument Translate(string text, string document, bool includeComments = true)
    {
        var stem = Path.GetFileNameWithoutExtension(document);
        var name = string.IsNullOrWhiteSpace(stem) ? document : stem;

        var diagnostics = new List<MetacodeDiagnostic>();
        var comments = new List<Comment>();

        var program = Parse(text, document, diagnostics, includeComments ? comments : null);
        if (program is null)
            return new MetacodeDocument(name, MetacodeLanguage.JavaScript, Array.Empty<MetacodeModule>(), diagnostics);

        var modules = new JavaScriptLowering(text, document, diagnostics, comments).Modules(program, name);
        return new MetacodeDocument(name, MetacodeLanguage.JavaScript, modules, diagnostics);
    }

    /// <summary>
    /// Parses as a module, then as a classic script. The two differ in what they allow (a top-level
    /// <c>return</c> and <c>with</c> exist only in a script, <c>import</c>/<c>export</c> only in a module),
    /// and a file has already been written by the time it is read, so both are tried before giving up.
    /// </summary>
    private static Program? Parse(
        string text,
        string document,
        List<MetacodeDiagnostic> diagnostics,
        List<Comment>? comments)
    {
        var moduleError = TryParse(text, document, comments, asModule: true);
        if (moduleError.Program is not null)
            return moduleError.Program;

        var scriptError = TryParse(text, document, comments, asModule: false);
        if (scriptError.Program is not null)
            return scriptError.Program;

        // The module attempt is the better error for an ES module, the script attempt for everything else:
        // report whichever got furthest into the file.
        var failure = scriptError.Error is not null && moduleError.Error is not null
            && scriptError.Error.LineNumber >= moduleError.Error.LineNumber
                ? scriptError.Error
                : moduleError.Error;

        var where = failure is null ? string.Empty : $" (line {failure.LineNumber}, column {failure.Column})";
        var reason = failure?.Description ?? "unknown parse failure";
        diagnostics.Add(new MetacodeDiagnostic(
            MetacodeDiagnosticCodes.SyntaxError,
            MetacodeDiagnosticSeverity.Error,
            $"The JavaScript parser could not read this file{where}: {reason}. JSX and TypeScript annotations are not ECMAScript and are not accepted by this reader.",
            new MetacodeOrigin(MetacodeLanguage.JavaScript, document)));

        return null;
    }

    private static (Program? Program, ParseErrorException? Error) TryParse(
        string text,
        string document,
        List<Comment>? comments,
        bool asModule)
    {
        // One list for the whole call: a retry is a new parse, and the comments of the attempt that failed
        // would otherwise be reported twice.
        comments?.Clear();

        // The handler takes the comment by `in` reference, which a lambda cannot express here.
        void Collect(in Comment comment) => comments!.Add(comment);

        var options = new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            AllowHashBang = true,
            // A CommonJS file legitimately returns from the top level and some legacy code still uses `with`.
            AllowReturnOutsideFunction = !asModule,
            OnComment = comments is null ? null : Collect,
        };

        try
        {
            var parser = new Parser(options);
            return (asModule ? parser.ParseModule(text, document) : parser.ParseScript(text, document), null);
        }
        catch (ParseErrorException exception)
        {
            return (null, exception);
        }
    }
}

/// <summary>
/// The lowering: Acornima's tree into the metacode nodes. Every method is total — it returns a node for
/// everything, and where the model has no node it returns the opaque form carrying the source text and
/// records why, so nothing disappears without a diagnostic.
/// </summary>
internal sealed partial class JavaScriptLowering
{
    private readonly string _text;
    private readonly string _document;
    private readonly List<MetacodeDiagnostic> _diagnostics;
    private readonly List<Comment> _comments;
    private int _commentIndex;
    private bool _reportedExportShape;

    public JavaScriptLowering(
        string text,
        string document,
        List<MetacodeDiagnostic> diagnostics,
        List<Comment> comments)
    {
        _text = text;
        _document = document;
        _diagnostics = diagnostics;
        _comments = comments;
    }

    // ── modules ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One module per file. Top-level declarations stay declarations; everything a script executes at the
    /// top level becomes the body of <c>__module__</c>, the same shape the Python frontend uses, because a
    /// module holds declarations and an entry point is the honest way to keep executable code visible.
    /// </summary>
    public IReadOnlyList<MetacodeModule> Modules(Program program, string name)
    {
        var declarations = new List<MetacodeDeclaration>();
        var statements = new List<MetacodeStatement>();

        foreach (var node in program.Body)
        {
            PendingComments(statements, node);

            switch (node)
            {
                case ImportDeclaration import:
                    statements.Add(Import(import));
                    break;

                case ExportNamedDeclaration { Declaration: { } declaration }:
                    DeclarationInto(declaration, declarations, statements);
                    break;

                case ExportDefaultDeclaration exported:
                    ExportDefaultInto(exported, declarations, statements);
                    break;

                case ExportNamedDeclaration or ExportAllDeclaration:
                    statements.Add(ReExport(node));
                    break;

                case VariableDeclaration variable:
                    Bindings(variable, declarations, statements);
                    break;

                case FunctionDeclaration or ClassDeclaration:
                    DeclarationInto(node, declarations, statements);
                    break;

                default:
                    statements.Add(Statement(node));
                    break;
            }
        }

        if (statements.Count > 0)
        {
            declarations.Insert(0, new MetacodeFunction(
                "__module__",
                MetacodeFunctionKind.Function,
                Body: new MetacodeBlock(statements) { Origin = Origin(program) }) { Origin = Origin(program) });
        }

        return new[] { new MetacodeModule(name, declarations) { Origin = Origin(program) } };
    }

    /// <summary>A declaration where a declaration belongs; anything else is a statement of the script.</summary>
    private void DeclarationInto(
        AstNode node,
        List<MetacodeDeclaration> declarations,
        List<MetacodeStatement> statements)
    {
        switch (node)
        {
            case FunctionDeclaration function:
                declarations.Add(Function(function));
                break;

            case ClassDeclaration @class:
                declarations.Add(Class(@class));
                break;

            case VariableDeclaration variable:
                Bindings(variable, declarations, statements);
                break;

            default:
                statements.Add(Statement(node));
                break;
        }
    }

    /// <summary>
    /// A top-level binding is a member of the module, so a plain <c>const MAX = 3</c> becomes a field and
    /// is visible to the index and the complexity report as a declaration. A destructuring declarator has
    /// no single name, so it stays the statement it is.
    /// </summary>
    private void Bindings(
        VariableDeclaration declaration,
        List<MetacodeDeclaration> declarations,
        List<MetacodeStatement> statements)
    {
        var modifiers = declaration.Kind == VariableDeclarationKind.Const
            ? MetacodeModifiers.Const
            : MetacodeModifiers.None;

        foreach (var declarator in declaration.Declarations)
        {
            if (declarator.Id is Identifier identifier)
            {
                declarations.Add(new MetacodeField(
                    identifier.Name,
                    null,
                    modifiers,
                    declarator.Init is null ? null : Expression(declarator.Init)) { Origin = Origin(declarator) });
                continue;
            }

            statements.Add(Local(declaration, declarator));
        }
    }

    // ── declarations ─────────────────────────────────────────────────────────────────

    private MetacodeFunction Function(FunctionDeclaration function)
    {
        if (function.Generator)
            ReportLossy(function, "a generator function is lowered without its generator flag");

        return new MetacodeFunction(
            function.Id?.Name ?? "<anonymous>",
            MetacodeFunctionKind.Function,
            Parameters(function.Params),
            null,
            Block(function.Body),
            function.Async ? MetacodeModifiers.Async : MetacodeModifiers.None) { Origin = Origin(function) };
    }

    private MetacodeTypeDeclaration Class(AstNode node)
    {
        var (body, name, superClass) = node switch
        {
            ClassDeclaration declaration => (
                declaration.Body,
                declaration.Id?.Name ?? "<anonymous>",
                declaration.SuperClass),
            ClassExpression expression => (
                expression.Body,
                expression.Id?.Name ?? "<anonymous>",
                expression.SuperClass),
            _ => ((ClassBody?)null, "<anonymous>", (Expression?)null),
        };

        var baseTypes = superClass is null ? null : new[] { MetacodeTypeRef.Of(Text(superClass)) };

        return new MetacodeTypeDeclaration(
            name,
            MetacodeTypeKind.Class,
            body is null ? Array.Empty<MetacodeDeclaration>() : ClassMembers(body),
            MetacodeModifiers.None,
            baseTypes) { Origin = Origin(node) };
    }

    private List<MetacodeDeclaration> ClassMembers(ClassBody body)
    {
        var members = new List<MetacodeDeclaration>();

        foreach (var member in body.Body)
        {
            switch (member)
            {
                case MethodDefinition definition:
                    Method(definition, members);
                    break;

                case PropertyDefinition field:
                {
                    var fieldName = MemberName(field.Key, field.Computed) ?? Text(field.Key);
                    members.Add(new MetacodeField(
                        fieldName,
                        null,
                        field.Static ? MetacodeModifiers.Static : MetacodeModifiers.None,
                        field.Value is null ? null : Expression(field.Value)) { Origin = Origin(field) });
                    break;
                }

                // `static { … }` runs once when the class is defined; a static constructor is the member
                // with the same role, so it is lowered as one instead of disappearing.
                case StaticBlock staticBlock:
                    members.Add(new MetacodeFunction(
                        "static",
                        MetacodeFunctionKind.Method,
                        null,
                        null,
                        Block(staticBlock),
                        MetacodeModifiers.Static) { Origin = Origin(staticBlock) });
                    break;

                default:
                    members.Add(OpaqueDeclaration(member, "a class member the model does not have"));
                    break;
            }
        }

        return members;
    }

    private void Method(MethodDefinition definition, List<MetacodeDeclaration> members)
    {
        var name = MemberName(definition.Key, definition.Computed) ?? Text(definition.Key);
        if (definition.Computed)
            ReportLossy(definition, $"the computed member name '{name}' is lowered as a plain name");

        var value = definition.Value;
        if (value.Generator)
            ReportLossy(definition, $"the generator method '{name}' is lowered without its generator flag");

        var modifiers = definition.Static ? MetacodeModifiers.Static : MetacodeModifiers.None;
        if (value.Async)
            modifiers |= MetacodeModifiers.Async;

        if (definition.Kind == PropertyKind.Constructor)
        {
            members.Add(new MetacodeFunction(
                value.Id?.Name ?? "constructor",
                MetacodeFunctionKind.Constructor,
                Parameters(value.Params),
                null,
                Block(value.Body),
                modifiers) { Origin = Origin(definition) });
            return;
        }

        // A getter and a setter of the same name are one property with two accessors, which is exactly what
        // the model's MetacodeProperty is: the second accessor fills the first member in.
        if (definition.Kind is PropertyKind.Get or PropertyKind.Set)
        {
            var accessor = new MetacodeAccessor(
                modifiers,
                Block(value.Body),
                definition.Kind == PropertyKind.Set ? Parameters(value.Params).FirstOrDefault()?.Name : null)
            {
                Origin = Origin(definition),
            };

            var existing = members.OfType<MetacodeProperty>().FirstOrDefault(p => p.Name == name);
            if (existing is not null)
            {
                members[members.IndexOf(existing)] = definition.Kind == PropertyKind.Get
                    ? existing with { Getter = accessor }
                    : existing with { Setter = accessor };
                return;
            }

            members.Add(definition.Kind == PropertyKind.Get
                ? new MetacodeProperty(name, null, Modifiers: MetacodeModifiers.None, Getter: accessor)
                {
                    Origin = Origin(definition),
                }
                : new MetacodeProperty(name, null, Modifiers: MetacodeModifiers.None, Setter: accessor)
                {
                    Origin = Origin(definition),
                });
            return;
        }

        members.Add(new MetacodeFunction(
            name,
            MetacodeFunctionKind.Method,
            Parameters(value.Params),
            null,
            Block(value.Body),
            modifiers) { Origin = Origin(definition) });
    }

    private void ExportDefaultInto(
        ExportDefaultDeclaration exported,
        List<MetacodeDeclaration> declarations,
        List<MetacodeStatement> statements)
    {
        switch (exported.Declaration)
        {
            case FunctionDeclaration function:
                declarations.Add(Function(function));
                break;

            case ClassDeclaration @class:
                declarations.Add(Class(@class));
                break;

            // `export default 42` is a binding of the module with no name of its own: a field called
            // `default` keeps it visible instead of dropping it.
            case Expression expression:
                declarations.Add(new MetacodeField("default", null, MetacodeModifiers.None, Expression(expression))
                {
                    Origin = Origin(exported),
                });
                break;

            case Statement statement:
                statements.Add(Statement(statement));
                break;

            default:
                statements.Add(Opaque(exported, "an export default the model cannot place"));
                break;
        }
    }

    // ── statements ──────────────────────────────────────────────────────────────────

    private List<MetacodeStatement> Statements(NodeList<Statement> body)
    {
        var statements = new List<MetacodeStatement>(body.Count);
        foreach (var statement in body)
        {
            PendingComments(statements, statement);
            statements.Add(Statement(statement));
        }

        return statements;
    }

    private MetacodeStatement Statement(AstNode node) => node switch
    {
        BlockStatement block => Block(block),
        EmptyStatement => MetacodeBlock.Empty,
        ExpressionStatement statement => ExpressionStatement(statement),
        VariableDeclaration declaration => Declarators(declaration),
        ReturnStatement @return => new MetacodeReturn(
            @return.Argument is null ? null : Expression(@return.Argument)) { Origin = Origin(node) },
        IfStatement @if => new MetacodeIf(
            Expression(@if.Test),
            Block(@if.Consequent),
            @if.Alternate is null ? null : Block(@if.Alternate)) { Origin = Origin(node) },
        WhileStatement @while => new MetacodeWhile(
            Expression(@while.Test),
            Block(@while.Body)) { Origin = Origin(node) },
        DoWhileStatement @do => new MetacodeDoWhile(
            Block(@do.Body),
            Expression(@do.Test)) { Origin = Origin(node) },
        ForStatement @for => For(@for),
        ForOfStatement forOf => ForOf(forOf),
        ForInStatement forIn => ForIn(forIn),
        BreakStatement @break => Break(@break),
        ContinueStatement @continue => Continue(@continue),
        LabeledStatement labeled => new MetacodeLabel(labeled.Label.Name, Statement(labeled.Body))
        {
            Origin = Origin(node),
        },
        SwitchStatement @switch => Switch(@switch),
        ThrowStatement @throw => new MetacodeThrow(
            @throw.Argument is null ? null : Expression(@throw.Argument)) { Origin = Origin(node) },
        TryStatement @try => Try(@try),
        FunctionDeclaration function => new MetacodeLocalFunction(Function(function)) { Origin = Origin(node) },
        ClassDeclaration nested => Opaque(nested, "a class declared inside a body is not modelled as a statement"),
        WithStatement => Opaque(node, "`with` has no node in the model"),
        DebuggerStatement => Opaque(node, "`debugger` has no node in the model"),
        _ => Opaque(node, "a statement the model does not have"),
    };

    private MetacodeStatement ExpressionStatement(ExpressionStatement statement)
    {
        // `yield x` is an expression in JavaScript and a statement in the model, so the wrapper decides.
        if (statement.Expression is YieldExpression yield)
            return Yield(yield);

        return ExpressionAsStatement(statement.Expression, Origin(statement));
    }

    /// <summary>
    /// An expression used as a statement. An assignment is the model's assignment, and <c>i++</c> is
    /// <c>i += 1</c> — the C# frontend lowers it the same way, which is what keeps the same loop printing
    /// the same lines whichever language it was written in.
    /// </summary>
    private MetacodeStatement ExpressionAsStatement(Expression expression, MetacodeOrigin origin)
    {
        switch (expression)
        {
            case AssignmentExpression assignment:
                return Assignment(assignment);

            case UpdateExpression update:
                return new MetacodeAssignment(
                    Expression(update.Argument),
                    update.Operator == Operator.Increment
                        ? MetacodeAssignmentOperator.Add
                        : MetacodeAssignmentOperator.Subtract,
                    MetacodeLiteral.Number("1")) { Origin = origin };
        }

        return new MetacodeExpressionStatement(Expression(expression)) { Origin = origin };
    }

    private MetacodeStatement Yield(YieldExpression yield)
    {
        if (yield.Delegate)
            ReportLossy(yield, "`yield*` is lowered as a plain yield and loses the delegation");

        return new MetacodeYield(yield.Argument is null ? null : Expression(yield.Argument)) { Origin = Origin(yield) };
    }

    private MetacodeStatement Assignment(AssignmentExpression assignment)
    {
        // `&&=` and `||=` have no assignment operator in the model; `x &&= y` is `x = x && y`, which is
        // the same program when the target is a reference, so it is desugared rather than lost.
        if (assignment.Operator is Operator.LogicalAndAssignment or Operator.LogicalOrAssignment)
        {
            var logical = assignment.Operator == Operator.LogicalAndAssignment
                ? MetacodeBinaryOperator.LogicalAnd
                : MetacodeBinaryOperator.LogicalOr;

            return new MetacodeAssignment(
                Expression(assignment.Left),
                MetacodeAssignmentOperator.Assign,
                new MetacodeBinary(logical, Expression(assignment.Left), Expression(assignment.Right))
                {
                    Origin = Origin(assignment),
                }) { Origin = Origin(assignment) };
        }

        return new MetacodeAssignment(
            Expression(assignment.Left),
            AssignmentOperator(assignment.Operator),
            Expression(assignment.Right)) { Origin = Origin(assignment) };
    }

    /// <summary>One local per declarator; several declarators are several statements.</summary>
    private MetacodeStatement Declarators(VariableDeclaration declaration)
    {
        var locals = new List<MetacodeStatement>(declaration.Declarations.Count);
        foreach (var declarator in declaration.Declarations)
            locals.Add(Local(declaration, declarator));

        return locals.Count == 1
            ? locals[0]
            : new MetacodeBlock(locals) { Origin = Origin(declaration) };
    }

    private MetacodeLocalVariable Local(VariableDeclaration declaration, VariableDeclarator declarator) =>
        new(
            declarator.Id is Identifier identifier ? identifier.Name : PatternText(declarator.Id),
            null,
            declarator.Init is null ? null : Expression(declarator.Init),
            declaration.Kind == VariableDeclarationKind.Const) { Origin = Origin(declarator) };

    private MetacodeStatement For(ForStatement @for)
    {
        if (@for.Init is VariableDeclaration { Declarations.Count: > 1 })
            return Opaque(@for, "a for-statement that declares more than one variable has no single initializer");

        // A block initializer would print a multi-line block inside the FOR header, so only a single
        // statement is accepted there; anything else stays opaque rather than rendering as nonsense.
        var initializer = @for.Init switch
        {
            null => null,
            VariableDeclaration declaration => Declarators(declaration),
            Expression expression => ExpressionAsStatement(expression, Origin(expression)),
            _ => null,
        };

        if (@for.Init is not null
            && initializer is not (MetacodeLocalVariable or MetacodeExpressionStatement or MetacodeAssignment))
        {
            return Opaque(@for, "a for-initializer the model cannot place in the loop header");
        }

        var increment = @for.Update is null
            ? null
            : ExpressionAsStatement(@for.Update, Origin(@for.Update));

        return new MetacodeFor(initializer, @for.Test is null ? null : Expression(@for.Test), increment, Block(@for.Body))
        {
            Origin = Origin(@for),
        };
    }

    private MetacodeStatement ForOf(ForOfStatement forOf) =>
        new MetacodeForEach(
            LoopVariable(forOf.Left),
            null,
            Expression(forOf.Right),
            Block(forOf.Body),
            forOf.Await) { Origin = Origin(forOf) };

    private MetacodeStatement ForIn(ForInStatement forIn)
    {
        // `for (const k in xs)` walks the keys, `for (const v of xs)` the values. The model has one
        // foreach, so the difference is said out loud instead of being printed as if it were not there.
        ReportLossy(forIn, "`for … in` iterates the keys; the model's FOREACH reads values");

        return new MetacodeForEach(
            LoopVariable(forIn.Left),
            null,
            Expression(forIn.Right),
            Block(forIn.Body)) { Origin = Origin(forIn) };
    }

    private string LoopVariable(AstNode left) => left switch
    {
        VariableDeclaration { Declarations.Count: 1 } declaration => declaration.Declarations[0].Id is Identifier
            ? ((Identifier)declaration.Declarations[0].Id).Name
            : PatternText(declaration.Declarations[0].Id),
        VariableDeclaration declaration => PatternText(declaration),
        Identifier identifier => identifier.Name,
        _ => PatternText(left),
    };

    private MetacodeStatement Break(BreakStatement @break)
    {
        if (@break.Label is null)
            return new MetacodeBreak { Origin = Origin(@break) };

        ReportLossy(@break, $"the labeled break to '{@break.Label.Name}' is kept as text");
        return Opaque(@break, "a labeled break has no node in the model");
    }

    private MetacodeStatement Continue(ContinueStatement @continue)
    {
        if (@continue.Label is null)
            return new MetacodeContinue { Origin = Origin(@continue) };

        ReportLossy(@continue, $"the labeled continue to '{@continue.Label.Name}' is kept as text");
        return Opaque(@continue, "a labeled continue has no node in the model");
    }

    private MetacodeStatement Switch(SwitchStatement @switch)
    {
        var cases = new List<MetacodeSwitchCase>();
        var labels = new List<MetacodeExpression>();
        var index = 0;

        foreach (var @case in @switch.Cases)
        {
            index++;
            if (@case.Test is null)
            {
                cases.Add(new MetacodeSwitchCase(
                    labels.Count == 0 ? null : labels.ToList(),
                    new MetacodeBlock(Statements(@case.Consequent)) { Origin = Origin(@case) },
                    IsDefault: true) { Origin = Origin(@case) });
                labels.Clear();
                continue;
            }

            labels.Add(Expression(@case.Test));

            // `case 1: case 2: …` is one branch with two labels; an empty consequent is the fall-through
            // that spells it, and merging the labels is what prints it back the same way.
            if (@case.Consequent.Count == 0)
                continue;

            if (index < @switch.Cases.Count && !Terminates(@case.Consequent))
                ReportLossy(@case, "this case falls through; the model prints each case as its own branch");

            cases.Add(new MetacodeSwitchCase(
                labels.ToList(),
                new MetacodeBlock(Statements(@case.Consequent)) { Origin = Origin(@case) }) { Origin = Origin(@case) });
            labels.Clear();
        }

        if (labels.Count > 0)
            cases.Add(new MetacodeSwitchCase(labels, MetacodeBlock.Empty) { Origin = Origin(@switch) });

        return new MetacodeSwitch(Expression(@switch.Discriminant), cases) { Origin = Origin(@switch) };
    }

    private static bool Terminates(NodeList<Statement> statements) =>
        statements.Count > 0
        && statements[^1] is BreakStatement or ReturnStatement or ThrowStatement or ContinueStatement;

    private MetacodeStatement Try(TryStatement @try)
    {
        var catches = new List<MetacodeCatch>();
        if (@try.Handler is { } handler)
        {
            catches.Add(new MetacodeCatch(
                null,
                handler.Param is Identifier identifier ? identifier.Name : handler.Param is null ? null : PatternText(handler.Param),
                Block(handler.Body)) { Origin = Origin(handler) });
        }

        return new MetacodeTry(
            Block(@try.Block),
            catches,
            @try.Finalizer is null ? null : Block(@try.Finalizer)) { Origin = Origin(@try) };
    }

    private MetacodeBlock Block(AstNode? node) => node switch
    {
        null => MetacodeBlock.Empty,
        BlockStatement block => new MetacodeBlock(Statements(block.Body)) { Origin = Origin(block) },
        _ => new MetacodeBlock(new[] { Statement(node) }) { Origin = Origin(node) },
    };

    // ── imports and exports ─────────────────────────────────────────────────────────

    private MetacodeImport Import(ImportDeclaration declaration)
    {
        var names = new List<MetacodeImportName>();

        foreach (var specifier in declaration.Specifiers)
        {
            switch (specifier)
            {
                case ImportDefaultSpecifier @default:
                    names.Add(new MetacodeImportName("default", @default.Local.Name));
                    break;

                case ImportNamespaceSpecifier @namespace:
                    names.Add(new MetacodeImportName("*", @namespace.Local.Name));
                    break;

                case ImportSpecifier named:
                {
                    var imported = named.Imported is Identifier identifier ? identifier.Name : Text(named.Imported);
                    names.Add(new MetacodeImportName(
                        imported,
                        string.Equals(imported, named.Local.Name, StringComparison.Ordinal) ? null : named.Local.Name));
                    break;
                }
            }
        }

        return new MetacodeImport(names, ModuleSpecifier(declaration.Source), IsFrom: true)
        {
            Origin = Origin(declaration),
        };
    }

    /// <summary>
    /// A re-export carries the names a module hands on. The model's only "names crossing a module
    /// boundary" node is an import, so that is what it is lowered to; one informational diagnostic per file
    /// says that the direction is not in the model rather than leaving the reader to guess.
    /// </summary>
    private MetacodeStatement ReExport(AstNode node)
    {
        var names = new List<MetacodeImportName>();
        string module;

        if (node is ExportAllDeclaration all)
        {
            names.Add(all.Exported is Identifier exported
                ? new MetacodeImportName("*", exported.Name)
                : new MetacodeImportName("*"));
            module = ModuleSpecifier(all.Source);
        }
        else
        {
            var named = (ExportNamedDeclaration)node;
            foreach (var specifier in named.Specifiers.OfType<ExportSpecifier>())
            {
                var local = specifier.Local is Identifier identifier ? identifier.Name : Text(specifier.Local);
                var exported = specifier.Exported is Identifier target ? target.Name : Text(specifier.Exported);
                names.Add(new MetacodeImportName(
                    local,
                    string.Equals(local, exported, StringComparison.Ordinal) ? null : exported));
            }

            module = named.Source is null ? "." : ModuleSpecifier(named.Source);
        }

        if (!_reportedExportShape)
        {
            _reportedExportShape = true;
            _diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.LossyLowering,
                MetacodeDiagnosticSeverity.Info,
                "A re-export is lowered as the import it mirrors: the model records the names that cross the module boundary, not the direction.",
                Origin(node)));
        }

        return new MetacodeImport(names, module, IsFrom: true) { Origin = Origin(node) };
    }

    private static string ModuleSpecifier(StringLiteral source) =>
        string.IsNullOrEmpty(source.Raw) ? "'" + source.Value + "'" : source.Raw;

    // ── comments ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Comments the parser skipped between the previous statement and this one, emitted as comment
    /// statements. The cursor only moves forward, so every comment is printed once; a comment inside an
    /// expression surfaces before the next statement, which is where a line-based model can place it.
    /// </summary>
    private void PendingComments(List<MetacodeStatement> into, AstNode node)
    {
        while (_commentIndex < _comments.Count && _comments[_commentIndex].End <= node.Start)
        {
            var comment = _comments[_commentIndex++];
            if (comment.Kind == CommentKind.HashBang)
                continue;

            var content = comment.GetContent(_text).ToString();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            into.Add(new MetacodeComment(OneLine(content)) { Origin = Origin(comment.Location) });
        }
    }

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
