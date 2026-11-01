using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Generation;

/// <summary>
/// Generate a C# DTO from a JSON sample (the "paste the payload, get the class" action): every
/// property is inferred from the data — integers become long, decimals double, arrays List&lt;T&gt;,
/// nested objects a nested record — and the result is added to the project behind the compiler gate.
/// </summary>
public interface IDtoFromJsonService
{
    Task<object> GenerateAsync(string json, string typeName, string? projectName, string? directory,
        string? namespaceName, bool dryRun);
}

public sealed class DtoFromJsonService : IDtoFromJsonService
{
    private readonly IRoslynWorkspace _workspace;
    public DtoFromJsonService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GenerateAsync(string json, string typeName, string? projectName = null,
        string? directory = null, string? namespaceName = null, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return new { error = "A JSON sample is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "A type name is required." };
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(typeName))
            return new { error = $"'{typeName}' is not a valid C# type name." };

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { return new { error = $"The JSON does not parse: {ex.Message}" }; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new { error = "The JSON root must be an object: an array has no shape to name." };

            var solution = await _workspace.EnsureSolutionLoadedAsync();
            if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

            var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();
            Project? project;
            if (projectName is not null)
            {
                project = projects.FirstOrDefault(candidate =>
                    candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                if (project is null) return new { error = $"Project '{projectName}' not found." };
            }
            else
            {
                if (projects.Count != 1)
                    return new { error = "Several projects; pass projectName.", candidates = projects.Select(p => p.Name).ToList() };
                project = projects[0];
            }

            var targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Path.GetDirectoryName(project.FilePath!)!
                : Path.GetFullPath(directory);

            var namespaceForType = string.IsNullOrWhiteSpace(namespaceName)
                ? project.DefaultNamespace ?? project.AssemblyName ?? "Generated"
                : namespaceName!;

            var shapes = new List<DtoShape>();
            var root = BuildShape(typeName, document.RootElement, shapes, 0);

            var source = Render(root, namespaceForType, shapes);
            var fileName = typeName + ".cs";
            var filePath = Path.Combine(targetDirectory, fileName);

            if (File.Exists(filePath)) return new { error = $"{filePath} already exists." };
            if (shapes.Count > 20) return new { error = $"The sample nests {shapes.Count} types; simplify it first." };

            var withFile = solution.AddDocument(
                DocumentId.CreateNewId(project.Id), fileName, SourceText.From(source), filePath: filePath);

            var gate = await RefactoringCompilerGate.CompareAsync(solution, withFile, [project.Id]);
            if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"generating {typeName}");

            if (!dryRun)
            {
                await File.WriteAllTextAsync(filePath, source);
                _workspace.TryAddDocument(project.Id, fileName, filePath, SourceText.From(source));
            }

            return new
            {
                @generated = typeName,
                types = shapes.Count + 1,
                properties = CountProperties(root),
                file = filePath,
                @namespace = namespaceForType,
                dryRun,
                applied = !dryRun,
                compilerErrors = new { before = gate.Before, after = gate.After },
                preview = source,
                note = dryRun
                    ? "Preview only: pass dryRun=false to write the file."
                    : "File written and added to the Roslyn model. Nullability: a property seen as null gets '?'.",
            };
        }
    }

    // ── shape inference ─────────────────────────────────────────────────────
    private sealed class DtoShape
    {
        public DtoShape(string name, bool nullable) { Name = name; Nullable = nullable; }
        public string Name { get; }
        public bool Nullable { get; set; }
        public List<DtoProperty> Properties { get; } = [];
    }

    private sealed record DtoProperty(string Name, string Type, bool Nullable, string Initializer);

    private static DtoShape BuildShape(string name, JsonElement element, List<DtoShape> collected, int depth)
    {
        if (depth > 6) throw new InvalidOperationException("The JSON nests deeper than 6 levels.");

        var shape = new DtoShape(name, nullable: false);

        foreach (var property in element.EnumerateObject())
        {
            var propertyName = ToPascal(property.Name);
            var (type, nullable, initializer) = Infer(propertyName, property.Value, collected, depth);
            shape.Properties.Add(new DtoProperty(propertyName, type, nullable, initializer));
        }

        collected.Add(shape);
        return shape;
    }

    private static (string Type, bool Nullable, string Initializer) Infer(string name, JsonElement value,
        List<DtoShape> collected, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ("string", false, "string.Empty");

            case JsonValueKind.Number:
                return value.TryGetInt64(out _) ? ("long", false, "0") : ("double", false, "0");

            case JsonValueKind.True:
            case JsonValueKind.False:
                return ("bool", false, "false");

            case JsonValueKind.Null:
                return ("object?", true, "null");

            case JsonValueKind.Object:
            {
                var nested = BuildShape(name + "Dto", value, collected, depth + 1);
                return (nested.Name, false, $"new {nested.Name}()");
            }

            case JsonValueKind.Array:
            {
                var first = value.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Undefined)
                    return ("List<object>", false, "[]");

                var (elementType, _, _) = Infer(name + "Item", first, collected, depth + 1);
                return ($"List<{elementType}>", false, "[]");
            }

            default:
                return ("object?", true, "null");
        }
    }

    private static string Render(DtoShape root, string namespaceName, List<DtoShape> shapes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();

        // The root first, the nested ones after it (each is a record of its own).
        foreach (var shape in new[] { root }.Concat(shapes.Where(candidate => candidate != root)))
        {
            builder.AppendLine($"public sealed record {shape.Name}");
            builder.AppendLine("{");

            if (shape.Properties.Count == 0)
            {
                builder.AppendLine("    // The JSON object was empty.");
            }

            foreach (var property in shape.Properties)
            {
                var type = property.Nullable ? property.Type : property.Type;
                builder.AppendLine($"    public {type} {property.Name} {{ get; init; }} = {property.Initializer};");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static int CountProperties(DtoShape shape) => shape.Properties.Count;

    private static string ToPascal(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Value";

        var cleaned = new string(name.Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray());
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();

        foreach (var word in words)
        {
            var first = char.ToUpperInvariant(word[0]);
            builder.Append(first).Append(word.Length > 1 ? word[1..] : string.Empty);
        }

        var result = builder.ToString();
        if (result.Length == 0) return "Value";
        if (char.IsDigit(result[0])) result = "Value" + result;
        return result;
    }
}
