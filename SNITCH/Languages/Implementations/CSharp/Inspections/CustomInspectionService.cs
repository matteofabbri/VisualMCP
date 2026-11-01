using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>One rule authored by the repository, not compiled into an analyzer.</summary>
public sealed record CustomInspectionRule(
    string Id,
    string Kind,
    string Value,
    string Severity,
    string Message,
    string? Title = null);

/// <summary>
/// Custom inspections declared in a file instead of an analyzer assembly: banned symbols and namespaces,
/// banned attributes, identifier patterns that must not appear, and patterns in string literals and
/// comments. The rules live in <c>.visualMCP/inspections.json</c> (or a path the caller names), so a team
/// can add a rule without building one — the gap an IDE closes with a compiled inspection SDK.
/// </summary>
public interface ICustomInspectionService
{
    /// <summary>Validates the rule file without analysing the solution.</summary>
    Task<object> ValidateAsync(string? path);

    /// <summary>Evaluates the rules over the solution (or one project / file).</summary>
    Task<object> RunAsync(string? projectName, string? filePath, string? path, string? minSeverity, int maxResults);
}

public sealed class CustomInspectionService : ICustomInspectionService
{
    private static readonly string[] Kinds =
        ["bannedSymbol", "bannedNamespace", "bannedAttribute", "identifierPattern", "stringLiteralPattern", "commentPattern"];

    private static readonly string[] Severities = ["error", "warning", "info"];

    private readonly IRoslynWorkspace _workspace;
    public CustomInspectionService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ValidateAsync(string? path)
    {
        var (file, rules, errors) = await LoadAsync(path);
        return new
        {
            path = file,
            found = file is not null,
            rules = rules.Count,
            errors,
            byKind = rules.GroupBy(rule => rule.Kind).ToDictionary(group => group.Key, group => group.Count()),
            note = file is null
                ? "No rule file: create .visualMCP/inspections.json at the solution root (or pass path). " +
                  "Schema: { \"rules\": [ { \"id\": \"VMC1001\", \"kind\": \"bannedSymbol\", \"value\": \"System.Console\", " +
                  "\"severity\": \"warning\", \"message\": \"Use ILogger\" } ] }."
                : null,
        };
    }

    public async Task<object> RunAsync(string? projectName, string? filePath, string? path, string? minSeverity, int maxResults)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var (file, rules, errors) = await LoadAsync(path);
        if (file is null)
            return new
            {
                error = "No custom inspection rule file found.",
                next = "Create .visualMCP/inspections.json at the solution root (or pass path), then retry. " +
                       "Call this tool with action=validate to see the expected schema.",
            };

        if (rules.Count == 0)
            return new { path = file, error = "The rule file contains no valid rule.", errors };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var minimum = Rank(minSeverity ?? "info");
        var limit = Math.Clamp(maxResults < 1 ? 200 : maxResults, 1, 5000);
        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                if (filePath is not null && !MatchesPath(document.FilePath, filePath)) continue;

                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var rule in rules)
                {
                    if (Rank(rule.Severity) < minimum) continue;
                    foreach (var hit in Evaluate(rule, root, model))
                    {
                        var key = $"{rule.Id}|{document.FilePath}|{hit.Line}|{hit.Text}";
                        if (!seen.Add(key)) continue;
                        findings.Add(new Finding(rule.Id, rule.Severity, rule.Kind, rule.Message,
                            project.Name, document.FilePath, hit.Line, hit.Column, hit.Text));
                    }
                }
            }
        }

        var byRule = findings
            .GroupBy(finding => finding.Rule, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new
        {
            path = file,
            projectFilter = projectName ?? "all",
            ruleCount = rules.Count,
            errors,
            total = findings.Count,
            byRule,
            findings = findings.Take(limit).Select(finding => new
            {
                rule = finding.Rule,
                severity = finding.Severity,
                kind = finding.Kind,
                message = finding.Message,
                project = finding.Project,
                file = finding.File,
                finding.Line,
                finding.Column,
                snippet = finding.Snippet,
            }).ToList(),
            truncated = findings.Count > limit,
            note = "Rules are declared in the file; they have no code fix, so a finding is fixed by hand. " +
                   "inspection_baseline can gate a change against them when inspect_code reports the same ids.",
        };
    }

    private static int Rank(string? severity) => (severity ?? "info").Trim().ToLowerInvariant() switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };

    private static bool MatchesPath(string? documentPath, string requested)
    {
        if (documentPath is null) return false;
        return string.Equals(Path.GetFullPath(documentPath), Path.GetFullPath(requested), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Hit(int Line, int Column, string Text);

    private sealed record Finding(
        string Rule, string Severity, string Kind, string Message, string Project, string? File, int Line, int Column, string Snippet);

    private static IEnumerable<Hit> Evaluate(CustomInspectionRule rule, SyntaxNode root, SemanticModel model)
    {
        var matcher = Matcher.For(rule.Value);
        if (matcher is null) yield break;

        switch (rule.Kind)
        {
            case "bannedNamespace":
                foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    var name = directive.Name?.ToString();
                    if (name is not null && matcher.IsMatch(name))
                        yield return Position(directive, name);
                }
                break;

            case "bannedAttribute":
                foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
                {
                    var name = attribute.Name.ToString();
                    if (matcher.IsMatch(name) || matcher.IsMatch(name + "Attribute"))
                        yield return Position(attribute, name);
                }
                break;

            case "identifierPattern":
                foreach (var node in Declarations(root))
                {
                    if (model.GetDeclaredSymbol(node) is not { } symbol) continue;
                    if (!matcher.IsMatch(symbol.Name)) continue;
                    var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location is null) continue;
                    var span = location.GetLineSpan();
                    yield return new Hit(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                        $"{symbol.Kind} {symbol.Name}");
                }
                break;

            case "stringLiteralPattern":
                foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                {
                    if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                    var value = literal.Token.ValueText;
                    if (matcher.IsMatch(value)) yield return Position(literal, Shorten(value));
                }
                break;

            case "commentPattern":
                foreach (var trivia in root.DescendantTrivia())
                {
                    if (!IsComment(trivia)) continue;
                    var text = trivia.ToString();
                    if (!matcher.IsMatch(text)) continue;
                    var span = trivia.GetLocation().GetLineSpan();
                    yield return new Hit(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, Shorten(text.Trim()));
                }
                break;

            case "bannedSymbol":
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(name).Symbol;
                    if (symbol is null) continue;
                    var display = symbol.ToDisplayString();
                    var qualified = symbol.ContainingType is null
                        ? display
                        : symbol.ContainingType.ToDisplayString() + "." + symbol.Name;
                    if (!matcher.IsMatch(display) && !matcher.IsMatch(qualified)) continue;
                    yield return Position(name, qualified);
                }
                break;
        }
    }

    private static IEnumerable<SyntaxNode> Declarations(SyntaxNode root) => root.DescendantNodes().Where(node =>
        node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax
            or PropertyDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax
            or VariableDeclaratorSyntax);

    private static Hit Position(SyntaxNode node, string text)
    {
        var span = node.GetLocation().GetLineSpan();
        return new Hit(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, Shorten(text));
    }

    private static string Shorten(string text) =>
        text.Length <= 120 ? text : text[..117] + "…";

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    /// <summary>A substring match, or a regular expression when the value is written as <c>/pattern/</c>.</summary>
    private sealed class Matcher
    {
        private readonly string? _literal;
        private readonly Regex? _regex;

        private Matcher(string? literal, Regex? regex)
        {
            _literal = literal;
            _regex = regex;
        }

        public static Matcher? For(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var trimmed = value.Trim();
            if (trimmed.Length > 2 && trimmed.StartsWith('/') && trimmed.EndsWith('/'))
            {
                try { return new Matcher(null, new Regex(trimmed[1..^1], RegexOptions.Compiled | RegexOptions.CultureInvariant)); }
                catch (ArgumentException) { return null; }
            }

            return new Matcher(trimmed, null);
        }

        public bool IsMatch(string input) =>
            _regex?.IsMatch(input) ?? (_literal is not null && input.Contains(_literal, StringComparison.Ordinal));
    }

    private async Task<(string? File, List<CustomInspectionRule> Rules, List<string> Errors)> LoadAsync(string? path)
    {
        var file = path;
        if (string.IsNullOrWhiteSpace(file))
        {
            var solutionDirectory = _workspace.LoadedSolutionPath is { } solutionPath
                ? Path.GetDirectoryName(Path.GetFullPath(solutionPath))
                : Directory.GetCurrentDirectory();
            if (solutionDirectory is null) return (null, [], []);

            var candidate = Path.Combine(solutionDirectory, ".visualMCP", "inspections.json");
            if (!File.Exists(candidate)) return (null, [], []);
            file = candidate;
        }

        if (!File.Exists(file))
            return (Path.GetFullPath(file), [], [$"Rule file not found: {file}"]);

        string json;
        try { json = await File.ReadAllTextAsync(file); }
        catch (Exception exception) { return (Path.GetFullPath(file), [], [$"Could not read {file}: {exception.Message}"]); }

        return (Path.GetFullPath(file), Parse(json, out var errors), errors);
    }

    /// <summary>The parsing half: public so the format is pinned by a test without a solution.</summary>
    public static List<CustomInspectionRule> Parse(string json, out List<string> errors)
    {
        errors = [];
        var rules = new List<CustomInspectionRule>();

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            errors.Add($"Invalid JSON: {exception.Message}");
            return rules;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("rules", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("The document needs a \"rules\" array.");
            return rules;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"rule #{index}: not an object.");
                continue;
            }

            var id = Text(element, "id");
            var kind = Text(element, "kind");
            var value = Text(element, "value");
            var severity = Text(element, "severity").ToLowerInvariant();
            var message = Text(element, "message");

            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("VMC", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"rule #{index}: 'id' is required and must start with VMC (e.g. VMC1001).");
                continue;
            }

            if (!Kinds.Contains(kind, StringComparer.Ordinal))
            {
                errors.Add($"rule {id}: 'kind' must be one of {string.Join(", ", Kinds)}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"rule {id}: 'value' is required.");
                continue;
            }

            if (severity.Length == 0) severity = "warning";
            if (!Severities.Contains(severity, StringComparer.Ordinal))
            {
                errors.Add($"rule {id}: 'severity' must be error, warning or info.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(message))
                message = $"{kind} rule {id} matched.";

            if (rules.Any(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"rule {id}: the id is declared twice.");
                continue;
            }

            rules.Add(new CustomInspectionRule(id, kind, value, severity, message, Text(element, "title")));
        }

        return rules;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
