using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Extract interface" for a Visual Basic class. Two things differ from the C# service and both
/// come from VB: the interface is written in VB's vocabulary (<c>Interface</c>, <c>ReadOnly Property</c>,
/// <c>Function</c>/<c>Sub</c>, <c>Event</c>), and patching the class is not a base list — VB requires
/// every implementing member to name what it implements (<c>Implements IName.Member</c>) on the member
/// itself, so the class statement and each member get a clause or the file would stop compiling.
/// </summary>
internal static class VbExtractInterface
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace,
        Solution solution,
        INamedTypeSymbol classSymbol,
        string interfaceName,
        bool includeProperties,
        bool includeMethods,
        bool includeEvents,
        bool addToClass,
        string? targetDirectory,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var members = classSymbol.GetMembers()
            .Where(member => !member.IsImplicitlyDeclared && !member.IsStatic &&
                             member.DeclaredAccessibility == Accessibility.Public)
            .Where(member => member switch
            {
                IPropertySymbol property => includeProperties && !property.IsIndexer,
                IMethodSymbol method => includeMethods && method.MethodKind == MethodKind.Ordinary && !method.IsOverride,
                IEventSymbol => includeEvents,
                _ => false,
            })
            .ToList();

        if (members.Count == 0)
            return new { error = "No eligible public instance members found to extract." };

        var location = classSymbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location?.SourceTree?.FilePath is null)
            return new { error = "The class has no source location on disk." };

        var document = VbRefactoringSupport.DocumentAt(solution, location.SourceTree.FilePath);
        if (document is null) return new { error = "Could not locate the class's document." };

        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (model is null || root is null) return new { error = "Could not read the class's file." };

        // A VB type is spelled the way the VB compiler spells it ("Integer", "List(Of String)"), so the
        // minimal display string is taken through the semantic model of the class rather than with a
        // language-neutral format that would print the C# keywords.
        var position = location.SourceSpan.Start;
        string Spell(ITypeSymbol type) =>
            type.ToMinimalDisplayString(model, position, SymbolDisplayFormat.MinimallyQualifiedFormat);

        var namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();

        var interfaceSource = BuildInterfaceSource(root.Imports, namespaceName, interfaceName, members, Spell);

        var sourceDirectory = Path.GetDirectoryName(document.FilePath)!;
        var destinationDirectory = targetDirectory is not null ? Path.GetFullPath(targetDirectory) : sourceDirectory;
        var destination = Path.Combine(destinationDirectory, interfaceName + ".vb");

        if (File.Exists(destination) && !dryRun)
            return new { error = $"Destination file already exists: {destination}" };

        var extracted = members.Select(member => $"{member.Kind} {classSymbol.Name}.{member.Name}").ToList();

        // ── the class patch: Implements IName on the type, Implements IName.Member on each member ──
        var changes = new Dictionary<DocumentId, List<TextChange>>();
        var notPatched = new List<string>();

        if (addToClass)
        {
            // VB points the reference at the block for a type with a body, but a partial type or an
            // enum-less one can sit on the statement, so both shapes are accepted.
            var classDeclaration = classSymbol.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(cancellationToken))
                .Select(node => node as TypeBlockSyntax ?? node.FirstAncestorOrSelf<TypeBlockSyntax>())
                .FirstOrDefault(node => node is not null);

            if (classDeclaration is null)
                return new { error = "Could not locate the class declaration." };

            var classDocument = VbRefactoringSupport.DocumentAt(solution, classDeclaration.SyntaxTree.FilePath);
            if (classDocument is null) return new { error = "Could not locate the document of the class declaration." };

            var classStatement = classDeclaration.BlockStatement;
            var text = await classDocument.GetTextAsync(cancellationToken);
            var newline = text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            // VB does not put "Implements" on the Class line: it is its own statement in the class body,
            // on the line after "Class C" (and it takes a comma list when one already exists).
            var implements = classDeclaration.Members.OfType<ImplementsStatementSyntax>().FirstOrDefault();
            Add(changes, classDocument.Id, new TextChange(
                new TextSpan(implements?.Span.End ?? classStatement.Span.End, 0),
                implements is not null
                    ? $", {interfaceName}"
                    : $"{newline}{IndentationOf(text, classStatement.SpanStart)}    Implements {interfaceName}"));

            foreach (var member in members)
            {
                // A VB block is itself a StatementSyntax, so the declaration is the statement the block
                // opens with: "End Function Implements I.Name" would not parse.
                var statement = member.DeclaringSyntaxReferences
                    .Select(reference => reference.GetSyntax(cancellationToken))
                    .Select(DeclarationStatement)
                    .FirstOrDefault(node => node is not null);

                if (statement is null ||
                    VbRefactoringSupport.DocumentAt(solution, statement.SyntaxTree.FilePath) is not { } owner)
                {
                    notPatched.Add(member.Name);
                    continue;
                }

                // VB spells the clause on each member; an existing "Implements A.B" takes the new one
                // after a comma, exactly the way the language writes a second interface member.
                Add(changes, owner.Id, new TextChange(new TextSpan(statement.Span.End, 0),
                    statement.ToString().Contains(" Implements ", StringComparison.OrdinalIgnoreCase)
                        ? $", {interfaceName}.{member.Name}"
                        : $" Implements {interfaceName}.{member.Name}"));
            }
        }

        // ── the projection the compiler judges: the patched class plus the new interface file ─────
        var after = solution;
        foreach (var (documentId, edits) in changes)
        {
            var target = after.GetDocument(documentId);
            if (target is null) continue;
            after = after.WithDocumentText(documentId, (await target.GetTextAsync(cancellationToken)).WithChanges(edits));
        }

        after = after.AddDocument(
            DocumentId.CreateNewId(document.Project.Id),
            interfaceName + ".vb",
            SourceText.From(interfaceSource),
            filePath: destination);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, after, [document.Project.Id], cancellationToken);
        if (!gate.Passed)
            return new
            {
                error = $"Refused: writing '{interfaceName}' and patching '{classSymbol.Name}' would add " +
                        $"{gate.Added} compiler error(s) ({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
            };

        var files = new List<object>();
        if (!dryRun)
        {
            Directory.CreateDirectory(destinationDirectory);
            await RefactoringFileWriter.WriteAsync(destination, interfaceSource, cancellationToken);

            foreach (var (documentId, _) in changes)
            {
                var target = solution.GetDocument(documentId);
                if (target?.FilePath is null) continue;

                var newText = (await after.GetDocument(documentId)!.GetTextAsync(cancellationToken)).ToString();
                await RefactoringFileWriter.WriteAndUpdateAsync(workspace, target, newText, cancellationToken);
            }

            workspace.TryAddDocument(document.Project.Id, interfaceName + ".vb", destination, SourceText.From(interfaceSource));
        }

        foreach (var (documentId, _) in changes)
        {
            var target = solution.GetDocument(documentId);
            if (target?.FilePath is null) continue;

            var before = (await target.GetTextAsync(cancellationToken)).ToString();
            var now = (await after.GetDocument(documentId)!.GetTextAsync(cancellationToken)).ToString();
            files.Add(new { file = target.FilePath, preview = RefactoringFileWriter.Diff(before, now) });
        }

        return new
        {
            @refactoring = "extract_interface",
            language = "Visual Basic",
            interfaceName,
            className = classSymbol.Name,
            destinationFile = destination,
            membersExtracted = extracted,
            implementsClauseAdded = addToClass,
            notPatched,
            files,
            dryRun,
            applied = !dryRun,
            interfaceSource,
            compilerErrors = new { before = gate.Before, after = gate.After },
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : $"Interface '{interfaceName}' written to {destination}.",
        };
    }

    /// <summary>The interface in VB's own vocabulary, with the class file's <c>Imports</c> above it.</summary>
    private static string BuildInterfaceSource(
        SyntaxList<ImportsStatementSyntax> imports,
        string? namespaceName,
        string interfaceName,
        IReadOnlyList<ISymbol> members,
        Func<ITypeSymbol, string> spell)
    {
        var builder = new StringBuilder();

        foreach (var import in imports)
            builder.AppendLine(import.ToString().TrimEnd());

        if (namespaceName is not null)
        {
            if (imports.Count > 0) builder.AppendLine();
            builder.AppendLine($"Namespace {namespaceName}");
            builder.AppendLine();
        }

        var indent = namespaceName is not null ? "    " : string.Empty;
        var inner = indent + "    ";

        builder.AppendLine($"{indent}Public Interface {interfaceName}");

        for (var index = 0; index < members.Count; index++)
        {
            if (index > 0) builder.AppendLine();
            builder.AppendLine(inner + RenderMember(members[index], spell));
        }

        builder.AppendLine($"{indent}End Interface");

        if (namespaceName is not null)
        {
            builder.AppendLine();
            builder.AppendLine("End Namespace");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The statement a member declaration opens with. A VB block is itself a statement, so the clause
    /// has to go on the <c>Function</c>/<c>Property</c>/<c>Event</c> line, not after <c>End Function</c>.
    /// </summary>
    private static StatementSyntax? DeclarationStatement(SyntaxNode node) =>
        node.ChildNodes().OfType<StatementSyntax>().FirstOrDefault()
        ?? node as StatementSyntax
        ?? node.FirstAncestorOrSelf<StatementSyntax>();

    private static string RenderMember(ISymbol member, Func<ITypeSymbol, string> spell) => member switch    {
        IPropertySymbol property => $"{(property.GetMethod is null ? "WriteOnly " : property.SetMethod is null ? "ReadOnly " : string.Empty)}" +
                                    $"Property {property.Name} As {spell(property.Type)}",

        IMethodSymbol method =>
            (method.ReturnsVoid ? "Sub " : "Function ") + method.Name + TypeParametersOf(method) +
            $"({string.Join(", ", method.Parameters.Select(parameter => ParameterOf(parameter, spell)))})" +
            (method.ReturnsVoid ? string.Empty : $" As {spell(method.ReturnType)}"),

        IEventSymbol @event => $"Event {@event.Name} As {spell(@event.Type)}",

        _ => string.Empty,
    };

    private static string TypeParametersOf(IMethodSymbol method) =>
        method.TypeParameters.Length == 0
            ? string.Empty
            : $"(Of {string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name))})";

    private static string ParameterOf(IParameterSymbol parameter, Func<ITypeSymbol, string> spell)
    {
        // VB's default is ByVal, so only ByRef has to be written; a ParamArray is declared as an array
        // but passed as the elements.
        var prefix = parameter.IsParams ? "ParamArray " : parameter.RefKind == RefKind.Ref ? "ByRef " : string.Empty;
        return $"{prefix}{parameter.Name} As {spell(parameter.Type)}";
    }

    private static void Add(Dictionary<DocumentId, List<TextChange>> changes, DocumentId documentId, TextChange change)
    {
        if (!changes.TryGetValue(documentId, out var list)) changes[documentId] = list = [];
        list.Add(change);
    }

    private static string IndentationOf(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, position));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }
}
