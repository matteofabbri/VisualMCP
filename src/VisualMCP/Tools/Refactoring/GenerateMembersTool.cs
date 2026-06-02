using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class GenerateMembersTool
{
    [McpServerTool, Description("Generate boilerplate members for a class: constructor (from fields/properties), Equals/GetHashCode (value equality), and/or ToString. Inserts the generated code into the class and writes to disk. Requires LoadSolution first.")]
    public static async Task<object> GenerateMembers(
        [Description("Name of the class to generate members for")] string className,
        [Description("Generate a constructor that assigns all eligible fields/properties (default: true)")] bool generateConstructor = true,
        [Description("Generate Equals and GetHashCode based on all eligible fields/properties (default: true)")] bool generateEquality = true,
        [Description("Generate a ToString returning a readable summary of all eligible fields/properties (default: true)")] bool generateToString = true,
        [Description("Dry run â€” return generated code without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

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

        // Collect eligible members: non-static, non-implicit fields and auto-properties
        var fields = classSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsImplicitlyDeclared && !f.IsConst)
            .ToList();

        var autoProps = classSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsImplicitlyDeclared && !p.IsIndexer &&
                        p.SetMethod is not null &&
                        p.Locations.Any(l => l.IsInSource))
            .ToList();

        // Prefer auto-properties over backing fields; if only fields, use those
        IReadOnlyList<(string Name, ITypeSymbol Type)> eligible = autoProps.Count > 0
            ? autoProps.Select(p => (p.Name, p.Type)).ToList()
            : fields.Select(f => (f.Name, f.Type)).ToList();

        if (eligible.Count == 0)
            return new { error = "No eligible fields or auto-properties found to generate members from." };

        var generated = new List<string>();
        var generatedCode = new System.Text.StringBuilder();

        if (generateConstructor)
        {
            var ctor = BuildConstructor(className, eligible);
            generatedCode.AppendLine(ctor);
            generated.Add("constructor");
        }

        if (generateEquality)
        {
            var eq = BuildEquality(className, eligible, classSymbol.TypeKind == TypeKind.Struct);
            generatedCode.AppendLine(eq);
            generated.Add("Equals/GetHashCode/==");
        }

        if (generateToString)
        {
            var ts = BuildToString(className, eligible);
            generatedCode.AppendLine(ts);
            generated.Add("ToString");
        }

        var newCode = generatedCode.ToString().TrimEnd();

        if (dryRun)
            return new { dryRun = true, className, generated, code = newCode };

        // Locate the class declaration and insert members before the closing brace
        var declLocation = classSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation is null)
            return new { error = "Class has no source location." };

        var tree = declLocation.SourceTree;
        var sourceDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);

        if (sourceDoc?.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null)
            return new { error = "Could not parse source file." };

        var classNode = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.Text.Equals(className, StringComparison.OrdinalIgnoreCase));

        if (classNode is null)
            return new { error = "Could not locate class declaration node." };

        // Parse the generated code as members and append to the class
        var wrappedSource = $"class __Tmp__ {{\n{newCode}\n}}";
        var tmpTree = CSharpSyntaxTree.ParseText(wrappedSource);
        var tmpRoot = tmpTree.GetCompilationUnitRoot();
        var tmpClass = tmpRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var newMembers = tmpClass.Members;

        // Add a blank line before each generated member for readability
        var membersWithTrivia = newMembers.Select(m =>
            m.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed));

        var updatedClass = classNode.AddMembers(membersWithTrivia.ToArray());
        var newRoot = root.ReplaceNode(classNode, updatedClass);

        await File.WriteAllTextAsync(sourceDoc.FilePath, newRoot.ToFullString(), System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            className,
            generated,
            file = sourceDoc.FilePath,
            message = $"Generated {string.Join(", ", generated)} for '{className}'.",
        };
    }

    private static string BuildConstructor(string className, IReadOnlyList<(string Name, ITypeSymbol Type)> members)
    {
        var parameters  = string.Join(", ", members.Select(m => $"{FormatType(m.Type)} {ToCamelCase(m.Name)}"));
        var assignments = string.Join("\n        ", members.Select(m => $"this.{m.Name} = {ToCamelCase(m.Name)};"));
        return
            $"    public {className}({parameters})\n" +
            $"    {{\n" +
            $"        {assignments}\n" +
            $"    }}";
    }

    private static string BuildEquality(string className, IReadOnlyList<(string Name, ITypeSymbol Type)> members, bool isStruct)
    {
        var comparisons = string.Join(" &&\n               ", members.Select(m => $"{m.Name} == other.{m.Name}"));
        var hashArgs    = string.Join(", ", members.Take(8).Select(m => m.Name));
        var hashLine    = members.Count == 1 ? $"HashCode.Combine({members[0].Name})" : $"HashCode.Combine({hashArgs})";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"    public override bool Equals(object? obj)");
        sb.AppendLine($"    {{");
        if (isStruct)
            sb.AppendLine($"        return obj is {className} other && Equals(other);");
        else
        {
            sb.AppendLine($"        if (obj is not {className} other) return false;");
            sb.AppendLine($"        return {comparisons};");
        }
        sb.AppendLine($"    }}");
        sb.AppendLine();

        if (isStruct)
        {
            sb.AppendLine($"    public bool Equals({className} other)");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        return {comparisons};");
            sb.AppendLine($"    }}");
            sb.AppendLine();
        }

        sb.AppendLine($"    public override int GetHashCode() => {hashLine};");
        sb.AppendLine();
        sb.AppendLine($"    public static bool operator ==({className}? left, {className}? right) => Equals(left, right);");
        sb.Append(    $"    public static bool operator !=({className}? left, {className}? right) => !Equals(left, right);");
        return sb.ToString();
    }

    private static string BuildToString(string className, IReadOnlyList<(string Name, ITypeSymbol Type)> members)
    {
        // Each member appears as "Name: {Name}" in the interpolated string
        var parts = string.Join(", ", members.Select(m => m.Name + ": {" + m.Name + "}"));
        return $"    public override string ToString() => $\"{className} {{ {parts} }}\";";
    }

    private static string FormatType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string ToCamelCase(string name) =>
        name.Length == 0 ? name :
        name[0] == '_' ? name.TrimStart('_') is { Length: > 0 } s ? char.ToLower(s[0]) + s[1..] : name :
        char.ToLower(name[0]) + name[1..];
}
