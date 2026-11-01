using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Generation;

/// <summary>One template: its shortcut, what kind it is, and the body with <c>$variable$</c> holes.</summary>
public sealed record TemplateDefinition(
    string Name,
    string Kind,
    string Body,
    string Description,
    IReadOnlyDictionary<string, string> Defaults);

/// <summary>
/// ReSharper's template family as data: a live template expands at a line (the shortcut line is
/// replaced, or the expansion is inserted after it), a postfix template expands an expression the line
/// already ends with (<c>order.if</c>), and a file template creates a new document in the project.
/// <para>
/// The built-ins cover the everyday templates; a team adds its own in
/// <c>.visualMCP/templates.json</c> and <c>save</c>/<c>delete</c> maintain them. Every write is
/// compiler-gated, previewed by default and recorded in the edit history, like the refactorings.
/// </para>
/// </summary>
public interface ITemplateService
{
    Task<object> ListAsync(CancellationToken cancellationToken = default);

    Task<object> ExpandAsync(
        string filePath, int line, string template, string? values, bool dryRun,
        CancellationToken cancellationToken = default);

    Task<object> CreateFileAsync(
        string template, string? name, string? projectName, string? namespaceName, string? directory,
        string? values, bool dryRun, CancellationToken cancellationToken = default);

    Task<object> SaveAsync(
        string name, string kind, string body, string? description, bool dryRun,
        CancellationToken cancellationToken = default);

    Task<object> DeleteAsync(string name, bool dryRun, CancellationToken cancellationToken = default);
}

public sealed class TemplateService : ITemplateService
{
    private const string StoreRelativePath = ".visualMCP/templates.json";

    private static readonly Regex Hole = new(@"\$([A-Za-z_][A-Za-z0-9_]*)\$", RegexOptions.Compiled);

    // ── built-in templates ─────────────────────────────────────────────────────

    private static TemplateDefinition Live(string name, string body, string description,
        params (string Key, string Value)[] defaults) =>
        new(name, "live", body, description,
            defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private static TemplateDefinition Postfix(string name, string body, string description,
        params (string Key, string Value)[] defaults) =>
        new(name, "postfix", body, description,
            defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private static TemplateDefinition FileTemplate(string name, string body, string description) =>
        new(name, "file", body, description, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static readonly TemplateDefinition[] BuiltInLive =
    [
        Live("prop", "public $type$ $name$ { get; set; }", "An auto-property.",
            ("type", "string"), ("name", "Value")),
        Live("propfull",
            "private $type$ _$name$;\npublic $type$ $property$\n{\n    get => _$name$;\n    set => _$name$ = value;\n}",
            "A property with a backing field.",
            ("type", "string"), ("name", "value"), ("property", "Value")),
        Live("ctor", "public $className$($parameters$)\n{\n}", "A constructor of the enclosing type.",
            ("parameters", "")),
        Live("for", "for (var $i$ = 0; $i$ < $count$; $i$++)\n{\n}", "A counting loop.",
            ("i", "i"), ("count", "count")),
        Live("foreach", "foreach (var $item$ in $collection$)\n{\n}", "A foreach loop.",
            ("item", "item"), ("collection", "items")),
        Live("while", "while ($condition$)\n{\n}", "A while loop.", ("condition", "true")),
        Live("if", "if ($condition$)\n{\n}", "An if statement.", ("condition", "true")),
        Live("ifelse", "if ($condition$)\n{\n}\nelse\n{\n}", "An if/else statement.", ("condition", "true")),
        Live("switch", "switch ($expression$)\n{\n    case $value$:\n        break;\n}", "A switch statement.",
            ("expression", "value"), ("value", "0")),
        Live("try", "try\n{\n}\ncatch (System.Exception ex)\n{\n}", "try/catch."),
        Live("tryf", "try\n{\n}\nfinally\n{\n}", "try/finally."),
        Live("using", "using (var $resource$ = $expression$)\n{\n}", "A using statement.",
            ("resource", "stream"), ("expression", "System.IO.File.OpenRead(\"path\")")),
        Live("lock", "lock ($syncRoot$)\n{\n}", "A lock over the type's sync root.", ("syncRoot", "_syncRoot")),
        Live("throw", "throw new $exception$(\"$message$\");", "Throw an exception.",
            ("exception", "System.ArgumentException"), ("message", "")),
        Live("test", "[Xunit.Fact]\npublic void $name$()\n{\n}", "An xUnit fact.",
            ("name", "It_works")),
        Live("guid", "System.Guid.NewGuid()", "A new GUID."),
        Live("main", "public static void Main(string[] args)\n{\n}", "A console entry point."),
    ];

    private static readonly TemplateDefinition[] BuiltInPostfix =
    [
        Postfix("if", "if ($expression$)\n{\n}", "Wrap the expression in an if."),
        Postfix("null", "$expression$ ?? throw new System.ArgumentNullException(nameof($expression$));",
            "Throw when the expression is null."),
        Postfix("not", "!$expression$", "Negate the expression."),
        Postfix("var", "var $name$ = $expression$;", "Assign the expression to a var."),
        Postfix("await", "await $expression$;", "Await the expression."),
        Postfix("foreach", "foreach (var $item$ in $expression$)\n{\n}", "Iterate the expression."),
        Postfix("forr", "for (var $i$ = $expression$.Count - 1; $i$ >= 0; $i$--)\n{\n}", "Iterate backwards."),
        Postfix("ret", "return $expression$;", "Return the expression."),
        Postfix("toList", "$expression$.ToList();", "Materialise the expression as a list."),
        Postfix("switch", "switch ($expression$)\n{\n    default:\n        break;\n}", "Switch over the expression."),
    ];

    private static readonly TemplateDefinition[] BuiltInFile =
    [
        FileTemplate("class", "namespace $namespace$;\n\npublic sealed class $name$\n{\n}",
            "A sealed class in a new file."),
        FileTemplate("interface", "namespace $namespace$;\n\npublic interface $name$\n{\n}",
            "An interface in a new file."),
        FileTemplate("record", "namespace $namespace$;\n\npublic sealed record $name$($parameters$);",
            "A positional record in a new file."),
        FileTemplate("struct", "namespace $namespace$;\n\npublic struct $name$\n{\n}",
            "A struct in a new file."),
        FileTemplate("enum", "namespace $namespace$;\n\npublic enum $name$\n{\n}",
            "An enum in a new file."),
        FileTemplate("static-class", "namespace $namespace$;\n\npublic static class $name$\n{\n}",
            "A static class in a new file."),
        FileTemplate("abstract-class", "namespace $namespace$;\n\npublic abstract class $name$\n{\n}",
            "An abstract class in a new file."),
        FileTemplate("xunit-test",
            "using Xunit;\n\nnamespace $namespace$;\n\npublic sealed class $name$\n{\n    [Fact]\n    public void $method$()\n    {\n    }\n}",
            "An xUnit test class."),
    ];

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public TemplateService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    // ── list ───────────────────────────────────────────────────────────────────

    public async Task<object> ListAsync(CancellationToken cancellationToken = default)
    {
        var (custom, path, errors, loadError) = await LoadCustomAsync(cancellationToken);
        if (loadError is not null) return loadError;

        object Describe(TemplateDefinition definition) => new
        {
            name = definition.Name,
            kind = definition.Kind,
            description = definition.Description,
            defaults = definition.Defaults,
            custom = custom.ContainsKey(definition.Name),
        };

        var builtInNames = BuiltInLive.Concat(BuiltInPostfix).Concat(BuiltInFile)
            .Select(definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new
        {
            live = BuiltInLive.Select(Describe).Concat(
                custom.Values.Where(definition => definition.Kind == "live").Select(Describe)).ToList(),
            postfix = BuiltInPostfix.Select(Describe).Concat(
                custom.Values.Where(definition => definition.Kind == "postfix").Select(Describe)).ToList(),
            file = BuiltInFile.Select(Describe).Concat(
                custom.Values.Where(definition => definition.Kind == "file").Select(Describe)).ToList(),
            customCount = custom.Count,
            store = path,
            storeExists = File.Exists(path),
            errors,
            usage =
                "expand: the line's trimmed text is the live shortcut (replaced) or anything else (the expansion is " +
                "inserted after it); a postfix template needs the line to end with '.name'. values is 'key=value;key2=value2'. " +
                "create: a file template makes a new document in the project. save/delete maintain the custom ones.",
            note = builtInNames.Count > 0 ? "A custom template cannot shadow a built-in name." : null,
        };
    }

    // ── expand (live and postfix) ──────────────────────────────────────────────

    public async Task<object> ExpandAsync(
        string filePath, int line, string template, string? values, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line is 1-based." };
        if (string.IsNullOrWhiteSpace(template)) return new { error = "A template name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var document = FindDocument(solution, filePath);
        if (document is null) return new { error = $"{filePath} is not a document of the loaded solution." };

        var text = await document.GetTextAsync(cancellationToken);
        if (line > text.Lines.Count)
            return new { error = $"Line {line} is out of range: the file has {text.Lines.Count} line(s)." };

        var (custom, _, _, loadError) = await LoadCustomAsync(cancellationToken);
        if (loadError is not null) return loadError;

        var trimmedLine = text.Lines[line - 1].ToString().Trim();
        var candidates = BuiltInLive.Concat(BuiltInPostfix).Concat(BuiltInFile).Concat(custom.Values)
            .Where(candidate => candidate.Name.Equals(template.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The same shortcut can name both a live and a postfix template ('if', 'foreach', 'switch'):
        // the line decides. '.if' at the end means the postfix one; otherwise the live one.
        var definition = candidates.FirstOrDefault(candidate =>
                             candidate.Kind == "postfix" &&
                             trimmedLine.EndsWith("." + candidate.Name, StringComparison.OrdinalIgnoreCase))
                         ?? candidates.FirstOrDefault(candidate => candidate.Kind == "live")
                         ?? candidates.FirstOrDefault();

        if (definition is null)
            return new
            {
                error = $"Unknown template '{template}'.",
                live = BuiltInLive.Concat(custom.Values.Where(item => item.Kind == "live")).Select(item => item.Name).ToList(),
                postfix = BuiltInPostfix.Concat(custom.Values.Where(item => item.Kind == "postfix")).Select(item => item.Name).ToList(),
            };

        if (definition.Kind == "file")
            return new { error = $"'{definition.Name}' is a file template: use action=create." };

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null) return new { error = $"Could not read the semantic model of {Path.GetFileName(document.FilePath)}." };

        var source = text.ToString();
        var newLine = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lineText = text.Lines[line - 1].ToString();
        var indentation = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());
        var trimmed = lineText.Trim();
        var input = ParseValues(values);

        string body;
        string mode;
        string replaced;

        if (definition.Kind == "postfix")
        {
            var suffix = "." + definition.Name;
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new
                {
                    error = $"The postfix template '{definition.Name}' needs the line to end with '{suffix}' " +
                            $"(line {line} is '{trimmed}').",
                };

            var expression = trimmed[..^suffix.Length].Trim();
            if (expression.Length == 0)
                return new { error = $"There is no expression before '.{definition.Name}' on line {line}." };

            input["expression"] = expression;
            input.TryAdd("name", NameFor(expression));
            input.TryAdd("item", ItemFor(expression));
            input.TryAdd("i", "i");

            body = definition.Name == "null" && !IsSimpleExpression(expression)
                ? "$expression$ ?? throw new System.InvalidOperationException(\"The value is null.\");"
                : definition.Body;

            mode = "postfix";
            replaced = trimmed;
        }
        else
        {
            input.TryAdd("className", EnclosingTypeName(model, text.Lines[line - 1].Start) ?? "Class");
            input.TryAdd("namespace", EnclosingNamespace(model, text.Lines[line - 1].Start) ?? "Generated");
            body = definition.Body;

            var isShortcut = trimmed.Equals(definition.Name, StringComparison.OrdinalIgnoreCase);
            mode = isShortcut ? "replace" : "insert";
            replaced = trimmed;
        }

        var rendered = Render(body, input, definition.Defaults, out var unresolved);
        var indented = Indent(rendered, indentation, newLine);

        var lineSpan = text.Lines[line - 1].Span;
        string newText;
        if (mode == "insert")
        {
            var includingBreak = text.Lines[line - 1].SpanIncludingLineBreak;
            var hasBreak = includingBreak.End > lineSpan.End;
            newText = hasBreak
                ? source[..includingBreak.End] + indented + newLine + source[includingBreak.End..]
                : source + newLine + indented + newLine;
        }
        else
        {
            newText = source[..lineSpan.Start] + indented + source[lineSpan.End..];
        }

        var afterSolution = solution.WithDocumentText(document.Id, SourceText.From(newText, text.Encoding));
        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, afterSolution, [document.Project.Id], cancellationToken);
        if (!gate.Passed)
            return RefactoringCompilerGate.Refusal(gate, $"expanding '{definition.Name}' on line {line}");

        if (!dryRun)
        {
            _store?.RecordEdit("templates", document.FilePath!, definition.Name, source, newText);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);
        }

        return new
        {
            tool = "templates",
            action = "expand",
            template = definition.Name,
            kind = definition.Kind,
            mode,
            file = document.FilePath,
            line,
            replaced,
            unresolved,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(source, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    // ── create (file templates) ────────────────────────────────────────────────

    public async Task<object> CreateFileAsync(
        string template, string? name, string? projectName, string? namespaceName, string? directory,
        string? values, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(template)) return new { error = "A template name is required." };
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A type name is required." };
        if (!SyntaxFacts.IsValidIdentifier(name))
            return new { error = $"'{name}' is not a valid C# type name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var (custom, _, _, loadError) = await LoadCustomAsync(cancellationToken);
        if (loadError is not null) return loadError;

        var definition = BuiltInFile.Concat(custom.Values)
            .FirstOrDefault(candidate => candidate.Name.Equals(template.Trim(), StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return new
            {
                error = $"Unknown file template '{template}'.",
                file = BuiltInFile.Concat(custom.Values.Where(item => item.Kind == "file")).Select(item => item.Name).ToList(),
            };

        if (definition.Kind != "file")
            return new { error = $"'{definition.Name}' is a {definition.Kind} template: use action=expand." };

        var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();
        Project? project;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new { error = $"Project '{projectName}' not found." };
        }
        else
        {
            if (projects.Count != 1)
                return new { error = "Several projects; pass projectName.", candidates = projects.Select(candidate => candidate.Name).ToList() };
            project = projects[0];
        }

        var className = definition.Name == "xunit-test" && !name.EndsWith("Tests", StringComparison.Ordinal)
            ? name + "Tests"
            : name;

        var targetDirectory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(project.FilePath!)!
            : Path.GetFullPath(directory);

        var namespaceForType = string.IsNullOrWhiteSpace(namespaceName)
            ? project.DefaultNamespace ?? project.AssemblyName ?? "Generated"
            : namespaceName!;

        var input = ParseValues(values);
        input["name"] = className;
        input["namespace"] = namespaceForType;
        input.TryAdd("method", "It_works");
        input.TryAdd("parameters", string.Empty);

        var source = Render(definition.Body, input, definition.Defaults, out var unresolved);
        var filePath = Path.Combine(targetDirectory, className + ".cs");
        if (File.Exists(filePath)) return new { error = $"{filePath} already exists." };

        var documentId = DocumentId.CreateNewId(project.Id);
        var withFile = solution.AddDocument(
            documentId, className + ".cs", SourceText.From(source), filePath: filePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, withFile, [project.Id], cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"creating {className}");

        if (!dryRun)
        {
            Directory.CreateDirectory(targetDirectory);
            await RefactoringFileWriter.WriteAsync(filePath, source, cancellationToken);
            _workspace.TryAddDocument(project.Id, className + ".cs", filePath, SourceText.From(source));
        }

        return new
        {
            tool = "templates",
            action = "create",
            template = definition.Name,
            type = className,
            @namespace = namespaceForType,
            file = filePath,
            unresolved,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = source,
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written and added to the Roslyn model.",
        };
    }

    // ── save / delete (the custom store) ───────────────────────────────────────

    public async Task<object> SaveAsync(
        string name, string kind, string body, string? description, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A template name is required." };
        if (name.Length > 64 || !name.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
            return new { error = $"'{name}' is not a template name (letters, digits, '-', '_' and '.' only, 64 max)." };

        var normalizedKind = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedKind is not ("live" or "postfix" or "file"))
            return new { error = "kind must be live, postfix or file." };

        if (string.IsNullOrWhiteSpace(body)) return new { error = "A template body is required." };

        if (BuiltInLive.Concat(BuiltInPostfix).Concat(BuiltInFile)
            .Any(builtIn => builtIn.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return new { error = $"'{name}' is a built-in template; save the custom one under another name." };

        var (custom, path, errors, loadError) = await LoadCustomAsync(cancellationToken);
        if (loadError is not null) return loadError;

        var all = new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (customName, definition) in custom) all[customName] = definition;
        all[name] = new TemplateDefinition(name, normalizedKind, body,
            description?.Trim() ?? string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        if (!dryRun)
        {
            var writeError = await WriteStoreAsync(all.Values.ToList(), path, cancellationToken);
            if (writeError is not null) return new { error = writeError };
        }

        return new
        {
            tool = "templates",
            action = "save",
            template = name,
            kind = normalizedKind,
            store = path,
            templates = all.Count,
            errors,
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the store." : "Store written.",
        };
    }

    public async Task<object> DeleteAsync(string name, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A template name is required." };

        var (custom, path, errors, loadError) = await LoadCustomAsync(cancellationToken);
        if (loadError is not null) return loadError;

        if (!custom.TryGetValue(name.Trim(), out var definition))
            return new
            {
                error = $"No custom template named '{name}'. Built-in templates cannot be deleted.",
                custom = custom.Keys.ToList(),
                errors,
            };

        var remaining = custom.Values
            .Where(candidate => !candidate.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!dryRun)
        {
            var writeError = await WriteStoreAsync(remaining, path, cancellationToken);
            if (writeError is not null) return new { error = writeError };
        }

        return new
        {
            tool = "templates",
            action = "delete",
            removed = definition.Name,
            kind = definition.Kind,
            store = path,
            templates = remaining.Count,
            dryRun,
            applied = !dryRun,
        };
    }

    // ── the custom store ───────────────────────────────────────────────────────

    private async Task<(Dictionary<string, TemplateDefinition> Templates, string Path, List<string> Errors, object? Error)>
        LoadCustomAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(SolutionPaths.DirectoryOf(_workspace), StoreRelativePath);
        var templates = new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        if (!File.Exists(path)) return (templates, path, errors, null);

        string json;
        try { json = await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception exception)
        {
            return (templates, path, errors, new { error = $"Could not read {path}: {exception.Message}" });
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("templates", out var array) ||
                array.ValueKind != JsonValueKind.Array)
                return (templates, path, errors,
                    new
                    {
                        error = $"{path} must be an object with a \"templates\" array.",
                        schema = "{\"templates\":[{\"name\":\"dto\",\"kind\":\"live\",\"description\":\"...\"," +
                                 "\"body\":\"public sealed record $name$($parameters$);\"}]}",
                    });

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("A template entry is not an object.");
                    continue;
                }

                var name = StringOf(item, "name");
                var kind = StringOf(item, "kind")?.ToLowerInvariant();
                var body = StringOf(item, "body");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body) ||
                    kind is not ("live" or "postfix" or "file"))
                {
                    errors.Add($"A template entry is incomplete (name, kind live|postfix|file, body): {item.GetRawText()}");
                    continue;
                }

                var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("defaults", out var defaultsElement) &&
                    defaultsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in defaultsElement.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String)
                            defaults[property.Name] = property.Value.GetString()!;
                }

                templates[name!] = new TemplateDefinition(name!, kind!,
                    body!.Replace("\r\n", "\n"), StringOf(item, "description") ?? string.Empty, defaults);
            }
        }
        catch (JsonException exception)
        {
            return (templates, path, errors,
                new { error = $"{path} is not valid JSON: {exception.Message}" });
        }

        return (templates, path, errors, null);
    }

    private static async Task<string?> WriteStoreAsync(
        IReadOnlyList<TemplateDefinition> templates, string path, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                new
                {
                    templates = templates
                        .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(template => new
                        {
                            name = template.Name,
                            kind = template.Kind,
                            description = template.Description,
                            body = template.Body,
                            defaults = template.Defaults.Count > 0 ? template.Defaults : null,
                        }),
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });

            await File.WriteAllTextAsync(path, json + Environment.NewLine, cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return $"Could not write {path}: {exception.Message}";
        }
    }

    // ── rendering ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Substitutes <c>$hole$</c> from the caller's values, then the template's defaults, then the
    /// context (the enclosing type and namespace); a hole nothing answers keeps its name, so the
    /// compiler gate reports it instead of the template silently generating a placeholder.
    /// </summary>
    private static string Render(
        string body, Dictionary<string, string> values, IReadOnlyDictionary<string, string> defaults,
        out List<string> unresolved)
    {
        var missing = new List<string>();
        var normalized = body.Replace("\r\n", "\n");
        var rendered = Hole.Replace(normalized, match =>
        {
            var name = match.Groups[1].Value;
            if (name == "END") return string.Empty;
            if (values.TryGetValue(name, out var value)) return value;
            if (defaults.TryGetValue(name, out var fallback)) return fallback;
            missing.Add(name);
            return name;
        });

        unresolved = missing;
        return rendered.TrimEnd();
    }

    /// <summary>Prepends the base indentation to every line of the rendered body, keeping relative indentation.</summary>
    private static string Indent(string body, string indentation, string newLine)
    {
        var lines = body.Split('\n');
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) builder.Append(newLine);
            if (lines[index].Length == 0) continue;
            builder.Append(indentation).Append(lines[index].TrimEnd());
        }

        return builder.ToString();
    }

    /// <summary>The first identifier of the expression's tail, camel-cased: 'GetOrders()' → 'orders'.</summary>
    private static string NameFor(string expression)
    {
        var identifiers = Regex.Matches(expression, "[A-Za-z_][A-Za-z0-9_]*")
            .Select(match => match.Value)
            .Where(value => value is not ("this" or "base"))
            .ToList();
        if (identifiers.Count == 0) return "value";

        var name = identifiers[^1];
        if (name.Length > 3 && name.StartsWith("Get", StringComparison.Ordinal)) name = name[3..];
        if (name.Length == 0) return "value";
        if (name.All(char.IsUpper)) return name.ToLowerInvariant();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The loop variable for a collection expression: 'orders' → 'order'.</summary>
    private static string ItemFor(string expression)
    {
        var name = NameFor(expression);
        if (name.Length > 1 && name.EndsWith("s", StringComparison.Ordinal) &&
            !name.EndsWith("ss", StringComparison.Ordinal))
            return name[..^1];
        return name;
    }

    private static bool IsSimpleExpression(string expression) =>
        Regex.IsMatch(expression, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");

    private static Dictionary<string, string> ParseValues(string? values)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(values)) return map;

        foreach (var pair in values.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            map[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        return map;
    }

    private static string? EnclosingTypeName(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        return type?.Name;
    }

    private static string? EnclosingNamespace(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        if (symbol?.ContainingNamespace is { IsGlobalNamespace: false } containing)
            return containing.ToDisplayString();

        var root = model.SyntaxTree.GetRoot();
        var declaration = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        return declaration?.Name.ToString();
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Document? FindDocument(Solution solution, string filePath)
    {
        var path = Path.GetFullPath(filePath);
        return solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path,
                                                       StringComparison.OrdinalIgnoreCase));
    }
}
