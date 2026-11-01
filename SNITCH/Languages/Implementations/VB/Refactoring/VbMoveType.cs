using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Move to file" for a Visual Basic type: a declaration is lifted out of a file that holds
/// several types into a file named after it, carrying the source file's <c>Imports</c> and the namespace
/// the type was declared in, so everything it could see before still resolves.
/// </summary>
internal static class VbMoveType
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace,
        Solution solution,
        INamedTypeSymbol typeSymbol,
        string? targetDirectory,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var location = typeSymbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location?.SourceTree?.FilePath is null)
            return new { error = "The type has no source location on disk." };

        var document = VbRefactoringSupport.DocumentAt(solution, location.SourceTree.FilePath);
        if (document?.FilePath is null) return new { error = "Could not locate the type's document." };

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root is null) return new { error = "Could not read the type's file." };

        var declaration = Declaration(root, typeSymbol.Name);
        if (declaration is null) return new { error = "Could not find the type declaration node." };

        if (declaration.Parent is TypeBlockSyntax)
            return new { error = "Cannot move a nested type. Extract it to a top-level declaration first." };

        var topLevel = root.DescendantNodes()
            .Count(node => IsTopLevel(node) &&
                           (node is TypeBlockSyntax || node is EnumBlockSyntax || node is DelegateStatementSyntax));

        if (topLevel <= 1)
            return new
            {
                error = "The source file contains only this type. It is already in its own file.",
                sourceFile = document.FilePath,
            };

        var destination = Path.Combine(
            targetDirectory is not null ? Path.GetFullPath(targetDirectory) : Path.GetDirectoryName(document.FilePath)!,
            typeSymbol.Name + ".vb");

        if (File.Exists(destination) &&
            !string.Equals(Path.GetFullPath(destination), Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase))
            return new { error = $"Destination file already exists: {destination}" };

        // An SDK VB project compiles every .vb file in the folder; one that lists its files explicitly
        // would silently not compile the new one.
        var projectFile = document.Project.FilePath is not null && File.Exists(document.Project.FilePath)
            ? await File.ReadAllTextAsync(document.Project.FilePath, cancellationToken)
            : null;

        if (projectFile is not null && projectFile.Contains("<Compile Include", StringComparison.OrdinalIgnoreCase))
            return new
            {
                error = "The project lists its files explicitly (<Compile Include>), so a new file on disk would " +
                        "not be compiled; add it to the project file by hand.",
            };

        var namespaceName = declaration.Ancestors().OfType<NamespaceBlockSyntax>().FirstOrDefault()
            ?.NamespaceStatement.Name.ToString();

        var newFile = BuildNewFile(root.Imports, namespaceName, declaration);
        var oldText = await document.GetTextAsync(cancellationToken);
        var remaining = root.RemoveNode(declaration, SyntaxRemoveOptions.KeepLeadingTrivia)!.ToFullString();

        if (dryRun)
            return new
            {
                @refactoring = "move_type",
                language = "Visual Basic",
                type = typeSymbol.ToDisplayString(),
                sourceFile = document.FilePath,
                destinationFile = destination,
                dryRun = true,
                applied = false,
                newFilePreview = newFile,
                preview = RefactoringFileWriter.Diff(oldText.ToString(), remaining),
                note = "Preview only: pass dryRun=false to write the files.",
            };

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await RefactoringFileWriter.WriteAsync(destination, newFile, cancellationToken);
        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, remaining, cancellationToken);
        workspace.TryAddDocument(document.Project.Id, Path.GetFileName(destination), destination, SourceText.From(newFile));

        return new
        {
            @refactoring = "move_type",
            language = "Visual Basic",
            type = typeSymbol.ToDisplayString(),
            sourceFile = document.FilePath,
            destinationFile = destination,
            dryRun = false,
            applied = true,
            preview = RefactoringFileWriter.Diff(oldText.ToString(), remaining),
            note = $"Type '{typeSymbol.Name}' moved to {destination}.",
        };
    }

    private static SyntaxNode? Declaration(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().FirstOrDefault(node => node switch
        {
            TypeBlockSyntax block => block.BlockStatement.Identifier.ValueText == name,
            EnumBlockSyntax block => block.EnumStatement.Identifier.ValueText == name,
            DelegateStatementSyntax statement => statement.Identifier.ValueText == name,
            _ => false,
        });

    /// <summary>A type declared directly in the file or directly in one of its namespace blocks.</summary>
    private static bool IsTopLevel(SyntaxNode node) => node.Parent is CompilationUnitSyntax or NamespaceBlockSyntax;

    /// <summary>The new file: the source file's <c>Imports</c>, the declaration's namespace, the type.</summary>
    private static string BuildNewFile(
        SyntaxList<ImportsStatementSyntax> imports,
        string? namespaceName,
        SyntaxNode declaration)
    {
        var builder = new StringBuilder();

        foreach (var import in imports)
            builder.AppendLine(import.ToString().TrimEnd());

        if (imports.Count > 0) builder.AppendLine();

        var body = declaration.ToFullString().TrimStart();

        if (namespaceName is null)
        {
            builder.Append(body);
            return builder.ToString();
        }

        builder.AppendLine($"Namespace {namespaceName}");
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
            builder.AppendLine("    " + line.TrimEnd());

        builder.AppendLine("End Namespace");
        return builder.ToString();
    }
}
