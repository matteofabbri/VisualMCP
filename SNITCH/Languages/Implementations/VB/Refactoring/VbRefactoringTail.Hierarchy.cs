using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Pull members up" and "Push members down" on a Visual Basic hierarchy. A pull into an
/// interface writes the VB signature and the <c>Implements I.Member</c> clause VB requires on every
/// member; a pull into a base class moves the declaration; a push moves one member into the single
/// derived type that is its only user. Every move is compiler-gated.
/// </summary>
internal static partial class VbRefactoringTail
{
    // ── pull_members_up ──────────────────────────────────────────────────────
    public static async Task<object> PullMembersUpAsync(IRoslynWorkspace workspace, string className,
        string[] memberNames, string targetName, bool dryRun, CancellationToken cancellationToken)
    {
        if (memberNames.Length == 0) return new { error = "At least one member name is required." };

        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No solution could be auto-located from the working directory." };

        var classSymbol = (await SymbolFinder.FindSourceDeclarationsAsync(solution,
                name => name.Equals(className, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type))
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(candidate => candidate.TypeKind == TypeKind.Class);
        if (classSymbol is null) return new { error = $"Class '{className}' not found." };

        var targets = classSymbol.AllInterfaces.Cast<INamedTypeSymbol>()
            .Concat(EnumerateBaseClasses(classSymbol))
            .ToList();
        var target = targets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return new
            {
                error = $"'{targetName}' is not in the base list of '{className}'. Available targets: " +
                        string.Join(", ", targets.Select(candidate => candidate.Name)),
            };

        var members = new List<(ISymbol Symbol, StatementSyntax Signature, StatementSyntax Block)>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var candidate = classSymbol.GetMembers(name)
                .FirstOrDefault(symbol => symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol);
            if (candidate is null) return new { error = $"Member '{name}' not found in '{className}'." };
            if (candidate.IsImplicitlyDeclared) return new { error = $"'{name}' is compiler-generated." };

            var syntax = candidate.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            if (syntax is null) return new { error = $"'{name}' has no source declaration." };
            var signature = MemberStatementOf(syntax);
            var block = MemberBlockOf(syntax);
            if (signature is null || block is null)
                return new { error = $"'{name}' is not a method, property, event or field." };
            members.Add((candidate, signature, block));
        }

        var classReference = classSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (classReference?.SyntaxTree.FilePath is null)
            return new { error = $"'{className}' has no source declaration." };
        var classDocument = VbRefactoringSupport.DocumentAt(solution, classReference.SyntaxTree.FilePath);
        if (classDocument is null) return new { error = $"Could not locate the document of '{className}'." };
        var classRoot = await classDocument.GetSyntaxRootAsync(cancellationToken);
        if (classRoot?.FindNode(classReference.Span) is not TypeBlockSyntax classBlock)
            return new { error = $"Could not locate the class declaration of '{className}'." };
        var classText = await classDocument.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(classText);
        var classIndent = IndentationOf(classText, classBlock.SpanStart) + "    ";

        var targetReference = target.DeclaringSyntaxReferences.FirstOrDefault();
        if (targetReference?.SyntaxTree.FilePath is null)
            return new { error = $"'{targetName}' has no source declaration." };
        var targetDocument = VbRefactoringSupport.DocumentAt(solution, targetReference.SyntaxTree.FilePath);
        if (targetDocument is null) return new { error = $"Could not locate the document of '{targetName}'." };
        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot?.FindNode(targetReference.Span) is not TypeBlockSyntax targetBlock)
            return new { error = $"Could not locate the declaration of '{targetName}'." };
        var targetText = await targetDocument.GetTextAsync(cancellationToken);
        var targetIndent = IndentationOf(targetText, targetBlock.SpanStart) + (targetBlock is InterfaceBlockSyntax ? "    " : "    ");

        var edits = new List<TextEdit>();
        var pulled = new List<string>();

        if (targetBlock is InterfaceBlockSyntax)
        {
            if (classSymbol.BaseType is { } baseType &&
                baseType.AllInterfaces.Contains(target, SymbolEqualityComparer.Default))
                return new
                {
                    error = $"'{targetName}' is implemented by '{baseType.Name}': VB refuses a second " +
                            "Implements clause in the derived class, so the member cannot implement it here.",
                };

            foreach (var (symbol, signatureStatement, _) in members)
            {
                if (symbol.IsStatic)
                    return new { error = $"'{symbol.Name}' is Shared: an interface cannot declare a static member here." };

                var signature = InterfaceSignature(symbol, signatureStatement);
                if (signature is null)
                    return new { error = $"'{symbol.Name}' is a field: an interface can hold methods, properties and events only." };

                edits.Add(new TextEdit(targetDocument,
                    new TextSpan(BeforeEndOfType(targetText, targetBlock), 0),
                    $"{targetIndent}{signature}{newLine}"));

                var owner = DeclarationStatementOf(signatureStatement);
                if (owner is null) return new { error = $"Could not locate the declaration of '{symbol.Name}'." };
                var clause = owner.ToString().Contains(" Implements ", StringComparison.OrdinalIgnoreCase)
                    ? $", {target.Name}.{symbol.Name}"
                    : $" Implements {target.Name}.{symbol.Name}";
                edits.Add(new TextEdit(classDocument, new TextSpan(owner.Span.End, 0), clause));
                pulled.Add(symbol.Name);
            }
        }
        else
        {
            foreach (var (symbol, _, declaration) in members)
            {
                if (symbol is IFieldSymbol { IsReadOnly: false })
                    return new { error = $"'{symbol.Name}' is a mutable field: moving it to the base changes what every instance shares." };
                if (HasModifier(ModifiersOf(declaration), SyntaxKind.OverridesKeyword))
                    return new { error = $"'{symbol.Name}' overrides a grandparent member; pull that declaration up instead." };
                if (HasModifier(ModifiersOf(declaration), SyntaxKind.PrivateKeyword))
                    return new { error = $"'{symbol.Name}' is Private: moving it to the base would change its accessibility." };

                var memberText = classText.ToString(declaration.Span);
                memberText = classIndent + memberText;
                var moved = Reindent(memberText, classIndent, targetIndent, newLine);
                edits.Add(new TextEdit(classDocument, WholeLines(classText, declaration.Span), string.Empty));
                edits.Add(new TextEdit(targetDocument,
                    new TextSpan(BeforeEndOfType(targetText, targetBlock), 0),
                    $"{moved}{newLine}"));
                pulled.Add(symbol.Name);
            }
        }

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the pull");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "pull_members_up",
            type = classSymbol.ToDisplayString(),
            target = target.ToDisplayString(),
            into = targetBlock is InterfaceBlockSyntax ? "interface" : "base class",
            members = pulled,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── push_members_down ────────────────────────────────────────────────────
    public static async Task<object> PushMembersDownAsync(IRoslynWorkspace workspace, string filePath,
        string memberName, string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .Where(block => containingType is null ||
                            string.Equals(block.BlockStatement.Identifier.ValueText, containingType,
                                StringComparison.OrdinalIgnoreCase))
            .SelectMany(block => block.Members
                .Where(member => string.Equals(MemberNameOf(member), memberName, StringComparison.Ordinal))
                .Select(member => (Block: block, Member: member)))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No method, property, event or field '{memberName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several members named '{memberName}'; address one declaration at a time." };

        var (baseBlock, member) = candidates[0];
        if (model.GetDeclaredSymbol(baseBlock.BlockStatement, cancellationToken) is not INamedTypeSymbol baseSymbol)
            return new { error = $"'{baseBlock.BlockStatement.Identifier.ValueText}' did not resolve." };

        if (HasModifier(ModifiersOf(member), SyntaxKind.MustOverrideKeyword))
            return new { error = $"'{memberName}' is MustOverride: it has no body to push down." };
        if (HasModifier(ModifiersOf(member), SyntaxKind.OverridesKeyword))
            return new { error = $"'{memberName}' overrides a grandparent member; push that declaration instead." };
        if (symbolOf(model, member, cancellationToken) is not { } symbol)
            return new { error = $"'{memberName}' did not resolve to a symbol." };
        var derived = (await SymbolFinder.FindDerivedClassesAsync(baseSymbol, solution,
                cancellationToken: cancellationToken))
            .Where(candidate => candidate.Locations.Any(location => location.IsInSource))
            .ToList();
        if (derived.Count != 1)
            return new
            {
                error = derived.Count == 0
                    ? $"'{baseSymbol.Name}' has no derived class in source."
                    : $"'{baseSymbol.Name}' has {derived.Count} derived classes: a pure push needs exactly one.",
                derived = derived.Select(candidate => candidate.Name).ToList(),
            };

        var derivedSymbol = derived[0];
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (member.Span.Contains(location.Location.SourceSpan)) continue;

                var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;
                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var owner = node.AncestorsAndSelf().OfType<TypeBlockSyntax>().FirstOrDefault();
                if (owner is null)
                    return new
                    {
                        error = $"'{memberName}' is used at {Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} outside a type declaration.",
                    };

                var targetModel = await location.Document.GetSemanticModelAsync(cancellationToken);
                if (targetModel is null) continue;
                var ownerSymbol = targetModel.GetDeclaredSymbol(owner.BlockStatement, cancellationToken);
                if (!SymbolEqualityComparer.Default.Equals(ownerSymbol, derivedSymbol))
                    return new
                    {
                        error = $"'{memberName}' is used from '{ownerSymbol?.Name}' at " +
                                $"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1}; a pure push is refused when " +
                                "anything outside the single derived type uses the member.",
                    };
            }

        var derivedReference = derivedSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (derivedReference?.SyntaxTree.FilePath is null)
            return new { error = $"'{derivedSymbol.Name}' has no source declaration." };
        var derivedDocument = VbRefactoringSupport.DocumentAt(solution, derivedReference.SyntaxTree.FilePath);
        if (derivedDocument is null) return new { error = $"Could not locate the document of '{derivedSymbol.Name}'." };
        var derivedRoot = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
        var derivedModel = await derivedDocument.GetSemanticModelAsync(cancellationToken);
        if (derivedRoot is null || derivedModel is null)
            return new { error = $"Could not read {Path.GetFileName(derivedDocument.FilePath)}." };

        var derivedBlock = derivedRoot.DescendantNodes().OfType<TypeBlockSyntax>()
            .FirstOrDefault(block => block.Span.Contains(derivedReference.Span));
        if (derivedBlock is null) return new { error = $"Could not locate the declaration of '{derivedSymbol.Name}'." };

        var baseText = await document.GetTextAsync(cancellationToken);
        var derivedText = await derivedDocument.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(baseText);
        var baseIndent = IndentationOf(baseText, member.SpanStart);
        var derivedIndent = IndentationOf(derivedText, derivedBlock.SpanStart) + "    ";

        var memberText = baseIndent + baseText.ToString(member.Span);
        var moved = Reindent(memberText, baseIndent, derivedIndent, newLine);

        var edits = new List<TextEdit>
        {
            new(document, WholeLines(baseText, member.Span), string.Empty),
            new(derivedDocument, new TextSpan(BeforeEndOfType(derivedText, derivedBlock), 0), $"{moved}{newLine}"),
        };

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the push");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "push_members_down",
            type = baseSymbol.ToDisplayString(),
            derived = derivedSymbol.ToDisplayString(),
            member = memberName,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<INamedTypeSymbol> EnumerateBaseClasses(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.SpecialType != SpecialType.System_Object)
                yield return current;
    }

    private static string? MemberNameOf(StatementSyntax member) => member switch
    {
        EventBlockSyntax @event => @event.EventStatement.Identifier.ValueText,
        EventStatementSyntax @event => @event.Identifier.ValueText,
        _ => MemberName(member),
    };

    private static StatementSyntax? MemberStatementOf(SyntaxNode syntax) => syntax switch
    {
        MethodBlockSyntax method => method.SubOrFunctionStatement,
        MethodStatementSyntax statement => statement,
        PropertyBlockSyntax property => property.PropertyStatement,
        PropertyStatementSyntax property => property,
        EventBlockSyntax @event => @event.EventStatement,
        EventStatementSyntax @event => @event,
        FieldDeclarationSyntax field => field,
        ModifiedIdentifierSyntax identifier =>
            identifier.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault(),
        _ => null,
    };

    /// <summary>The whole member — the block with its body — for a move, not just its opening statement.</summary>
    private static StatementSyntax? MemberBlockOf(SyntaxNode syntax)
    {
        StatementSyntax? statement = syntax switch
        {
            StatementSyntax node => node,
            ModifiedIdentifierSyntax identifier =>
                identifier.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault(),
            _ => null,
        };

        if (statement is null) return null;
        if (statement is MethodBlockSyntax or PropertyBlockSyntax or EventBlockSyntax or FieldDeclarationSyntax)
            return statement;

        // A VB symbol's declaring reference is the opening statement, not the block that owns it.
        for (var current = statement.Parent; current is not null; current = current.Parent)
            if (current is MethodBlockSyntax or PropertyBlockSyntax or EventBlockSyntax)
                return (StatementSyntax)current;

        return statement;
    }

    /// <summary>The statement that carries a member's modifiers and identifier, for an Implements clause.</summary>
    private static StatementSyntax? DeclarationStatementOf(StatementSyntax member) => member switch
    {
        MethodBlockSyntax method => method.SubOrFunctionStatement,
        PropertyBlockSyntax property => property.PropertyStatement,
        EventBlockSyntax @event => @event.EventStatement,
        _ => member,
    };

    private static string? InterfaceSignature(ISymbol symbol, StatementSyntax declaration) => symbol switch
    {
        IMethodSymbol method when declaration is MethodStatementSyntax statement =>
            MethodSignature(method, statement),
        IMethodSymbol method when declaration is MethodBlockSyntax block =>
            MethodSignature(method, block.SubOrFunctionStatement),
        IPropertySymbol property when declaration is PropertyBlockSyntax block =>
            $"{PropertyKeyword(block.PropertyStatement)} {property.Name} As " +
            $"{TypeTextOf(block.PropertyStatement.AsClause)}",
        IPropertySymbol property when declaration is PropertyStatementSyntax statement =>
            $"{PropertyKeyword(statement)} {property.Name} As {TypeTextOf(statement.AsClause)}",
        IEventSymbol @event when declaration is EventBlockSyntax block =>
            $"Event {@event.Name} As {TypeTextOf(block.EventStatement.AsClause)}",
        IEventSymbol @event when declaration is EventStatementSyntax statement =>
            $"Event {@event.Name} As {TypeTextOf(statement.AsClause)}",
        _ => null,
    };

    private static string MethodSignature(IMethodSymbol method, MethodStatementSyntax statement) =>
        $"{(method.ReturnsVoid ? "Sub" : "Function")} {statement.Identifier.ValueText}" +
        $"{statement.ParameterList?.ToString() ?? "()"}" +
        (statement.AsClause is { } asClause ? $" {asClause}" : string.Empty);

    private static string PropertyKeyword(PropertyStatementSyntax statement)
    {
        if (statement.Modifiers.Any(token => token.IsKind(SyntaxKind.ReadOnlyKeyword))) return "ReadOnly Property";
        if (statement.Modifiers.Any(token => token.IsKind(SyntaxKind.WriteOnlyKeyword))) return "WriteOnly Property";
        return "Property";
    }

    private static ISymbol? symbolOf(SemanticModel model, StatementSyntax member, CancellationToken cancellationToken) =>
        member switch
        {
            MethodBlockSyntax method => model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken),
            PropertyBlockSyntax property => model.GetDeclaredSymbol(property.PropertyStatement, cancellationToken),
            PropertyStatementSyntax property => model.GetDeclaredSymbol(property, cancellationToken),
            EventBlockSyntax @event => model.GetDeclaredSymbol(@event.EventStatement, cancellationToken),
            EventStatementSyntax @event => model.GetDeclaredSymbol(@event, cancellationToken),
            FieldDeclarationSyntax field when field.Declarators.Count == 1 && field.Declarators[0].Names.Count == 1
                => model.GetDeclaredSymbol(field.Declarators[0].Names[0], cancellationToken),
            _ => null,
        };
}
