using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Extract class", for the case that can be checked mechanically: the selected members must
/// not read any instance state that stays behind, so the new class is self-contained; the original
/// keeps one field of the new type plus forwarding members, so its callers are unaffected.
/// </summary>
public interface IExtractClassService
{
    Task<object> ExtractClass(
        string filePath,
        string typeName,
        string[] memberNames,
        string newClassName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ExtractClassService : IExtractClassService
{
    private readonly IRoslynWorkspace _workspace;

    public ExtractClassService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ExtractClass(
        string filePath,
        string typeName,
        string[] memberNames,
        string newClassName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to extract from is required." };
        if (memberNames is null || memberNames.Length == 0) return new { error = "Name the members to extract." };
        if (string.IsNullOrWhiteSpace(newClassName) || !SyntaxFacts.IsValidIdentifier(newClassName))
            return new { error = $"'{newClassName}' is not a valid class name." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.ExtractClassAsync(
                _workspace, filePath, typeName, memberNames, newClassName, dryRun, cancellationToken);

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
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || tree is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var typeDeclaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (typeDeclaration is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(path)}." };

        if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var directory = Path.GetDirectoryName(document.FilePath);
        if (directory is null) return new { error = "The document has no directory on disk." };

        var newFilePath = Path.Combine(directory, newClassName + ".cs");
        if (File.Exists(newFilePath)) return new { error = $"{newFilePath} already exists; pick another name." };

        // ── Locate and validate the selection ─────────────────────────────────────────────
        var selected = new List<MemberDeclarationSyntax>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = typeDeclaration.Members.FirstOrDefault(candidate => NameOf(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No extractable member '{name}' in '{typeName}' (methods, properties and fields only).",
                    available = typeDeclaration.Members.Select(NameOf).Where(n => n is not null).Distinct().ToList(),
                };

            var symbol = SymbolOf(member, model, cancellationToken);
            if (symbol is { IsStatic: true }) return new { error = $"'{name}' is static; a forwarded instance member cannot stand in for it." };

            selected.Add(member);
        }

        var selectedSymbols = new HashSet<ISymbol>(
            selected.Select(member => SymbolOf(member, model, cancellationToken)).Where(symbol => symbol is not null)!,
            SymbolEqualityComparer.Default);

        var movedFields = selected.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .Select(variable => variable.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var constructor in typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>())
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
                    return new { error = $"Field '{target}' is assigned in the constructor, which stays behind; initialise it in the new class first." };
            }
        }

        // Every instance member the selection reads must travel with it.
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
                    error = $"'{memberNames.First(name => NameOf(member) == name)}' reads '{symbol.Name}', which is not being extracted. " +
                            "Add it to the selection or leave the member where it is.",
                };
            }
        }

        // A field cannot be forwarded, so only the members that travel with it may read it:
        // a member staying behind, or any code outside the type, would lose it.
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

                    var where = typeDeclaration.Span.Contains(span)
                        ? "a member that stays behind"
                        : $"code outside '{typeName}'";
                    return new
                    {
                        error = $"Field '{variable.Identifier.ValueText}' is read by {where} " +
                                $"({Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}); " +
                                "a field cannot be forwarded.",
                    };
                }
            }
        }

        // ── Build the new class and the forwarding members ────────────────────────────────
        var indentation = IndentationOf(typeDeclaration);
        var memberIndentation = indentation + "    ";

        // NormalizeWhitespace keeps each member's internal layout but drops the surrounding blank
        // lines, so members are re-indented here and separated by one blank line.
        var membersText = string.Join(Environment.NewLine + Environment.NewLine,
            selected.Select(member => memberIndentation + Reindent(
                member.NormalizeWhitespace().ToFullString().TrimStart(' ', '\t', '\r', '\n'), "    ")));

        var namespaceName = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : null;

        var classText = (namespaceName is null ? string.Empty : $"namespace {namespaceName};{Environment.NewLine}{Environment.NewLine}") +
                        $"internal sealed class {newClassName}{Environment.NewLine}{{{Environment.NewLine}{membersText}{Environment.NewLine}}}{Environment.NewLine}";

        var fieldName = "_" + char.ToLowerInvariant(newClassName[0]) + newClassName[1..];
        var holder = ParseMemberDeclaration($"private readonly {newClassName} {fieldName} = new {newClassName}();");
        if (holder is null) return new { error = "Could not build the field that holds the new class." };

        var shims = new Dictionary<MemberDeclarationSyntax, MemberDeclarationSyntax>();
        foreach (var member in selected)
        {
            // A field moves outright: it is private, has no shim, and the reference check above
            // guarantees nobody left behind reads it.
            if (member is FieldDeclarationSyntax) continue;

            var shim = BuildShim(member, fieldName);
            if (shim is null)
                return new { error = $"'{NameOf(member)}' cannot be forwarded (only methods, and properties without an init accessor)." };

            shims[member] = shim.WithLeadingTrivia(member.GetLeadingTrivia()).WithTrailingTrivia(ElasticCarriageReturnLineFeed);
        }

        // One pass over the original members: a selected method or property becomes its shim, a
        // selected field disappears (it now lives in the new class), everything else is kept.
        // Rebuilding beats SeparatedSyntaxList.Replace, which cannot match nodes once the list has
        // been rebuilt by a previous replacement.
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in typeDeclaration.Members)
        {
            if (shims.TryGetValue(member, out var shim))
            {
                members.Add(shim);
            }
            else if (member is not FieldDeclarationSyntax || !selected.Contains(member))
            {
                members.Add(member);
            }
        }

        members.Insert(0, holder
            .WithLeadingTrivia(Whitespace(memberIndentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed));

        var newDocumentText = root.ReplaceNode(typeDeclaration, typeDeclaration.WithMembers(List(members))).ToFullString();

        // ── Gate: the project must still compile, with the new file in place ──────────────
        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projected = document.Project.Solution
            .WithDocumentText(document.Id, SourceText.From(newDocumentText))
            .GetProject(document.Project.Id)!;
        var projectedDocument = projected.AddDocument(newClassName + ".cs", SourceText.From(classText), filePath: newFilePath);

        var afterDiagnostics = (await projectedDocument.Project.GetCompilationAsync(cancellationToken))!
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: the extraction would add {added} compiler error(s) ({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newDocumentText, cancellationToken);
            await Platform.FileText.WriteAsync(newFilePath, classText, cancellationToken);
            _workspace.TryAddDocument(document.Project.Id, newClassName + ".cs", newFilePath, SourceText.From(classText));
        }

        return new
        {
            @refactoring = "extract_class",
            from = typeSymbol.ToDisplayString(),
            to = newClassName,
            newFile = newFilePath,
            members = selected.Select(NameOf).ToList(),
            forwardingField = $"{newClassName} {fieldName}",
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>`member(...)` becomes `_holder.member(...)`, `P` becomes `_holder.P`.</summary>
    private static MemberDeclarationSyntax? BuildShim(MemberDeclarationSyntax member, string fieldName) => member switch
    {
        MethodDeclarationSyntax method => MethodDeclaration(method.ReturnType, method.Identifier)
            .WithModifiers(method.Modifiers)
            .WithParameterList(method.ParameterList)
            .WithTypeParameterList(method.TypeParameterList)
            .WithConstraintClauses(method.ConstraintClauses)
            .WithExpressionBody(ArrowExpressionClause(ParseExpression(
                $"{fieldName}.{method.Identifier.ValueText}({string.Join(", ", method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText))})")))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .NormalizeWhitespace(),

        PropertyDeclarationSyntax property => BuildPropertyShim(property, fieldName),

        _ => null,
    };

    private static PropertyDeclarationSyntax? BuildPropertyShim(PropertyDeclarationSyntax property, string fieldName)
    {
        var accessors = property.AccessorList?.Accessors;
        var hasSetter = accessors?.Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
        var hasInit = accessors?.Any(accessor => accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
        if (hasInit) return null;

        var forward = $"{fieldName}.{property.Identifier.ValueText}";

        if (!hasSetter)
        {
            return PropertyDeclaration(property.Type, property.Identifier)
                .WithModifiers(property.Modifiers)
                .WithExpressionBody(ArrowExpressionClause(ParseExpression(forward)))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace();
        }

        var setter = accessors!.Value.First(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
        return PropertyDeclaration(property.Type, property.Identifier)
            .WithModifiers(property.Modifiers)
            .WithAccessorList(AccessorList(List(new[]
            {
                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithExpressionBody(ArrowExpressionClause(ParseExpression(forward)))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithModifiers(setter.Modifiers)
                    .WithExpressionBody(ArrowExpressionClause(ParseExpression($"{forward} = value")))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            })))
            .NormalizeWhitespace();
    }

    private static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 => field.Declaration.Variables[0].Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// The symbol a member declares. A field is declared by its variable declarator, not by the
    /// field declaration — <c>GetDeclaredSymbol(FieldDeclarationSyntax)</c> returns null.
    /// </summary>
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
