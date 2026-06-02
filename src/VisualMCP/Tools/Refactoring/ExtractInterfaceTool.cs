using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ExtractInterfaceTool
{
    [McpServerTool, Description("Generate an interface from the public instance members of a class and optionally add it to the class's base list. Writes the interface to a new file. Equivalent to ReSharper 'Extract Interface'. Requires LoadSolution first.")]
    public static async Task<object> ExtractInterface(
        [Description("Name of the class to extract an interface from")] string className,
        [Description("Name for the new interface (default: 'I' + className)")] string? interfaceName = null,
        [Description("Include public properties (default: true)")] bool includeProperties = true,
        [Description("Include public methods (default: true)")] bool includeMethods = true,
        [Description("Include public events (default: false)")] bool includeEvents = false,
        [Description("Add the interface to the class's base list (default: true)")] bool addToClass = true,
        [Description("Target directory for the new interface file (default: same directory as the class)")] string? targetDirectory = null,
        [Description("Dry run â€” return generated code without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        interfaceName ??= "I" + className;

        // Find the class
        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(className, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var classSymbols = candidates
            .OfType<INamedTypeSymbol>()
            .Where(s => s.TypeKind == TypeKind.Class || s.TypeKind == TypeKind.Struct)
            .ToList();

        if (classSymbols.Count == 0)
            return new { error = $"Class '{className}' not found." };
        if (classSymbols.Count > 1)
            return new
            {
                error = $"Ambiguous: {classSymbols.Count} types named '{className}'.",
                candidates = classSymbols.Select(s => s.ToDisplayString()).ToList(),
            };

        var classSymbol = classSymbols[0];

        // Collect members to include
        var members = classSymbol.GetMembers()
            .Where(m =>
                !m.IsImplicitlyDeclared &&
                !m.IsStatic &&
                m.DeclaredAccessibility == Accessibility.Public &&
                m switch
                {
                    IPropertySymbol p  => includeProperties && !p.IsIndexer,
                    IMethodSymbol meth => includeMethods &&
                                         meth.MethodKind == MethodKind.Ordinary &&
                                         !meth.IsOverride,
                    IEventSymbol       => includeEvents,
                    _                  => false,
                })
            .ToList();

        if (members.Count == 0)
            return new { error = "No eligible public instance members found to extract." };

        // Find the source document of the class
        var declLocation = classSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation is null)
            return new { error = "Class has no source location." };

        var tree = declLocation.SourceTree;
        var sourceDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);

        if (sourceDoc is null || sourceDoc.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null)
            return new { error = "Could not parse source file." };

        // Determine namespace
        var classNode = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.Text.Equals(className, StringComparison.OrdinalIgnoreCase));

        var namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();

        // Build interface source
        var interfaceSource = BuildInterfaceSource(
            root.Usings, namespaceName, interfaceName, members,
            IsFileScopedNamespace(classNode));

        // Destination file path
        var sourceDir = Path.GetDirectoryName(sourceDoc.FilePath)!;
        var destDir = targetDirectory is not null ? Path.GetFullPath(targetDirectory) : sourceDir;
        var destFile = Path.Combine(destDir, interfaceName + ".cs");

        var extractedMembers = members.Select(m => m.ToDisplayString()).ToList();

        if (dryRun)
        {
            return new
            {
                dryRun = true,
                interfaceName,
                className,
                destinationFile = destFile,
                membersExtracted = extractedMembers,
                interfaceSource,
            };
        }

        if (File.Exists(destFile))
            return new { error = $"Destination file already exists: {destFile}" };

        // Write interface file
        Directory.CreateDirectory(destDir);
        await File.WriteAllTextAsync(destFile, interfaceSource, System.Text.Encoding.UTF8);

        // Optionally patch class to implement the interface
        string? updatedClassFile = null;
        if (addToClass && classNode is not null)
        {
            var ifaceType = SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(interfaceName));
            TypeDeclarationSyntax newClassNode;
            if (classNode.BaseList is null)
            {
                newClassNode = classNode.WithBaseList(
                    SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(ifaceType)));
            }
            else
            {
                newClassNode = classNode.WithBaseList(
                    classNode.BaseList.AddTypes(ifaceType));
            }

            var newRoot = root.ReplaceNode(classNode, newClassNode);
            await File.WriteAllTextAsync(sourceDoc.FilePath, newRoot.ToFullString(), System.Text.Encoding.UTF8);
            updatedClassFile = sourceDoc.FilePath;
        }

        return new
        {
            dryRun = false,
            interfaceName,
            className,
            destinationFile = destFile,
            updatedClassFile,
            membersExtracted = extractedMembers,
            message = $"Interface '{interfaceName}' written to {destFile}.",
        };
    }

    private static string BuildInterfaceSource(
        SyntaxList<UsingDirectiveSyntax> usings,
        string? namespaceName,
        string interfaceName,
        List<ISymbol> members,
        bool fileScopedNamespace)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var u in usings)
            sb.AppendLine(u.ToFullString().TrimEnd());
        if (usings.Count > 0)
            sb.AppendLine();

        var indent = namespaceName is not null && !fileScopedNamespace ? "    " : "";

        if (namespaceName is not null)
        {
            if (fileScopedNamespace)
            {
                sb.AppendLine($"namespace {namespaceName};");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"namespace {namespaceName}");
                sb.AppendLine("{");
            }
        }

        sb.AppendLine($"{indent}public interface {interfaceName}");
        sb.AppendLine($"{indent}{{");

        foreach (var member in members)
        {
            switch (member)
            {
                case IPropertySymbol prop:
                    var getSet = (prop.GetMethod is not null, prop.SetMethod is not null) switch
                    {
                        (true, true)  => "{ get; set; }",
                        (true, false) => "{ get; }",
                        (false, true) => "{ set; }",
                        _             => "{ }",
                    };
                    sb.AppendLine($"{indent}    {FormatType(prop.Type)} {prop.Name} {getSet}");
                    break;

                case IMethodSymbol meth:
                    var typeParams = meth.TypeParameters.Length > 0
                        ? $"<{string.Join(", ", meth.TypeParameters.Select(t => t.Name))}>"
                        : "";
                    var methodParams = string.Join(", ", meth.Parameters.Select(p =>
                        $"{FormatType(p.Type)} {p.Name}"));
                    sb.AppendLine($"{indent}    {FormatType(meth.ReturnType)} {meth.Name}{typeParams}({methodParams});");
                    break;

                case IEventSymbol evt:
                    sb.AppendLine($"{indent}    event {FormatType(evt.Type)} {evt.Name};");
                    break;
            }
        }

        sb.AppendLine($"{indent}}}");

        if (namespaceName is not null && !fileScopedNamespace)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static string FormatType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static bool IsFileScopedNamespace(TypeDeclarationSyntax? typeNode)
    {
        if (typeNode is null) return false;
        return typeNode.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().Any();
    }
}
