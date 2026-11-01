using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Generation;

/// <summary>
/// Generate a fluent builder for a type (the "builder generator" action): one <c>With</c> method per
/// settable public property and a <c>Build()</c> that returns the value. Accepted only when the type
/// has a public parameterless constructor and at least one settable property; compiler-gated.
/// </summary>
public interface IBuilderGeneratorService
{
    Task<object> GenerateAsync(string typeName, string? projectName, string? builderName, bool dryRun);
}

public sealed class BuilderGeneratorService : IBuilderGeneratorService
{
    private readonly IRoslynWorkspace _workspace;
    public BuilderGeneratorService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GenerateAsync(string typeName, string? projectName = null, string? builderName = null,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "A type name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        INamedTypeSymbol? type = null;
        Project? project = null;

        foreach (var candidate in solution.Projects)
        {
            if (projectName is not null && !candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)) continue;

            var compilation = await candidate.GetCompilationAsync();
            var found = compilation?.GetTypeByMetadataName(typeName)
                        ?? compilation?.GlobalNamespace
                            .DescendantTypesAndSelf()
                            .FirstOrDefault(symbol => symbol.Name == typeName);

            if (found is null) continue;
            type = found;
            project = candidate;
            break;
        }

        if (type is null || project is null)
            return new { error = $"Type '{typeName}' not found{(projectName is null ? "" : $" in '{projectName}'")}." };

        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return new { error = $"'{type.Name}' is a {type.TypeKind}: a builder is generated for classes and structs." };

        if (type.IsAbstract) return new { error = $"'{type.Name}' is abstract: a builder cannot construct it." };

        var constructor = type.InstanceConstructors.FirstOrDefault(candidate =>
            candidate.Parameters.Length == 0 && candidate.DeclaredAccessibility == Accessibility.Public);
        if (constructor is null)
            return new { error = $"'{type.Name}' has no public parameterless constructor." };

        var settable = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic &&
                               property.SetMethod is { DeclaredAccessibility: Accessibility.Public } &&
                               property.Type.TypeKind != TypeKind.Error)
            .ToList();

        if (settable.Count == 0)
            return new { error = $"'{type.Name}' has no public settable property: there is nothing for the builder to set." };

        var name = string.IsNullOrWhiteSpace(builderName) ? type.Name + "Builder" : builderName!;
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
            return new { error = $"'{name}' is not a valid class name." };

        if (type.ContainingType is not null)
            return new { error = "The type is nested: a builder for a nested type is not generated yet." };

        var namespaceName = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : project.DefaultNamespace ?? "Generated";

        var source = Render(type, name, namespaceName, settable);        var directory = Path.GetDirectoryName(project.FilePath!)!;
        var filePath = Path.Combine(directory, name + ".cs");

        if (File.Exists(filePath)) return new { error = $"{filePath} already exists." };

        var withFile = solution.AddDocument(
            DocumentId.CreateNewId(project.Id), name + ".cs", SourceText.From(source), filePath: filePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, withFile, [project.Id]);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"generating {name}");

        if (!dryRun)
        {
            await File.WriteAllTextAsync(filePath, source);
            _workspace.TryAddDocument(project.Id, name + ".cs", filePath, SourceText.From(source));
        }

        return new
        {
            @generated = name,
            forType = type.ToDisplayString(),
            properties = settable.Count,
            file = filePath,
            @namespace = namespaceName,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = source,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "File written and added to the Roslyn model.",
        };
    }

    private static string Render(INamedTypeSymbol type, string builderName, string namespaceName,
        List<IPropertySymbol> properties)
    {
        var typeName = type.Name;

        // The builder must not be more accessible than the type it returns (CS0050 otherwise).
        var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        var builder = new StringBuilder();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        builder.AppendLine($"{accessibility} sealed class {builderName}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly {typeName} _value = new();");
        builder.AppendLine();

        foreach (var property in properties)
        {
            var propertyType = property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            builder.AppendLine($"    public {builderName} With{property.Name}({propertyType} value)");
            builder.AppendLine("    {");
            builder.AppendLine($"        _value.{property.Name} = value;");
            builder.AppendLine("        return this;");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine($"    public {typeName} Build() => _value;");
        builder.AppendLine("}");
        return builder.ToString();
    }
}

/// <summary>Namespace traversal, shared by the generators in this folder.</summary>
internal static class SymbolTraversal
{
    public static IEnumerable<INamedTypeSymbol> DescendantTypesAndSelf(this INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    foreach (var nested in child.DescendantTypesAndSelf()) yield return nested;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers()) yield return nested;
                    break;
            }
        }
    }
}
