using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Convert anonymous type to declared type" (Anonymous2Declared): one <c>new { ... }</c> becomes
/// a real class in its own file, with a constructor and read-only properties, and the creation is
/// rewritten to <c>new TypeName(values...)</c>. The shape is taken from the anonymous type symbol, so
/// the member types are the ones the compiler inferred.
/// </summary>
public interface IConvertAnonymousToTypeService
{
    Task<object> ConvertAnonymousToType(
        string filePath,
        int line,
        string? typeName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConvertAnonymousToTypeService : IConvertAnonymousToTypeService
{
    private readonly IRoslynWorkspace _workspace;

    public ConvertAnonymousToTypeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConvertAnonymousToType(
        string filePath,
        int line,
        string? typeName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line <= 0) return new { error = "A 1-based line number is required." };

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
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.ConvertAnonymousToTypeAsync(
                _workspace, filePath, line, typeName, dryRun, cancellationToken);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var creations = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().ToList();
        var target = creations.FirstOrDefault(creation =>
        {
            var span = creation.GetLocation().GetLineSpan();
            return line >= span.StartLinePosition.Line + 1 && line <= span.EndLinePosition.Line + 1;
        });

        if (target is null)
            return new
            {
                error = $"No anonymous object creation on line {line} of {Path.GetFileName(path)}.",
                anonymousCreations = creations
                    .Select(creation => creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList(),
            };

        if (model.GetTypeInfo(target, cancellationToken).Type is not INamedTypeSymbol anonymous)
            return new { error = "The creation did not resolve to an anonymous type." };

        var name = string.IsNullOrWhiteSpace(typeName) ? "AnonymousType" : typeName!;
        if (!SyntaxFacts.IsValidIdentifier(name)) return new { error = $"'{name}' is not a valid class name." };

        var ns = target.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        if (compilation.GetTypeByMetadataName((ns is null ? string.Empty : ns + ".") + name) is not null)
            return new { error = $"A type '{name}' already exists in '{ns ?? "the global namespace"}'; pass another typeName." };

        var directory = Path.GetDirectoryName(document.FilePath);
        if (directory is null) return new { error = "The document has no directory on disk." };

        var newFilePath = Path.Combine(directory, name + ".cs");
        if (File.Exists(newFilePath)) return new { error = $"{newFilePath} already exists; pass another typeName." };

        // ── The shape: one property per initializer, in declaration order ──────────────────
        var members = new List<(string Property, ITypeSymbol Type, ExpressionSyntax Value)>();
        foreach (var initializer in target.Initializers)
        {
            var memberName = initializer.NameEquals?.Name.Identifier.ValueText
                             ?? (initializer.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText
                             ?? (initializer.Expression as IdentifierNameSyntax)?.Identifier.ValueText;

            if (memberName is null)
                return new { error = "An initializer of the anonymous type has no member name to project." };

            var property = anonymous.GetMembers(memberName).OfType<IPropertySymbol>().FirstOrDefault();
            if (property is null)
                return new { error = $"The anonymous type has no property '{memberName}'." };

            members.Add((memberName, property.Type, initializer.Expression));
        }

        if (members.Any(member => string.Equals(member.Property, name, StringComparison.Ordinal)))
            return new { error = $"The anonymous type has a member named '{name}', which cannot be a property of a class with that name." };

        // ── Build the class ───────────────────────────────────────────────────────────────
        var parameterNames = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (property, _, _) in members)
        {
            var candidate = CamelCase(property);
            var unique = candidate;
            for (var suffix = 2; !used.Add(unique); suffix++) unique = candidate + suffix;
            parameterNames.Add(unique);
        }

        var typeTexts = members.Select(member => member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToList();
        var classText = BuildClassText(ns, name, members.Select(member => member.Property).ToList(), parameterNames, typeTexts);

        // ── Rewrite the creation ──────────────────────────────────────────────────────────
        var arguments = members.Select(member => Argument(member.Value.WithoutTrivia())).ToList();
        var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space),
            Math.Max(0, arguments.Count - 1)).ToList();

        var replacement = ObjectCreationExpression(IdentifierName(name))
            .WithNewKeyword(Token(SyntaxKind.NewKeyword).WithTrailingTrivia(Space))
            .WithArgumentList(ArgumentList(SeparatedList(arguments, separators)))
            .WithTriviaFrom(target);

        var newDocumentText = root.ReplaceNode(target, replacement).ToFullString();

        // ── Gate: the project must still compile, with the new file in place ──────────────
        var projected = document.Project.Solution
            .WithDocumentText(document.Id, SourceText.From(newDocumentText))
            .GetProject(document.Project.Id)!;
        var projectedDocument = projected.AddDocument(name + ".cs", SourceText.From(classText), filePath: newFilePath);

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projectedDocument.Project.Solution, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the conversion");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newDocumentText, cancellationToken);
            await Platform.FileText.WriteAsync(newFilePath, classText, cancellationToken);
            _workspace.TryAddDocument(document.Project.Id, name + ".cs", newFilePath, SourceText.From(classText));
        }

        return new
        {
            @refactoring = "convert_anonymous_to_type",
            type = ns is null ? name : ns + "." + name,
            newFile = newFilePath,
            members = members.Select((member, index) => new { member.Property, Parameter = parameterNames[index], Type = typeTexts[index] }).ToList(),
            file = document.FilePath,
            line,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            newFilePreview = classText,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newDocumentText),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string BuildClassText(
        string? ns, string name, IReadOnlyList<string> properties, IReadOnlyList<string> parameters, IReadOnlyList<string> types)
    {
        var builder = new StringBuilder();
        if (ns is not null) builder.Append("namespace ").Append(ns).AppendLine(";").AppendLine();

        builder.Append("internal sealed class ").Append(name).AppendLine();
        builder.AppendLine("{");
        builder.Append("    public ").Append(name).Append('(');

        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(types[i]).Append(' ').Append(parameters[i]);
        }

        builder.AppendLine(")");
        builder.AppendLine("    {");
        for (var i = 0; i < properties.Count; i++)
            builder.Append("        ").Append(properties[i]).Append(" = ").Append(parameters[i]).AppendLine(";");
        builder.AppendLine("    }");

        for (var i = 0; i < properties.Count; i++)
        {
            builder.AppendLine();
            builder.Append("    public ").Append(types[i]).Append(' ').Append(properties[i]).AppendLine(" { get; }");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string CamelCase(string name)
    {
        if (name.Length == 0) return name;
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return SyntaxFacts.GetKeywordKind(camel) != SyntaxKind.None ? "@" + camel : camel;
    }
}
