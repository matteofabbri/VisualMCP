using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>The private method a block became, and the call that replaced the block.</summary>
internal sealed record ExtractedBlock(
    MethodDeclarationSyntax Helper,
    StatementSyntax Call,
    IReadOnlyList<(string Type, string Name)> Parameters,
    string ReturnType,
    bool IsAsync);

/// <summary>
/// "Move these statements into a private method" — the one implementation behind both the
/// <c>extract_method</c> refactoring and the complexity reducer's extraction move.
/// <para>
/// Only the shape that can be proven behaviour-preserving is accepted: a contiguous run that reads
/// (but never writes) the locals declared before it, contains no jump that would escape, and
/// optionally ends with a single <c>return expression;</c>. A block that awaits becomes
/// <c>async Task(…)</c>, which requires the host to be async already.
/// </para>
/// </summary>
internal static class BlockExtraction
{
    /// <summary>
    /// Checks the safety rules and builds the helper plus the call. Returns null and sets
    /// <paramref name="error"/> when the block cannot be moved without changing behaviour.
    /// </summary>
    public static ExtractedBlock? TryCreate(
        SyntaxNode root,
        SemanticModel model,
        MethodDeclarationSyntax host,
        TypeDeclarationSyntax owner,
        INamedTypeSymbol ownerSymbol,
        IReadOnlyList<StatementSyntax> selected,
        string methodName,
        CancellationToken cancellationToken,
        out string error)
    {
        error = string.Empty;
        if (selected.Count == 0)
        {
            error = "The selection is empty.";
            return null;
        }

        var blockStart = selected[0].SpanStart;
        var blockEnd = selected[^1].Span.End;

        // ── Safety rules ──────────────────────────────────────────────────────────────────
        // "Own code" only: an await or a yield inside a nested lambda/local function belongs to that
        // function, and moving the statement that declares it does not change its signature.
        var awaits = selected.Any(statement => OwnNodes(statement).Any(node => node is AwaitExpressionSyntax));

        foreach (var statement in selected)
        {
            if (OwnNodes(statement).Any(node => node is YieldStatementSyntax))
            {
                error = "The block contains yield; the iterator state machine would be split across two methods. Not supported.";
                return null;
            }

            if (OwnNodes(statement).Any(node => node is GotoStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax))
            {
                error = "The block contains a jump (break/continue/goto) that would escape the extracted method.";
                return null;
            }

            var returns = OwnNodes(statement).OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count == 0) continue;

            var isLast = ReferenceEquals(statement, selected[^1]);
            if (!isLast || statement is not ReturnStatementSyntax || returns[0].Expression is null)
            {
                error = "Only a single 'return expression;' as the last statement can be extracted.";
                return null;
            }
        }

        // An awaited block stays behaviour-identical only if the extracted method is async and its call
        // is awaited — which the enclosing method can only do if it is already async.
        if (awaits && !host.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            error = $"The block awaits, so the extracted method would be async and its call needs 'await'; " +
                    $"'{host.Identifier.ValueText}' is not async. Make it async first.";
            return null;
        }

        var trailingReturn = selected[^1] as ReturnStatementSyntax;
        var returnExpression = trailingReturn?.Expression;
        var returnType = returnExpression is null ? null : model.GetTypeInfo(returnExpression, cancellationToken).Type;

        if (returnExpression is not null && (returnType is null || returnType.TypeKind == TypeKind.Error))
        {
            error = "The returned expression has no resolvable type.";
            return null;
        }

        // ── Locals and parameters: reads become parameters, writes are refused ────────────
        var hostSymbol = model.GetDeclaredSymbol(host, cancellationToken);
        var parameters = new SortedDictionary<int, (string Name, string Type)>();
        var written = new List<string>();
        var internalLocals = new List<ILocalSymbol>();

        foreach (var node in selected.SelectMany(statement => statement.DescendantNodesAndSelf()))
        {
            if (node is not IdentifierNameSyntax identifier) continue;

            // A parameter of the host is just as out of scope in the new method as a local is.
            if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IParameterSymbol parameter)
            {
                if (hostSymbol is null || !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, hostSymbol))
                    continue;

                if (parameter.RefKind != RefKind.None || parameter.IsParams)
                {
                    error = $"The block reads the '{parameter.Name}' parameter, which is passed by ref/params and " +
                            "cannot be forwarded to a new method.";
                    return null;
                }

                parameters.TryAdd(parameter.Ordinal, (parameter.Name, parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                continue;
            }

            if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is not ILocalSymbol local) continue;

            var declarationSpan = local.DeclaringSyntaxReferences.FirstOrDefault()?.Span;
            var declaredInside = declarationSpan is { } span && span.Start >= blockStart && span.End <= blockEnd;

            if (declaredInside)
            {
                if (!internalLocals.Contains(local, SymbolEqualityComparer.Default))
                    internalLocals.Add(local);
                continue;
            }

            if (IsWritten(identifier))
            {
                written.Add(local.Name);
                continue;
            }

            var type = local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            parameters.TryAdd(declarationSpan?.Start ?? identifier.SpanStart, (local.Name, type));
        }

        if (written.Count > 0)
        {
            error = "The block writes locals declared before it, so their new values would be lost " +
                    $"({string.Join(", ", written.Distinct())}). Return the value instead, or select a narrower block.";
            return null;
        }

        foreach (var local in internalLocals)
        {
            var usedAfter = root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => identifier.SpanStart > blockEnd &&
                                     SymbolEqualityComparer.Default.Equals(
                                         model.GetSymbolInfo(identifier, cancellationToken).Symbol, local))
                .Any();

            if (usedAfter)
            {
                error = $"Local '{local.Name}' is declared in the block and used after it; the extraction would lose it.";
                return null;
            }
        }

        if (ownerSymbol.GetMembers(methodName).Length > 0)
        {
            error = $"'{ownerSymbol.ToDisplayString()}' already has a member named '{methodName}'.";
            return null;
        }

        // ── Build the extracted method and the call ───────────────────────────────────────
        var parameterNodes = parameters.Values
            .Select(parameter => Parameter(Identifier(parameter.Name)).WithType(ParseTypeName(parameter.Type)))
            .ToList();

        var modifiers = TokenList(Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space));
        if (host.Modifiers.Any(SyntaxKind.StaticKeyword))
            modifiers = modifiers.Add(Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(Space));
        if (awaits)
            modifiers = modifiers.Add(Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space));

        var valueTypeText = returnType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var returnTypeText = awaits
            ? (valueTypeText is null ? "Task" : $"Task<{valueTypeText}>")
            : valueTypeText ?? "void";

        var helper = MethodDeclaration(ParseTypeName(returnTypeText), Identifier(methodName))
            .WithModifiers(modifiers)
            .WithParameterList(ParameterList(SeparatedList(parameterNodes)))
            .WithBody(Block(selected.ToList()));

        if (host.TypeParameterList is not null)
            helper = helper.WithTypeParameterList(host.TypeParameterList);

        var indentation = IndentationOf(host);
        helper = NormalizeAt(helper, indentation);

        var arguments = string.Join(", ", parameters.Values.Select(parameter => parameter.Name));
        var awaited = awaits ? "await " : string.Empty;
        var call = ParseStatement(returnExpression is null
                ? $"{awaited}{methodName}({arguments});"
                : $"return {awaited}{methodName}({arguments});")
            .WithLeadingTrivia(selected[0].GetLeadingTrivia())
            .WithTrailingTrivia(selected[^1].GetTrailingTrivia());

        return new ExtractedBlock(
            helper,
            call,
            parameters.Values.Select(parameter => (parameter.Type, parameter.Name)).ToList(),
            returnTypeText,
            awaits);
    }

    /// <summary>
    /// Replaces statements <paramref name="firstIndex"/>..<paramref name="lastIndex"/> of
    /// <paramref name="targetBlock"/> with the call and inserts the helper right after
    /// <paramref name="host"/>.
    /// </summary>
    public static SyntaxNode BuildRoot(
        SyntaxNode root,
        TypeDeclarationSyntax owner,
        MethodDeclarationSyntax host,
        BlockSyntax targetBlock,
        int firstIndex,
        int lastIndex,
        ExtractedBlock extraction,
        string indentation)
    {
        var statements = targetBlock.Statements;

        var newStatements = statements
            .Take(firstIndex)
            .Concat(new[] { extraction.Call })
            .Concat(statements.Skip(lastIndex + 1))
            .ToList();

        var newHost = host.ReplaceNode(targetBlock, targetBlock.WithStatements(List(newStatements)));

        var index = owner.Members.IndexOf(host);
        var newOwner = owner.Members
            .Replace(host, newHost)
            .Insert(index + 1, extraction.Helper
                .WithLeadingTrivia(new SyntaxTriviaList(ElasticCarriageReturnLineFeed, Whitespace(indentation)))
                .WithTrailingTrivia(ElasticCarriageReturnLineFeed));

        return root.ReplaceNode(owner, owner.WithMembers(newOwner));
    }

    /// <summary>`NormalizeWhitespace` then re-indent to the type's own column and re-parse, so the moved body lines up with its siblings.</summary>
    public static MethodDeclarationSyntax NormalizeAt(MethodDeclarationSyntax helper, string indentation)
    {
        var normalized = helper.NormalizeWhitespace();
        var reindented = Reindent(normalized.ToFullString(), indentation);

        return ParseMemberDeclaration(reindented) as MethodDeclarationSyntax ?? normalized;
    }

    /// <summary>
    /// The whitespace that precedes this node on its own line. Read from the source line rather than
    /// from the leading trivia: the newline belongs to the previous token, so a member that follows
    /// another one carries indentation without a line break.
    /// </summary>
    public static string IndentationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var line = node.SyntaxTree.GetText().Lines[span.StartLinePosition.Line].ToString();
        return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    public static string Reindent(string text, string indentation)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }

    /// <summary>The node plus its descendants, without crossing into nested lambdas or local functions.</summary>
    public static IEnumerable<SyntaxNode> OwnNodes(SyntaxNode node) =>
        node.DescendantNodesAndSelf(child => child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

    private static bool IsWritten(IdentifierNameSyntax identifier) =>
        identifier.Parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Left == identifier => true,
            PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                                                  prefix.IsKind(SyntaxKind.PreDecrementExpression),
            PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                                                    postfix.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                                       argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword),
            _ => false,
        };
}
