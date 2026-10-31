using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads Visual Basic syntax trees. Like the C# frontend it uses only Roslyn's syntax layer — no
/// compilation, no workspace — so a translation costs a parse and nothing else. Anything the model has
/// no node for becomes an opaque node plus a diagnostic instead of disappearing.
/// </summary>
/// <remarks>
/// The one construct with no direct node is <c>With … End With</c>: the model has no scoped receiver,
/// and desugaring it would duplicate evaluation of the receiver, so it stays opaque and says so.
/// </remarks>
public sealed class VisualBasicFrontend : ILanguageFrontend
{
    public string Name => "Visual Basic";

    public MetacodeLanguage Language => MetacodeLanguage.VisualBasic;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.VisualBasic
        || (source.Language is null && source.Path is not null
            && source.Path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var tree = VisualBasicSyntaxTree.ParseText(
            source.Text,
            new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest),
            source.Path ?? string.Empty);

        var diagnostics = new List<MetacodeDiagnostic>();
        foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.SyntaxError,
                MetacodeDiagnosticSeverity.Error,
                error.GetMessage(),
                VisualBasicLowering.OriginOf(error.Location)));
        }

        var lowering = new VisualBasicLowering(source.Name, options, diagnostics);
        var modules = lowering.Build((CompilationUnitSyntax)tree.GetRoot());
        return new MetacodeDocument(source.Name, MetacodeLanguage.VisualBasic, modules, diagnostics);
    }
}

/// <summary>The one Visual Basic translation in flight: the options, the diagnostics it produced, and the builders.</summary>
internal sealed partial class VisualBasicLowering
{
    private readonly string _name;
    private readonly MetacodeOptions _options;
    private readonly List<MetacodeDiagnostic> _diagnostics;

    public VisualBasicLowering(string name, MetacodeOptions options, List<MetacodeDiagnostic> diagnostics)
    {
        _name = name;
        _options = options;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<MetacodeModule> Build(CompilationUnitSyntax root)
    {
        var modules = new List<MetacodeModule>();
        var loose = new List<MetacodeDeclaration>();

        foreach (var member in root.Members)
        {
            if (member is NamespaceBlockSyntax @namespace)
                CollectNamespace(@namespace, string.Empty, modules);
            else
                loose.AddRange(Declarations(new[] { member }));
        }

        if (loose.Count > 0)
            modules.Insert(0, new MetacodeModule(_name, loose) { Origin = OriginOf(root) });

        return modules;
    }

    private void CollectNamespace(NamespaceBlockSyntax @namespace, string prefix, List<MetacodeModule> modules)
    {
        var name = prefix.Length == 0
            ? @namespace.NamespaceStatement.Name.ToString()
            : prefix + "." + @namespace.NamespaceStatement.Name;

        var declarations = new List<MetacodeDeclaration>();
        var nested = new List<NamespaceBlockSyntax>();

        foreach (var member in @namespace.Members)
        {
            if (member is NamespaceBlockSyntax inner)
                nested.Add(inner);
            else
                declarations.AddRange(Declarations(new[] { member }));
        }

        modules.Add(new MetacodeModule(name, declarations, MetacodeModuleKind.Namespace) { Origin = OriginOf(@namespace) });

        foreach (var inner in nested)
            CollectNamespace(inner, name, modules);
    }

    private IEnumerable<MetacodeDeclaration> Declarations(IEnumerable<StatementSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var declaration in Fields(field))
                        yield return declaration;
                    break;

                case PropertyBlockSyntax property:
                    yield return Property(property);
                    break;

                case PropertyStatementSyntax property:
                    yield return AutoProperty(property);
                    break;

                case MethodBlockSyntax method:
                    yield return Method(method.SubOrFunctionStatement, method.Statements);
                    break;

                case MethodStatementSyntax method:
                    yield return Method(method, null);
                    break;

                case ConstructorBlockSyntax constructor:
                    yield return new MetacodeFunction(
                        "New",
                        MetacodeFunctionKind.Constructor,
                        Parameters(constructor.SubNewStatement.ParameterList),
                        Body: Block(constructor.Statements),
                        Modifiers: Modifiers(constructor.SubNewStatement.Modifiers)) { Origin = OriginOf(constructor) };
                    break;

                case OperatorBlockSyntax @operator:
                    yield return new MetacodeFunction(
                        "operator " + @operator.OperatorStatement.OperatorToken.Text,
                        MetacodeFunctionKind.Operator,
                        Parameters(@operator.OperatorStatement.ParameterList),
                        TypeRef(@operator.OperatorStatement.AsClause),
                        Block(@operator.Statements),
                        Modifiers(@operator.OperatorStatement.Modifiers)) { Origin = OriginOf(@operator) };
                    break;

                case EventBlockSyntax @event:
                    yield return new MetacodeEvent(
                        @event.EventStatement.Identifier.Text,
                        TypeRef(@event.EventStatement.AsClause),
                        Modifiers(@event.EventStatement.Modifiers)) { Origin = OriginOf(@event) };
                    break;

                case EventStatementSyntax @event:
                    yield return new MetacodeEvent(
                        @event.Identifier.Text,
                        TypeRef(@event.AsClause),
                        Modifiers(@event.Modifiers)) { Origin = OriginOf(@event) };
                    break;

                case EnumBlockSyntax enumeration:
                    yield return Enumeration(enumeration);
                    break;

                case TypeBlockSyntax type:
                    yield return Type(type);
                    break;

                case DelegateStatementSyntax delegateStatement:
                    yield return Opaque(delegateStatement, "a delegate is a signature, not a body");
                    break;

                case DeclareStatementSyntax declare:
                    yield return Opaque(declare, "a Declare statement is a native signature, not a body");
                    break;

                // File-level metadata: imports and Option statements are settings, not declarations.
                case ImportsStatementSyntax or OptionStatementSyntax:
                    break;

                default:
                    yield return Opaque(member, "unmodelled declaration: " + member.Kind());
                    break;
            }
        }
    }

    private MetacodeDeclaration Type(TypeBlockSyntax node)
    {
        var kind = node switch
        {
            InterfaceBlockSyntax => MetacodeTypeKind.Interface,
            StructureBlockSyntax => MetacodeTypeKind.Struct,
            _ => MetacodeTypeKind.Class,
        };

        var modifiers = Modifiers(node.BlockStatement.Modifiers);
        if (node is ModuleBlockSyntax)
            modifiers |= MetacodeModifiers.Static;

        var bases = node.Inherits.SelectMany(statement => statement.Types)
            .Concat(node.Implements.SelectMany(statement => statement.Types))
            .Select(TypeRef)
            .ToList();

        var members = Declarations(node.Members).ToList();

        // Every member of a Module is implicitly Shared; the model says so, which is what lets a module
        // and a C# static class print the same way.
        if (node is ModuleBlockSyntax)
            members = members.Select(AsModuleMember).ToList();

        return new MetacodeTypeDeclaration(
            node.BlockStatement.Identifier.Text,
            kind,
            members,
            modifiers,
            bases.Count == 0 ? null : bases,
            TypeParameters(node.BlockStatement.TypeParameterList)) { Origin = OriginOf(node) };
    }

    private static MetacodeDeclaration AsModuleMember(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeFunction function => function with { Modifiers = function.Modifiers | MetacodeModifiers.Static },
        MetacodeField field => field with { Modifiers = field.Modifiers | MetacodeModifiers.Static },
        MetacodeProperty property => property with { Modifiers = property.Modifiers | MetacodeModifiers.Static },
        MetacodeEvent @event => @event with { Modifiers = @event.Modifiers | MetacodeModifiers.Static },
        _ => declaration,
    };

    private MetacodeDeclaration Enumeration(EnumBlockSyntax node)
    {
        var members = node.Members
            .OfType<EnumMemberDeclarationSyntax>()
            .Select(member => (MetacodeDeclaration)new MetacodeEnumMember(
                member.Identifier.Text,
                member.Initializer is null ? null : Expression(member.Initializer.Value)) { Origin = OriginOf(member) })
            .ToList();

        return new MetacodeTypeDeclaration(
            node.EnumStatement.Identifier.Text,
            MetacodeTypeKind.Enum,
            members,
            Modifiers(node.EnumStatement.Modifiers)) { Origin = OriginOf(node) };
    }

    private IEnumerable<MetacodeDeclaration> Fields(FieldDeclarationSyntax node)
    {
        var modifiers = Modifiers(node.Modifiers);
        foreach (var declarator in node.Declarators)
        {
            var declared = TypeRef(declarator.AsClause);
            var initializer = declarator.Initializer is null ? null : Expression(declarator.Initializer.Value);

            foreach (var name in declarator.Names)
            {
                // `Dim buffer(10) As Byte` declares an array: the identifier carries the bounds.
                var type = name.ArrayBounds is null
                    ? declared
                    : (declared ?? MetacodeTypeRef.Dynamic) with { IsArray = true };

                yield return new MetacodeField(name.Identifier.Text, type, modifiers, initializer) { Origin = OriginOf(name) };
            }
        }
    }

    private MetacodeProperty Property(PropertyBlockSyntax node)
    {
        var statement = node.PropertyStatement;
        MetacodeAccessor? getter = null;
        MetacodeAccessor? setter = null;

        foreach (var accessor in node.Accessors)
        {
            // The token's kind is GetKeyword/SetKeyword; GetAccessorStatement is the statement node, so
            // comparing the token against it silently matches nothing.
            var keyword = accessor.AccessorStatement.AccessorKeyword;
            var lowered = new MetacodeAccessor(
                Modifiers(accessor.AccessorStatement.Modifiers),
                Block(accessor.Statements),
                keyword.IsKind(SyntaxKind.SetKeyword) ? "value" : null) { Origin = OriginOf(accessor) };

            if (keyword.IsKind(SyntaxKind.GetKeyword))
                getter = lowered;
            else if (keyword.IsKind(SyntaxKind.SetKeyword))
                setter = lowered;
            else
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedDeclaration, "accessors: " + keyword.Kind(), accessor));
        }

        return new MetacodeProperty(
            statement.Identifier.Text,
            TypeRef(statement.AsClause),
            Modifiers(statement.Modifiers),
            getter,
            setter,
            statement.Initializer is null ? null : Expression(statement.Initializer.Value),
            Parameters(statement.ParameterList)) { Origin = OriginOf(node) };
    }

    private MetacodeProperty AutoProperty(PropertyStatementSyntax node)
    {
        var readOnly = node.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
        var writeOnly = node.Modifiers.Any(SyntaxKind.WriteOnlyKeyword);

        return new MetacodeProperty(
            node.Identifier.Text,
            TypeRef(node.AsClause),
            Modifiers(node.Modifiers),
            readOnly ? null : new MetacodeAccessor(),
            writeOnly ? null : new MetacodeAccessor(),
            node.Initializer is null ? null : Expression(node.Initializer.Value),
            Parameters(node.ParameterList)) { Origin = OriginOf(node) };
    }

    private MetacodeFunction Method(MethodStatementSyntax statement, IReadOnlyList<StatementSyntax>? statements)
    {
        var isFunction = statement.SubOrFunctionKeyword.IsKind(SyntaxKind.FunctionKeyword);

        return new MetacodeFunction(
            statement.Identifier.Text,
            MetacodeFunctionKind.Method,
            Parameters(statement.ParameterList),
            isFunction ? TypeRef(statement.AsClause) : MetacodeTypeRef.Void,
            statements is null ? null : Block(statements),
            Modifiers(statement.Modifiers),
            TypeParameters(statement.TypeParameterList)) { Origin = OriginOf(statement) };
    }

    private IReadOnlyList<MetacodeParameter> Parameters(ParameterListSyntax? list)
    {
        if (list is null)
            return Array.Empty<MetacodeParameter>();

        return list.Parameters.Select(parameter => new MetacodeParameter(
            parameter.Identifier.Identifier.Text,
            TypeRef(parameter.AsClause),
            parameter.Default is null ? null : Expression(parameter.Default.Value),
            parameter.Modifiers.Any(SyntaxKind.ByRefKeyword) ? MetacodeParameterKind.Ref
            : parameter.Modifiers.Any(SyntaxKind.ParamArrayKeyword) ? MetacodeParameterKind.Params
            : MetacodeParameterKind.Value) { Origin = OriginOf(parameter) }).ToList();
    }

    private IReadOnlyList<MetacodeArgument> Arguments(ArgumentListSyntax? list)
    {
        if (list is null)
            return Array.Empty<MetacodeArgument>();

        var arguments = new List<MetacodeArgument>(list.Arguments.Count);
        foreach (var argument in list.Arguments)
        {
            switch (argument)
            {
                case SimpleArgumentSyntax simple:
                    arguments.Add(new MetacodeArgument(
                        simple.NameColonEquals?.Name.Identifier.Text,
                        Expression(simple.Expression)) { Origin = OriginOf(simple) });
                    break;

                case OmittedArgumentSyntax omitted:
                    arguments.Add(new MetacodeArgument(
                        null,
                        new MetacodeLiteral(MetacodeLiteralKind.Default, "default") { Origin = OriginOf(omitted) }) { Origin = OriginOf(omitted) });
                    break;

                case RangeArgumentSyntax range:
                    arguments.Add(new MetacodeArgument(
                        null,
                        new MetacodeRange(
                            range.LowerBound is null ? null : Expression(range.LowerBound),
                            range.UpperBound is null ? null : Expression(range.UpperBound)) { Origin = OriginOf(range) }) { Origin = OriginOf(range) });
                    break;
            }
        }

        return arguments;
    }

    private IReadOnlyList<MetacodeTypeParameter>? TypeParameters(TypeParameterListSyntax? list)
    {
        if (list is null)
            return null;

        return list.Parameters.Select(parameter => new MetacodeTypeParameter(
            parameter.Identifier.Text,
            parameter.TypeParameterConstraintClause is null
                ? null
                : new[] { parameter.TypeParameterConstraintClause.ToString().Trim() }) { Origin = OriginOf(parameter) }).ToList();
    }

    private MetacodeBlock Block(IReadOnlyList<StatementSyntax> statements)
    {
        var lowered = new List<MetacodeStatement>(statements.Count);
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            if (_options.IncludeComments)
                lowered.AddRange(LeadingComments(statement));

            // A Visual Basic label is a statement of its own; the model's label wraps the statement it
            // names, which is the next one in this list.
            if (statement is LabelStatementSyntax label)
            {
                var target = index + 1 < statements.Count
                    ? Statement(statements[++index])
                    : MetacodeBlock.Empty;
                lowered.Add(new MetacodeLabel(label.LabelToken.Text, target) { Origin = OriginOf(label) });
                continue;
            }

            var one = Statement(statement);
            if (one is MetacodeBlock splice)
                lowered.AddRange(splice.Statements);
            else
                lowered.Add(one);
        }

        return new MetacodeBlock(lowered);
    }

    private IEnumerable<MetacodeStatement> LeadingComments(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.CommentTrivia))
                yield return new MetacodeComment(trivia.ToString().Trim('\'', ' ')) { Origin = OriginOf(node) };
        }
    }

    private MetacodeStatement Statement(StatementSyntax statement)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax local:
                return Locals(local);

            case ExpressionStatementSyntax expression:
                return new MetacodeExpressionStatement(Expression(expression.Expression)) { Origin = OriginOf(expression) };

            case AssignmentStatementSyntax assignment:
                return Assignment(Expression(assignment.Left), assignment.Kind(), Expression(assignment.Right), OriginOf(assignment));

            case CallStatementSyntax call:
                return new MetacodeExpressionStatement(Expression(call.Invocation)) { Origin = OriginOf(call) };

            case MultiLineIfBlockSyntax @if:
                return If(@if);

            case SingleLineIfStatementSyntax @if:
                return SingleLineIf(@if);

            case WhileBlockSyntax @while:
                return new MetacodeWhile(Expression(@while.WhileStatement.Condition), Block(@while.Statements)) { Origin = OriginOf(@while) };

            case DoLoopBlockSyntax @do:
                return DoLoop(@do);

            case ForBlockSyntax @for:
                return For(@for);

            case ForEachBlockSyntax @foreach:
                return ForEach(@foreach);

            case SelectBlockSyntax @select:
                return Select(@select);

            case TryBlockSyntax @try:
                return Try(@try);

            case UsingBlockSyntax @using:
                return Using(@using);

            case SyncLockBlockSyntax @lock:
                return new MetacodeLock(Expression(@lock.SyncLockStatement.Expression), Block(@lock.Statements)) { Origin = OriginOf(@lock) };

            case ReturnStatementSyntax @return:
                return new MetacodeReturn(@return.Expression is null ? null : Expression(@return.Expression)) { Origin = OriginOf(@return) };

            case ThrowStatementSyntax @throw:
                return new MetacodeThrow(@throw.Expression is null ? null : Expression(@throw.Expression)) { Origin = OriginOf(@throw) };

            case YieldStatementSyntax @yield:
                return new MetacodeYield(@yield.Expression is null ? null : Expression(@yield.Expression)) { Origin = OriginOf(@yield) };

            case ExitStatementSyntax exit:
                return Exit(exit);

            case ContinueStatementSyntax @continue:
                return new MetacodeContinue { Origin = OriginOf(@continue) };

            case GoToStatementSyntax @goto:
                return new MetacodeGoto(@goto.Label.ToString()) { Origin = OriginOf(@goto) };

            case LabelStatementSyntax label:
                return new MetacodeLabel(label.LabelToken.Text, MetacodeBlock.Empty) { Origin = OriginOf(label) };

            case MethodBlockSyntax local:
                return new MetacodeLocalFunction(Method(local.SubOrFunctionStatement, local.Statements)) { Origin = OriginOf(local) };

            case WithBlockSyntax @with:
                return OpaqueStatement(@with, "With … End With has no pseudocode node and cannot be desugared without duplicating the receiver");

            case AddRemoveHandlerStatementSyntax handler:
                return OpaqueStatement(handler, "AddHandler/RemoveHandler is not modelled");

            case RaiseEventStatementSyntax raise:
                return OpaqueStatement(raise, "RaiseEvent is not modelled");

            case ReDimStatementSyntax redim:
                return OpaqueStatement(redim, "ReDim has no pseudocode node");

            case EraseStatementSyntax erase:
                return OpaqueStatement(erase, "Erase has no pseudocode node");

            case OnErrorGoToStatementSyntax onError:
                return OpaqueStatement(onError, "On Error is not modelled");

            case OnErrorResumeNextStatementSyntax onError:
                return OpaqueStatement(onError, "On Error Resume Next is not modelled");

            case ResumeStatementSyntax resume:
                return OpaqueStatement(resume, "Resume is not modelled");

            case StopOrEndStatementSyntax stop:
                return OpaqueStatement(stop, "Stop/End is not modelled");
        }

        return OpaqueStatement(statement, "unmodelled statement: " + statement.Kind());
    }

    private MetacodeStatement Locals(LocalDeclarationStatementSyntax node)
    {
        var isConstant = node.Modifiers.Any(SyntaxKind.ConstKeyword);
        var variables = new List<MetacodeStatement>();

        foreach (var declarator in node.Declarators)
        {
            var type = TypeRef(declarator.AsClause);
            var initializer = declarator.Initializer is null ? null : Expression(declarator.Initializer.Value);
            foreach (var name in declarator.Names)
                variables.Add(new MetacodeLocalVariable(name.Identifier.Text, type, initializer, isConstant) { Origin = OriginOf(name) });
        }

        return variables.Count == 1
            ? variables[0]
            : new MetacodeBlock(variables) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Assignment(
        MetacodeExpression target, SyntaxKind kind, MetacodeExpression value, MetacodeOrigin origin)
    {
        switch (kind)
        {
            case SyntaxKind.AddAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Add, value) { Origin = origin };
            case SyntaxKind.SubtractAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Subtract, value) { Origin = origin };
            case SyntaxKind.MultiplyAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Multiply, value) { Origin = origin };
            case SyntaxKind.DivideAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Divide, value) { Origin = origin };
            case SyntaxKind.ExponentiateAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Power, value) { Origin = origin };
            case SyntaxKind.LeftShiftAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.ShiftLeft, value) { Origin = origin };
            case SyntaxKind.RightShiftAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.ShiftRight, value) { Origin = origin };
            case SyntaxKind.ConcatenateAssignmentStatement:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Add, value) { Origin = origin };
            case SyntaxKind.IntegerDivideAssignmentStatement:
                // `x \= y` has no compound operator in the model: it is the explicit binary form.
                return new MetacodeAssignment(
                    target,
                    MetacodeAssignmentOperator.Assign,
                    new MetacodeBinary(MetacodeBinaryOperator.IntegerDivide, target, value) { Origin = origin }) { Origin = origin };
            default:
                return new MetacodeAssignment(target, MetacodeAssignmentOperator.Assign, value) { Origin = origin };
        }
    }

    private MetacodeStatement If(MultiLineIfBlockSyntax node)
    {
        MetacodeBlock? @else = node.ElseBlock is null ? null : Block(node.ElseBlock.Statements);

        for (var index = node.ElseIfBlocks.Count - 1; index >= 0; index--)
        {
            var elseIf = node.ElseIfBlocks[index];
            @else = MetacodeBlock.Of(new MetacodeIf(
                Expression(elseIf.ElseIfStatement.Condition),
                Block(elseIf.Statements),
                @else) { Origin = OriginOf(elseIf) });
        }

        return new MetacodeIf(Expression(node.IfStatement.Condition), Block(node.Statements), @else) { Origin = OriginOf(node) };
    }

    private MetacodeStatement SingleLineIf(SingleLineIfStatementSyntax node) => new MetacodeIf(
        Expression(node.Condition),
        Block(node.Statements),
        node.ElseClause is null ? null : Block(node.ElseClause.Statements)) { Origin = OriginOf(node) };

    private MetacodeStatement DoLoop(DoLoopBlockSyntax node)
    {
        // `Do While c … Loop` tests first; `Do … Loop While c` tests last. Until is the negation.
        if (node.DoStatement.WhileOrUntilClause is { } pre)
            return new MetacodeWhile(Condition(pre), Block(node.Statements)) { Origin = OriginOf(node) };

        var post = node.LoopStatement.WhileOrUntilClause is { } clause
            ? Condition(clause)
            : MetacodeLiteral.True;
        return new MetacodeDoWhile(Block(node.Statements), post) { Origin = OriginOf(node) };
    }

    private MetacodeExpression Condition(WhileOrUntilClauseSyntax clause) =>
        clause.WhileOrUntilKeyword.IsKind(SyntaxKind.UntilKeyword)
            ? new MetacodeUnary(MetacodeUnaryOperator.Not, Expression(clause.Condition)) { Origin = OriginOf(clause) }
            : Expression(clause.Condition);

    private MetacodeStatement For(ForBlockSyntax node)
    {
        var statement = node.ForStatement;
        var (name, type) = ControlVariable(statement.ControlVariable);

        var from = Expression(statement.FromValue);
        var to = Expression(statement.ToValue);
        var step = statement.StepClause is null ? null : Expression(statement.StepClause.StepValue);

        if (step is not null && !IsConstant(step))
            _diagnostics.Add(Diagnostic(
                MetacodeDiagnosticCodes.LossyLowering,
                "the direction of a For loop with a non-constant Step is assumed positive",
                statement));

        var ascending = step is null || !IsNegative(step);
        var initializer = type is null
            ? (MetacodeStatement)new MetacodeAssignment(
                new MetacodeIdentifier(name) { Origin = OriginOf(statement.ControlVariable) },
                MetacodeAssignmentOperator.Assign,
                from) { Origin = OriginOf(statement) }
            : new MetacodeLocalVariable(name, type, from) { Origin = OriginOf(statement.ControlVariable) };

        var condition = new MetacodeBinary(
            ascending ? MetacodeBinaryOperator.LessThanOrEqual : MetacodeBinaryOperator.GreaterThanOrEqual,
            new MetacodeIdentifier(name) { Origin = OriginOf(statement.ControlVariable) },
            to) { Origin = OriginOf(statement) };

        var increment = new MetacodeAssignment(
            new MetacodeIdentifier(name) { Origin = OriginOf(statement.ControlVariable) },
            MetacodeAssignmentOperator.Add,
            step ?? MetacodeLiteral.Number("1")) { Origin = OriginOf(statement) };

        return new MetacodeFor(initializer, condition, increment, Block(node.Statements)) { Origin = OriginOf(node) };
    }

    private MetacodeStatement ForEach(ForEachBlockSyntax node)
    {
        var statement = node.ForEachStatement;
        var (name, type) = ControlVariable(statement.ControlVariable);

        return new MetacodeForEach(
            name,
            type,
            Expression(statement.Expression),
            Block(node.Statements)) { Origin = OriginOf(node) };
    }

    private (string Name, MetacodeTypeRef? Type) ControlVariable(VisualBasicSyntaxNode controlVariable) => controlVariable switch
    {
        VariableDeclaratorSyntax declarator when declarator.Names.Count > 0 =>
            (declarator.Names[0].Identifier.Text, TypeRef(declarator.AsClause)),
        IdentifierNameSyntax identifier => (identifier.Identifier.Text, null),
        _ => (controlVariable.ToString(), null),
    };

    private static bool IsNegative(MetacodeExpression expression) =>
        expression is MetacodeUnary { Operator: MetacodeUnaryOperator.Negate };

    private static bool IsConstant(MetacodeExpression expression) => expression switch
    {
        MetacodeLiteral => true,
        MetacodeUnary { Operator: MetacodeUnaryOperator.Negate or MetacodeUnaryOperator.Plus } unary => IsConstant(unary.Operand),
        _ => false,
    };

    private MetacodeStatement Select(SelectBlockSyntax node)
    {
        var cases = new List<MetacodeSwitchCase>(node.CaseBlocks.Count);
        foreach (var block in node.CaseBlocks)
        {
            var labels = new List<MetacodeExpression>();
            var isDefault = false;

            foreach (var clause in block.CaseStatement.Cases)
            {
                switch (clause)
                {
                    case SimpleCaseClauseSyntax simple:
                        labels.Add(Expression(simple.Value));
                        break;

                    case RangeCaseClauseSyntax range:
                        labels.Add(new MetacodePatternText(MetacodePatternText.Normalize(
                            range.LowerBound + " To " + range.UpperBound)) { Origin = OriginOf(range) });
                        break;

                    case RelationalCaseClauseSyntax relational:
                        labels.Add(new MetacodePatternText(MetacodePatternText.Normalize(
                            "Is " + relational.OperatorToken.Text + " " + relational.Value)) { Origin = OriginOf(relational) });
                        break;

                    case ElseCaseClauseSyntax:
                        isDefault = true;
                        break;
                }
            }

            cases.Add(new MetacodeSwitchCase(labels, Block(block.Statements), isDefault) { Origin = OriginOf(block) });
        }

        return new MetacodeSwitch(Expression(node.SelectStatement.Expression), cases) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Try(TryBlockSyntax node)
    {
        var catches = node.CatchBlocks.Select(block => new MetacodeCatch(
            block.CatchStatement.AsClause is { } clause ? TypeRef(clause) : null,
            block.CatchStatement.IdentifierName?.Identifier.Text,
            Block(block.Statements),
            block.CatchStatement.WhenClause is null ? null : Expression(block.CatchStatement.WhenClause.Filter))
        { Origin = OriginOf(block) }).ToList();

        return new MetacodeTry(
            Block(node.Statements),
            catches,
            node.FinallyBlock is null ? null : Block(node.FinallyBlock.Statements)) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Using(UsingBlockSyntax node)
    {
        var statement = node.UsingStatement;

        if (statement.Variables.Count > 1)
            return OpaqueStatement(node, "Using with several resources");

        if (statement.Variables.Count == 1)
        {
            var declarator = statement.Variables[0];
            var name = declarator.Names.Count > 0 ? declarator.Names[0].Identifier.Text : "_";
            var declaration = new MetacodeLocalVariable(
                name,
                TypeRef(declarator.AsClause),
                declarator.Initializer is null ? null : Expression(declarator.Initializer.Value)) { Origin = OriginOf(declarator) };
            return new MetacodeUsing(declaration, null, Block(node.Statements)) { Origin = OriginOf(node) };
        }

        return new MetacodeUsing(
            null,
            statement.Expression is null ? null : Expression(statement.Expression),
            Block(node.Statements)) { Origin = OriginOf(node) };
    }

    private MetacodeStatement Exit(ExitStatementSyntax node)
    {
        if (node.BlockKeyword.IsKind(SyntaxKind.SubKeyword)
            || node.BlockKeyword.IsKind(SyntaxKind.FunctionKeyword)
            || node.BlockKeyword.IsKind(SyntaxKind.PropertyKeyword))
        {
            return new MetacodeReturn { Origin = OriginOf(node) };
        }

        if (node.BlockKeyword.IsKind(SyntaxKind.TryKeyword))
            return OpaqueStatement(node, "Exit Try is not modelled");

        return new MetacodeBreak { Origin = OriginOf(node) };
    }

    private MetacodeDeclaration Opaque(SyntaxNode node, string reason)
    {
        _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedDeclaration, reason, node));
        return new MetacodeOpaqueDeclaration(MetacodeLanguage.VisualBasic, node.ToString(), reason) { Origin = OriginOf(node) };
    }

    private MetacodeStatement OpaqueStatement(SyntaxNode node, string reason)
    {
        _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.UnsupportedStatement, reason, node));
        return new MetacodeOpaqueStatement(MetacodeLanguage.VisualBasic, node.ToString(), reason) { Origin = OriginOf(node) };
    }

    private static MetacodeTypeRef TypeRef(TypeSyntax type) => type switch
    {
        PredefinedTypeSyntax predefined => new MetacodeTypeRef(CanonicalType(predefined.Keyword.Text)),
        ArrayTypeSyntax array => TypeRef(array.ElementType) with { IsArray = true },
        NullableTypeSyntax nullable => TypeRef(nullable.ElementType) with { IsNullable = true },
        GenericNameSyntax generic => new MetacodeTypeRef(
            generic.Identifier.Text,
            generic.TypeArgumentList.Arguments.Select(TypeRef).ToList()),
        QualifiedNameSyntax qualified => new MetacodeTypeRef(qualified.ToString()),
        IdentifierNameSyntax identifier => new MetacodeTypeRef(identifier.Identifier.Text),
        _ => new MetacodeTypeRef(type.ToString()),
    };

    /// <summary>
    /// The model spells a type the way the source does, but a VB predefined type has a canonical name the
    /// rest of the pipeline already uses: mapping <c>Integer</c> to <c>int</c> here is what lets the same
    /// algorithm in VB and C# print identically, and what makes the C# emitter's output compile.
    /// </summary>
    private static string CanonicalType(string keyword) => keyword switch
    {
        "Integer" => "int",
        "Long" => "long",
        "Short" => "short",
        "Byte" => "byte",
        "SByte" => "sbyte",
        "UInteger" => "uint",
        "ULong" => "ulong",
        "UShort" => "ushort",
        "Single" => "float",
        "Double" => "double",
        "Decimal" => "decimal",
        "String" => "string",
        "Char" => "char",
        "Boolean" => "bool",
        "Object" => "object",
        "Date" => "DateTime",
        _ => keyword,
    };

    private static MetacodeTypeRef? TypeRef(AsClauseSyntax? clause) =>
        clause is SimpleAsClauseSyntax simple ? TypeRef(simple.Type) : null;

    private static MetacodeModifiers Modifiers(SyntaxTokenList tokens)
    {
        var result = MetacodeModifiers.None;
        foreach (var token in tokens)
        {
            result |= token.Kind() switch
            {
                SyntaxKind.PublicKeyword => MetacodeModifiers.Public,
                SyntaxKind.PrivateKeyword => MetacodeModifiers.Private,
                SyntaxKind.ProtectedKeyword => MetacodeModifiers.Protected,
                SyntaxKind.FriendKeyword => MetacodeModifiers.Internal,
                SyntaxKind.SharedKeyword => MetacodeModifiers.Static,
                SyntaxKind.MustOverrideKeyword => MetacodeModifiers.Abstract,
                SyntaxKind.OverridableKeyword => MetacodeModifiers.Virtual,
                SyntaxKind.OverridesKeyword => MetacodeModifiers.Override,
                SyntaxKind.NotOverridableKeyword => MetacodeModifiers.Sealed,
                SyntaxKind.ReadOnlyKeyword => MetacodeModifiers.ReadOnly,
                SyntaxKind.ConstKeyword => MetacodeModifiers.Const,
                SyntaxKind.StaticKeyword => MetacodeModifiers.Static,
                SyntaxKind.AsyncKeyword => MetacodeModifiers.Async,
                SyntaxKind.PartialKeyword => MetacodeModifiers.Partial,
                SyntaxKind.ShadowsKeyword => MetacodeModifiers.New,
                _ => MetacodeModifiers.None,
            };
        }

        return result;
    }

    private MetacodeDiagnostic Diagnostic(string code, string message, SyntaxNode node) =>
        new(code, MetacodeDiagnosticSeverity.Warning, message, OriginOf(node));

    internal static MetacodeOrigin OriginOf(SyntaxNode node) => OriginOf(node.GetLocation());

    internal static MetacodeOrigin OriginOf(Location location)
    {
        var span = location.GetLineSpan();
        return new MetacodeOrigin(
            MetacodeLanguage.VisualBasic,
            string.IsNullOrEmpty(span.Path) ? null : span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);
    }
}
