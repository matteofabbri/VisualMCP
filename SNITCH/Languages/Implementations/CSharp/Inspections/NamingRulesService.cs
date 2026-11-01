using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>A naming rule from <c>.editorconfig</c>: which symbols, which style, which severity.</summary>
public sealed record NamingRule(
    string Name,
    string Severity,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Accessibilities,
    string Capitalization,
    string Prefix,
    string Suffix,
    string Separator);

/// <summary>
/// Applies the repository's <c>dotnet_naming_rule</c> settings: reads the naming rules, styles and symbol
/// groups from the <c>.editorconfig</c> chain, names every violating type or member the way the style
/// asks, and — when asked — renames it through the normal rename engine (recorded and revertible).
/// </summary>
public interface INamingRulesService
{
    Task<object> RunAsync(string? projectName, bool apply, int maxResults, CancellationToken cancellationToken = default);
}

public sealed class NamingRulesService : INamingRulesService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IApplyRenameService _rename;

    public NamingRulesService(IRoslynWorkspace workspace, IApplyRenameService rename)
    {
        _workspace = workspace;
        _rename = rename;
    }

    public async Task<object> RunAsync(
        string? projectName = null, bool apply = false, int maxResults = 200, CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var solutionDirectory = _workspace.LoadedSolutionPath is { } solutionPath
            ? Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Directory.GetCurrentDirectory()
            : solution.Projects.Select(project => project.FilePath)
                .Where(path => path is not null)
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path!)))
                .FirstOrDefault(directory => directory is not null) ?? Directory.GetCurrentDirectory();

        var (files, settings) = ReadSettings(solutionDirectory);
        var rules = BuildRules(settings);
        if (rules.Count == 0)
            return new
            {
                error = "No dotnet_naming_rule is configured in the .editorconfig chain.",
                files,
                expected = new[]
                {
                    "dotnet_naming_rule.methods.severity = warning",
                    "dotnet_naming_rule.methods.symbols = methods",
                    "dotnet_naming_rule.methods.style = async_suffix",
                    "dotnet_naming_symbols.methods.applicable_kinds = method",
                    "dotnet_naming_style.async_suffix.capitalization = pascal_case",
                    "dotnet_naming_style.async_suffix.required_suffix = Async",
                },
            };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var limit = Math.Clamp(maxResults < 1 ? 200 : maxResults, 1, 5000);
        var violations = new List<Violation>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    if (model.GetDeclaredSymbol(node, cancellationToken) is not { } symbol) continue;
                    if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
                        continue;
                    if (symbol.IsImplicitlyDeclared) continue;
                    if (symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                    if (symbol.Name.Length == 0) continue;

                    var rule = Match(rules, symbol);
                    if (rule is null) continue;

                    var expected = ExpectedName(rule, symbol.Name);
                    if (string.Equals(expected, symbol.Name, StringComparison.Ordinal)) continue;

                    var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
                    violations.Add(new Violation(
                        rule.Name, rule.Severity, symbol.ToDisplayString(), symbol.Kind.ToString(),
                        symbol.DeclaredAccessibility.ToString(), symbol.Name, expected, project.Name,
                        location?.SourceTree?.FilePath ?? document.FilePath,
                        (location?.GetLineSpan().StartLinePosition.Line + 1) ?? 0,
                        symbol.ContainingType?.Name));
                }
            }
        }

        if (!apply)
            return new
            {
                files,
                rules = rules.Select(rule => new
                {
                    rule.Name, rule.Severity, kinds = rule.Kinds, accessibilities = rule.Accessibilities,
                    rule.Capitalization, rule.Prefix, rule.Suffix, rule.Separator,
                }).ToList(),
                projectFilter = projectName ?? "all",
                violationCount = violations.Count,
                violations = violations.Take(limit).Select(Project).ToList(),
                truncated = violations.Count > limit,
                applied = false,
                note = "Read-only: pass apply=true to rename the violations through apply_rename (each write is recorded and " +
                       "revertible). Only types and members are renamed; locals and parameters are skipped because a rename " +
                       "by name cannot tell two same-named locals apart.",
            };

        var applied = new List<object>();
        foreach (var violation in violations.Take(limit))
        {
            var result = await _rename.ApplyRename(
                violation.Current, violation.Expected, violation.ContainingType, renameOverloads: false,
                renameInStrings: false, renameInComments: false);
            applied.Add(new { violation.Symbol, from = violation.Current, to = violation.Expected, result });
        }

        return new
        {
            files,
            projectFilter = projectName ?? "all",
            violationCount = violations.Count,
            applied = applied.Count,
            results = applied,
            truncated = violations.Count > limit,
            note = "Each rename went through apply_rename: the compiler gate and the edit history apply, and a failure is " +
                   "reported in its result instead of stopping the batch.",
        };
    }

    private static object Project(Violation violation) => new
    {
        rule = violation.Rule,
        severity = violation.Severity,
        symbol = violation.Symbol,
        kind = violation.Kind,
        accessibility = violation.Accessibility,
        current = violation.Current,
        expected = violation.Expected,
        project = violation.Project,
        file = violation.File,
        violation.Line,
    };

    private sealed record Violation(
        string Rule, string Severity, string Symbol, string Kind, string Accessibility,
        string Current, string Expected, string Project, string? File, int Line, string? ContainingType);

    /// <summary>The first rule whose symbol group matches, as the IDE resolves them in configuration order.</summary>
    private static NamingRule? Match(IReadOnlyList<NamingRule> rules, ISymbol symbol)
    {
        foreach (var rule in rules)
        {
            if (rule.Kinds.Count > 0 && !rule.Kinds.Contains(KindOf(symbol), StringComparer.OrdinalIgnoreCase))
                continue;
            if (rule.Accessibilities.Count > 0 &&
                !rule.Accessibilities.Contains(AccessibilityOf(symbol.DeclaredAccessibility), StringComparer.OrdinalIgnoreCase) &&
                !rule.Accessibilities.Contains("all", StringComparer.OrdinalIgnoreCase))
                continue;

            return rule;
        }

        return null;
    }

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class => type.IsRecord ? "record" : "class",
            TypeKind.Struct => type.IsRecord ? "record_struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "type",
        },
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol field => field.IsConst ? "field" : "field",
        IEventSymbol => "event",
        _ => "other",
    };

    private static string AccessibilityOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected",
        _ => "internal",
    };

    /// <summary>The name the style asks for: prefix + words in the capitalization + suffix.</summary>
    public static string ExpectedName(NamingRule rule, string current)
    {
        var words = Words(current);
        var rendered = words.Select((word, index) => rule.Capitalization switch
        {
            "camel_case" => index == 0 ? word.ToLowerInvariant() : Capitalize(word),
            "all_upper" => word.ToUpperInvariant(),
            "all_lower" => word.ToLowerInvariant(),
            "first_word_upper" => index == 0 ? Capitalize(word) : word.ToLowerInvariant(),
            _ => Capitalize(word),
        });

        return rule.Prefix + string.Join(rule.Separator, rendered) + rule.Suffix;
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    /// <summary><c>order_items</c>, <c>OrderItems</c> and <c>HTTPClient</c> all split into their words.</summary>
    public static List<string> Words(string name)
    {
        var words = new List<string>();
        foreach (var part in name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var index = 1; index <= part.Length; index++)
            {
                var boundary = index == part.Length ||
                               (char.IsUpper(part[index]) && (char.IsLower(part[index - 1]) ||
                                                              (index + 1 < part.Length && char.IsLower(part[index + 1]))));

                if (!boundary) continue;
                var word = part[start..index];
                if (word.Length > 0) words.Add(word);
                start = index;
            }
        }

        return words;
    }

    /// <summary>Reads every <c>.editorconfig</c> under the solution; later (deeper) files override earlier keys.</summary>
    private static (List<string> Files, Dictionary<string, string> Settings) ReadSettings(string solutionDirectory)
    {
        var files = new List<string>();
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory
                     .EnumerateFiles(solutionDirectory, ".editorconfig", SearchOption.AllDirectories)
                     .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
                     .OrderBy(file => file.Length))
        {
            files.Add(file);
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('['))
                    continue;

                var equals = trimmed.IndexOf('=');
                if (equals <= 0) continue;
                var key = trimmed[..equals].Trim();
                var value = trimmed[(equals + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0) settings[key] = value;
            }
        }

        return (files, settings);
    }

    /// <summary>Builds one rule per complete <c>dotnet_naming_rule.&lt;name&gt;</c> group; public for tests.</summary>
    public static List<NamingRule> BuildRules(IReadOnlyDictionary<string, string> settings)
    {
        var rules = new List<NamingRule>();

        var names = settings.Keys
            .Where(key => key.StartsWith("dotnet_naming_rule.", StringComparison.OrdinalIgnoreCase))
            .Select(key => key["dotnet_naming_rule.".Length..].Split('.')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var severity = Value(settings, $"dotnet_naming_rule.{name}.severity");
            if (severity is null || severity is "none" or "silent") continue;

            var symbolsName = Value(settings, $"dotnet_naming_rule.{name}.symbols");
            var styleName = Value(settings, $"dotnet_naming_rule.{name}.style");
            if (symbolsName is null || styleName is null) continue;

            var kinds = (Value(settings, $"dotnet_naming_symbols.{symbolsName}.applicable_kinds") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(kind => kind.ToLowerInvariant())
                .ToList();

            var accessibilities = (Value(settings, $"dotnet_naming_symbols.{symbolsName}.applicable_accessibilities") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .ToList();

            var separator = Value(settings, $"dotnet_naming_style.{styleName}.word_separator") ?? string.Empty;

            rules.Add(new NamingRule(
                name,
                severity,
                kinds,
                accessibilities,
                (Value(settings, $"dotnet_naming_style.{styleName}.capitalization") ?? "pascal_case").ToLowerInvariant(),
                Value(settings, $"dotnet_naming_style.{styleName}.required_prefix") ?? string.Empty,
                Value(settings, $"dotnet_naming_style.{styleName}.required_suffix") ?? string.Empty,
                separator));
        }

        return rules;
    }

    private static string? Value(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;
}
