using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.VB.Style;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The refactorings that are a declaration rather than a C# idiom, written for Visual Basic: replace a
/// declaration's type, take one of its base types, mark every declaration of a type <c>Partial</c>, and
/// delete a symbol nothing refers to. The shape is the one the C# services use — resolve, project the
/// edit, compile the projection, write only when it does not add an error.
/// </summary>
internal static class VbRefactoringSupport
{
    /// <summary>Rider's "Change type" for a .vb document.</summary>
    public static async Task<object?> ChangeTypeAsync(
        IRoslynWorkspace workspace, Document document, int line, string newType, bool dryRun, CancellationToken cancellationToken)
    {
        var parsed = ParseTypeName(newType);
        if (parsed.ContainsDiagnostics)
            return new { error = $"'{newType}' is not a valid Visual Basic type name." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return null;

        var target = TypeOnLine(root, line);
        if (target is null)
            return new { error = $"No field, property, local, parameter or return type starts on line {line}." };

        var current = target.ToString();
        if (string.Equals(current, newType, StringComparison.Ordinal))
            return new { error = $"The declaration on line {line} already has type '{current}'." };

        var newRoot = root.ReplaceNode(target, parsed.WithTriviaFrom(target));
        var (outcome, error) = await TryApplyAsync(workspace, document, newRoot, dryRun, cancellationToken,
            gate => $"Refused: '{current}' → '{newType}' would add {gate.Added} compiler error(s) " +
                    $"({gate.Before} -> {gate.After}). Fix the usages first, or change the type text directly.");

        if (error is not null) return error;

        return new
        {
            @refactoring = "change_type",
            from = current,
            to = newType,
            file = document.FilePath,
            line,
            compilerErrors = new { before = outcome!.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            preview = outcome.Preview,
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>Rider's "Use base type" for a .vb document: the base type is derived, not typed.</summary>
    public static async Task<object?> UseBaseTypeAsync(
        IRoslynWorkspace workspace, Document document, int line, string? baseType, bool dryRun, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return null;

        var target = TypeOnLine(root, line);
        if (target is null)
            return new { error = $"No field, property, local, parameter or return type starts on line {line}." };

        if (model.GetTypeInfo(target, cancellationToken).Type is not { } current)
            return new { error = "The declaration's type did not resolve." };

        var candidates = new List<INamedTypeSymbol>();
        if (current.BaseType is { SpecialType: not SpecialType.System_Object } baseClass) candidates.Add(baseClass);
        candidates.AddRange(current.AllInterfaces.OrderBy(candidate => candidate.Name, StringComparer.Ordinal));

        if (candidates.Count == 0)
            return new { error = $"'{current.ToDisplayString()}' has no base type in source." };

        INamedTypeSymbol? chosen;
        if (baseType is null)
        {
            if (candidates.Count > 1)
                return new
                {
                    error = "Several base types are available: pass baseType.",
                    candidates = candidates.Select(candidate => candidate.ToDisplayString()).ToList(),
                };

            chosen = candidates[0];
        }
        else
        {
            chosen = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, baseType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToDisplayString(), baseType, StringComparison.OrdinalIgnoreCase));

            if (chosen is null)
                return new
                {
                    error = $"'{baseType}' is not a base type of '{current.ToDisplayString()}'.",
                    candidates = candidates.Select(candidate => candidate.ToDisplayString()).ToList(),
                };
        }

        var replacement = ParseTypeName(chosen.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .WithTriviaFrom(target);
        var newRoot = root.ReplaceNode(target, replacement);

        var (outcome, error) = await TryApplyAsync(workspace, document, newRoot, dryRun, cancellationToken,
            gate => $"Refused: '{current.ToDisplayString()}' → '{chosen.ToDisplayString()}' would add " +
                    $"{gate.Added} compiler error(s) ({gate.Before} -> {gate.After}): the usages need the derived members.");

        if (error is not null) return error;

        return new
        {
            @refactoring = "use_base_type",
            from = current.ToDisplayString(),
            to = chosen.ToDisplayString(),
            file = document.FilePath,
            line,
            compilerErrors = new { before = outcome!.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            preview = outcome.Preview,
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>
    /// Rider's "Make partial" for a .vb document. Every declaration of the type has to change together —
    /// VB rejects a mix — so each file is projected once and written once.
    /// </summary>
    public static async Task<object> TypeToPartialAsync(
        IRoslynWorkspace workspace, Document document, string typeName, string? containingType, bool dryRun,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        var statement = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .Select(block => block.BlockStatement)
            .FirstOrDefault(candidate => IdentifierOf(candidate) is { } name &&
                                         string.Equals(name.ValueText, typeName, StringComparison.Ordinal) &&
                                         (containingType is null ||
                                          string.Equals(model.GetDeclaredSymbol(candidate, cancellationToken)?.ContainingType?.Name,
                                              containingType, StringComparison.OrdinalIgnoreCase)));

        if (statement is null)
            return new { error = $"No class, structure, interface or module named '{typeName}' in {Path.GetFileName(document.FilePath)}." };

        if (model.GetDeclaredSymbol(statement, cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type symbol." };

        var documents = document.Project.Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(held => held.FilePath is not null)
            .ToList();

        var pending = new List<(Document Document, string Text)>();
        var alreadyPartial = 0;
        var notInSolution = new List<string>();

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var path = reference.SyntaxTree.FilePath;
            if (path is null)
            {
                notInSolution.Add("(in memory)");
                continue;
            }

            var owner = documents.FirstOrDefault(held =>
                string.Equals(Path.GetFullPath(held.FilePath!), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (owner is null)
            {
                notInSolution.Add(path);
                continue;
            }

            var ownerRoot = await owner.GetSyntaxRootAsync(cancellationToken);
            if (ownerRoot?.FindNode(reference.Span) is not TypeBlockSyntax block)
            {
                notInSolution.Add(path);
                continue;
            }

            var opening = block.BlockStatement;
            if (opening.Modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword)))
            {
                alreadyPartial++;
                continue;
            }

            // The modifier goes with the others: after the last one ("Public Partial Class"), or in
            // front of the keyword when the declaration has none.
            var insertAt = opening.Modifiers.Count > 0
                ? opening.Modifiers[^1].Span.End
                : opening.AttributeLists.Count > 0
                    ? opening.AttributeLists[^1].Span.End
                    : opening.SpanStart;

            var text = (await owner.GetTextAsync(cancellationToken)).ToString();
            pending.Add((owner, text.Insert(insertAt, " Partial")));
        }

        if (pending.Count == 0)
            return new
            {
                @refactoring = "type_to_partial",
                type = symbol.ToDisplayString(),
                dryRun,
                applied = false,
                alreadyPartial,
                note = alreadyPartial > 0 ? "Every declaration is already partial; nothing to do." : "No declaration could be updated.",
                notInSolution,
            };

        var results = new List<object>();
        foreach (var (owner, text) in pending)
        {
            var oldText = (await owner.GetTextAsync(cancellationToken)).ToString();
            if (!dryRun)
                await RefactoringFileWriter.WriteAndUpdateAsync(workspace, owner, text, cancellationToken);

            results.Add(new
            {
                file = owner.FilePath,
                declarations = 1,
                preview = RefactoringFileWriter.Diff(oldText, text),
            });
        }

        return new
        {
            @refactoring = "type_to_partial",
            type = symbol.ToDisplayString(),
            dryRun,
            applied = !dryRun,
            declarationsUpdated = pending.Count,
            filesChanged = results.Count,
            alreadyPartial,
            notInSolution,
            files = results,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>
    /// Rider's "Safe delete" for a .vb document: the caller has already proved the symbol has no
    /// references, so this removes the declaration it names.
    /// </summary>
    public static async Task<object> DeleteAsync(
        IRoslynWorkspace workspace, Solution solution, ISymbol symbol, Location location, bool dryRun,
        CancellationToken cancellationToken)
    {
        var tree = location.SourceTree;
        if (tree?.FilePath is null) return new { error = "Could not locate the declaration's file." };

        var path = Path.GetFullPath(tree.FilePath);
        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));

        if (document is null) return new { error = "Could not locate the document for this symbol." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return new { error = "Could not read the syntax tree." };

        var found = root.FindNode(location.SourceSpan);

        // The declaration is the statement the type or namespace block owns.
        var member = found.AncestorsAndSelf().OfType<StatementSyntax>()
            .FirstOrDefault(candidate => candidate.Parent is TypeBlockSyntax or NamespaceBlockSyntax or CompilationUnitSyntax);

        if (member is null)
            return new { error = $"Unsupported declaration context: {found.Parent?.GetType().Name}." };

        if (dryRun)
            return new
            {
                safe = true,
                dryRun = true,
                symbol = symbol.ToDisplayString(),
                kind = symbol.Kind.ToString(),
                declarationFile = document.FilePath,
                message = "Symbol has zero references and can be safely deleted.",
            };

        var parent = member.Parent!;
        var remaining = VbBlocks.StatementsOf(parent).Where(statement => statement != member).ToList();
        var newRoot = root.ReplaceNode(parent, VbBlocks.WithStatements(parent, remaining));

        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newRoot.ToFullString(), cancellationToken);

        return new
        {
            safe = true,
            dryRun = false,
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            declarationFile = document.FilePath,
            message = "Symbol deleted successfully.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Whether a symbol is declared in a Visual Basic document.</summary>
    public static bool IsVisualBasic(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.Options.Language == LanguageNames.VisualBasic;

    /// <summary>The document of <paramref name="solution"/> that holds <paramref name="path"/>, or null.</summary>
    public static Document? DocumentAt(Solution solution, string? path)
    {
        if (path is null) return null;

        var full = Path.GetFullPath(path);
        return solution.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(document => document.FilePath is not null &&
                                        string.Equals(Path.GetFullPath(document.FilePath), full,
                                            Snitch.Platform.HostSystem.PathComparison));
    }

    /// <summary>The type of the first declaration that starts on the line: an As clause of any kind.</summary>
    private static TypeSyntax? TypeOnLine(SyntaxNode root, int line)
    {
        foreach (var node in root.DescendantNodes()
                     .Where(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
                     .OrderBy(node => node.SpanStart))
        {
            TypeSyntax? type = node switch
            {
                // "As New C(...)" is a creation, not a type reference: the type lives inside the New.
                SimpleAsClauseSyntax clause => clause.Type,
                _ => null,
            };

            if (type is not null) return type;
        }

        return null;
    }

    private static SyntaxToken? IdentifierOf(StatementSyntax statement) => statement switch
    {
        ClassStatementSyntax type => type.Identifier,
        StructureStatementSyntax type => type.Identifier,
        InterfaceStatementSyntax type => type.Identifier,
        ModuleStatementSyntax type => type.Identifier,
        _ => null,
    };

    private sealed record ApplyOutcome(CompilerGate Gate, string Preview);

    /// <summary>Gates a projected edit and writes it when the project does not gain an error.</summary>
    private static async Task<(ApplyOutcome? Outcome, object? Error)> TryApplyAsync(
        IRoslynWorkspace workspace,
        Document document,
        SyntaxNode newRoot,
        bool dryRun,
        CancellationToken cancellationToken,
        Func<CompilerGate, string> refused)
    {
        var current = await document.GetTextAsync(cancellationToken);
        var original = current.ToString();
        var newText = newRoot.ToFullString();

        var before = document.Project.Solution;
        var after = before.WithDocumentText(document.Id, SourceText.From(newText, current.Encoding));
        var gate = await RefactoringCompilerGate.CompareAsync(before, after, [document.Project.Id], cancellationToken);

        if (!gate.Passed)
            return (null, new { error = refused(gate), errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors });

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);

        return (new ApplyOutcome(gate, RefactoringFileWriter.Diff(original, newText)), null);
    }
}
