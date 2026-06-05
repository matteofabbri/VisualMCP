using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class EncapsulateFieldTool
{
    [McpServerTool, Description(
        "When you want to wrap a field in a property, use this INSTEAD OF manual edits: it makes the field private, generates a public get/set property, " +
        "and rewrites every reference across the solution to use the property (ReSharper 'Encapsulate Field'). " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> EncapsulateField(
        [Description("Name of the field to encapsulate")] string fieldName,
        [Description("Containing type name to disambiguate (required if multiple types have a field with this name)")] string? containingType = null,
        [Description("Name for the generated property (default: PascalCase version of the field name)")] string? propertyName = null,
        [Description("Dry run â€” show changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // Find the field symbol
        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(fieldName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Member);

        var fields = candidates
            .OfType<IFieldSymbol>()
            .Where(f => containingType is null ||
                        f.ContainingType.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fields.Count == 0)
            return new { error = $"Field '{fieldName}' not found." };
        if (fields.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {fields.Count} fields named '{fieldName}'. Specify containingType.",
                candidates = fields.Select(f => f.ToDisplayString()).ToList(),
            };

        var field = fields[0];

        if (field.IsConst)
            return new { error = "Cannot encapsulate a const field." };
        if (field.IsReadOnly && field.DeclaredAccessibility != Accessibility.Public)
            return new { error = "Field is readonly â€” encapsulate would remove the ability to set it. Rename it to a property manually." };

        // Determine property name
        propertyName ??= ToPascalCase(fieldName);
        if (propertyName.Equals(fieldName, StringComparison.Ordinal))
            propertyName = "_" + fieldName is ['_', ..] ? ToPascalCase(fieldName.TrimStart('_')) : "Property" + fieldName;

        // Find the source document
        var declLocation = field.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation is null)
            return new { error = "Field has no source location." };

        var tree = declLocation.SourceTree;
        var sourceDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);

        if (sourceDoc?.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null)
            return new { error = "Could not parse source file." };

        // Find the field declaration node
        var fieldDecl = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f =>
            {
                var parent = f.Parent as TypeDeclarationSyntax;
                return parent?.Identifier.Text.Equals(field.ContainingType.Name, StringComparison.Ordinal) == true &&
                       f.Declaration.Variables.Any(v => v.Identifier.Text.Equals(fieldName, StringComparison.Ordinal));
            });

        if (fieldDecl is null)
            return new { error = "Could not locate field declaration node." };

        // Build the replacement: private field + public property
        var fieldType = fieldDecl.Declaration.Type.ToString();
        var privateName = fieldName.StartsWith('_') ? fieldName : "_" + char.ToLower(fieldName[0]) + fieldName[1..];

        // If field name didn't start with _, rename it to _camelCase
        var needsFieldRename = !fieldName.StartsWith('_');
        var newFieldName = needsFieldRename ? privateName : fieldName;

        var newFieldSource = $"private {fieldType} {newFieldName};";
        var newPropertySource =
            $"public {fieldType} {propertyName}\n" +
            $"{{\n" +
            $"    get => {newFieldName};\n" +
            $"    set => {newFieldName} = value;\n" +
            $"}}";

        // Parse new nodes
        var tmpTree = CSharpSyntaxTree.ParseText($"class T {{ {newFieldSource}\n{newPropertySource} }}");
        var tmpRoot = tmpTree.GetCompilationUnitRoot();
        var tmpMembers = tmpRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
        var newFieldNode = tmpMembers[0].WithLeadingTrivia(fieldDecl.GetLeadingTrivia());
        var newPropNode = tmpMembers[1].WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

        // Replace field declaration with private field + property
        var typeNode = (TypeDeclarationSyntax)fieldDecl.Parent!;
        var fieldIndex = typeNode.Members.IndexOf(fieldDecl);
        var newMembers = typeNode.Members
            .RemoveAt(fieldIndex)
            .Insert(fieldIndex, newPropNode)
            .Insert(fieldIndex, newFieldNode);

        var newTypeNode = typeNode.WithMembers(newMembers);
        var newRoot = root.ReplaceNode(typeNode, newTypeNode);

        // Find all references to the field and rename them to the property name
        var refs = await SymbolFinder.FindReferencesAsync(field, solution);
        var refsByDoc = refs
            .SelectMany(r => r.Locations)
            .GroupBy(l => l.Document.FilePath)
            .ToList();

        var filesToUpdate = new Dictionary<string, string>
        {
            [sourceDoc.FilePath] = newRoot.ToFullString()
        };

        // Rename field references to property name in all documents
        foreach (var group in refsByDoc)
        {
            var docPath = group.Key;
            if (docPath is null) continue;

            string currentText;
            if (filesToUpdate.TryGetValue(docPath, out var alreadyUpdated))
                currentText = alreadyUpdated;
            else
            {
                var refDoc = solution.Projects.SelectMany(p => p.Documents)
                    .FirstOrDefault(d => d.FilePath == docPath);
                if (refDoc is null) continue;
                currentText = (await refDoc.GetTextAsync()).ToString();
            }

            // Replace occurrences from the end to avoid offset drift
            var locations = group.OrderByDescending(l => l.Location.SourceSpan.Start).ToList();
            foreach (var loc in locations)
            {
                var span = loc.Location.SourceSpan;
                var refTree = loc.Location.SourceTree;
                if (refTree is null) continue;

                // Get the text of that specific file (we may have already updated it)
                string fileText = currentText;
                var slice = fileText.Substring(span.Start, span.Length);
                if (slice.Equals(fieldName, StringComparison.Ordinal) ||
                    (needsFieldRename && slice.Equals(newFieldName, StringComparison.Ordinal)))
                {
                    currentText = fileText[..span.Start] + propertyName + fileText[(span.Start + span.Length)..];
                }
            }

            filesToUpdate[docPath] = currentText;
        }

        var summary = new
        {
            dryRun,
            fieldName,
            newFieldName,
            propertyName,
            containingType = field.ContainingType.Name,
            filesAffected = filesToUpdate.Count,
            files = filesToUpdate.Keys.ToList(),
        };

        if (dryRun)
            return summary;

        foreach (var (path, text) in filesToUpdate)
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8);

        return summary;
    }

    private static string ToPascalCase(string name)
    {
        var trimmed = name.TrimStart('_');
        if (trimmed.Length == 0) return name;
        return char.ToUpper(trimmed[0]) + trimmed[1..];
    }
}
