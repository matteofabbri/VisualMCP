using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Generation;

/// <summary>The answer of <c>generate_dto_from_xml</c>.</summary>
public sealed record XmlDtoResult
{
    /// <summary>The type that was generated.</summary>
    public string? Generated { get; init; }

    /// <summary>How many types the sample produced (the root plus its nested shapes).</summary>
    public int? Types { get; init; }

    /// <summary>How many properties all of them carry.</summary>
    public int? Properties { get; init; }

    /// <summary>Where the file was written.</summary>
    public string? File { get; init; }

    /// <summary>The namespace the type was put in.</summary>
    public string? Namespace { get; init; }

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
/// The DTO counterpart of <c>generate_dto_from_json</c>: an XML sample becomes a C# type. Attributes and
/// child elements become properties, an element that repeats becomes a <c>List&lt;T&gt;</c>, an element with
/// children becomes a nested type, and a leaf's CLR type is inferred from its text.
/// </summary>
public interface IXmlDtoService
{
    Task<XmlDtoResult> GenerateAsync(
        string xml,
        string? typeName,
        string? projectName,
        string? namespaceName,
        string? directory,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class XmlDtoService : IXmlDtoService
{
    private const int MaxDepth = 6;
    private const int MaxShapes = 20;

    private readonly IRoslynWorkspace _workspace;

    public XmlDtoService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<XmlDtoResult> GenerateAsync(
        string xml,
        string? typeName,
        string? projectName,
        string? namespaceName,
        string? directory,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new XmlDtoResult { Error = "An XML sample is required." };

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return new XmlDtoResult { Error = $"The XML does not parse: {ex.Message}" };
        }

        if (document.Root is null) return new XmlDtoResult { Error = "The XML has no root element." };

        var rootName = string.IsNullOrWhiteSpace(typeName) ? Pascal(document.Root.Name.LocalName) : typeName!.Trim();
        if (!SyntaxFacts.IsValidIdentifier(rootName))
            return new XmlDtoResult { Error = $"'{rootName}' is not a valid C# type name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new XmlDtoResult { Error = "No C# solution could be auto-located from the working directory." };

        var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();
        Project? project;
        if (projectName is not null)
        {
            project = projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new XmlDtoResult { Error = $"Project '{projectName}' not found." };
        }
        else
        {
            if (projects.Count != 1)
            {
                return new XmlDtoResult
                {
                    Error = "Several projects; pass projectName.",
                    Note = string.Join(", ", projects.Select(candidate => candidate.Name)),
                };
            }

            project = projects[0];
        }

        var shapes = new List<Shape>();
        Build(rootName, document.Root, shapes, depth: 0);
        if (shapes.Count > MaxShapes)
            return new XmlDtoResult { Error = $"The sample nests {shapes.Count} shapes; simplify it first." };

        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(project.FilePath!)!
            : Path.GetFullPath(directory);

        var targetNamespace = string.IsNullOrWhiteSpace(namespaceName)
            ? project.DefaultNamespace ?? project.AssemblyName ?? "Generated"
            : namespaceName!;

        var source = Render(shapes, targetNamespace);
        var fileName = rootName + ".cs";
        var filePath = Path.Combine(targetDirectory, fileName);

        if (File.Exists(filePath)) return new XmlDtoResult { Error = $"{filePath} already exists." };

        var withFile = solution.AddDocument(
            DocumentId.CreateNewId(project.Id), fileName, SourceText.From(source), filePath: filePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, withFile, [project.Id], cancellationToken);
        if (!gate.Passed)
        {
            return new XmlDtoResult
            {
                Error = $"Refused: generating {rootName} would add {gate.Added} compiler error(s) " +
                        $"({gate.Before} → {gate.After}).",
                Note = string.Join("; ", gate.NewErrors.Take(5)),
            };
        }

        if (!dryRun)
        {
            await File.WriteAllTextAsync(filePath, source, cancellationToken);
            _workspace.TryAddDocument(project.Id, fileName, filePath, SourceText.From(source));
        }

        return new XmlDtoResult
        {
            Generated = rootName,
            Types = shapes.Count,
            Properties = shapes.Sum(shape => shape.Members.Count),
            File = filePath,
            Namespace = targetNamespace,
            DryRun = dryRun,
            Applied = !dryRun,
            Preview = source,
            Note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "File written and added to the Roslyn model. A leaf's type comes from its text; a repeated element becomes a List<T>.",
        };
    }

    private sealed class Shape(string name)
    {
        public string Name { get; } = name;

        public List<Member> Members { get; } = [];
    }

    private sealed record Member(string Name, string Type, bool IsList, bool IsLeaf);

    private Shape Build(string name, XElement element, List<Shape> collected, int depth)
    {
        var shape = new Shape(name);
        collected.Add(shape);

        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            shape.Members.Add(new Member(Pascal(attribute.Name.LocalName), Infer(attribute.Value), false, true));

        foreach (var group in element.Elements().GroupBy(child => child.Name.LocalName, StringComparer.Ordinal))
        {
            var first = group.First();
            var isList = group.Count() > 1;
            var memberName = Pascal(first.Name.LocalName);

            if (first.HasElements && depth < MaxDepth)
            {
                var nested = Build(memberName, first, collected, depth + 1);
                shape.Members.Add(new Member(memberName, nested.Name, isList, false));
                continue;
            }

            var values = group.Select(child => child.Value).ToList();
            var inferred = values.Select(Infer).Distinct(StringComparer.Ordinal).Count() == 1
                ? Infer(values[0])
                : "string";

            shape.Members.Add(new Member(memberName, inferred, isList, true));
        }

        return shape;
    }

    private static string Render(IReadOnlyList<Shape> shapes, string namespaceName)
    {
        var builder = new StringBuilder();
        if (shapes.Any(shape => shape.Members.Any(member => member.IsList)))
            builder.AppendLine("using System.Collections.Generic;").AppendLine();

        if (namespaceName.Length > 0) builder.AppendLine($"namespace {namespaceName};").AppendLine();

        foreach (var shape in shapes)
        {
            builder.AppendLine($"public sealed class {shape.Name}");
            builder.AppendLine("{");

            foreach (var member in shape.Members)
            {
                if (member.IsList)
                {
                    builder.AppendLine($"    public List<{member.Type}> {member.Name} {{ get; init; }} = new();");
                }
                else if (!member.IsLeaf)
                {
                    builder.AppendLine($"    public {member.Type} {member.Name} {{ get; init; }} = new();");
                }
                else if (member.Type == "string")
                {
                    builder.AppendLine($"    public string {member.Name} {{ get; init; }} = string.Empty;");
                }
                else
                {
                    builder.AppendLine($"    public {member.Type} {member.Name} {{ get; init; }}");
                }
            }

            builder.AppendLine("}");
            if (shape != shapes[^1]) builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>A leaf's CLR type from its text: the bool, then the numbers, then the date and the GUID.</summary>
    private static string Infer(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return "string";
        if (bool.TryParse(text, out _)) return "bool";
        if (int.TryParse(text, out _)) return "int";
        if (long.TryParse(text, out _)) return "long";
        if (decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return "decimal";
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)) return "DateTime";
        if (Guid.TryParse(text, out _)) return "Guid";
        return "string";
    }

    /// <summary><c>item-id</c> becomes <c>ItemId</c>; a character a name cannot hold becomes nothing.</summary>
    private static string Pascal(string name)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var character in name)
        {
            if (character is '-' or '_' or '.' or ':' or ' ')
            {
                upper = true;
                continue;
            }

            if (!char.IsLetterOrDigit(character)) continue;
            builder.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }

        if (builder.Length == 0) return "Value";
        if (char.IsDigit(builder[0])) builder.Insert(0, '_');
        return builder.ToString();
    }
}
