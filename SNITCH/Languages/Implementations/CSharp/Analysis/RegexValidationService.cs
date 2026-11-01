using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>One regular-expression pattern that does not compile.</summary>
public sealed record RegexFinding(string File, int Line, string Pattern, string Message);

/// <summary>The answer of <c>find_regex_issues</c>.</summary>
public sealed record RegexValidationResult
{
    /// <summary>How many patterns were found.</summary>
    public int? Patterns { get; init; }

    /// <summary>How many of them do not parse.</summary>
    public int? Invalid { get; init; }

    /// <summary>The patterns that do not parse, with the parser's own message.</summary>
    public IReadOnlyList<RegexFinding>? Findings { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Validates the regular expressions a solution writes: every constant pattern handed to a
/// <c>Regex</c> constructor, to a <c>Regex</c> static method, or to a <c>[RegularExpression]</c>
/// attribute is compiled by the real .NET parser, so a pattern that only a running program would
/// reject is caught here. Read-only.
/// </summary>
public interface IRegexValidationService
{
    Task<RegexValidationResult> FindAsync(string? projectName, int maxResults,
        CancellationToken cancellationToken = default);
}

public sealed class RegexValidationService : IRegexValidationService
{
    private const string RegexTypeName = "System.Text.RegularExpressions.Regex";

    private readonly IRoslynWorkspace _workspace;

    public RegexValidationService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<RegexValidationResult> FindAsync(string? projectName, int maxResults,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new RegexValidationResult { Error = "No C# solution could be auto-located from the working directory." };

        var projects = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projects.Count == 0)
        {
            return new RegexValidationResult
            {
                Error = projectName is null ? "The solution holds no C# project." : $"Project '{projectName}' not found.",
            };
        }

        var findings = new List<RegexFinding>();
        var patterns = 0;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null) continue;
                if (await document.GetSemanticModelAsync(cancellationToken) is not { } model) continue;
                if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root) continue;

                foreach (var pattern in ConstantPatterns(root, model))
                {
                    patterns++;
                    if (Validate(pattern.Pattern) is not { } message) continue;

                    findings.Add(new RegexFinding(
                        document.FilePath,
                        root.SyntaxTree.GetLineSpan(pattern.Span).StartLinePosition.Line + 1,
                        pattern.Pattern,
                        message));

                    if (findings.Count >= maxResults) goto done;
                }
            }
        }

        done:
        return new RegexValidationResult
        {
            Patterns = patterns,
            Invalid = findings.Count,
            Findings = findings,
            Note = findings.Count == 0
                ? $"{patterns} constant pattern(s) compiled; none is rejected by the .NET parser."
                : $"{findings.Count} of {patterns} pattern(s) do not parse. A pattern built at run time is not checked.",
        };
    }

    /// <summary>Every constant pattern handed to Regex, with the span of the literal (or the attribute).</summary>
    private static IEnumerable<(string Pattern, Microsoft.CodeAnalysis.Text.TextSpan Span)> ConstantPatterns(
        CompilationUnitSyntax root, SemanticModel model)
    {
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var type = model.GetTypeInfo(creation).Type;
            if (type?.ToDisplayString() != RegexTypeName) continue;

            if (PatternArgument(model.GetSymbolInfo(creation).Symbol as IMethodSymbol, creation.ArgumentList?.Arguments)
                is { } expression && Constant(expression, model) is { } pattern)
            {
                yield return (pattern, expression.Span);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) continue;
            if (method.ContainingType?.ToDisplayString() != RegexTypeName) continue;

            if (PatternArgument(method, invocation.ArgumentList.Arguments) is { } expression
                && Constant(expression, model) is { } pattern)
            {
                yield return (pattern, expression.Span);
            }
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not { } expression) continue;

            var name = attribute.Name.ToString().Split('.').Last();
            if (!name.Contains("RegularExpression", StringComparison.Ordinal)) continue;

            if (Constant(expression, model) is { } pattern)
                yield return (pattern, attribute.Span);
        }
    }

    /// <summary>
    /// The argument the <c>pattern</c> parameter carries. Regex.IsMatch takes the input first and the
    /// pattern second, so "the first argument" would have checked the wrong string.
    /// </summary>
    private static ExpressionSyntax? PatternArgument(IMethodSymbol? method,
        SeparatedSyntaxList<ArgumentSyntax>? arguments)
    {
        if (method is null || arguments is not { } list) return null;

        for (var index = 0; index < method.Parameters.Length && index < list.Count; index++)
        {
            if (method.Parameters[index].Name == "pattern") return list[index].Expression;
        }

        return list.Count > 0 ? list[0].Expression : null;
    }

    private static string? Constant(ExpressionSyntax expression, SemanticModel model)
    {
        var constant = model.GetConstantValue(expression);
        return constant.HasValue && constant.Value is string text ? text : null;
    }

    /// <summary>The parser's message when the pattern does not compile, or null when it does.</summary>
    private static string? Validate(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }
}
