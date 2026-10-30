using System.Text.Json;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads a CPython <c>ast</c> dump as JSON. Every node is an object with <c>_type</c> (CPython's class
/// name) plus its own fields, which is what the four-line dumper in <c>docs/metacode.md</c> produces.
/// </summary>
public sealed class PythonFrontend : ILanguageFrontend
{
    public string Name => "Python";

    public MetacodeLanguage Language => MetacodeLanguage.Python;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Python
        || (source.Language is null && source.Path is not null
            && source.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Text, new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.ParseError,
                MetacodeDiagnosticSeverity.Error,
                "The Python input is not JSON: " + exception.Message + ". Pass the output of the AST dumper, not the .py source.",
                MetacodeOrigin.Unknown));
            return new MetacodeDocument(source.Name, MetacodeLanguage.Python, Array.Empty<MetacodeModule>(), diagnostics);
        }

        var lowering = new PythonLowering(diagnostics);
        var modules = lowering.Modules(document.RootElement, source.Name);
        return new MetacodeDocument(source.Name, MetacodeLanguage.Python, modules, diagnostics);
    }
}

internal sealed class PythonLowering
{
    private readonly List<MetacodeDiagnostic> _diagnostics;

    /// <summary>Names assigned by the statements of the scope being lowered, one set per function.</summary>
    private readonly Stack<HashSet<string>> _scopes = new();

    private bool _inMethod;
    private string _receiver = "self";

    public PythonLowering(List<MetacodeDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public IReadOnlyList<MetacodeModule> Modules(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            _diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.MalformedTree,
                MetacodeDiagnosticSeverity.Error,
                "The JSON root is not an AST node object.",
                MetacodeOrigin.Unknown));
            return Array.Empty<MetacodeModule>();
        }

        var kind = Kind(root);
        if (kind != "Module")
        {
            _diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.MalformedTree,
                MetacodeDiagnosticSeverity.Error,
                $"Expected a Module node, found '{kind}'.",
                Origin(root)));
            return Array.Empty<MetacodeModule>();
        }

        var declarations = new List<MetacodeDeclaration>();
        var statements = new List<MetacodeStatement>();
        foreach (var statement in Items(Field(root, "body")))
        {
            if (Declaration(statement, inClass: false) is { } declaration)
                declarations.Add(declaration);
            else
                statements.Add(Statement(statement));
        }

        if (statements.Count > 0)
        {
            declarations.Insert(0, new MetacodeFunction(
                "__module__",
                MetacodeFunctionKind.Function,
                Body: new MetacodeBlock(statements) { Origin = Origin(root) }) { Origin = Origin(root) });
        }

        return new[] { new MetacodeModule(name, declarations) { Origin = Origin(root) } };
    }

    /// <summary>A declaration, when the node is one; the module and class bodies use this to split.</summary>
    private MetacodeDeclaration? Declaration(JsonElement node, bool inClass)
    {
        switch (Kind(node))
        {
            case "FunctionDef":
            case "AsyncFunctionDef":
                return Function(node, inClass ? MetacodeFunctionKind.Method : MetacodeFunctionKind.Function);
            case "ClassDef":
                return Type(node);
            case "Assign" when inClass:
                return ClassField(node);
            case "AnnAssign" when inClass:
                return ClassField(node);
            default:
                return null;
        }
    }

    private MetacodeTypeDeclaration Type(JsonElement node)
    {
        var members = new List<MetacodeDeclaration>();
        foreach (var child in Items(Field(node, "body")))
        {
            if (Declaration(child, inClass: true) is { } declaration)
            {
                members.Add(declaration);
            }
            else if (Kind(child) == "Pass")
            {
                // An empty class body.
            }
            else
            {
                _diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.LossyLowering,
                    MetacodeDiagnosticSeverity.Warning,
                    "A statement in a class body has no declaration form and was kept as opaque text.",
                    Origin(child)));
                members.Add(new MetacodeOpaqueDeclaration(MetacodeLanguage.Python, Kind(child), "class-level statement")
                {
                    Origin = Origin(child),
                });
            }
        }

        return new MetacodeTypeDeclaration(
            Text(node, "name") ?? "?",
            MetacodeTypeKind.Class,
            members,
            BaseTypes: Items(Field(node, "bases")).Select(b => new MetacodeTypeRef(Name(b) ?? Kind(b)))
                .ToList(),
            Decorators: Decorators(node)) { Origin = Origin(node) };
    }

    private MetacodeDeclaration ClassField(JsonElement node)
    {
        var target = Field(node, "target");
        var value = Field(node, "value");
        var annotation = Field(node, "annotation");
        return new MetacodeField(
            target is null ? "?" : Name(target.Value) ?? "?",
            annotation is null ? null : new MetacodeTypeRef(Name(annotation.Value) ?? Kind(annotation.Value)),
            Initializer: value is null ? null : Expression(value.Value)) { Origin = Origin(node) };
    }

    private MetacodeFunction Function(JsonElement node, MetacodeFunctionKind kind)
    {
        var arguments = Field(node, "args");
        var parameters = new List<MetacodeParameter>();
        var receiver = default(string);

        if (arguments is not null)
        {
            foreach (var argument in Items(Field(arguments.Value, "posonlyargs")))
                parameters.Add(Parameter(argument));
            foreach (var argument in Items(Field(arguments.Value, "args")))
                parameters.Add(Parameter(argument));
            if (Field(arguments.Value, "vararg") is { } vararg)
                parameters.Add(Parameter(vararg) with { Kind = MetacodeParameterKind.Params });
            foreach (var argument in Items(Field(arguments.Value, "kwonlyargs")))
                parameters.Add(Parameter(argument) with { Kind = MetacodeParameterKind.Keyword });
            if (Field(arguments.Value, "kwarg") is { } kwarg)
                parameters.Add(Parameter(kwarg) with { Kind = MetacodeParameterKind.Params });

            // `self` is a parameter only because Python needs one: the receiver is not an argument.
            if (kind == MetacodeFunctionKind.Method && parameters.Count > 0 && parameters[0].Name is "self" or "cls")
            {
                receiver = parameters[0].Name;
                parameters.RemoveAt(0);
            }
        }

        var defaults = Items(Field(arguments is null ? default : arguments.Value, "defaults")).ToList();
        if (defaults.Count > 0)
        {
            var offset = parameters.Count - defaults.Count;
            for (var i = 0; i < defaults.Count && offset + i >= 0; i++)
                parameters[offset + i] = parameters[offset + i] with { DefaultValue = Expression(defaults[i]) };
        }

        var previousReceiver = _receiver;
        var previousInMethod = _inMethod;
        _receiver = receiver ?? "self";
        _inMethod = receiver is not null;

        // Python has no declarations: the first assignment to a name in a scope is the declaration,
        // which is what makes it print exactly like the C# `var total = 0`.
        _scopes.Push(new HashSet<string>(DeclaredNames(Field(node, "body")), StringComparer.Ordinal));
        var body = Block(Items(Field(node, "body")));
        _scopes.Pop();

        _receiver = previousReceiver;
        _inMethod = previousInMethod;

        var returns = Field(node, "returns");
        var modifiers = Kind(node) == "AsyncFunctionDef" ? MetacodeModifiers.Async : MetacodeModifiers.None;

        return new MetacodeFunction(
            Text(node, "name") ?? "?",
            kind,
            parameters,
            returns is null ? null : new MetacodeTypeRef(Name(returns.Value) ?? Kind(returns.Value)),
            body,
            modifiers,
            Decorators: Decorators(node)) { Origin = Origin(node) };
    }

    private List<string> Decorators(JsonElement node) =>
        Items(Field(node, "decorator_list")).Select(DecoratorText).ToList();

    /// <summary>
    /// A decorator as written. Only the shapes a decorator actually takes are printed — a name, an
    /// attribute path, a call of one — and anything else keeps its node kind plus a diagnostic.
    /// </summary>
    private string DecoratorText(JsonElement node)
    {
        switch (Kind(node))
        {
            case "Name":
                return Text(node, "id") ?? "?";

            case "Attribute":
            {
                JsonElement? target = Field(node, "value");
                return (target is null ? "?" : DecoratorText(target.Value)) + "." + (Text(node, "attr") ?? "?");
            }

            case "Call":
            {
                var arguments = new List<string>();
                foreach (var argument in Items(Field(node, "args")))
                    arguments.Add(DecoratorText(argument));
                foreach (var keyword in Items(Field(node, "keywords")))
                {
                    var name = Text(keyword, "arg") ?? "?";
                    JsonElement? keywordValue = Field(keyword, "value");
                    arguments.Add(name + "=" + (keywordValue is null ? "?" : DecoratorText(keywordValue.Value)));
                }

                JsonElement? callee = Field(node, "func");
                return (callee is null ? "?" : DecoratorText(callee.Value)) + "(" + string.Join(", ", arguments) + ")";
            }

            case "Constant" when node.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String:
                return Quote(value.GetString()!);

            default:
                _diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedExpression,
                    MetacodeDiagnosticSeverity.Warning,
                    "a decorator has no text form: " + Kind(node),
                    Origin(node)));
                return Kind(node);
        }
    }

    private MetacodeParameter Parameter(JsonElement node)
    {
        var annotation = Field(node, "annotation");
        return new MetacodeParameter(
            Text(node, "arg") ?? "?",
            annotation is null ? null : new MetacodeTypeRef(Name(annotation.Value) ?? Kind(annotation.Value)))
        {
            Origin = Origin(node),
        };
    }

    private MetacodeBlock Block(IEnumerable<JsonElement> statements)
    {
        var lowered = new List<MetacodeStatement>();
        foreach (var statement in statements)
            lowered.Add(Statement(statement));
        return new MetacodeBlock(lowered);
    }

    private MetacodeStatement Statement(JsonElement node)
    {
        var origin = Origin(node);
        switch (Kind(node))
        {
            case "Expr":
            {
                var value = Field(node, "value");
                return value is null
                    ? MetacodeBlock.Empty
                    : new MetacodeExpressionStatement(Expression(value.Value)) { Origin = origin };
            }

            case "Return":
            {
                var value = Field(node, "value");
                return new MetacodeReturn(value is null ? null : Expression(value.Value)) { Origin = origin };
            }

            case "Assign":
                return Assign(node);

            case "AnnAssign":
            {
                var target = Field(node, "target");
                var value = Field(node, "value");
                var annotation = Field(node, "annotation");
                var type = annotation is null ? null : new MetacodeTypeRef(Name(annotation.Value) ?? Kind(annotation.Value));
                if (target is not null && Kind(target.Value) == "Name")
                {
                    return new MetacodeLocalVariable(
                        Text(target.Value, "id") ?? "?",
                        type,
                        value is null ? null : Expression(value.Value)) { Origin = origin };
                }

                return target is null
                    ? MetacodeBlock.Empty
                    : new MetacodeAssignment(Expression(target.Value), MetacodeAssignmentOperator.Assign,
                        value is null ? MetacodeLiteral.Null : Expression(value.Value)) { Origin = origin };
            }

            case "AugAssign":
            {
                var target = Field(node, "target");
                var value = Field(node, "value");
                if (target is null || value is null)
                    return OpaqueStatement(node, "incomplete AugAssign");
                return new MetacodeAssignment(
                    Expression(target.Value),
                    AssignmentOperator(Op(node)),
                    Expression(value.Value)) { Origin = origin };
            }

            case "If":
            {
                var condition = Field(node, "test");
                return new MetacodeIf(
                    condition is null ? MetacodeLiteral.True : Expression(condition.Value),
                    Block(Items(Field(node, "body"))),
                    Field(node, "orelse") is { } @else && @else.GetArrayLength() > 0 ? Block(Items(@else)) : null)
                {
                    Origin = origin,
                };
            }

            case "While":
            {
                var condition = Field(node, "test");
                return new MetacodeWhile(
                    condition is null ? MetacodeLiteral.True : Expression(condition.Value),
                    Block(Items(Field(node, "body")))) { Origin = origin };
            }

            case "For":
                return For(node);

            case "Try":
                return Try(node);

            case "Raise":
            {
                var exception = Field(node, "exc");
                return new MetacodeThrow(exception is null ? null : Expression(exception.Value)) { Origin = origin };
            }

            case "Break":
                return new MetacodeBreak { Origin = origin };

            case "Continue":
                return new MetacodeContinue { Origin = origin };

            case "Pass":
                return MetacodeBlock.Empty;

            case "With":
                return With(node);

            case "Import":
                return Import(node, isFrom: false);

            case "ImportFrom":
                return Import(node, isFrom: true);

            case "Global" or "Nonlocal" or "Assert" or "Delete" or "Match":
                return OpaqueStatement(node, Kind(node).ToLowerInvariant() + " has no pseudocode node");

            case "FunctionDef":
            case "AsyncFunctionDef":
                return new MetacodeLocalFunction(Function(node, MetacodeFunctionKind.LocalFunction)) { Origin = origin };

            default:
                return OpaqueStatement(node, "unmodelled statement: " + Kind(node));
        }
    }

    private MetacodeStatement Assign(JsonElement node)
    {
        var value = Field(node, "value");
        var targets = Items(Field(node, "targets")).ToList();
        if (value is null || targets.Count == 0)
            return OpaqueStatement(node, "incomplete Assign");

        var assignments = new List<MetacodeStatement>(targets.Count);
        foreach (var target in targets)
        {
            var name = Kind(target) == "Name" ? Text(target, "id") : null;
            if (name is not null && _scopes.Count > 0 && _scopes.Peek().Remove(name))
            {
                assignments.Add(new MetacodeLocalVariable(name, null, Expression(value.Value)) { Origin = Origin(target) });
                continue;
            }

            assignments.Add(new MetacodeAssignment(
                Expression(target),
                MetacodeAssignmentOperator.Assign,
                Expression(value.Value)) { Origin = Origin(target) });
        }

        return assignments.Count == 1 ? assignments[0] : new MetacodeBlock(assignments) { Origin = Origin(node) };
    }

    /// <summary>The names a block declares, i.e. assigns to directly.</summary>
    private static IEnumerable<string> DeclaredNames(JsonElement? body)
    {
        foreach (var statement in Items(body))
        {
            switch (Kind(statement))
            {
                case "Assign":
                    foreach (var target in Items(Field(statement, "targets")))
                    {
                        if (Kind(target) == "Name" && Text(target, "id") is { } name)
                            yield return name;
                    }

                    break;

                case "AnnAssign":
                    if (Field(statement, "target") is { } annotated && Kind(annotated) == "Name" && Text(annotated, "id") is { } annotatedName)
                        yield return annotatedName;
                    break;
            }
        }
    }

    private MetacodeStatement For(JsonElement node)
    {
        var target = Field(node, "target");
        var iterable = Field(node, "iter");
        if (target is null || iterable is null)
            return OpaqueStatement(node, "incomplete For");

        var body = Block(Items(Field(node, "body")));

        // `for i in range(a, b, step)` is a counted loop, and says so: it becomes the same FOR node a
        // C# for-loop produces, so both render identically.
        if (Kind(target.Value) == "Name" && Range(iterable.Value) is { } range)
        {
            var name = Text(target.Value, "id") ?? "?";
            var variable = new MetacodeIdentifier(name) { Origin = Origin(target.Value) };
            MetacodeStatement initializer = new MetacodeLocalVariable(name, null, range.Start) { Origin = Origin(node) };
            var condition = new MetacodeBinary(
                range.StepIsNegative ? MetacodeBinaryOperator.GreaterThan : MetacodeBinaryOperator.LessThan,
                variable,
                range.End) { Origin = Origin(node) };
            MetacodeStatement increment = new MetacodeAssignment(
                variable,
                range.StepIsNegative ? MetacodeAssignmentOperator.Subtract : MetacodeAssignmentOperator.Add,
                range.Step) { Origin = Origin(node) };

            return new MetacodeFor(initializer, condition, increment, body) { Origin = Origin(node) };
        }

        return new MetacodeForEach(
            Target(target.Value),
            target.Value.TryGetProperty("annotation", out var annotation) ? new MetacodeTypeRef(Name(annotation) ?? "?") : null,
            Expression(iterable.Value),
            body) { Origin = Origin(node) };
    }

    /// <summary>A loop variable, or the names of a tuple target (<c>for k, v in …</c>).</summary>
    private static string Target(JsonElement target) => Kind(target) switch
    {
        "Name" => Text(target, "id") ?? "?",
        "Tuple" or "List" => string.Join(", ", Items(Field(target, "elts")).Select(element => Name(element) ?? "?")),
        _ => Name(target) ?? "?",
    };

    private (MetacodeExpression Start, MetacodeExpression End, MetacodeExpression Step, bool StepIsNegative)? Range(JsonElement iterable)
    {
        if (Kind(iterable) != "Call" || Field(iterable, "func") is not { } func || Kind(func) != "Name"
            || Text(func, "id") != "range")
        {
            return null;
        }

        var arguments = Items(Field(iterable, "args")).ToList();
        if (arguments.Count is < 1 or > 3)
            return null;

        var steps = Field(iterable, "keywords");
        if (steps is not null && steps.Value.GetArrayLength() > 0)
            return null;

        var start = arguments.Count == 1 ? MetacodeLiteral.Number("0") : Expression(arguments[0]);
        var end = arguments.Count == 1 ? Expression(arguments[0]) : Expression(arguments[1]);
        var step = arguments.Count == 3 ? Expression(arguments[2]) : MetacodeLiteral.Number("1");
        var negative = arguments.Count == 3 && Kind(arguments[2]) == "UnaryOp" && Op(arguments[2]) == "USub";
        return (start, end, step, negative);
    }

    private MetacodeStatement Try(JsonElement node)
    {
        var catches = new List<MetacodeCatch>();
        foreach (var handler in Items(Field(node, "handlers")))
        {
            var type = Field(handler, "type");
            catches.Add(new MetacodeCatch(
                type is null ? null : new MetacodeTypeRef(Name(type.Value) ?? Kind(type.Value)),
                Text(handler, "name"),
                Block(Items(Field(handler, "body")))) { Origin = Origin(handler) });
        }

        var @else = Field(node, "orelse");
        var @finally = Field(node, "finalbody");
        return new MetacodeTry(
            Block(Items(Field(node, "body"))),
            catches,
            @finally is null || @finally.Value.GetArrayLength() == 0 ? null : Block(Items(@finally.Value)),
            @else is null || @else.Value.GetArrayLength() == 0 ? null : Block(Items(@else.Value))) { Origin = Origin(node) };
    }

    private MetacodeStatement With(JsonElement node)
    {
        var body = Block(Items(Field(node, "body")));
        var items = Items(Field(node, "items")).ToList();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            var context = Field(item, "context_expr");
            var target = Field(item, "optional_vars");
            var declaration = target is not null && Kind(target.Value) == "Name"
                ? new MetacodeLocalVariable(Text(target.Value, "id") ?? "?") { Origin = Origin(target.Value) }
                : null;

            body = new MetacodeBlock(new MetacodeStatement[]
            {
                new MetacodeUsing(
                    declaration,
                    context is null ? null : Expression(context.Value),
                    body) { Origin = Origin(item) },
            }) { Origin = Origin(node) };
        }

        return body;
    }

    /// <summary>
    /// <c>import os, numpy as np</c> and <c>from typing import List</c>. A relative import keeps its
    /// leading dots (<c>from . import x</c> is module <c>.</c>), so the module path is not lost.
    /// </summary>
    private MetacodeStatement Import(JsonElement node, bool isFrom)
    {
        var names = Items(Field(node, "names"))
            .Select(name => new MetacodeImportName(Text(name, "name") ?? "?", Text(name, "asname")))
            .ToList();

        if (!isFrom)
            return new MetacodeImport(names, null, false) { Origin = Origin(node) };

        var module = Text(node, "module");
        var level = Integer(node, "level");
        if (level > 0)
            module = new string('.', level) + (module ?? string.Empty);

        if (string.IsNullOrEmpty(module))
            return OpaqueStatement(node, "a relative import without a module");

        return new MetacodeImport(names, module, true) { Origin = Origin(node) };
    }

    private MetacodeExpression Expression(JsonElement node)
    {
        var origin = Origin(node);
        switch (Kind(node))
        {
            case "Constant":
                return Constant(node);

            case "Name":
            {
                var id = Text(node, "id") ?? "?";
                // A receiver is a receiver: `self` prints as `this`, exactly like C#'s or C++'s.
                if (_inMethod && id == _receiver)
                    id = "this";
                return new MetacodeIdentifier(id) { Origin = origin };
            }

            case "Attribute":
            {
                var value = Field(node, "value");
                var attribute = Text(node, "attr") ?? "?";
                return value is null
                    ? new MetacodeIdentifier(attribute) { Origin = origin }
                    : new MetacodeMemberAccess(Expression(value.Value), attribute) { Origin = origin };
            }

            case "Call":
                return Call(node);

            case "BinOp":
            {
                var left = Field(node, "left");
                var right = Field(node, "right");
                if (left is null || right is null)
                    return Opaque(node);
                var @operator = Op(node) switch
                {
                    "Add" => MetacodeBinaryOperator.Add,
                    "Sub" => MetacodeBinaryOperator.Subtract,
                    "Mult" => MetacodeBinaryOperator.Multiply,
                    "Div" => MetacodeBinaryOperator.Divide,
                    "FloorDiv" => MetacodeBinaryOperator.IntegerDivide,
                    "Mod" => MetacodeBinaryOperator.Modulo,
                    "Pow" => MetacodeBinaryOperator.Power,
                    "LShift" => MetacodeBinaryOperator.ShiftLeft,
                    "RShift" => MetacodeBinaryOperator.ShiftRight,
                    "BitOr" => MetacodeBinaryOperator.BitwiseOr,
                    "BitAnd" => MetacodeBinaryOperator.BitwiseAnd,
                    "BitXor" => MetacodeBinaryOperator.BitwiseXor,
                    _ => (MetacodeBinaryOperator?)null,
                };

                return @operator is null
                    ? Opaque(node)
                    : new MetacodeBinary(@operator.Value, Expression(left.Value), Expression(right.Value)) { Origin = origin };
            }

            case "UnaryOp":
            {
                var operand = Field(node, "operand");
                if (operand is null)
                    return Opaque(node);
                var @operator = Op(node) switch
                {
                    "Not" => MetacodeUnaryOperator.Not,
                    "USub" => MetacodeUnaryOperator.Negate,
                    "UAdd" => MetacodeUnaryOperator.Plus,
                    "Invert" => MetacodeUnaryOperator.BitwiseNot,
                    _ => (MetacodeUnaryOperator?)null,
                };

                return @operator is null
                    ? Opaque(node)
                    : new MetacodeUnary(@operator.Value, Expression(operand.Value)) { Origin = origin };
            }

            case "BoolOp":
            {
                var values = Items(Field(node, "values")).Select(Expression).ToList();
                if (values.Count == 0)
                    return Opaque(node);
                var @operator = Op(node) == "Or" ? MetacodeBinaryOperator.LogicalOr : MetacodeBinaryOperator.LogicalAnd;
                return values.Skip(1).Aggregate(values[0], (left, right) => new MetacodeBinary(@operator, left, right) { Origin = origin });
            }

            case "Compare":
                return Compare(node);

            case "Subscript":
            {
                var value = Field(node, "value");
                var slice = Field(node, "slice");
                if (value is null || slice is null)
                    return Opaque(node);
                var index = Kind(slice.Value) == "Slice" ? Slice(slice.Value) : Expression(slice.Value);
                return new MetacodeIndex(Expression(value.Value), new[] { index }) { Origin = origin };
            }

            case "List":
                return new MetacodeCollection(MetacodeCollectionKind.List, Items(Field(node, "elts")).Select(Expression).ToList())
                {
                    Origin = origin,
                };

            case "Tuple":
                return new MetacodeCollection(MetacodeCollectionKind.Tuple, Items(Field(node, "elts")).Select(Expression).ToList())
                {
                    Origin = origin,
                };

            case "Set":
                return new MetacodeCollection(MetacodeCollectionKind.Set, Items(Field(node, "elts")).Select(Expression).ToList())
                {
                    Origin = origin,
                };

            case "Dict":
            {
                var keys = Items(Field(node, "keys")).ToList();
                var values = Items(Field(node, "values")).ToList();
                var entries = new List<MetacodeDictionaryEntry>();
                for (var i = 0; i < keys.Count && i < values.Count; i++)
                {
                    if (keys[i].ValueKind == JsonValueKind.Null)
                        continue;
                    entries.Add(new MetacodeDictionaryEntry(Expression(keys[i]), Expression(values[i])) { Origin = origin });
                }

                return new MetacodeDictionary(entries) { Origin = origin };
            }

            case "JoinedStr":
                return JoinedString(node);

            case "IfExp":
            {
                var test = Field(node, "test");
                var body = Field(node, "body");
                var @else = Field(node, "orelse");
                if (test is null || body is null || @else is null)
                    return Opaque(node);
                return new MetacodeConditional(Expression(test.Value), Expression(body.Value), Expression(@else.Value)) { Origin = origin };
            }

            case "Lambda":
            {
                var arguments = Field(node, "args");
                var body = Field(node, "body");
                var parameters = arguments is null
                    ? new List<MetacodeParameter>()
                    : Items(Field(arguments.Value, "args")).Select(Parameter).ToList();
                return body is null
                    ? Opaque(node)
                    : new MetacodeLambda(parameters, Expression: Expression(body.Value)) { Origin = origin };
            }

            case "Await":
            {
                var value = Field(node, "value");
                return value is null ? Opaque(node) : new MetacodeAwait(Expression(value.Value)) { Origin = origin };
            }

            case "NamedExpr":
            {
                var target = Field(node, "target");
                var value = Field(node, "value");
                if (target is null || value is null)
                    return Opaque(node);
                return new MetacodeAssignmentExpression(
                    Expression(target.Value),
                    MetacodeAssignmentOperator.Assign,
                    Expression(value.Value)) { Origin = origin };
            }

            case "Starred" or "Yield" or "YieldFrom":
                return Opaque(node);

            case "ListComp":
                return Comprehension(node, MetacodeComprehensionKind.List);

            case "SetComp":
                return Comprehension(node, MetacodeComprehensionKind.Set);

            case "DictComp":
                return Comprehension(node, MetacodeComprehensionKind.Dictionary);

            case "GeneratorExp":
                return Comprehension(node, MetacodeComprehensionKind.Generator);

            default:
                return Opaque(node);
        }
    }

    /// <summary>
    /// A comprehension: the element it builds plus every generator, in source order, each with its own
    /// <c>if</c> filters — the order is what makes a nested comprehension mean what it means.
    /// </summary>
    private MetacodeExpression Comprehension(JsonElement node, MetacodeComprehensionKind kind)
    {
        JsonElement? element = Field(node, kind == MetacodeComprehensionKind.Dictionary ? "key" : "elt");
        JsonElement? value = kind == MetacodeComprehensionKind.Dictionary ? Field(node, "value") : null;
        if (element is null || (kind == MetacodeComprehensionKind.Dictionary && value is null))
            return Opaque(node);

        var clauses = new List<MetacodeComprehensionClause>();
        foreach (var generator in Items(Field(node, "generators")))
        {
            JsonElement? target = Field(generator, "target");
            JsonElement? source = Field(generator, "iter");
            if (target is null || source is null)
                return Opaque(node);

            clauses.Add(new MetacodeComprehensionClause(
                MetacodeComprehensionClauseKind.For,
                Targets(target.Value),
                Expression(source.Value)) { Origin = Origin(generator) });

            foreach (var filter in Items(Field(generator, "ifs")))
                clauses.Add(new MetacodeComprehensionClause(
                    MetacodeComprehensionClauseKind.If,
                    null,
                    Expression(filter)) { Origin = Origin(filter) });
        }

        if (clauses.Count == 0)
            return Opaque(node);

        return new MetacodeComprehension(
            kind,
            Expression(element.Value),
            clauses,
            value is null ? null : Expression(value.Value)) { Origin = Origin(node) };
    }

    /// <summary>The names a comprehension binds: one, or the parts of a tuple target.</summary>
    private static IReadOnlyList<string> Targets(JsonElement target) => Kind(target) switch
    {
        "Name" => new List<string> { Text(target, "id") ?? "?" },
        "Tuple" or "List" => Items(Field(target, "elts")).SelectMany(Targets).ToList(),
        _ => new List<string> { Name(target) ?? "?" },
    };

    private MetacodeExpression Constant(JsonElement node)
    {
        var origin = Origin(node);
        var value = node.TryGetProperty("value", out var raw) ? raw : default;
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return MetacodeLiteral.True with { Origin = origin };
            case JsonValueKind.False:
                return MetacodeLiteral.False with { Origin = origin };
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return MetacodeLiteral.Null with { Origin = origin };
            case JsonValueKind.Number:
                return new MetacodeLiteral(MetacodeLiteralKind.Number, value.GetRawText()) { Origin = origin };
            case JsonValueKind.String:
                return new MetacodeLiteral(MetacodeLiteralKind.String, Quote(value.GetString()!)) { Origin = origin };
            default:
                return new MetacodeLiteral(MetacodeLiteralKind.Raw, value.GetRawText()) { Origin = origin };
        }
    }

    private string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", string.Empty) + "\"";

    private MetacodeExpression Call(JsonElement node)
    {
        var origin = Origin(node);
        var func = Field(node, "func");
        if (func is null)
            return Opaque(node);

        var arguments = new List<MetacodeArgument>();
        foreach (var argument in Items(Field(node, "args")))
            arguments.Add(new MetacodeArgument(null, Expression(argument)));

        foreach (var keyword in Items(Field(node, "keywords")))
        {
            var value = Field(keyword, "value");
            if (value is null)
                continue;
            arguments.Add(new MetacodeArgument(Text(keyword, "arg"), Expression(value.Value)));
        }

        return new MetacodeInvocation(Expression(func.Value), arguments) { Origin = origin };
    }

    private MetacodeExpression Compare(JsonElement node)
    {
        var origin = Origin(node);
        var left = Field(node, "left");
        var operators = Items(Field(node, "ops")).ToList();
        var comparators = Items(Field(node, "comparators")).ToList();
        if (left is null || operators.Count == 0 || operators.Count != comparators.Count)
            return Opaque(node);

        var comparisons = new List<MetacodeExpression>(operators.Count);
        var previous = Expression(left.Value);
        for (var i = 0; i < operators.Count; i++)
        {
            var right = Expression(comparators[i]);
            var @operator = OperatorOf(operators[i]) switch
            {
                "Eq" => MetacodeBinaryOperator.Equal,
                "NotEq" => MetacodeBinaryOperator.NotEqual,
                "Lt" => MetacodeBinaryOperator.LessThan,
                "LtE" => MetacodeBinaryOperator.LessThanOrEqual,
                "Gt" => MetacodeBinaryOperator.GreaterThan,
                "GtE" => MetacodeBinaryOperator.GreaterThanOrEqual,
                // `is` and `==` mean the same thing once the types are gone from the picture.
                "Is" => MetacodeBinaryOperator.Equal,
                "IsNot" => MetacodeBinaryOperator.NotEqual,
                "In" => MetacodeBinaryOperator.Contains,
                "NotIn" => MetacodeBinaryOperator.NotContains,
                _ => (MetacodeBinaryOperator?)null,
            };

            if (@operator is null)
                return Opaque(node);

            comparisons.Add(new MetacodeBinary(@operator.Value, previous, right) { Origin = origin });
            previous = right;
        }

        // Python's `a < b < c` already means `a < b and b < c`, and that is how it prints.
        return comparisons.Skip(1).Aggregate(
            comparisons[0],
            (leftComparison, rightComparison) => new MetacodeBinary(MetacodeBinaryOperator.LogicalAnd, leftComparison, rightComparison)
            {
                Origin = origin,
            });
    }

    private MetacodeExpression Slice(JsonElement node)
    {
        var lower = Field(node, "lower");
        var upper = Field(node, "upper");
        var step = Field(node, "step");
        return new MetacodeRange(
            lower is null ? null : Expression(lower.Value),
            upper is null ? null : Expression(upper.Value),
            step is null ? null : Expression(step.Value)) { Origin = Origin(node) };
    }

    private MetacodeExpression JoinedString(JsonElement node)
    {
        var parts = new List<MetacodeInterpolationPart>();
        foreach (var value in Items(Field(node, "values")))
        {
            if (Kind(value) == "Constant" && value.TryGetProperty("value", out var raw) && raw.ValueKind == JsonValueKind.String)
            {
                parts.Add(new MetacodeInterpolationPart(raw.GetString(), null));
                continue;
            }

            if (Kind(value) == "FormattedValue" && Field(value, "value") is { } inner)
            {
                parts.Add(new MetacodeInterpolationPart(null, Expression(inner)));
                continue;
            }

            parts.Add(new MetacodeInterpolationPart(null, Expression(value)));
        }

        return new MetacodeInterpolatedString(parts) { Origin = Origin(node) };
    }

    private MetacodeExpression Opaque(JsonElement node)
    {
        var reason = "unmodelled expression: " + Kind(node);
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.UnsupportedExpression, MetacodeDiagnosticSeverity.Warning, reason, Origin(node)));
        return new MetacodeOpaqueExpression(MetacodeLanguage.Python, Kind(node), reason) { Origin = Origin(node) };
    }

    private MetacodeStatement OpaqueStatement(JsonElement node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.UnsupportedStatement, MetacodeDiagnosticSeverity.Warning, reason, Origin(node)));
        return new MetacodeOpaqueStatement(MetacodeLanguage.Python, Kind(node), reason) { Origin = Origin(node) };
    }

    private static MetacodeAssignmentOperator AssignmentOperator(string op) => op switch
    {
        "Add" => MetacodeAssignmentOperator.Add,
        "Sub" => MetacodeAssignmentOperator.Subtract,
        "Mult" => MetacodeAssignmentOperator.Multiply,
        "Div" => MetacodeAssignmentOperator.Divide,
        "FloorDiv" => MetacodeAssignmentOperator.Divide,
        "Mod" => MetacodeAssignmentOperator.Modulo,
        "Pow" => MetacodeAssignmentOperator.Power,
        "LShift" => MetacodeAssignmentOperator.ShiftLeft,
        "RShift" => MetacodeAssignmentOperator.ShiftRight,
        "BitOr" => MetacodeAssignmentOperator.BitwiseOr,
        "BitAnd" => MetacodeAssignmentOperator.BitwiseAnd,
        "BitXor" => MetacodeAssignmentOperator.BitwiseXor,
        _ => MetacodeAssignmentOperator.Assign,
    };

    private static string Kind(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object ? string.Empty
        : node.TryGetProperty("_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()!
        : node.TryGetProperty("type", out var fallback) && fallback.ValueKind == JsonValueKind.String ? fallback.GetString()!
        : string.Empty;

    /// <summary>The operator of a BinOp/UnaryOp/BoolOp/AugAssign node.</summary>
    private static string Op(JsonElement node)
    {
        if (!node.TryGetProperty("op", out var op) && !node.TryGetProperty("_op", out op))
            return string.Empty;
        return OperatorOf(op);
    }

    /// <summary>An operator that may be a bare string or a node whose <c>_type</c> names it.</summary>
    private static string OperatorOf(JsonElement node) =>
        node.ValueKind == JsonValueKind.String ? node.GetString()! : Kind(node);

    private static string? Name(JsonElement node) => Kind(node) switch
    {
        "Name" => Text(node, "id"),
        "Attribute" => Text(node, "attr"),
        "Constant" when node.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
        _ => null,
    };

    private static JsonElement? Field(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement? array) =>
        array is null || array.Value.ValueKind != JsonValueKind.Array
            ? Enumerable.Empty<JsonElement>()
            : array.Value.EnumerateArray();

    private static MetacodeOrigin Origin(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return new MetacodeOrigin(MetacodeLanguage.Python);

        return new MetacodeOrigin(
            MetacodeLanguage.Python,
            null,
            Integer(node, "lineno"),
            Integer(node, "col_offset") + 1,
            Integer(node, "end_lineno"),
            Integer(node, "end_col_offset") + 1);
    }

    private static int Integer(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
}
