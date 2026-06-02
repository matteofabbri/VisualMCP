using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ImplementInpcTool
{
    [McpServerTool, Description(
        "Generate INotifyPropertyChanged boilerplate for a class: add the interface to the base list, " +
        "add the PropertyChanged event, add a SetField<T> helper, and convert auto-properties to " +
        "backing-field properties that call SetField. Equivalent to ReSharper's INPC generation. " +
        "Requires LoadSolution first.")]
    public static async Task<object> ImplementInpc(
        [Description("Name of the class to modify")] string className,
        [Description("Names of properties to convert to notifying properties (default: all public auto-properties)")] string[]? propertyNames = null,
        [Description("Dry run â€” return generated code without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(className, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var classSymbol = candidates
            .OfType<INamedTypeSymbol>()
            .Where(s => s.TypeKind == TypeKind.Class)
            .FirstOrDefault();

        if (classSymbol is null)
            return new { error = $"Class '{className}' not found." };

        // Find source document
        var declLoc = classSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLoc is null) return new { error = "Class has no source location." };

        var tree = declLoc.SourceTree!;
        var sourceDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);
        if (sourceDoc?.FilePath is null) return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null) return new { error = "Could not parse source file." };

        var classNode = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.Text.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (classNode is null) return new { error = "Could not locate class node." };

        // Check if INPC already implemented
        bool alreadyHasInpc = classSymbol.AllInterfaces
            .Any(i => i.Name == "INotifyPropertyChanged");

        // Collect auto-properties to convert
        var autoProps = classNode.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p =>
            {
                if (propertyNames is not null &&
                    !propertyNames.Contains(p.Identifier.Text, StringComparer.OrdinalIgnoreCase))
                    return false;
                // Auto-property: has get; and set; accessors with no body
                return p.AccessorList?.Accessors.All(a =>
                    a.Body is null && a.ExpressionBody is null) == true;
            })
            .ToList();

        // Build the new class members
        var newClassNode = classNode;

        // 1. Add INotifyPropertyChanged to base list
        if (!alreadyHasInpc)
        {
            var inpcType = SyntaxFactory.SimpleBaseType(
                SyntaxFactory.IdentifierName("INotifyPropertyChanged"));

            newClassNode = newClassNode.BaseList is null
                ? newClassNode.WithBaseList(
                    SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(inpcType)))
                : newClassNode.WithBaseList(
                    newClassNode.BaseList.AddTypes(inpcType));
        }

        // 2. Build boilerplate members to prepend (event + SetField helper)
        var boilerplate = BuildBoilerplate(alreadyHasInpc);
        var boilerplateMembers = ParseMembers(boilerplate);

        // 3. Convert auto-properties to backing-field properties
        var convertedNames = new List<string>();
        foreach (var prop in autoProps)
        {
            var propName = prop.Identifier.Text;
            var propType = prop.Type.ToString();
            var fieldName = "_" + char.ToLower(propName[0]) + propName[1..];

            var newPropText =
                $"private {propType} {fieldName};\n" +
                $"public {propType} {propName}\n" +
                $"{{\n" +
                $"    get => {fieldName};\n" +
                $"    set => SetField(ref {fieldName}, value);\n" +
                $"}}";

            var replacementMembers = ParseMembers(newPropText);

            // Remove old property, insert new backing field + property in its place
            var idx = newClassNode.Members.IndexOf(
                newClassNode.Members.OfType<PropertyDeclarationSyntax>()
                    .First(p => p.Identifier.Text == propName));

            var updatedMembers = newClassNode.Members.RemoveAt(idx);
            for (int i = replacementMembers.Count - 1; i >= 0; i--)
                updatedMembers = updatedMembers.Insert(idx, replacementMembers[i]);

            newClassNode = newClassNode.WithMembers(updatedMembers);
            convertedNames.Add(propName);
        }

        // Prepend boilerplate members after existing fields (before other members)
        var insertAt = FindBoilerplateInsertIndex(newClassNode);
        var allMembers = newClassNode.Members;
        for (int i = boilerplateMembers.Count - 1; i >= 0; i--)
            allMembers = allMembers.Insert(insertAt, boilerplateMembers[i]);
        newClassNode = newClassNode.WithMembers(allMembers);

        // Ensure using System.ComponentModel is present
        var newRoot = root.ReplaceNode(classNode, newClassNode);
        if (!root.Usings.Any(u => u.Name?.ToString() == "System.ComponentModel"))
        {
            var usingDir = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("System.ComponentModel"))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newRoot = newRoot.AddUsings(usingDir);
        }

        var newSource = newRoot.ToFullString();

        if (dryRun)
            return new
            {
                dryRun = true,
                className,
                alreadyHadInpc = alreadyHasInpc,
                propertiesConverted = convertedNames,
                preview = newSource,
            };

        await File.WriteAllTextAsync(sourceDoc.FilePath, newSource, System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            className,
            alreadyHadInpc = alreadyHasInpc,
            propertiesConverted = convertedNames,
            file = sourceDoc.FilePath,
        };
    }

    private static string BuildBoilerplate(bool skipEvent) =>
        (skipEvent ? "" :
            "public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;\n\n") +
        "protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)\n" +
        "    => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));\n\n" +
        "protected bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)\n" +
        "{\n" +
        "    if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;\n" +
        "    field = value;\n" +
        "    OnPropertyChanged(propertyName);\n" +
        "    return true;\n" +
        "}";

    private static SyntaxList<MemberDeclarationSyntax> ParseMembers(string code)
    {
        var wrapped = $"class __T__ {{\n{code}\n}}";
        var tmpRoot = CSharpSyntaxTree.ParseText(wrapped).GetCompilationUnitRoot();
        var tmpClass = tmpRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        return tmpClass.Members;
    }

    private static int FindBoilerplateInsertIndex(TypeDeclarationSyntax classNode)
    {
        // Insert after the last field, before the first constructor or method
        var members = classNode.Members;
        int lastField = -1;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is FieldDeclarationSyntax)
                lastField = i;
        }
        return lastField + 1;
    }
}
