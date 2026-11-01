using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Administering the inspections, which is the half a findings list does not give: change a rule's
/// severity where the compiler reads it (<c>.editorconfig</c>), and write a suppression in the place
/// that actually silences the finding — a <c>#pragma</c> around the member or a
/// <see cref="System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"/> on it — refusing an edit
/// that would stop the project compiling.
/// </summary>
public interface IInspectionConfigurationService
{
    /// <summary>Set <c>dotnet_diagnostic.&lt;id&gt;.severity</c> for the solution, a project or one file.</summary>
    Task<object> ConfigureAsync(string diagnosticId, string severity, string? scope, string? filePath,
        string? projectName, bool dryRun);

    /// <summary>Suppress one finding: strategy "pragma" (around the member) or "attribute" (on it).</summary>
    Task<object> SuppressAsync(string diagnosticId, string? strategy, string? filePath, int? line,
        string? justification, bool dryRun);

    /// <summary>Author any .editorconfig option in the families this product understands.</summary>
    Task<object> ConfigureOptionAsync(string option, string value, string? scope, string? filePath,
        string? projectName, bool dryRun);
}

public sealed class InspectionConfigurationService : IInspectionConfigurationService
{
    private static readonly string[] Severities = ["none", "silent", "suggestion", "warning", "error", "default"];
    private static readonly Regex DiagnosticIdPattern = new("^[A-Za-z]{2,}\\d{3,}$", RegexOptions.Compiled);

    /// <summary>The option families this tool is allowed to author; anything else is refused with examples.</summary>
    private static readonly Regex OptionPattern = new(
        "^(csharp|dotnet|indent|tab|charset|end_of_line|insert_final_newline|trim_trailing_whitespace|max_line_length|file_header)[._A-Za-z0-9]*$",
        RegexOptions.Compiled);

    private readonly IRoslynWorkspace _workspace;
    public InspectionConfigurationService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConfigureAsync(string diagnosticId, string severity, string? scope = null,
        string? filePath = null, string? projectName = null, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(diagnosticId) || !DiagnosticIdPattern.IsMatch(diagnosticId.Trim()))
            return new
            {
                error = $"'{diagnosticId}' is not a diagnostic id.",
                examples = new[] { "CA1822", "RCS1060", "SA1200", "IDE0001", "VMC0001", "CS0168" },
            };

        var level = (severity ?? string.Empty).Trim().ToLowerInvariant();
        if (!Severities.Contains(level))
            return new { error = $"'{severity}' is not a severity. Use one of: {string.Join(", ", Severities)}." };

        var id = diagnosticId.Trim().ToUpperInvariant();
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        var solutionDirectory = SolutionDirectory(solution);
        if (solutionDirectory is null)
            return new { error = "No solution is loaded, so there is no folder for the .editorconfig." };

        var kind = (scope ?? "solution").Trim().ToLowerInvariant();
        string directory;
        string section;

        switch (kind)
        {
            case "solution":
                directory = solutionDirectory;
                section = "[*.cs]";
                break;

            case "project":
            {
                var project = ResolveProject(solution, projectName);
                if (project?.FilePath is null)
                    return new { error = projectName is null
                        ? "Pass projectName for scope=project."
                        : $"Project '{projectName}' not found in the solution." };

                directory = Path.GetDirectoryName(project.FilePath)!;
                section = "[*.cs]";
                break;
            }

            case "file":
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return new { error = "Pass filePath for scope=file." };

                var full = Path.GetFullPath(Path.IsPathRooted(filePath)
                    ? filePath
                    : Path.Combine(solutionDirectory, filePath));

                directory = solutionDirectory;
                var relative = Path.GetRelativePath(directory, full).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal))
                    return new { error = $"{full} is outside the solution folder; scope=file needs a file inside it." };

                section = $"[{relative}]";
                break;
            }

            default:
                return new { error = $"Unknown scope '{scope}'. Use solution, project or file." };
        }

        var editorConfig = Path.Combine(directory, ".editorconfig");
        var existing = File.Exists(editorConfig) ? await File.ReadAllTextAsync(editorConfig) : string.Empty;
        var key = $"dotnet_diagnostic.{id}.severity";
        var (updated, previous, atLine) = Upsert(existing, section, key, level);

        if (!dryRun)
            await File.WriteAllTextAsync(editorConfig, updated);

        return new
        {
            configured = true,
            id,
            severity = level,
            scope = kind,
            editorConfig,
            section,
            key,
            previous,
            line = atLine,
            changed = !string.Equals(existing, updated, StringComparison.Ordinal),
            dryRun,
            applied = !dryRun,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the .editorconfig."
                : "The next build/inspection pass reads the new severity. An .editorconfig change cannot break the build.",
        };
    }

    public async Task<object> SuppressAsync(string diagnosticId, string? strategy = null, string? filePath = null,
        int? line = null, string? justification = null, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(diagnosticId) || !DiagnosticIdPattern.IsMatch(diagnosticId.Trim()))
            return new { error = $"'{diagnosticId}' is not a diagnostic id." };
        if (string.IsNullOrWhiteSpace(filePath))
            return new { error = "A file path is required." };

        var id = diagnosticId.Trim().ToUpperInvariant();
        var mode = (strategy ?? "pragma").Trim().ToLowerInvariant();
        if (mode is not ("pragma" or "attribute"))
            return new { error = $"Unknown strategy '{strategy}'. Use pragma or attribute." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync();
        var text = await document.GetTextAsync();
        if (root is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        // The declaration to guard: the innermost member that contains the requested line; with no
        // line, the whole file is guarded.
        if (line is int outside && (outside < 1 || outside > text.Lines.Count))
            return new
            {
                error = $"Line {outside} is outside {Path.GetFileName(path)}, which has {text.Lines.Count} line(s).",
            };

        var position = line is int requested
            ? text.Lines[requested - 1].Start
            : 0;

        MemberDeclarationSyntax? node = null;
        FileLinePositionSpan span;
        string label;

        if (line is null)
        {
            span = root.GetLocation().GetLineSpan();
            label = "the file";
        }
        else
        {
            node = root.FindToken(position).Parent?.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>().FirstOrDefault();

            if (node is null)
                return new { error = $"No declaration to guard at line {line} of {Path.GetFileName(path)}." };

            span = node.GetLocation().GetLineSpan();
            label = MemberLabel(node);
        }
        var newline = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
        var note = justification is null ? string.Empty : " // " + justification;

        string newText;
        int fromLine;
        int toLine;

        if (mode == "pragma")
        {
            var lines = text.ToString().Replace("\r\n", "\n").Split('\n').ToList();
            var firstLine = span.StartLinePosition.Line;
            var lastLine = span.EndLinePosition.Line;
            var indent = new string(lines[firstLine].TakeWhile(char.IsWhiteSpace).ToArray());

            lines.Insert(lastLine + 1, $"{indent}#pragma warning restore {id}{note}");
            lines.Insert(firstLine, $"{indent}#pragma warning disable {id}{note}");

            newText = string.Join(newline, lines);
            fromLine = firstLine + 1;
            toLine = lastLine + 3;
        }
        else
        {
            var category = CategoryFor(id);
            var attribute = $"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"{category}\", \"{id}\"{Justification(justification)})]";
            var firstLine = span.StartLinePosition.Line;
            var lines = text.ToString().Replace("\r\n", "\n").Split('\n').ToList();
            var indent = new string(lines[firstLine].TakeWhile(char.IsWhiteSpace).ToArray());

            lines.Insert(firstLine, indent + attribute);

            newText = string.Join(newline, lines);
            fromLine = firstLine + 1;
            toLine = fromLine;
        }

        // ── Gate: the edit must not add compiler errors ──────────────────────────────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, [document.Project.Id]);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"suppressing {id}");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText);

        return new
        {
            suppressed = true,
            id,
            strategy = mode,
            symbol = label,
            file = document.FilePath,
            fromLine,
            toLine,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : mode == "pragma"
                    ? "The pragma wraps the whole member, so every occurrence of the id inside it is silenced."
                    : "The attribute is on the member; analyzers that read SuppressMessage honour it.",
        };
    }

    /// <summary>
    /// Author any .editorconfig option this product understands (formatting preferences, style
    /// options), at the same scopes: <c>configure_inspection</c> writes the diagnostic severities,
    /// this writes the rest of the code-style surface.
    /// </summary>
    public async Task<object> ConfigureOptionAsync(string option, string value, string? scope = null,
        string? filePath = null, string? projectName = null, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(option) || !OptionPattern.IsMatch(option.Trim()))
            return new
            {
                error = $"'{option}' is not an .editorconfig option this tool authors.",
                examples = new[]
                {
                    "csharp_prefer_braces", "indent_size", "indent_style",
                    "dotnet_style_qualification_for_field", "insert_final_newline",
                },
            };

        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
            return new { error = "A single-line value is required." };

        var key = option.Trim();
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        var solutionDirectory = SolutionDirectory(solution);
        if (solutionDirectory is null)
            return new { error = "No solution is loaded, so there is no folder for the .editorconfig." };

        var kind = (scope ?? "solution").Trim().ToLowerInvariant();
        string directory;
        string section;

        switch (kind)
        {
            case "solution":
                directory = solutionDirectory;
                section = "[*.cs]";
                break;

            case "project":
            {
                var project = ResolveProject(solution, projectName);
                if (project?.FilePath is null)
                    return new { error = projectName is null
                        ? "Pass projectName for scope=project."
                        : $"Project '{projectName}' not found in the solution." };

                directory = Path.GetDirectoryName(project.FilePath)!;
                section = "[*.cs]";
                break;
            }

            case "file":
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return new { error = "Pass filePath for scope=file." };

                var full = Path.GetFullPath(Path.IsPathRooted(filePath)
                    ? filePath
                    : Path.Combine(solutionDirectory, filePath));

                directory = solutionDirectory;
                var relative = Path.GetRelativePath(directory, full).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal))
                    return new { error = $"{full} is outside the solution folder; scope=file needs a file inside it." };

                section = $"[{relative}]";
                break;
            }

            default:
                return new { error = $"Unknown scope '{scope}'. Use solution, project or file." };
        }

        var editorConfig = Path.Combine(directory, ".editorconfig");
        var existing = File.Exists(editorConfig) ? await File.ReadAllTextAsync(editorConfig) : string.Empty;
        var (updated, previous, atLine) = Upsert(existing, section, key, value.Trim());

        if (!dryRun) await File.WriteAllTextAsync(editorConfig, updated);

        return new
        {
            configured = true,
            option = key,
            value = value.Trim(),
            scope = kind,
            editorConfig,
            section,
            previous,
            line = atLine,
            changed = !string.Equals(existing, updated, StringComparison.Ordinal),
            dryRun,
            applied = !dryRun,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the .editorconfig."
                : "The formatter and the analyzers read the new option on their next run.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string? SolutionDirectory(Solution? solution)
    {
        var path = solution?.FilePath;
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetDirectoryName(path);

        // An ad-hoc workspace has no solution file: the first project's folder is the root.
        return solution?.Projects
            .Select(project => project.FilePath)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => Path.GetDirectoryName(file!))
            .FirstOrDefault();
    }

    private static Project? ResolveProject(Solution? solution, string? projectName)
    {
        if (solution is null || string.IsNullOrWhiteSpace(projectName)) return null;
        return solution.Projects.FirstOrDefault(project =>
            project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Set a key in one .editorconfig section, creating the file or the section when needed.</summary>
    private static (string Text, string? Previous, int Line) Upsert(string text, string section, string key, string value)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Length == 0
            ? new List<string>()
            : text.Replace("\r\n", "\n").Split('\n').ToList();

        var header = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                header = index;
                break;
            }
        }

        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add(string.Empty);
            lines.Add(section);
            lines.Add($"{key} = {value}");
            return (string.Join(newline, lines), null, lines.Count);
        }

        var end = header + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;

        for (var index = header + 1; index < end; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                continue;

            var equals = trimmed.IndexOf('=');
            var previous = trimmed[(equals + 1)..].Trim();
            var indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
            lines[index] = $"{indent}{key} = {value}";
            return (string.Join(newline, lines), previous, index + 1);
        }

        lines.Insert(end, $"{key} = {value}");
        return (string.Join(newline, lines), null, end + 1);
    }

    private static string CategoryFor(string id) => id[..2] switch
    {
        "CA" => "Design",
        "CS" => "Compiler",
        "RCS" => "Roslynator",
        "SA" => "StyleCop",
        "IDE" => "Style",
        "VMC" => "VisualMCP",
        "MA" => "Usage",
        _ => "General",
    };

    private static string Justification(string? justification) =>
        string.IsNullOrWhiteSpace(justification)
            ? string.Empty
            : ", Justification = \"" + justification.Replace("\"", "'") + "\"";

    private static string MemberLabel(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText + "()",
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText ?? "field",
        TypeDeclarationSyntax type => type.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };
}
