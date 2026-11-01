using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Generation;

/// <summary>The answer of <c>generate_comparer</c>.</summary>
public sealed record ComparerResult
{
    /// <summary>The comparer that was generated.</summary>
    public string? Generated { get; init; }

    /// <summary>The type it orders.</summary>
    public string? Ordered { get; init; }

    /// <summary>The properties it compares, in order.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>Where the file was written.</summary>
    public string? File { get; init; }

    /// <summary>Whether the call only reported what it would write.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Whether the file was written.</summary>
    public bool? Applied { get; init; }

    /// <summary>The generated source (the preview on a dry run).</summary>
    public string? Preview { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// An <c>IComparer&lt;T&gt;</c> for a type, generated from the members the type already has: every public
/// instance property whose type is a string, a primitive, an enum, a Guid, a date or a comparable struct,
/// compared in declaration order, with a <c>Default</c> singleton and null handling. Compiler-gated like
/// the other generators.
/// </summary>
public interface IComparerGeneratorService
{
    Task<ComparerResult> GenerateAsync(
        string typeName,
        string? projectName,
        string? directory,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class ComparerGeneratorService : IComparerGeneratorService
{
    private readonly IRoslynWorkspace _workspace;

    public ComparerGeneratorService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<ComparerResult> GenerateAsync(
        string typeName,
        string? projectName,
        string? directory,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return new ComparerResult { Error = "A type name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new ComparerResult { Error = "No C# solution could be auto-located from the working directory." };

        var type = await FindTypeAsync(solution, typeName, projectName, cancellationToken);
        if (type is null) return new ComparerResult { Error = $"Type '{typeName}' not found." };
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return new ComparerResult { Error = $"'{type.ToDisplayString()}' is a {type.TypeKind}: a comparer needs a class or a struct." };
        if (type.IsAbstract && type.TypeKind == TypeKind.Class && !type.IsStatic)
            return new ComparerResult { Error = $"'{type.ToDisplayString()}' is abstract: comparisons would have no instance shape." };

        var keys = type.GetMembers().OfType<IPropertySymbol>()
            .Where(property => property.DeclaredAccessibility == Accessibility.Public)
            .Where(property => !property.IsStatic && !property.IsIndexer)
            .Where(property => property.GetMethod is not null)
            .Where(property => IsComparable(property.Type))
            .ToList();

        if (keys.Count == 0)
        {
            return new ComparerResult
            {
                Error = $"'{type.Name}' has no public comparable property (string, number, enum, Guid, date, or a type implementing IComparable<T>).",
            };
        }

        var assemblyName = type.ContainingAssembly?.Name;
        var project = solution.Projects.FirstOrDefault(candidate =>
            candidate.AssemblyName is not null &&
            string.Equals(candidate.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));

        if (project?.FilePath is null)
            return new ComparerResult { Error = $"The project that declares '{type.Name}' has no file on disk." };

        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(project.FilePath)!
            : Path.GetFullPath(directory);

        var comparerName = type.Name + "Comparer";
        var namespaceName = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : project.DefaultNamespace ?? "Generated";

        var source = Render(type, comparerName, namespaceName, keys);
        var filePath = Path.Combine(targetDirectory, comparerName + ".cs");
        if (File.Exists(filePath)) return new ComparerResult { Error = $"{filePath} already exists." };

        var withFile = solution.AddDocument(
            DocumentId.CreateNewId(project.Id), comparerName + ".cs", SourceText.From(source), filePath: filePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, withFile, [project.Id], cancellationToken);
        if (!gate.Passed)
        {
            return new ComparerResult
            {
                Error = $"Refused: generating {comparerName} would add {gate.Added} compiler error(s) " +
                        $"({gate.Before} → {gate.After}).",
                Note = string.Join("; ", gate.NewErrors.Take(5)),
            };
        }

        if (!dryRun)
        {
            await File.WriteAllTextAsync(filePath, source, cancellationToken);
            _workspace.TryAddDocument(project.Id, comparerName + ".cs", filePath, SourceText.From(source));
        }

        return new ComparerResult
        {
            Generated = comparerName,
            Ordered = type.ToDisplayString(),
            Keys = keys.Select(property => property.Name).ToList(),
            File = filePath,
            DryRun = dryRun,
            Applied = !dryRun,
            Preview = source,
            Note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "File written and added to the Roslyn model. The order is the properties' declaration order — edit it if it should be the business key.",
        };
    }

    private static string Render(INamedTypeSymbol type, string comparerName, string namespaceName,
        IReadOnlyList<IPropertySymbol> keys)
    {
        var typeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var builder = new StringBuilder();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        builder.AppendLine($"{accessibility} sealed class {comparerName} : System.Collections.Generic.IComparer<{typeName}>");
        builder.AppendLine("{");
        builder.AppendLine($"    public static readonly {comparerName} Default = new();");
        builder.AppendLine();
        builder.AppendLine($"    public int Compare({typeName}? x, {typeName}? y)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (ReferenceEquals(x, y)) return 0;");
        builder.AppendLine("        if (x is null) return -1;");
        builder.AppendLine("        if (y is null) return 1;");
        builder.AppendLine();

        foreach (var key in keys)
        {
            builder.AppendLine($"        var by{key.Name} = System.Collections.Generic.Comparer<{key.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>.Default.Compare(x.{key.Name}, y.{key.Name});");
            builder.AppendLine($"        if (by{key.Name} != 0) return by{key.Name};");
        }

        builder.AppendLine("        return 0;");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static bool IsComparable(ITypeSymbol type)
    {
        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Boolean
            or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
            or SpecialType.System_Byte or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Decimal or SpecialType.System_Char)
        {
            return true;
        }

        if (type.TypeKind == TypeKind.Enum) return true;
        if (type.ToDisplayString() is "System.Guid" or "System.DateTime" or "System.DateTimeOffset"
            or "System.TimeSpan" or "System.DateOnly" or "System.TimeOnly") return true;

        return type.AllInterfaces.Any(@interface =>
            @interface.Name == "IComparable" && @interface.TypeArguments.Length == 1);
    }

    private static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string typeName,
        string? projectName, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            var direct = compilation.GetTypeByMetadataName(typeName);
            if (direct is not null) return direct;

            var symbols = compilation.GetSymbolsWithName(typeName.Split('.').Last(), SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .ToList();

            if (symbols.Count == 1) return symbols[0];
            var exact = symbols.FirstOrDefault(symbol => symbol.ToDisplayString() == typeName);
            if (exact is not null) return exact;
        }

        return null;
    }
}
