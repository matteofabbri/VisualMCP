namespace Snitch.Metacode.Passes;

/// <summary>
/// Rewrites a counted loop into the shape every language can produce: the initializer, then a
/// <c>while</c> whose body ends with the increment.
/// </summary>
/// <remarks>
/// A loop whose body contains <c>continue</c> is left alone: after the rewrite the increment would sit
/// after the <c>continue</c> and stop running, which changes the program. Nested loops are the same
/// story — a <c>continue</c> anywhere inside makes the pass conservative, so it may keep a
/// <c>for</c> that could in principle be rewritten.
/// </remarks>
public sealed class NormalizeControlFlowPass : MetacodeRewriter, IMetacodePass
{
    public static NormalizeControlFlowPass Instance { get; } = new();

    public string Name => "normalize-control-flow";

    public MetacodeDocument Apply(MetacodeDocument document) => Rewrite(document);

    protected override MetacodeStatement RewriteStatement(MetacodeStatement statement)
    {
        var rewritten = base.RewriteStatement(statement);
        if (rewritten is MetacodeFor loop && !ContainsContinue(loop.Body))
            return Desugar(loop);

        return rewritten;
    }

    private static MetacodeBlock Desugar(MetacodeFor loop)
    {
        var body = new List<MetacodeStatement>(loop.Body.Statements);
        if (loop.Increment is not null)
            body.Add(loop.Increment);

        var statements = new List<MetacodeStatement>(2);
        if (loop.Initializer is not null)
            statements.Add(loop.Initializer);

        statements.Add(new MetacodeWhile(
            loop.Condition ?? new MetacodeLiteral(MetacodeLiteralKind.Boolean, "true") { Origin = loop.Origin },
            new MetacodeBlock(body) { Origin = loop.Body.Origin }) { Origin = loop.Origin });

        return new MetacodeBlock(statements) { Origin = loop.Origin };
    }

    internal static bool ContainsContinue(MetacodeStatement statement) => statement switch
    {
        MetacodeContinue => true,
        MetacodeBlock block => block.Statements.Any(ContainsContinue),
        MetacodeIf branch => ContainsContinue(branch.Then) || (branch.Else is not null && ContainsContinue(branch.Else)),
        MetacodeWhile loop => ContainsContinue(loop.Body),
        MetacodeDoWhile loop => ContainsContinue(loop.Body),
        MetacodeForEach loop => ContainsContinue(loop.Body),
        MetacodeFor loop => ContainsContinue(loop.Body),
        MetacodeSwitch @switch => @switch.Cases.Any(c => ContainsContinue(c.Body)),
        MetacodeTry @try => ContainsContinue(@try.Body)
                             || @try.Catches.Any(c => ContainsContinue(c.Body))
                             || (@try.Finally is not null && ContainsContinue(@try.Finally))
                             || (@try.Else is not null && ContainsContinue(@try.Else)),
        MetacodeUsing @using => ContainsContinue(@using.Body),
        // A `lock` is a block like any other: a `continue` inside it still belongs to the loop around it.
        MetacodeLock @lock => ContainsContinue(@lock.Body),
        MetacodeLabel label => ContainsContinue(label.Statement),
        MetacodeLocalFunction local => local.Function.Body is not null && ContainsContinue(local.Function.Body),
        _ => false,
    };
}

/// <summary>Folds a block whose only statement is a block into its parent — the residue of other passes.</summary>
public sealed class FlattenSingleStatementBlocksPass : MetacodeRewriter, IMetacodePass
{
    public static FlattenSingleStatementBlocksPass Instance { get; } = new();

    public string Name => "flatten-blocks";

    public MetacodeDocument Apply(MetacodeDocument document) => Rewrite(document);

    protected override MetacodeBlock RewriteBlock(MetacodeBlock block)
    {
        var rewritten = base.RewriteBlock(block);
        while (rewritten.Statements.Count == 1 && rewritten.Statements[0] is MetacodeBlock inner)
            rewritten = inner;

        return rewritten;
    }
}
