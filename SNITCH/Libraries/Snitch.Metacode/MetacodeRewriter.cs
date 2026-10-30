namespace Snitch.Metacode;

/// <summary>
/// The base every pass and every future expression-level rewrite builds on. It rebuilds a document
/// bottom-up and only allocates where something changed, so a pass that touches one loop leaves the
/// rest of the tree reference-identical.
/// </summary>
/// <remarks>
/// A statement rewrite that returns a <see cref="MetacodeBlock"/> is <em>spliced</em> into its parent
/// block: that is how <c>for</c> becomes <c>declare; while</c> without a second tree shape.
/// Expressions are handed back unchanged today; a pass that wants to rewrite them overrides
/// <see cref="RewriteExpression"/>, and one that wants to see inside lambda bodies must do it there.
/// </remarks>
public class MetacodeRewriter
{
    public MetacodeDocument Rewrite(MetacodeDocument document)
    {
        var modules = RewriteModules(document.Modules);
        return ReferenceEquals(modules, document.Modules) ? document : document with { Modules = modules };
    }

    protected virtual IReadOnlyList<MetacodeModule> RewriteModules(IReadOnlyList<MetacodeModule> modules)
    {
        List<MetacodeModule>? replaced = null;
        for (var i = 0; i < modules.Count; i++)
        {
            var rewritten = RewriteModule(modules[i]);
            if (!ReferenceEquals(rewritten, modules[i]))
            {
                replaced ??= modules.ToList();
                replaced[i] = rewritten;
            }
        }

        return replaced ?? modules;
    }

    protected virtual MetacodeModule RewriteModule(MetacodeModule module)
    {
        var declarations = RewriteDeclarations(module.Declarations);
        return ReferenceEquals(declarations, module.Declarations) ? module : module with { Declarations = declarations };
    }

    protected virtual IReadOnlyList<MetacodeDeclaration> RewriteDeclarations(IReadOnlyList<MetacodeDeclaration> declarations)
    {
        List<MetacodeDeclaration>? replaced = null;
        for (var i = 0; i < declarations.Count; i++)
        {
            var rewritten = RewriteDeclaration(declarations[i]);
            if (ReferenceEquals(rewritten, declarations[i]))
                continue;

            replaced ??= declarations.ToList();
            replaced[i] = rewritten;
        }

        return replaced ?? declarations;
    }

    protected virtual MetacodeDeclaration RewriteDeclaration(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeTypeDeclaration type => RewriteType(type),
        MetacodeFunction function => RewriteFunction(function),
        MetacodeProperty property => RewriteProperty(property),
        _ => declaration,
    };

    protected virtual MetacodeDeclaration RewriteType(MetacodeTypeDeclaration type)
    {
        var members = RewriteDeclarations(type.Members);
        return ReferenceEquals(members, type.Members) ? type : type with { Members = members };
    }

    protected virtual MetacodeDeclaration RewriteFunction(MetacodeFunction function)
    {
        if (function.Body is null)
            return function;

        var body = RewriteBlock(function.Body);
        return ReferenceEquals(body, function.Body) ? function : function with { Body = body };
    }

    protected virtual MetacodeDeclaration RewriteProperty(MetacodeProperty property)
    {
        var getter = RewriteAccessor(property.Getter);
        var setter = RewriteAccessor(property.Setter);
        return ReferenceEquals(getter, property.Getter) && ReferenceEquals(setter, property.Setter)
            ? property
            : property with { Getter = getter, Setter = setter };
    }

    protected virtual MetacodeAccessor? RewriteAccessor(MetacodeAccessor? accessor)
    {
        if (accessor?.Body is null)
            return accessor;

        var body = RewriteBlock(accessor.Body);
        return ReferenceEquals(body, accessor.Body) ? accessor : accessor with { Body = body };
    }

    /// <summary>Rewrites a block and splices any statement that expanded into a block.</summary>
    protected virtual MetacodeBlock RewriteBlock(MetacodeBlock block)
    {
        List<MetacodeStatement>? statements = null;
        for (var i = 0; i < block.Statements.Count; i++)
        {
            var original = block.Statements[i];
            var rewritten = RewriteStatement(original);
            if (ReferenceEquals(rewritten, original) && statements is null)
                continue;

            statements ??= block.Statements.Take(i).ToList();
            if (rewritten is MetacodeBlock splice)
                statements.AddRange(splice.Statements);
            else
                statements.Add(rewritten);
        }

        return statements is null ? block : block with { Statements = statements };
    }

    protected virtual MetacodeStatement RewriteStatement(MetacodeStatement statement) => statement switch
    {
        MetacodeBlock block => RewriteBlock(block),
        MetacodeIf s => RewriteIf(s),
        MetacodeWhile s => s with { Body = RewriteBlock(s.Body) },
        MetacodeDoWhile s => s with { Body = RewriteBlock(s.Body) },
        MetacodeForEach s => s with { Body = RewriteBlock(s.Body) },
        MetacodeFor s => RewriteFor(s),
        MetacodeSwitch s => RewriteSwitch(s),
        MetacodeTry s => RewriteTry(s),
        MetacodeUsing s => RewriteUsing(s),
        MetacodeLock s => s with { Body = RewriteBlock(s.Body) },
        MetacodeLabel s => RewriteLabel(s),
        MetacodeLocalFunction s => RewriteLocalFunction(s),
        MetacodeReturn s => s with { Value = s.Value is null ? null : RewriteExpression(s.Value) },
        MetacodeThrow s => s with { Exception = s.Exception is null ? null : RewriteExpression(s.Exception) },
        MetacodeYield s => s with { Value = s.Value is null ? null : RewriteExpression(s.Value) },
        MetacodeExpressionStatement s => s with { Expression = RewriteExpression(s.Expression) },
        MetacodeAssignment s => RewriteAssignment(s),
        MetacodeLocalVariable s => s with { Initializer = s.Initializer is null ? null : RewriteExpression(s.Initializer) },
        _ => statement,
    };

    protected virtual MetacodeStatement RewriteIf(MetacodeIf statement)
    {
        var then = RewriteBlock(statement.Then);
        var @else = statement.Else is null ? null : RewriteBlock(statement.Else);
        var condition = RewriteExpression(statement.Condition);
        return ReferenceEquals(then, statement.Then) && ReferenceEquals(@else, statement.Else)
            ? statement
            : statement with { Condition = condition, Then = then, Else = @else };
    }

    protected virtual MetacodeStatement RewriteFor(MetacodeFor statement)
    {
        var body = RewriteBlock(statement.Body);
        return ReferenceEquals(body, statement.Body) ? statement : statement with { Body = body };
    }

    protected virtual MetacodeStatement RewriteSwitch(MetacodeSwitch statement)
    {
        var cases = new List<MetacodeSwitchCase>(statement.Cases.Count);
        var changed = false;
        foreach (var @case in statement.Cases)
        {
            var body = RewriteBlock(@case.Body);
            if (ReferenceEquals(body, @case.Body))
            {
                cases.Add(@case);
                continue;
            }

            changed = true;
            cases.Add(@case with { Body = body });
        }

        return changed ? statement with { Cases = cases } : statement;
    }

    protected virtual MetacodeStatement RewriteTry(MetacodeTry statement)
    {
        var body = RewriteBlock(statement.Body);
        var @finally = statement.Finally is null ? null : RewriteBlock(statement.Finally);
        var @else = statement.Else is null ? null : RewriteBlock(statement.Else);
        var changed = !ReferenceEquals(body, statement.Body)
                      || !ReferenceEquals(@finally, statement.Finally)
                      || !ReferenceEquals(@else, statement.Else);

        var catches = new List<MetacodeCatch>(statement.Catches.Count);
        foreach (var @catch in statement.Catches)
        {
            var catchBody = RewriteBlock(@catch.Body);
            if (ReferenceEquals(catchBody, @catch.Body))
            {
                catches.Add(@catch);
                continue;
            }

            changed = true;
            catches.Add(@catch with { Body = catchBody });
        }

        return changed ? statement with { Body = body, Catches = catches, Finally = @finally, Else = @else } : statement;
    }

    protected virtual MetacodeStatement RewriteUsing(MetacodeUsing statement)
    {
        var body = RewriteBlock(statement.Body);
        return ReferenceEquals(body, statement.Body) ? statement : statement with { Body = body };
    }

    protected virtual MetacodeStatement RewriteLabel(MetacodeLabel statement)
    {
        var inner = RewriteStatement(statement.Statement);
        return ReferenceEquals(inner, statement.Statement) ? statement : statement with { Statement = inner };
    }

    protected virtual MetacodeStatement RewriteLocalFunction(MetacodeLocalFunction statement)
    {
        var function = RewriteFunction(statement.Function);
        return ReferenceEquals(function, statement.Function) ? statement : statement with { Function = (MetacodeFunction)function };
    }

    protected virtual MetacodeStatement RewriteAssignment(MetacodeAssignment statement)
    {
        var target = RewriteExpression(statement.Target);
        var value = RewriteExpression(statement.Value);
        return ReferenceEquals(target, statement.Target) && ReferenceEquals(value, statement.Value)
            ? statement
            : statement with { Target = target, Value = value };
    }

    /// <summary>
    /// Identity by default. Override to rewrite expressions; remember that a lambda body is a block
    /// reachable only from here, never from <see cref="RewriteBlock"/>.
    /// </summary>
    protected virtual MetacodeExpression RewriteExpression(MetacodeExpression expression) => expression;
}
