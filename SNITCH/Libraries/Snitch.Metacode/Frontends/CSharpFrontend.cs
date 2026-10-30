using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads C# syntax trees. It uses only Roslyn's syntax layer — no compilation, no workspace — so a
/// translation costs a parse and nothing else. Anything the model has no node for becomes an opaque
/// node plus a diagnostic instead of disappearing.
/// </summary>
public sealed class CSharpFrontend : ILanguageFrontend
{
    public string Name => "C#";

    public MetacodeLanguage Language => MetacodeLanguage.CSharp;

    public bool CanHandle(MetacodeSource source) =>
        source.Roslyn is not null
        || source.Language == MetacodeLanguage.CSharp
        || (source.Language is null && source.Path is not null
            && source.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var tree = source.Roslyn ?? CSharpSyntaxTree.ParseText(
            source.Text,
            new CSharpParseOptions(LanguageVersion.Preview),
            source.Path ?? string.Empty);

        var diagnostics = new List<MetacodeDiagnostic>();
        foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.SyntaxError,
                MetacodeDiagnosticSeverity.Error,
                error.GetMessage(),
                CSharpLowering.OriginOf(error.Location)));
        }

        var lowering = new CSharpLowering(source.Name, options, diagnostics);
        var modules = lowering.Build((CompilationUnitSyntax)tree.GetRoot());
        return new MetacodeDocument(source.Name, MetacodeLanguage.CSharp, modules, diagnostics);
    }
}

/// <summary>The one translation in flight: the options, the diagnostics it produced, and the builders.</summary>
internal sealed partial class CSharpLowering
{
    private readonly string _name;
    private readonly MetacodeOptions _options;
    private readonly List<MetacodeDiagnostic> _diagnostics;

    public CSharpLowering(string name, MetacodeOptions options, List<MetacodeDiagnostic> diagnostics)
    {
        _name = name;
        _options = options;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<MetacodeModule> Build(CompilationUnitSyntax root)
    {
        var modules = new List<MetacodeModule>();
        var loose = new List<MetacodeDeclaration>();
        var topLevel = new List<MetacodeStatement>();

        foreach (var member in root.Members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax @namespace:
                    CollectNamespace(@namespace, string.Empty, modules);
                    break;
                case GlobalStatementSyntax global:
                    topLevel.Add(Statement(global.Statement));
                    break;
                default:
                    loose.AddRange(Declarations(new[] { member }));
                    break;
            }
        }

        if (loose.Count > 0)
            modules.Insert(0, new MetacodeModule(_name, loose) { Origin = OriginOf(root) });

        if (topLevel.Count > 0)
        {
            modules.Add(new MetacodeModule(
                "<statements>",
                new MetacodeDeclaration[]
                {
                    new MetacodeFunction(
                        "Main",
                        MetacodeFunctionKind.Function,
                        Body: new MetacodeBlock(topLevel),
                        Modifiers: MetacodeModifiers.Static) { Origin = OriginOf(root) },
                }) { Origin = OriginOf(root) });
        }

        return modules;
    }

    private void CollectNamespace(BaseNamespaceDeclarationSyntax @namespace, string prefix, List<MetacodeModule> modules)
    {
        var name = prefix.Length == 0 ? @namespace.Name.ToString() : prefix + "." + @namespace.Name;
        var declarations = new List<MetacodeDeclaration>();
        var nested = new List<BaseNamespaceDeclarationSyntax>();

        foreach (var member in @namespace.Members)
        {
            if (member is BaseNamespaceDeclarationSyntax inner)
                nested.Add(inner);
            else
                declarations.AddRange(Declarations(new[] { member }));
        }

        modules.Add(new MetacodeModule(name, declarations, MetacodeModuleKind.Namespace) { Origin = OriginOf(@namespace) });

        foreach (var inner in nested)
            CollectNamespace(inner, name, modules);
    }

    private IEnumerable<MetacodeDeclaration> Declarations(IEnumerable<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                        yield return Field(field, variable);
                    break;

                case EventFieldDeclarationSyntax @event:
                    foreach (var variable in @event.Declaration.Variables)
                        yield return new MetacodeEvent(
                            variable.Identifier.Text,
                            TypeRef(@event.Declaration.Type),
                            Modifiers(@event.Modifiers)) { Origin = OriginOf(variable) };
                    break;

                case EventDeclarationSyntax @event:
                    yield return new MetacodeEvent(
                        @event.Identifier.Text,
                        TypeRef(@event.Type),
                        Modifiers(@event.Modifiers)) { Origin = OriginOf(@event) };
                    break;

                case DelegateDeclarationSyntax:
                    yield return Opaque(member, "a delegate is a signature, not a body");
                    break;

                case BaseTypeDeclarationSyntax type:
                    yield return Type(type);
                    break;

                case MethodDeclarationSyntax method:
                    yield return Method(method);
                    break;

                case ConstructorDeclarationSyntax constructor:
                    yield return Constructor(constructor);
                    break;

                case DestructorDeclarationSyntax destructor:
                    yield return new MetacodeFunction(
                        destructor.Identifier.Text,
                        MetacodeFunctionKind.Destructor,
                        Body: Block(destructor.Body),
                        Modifiers: Modifiers(destructor.Modifiers)) { Origin = OriginOf(destructor) };
                    break;

                case OperatorDeclarationSyntax @operator:
                    yield return new MetacodeFunction(
                        "operator " + @operator.OperatorToken.Text,
                        MetacodeFunctionKind.Operator,
                        Parameters(@operator.ParameterList),
                        TypeRef(@operator.ReturnType),
                        Body(@operator.Body, @operator.ExpressionBody, @operator.ReturnType),
                        Modifiers(@operator.Modifiers)) { Origin = OriginOf(@operator) };
                    break;

                case ConversionOperatorDeclarationSyntax conversion:
                    yield return Opaque(conversion, "a user-defined conversion is not modelled");
                    break;

                case PropertyDeclarationSyntax property:
                    yield return Property(property);
                    break;

                case IndexerDeclarationSyntax indexer:
                    yield return Indexer(indexer);
                    break;

                default:
                    yield return Opaque(member, "unmodelled declaration: " + member.Kind());
                    break;
            }
        }
    }

    private MetacodeDeclaration Type(BaseTypeDeclarationSyntax node)
    {
        var kind = node switch
        {
            InterfaceDeclarationSyntax => MetacodeTypeKind.Interface,
            StructDeclarationSyntax => MetacodeTypeKind.Struct,
            EnumDeclarationSyntax => MetacodeTypeKind.Enum,
            RecordDeclarationSyntax => MetacodeTypeKind.Record,
            _ => MetacodeTypeKind.Class,
        };

        var members = node switch
        {
            EnumDeclarationSyntax enumeration => enumeration.Members
                .Select(m => (MetacodeDeclaration)new MetacodeEnumMember(
                    m.Identifier.Text,
                    m.EqualsValue is null ? null : Expression(m.EqualsValue.Value)) { Origin = OriginOf(m) })
                .ToList(),
            TypeDeclarationSyntax declaration => Declarations(declaration.Members).ToList(),
            _ => new List<MetacodeDeclaration>(),
        };

        var typeParameters = (node as TypeDeclarationSyntax) is { } declarationWithParameters && declarationWithParameters.TypeParameterList is not null
            ? declarationWithParameters.TypeParameterList.Parameters
                .Select(p => TypeParameter(p, declarationWithParameters.ConstraintClauses))
                .ToList()
            : null;

        return new MetacodeTypeDeclaration(
            node.Identifier.Text,
            kind,
            members,
            Modifiers(node.Modifiers),
            node.BaseList?.Types.Select(b => TypeRef(b.Type)).ToList(),
            typeParameters) { Origin = OriginOf(node) };
    }

    private MetacodeFunction Method(MethodDeclarationSyntax node) => new(
        node.Identifier.Text,
        MetacodeFunctionKind.Method,
        Parameters(node.ParameterList),
        TypeRef(node.ReturnType),
        Body(node.Body, node.ExpressionBody, node.ReturnType),
        Modifiers(node.Modifiers),
        TypeParameters(node.TypeParameterList, node.ConstraintClauses)) { Origin = OriginOf(node) };

    private MetacodeFunction Constructor(ConstructorDeclarationSyntax node)
    {
        // `public Order(string c) => Customer = c;` assigns, it does not return.
        var body = node.Body is not null
            ? Block(node.Body)
            : node.ExpressionBody is null
                ? null
                : MetacodeBlock.Of(StatementFromExpression(node.ExpressionBody.Expression, OriginOf(node.ExpressionBody)));

        if (node.Initializer is not null)
        {
            var call = new MetacodeExpressionStatement(new MetacodeInvocation(
                new MetacodeIdentifier(node.Initializer.ThisOrBaseKeyword.Text) { Origin = OriginOf(node.Initializer) },
                Arguments(node.Initializer.ArgumentList))) { Origin = OriginOf(node.Initializer) };
            body = body is null
                ? MetacodeBlock.Of(call)
                : body with { Statements = new[] { call }.Concat(body.Statements).ToList() };
        }

        return new MetacodeFunction(
            node.Identifier.Text,
            MetacodeFunctionKind.Constructor,
            Parameters(node.ParameterList),
            Body: body,
            Modifiers: Modifiers(node.Modifiers)) { Origin = OriginOf(node) };
    }

    private MetacodeProperty Property(PropertyDeclarationSyntax node)
    {
        var (getter, setter) = Accessors(node.AccessorList, node.ExpressionBody);
        return new MetacodeProperty(
            node.Identifier.Text,
            TypeRef(node.Type),
            Modifiers(node.Modifiers),
            getter,
            setter,
            node.Initializer is null ? null : Expression(node.Initializer.Value)) { Origin = OriginOf(node) };
    }

    private MetacodeProperty Indexer(IndexerDeclarationSyntax node)
    {
        var (getter, setter) = Accessors(node.AccessorList, node.ExpressionBody);
        return new MetacodeProperty(
            "this",
            TypeRef(node.Type),
            Modifiers(node.Modifiers),
            getter,
            setter,
            Parameters: Parameters(node.ParameterList)) { Origin = OriginOf(node) };
    }

    private (MetacodeAccessor?, MetacodeAccessor?) Accessors(AccessorListSyntax? list, ArrowExpressionClauseSyntax? arrow)
    {
        if (list is null)
        {
            return arrow is null
                ? (null, null)
                : (new MetacodeAccessor(Body: MetacodeBlock.Of(Return(arrow.Expression))) { Origin = OriginOf(arrow) }, null);
        }

        MetacodeAccessor? getter = null;
        MetacodeAccessor? setter = null;
        foreach (var accessor in list.Accessors)
        {
            var lowered = new MetacodeAccessor(
                Modifiers(accessor.Modifiers),
                AccessorBody(accessor),
                accessor.IsKind(SyntaxKind.SetAccessorDeclaration) || accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ? "value" : null)
            {
                Origin = OriginOf(accessor),
            };

            if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                getter = lowered;
            else if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration) || accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
                setter = lowered;
            else
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedDeclaration, "accessors: " + accessor.Kind(), accessor));
        }

        return (getter, setter);
    }

    private MetacodeBlock? AccessorBody(AccessorDeclarationSyntax accessor)
    {
        if (accessor.Body is not null)
            return Block(accessor.Body);
        return accessor.ExpressionBody is null ? null : MetacodeBlock.Of(Return(accessor.ExpressionBody.Expression));
    }

    private MetacodeField Field(FieldDeclarationSyntax node, VariableDeclaratorSyntax variable) => new(
        variable.Identifier.Text,
        TypeRef(node.Declaration.Type),
        Modifiers(node.Modifiers),
        variable.Initializer is null ? null : Expression(variable.Initializer.Value)) { Origin = OriginOf(variable) };

    private MetacodeDeclaration Opaque(SyntaxNode node, string reason)
    {
        _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedDeclaration, reason, node));
        return new MetacodeOpaqueDeclaration(MetacodeLanguage.CSharp, node.ToString(), reason) { Origin = OriginOf(node) };
    }

    private MetacodeBlock? Body(BlockSyntax? block, ArrowExpressionClauseSyntax? arrow, TypeSyntax? returnType)
    {
        if (block is not null)
            return Block(block);
        if (arrow is null)
            return null;

        var isVoid = returnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword };
        MetacodeStatement statement = isVoid
            ? new MetacodeExpressionStatement(Expression(arrow.Expression)) { Origin = OriginOf(arrow) }
            : Return(arrow.Expression);
        return MetacodeBlock.Of(statement);
    }

    private MetacodeReturn Return(ExpressionSyntax expression) =>
        new(Expression(expression)) { Origin = OriginOf(expression) };

    private IReadOnlyList<MetacodeParameter> Parameters(BaseParameterListSyntax? list) =>
        list is null ? Array.Empty<MetacodeParameter>() : list.Parameters.Select(Parameter).ToList();

    private MetacodeParameter Parameter(ParameterSyntax parameter) => new(
        parameter.Identifier.Text,
        parameter.Type is null ? null : TypeRef(parameter.Type),
        parameter.Default is null ? null : Expression(parameter.Default.Value),
        parameter.Modifiers.Any(SyntaxKind.RefKeyword) ? MetacodeParameterKind.Ref
        : parameter.Modifiers.Any(SyntaxKind.OutKeyword) ? MetacodeParameterKind.Out
        : parameter.Modifiers.Any(SyntaxKind.InKeyword) ? MetacodeParameterKind.In
        : parameter.Modifiers.Any(SyntaxKind.ParamsKeyword) ? MetacodeParameterKind.Params
        : MetacodeParameterKind.Value) { Origin = OriginOf(parameter) };

    private IReadOnlyList<MetacodeArgument> Arguments(ArgumentListSyntax? list) =>
        list is null
            ? Array.Empty<MetacodeArgument>()
            : list.Arguments.Select(a => new MetacodeArgument(
                a.NameColon?.Name.Identifier.Text,
                Expression(a.Expression),
                a.RefKindKeyword.Kind() switch
                {
                    SyntaxKind.RefKeyword => MetacodeParameterKind.Ref,
                    SyntaxKind.OutKeyword => MetacodeParameterKind.Out,
                    SyntaxKind.InKeyword => MetacodeParameterKind.In,
                    _ => MetacodeParameterKind.Value,
                })).ToList();

    private IReadOnlyList<MetacodeTypeParameter>? TypeParameters(TypeParameterListSyntax? list, SyntaxList<TypeParameterConstraintClauseSyntax> clauses)
    {
        if (list is null)
            return null;

        return list.Parameters.Select(p => TypeParameter(p, clauses)).ToList();
    }

    private static MetacodeTypeParameter TypeParameter(TypeParameterSyntax parameter, SyntaxList<TypeParameterConstraintClauseSyntax> clauses)
    {
        var clause = clauses.FirstOrDefault(c => c.Name.Identifier.Text == parameter.Identifier.Text);
        return new MetacodeTypeParameter(
            parameter.Identifier.Text,
            clause?.Constraints.Select(c => c.ToString()).ToList());
    }

    private static MetacodeTypeRef TypeRef(TypeSyntax type) => type switch
    {
        PredefinedTypeSyntax predefined => new MetacodeTypeRef(predefined.Keyword.Text),
        IdentifierNameSyntax identifier when identifier.Identifier.Text == "var" => MetacodeTypeRef.Dynamic,
        IdentifierNameSyntax identifier => new MetacodeTypeRef(identifier.Identifier.Text),
        GenericNameSyntax generic => new MetacodeTypeRef(
            generic.Identifier.Text,
            generic.TypeArgumentList.Arguments.Select(TypeRef).ToList()),
        ArrayTypeSyntax array => TypeRef(array.ElementType) with { IsArray = true },
        NullableTypeSyntax nullable => TypeRef(nullable.ElementType) with { IsNullable = true },
        RefTypeSyntax reference => TypeRef(reference.Type) with { IsReference = true },
        PointerTypeSyntax pointer => TypeRef(pointer.ElementType) with { IsPointer = true },
        _ => new MetacodeTypeRef(type.ToString()),
    };

    private static MetacodeModifiers Modifiers(SyntaxTokenList tokens)
    {
        var result = MetacodeModifiers.None;
        foreach (var token in tokens)
        {
            result |= token.Kind() switch
            {
                SyntaxKind.PublicKeyword => MetacodeModifiers.Public,
                SyntaxKind.ProtectedKeyword => MetacodeModifiers.Protected,
                SyntaxKind.InternalKeyword => MetacodeModifiers.Internal,
                SyntaxKind.PrivateKeyword => MetacodeModifiers.Private,
                SyntaxKind.StaticKeyword => MetacodeModifiers.Static,
                SyntaxKind.AbstractKeyword => MetacodeModifiers.Abstract,
                SyntaxKind.VirtualKeyword => MetacodeModifiers.Virtual,
                SyntaxKind.OverrideKeyword => MetacodeModifiers.Override,
                SyntaxKind.SealedKeyword => MetacodeModifiers.Sealed,
                SyntaxKind.ReadOnlyKeyword => MetacodeModifiers.ReadOnly,
                SyntaxKind.ConstKeyword => MetacodeModifiers.Const,
                SyntaxKind.AsyncKeyword => MetacodeModifiers.Async,
                SyntaxKind.PartialKeyword => MetacodeModifiers.Partial,
                SyntaxKind.ExternKeyword => MetacodeModifiers.Extern,
                SyntaxKind.UnsafeKeyword => MetacodeModifiers.Unsafe,
                SyntaxKind.NewKeyword => MetacodeModifiers.New,
                _ => MetacodeModifiers.None,
            };
        }

        return result;
    }

    private MetacodeBlock Block(BlockSyntax? block) =>
        block is null ? MetacodeBlock.Empty : BlockOf(block.Statements, OriginOf(block));

    private MetacodeBlock BlockOf(IReadOnlyList<StatementSyntax> list, MetacodeOrigin origin)
    {
        var statements = new List<MetacodeStatement>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var statement = list[i];
            if (_options.IncludeComments)
                statements.AddRange(LeadingComments(statement));

            // `using var x = ...;` scopes the rest of the block, which is exactly what MetacodeUsing says.
            if (statement is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } declaration
                && declaration.Declaration.Variables.Count == 1)
            {
                var rest = BlockOf(list.Skip(i + 1).ToList(), origin);
                statements.Add(new MetacodeUsing(LocalVariable(declaration.Declaration), null, rest) { Origin = OriginOf(statement) });
                return new MetacodeBlock(statements) { Origin = origin };
            }

            // A multi-declarator local is several statements, and it belongs spliced into its block.
            var lowered = Statement(statement);
            if (lowered is MetacodeBlock splice)
                statements.AddRange(splice.Statements);
            else
                statements.Add(lowered);
        }

        return new MetacodeBlock(statements) { Origin = origin };
    }

    private IEnumerable<MetacodeStatement> LeadingComments(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
                yield return new MetacodeComment(trivia.ToString().Trim('/', ' ')) { Origin = OriginOf(node) };
            else if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                yield return new MetacodeComment(trivia.ToString().Trim('/', '*', ' ')) { Origin = OriginOf(node) };
        }
    }

    private MetacodeBlock AsBlock(StatementSyntax statement) =>
        statement is BlockSyntax block ? Block(block) : MetacodeBlock.Of(Statement(statement));

    private MetacodeStatement Statement(StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockSyntax block:
                return Block(block);

            case LocalDeclarationStatementSyntax local:
                return Locals(local);

            case ExpressionStatementSyntax expression:
                return ExpressionStatement(expression);

            case IfStatementSyntax @if:
                return If(@if);

            case WhileStatementSyntax @while:
                return new MetacodeWhile(Expression(@while.Condition), AsBlock(@while.Statement)) { Origin = OriginOf(@while) };

            case DoStatementSyntax @do:
                return new MetacodeDoWhile(AsBlock(@do.Statement), Expression(@do.Condition)) { Origin = OriginOf(@do) };

            case ForStatementSyntax @for:
                return For(@for);

            case ForEachStatementSyntax @foreach:
                return new MetacodeForEach(
                    @foreach.Identifier.Text,
                    TypeRef(@foreach.Type),
                    Expression(@foreach.Expression),
                    AsBlock(@foreach.Statement),
                    @foreach.AwaitKeyword.RawKind != 0) { Origin = OriginOf(@foreach) };

            case ForEachVariableStatementSyntax @foreach:
                // `foreach (var (key, value) in pairs)`: the loop variable is a designation, not a name.
                return new MetacodeForEach(
                    VariableText(@foreach.Variable),
                    null,
                    Expression(@foreach.Expression),
                    AsBlock(@foreach.Statement),
                    @foreach.AwaitKeyword.RawKind != 0) { Origin = OriginOf(@foreach) };

            case SwitchStatementSyntax @switch:
                return Switch(@switch);

            case TryStatementSyntax @try:
                return Try(@try);

            case ReturnStatementSyntax @return:
                return new MetacodeReturn(@return.Expression is null ? null : Expression(@return.Expression)) { Origin = OriginOf(@return) };

            case ThrowStatementSyntax @throw:
                return new MetacodeThrow(@throw.Expression is null ? null : Expression(@throw.Expression)) { Origin = OriginOf(@throw) };

            case BreakStatementSyntax:
                return new MetacodeBreak { Origin = OriginOf(statement) };

            case ContinueStatementSyntax:
                return new MetacodeContinue { Origin = OriginOf(statement) };

            case GotoStatementSyntax @goto when @goto.IsKind(SyntaxKind.GotoStatement):
                return new MetacodeGoto(@goto.Expression?.ToString() ?? string.Empty) { Origin = OriginOf(@goto) };

            case LabeledStatementSyntax label:
                return new MetacodeLabel(label.Identifier.Text, Statement(label.Statement)) { Origin = OriginOf(label) };

            case UsingStatementSyntax @using:
                return Using(@using);

            case LocalFunctionStatementSyntax localFunction:
                return new MetacodeLocalFunction(new MetacodeFunction(
                    localFunction.Identifier.Text,
                    MetacodeFunctionKind.LocalFunction,
                    Parameters(localFunction.ParameterList),
                    TypeRef(localFunction.ReturnType),
                    Body(localFunction.Body, localFunction.ExpressionBody, localFunction.ReturnType),
                    Modifiers(localFunction.Modifiers),
                    TypeParameters(localFunction.TypeParameterList, localFunction.ConstraintClauses)) { Origin = OriginOf(localFunction) })
                { Origin = OriginOf(localFunction) };

            case LockStatementSyntax @lock:
                return new MetacodeLock(Expression(@lock.Expression), AsBlock(@lock.Statement)) { Origin = OriginOf(@lock) };

            case YieldStatementSyntax @yield:
                return new MetacodeYield(
                    @yield.IsKind(SyntaxKind.YieldReturnStatement) && @yield.Expression is not null
                        ? Expression(@yield.Expression)
                        : null) { Origin = OriginOf(@yield) };

            case FixedStatementSyntax or UnsafeStatementSyntax or CheckedStatementSyntax:
                return OpaqueStatement(statement, statement.Kind() + " has no pseudocode node");

            case EmptyStatementSyntax:
                return MetacodeBlock.Empty;
        }

        return OpaqueStatement(statement, "unmodelled statement: " + statement.Kind());
    }

    private MetacodeStatement Locals(LocalDeclarationStatementSyntax node)
    {
        var isConstant = node.Modifiers.Any(SyntaxKind.ConstKeyword);
        var variables = node.Declaration.Variables
            .Select(v => (MetacodeStatement)new MetacodeLocalVariable(
                v.Identifier.Text,
                TypeRef(node.Declaration.Type),
                v.Initializer is null ? null : Expression(v.Initializer.Value),
                isConstant) { Origin = OriginOf(v) })
            .ToList();

        return variables.Count == 1 ? variables[0] : new MetacodeBlock(variables) { Origin = OriginOf(node) };
    }

    private MetacodeStatement ExpressionStatement(ExpressionStatementSyntax node) =>
        StatementFromExpression(node.Expression, OriginOf(node));

    /// <summary>
    /// The statement a side-effecting expression means in statement position: <c>i++</c> and
    /// <c>i += 1</c> are the same statement, which is what makes them print the same way.
    /// </summary>
    private MetacodeStatement StatementFromExpression(ExpressionSyntax expression, MetacodeOrigin origin)
    {
        switch (expression)
        {
            case AssignmentExpressionSyntax assignment:
                return new MetacodeAssignment(
                    Expression(assignment.Left),
                    AssignmentOperator(assignment.Kind()),
                    Expression(assignment.Right)) { Origin = origin };

            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                return new MetacodeAssignment(
                    Expression(prefix.Operand),
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) ? MetacodeAssignmentOperator.Add : MetacodeAssignmentOperator.Subtract,
                    MetacodeLiteral.Number("1")) { Origin = origin };

            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                return new MetacodeAssignment(
                    Expression(postfix.Operand),
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) ? MetacodeAssignmentOperator.Add : MetacodeAssignmentOperator.Subtract,
                    MetacodeLiteral.Number("1")) { Origin = origin };
        }

        return new MetacodeExpressionStatement(Expression(expression)) { Origin = origin };
    }

    private MetacodeStatement If(IfStatementSyntax node)
    {
        MetacodeBlock? @else = null;
        if (node.Else is not null)
        {
            @else = node.Else.Statement is IfStatementSyntax nested
                ? MetacodeBlock.Of(If(nested))
                : AsBlock(node.Else.Statement);
        }

        return new MetacodeIf(Expression(node.Condition), AsBlock(node.Statement), @else) { Origin = OriginOf(node) };
    }

    private MetacodeStatement For(ForStatementSyntax node)
    {
        MetacodeStatement? initializer = null;
        if (node.Declaration is not null)
        {
            initializer = node.Declaration.Variables.Count == 1
                ? LocalVariable(node.Declaration)
                : new MetacodeBlock(node.Declaration.Variables.Select(v =>
                    (MetacodeStatement)new MetacodeLocalVariable(
                        v.Identifier.Text,
                        TypeRef(node.Declaration.Type),
                        v.Initializer is null ? null : Expression(v.Initializer.Value)) { Origin = OriginOf(v) }).ToList());
        }
        else if (node.Initializers.Count > 0)
        {
            initializer = StatementFromExpression(node.Initializers[0], OriginOf(node));
            if (node.Initializers.Count > 1)
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.LossyLowering, "only the first of several for-initializers was kept", node));
        }

        MetacodeStatement? increment = null;
        if (node.Incrementors.Count > 0)
        {
            increment = StatementFromExpression(node.Incrementors[0], OriginOf(node));
            if (node.Incrementors.Count > 1)
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.LossyLowering, "only the first of several for-incrementors was kept", node));
        }

        return new MetacodeFor(initializer, node.Condition is null ? null : Expression(node.Condition), increment, AsBlock(node.Statement))
        {
            Origin = OriginOf(node),
        };
    }

    private MetacodeLocalVariable LocalVariable(VariableDeclarationSyntax declaration)
    {
        var variable = declaration.Variables[0];
        return new MetacodeLocalVariable(
            variable.Identifier.Text,
            TypeRef(declaration.Type),
            variable.Initializer is null ? null : Expression(variable.Initializer.Value)) { Origin = OriginOf(variable) };
    }

    /// <summary>
    /// The loop variable of a <c>foreach</c>: a name, or the deconstruction of a designation
    /// (<c>var (key, value)</c> prints as <c>(key, value)</c>).
    /// </summary>
    private static string VariableText(ExpressionSyntax variable) => variable switch
    {
        DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax designation } =>
            "(" + string.Join(", ", designation.Variables.Select(DesignationText)) + ")",
        DeclarationExpressionSyntax declaration => declaration.Designation.ToString(),
        _ => variable.ToString(),
    };

    private static string DesignationText(VariableDesignationSyntax designation) => designation switch
    {
        SingleVariableDesignationSyntax single => single.Identifier.Text,
        ParenthesizedVariableDesignationSyntax nested =>
            "(" + string.Join(", ", nested.Variables.Select(DesignationText)) + ")",
        _ => designation.ToString(),
    };

    private MetacodeStatement Switch(SwitchStatementSyntax node)
    {
        var cases = new List<MetacodeSwitchCase>(node.Sections.Count);
        foreach (var section in node.Sections)
        {
            var labels = new List<MetacodeExpression>();
            var isDefault = false;
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax @case:
                        labels.Add(Expression(@case.Value));
                        break;
                    case DefaultSwitchLabelSyntax:
                        isDefault = true;
                        break;
                    case CasePatternSwitchLabelSyntax pattern:
                        // The model keeps patterns as text on purpose (see MetacodeSwitchArm): the label is
                        // a real node, so no lossy-lowering diagnostic is needed for a complete pattern.
                        labels.Add(new MetacodePatternText(MetacodePatternText.Normalize(pattern.Pattern.ToString()))
                        {
                            Origin = OriginOf(pattern),
                        });
                        break;
                }
            }

            cases.Add(new MetacodeSwitchCase(
                labels,
                new MetacodeBlock(section.Statements.Select(Statement).ToList()),
                isDefault) { Origin = OriginOf(section) });
        }

        return new MetacodeSwitch(Expression(node.Expression), cases) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Try(TryStatementSyntax node)
    {
        var catches = node.Catches.Select(@catch => new MetacodeCatch(
            @catch.Declaration is null ? null : TypeRef(@catch.Declaration.Type),
            @catch.Declaration?.Identifier.Text,
            Block(@catch.Block),
            @catch.Filter is null ? null : Expression(@catch.Filter.FilterExpression)) { Origin = OriginOf(@catch) }).ToList();

        return new MetacodeTry(Block(node.Block), catches, node.Finally is null ? null : Block(node.Finally.Block)) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Using(UsingStatementSyntax node)
    {
        if (node.Declaration is not null && node.Declaration.Variables.Count != 1)
            return OpaqueStatement(node, "using with several resources");

        return new MetacodeUsing(
            node.Declaration is null ? null : LocalVariable(node.Declaration),
            node.Expression is null ? null : Expression(node.Expression),
            AsBlock(node.Statement)) { Origin = OriginOf(node) };
    }

    private MetacodeStatement OpaqueStatement(StatementSyntax node, string reason)
    {
        _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedStatement, reason, node));
        return new MetacodeOpaqueStatement(MetacodeLanguage.CSharp, node.ToString(), reason) { Origin = OriginOf(node) };
    }

    private static MetacodeAssignmentOperator AssignmentOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.SimpleAssignmentExpression => MetacodeAssignmentOperator.Assign,
        SyntaxKind.AddAssignmentExpression => MetacodeAssignmentOperator.Add,
        SyntaxKind.SubtractAssignmentExpression => MetacodeAssignmentOperator.Subtract,
        SyntaxKind.MultiplyAssignmentExpression => MetacodeAssignmentOperator.Multiply,
        SyntaxKind.DivideAssignmentExpression => MetacodeAssignmentOperator.Divide,
        SyntaxKind.ModuloAssignmentExpression => MetacodeAssignmentOperator.Modulo,
        SyntaxKind.AndAssignmentExpression => MetacodeAssignmentOperator.BitwiseAnd,
        SyntaxKind.OrAssignmentExpression => MetacodeAssignmentOperator.BitwiseOr,
        SyntaxKind.ExclusiveOrAssignmentExpression => MetacodeAssignmentOperator.BitwiseXor,
        SyntaxKind.LeftShiftAssignmentExpression => MetacodeAssignmentOperator.ShiftLeft,
        SyntaxKind.RightShiftAssignmentExpression => MetacodeAssignmentOperator.ShiftRight,
        SyntaxKind.CoalesceAssignmentExpression => MetacodeAssignmentOperator.Coalesce,
        _ => MetacodeAssignmentOperator.Assign,
    };

    private MetacodeDiagnostic Diagnostic(string code, string message, SyntaxNode node) =>
        new(code, MetacodeDiagnosticSeverity.Warning, message, OriginOf(node));

    internal static MetacodeOrigin OriginOf(SyntaxNode node) => OriginOf(node.GetLocation());

    internal static MetacodeOrigin OriginOf(Location location)
    {
        var span = location.GetLineSpan();
        return new MetacodeOrigin(
            MetacodeLanguage.CSharp,
            string.IsNullOrEmpty(span.Path) ? null : span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }
}
