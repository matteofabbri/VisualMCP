using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Introduce field": hoist an expression that appears in a member into a field initialised
/// with it. Only context-free expressions qualify, because a field initializer cannot read locals,
/// parameters or instance state.
/// </summary>
public interface IIntroduceFieldService
{
    Task<object> IntroduceField(
        string filePath,
        int line,
        string expressionText,
        string fieldName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class IntroduceFieldService : IIntroduceFieldService
{
    private readonly IRoslynWorkspace _workspace;

    public IntroduceFieldService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> IntroduceField(
        string filePath,
        int line,
        string expressionText,
        string fieldName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };
        if (string.IsNullOrWhiteSpace(expressionText)) return new { error = "The exact expression text is required." };
        if (string.IsNullOrWhiteSpace(fieldName) || !SyntaxFacts.IsValidIdentifier(fieldName))
            return new { error = $"'{fieldName}' is not a valid field name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.IntroduceFieldAsync(
                _workspace, filePath, line, expressionText, fieldName, dryRun, cancellationToken);

        var sourceText = await document.GetTextAsync(cancellationToken);
        if (line > sourceText.Lines.Count) return new { error = $"Line {line} is out of range." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var textLine = sourceText.Lines[line - 1];
        var column = textLine.ToString().IndexOf(expressionText, StringComparison.Ordinal);
        if (column < 0) return new { error = $"Expression '{expressionText}' not found on line {line}." };

        var position = textLine.Start + column;
        var expression = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(candidate => candidate.ToString() == expressionText &&
                                candidate.SpanStart >= textLine.Start &&
                                candidate.Span.End <= textLine.End)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (expression is null)
            return new { error = $"Could not locate '{expressionText}' as a single expression on line {line}." };

        var type = model.GetTypeInfo(expression, cancellationToken).Type;
        if (type is null || type.TypeKind is TypeKind.Error)
            return new { error = "The expression has no resolvable type; nothing to declare." };
        if (type.SpecialType == SpecialType.System_Void)
            return new { error = "A void expression cannot initialise a field." };

        var owner = expression.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (owner is null) return new { error = "The expression is not inside a type declaration." };
        if (owner is not ClassDeclarationSyntax and not RecordDeclarationSyntax)
            return new { error = $"'{owner.Identifier.ValueText}' is not a class/record: field initialisers are not safe here." };

        var typeSymbol = model.GetDeclaredSymbol(owner, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol is null) return new { error = $"'{owner.Identifier.ValueText}' did not resolve to a type." };
        if (typeSymbol.GetMembers(fieldName).Length > 0)
            return new { error = $"'{typeSymbol.ToDisplayString()}' already has a member named '{fieldName}'." };

        var contextUse = FindNonHoistableUse(expression, model, typeSymbol, cancellationToken);
        if (contextUse is not null)
            return new { error = $"The expression reads {contextUse}; a field initializer cannot use it." };

        // Replace occurrences in the member where the expression was found.
        var member = expression.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member is null) return new { error = "The expression is not inside a member declaration." };

        var occurrences = member.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(candidate => string.Equals(candidate.ToString(), expressionText, StringComparison.Ordinal))
            .ToList();

        // Drop nested hits: `a + b` contains `a`, and replacing both would corrupt the code.
        var outermost = occurrences
            .Where(candidate => !occurrences.Any(other => other != candidate && other.Span.Contains(candidate.Span)))
            .ToList();

        if (outermost.Count == 0) return new { error = "The expression was not found again for replacement." };

        var replacement = IdentifierName(fieldName);
        var newRoot = root.ReplaceNodes(outermost, (original, _) => replacement.WithTriviaFrom(original));

        var field = FieldDeclaration(
                VariableDeclaration(ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                    SingletonSeparatedList(
                        VariableDeclarator(Identifier(fieldName))
                            .WithInitializer(EqualsValueClause(expression.WithoutTrivia())))))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space),
                Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(Space)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            // The declaration is brand new, so its tokens carry no trivia: normalise it or it renders
            // as `private readonly int_offset=41;`.
            .NormalizeWhitespace()
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        var ownerAfterReplacement = newRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == owner.Identifier.ValueText);
        if (ownerAfterReplacement is null) return new { error = "Lost the type declaration while rewriting." };

        var leading = ownerAfterReplacement.Members.Count > 0
            ? ownerAfterReplacement.Members[0].GetLeadingTrivia()
            : new SyntaxTriviaList(ElasticCarriageReturnLineFeed, Whitespace("    "));

        newRoot = newRoot.ReplaceNode(
            ownerAfterReplacement,
            ownerAfterReplacement.WithMembers(ownerAfterReplacement.Members.Insert(0, field.WithLeadingTrivia(leading))));

        var oldText = sourceText.ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "introduce_field",
            type = typeSymbol.ToDisplayString(),
            field = fieldName,
            fieldType = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            expression = expressionText,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            replacements = outermost.Count,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>What makes the expression unusable in a field initializer, or null when it is context-free.</summary>
    private static string? FindNonHoistableUse(
        ExpressionSyntax expression, SemanticModel model, INamedTypeSymbol owner, CancellationToken cancellationToken)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return "'this'/'base'";
            if (node is AwaitExpressionSyntax)
                return "an awaited value";

            if (node is not (IdentifierNameSyntax or GenericNameSyntax)) continue;

            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is null) continue;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
                return $"'{symbol.Name}' (a local or parameter)";

            if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
            if (symbol.IsStatic) continue;

            // `other.Member` through a local is already refused above; anything else reaching an
            // instance member needs an instance, which a field initializer does not have.
            if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node &&
                access.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax))
            {
                return $"'{symbol.Name}' through an instance expression";
            }

            return $"the instance member '{symbol.Name}'";
        }

        return null;
    }
}
