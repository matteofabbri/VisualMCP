using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Extract superclass": the selected members move to a new class in a new file and the
/// original type derives from it. The selection must be self-contained — a member that travels may
/// only read members that travel with it, and a field cannot be forwarded — so the result compiles
/// without the original class keeping a copy of anything.
/// </summary>
public interface IExtractSuperclassService
{
    Task<object> ExtractSuperclass(
        string filePath,
        string typeName,
        string[] memberNames,
        string baseClassName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ExtractSuperclassService : IExtractSuperclassService
{
    private readonly IRoslynWorkspace _workspace;

    public ExtractSuperclassService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ExtractSuperclass(
        string filePath,
        string typeName,
        string[] memberNames,
        string baseClassName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to extract from is required." };
        if (memberNames is null || memberNames.Length == 0) return new { error = "Name the members to move to the base class." };
        if (string.IsNullOrWhiteSpace(baseClassName) || !SyntaxFacts.IsValidIdentifier(baseClassName))
            return new { error = $"'{baseClassName}' is not a valid class name." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.ExtractSuperclassAsync(
                _workspace, filePath, typeName, memberNames, baseClassName, dryRun, cancellationToken);

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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (declaration is null)
            return new { error = $"No class '{typeName}' in {Path.GetFileName(path)} (only classes get a superclass)." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        if (typeSymbol.IsStatic) return new { error = $"'{typeName}' is static; a static class cannot derive from a base class." };
        if (typeSymbol.BaseType is { SpecialType: not SpecialType.System_Object } existingBase)
            return new { error = $"'{typeName}' already derives from '{existingBase.ToDisplayString()}'; C# allows one base class." };

        var directory = Path.GetDirectoryName(document.FilePath);
        if (directory is null) return new { error = "The document has no directory on disk." };

        var newFilePath = Path.Combine(directory, baseClassName + ".cs");
        if (File.Exists(newFilePath)) return new { error = $"{newFilePath} already exists; pick another name." };

        var ns = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : null;
        if (compilation.GetTypeByMetadataName((ns is null ? string.Empty : ns + ".") + baseClassName) is not null)
            return new { error = $"A type '{baseClassName}' already exists in '{ns ?? "the global namespace"}'; pick another name." };

        // ── Locate and validate the selection ─────────────────────────────────────────────
        var selected = new List<MemberDeclarationSyntax>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = declaration.Members.FirstOrDefault(candidate => NameOf(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No extractable member '{name}' in '{typeName}' (methods, properties and fields only).",
                    available = declaration.Members.Select(NameOf).Where(candidate => candidate is not null).Distinct().ToList(),
                };

            var symbol = SymbolOf(member, model, cancellationToken);
            if (symbol is { IsStatic: true }) return new { error = $"'{name}' is static; a static member cannot become an inherited instance member this way." };
            if (symbol is { IsAbstract: true }) return new { error = $"'{name}' is abstract; the base class would need it too." };

            if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OverrideKeyword)))
                return new { error = $"'{name}' is an override; it cannot move to a class that has nothing to override." };

            selected.Add(member);
        }

        var selectedSymbols = new HashSet<ISymbol>(
            selected.Select(member => SymbolOf(member, model, cancellationToken)).Where(symbol => symbol is not null)!,
            SymbolEqualityComparer.Default);

        var movedFields = selected.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .Select(variable => variable.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var constructor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var assignment in constructor.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var target = assignment.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
                    _ => null,
                };

                if (target is not null && movedFields.Contains(target))
                    return new { error = $"Field '{target}' is assigned in a constructor that stays behind; initialise it in '{baseClassName}' instead." };
            }
        }

        foreach (var member in selected)
        {
            foreach (var node in member.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol)) continue;
                if (symbol.IsStatic) continue;
                if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, typeSymbol)) continue;
                if (selectedSymbols.Contains(symbol)) continue;

                return new
                {
                    error = $"'{NameOf(member)}' reads '{symbol.Name}', which stays in '{typeName}'. " +
                            "Add it to the selection or leave the member where it is.",
                };
            }
        }

        // A field moves and is not forwarded: the members that stay behind must not read it.
        foreach (var variable in selected.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables))
        {
            var fieldSymbol = model.GetDeclaredSymbol(variable, cancellationToken);
            if (fieldSymbol is null) continue;

            foreach (var reference in await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken))
            {
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    var span = location.Location.SourceSpan;
                    if (selected.Any(member => member.Span.Contains(span))) continue;
                    if (span.IntersectsWith(variable.Identifier.Span)) continue;

                    var where = declaration.Span.Contains(span)
                        ? "a member that stays behind"
                        : $"code outside '{typeName}'";
                    return new
                    {
                        error = $"Field '{variable.Identifier.ValueText}' is read by {where} " +
                                $"({Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}); " +
                                "a field cannot be inherited privately.",
                    };
                }
            }
        }

        // ── Build the base class and the derived type ─────────────────────────────────────
        var indentation = IndentationOf(declaration);
        var memberIndentation = indentation + "    ";

        var membersText = string.Join(Environment.NewLine + Environment.NewLine,
            selected.Select(member => memberIndentation + Reindent(
                member.NormalizeWhitespace().ToFullString().TrimStart(' ', '\t', '\r', '\n'), "    ")));

        var accessibility = typeSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            _ => "internal ",
        };
        var abstractKeyword = typeSymbol.IsAbstract ? "abstract " : string.Empty;

        var classText = (ns is null ? string.Empty : $"namespace {ns};{Environment.NewLine}{Environment.NewLine}") +
                        $"{accessibility}{abstractKeyword}class {baseClassName}{Environment.NewLine}{{{Environment.NewLine}{membersText}{Environment.NewLine}}}{Environment.NewLine}";

        var remaining = declaration.Members
            .Where(member => !selected.Contains(member))
            .ToList();

        var baseList = declaration.BaseList is { } existing
            ? existing.WithTypes(existing.Types.Insert(0, SimpleBaseType(
                IdentifierName(baseClassName).WithLeadingTrivia(Space).WithTrailingTrivia(Space))))
            : BaseList(
                Token(SyntaxKind.ColonToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space),
                SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(IdentifierName(baseClassName))));

        ClassDeclarationSyntax derived;
        if (declaration.BaseList is not null)
        {
            derived = declaration.WithMembers(List(remaining)).WithBaseList(baseList);
        }
        else
        {
            // The newline before the body is trailing trivia of the identifier (or of the closing `>`
            // of the type parameter list). It has to move after the base list, or the type prints
            // `class C` + newline + `: Base`.
            var anchor = declaration.TypeParameterList?.GreaterThanToken ?? declaration.Identifier;
            var trailing = anchor.TrailingTrivia;

            var trimmed = declaration.WithIdentifier(declaration.Identifier.WithTrailingTrivia());
            if (declaration.TypeParameterList is { } typeParameters)
                trimmed = trimmed.WithTypeParameterList(typeParameters.WithGreaterThanToken(
                    typeParameters.GreaterThanToken.WithTrailingTrivia()));

            derived = trimmed
                .WithMembers(List(remaining))
                .WithBaseList(baseList.WithTrailingTrivia(trailing));
        }

        var newDocumentText = root.ReplaceNode(declaration, derived).ToFullString();

        // ── Gate: the project must still compile, with the new file in place ──────────────
        var projected = document.Project.Solution
            .WithDocumentText(document.Id, SourceText.From(newDocumentText))
            .GetProject(document.Project.Id)!;
        var projectedDocument = projected.AddDocument(baseClassName + ".cs", SourceText.From(classText), filePath: newFilePath);

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projectedDocument.Project.Solution, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the extraction");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newDocumentText, cancellationToken);
            await Platform.FileText.WriteAsync(newFilePath, classText, cancellationToken);
            _workspace.TryAddDocument(document.Project.Id, baseClassName + ".cs", newFilePath, SourceText.From(classText));
        }

        return new
        {
            @refactoring = "extract_superclass",
            from = typeSymbol.ToDisplayString(),
            baseClass = baseClassName,
            newFile = newFilePath,
            members = selected.Select(NameOf).ToList(),
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newDocumentText),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 => field.Declaration.Variables[0].Identifier.ValueText,
        _ => null,
    };

    private static ISymbol? SymbolOf(MemberDeclarationSyntax member, SemanticModel model, CancellationToken cancellationToken) =>
        member switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method, cancellationToken),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property, cancellationToken),
            FieldDeclarationSyntax { Declaration.Variables.Count: 1 } field =>
                model.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken),
            _ => null,
        };

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static string Reindent(string text, string indentation)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }
}
