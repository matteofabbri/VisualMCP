using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Move members of a type into a new partial file of the same type (the operation Rider calls
/// "Extract members to partial"): the type is marked <c>partial</c> in every declaration, the
/// selected members move to <c>&lt;Type&gt;.&lt;Suffix&gt;.cs</c> with the file's usings and its
/// namespace form, and the compiler decides whether the move is legal.
/// </summary>
public interface IExtractMembersToPartialService
{
    Task<object> Extract(string filePath, string typeName, string[] memberNames, string? suffix, bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class ExtractMembersToPartialService : IExtractMembersToPartialService
{
    private readonly IRoslynWorkspace _workspace;
    public ExtractMembersToPartialService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Extract(string filePath, string typeName, string[] memberNames, string? suffix = null,
        bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "A type name is required." };
        if (memberNames is null || memberNames.Length == 0) return new { error = "At least one member name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is not CompilationUnitSyntax unit)
            return new { error = $"Could not read the syntax of {Path.GetFileName(path)}." };

        var type = unit.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);

        if (type is null) return new { error = $"No class/struct/record/interface '{typeName}' in {Path.GetFileName(path)}." };
        if (type.TypeParameterList is not null)
            return new { error = "The type is generic: the partial declaration would have to repeat the constraints, which is not supported yet." };

        // ── Which members move ───────────────────────────────────────────────────────────
        var selected = new List<MemberDeclarationSyntax>();
        var missing = new List<string>();

        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var matches = type.Members
                .Where(member => member is not ConstructorDeclarationSyntax)
                .Where(member => MemberName(member) == name)
                .ToList();

            if (matches.Count == 0) missing.Add(name);
            else if (matches.Count > 1) return new { error = $"Several members named '{name}' (overloads); address one at a time." };
            else selected.Add(matches[0]);
        }

        if (missing.Count > 0)
        {
            var available = type.Members
                .Select(MemberName)
                .Where(name => name is not null)
                .Distinct()
                .ToList();

            return new
            {
                error = $"Not found on '{typeName}': {string.Join(", ", missing)}.",
                available,
            };
        }

        var className = type.Identifier.ValueText;
        var directory = Path.GetDirectoryName(document.FilePath)!;
        var fileName = $"{className}.{(string.IsNullOrWhiteSpace(suffix) ? "Partial" : suffix!.Trim())}.cs";
        var newFilePath = Path.Combine(directory, fileName);

        if (File.Exists(newFilePath))
            return new { error = $"{newFilePath} already exists; pass another suffix." };

        // ── The new partial file ─────────────────────────────────────────────────────────
        var newline = (await document.GetTextAsync(cancellationToken)).ToString().Contains("\r\n") ? "\r\n" : "\n";
        var usings = unit.Usings.Select(directive => directive.ToFullString().TrimEnd()).Where(text => text.Length > 0).ToList();
        var namespaceDeclaration = unit.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        var header = new System.Text.StringBuilder();
        foreach (var directive in usings) header.Append(directive).Append(newline);
        if (usings.Count > 0) header.Append(newline);

        string indent = string.Empty;
        if (namespaceDeclaration is FileScopedNamespaceDeclarationSyntax fileScoped)
        {
            header.Append("namespace ").Append(fileScoped.Name).Append(';').Append(newline).Append(newline);
        }
        else if (namespaceDeclaration is NamespaceDeclarationSyntax block)
        {
            header.Append("namespace ").Append(block.Name).Append(newline).Append('{').Append(newline);
            indent = "    ";
        }

        var modifiers = type.Modifiers
            .Where(token => !token.IsKind(SyntaxKind.PartialKeyword))
            .Select(token => token.Text)
            .ToList();

        var keyword = type is RecordDeclarationSyntax record
            ? "record " + (record.ClassOrStructKeyword.ValueText.Length > 0 ? record.ClassOrStructKeyword.ValueText + " " : string.Empty)
            : type.Keyword.Text;

        var typeHeader = string.Join(" ", modifiers.Append("partial").Append(keyword)) + " " + className;

        var body = new System.Text.StringBuilder();
        body.Append(indent).Append(typeHeader).Append(newline);
        body.Append(indent).Append('{').Append(newline);
        foreach (var member in selected)
        {
            body.Append(member.ToFullString().TrimEnd()).Append(newline).Append(newline);
        }
        body.Append(indent).Append('}').Append(newline);

        var newFileText = header + body.ToString();
        if (namespaceDeclaration is NamespaceDeclarationSyntax) newFileText += newline + "}" + newline;

        // ── The original: mark partial, remove the members ───────────────────────────────
        var partialType = type.Modifiers.Any(SyntaxKind.PartialKeyword)
            ? type
            : type.WithModifiers(type.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space)));

        var withoutMembers = partialType.WithMembers(
            SyntaxFactory.List(type.Members.Where(member => !selected.Contains(member))));

        var newRoot = root.ReplaceNode(type, withoutMembers);
        var newText = newRoot.ToFullString();

        // ── Gate: both files together must compile ───────────────────────────────────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        projected = projected.AddDocument(
            DocumentId.CreateNewId(document.Project.Id), fileName, SourceText.From(newFileText), filePath: newFilePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, [document.Project.Id], cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"moving {selected.Count} member(s) to a partial file");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);
            await File.WriteAllTextAsync(newFilePath, newFileText, cancellationToken);
            _workspace.TryAddDocument(document.Project.Id, fileName, newFilePath, SourceText.From(newFileText));
        }

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();

        return new
        {
            @refactoring = "extract_members_to_partial",
            type = className,
            moved = selected.Select(member => MemberName(member)).Where(name => name is not null).ToList(),
            file = document.FilePath,
            newFile = newFilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            newFilePreview = newFileText,
            note = dryRun
                ? "Preview only: pass dryRun=false to write both files."
                : "Both files written; the type is partial in every declaration.",
        };
    }

    private static string? MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        BaseTypeDeclarationSyntax nested => nested.Identifier.ValueText,
        _ => null,
    };
}
