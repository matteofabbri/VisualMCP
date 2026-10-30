using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Constructor to factory method": the constructor becomes private and a static factory
/// takes its place; every <c>new T(...)</c> becomes <c>T.Create(...)</c>. Gated on the compiler.
/// </summary>
public interface IConstructorToFactoryService
{
    Task<object> ConstructorToFactory(
        string filePath,
        string typeName,
        string factoryName = "Create",
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConstructorToFactoryService : IConstructorToFactoryService
{
    private readonly IRoslynWorkspace _workspace;

    public ConstructorToFactoryService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConstructorToFactory(
        string filePath,
        string typeName,
        string factoryName = "Create",
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type name is required." };
        if (string.IsNullOrWhiteSpace(factoryName) || !SyntaxFacts.IsValidIdentifier(factoryName))
            return new { error = $"'{factoryName}' is not a valid method name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (declaration is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(path)}." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var constructors = declaration.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count == 0) return new { error = $"'{typeName}' has no constructor." };
        if (constructors.Count > 1) return new { error = $"'{typeName}' has {constructors.Count} constructors; give it a single one first." };

        var constructor = constructors[0];
        if (!constructor.Modifiers.Any(SyntaxKind.PublicKeyword))
            return new { error = "The constructor is not public." };
        if (type.GetMembers(factoryName).Length > 0)
            return new { error = $"'{typeName}' already has a member named '{factoryName}'." };

        if (model.GetDeclaredSymbol(constructor, cancellationToken) is not IMethodSymbol constructorSymbol)
            return new { error = "The constructor did not resolve." };

        var callSites = 0;
        var affected = new Dictionary<DocumentId, Document> { [document.Id] = document };
        foreach (var reference in await SymbolFinder.FindReferencesAsync(constructorSymbol, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                affected[location.Document.Id] = location.Document;
                callSites++;
            }
        }

        // ── new T(...) → T.Create(...) — before the factory is inserted, or its own `new T()` ──
        // would be rewritten too and the factory would call itself.
        var edits = new List<(Document Document, string NewText, int Rewritten)>();

        foreach (var (documentId, target) in affected)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var creations = targetRoot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(creation, cancellationToken).Symbol, constructorSymbol))
                .ToList();

            if (creations.Any(creation => creation.Initializer is not null))
                return new { error = "An object initializer cannot be expressed as a factory call; handling it would change semantics." };

            // Replacing changes the node kind (creation → invocation), so replace one at a time, from
            // the end: the spans of the earlier nodes are then still valid.
            var current = (SyntaxNode)targetRoot;
            foreach (var creation in creations.OrderByDescending(candidate => candidate.SpanStart))
            {
                if (current.FindNode(creation.Span) is not ObjectCreationExpressionSyntax node) continue;
                current = current.ReplaceNode(node, RewriteCreation(node, factoryName));
            }

            if (documentId == document.Id)
            {
                var rewrittenRoot = (CompilationUnitSyntax)current;
                var typeNode = rewrittenRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
                var constructorNode = typeNode?.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
                if (typeNode is null || constructorNode is null)
                    return new { error = $"Lost the declaration of '{typeName}' while rewriting." };

                current = rewrittenRoot.ReplaceNode(typeNode, typeNode.WithMembers(typeNode.Members
                    .Replace(constructorNode, PrivateConstructor(constructorNode))
                    .Insert(typeNode.Members.IndexOf(constructorNode) + 1, Factory(constructorNode, typeName, factoryName))));
            }

            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = current.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, creations.Count));
        }

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null) return new { error = "The project produced no compilation." };

        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projected = document.Project;
        foreach (var edit in edits)
            projected = projected.Solution.WithDocumentText(edit.Document.Id,
                    Microsoft.CodeAnalysis.Text.SourceText.From(edit.NewText))
                .GetProject(document.Project.Id)!;

        var afterDiagnostics = (await projected.GetCompilationAsync(cancellationToken))!
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: the factory would add {added} compiler error(s) ({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "constructor_to_factory",
            type = type.ToDisplayString(),
            factory = $"{typeName}.{factoryName}",
            callSitesRewritten = callSites,
            filesChanged = edits.Count,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>`new T(args)` → `T.Create(args)`, keeping the receiver type as written.</summary>
    private static InvocationExpressionSyntax RewriteCreation(ObjectCreationExpressionSyntax creation, string factoryName) =>
        InvocationExpression(
                ParseExpression($"{creation.Type.ToString().Trim()}.{factoryName}"),
                creation.ArgumentList ?? ArgumentList())
            .WithLeadingTrivia(creation.GetLeadingTrivia())
            .WithTrailingTrivia(creation.GetTrailingTrivia());

    private static ConstructorDeclarationSyntax PrivateConstructor(ConstructorDeclarationSyntax constructor) =>
        constructor.Modifiers.Any(SyntaxKind.PrivateKeyword)
            ? constructor
            : constructor.WithModifiers(constructor.Modifiers.Count == 0
                ? TokenList(Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space))
                : constructor.Modifiers.Replace(constructor.Modifiers[0],
                    Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space)));

    private static MethodDeclarationSyntax Factory(
        ConstructorDeclarationSyntax constructor, string typeName, string factoryName)
    {
        var parameters = (ParameterListSyntax)constructor.ParameterList.NormalizeWhitespace();
        var arguments = string.Join(", ", parameters.Parameters.Select(parameter => parameter.Identifier.ValueText));

        return MethodDeclaration(ParseTypeName(typeName), Identifier(factoryName))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space),
                Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(Space)))
            .WithParameterList(parameters)
            .WithBody(Block(ParseStatement($"return new {typeName}({arguments});")))
            .NormalizeWhitespace()
            .WithLeadingTrivia(new SyntaxTriviaList(ElasticCarriageReturnLineFeed, ElasticCarriageReturnLineFeed)
                .AddRange(constructor.GetLeadingTrivia()))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);
    }
}
