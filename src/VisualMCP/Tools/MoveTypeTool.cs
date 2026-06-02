using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class MoveTypeTool
{
    [McpServerTool, Description("Move a type declaration to a new file whose name matches the type name (e.g. MyClass -> MyClass.cs in the same directory). Equivalent to ReSharper 'Move to File'. Requires LoadSolution first.")]
    public static async Task<object> MoveType(
        [Description("Name of the type to move (class, struct, interface, enum, record)")] string typeName,
        [Description("Optional: target directory for the new file. Defaults to the same directory as the source file.")] string? targetDirectory = null,
        [Description("Dry run — show what would happen without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        // Find the type symbol
        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var typeSymbols = candidates.OfType<INamedTypeSymbol>().ToList();
        if (typeSymbols.Count == 0)
            return new { error = $"Type '{typeName}' not found." };
        if (typeSymbols.Count > 1)
            return new
            {
                error = $"Ambiguous: {typeSymbols.Count} types named '{typeName}'.",
                candidates = typeSymbols.Select(s => s.ToDisplayString()).ToList(),
            };

        var typeSymbol = typeSymbols[0];
        var declLocation = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation is null)
            return new { error = "Type has no source location." };

        // Find the source document
        var tree = declLocation.SourceTree;
        var sourceDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);

        if (sourceDoc is null || sourceDoc.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null)
            return new { error = "Could not parse source file." };

        // Locate the type declaration node
        var typeNode = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Concat<SyntaxNode>(root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            .FirstOrDefault(n => n switch
            {
                TypeDeclarationSyntax t => t.Identifier.Text.Equals(typeName, StringComparison.OrdinalIgnoreCase),
                EnumDeclarationSyntax e => e.Identifier.Text.Equals(typeName, StringComparison.OrdinalIgnoreCase),
                _ => false,
            });

        if (typeNode is null)
            return new { error = "Could not find type declaration node." };

        // Refuse if the type is nested
        if (typeNode.Parent is TypeDeclarationSyntax)
            return new { error = "Cannot move a nested type. Extract it to a top-level declaration first." };

        // Refuse if the source file contains only this type (nothing to move)
        var topLevelTypes = root.Members.OfType<TypeDeclarationSyntax>()
            .Concat<SyntaxNode>(root.Members.OfType<EnumDeclarationSyntax>())
            .ToList();

        if (topLevelTypes.Count == 1)
            return new
            {
                error = "The source file contains only this type. It is already in its own file.",
                sourceFile = sourceDoc.FilePath,
            };

        // Build destination path
        var sourceDir = Path.GetDirectoryName(sourceDoc.FilePath)!;
        var destDir = targetDirectory is not null
            ? Path.GetFullPath(targetDirectory)
            : sourceDir;
        var destFile = Path.Combine(destDir, typeName + ".cs");

        if (File.Exists(destFile) && !string.Equals(destFile, sourceDoc.FilePath, StringComparison.OrdinalIgnoreCase))
            return new { error = $"Destination file already exists: {destFile}" };

        // Build the new file content: same usings + namespace wrapper + the type
        var usings = root.Usings;
        var namespaceDecl = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        string newFileContent;
        if (namespaceDecl is FileScopedNamespaceDeclarationSyntax fileScopedNs)
        {
            var nsName = fileScopedNs.Name.ToString();
            newFileContent = BuildNewFile(usings, nsName, typeNode, fileScopedNamespace: true);
        }
        else if (namespaceDecl is NamespaceDeclarationSyntax blockNs)
        {
            var nsName = blockNs.Name.ToString();
            newFileContent = BuildNewFile(usings, nsName, typeNode, fileScopedNamespace: false);
        }
        else
        {
            // No namespace
            newFileContent = BuildNewFile(usings, null, typeNode, fileScopedNamespace: false);
        }

        // Remove the type from the source file
        SyntaxNode newSourceRoot = root.RemoveNode(typeNode, SyntaxRemoveOptions.KeepLeadingTrivia)!;

        if (dryRun)
        {
            return new
            {
                dryRun = true,
                typeName,
                sourceFile = sourceDoc.FilePath,
                destinationFile = destFile,
                newFilePreview = newFileContent,
            };
        }

        // Write destination file
        Directory.CreateDirectory(destDir);
        await File.WriteAllTextAsync(destFile, newFileContent, System.Text.Encoding.UTF8);

        // Write updated source file
        await File.WriteAllTextAsync(sourceDoc.FilePath, newSourceRoot.ToFullString(), System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            typeName,
            sourceFile = sourceDoc.FilePath,
            destinationFile = destFile,
            message = $"Type '{typeName}' moved to {destFile}.",
        };
    }

    private static string BuildNewFile(
        SyntaxList<UsingDirectiveSyntax> usings,
        string? namespaceName,
        SyntaxNode typeNode,
        bool fileScopedNamespace)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var u in usings)
            sb.AppendLine(u.ToFullString().TrimEnd());

        if (usings.Count > 0)
            sb.AppendLine();

        if (namespaceName is not null)
        {
            if (fileScopedNamespace)
            {
                sb.AppendLine($"namespace {namespaceName};");
                sb.AppendLine();
                sb.Append(typeNode.ToFullString().TrimStart());
            }
            else
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
                foreach (var line in typeNode.ToFullString().TrimStart().Split('\n'))
                    sb.AppendLine("    " + line.TrimEnd());
                sb.AppendLine("}");
            }
        }
        else
        {
            sb.Append(typeNode.ToFullString().TrimStart());
        }

        return sb.ToString();
    }
}
