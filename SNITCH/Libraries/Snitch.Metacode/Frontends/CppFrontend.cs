using System.Text.Json;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads a Clang <c>-ast-dump=json</c> document (<c>clang++ -Xclang -ast-dump=json -fsyntax-only file.cpp</c>).
/// Clang's tree is deep and wraps every expression in implicit nodes; the wrapper kinds are unwrapped,
/// the rest is lowered like any other language. Templates, lambdas and range-for have no node yet and
/// are kept as opaque text with a diagnostic.
/// </summary>
public sealed class CppFrontend : ILanguageFrontend
{
    public string Name => "C++";

    public MetacodeLanguage Language => MetacodeLanguage.Cpp;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Cpp
        || (source.Language is null && source.Path is not null
            && new[] { ".cpp", ".cc", ".cxx", ".c", ".hpp", ".hh", ".hxx", ".h" }
                .Any(extension => source.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var diagnostics = new List<MetacodeDiagnostic>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                MaxDepth = 512,
            });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.ParseError,
                MetacodeDiagnosticSeverity.Error,
                "The C++ input is not JSON: " + exception.Message + ". Pass clang's -ast-dump=json output, not the .cpp source.",
                MetacodeOrigin.Unknown));
            return new MetacodeDocument(source.Name, MetacodeLanguage.Cpp, Array.Empty<MetacodeModule>(), diagnostics);
        }

        var lowering = new CppLowering(diagnostics);
        var modules = lowering.Modules(document.RootElement, source.Name);
        return new MetacodeDocument(source.Name, MetacodeLanguage.Cpp, modules, diagnostics);
    }
}

internal enum CppAccess
{
    Public,
    Protected,
    Private,
}

internal sealed class CppLowering
{
    private readonly List<MetacodeDiagnostic> _diagnostics;

    public CppLowering(List<MetacodeDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public IReadOnlyList<MetacodeModule> Modules(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<MetacodeModule>();
        }

        // clang prints the TranslationUnitDecl as the root object.
        var nodes = Kind(root) == "TranslationUnitDecl" ? Items(Field(root, "inner")) : new[] { root };

        var modules = new List<MetacodeModule>();
        var loose = new List<MetacodeDeclaration>();
        CollectNamespaces(nodes, string.Empty, modules, loose);
        if (loose.Count > 0)
            modules.Insert(0, new MetacodeModule(name, loose, MetacodeModuleKind.TranslationUnit));

        return modules;
    }

    private void CollectNamespaces(
        IEnumerable<JsonElement> nodes,
        string prefix,
        List<MetacodeModule> modules,
        List<MetacodeDeclaration> loose)
    {
        foreach (var node in nodes)
        {
            if (Kind(node) == "LinkageSpecDecl")
            {
                CollectNamespaces(Items(Field(node, "inner")), prefix, modules, loose);
                continue;
            }

            if (Kind(node) != "NamespaceDecl")
            {
                loose.AddRange(Declarations(node, CppAccess.Public));
                continue;
            }

            var name = Text(node, "name");
            var full = string.IsNullOrEmpty(name)
                ? prefix
                : prefix.Length == 0 ? name : prefix + "." + name;

            var declarations = new List<MetacodeDeclaration>();
            var nested = new List<JsonElement>();
            foreach (var child in Items(Field(node, "inner")))
            {
                if (Kind(child) == "NamespaceDecl")
                    nested.Add(child);
                else
                    declarations.AddRange(Declarations(child, CppAccess.Public));
            }

            modules.Add(new MetacodeModule(full.Length == 0 ? "<anonymous>" : full, declarations, MetacodeModuleKind.Namespace));
            CollectNamespaces(nested, full, modules, loose);
        }
    }

    private IEnumerable<MetacodeDeclaration> Declarations(JsonElement node, CppAccess access)
    {
        switch (Kind(node))
        {
            case "CXXRecordDecl":
            case "RecordDecl":
                if (IsImplicit(node) || !IsCompleteDefinition(node))
                    yield break;
                yield return Type(node);
                break;

            case "EnumDecl":
                if (IsImplicit(node))
                    yield break;
                yield return Enumeration(node);
                break;

            case "FunctionDecl":
                yield return Function(node, MetacodeFunctionKind.Function, MetacodeModifiers.None);
                break;

            case "CXXMethodDecl":
            case "CXXConversionDecl":
                yield return Function(node, MetacodeFunctionKind.Method, Access(access));
                break;

            case "CXXConstructorDecl":
                yield return Function(node, MetacodeFunctionKind.Constructor, Access(access));
                break;

            case "CXXDestructorDecl":
                yield return Function(node, MetacodeFunctionKind.Destructor, Access(access));
                break;

            case "FunctionTemplateDecl":
                yield return FunctionTemplate(node);
                break;

            case "ClassTemplateDecl":
                yield return ClassTemplate(node);
                break;

            case "ClassTemplateSpecializationDecl":
                yield return Type(node);
                break;

            case "TypeAliasDecl":
            case "TypedefDecl":
                // A translation unit carries implicit typedefs (__int128_t and friends) that no source
                // wrote; they are the compiler's, not the program's.
                if (IsImplicit(node))
                    yield break;
                yield return new MetacodeTypeAlias(Text(node, "name") ?? "?", TypeRef(node)) { Origin = Origin(node) };
                break;

            case "UsingDecl":
            case "UsingShadowDecl":
            case "FriendDecl":
            case "StaticAssertDecl":
                yield return OpaqueProperty(node, "no pseudocode node for " + Kind(node));
                break;

            case "FieldDecl":
                yield return new MetacodeField(
                    Text(node, "name") ?? "?",
                    TypeRef(node),
                    Access(access)) { Origin = Origin(node) };
                break;

            case "VarDecl":
                yield return new MetacodeField(
                    Text(node, "name") ?? "?",
                    TypeRef(node),
                    AttributeModifiers(node),
                    Initializer(node)) { Origin = Origin(node) };
                break;

            case "LinkageSpecDecl":
                foreach (var child in Items(Field(node, "inner")))
                foreach (var declaration in Declarations(child, access))
                    yield return declaration;
                break;

            case "ParmVarDecl":
            case "IndirectFieldDecl":
                yield break;

            default:
                yield return OpaqueProperty(node, "unmodelled declaration: " + Kind(node));
                break;
        }
    }

    private MetacodeTypeDeclaration Type(JsonElement node)
    {
        var kind = Text(node, "tagUsed") switch
        {
            "struct" => MetacodeTypeKind.Struct,
            "union" => MetacodeTypeKind.Union,
            _ => MetacodeTypeKind.Class,
        };

        // A class member is private until an access specifier says otherwise; a struct is the reverse.
        var access = kind == MetacodeTypeKind.Class ? CppAccess.Private : CppAccess.Public;
        var members = new List<MetacodeDeclaration>();
        foreach (var child in Items(Field(node, "inner")))
        {
            if (Kind(child) == "AccessSpecDecl")
            {
                access = Text(child, "access") switch
                {
                    "public" => CppAccess.Public,
                    "protected" => CppAccess.Protected,
                    _ => CppAccess.Private,
                };
                continue;
            }

            members.AddRange(Declarations(child, access));
        }

        return new MetacodeTypeDeclaration(
            Text(node, "name") ?? "?",
            kind,
            members,
            Modifiers(node),
            Items(Field(node, "bases")).Select(b => new MetacodeTypeRef(Text(b, "type.qualType") ?? "?")).ToList())
        {
            Origin = Origin(node),
        };
    }

    private MetacodeDeclaration Enumeration(JsonElement node)
    {
        var members = Items(Field(node, "inner"))
            .Where(child => Kind(child) == "EnumConstantDecl")
            .Select(child => (MetacodeDeclaration)new MetacodeEnumMember(
                Text(child, "name") ?? "?",
                Items(Field(child, "inner")).FirstOrDefault() is { ValueKind: JsonValueKind.Object } value
                    ? Expression(value)
                    : null) { Origin = Origin(child) })
            .ToList();

        return new MetacodeTypeDeclaration(Text(node, "name") ?? "?", MetacodeTypeKind.Enum, members) { Origin = Origin(node) };
    }

    private MetacodeFunction Function(
        JsonElement node,
        MetacodeFunctionKind kind,
        MetacodeModifiers access,
        IReadOnlyList<MetacodeTypeParameter>? typeParameters = null)
    {
        var parameters = Items(Field(node, "inner"))
            .Where(child => Kind(child) == "ParmVarDecl")
            .Select(child => new MetacodeParameter(
                Text(child, "name") ?? "?",
                TypeRef(child),
                DefaultValue(child)) { Origin = Origin(child) })
            .ToList();

        var body = Items(Field(node, "inner")).FirstOrDefault(child => Kind(child) == "CompoundStmt");
        var returnType = kind is MetacodeFunctionKind.Constructor or MetacodeFunctionKind.Destructor
            ? null
            : TypeRefOf(ReturnTypeText(Text(node, "type.qualType")));

        return new MetacodeFunction(
            Text(node, "name") ?? "?",
            kind,
            parameters,
            returnType,
            body.ValueKind == JsonValueKind.Object ? Block(body) : null,
            access | Modifiers(node),
            typeParameters) { Origin = Origin(node) };
    }

    /// <summary>
    /// A function template. clang keeps the template parameters next to the function it stamps out, so
    /// the declaration is the inner <c>FunctionDecl</c> plus the <c>T</c>s in front of it.
    /// </summary>
    private MetacodeDeclaration FunctionTemplate(JsonElement node)
    {
        var function = Items(Field(node, "inner")).FirstOrDefault(child => Kind(child) == "FunctionDecl");
        if (function.ValueKind != JsonValueKind.Object)
            return OpaqueProperty(node, "a function template without its function declaration");

        return Function(function, MetacodeFunctionKind.Function, MetacodeModifiers.None, TypeParameters(node));
    }

    /// <summary>A class template: the record declaration inside, with the template parameters as its type parameters.</summary>
    private MetacodeDeclaration ClassTemplate(JsonElement node)
    {
        var record = Items(Field(node, "inner")).FirstOrDefault(child => Kind(child) is "CXXRecordDecl" or "RecordDecl");
        if (record.ValueKind != JsonValueKind.Object)
            return OpaqueProperty(node, "a class template without its record declaration");

        return Type(record) with { TypeParameters = TypeParameters(node) };
    }

    private static IReadOnlyList<MetacodeTypeParameter> TypeParameters(JsonElement node) =>
        Items(Field(node, "inner"))
            .Where(child => Kind(child) == "TemplateTypeParmDecl")
            .Select(child => new MetacodeTypeParameter(Text(child, "name") ?? "?"))
            .ToList();

    private MetacodeExpression? DefaultValue(JsonElement node)
    {
        foreach (var child in Items(Field(node, "inner")))
        {
            if (Kind(child) is "IntegerLiteral" or "FloatingLiteral" or "StringLiteral" or "CXXBoolLiteralExpr"
                or "UnaryOperator" or "BinaryOperator" or "DeclRefExpr" or "CXXNullPtrLiteralExpr")
            {
                return Expression(child);
            }
        }

        return null;
    }

    private MetacodeExpression? Initializer(JsonElement node)
    {
        foreach (var child in Items(Field(node, "inner")))
        {
            if (Kind(child) is "IntegerLiteral" or "FloatingLiteral" or "StringLiteral" or "CXXBoolLiteralExpr"
                or "CXXConstructExpr" or "InitListExpr" or "CallExpr" or "CXXNewExpr" or "BinaryOperator"
                or "UnaryOperator" or "DeclRefExpr" or "CXXNullPtrLiteralExpr" or "ConditionalOperator"
                or "LambdaExpr")
            {
                return Expression(child);
            }
        }

        return null;
    }

    private MetacodeBlock Block(JsonElement compound)
    {
        var statements = new List<MetacodeStatement>();
        foreach (var child in Items(Field(compound, "inner")))
            statements.AddRange(Statements(child));
        return new MetacodeBlock(statements) { Origin = Origin(compound) };
    }

    private IEnumerable<MetacodeStatement> Statements(JsonElement node)
    {
        var statements = new List<MetacodeStatement>();
        var statement = Statement(node);
        if (statement is not null)
            statements.Add(statement);

        return statements;
    }

    private MetacodeStatement? Statement(JsonElement node)
    {
        var origin = Origin(node);
        switch (Kind(node))
        {
            case "CompoundStmt":
                return Block(node);

            case "DeclStmt":
            {
                var variables = Items(Field(node, "inner"))
                    .Where(child => Kind(child) == "VarDecl")
                    .Select(child => (MetacodeStatement)new MetacodeLocalVariable(
                        Text(child, "name") ?? "?",
                        TypeRef(child),
                        Initializer(child)) { Origin = Origin(child) })
                    .ToList();

                return variables.Count switch
                {
                    0 => null,
                    1 => variables[0],
                    _ => new MetacodeBlock(variables) { Origin = origin },
                };
            }

            case "ReturnStmt":
            {
                var value = Items(Field(node, "inner")).FirstOrDefault();
                return new MetacodeReturn(value.ValueKind == JsonValueKind.Object ? Expression(value) : null) { Origin = origin };
            }

            case "IfStmt":
                return If(node);

            case "WhileStmt":
            {
                var children = Items(Field(node, "inner")).ToList();
                return children.Count < 2
                    ? OpaqueStatement(node, "while without a condition or a body")
                    : new MetacodeWhile(Expression(children[0]), Block(children[^1])) { Origin = origin };
            }

            case "DoStmt":
            {
                var children = Items(Field(node, "inner")).ToList();
                return children.Count < 2
                    ? OpaqueStatement(node, "do without a condition or a body")
                    : new MetacodeDoWhile(Block(children[0]), Expression(children[^1])) { Origin = origin };
            }

            case "ForStmt":
                return For(node);

            case "CXXForRangeStmt":
                return ForRange(node);

            case "SwitchStmt":
                return Switch(node);

            case "BreakStmt":
                return new MetacodeBreak { Origin = origin };

            case "ContinueStmt":
                return new MetacodeContinue { Origin = origin };

            case "NullStmt":
                return null;

            case "GotoStmt":
                return OpaqueStatement(node, "goto has no node yet");

            case "LabelStmt":
            {
                var children = Items(Field(node, "inner")).ToList();
                return children.Count == 0
                    ? null
                    : new MetacodeLabel(Text(node, "name") ?? "?", Statement(children[^1]) ?? MetacodeBlock.Empty) { Origin = origin };
            }

            case "CXXTryStmt":
            {
                var children = Items(Field(node, "inner")).ToList();
                if (children.Count == 0)
                    return OpaqueStatement(node, "try without a body");

                var catches = children.Skip(1)
                    .Where(child => Kind(child) == "CXXCatchStmt")
                    .Select(child =>
                    {
                        var parts = Items(Field(child, "inner")).ToList();
                        var declaration = parts.FirstOrDefault(part => Kind(part) == "VarDecl");
                        var body = parts.FirstOrDefault(part => Kind(part) == "CompoundStmt");
                        return new MetacodeCatch(
                            declaration.ValueKind == JsonValueKind.Object ? TypeRef(declaration) : null,
                            declaration.ValueKind == JsonValueKind.Object ? Text(declaration, "name") : null,
                            body.ValueKind == JsonValueKind.Object ? Block(body) : MetacodeBlock.Empty) { Origin = Origin(child) };
                    })
                    .ToList();

                return new MetacodeTry(Block(children[0]), catches) { Origin = origin };
            }

            case "CXXThrowExpr":
            {
                var value = Items(Field(node, "inner")).FirstOrDefault();
                return new MetacodeThrow(value.ValueKind == JsonValueKind.Object ? Expression(value) : null) { Origin = origin };
            }

            case "CaseStmt":
            case "DefaultStmt":
                return OpaqueStatement(node, "a case outside a switch body");

            default:
                return ExpressionOrStatement(node, origin);
        }
    }

    private MetacodeStatement? ExpressionOrStatement(JsonElement node, MetacodeOrigin origin)
    {
        if (!IsExpression(node))
            return OpaqueStatement(node, "unmodelled statement: " + Kind(node));

        var expression = Expression(node);
        switch (expression)
        {
            case MetacodeAssignmentExpression assignment:
                return new MetacodeAssignment(assignment.Target, assignment.Operator, assignment.Value) { Origin = origin };
            // `i++` in statement position is `i += 1`, the same statement every other frontend emits.
            case MetacodeUnary { Operator: MetacodeUnaryOperator.Increment or MetacodeUnaryOperator.Decrement } unary:
                return new MetacodeAssignment(
                    unary.Operand,
                    unary.Operator == MetacodeUnaryOperator.Increment ? MetacodeAssignmentOperator.Add : MetacodeAssignmentOperator.Subtract,
                    new MetacodeLiteral(MetacodeLiteralKind.Number, "1") { Origin = origin }) { Origin = origin };
            default:
                return new MetacodeExpressionStatement(expression) { Origin = origin };
        }
    }

    private static bool IsExpression(JsonElement node) => Kind(node) switch
    {
        "IntegerLiteral" or "FloatingLiteral" or "StringLiteral" or "CharacterLiteral" or "CXXBoolLiteralExpr"
            or "CXXNullPtrLiteralExpr" or "GNUNullExpr" or "DeclRefExpr" or "MemberExpr" or "CXXThisExpr"
            or "CallExpr" or "CXXMemberCallExpr" or "CXXOperatorCallExpr" or "BinaryOperator"
            or "CompoundAssignOperator" or "UnaryOperator" or "ParenExpr" or "ImplicitCastExpr"
            or "CStyleCastExpr" or "CXXStaticCastExpr" or "CXXReinterpretCastExpr" or "CXXConstCastExpr"
            or "CXXDynamicCastExpr" or "CXXConstructExpr" or "CXXNewExpr" or "ConditionalOperator"
            or "ArraySubscriptExpr" or "InitListExpr" or "ConstantExpr" or "ExprWithCleanups"
            or "MaterializeTemporaryExpr" or "CXXBindTemporaryExpr" or "CXXFunctionalCastExpr"
            or "SubstNonTypeTemplateParmExpr" or "LambdaExpr" => true,
        _ => false,
    };

    private MetacodeStatement If(JsonElement node)
    {
        var children = Items(Field(node, "inner")).ToList();
        var hasElse = Bool(node, "hasElse");

        // `if (int x = f())` puts a DeclStmt in front of the condition.
        var index = 0;
        while (index < children.Count && Kind(children[index]) == "DeclStmt")
            index++;

        var condition = index < children.Count ? Expression(children[index]) : MetacodeLiteral.True;
        var then = index + 1 < children.Count ? Block(children[index + 1]) : MetacodeBlock.Empty;
        var @else = hasElse && index + 2 < children.Count ? Block(children[index + 2]) : null;
        return new MetacodeIf(condition, then, @else) { Origin = Origin(node) };
    }

    private MetacodeStatement For(JsonElement node)
    {
        // clang always prints four slots: init, condition, increment, body — with nulls for the holes.
        var children = Items(Field(node, "inner")).ToList();
        if (children.Count != 4)
            return OpaqueStatement(node, "for with an unexpected shape");

        var initializer = children[0].ValueKind == JsonValueKind.Object ? Statement(children[0]) : null;
        var condition = children[1].ValueKind == JsonValueKind.Object ? Expression(children[1]) : null;
        var increment = children[2].ValueKind == JsonValueKind.Object ? Statement(children[2]) : null;
        var body = children[3].ValueKind == JsonValueKind.Object ? Block(children[3]) : MetacodeBlock.Empty;
        return new MetacodeFor(initializer, condition, increment, body) { Origin = Origin(node) };
    }

    /// <summary>
    /// clang prints a range-based <c>for</c> as its own desugaring: the hidden range variable, the
    /// begin/end calls, the condition, the increment, the loop variable and the body. The model needs
    /// two of those — the loop variable and the expression it ranges over — and they are picked by role
    /// rather than by position, so a clang that adds or drops a synthetic slot still reads.
    /// </summary>
    private MetacodeStatement ForRange(JsonElement node)
    {
        var children = Items(Field(node, "inner")).Where(child => child.ValueKind == JsonValueKind.Object).ToList();
        var body = children.LastOrDefault(child => Kind(child) == "CompoundStmt");
        var declarations = children.Where(child => Kind(child) == "DeclStmt").ToList();

        // Two declarations: __range first, the loop variable second.
        if (body.ValueKind != JsonValueKind.Object || declarations.Count < 2)
            return OpaqueStatement(node, "range-based for without a loop variable or a body");

        var variable = Items(Field(declarations[1], "inner")).FirstOrDefault(child => Kind(child) == "VarDecl");
        var range = Items(Field(declarations[0], "inner")).FirstOrDefault(child => Kind(child) == "VarDecl");
        var source = Items(Field(range, "inner")).FirstOrDefault(child => child.ValueKind == JsonValueKind.Object);
        if (variable.ValueKind != JsonValueKind.Object || source.ValueKind != JsonValueKind.Object)
            return OpaqueStatement(node, "range-based for without a range expression");

        return new MetacodeForEach(
            Text(variable, "name") ?? "?",
            TypeRef(variable),
            Expression(source),
            Block(body)) { Origin = Origin(node) };
    }

    private MetacodeStatement Switch(JsonElement node)
    {
        var children = Items(Field(node, "inner")).ToList();
        if (children.Count < 2)
            return OpaqueStatement(node, "switch without a body");

        var cases = new List<MetacodeSwitchCase>();
        foreach (var child in Items(Field(children[1], "inner")))
        {
            if (Kind(child) is "CaseStmt" or "DefaultStmt")
                cases.Add(Case(child));
            else
                _diagnostics.Add(Diagnostic(MetacodeDiagnosticCodes.LossyLowering, "a statement before the first case is not kept", child));
        }

        return new MetacodeSwitch(Expression(children[0]), cases) { Origin = Origin(node) };
    }

    private MetacodeSwitchCase Case(JsonElement node)
    {
        var labels = new List<MetacodeExpression>();
        var isDefault = Kind(node) == "DefaultStmt";
        var body = MetacodeBlock.Empty;

        var current = node;
        while (true)
        {
            var children = Items(Field(current, "inner")).ToList();
            if (Kind(current) == "CaseStmt" && children.Count > 0)
                labels.Add(Expression(children[0]));

            var tail = children.Count > 1 ? children[^1] : default;
            if (tail.ValueKind == JsonValueKind.Object && Kind(tail) is "CaseStmt" or "DefaultStmt")
            {
                // Fall-through: clang nests the next case inside this one.
                isDefault |= Kind(tail) == "DefaultStmt";
                current = tail;
                continue;
            }

            if (tail.ValueKind == JsonValueKind.Object)
                body = Block(tail);
            break;
        }

        return new MetacodeSwitchCase(labels, body, isDefault) { Origin = Origin(node) };
    }

    private MetacodeExpression Expression(JsonElement node)
    {
        var origin = Origin(node);
        switch (Kind(node))
        {
            case "IntegerLiteral":
            case "FloatingLiteral":
                return new MetacodeLiteral(MetacodeLiteralKind.Number, Scalar(node, "value") ?? "?") { Origin = origin };

            case "StringLiteral":
                return new MetacodeLiteral(MetacodeLiteralKind.String, Quote(Scalar(node, "value") ?? string.Empty)) { Origin = origin };

            case "CharacterLiteral":
                return new MetacodeLiteral(
                    MetacodeLiteralKind.Character,
                    "'" + (char)(int.TryParse(Scalar(node, "value"), out var code) ? code : '?') + "'") { Origin = origin };

            case "CXXBoolLiteralExpr":
                return new MetacodeLiteral(MetacodeLiteralKind.Boolean, Bool(node, "value") ? "true" : "false") { Origin = origin };

            case "CXXNullPtrLiteralExpr":
            case "GNUNullExpr":
                return MetacodeLiteral.Null with { Origin = origin };

            case "DeclRefExpr":
                return new MetacodeIdentifier(ReferencedName(node) ?? Text(node, "name") ?? "?") { Origin = origin };

            case "CXXThisExpr":
                return new MetacodeIdentifier("this") { Origin = origin };

            case "MemberExpr":
            {
                var target = Items(Field(node, "inner")).FirstOrDefault();
                var name = Text(node, "name") ?? "?";
                return target.ValueKind == JsonValueKind.Object
                    ? new MetacodeMemberAccess(Expression(target), name) { Origin = origin }
                    : new MetacodeIdentifier(name) { Origin = origin };
            }

            case "CallExpr":
            case "CXXMemberCallExpr":
            case "CUDAKernelCallExpr":
                return Call(node);

            case "CXXOperatorCallExpr":
                return OperatorCall(node);

            case "BinaryOperator":
            case "CompoundAssignOperator":
                return Binary(node);

            case "UnaryOperator":
                return Unary(node);

            case "ParenExpr":
            case "ImplicitCastExpr":
            case "ConstantExpr":
            case "ExprWithCleanups":
            case "MaterializeTemporaryExpr":
            case "CXXBindTemporaryExpr":
            case "SubstNonTypeTemplateParmExpr":
            case "CXXFunctionalCastExpr":
            {
                var inner = Items(Field(node, "inner")).FirstOrDefault();
                return inner.ValueKind == JsonValueKind.Object ? Expression(inner) : Opaque(node);
            }

            case "CStyleCastExpr":
            case "CXXStaticCastExpr":
            case "CXXReinterpretCastExpr":
            case "CXXConstCastExpr":
            case "CXXDynamicCastExpr":
            {
                var inner = Items(Field(node, "inner")).FirstOrDefault();
                return inner.ValueKind == JsonValueKind.Object
                    ? new MetacodeCast(TypeRef(node), Expression(inner)) { Origin = origin }
                    : Opaque(node);
            }

            case "CXXConstructExpr":
            {
                var arguments = Items(Field(node, "inner"))
                    .Where(child => Kind(child) != "CXXDefaultArgExpr")
                    .Select(child => new MetacodeArgument(null, Expression(child)))
                    .ToList();
                return new MetacodeObjectCreation(TypeRef(node), arguments) { Origin = origin };
            }

            case "CXXNewExpr":
            {
                var arguments = Items(Field(node, "inner"))
                    .Where(child => Kind(child) != "CXXConstructExpr")
                    .Select(child => new MetacodeExpression[] { Expression(child) })
                    .SelectMany(e => e)
                    .ToList();
                return new MetacodeObjectCreation(TypeRef(node), arguments.Select(a => new MetacodeArgument(null, a)).ToList())
                {
                    Origin = origin,
                };
            }

            case "ConditionalOperator":
            {
                var children = Items(Field(node, "inner")).ToList();
                return children.Count < 3
                    ? Opaque(node)
                    : new MetacodeConditional(Expression(children[0]), Expression(children[1]), Expression(children[2])) { Origin = origin };
            }

            case "ArraySubscriptExpr":
            {
                var children = Items(Field(node, "inner")).ToList();
                return children.Count < 2
                    ? Opaque(node)
                    : new MetacodeIndex(Expression(children[0]), new[] { Expression(children[1]) }) { Origin = origin };
            }

            case "InitListExpr":
                return new MetacodeCollection(MetacodeCollectionKind.Array, Items(Field(node, "inner")).Select(Expression).ToList())
                {
                    Origin = origin,
                };

            case "LambdaExpr":
                return LambdaExpr(node);

            case "CXXDeleteExpr" or "StmtExpr" or "UnaryExprOrTypeTraitExpr" or "VAArgExpr"
                or "CXXNoexceptExpr" or "CXXUuidofExpr" or "CXXDefaultArgExpr" or "CXXPseudoDestructorExpr":
                return Opaque(node);

            default:
                return Opaque(node);
        }
    }

    /// <summary>
    /// A lambda. clang puts its signature in the closure type's <c>operator()</c>, so the parameters and
    /// the body are read from that method; when the body sits directly on the lambda instead, it is
    /// taken from there. Captures are not modelled — they are the closure's state, not its shape.
    /// </summary>
    private MetacodeExpression LambdaExpr(JsonElement node)
    {
        var found = FindLambdaMethod(node);
        if (found is null)
            return Opaque(node);

        var method = found.Value;
        var parameters = Items(Field(method, "inner"))
            .Where(child => Kind(child) == "ParmVarDecl")
            .Select(child => new MetacodeParameter(
                Text(child, "name") ?? "?",
                TypeRef(child),
                DefaultValue(child)) { Origin = Origin(child) })
            .ToList();

        var body = Items(Field(method, "inner")).FirstOrDefault(child => Kind(child) == "CompoundStmt");
        if (body.ValueKind != JsonValueKind.Object)
            body = Items(Field(node, "inner")).FirstOrDefault(child => Kind(child) == "CompoundStmt");

        return body.ValueKind == JsonValueKind.Object
            ? new MetacodeLambda(parameters, Body: Block(body)) { Origin = Origin(node) }
            : Opaque(node);
    }

    /// <summary>The <c>operator()</c> of a lambda's closure type, directly or inside its record declaration.</summary>
    private static JsonElement? FindLambdaMethod(JsonElement node)
    {
        foreach (var child in Items(Field(node, "inner")))
        {
            if (Kind(child) is "CXXMethodDecl" or "FunctionDecl")
                return child;

            if (Kind(child) != "CXXRecordDecl")
                continue;

            foreach (var member in Items(Field(child, "inner")))
            {
                if (Kind(member) is "CXXMethodDecl" or "FunctionDecl")
                    return member;
            }
        }

        return null;
    }

    private MetacodeExpression Call(JsonElement node)
    {
        var children = Items(Field(node, "inner")).ToList();
        if (children.Count == 0)
            return Opaque(node);

        var arguments = children.Skip(1)
            .Where(child => Kind(child) != "CXXDefaultArgExpr")
            .Select(child => new MetacodeArgument(null, Expression(child)))
            .ToList();
        return new MetacodeInvocation(Expression(children[0]), arguments) { Origin = Origin(node) };
    }

    private MetacodeExpression OperatorCall(JsonElement node)
    {
        var name = ReferencedName(node) ?? string.Empty;
        var children = Items(Field(node, "inner")).ToList();
        if (children.Count == 0)
            return Opaque(node);

        var arguments = children.Skip(1).Select(Expression).ToList();
        switch (name)
        {
            case "operator[]" when arguments.Count >= 2:
                return new MetacodeIndex(arguments[0], new[] { arguments[1] }) { Origin = Origin(node) };
            case "operator()" when arguments.Count >= 1:
                return new MetacodeInvocation(arguments[0], arguments.Skip(1).Select(a => new MetacodeArgument(null, a)).ToList())
                {
                    Origin = Origin(node),
                };
            case "operator-":
                return arguments.Count == 1
                    ? new MetacodeUnary(MetacodeUnaryOperator.Negate, arguments[0]) { Origin = Origin(node) }
                    : BinaryOf(MetacodeBinaryOperator.Subtract, arguments, node);
            case "operator+":
                return arguments.Count == 1
                    ? new MetacodeUnary(MetacodeUnaryOperator.Plus, arguments[0]) { Origin = Origin(node) }
                    : BinaryOf(MetacodeBinaryOperator.Add, arguments, node);
            case "operator*" when arguments.Count == 1:
                return arguments[0];
            case "operator==": return BinaryOf(MetacodeBinaryOperator.Equal, arguments, node);
            case "operator!=": return BinaryOf(MetacodeBinaryOperator.NotEqual, arguments, node);
            case "operator<": return BinaryOf(MetacodeBinaryOperator.LessThan, arguments, node);
            case "operator<=": return BinaryOf(MetacodeBinaryOperator.LessThanOrEqual, arguments, node);
            case "operator>": return BinaryOf(MetacodeBinaryOperator.GreaterThan, arguments, node);
            case "operator>=": return BinaryOf(MetacodeBinaryOperator.GreaterThanOrEqual, arguments, node);
            case "operator<<": return BinaryOf(MetacodeBinaryOperator.ShiftLeft, arguments, node);
            case "operator>>": return BinaryOf(MetacodeBinaryOperator.ShiftRight, arguments, node);
            case "operator[]=":
            default:
                return Opaque(node);
        }
    }

    private static MetacodeExpression BinaryOf(MetacodeBinaryOperator @operator, List<MetacodeExpression> arguments, JsonElement node) =>
        arguments.Count >= 2
            ? new MetacodeBinary(@operator, arguments[0], arguments[1]) { Origin = Origin(node) }
            : new MetacodeOpaqueExpression(MetacodeLanguage.Cpp, "operator", "not enough operands") { Origin = Origin(node) };

    private MetacodeExpression Binary(JsonElement node)
    {
        var children = Items(Field(node, "inner")).ToList();
        if (children.Count < 2)
            return Opaque(node);

        var left = Expression(children[0]);
        var right = Expression(children[1]);
        var opcode = Text(node, "opcode") ?? "?";

        if (Kind(node) == "CompoundAssignOperator" || opcode == "=")
        {
            var @operator = opcode switch
            {
                "=" => MetacodeAssignmentOperator.Assign,
                "+=" => MetacodeAssignmentOperator.Add,
                "-=" => MetacodeAssignmentOperator.Subtract,
                "*=" => MetacodeAssignmentOperator.Multiply,
                "/=" => MetacodeAssignmentOperator.Divide,
                "%=" => MetacodeAssignmentOperator.Modulo,
                "&=" => MetacodeAssignmentOperator.BitwiseAnd,
                "|=" => MetacodeAssignmentOperator.BitwiseOr,
                "^=" => MetacodeAssignmentOperator.BitwiseXor,
                "<<=" => MetacodeAssignmentOperator.ShiftLeft,
                ">>=" => MetacodeAssignmentOperator.ShiftRight,
                _ => MetacodeAssignmentOperator.Assign,
            };
            return new MetacodeAssignmentExpression(left, @operator, right) { Origin = Origin(node) };
        }

        var binary = opcode switch
        {
            "+" => MetacodeBinaryOperator.Add,
            "-" => MetacodeBinaryOperator.Subtract,
            "*" => MetacodeBinaryOperator.Multiply,
            "/" => MetacodeBinaryOperator.Divide,
            "%" => MetacodeBinaryOperator.Modulo,
            "==" => MetacodeBinaryOperator.Equal,
            "!=" => MetacodeBinaryOperator.NotEqual,
            "<" => MetacodeBinaryOperator.LessThan,
            "<=" => MetacodeBinaryOperator.LessThanOrEqual,
            ">" => MetacodeBinaryOperator.GreaterThan,
            ">=" => MetacodeBinaryOperator.GreaterThanOrEqual,
            "&&" => MetacodeBinaryOperator.LogicalAnd,
            "||" => MetacodeBinaryOperator.LogicalOr,
            "&" => MetacodeBinaryOperator.BitwiseAnd,
            "|" => MetacodeBinaryOperator.BitwiseOr,
            "^" => MetacodeBinaryOperator.BitwiseXor,
            "<<" => MetacodeBinaryOperator.ShiftLeft,
            ">>" => MetacodeBinaryOperator.ShiftRight,
            _ => (MetacodeBinaryOperator?)null,
        };

        return binary is null
            ? Opaque(node)
            : new MetacodeBinary(binary.Value, left, right) { Origin = Origin(node) };
    }

    private MetacodeExpression Unary(JsonElement node)
    {
        var inner = Items(Field(node, "inner")).FirstOrDefault();
        if (inner.ValueKind != JsonValueKind.Object)
            return Opaque(node);

        var operand = Expression(inner);
        var postfix = Bool(node, "isPostfix");
        var @operator = Text(node, "opcode") switch
        {
            "-" => MetacodeUnaryOperator.Negate,
            "+" => MetacodeUnaryOperator.Plus,
            "~" => MetacodeUnaryOperator.BitwiseNot,
            "!" => MetacodeUnaryOperator.Not,
            "++" => MetacodeUnaryOperator.Increment,
            "--" => MetacodeUnaryOperator.Decrement,
            _ => (MetacodeUnaryOperator?)null,
        };

        return @operator is null
            ? Opaque(node)
            : new MetacodeUnary(@operator.Value, operand, postfix) { Origin = Origin(node) };
    }

    private MetacodeExpression Opaque(JsonElement node)
    {
        var reason = "unmodelled expression: " + Kind(node);
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.UnsupportedExpression, MetacodeDiagnosticSeverity.Warning, reason, Origin(node)));
        return new MetacodeOpaqueExpression(MetacodeLanguage.Cpp, Kind(node), reason) { Origin = Origin(node) };
    }

    private MetacodeStatement OpaqueStatement(JsonElement node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.UnsupportedStatement, MetacodeDiagnosticSeverity.Warning, reason, Origin(node)));
        return new MetacodeOpaqueStatement(MetacodeLanguage.Cpp, Kind(node), reason) { Origin = Origin(node) };
    }

    private MetacodeOpaqueDeclaration OpaqueProperty(JsonElement node, string reason)
    {
        _diagnostics.Add(new MetacodeDiagnostic(MetacodeDiagnosticCodes.UnsupportedDeclaration, MetacodeDiagnosticSeverity.Warning, reason, Origin(node)));
        return new MetacodeOpaqueDeclaration(MetacodeLanguage.Cpp, Kind(node), reason) { Origin = Origin(node) };
    }

    private static MetacodeModifiers AttributeModifiers(JsonElement node) => Text(node, "storageClass") switch
    {
        "static" => MetacodeModifiers.Static,
        "extern" => MetacodeModifiers.Extern,
        _ => MetacodeModifiers.None,
    };

    private static MetacodeModifiers Modifiers(JsonElement node)
    {
        var modifiers = MetacodeModifiers.None;
        if (Bool(node, "storageClass", "static"))
            modifiers |= MetacodeModifiers.Static;
        if (Bool(node, "isVirtual") || Text(node, "virtual") == "virtual")
            modifiers |= MetacodeModifiers.Virtual;
        if (Text(node, "constexpr") == "constexpr")
            modifiers |= MetacodeModifiers.Const;

        return modifiers;
    }

    private static MetacodeModifiers Access(CppAccess access) => access switch
    {
        CppAccess.Public => MetacodeModifiers.Public,
        CppAccess.Protected => MetacodeModifiers.Protected,
        _ => MetacodeModifiers.Private,
    };

    private static MetacodeTypeRef TypeRef(JsonElement node) => TypeRefOf(Text(node, "type.qualType"));

    private static MetacodeTypeRef TypeRefOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return MetacodeTypeRef.Dynamic;

        var name = text.Trim();
        var isReference = name.EndsWith('&');
        if (isReference)
            name = name[..^1].Trim();
        var isPointer = name.EndsWith('*');
        if (isPointer)
            name = name[..^1].Trim();
        while (name.StartsWith("const ", StringComparison.Ordinal))
            name = name[6..].Trim();

        return new MetacodeTypeRef(name, IsReference: isReference, IsPointer: isPointer);
    }

    private static bool IsImplicit(JsonElement node) => Bool(node, "isImplicit");

    private static bool IsCompleteDefinition(JsonElement node) =>
        !node.TryGetProperty("completeDefinition", out _) || Bool(node, "completeDefinition");

    /// <summary>The part of <c>"int (int, int)"</c> before the parameter list.</summary>
    private static string? ReturnTypeText(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var paren = signature.IndexOf('(');
        return paren > 0 ? signature[..paren].Trim() : signature;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", string.Empty) + "\"";

    private static string? ReferencedName(JsonElement node) =>
        node.TryGetProperty("referencedDecl", out var referenced) && referenced.ValueKind == JsonValueKind.Object
            ? Text(referenced, "name")
            : null;

    private static string Kind(JsonElement node) => Text(node, "kind") ?? string.Empty;

    private static JsonElement? Field(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement? array) =>
        array is null || array.Value.ValueKind != JsonValueKind.Array
            ? Enumerable.Empty<JsonElement>()
            : array.Value.EnumerateArray();

    /// <summary>A nested property like <c>type.qualType</c>, or a scalar rendered as text.</summary>
    private static string? Text(JsonElement node, string path)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        var current = node;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? Scalar(JsonElement node, string name) => Text(node, name);

    private static bool Bool(JsonElement node, string path) => Text(node, path) == "true";

    private static bool Bool(JsonElement node, string path, string expected) => Text(node, path) == expected;

    private static MetacodeOrigin Origin(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return new MetacodeOrigin(MetacodeLanguage.Cpp);

        return new MetacodeOrigin(
            MetacodeLanguage.Cpp,
            null,
            Integer(node, "loc.line"),
            Integer(node, "loc.col"),
            Integer(node, "range.end.line"),
            Integer(node, "range.end.col"));
    }

    private static int Integer(JsonElement node, string path)
    {
        var text = Text(node, path);
        return int.TryParse(text, out var value) ? value : 0;
    }

    private MetacodeDiagnostic Diagnostic(string code, string message, JsonElement node) =>
        new(code, MetacodeDiagnosticSeverity.Warning, message, Origin(node));
}
