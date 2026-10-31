using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Lists the places where the solution looks something up reflectively by name — <c>Activator.CreateInstance</c>,
/// <c>Type.GetType</c>, <c>Assembly.Load/LoadFrom</c>, <c>Type.GetMethod/GetProperty/GetField/GetMember</c>,
/// <c>Delegate.CreateDelegate</c> — and resolves the names it can against the solution's own declarations.
/// That is the honest boundary of a call graph: reflection is not followed, but it is enumerated, so a
/// dead-code result can be checked against the reflective entry points instead of being trusted blindly.
/// </summary>
public interface IFindReflectionUsageService
{
    Task<object> FindReflectionUsage(string? projectName);
}

public sealed class FindReflectionUsageService : IFindReflectionUsageService
{
    private readonly IRoslynWorkspace _workspace;
    public FindReflectionUsageService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <summary>The reflective APIs worth reporting: containing type, member, and the label used in the answer.</summary>
    private static readonly (string Type, string Member, string Api)[] ReflectiveApis =
    [
        ("Activator", "CreateInstance", "Activator.CreateInstance"),
        ("Type", "GetType", "Type.GetType"),
        ("Type", "GetMethod", "Type.GetMethod"),
        ("Type", "GetProperty", "Type.GetProperty"),
        ("Type", "GetField", "Type.GetField"),
        ("Type", "GetMember", "Type.GetMember"),
        ("Type", "GetConstructor", "Type.GetConstructor"),
        ("Type", "GetMethods", "Type.GetMethods"),
        ("Type", "GetProperties", "Type.GetProperties"),
        ("Type", "GetFields", "Type.GetFields"),
        ("Type", "GetCustomAttribute", "Type.GetCustomAttribute"),
        ("Type", "GetCustomAttributes", "Type.GetCustomAttributes"),
        ("Type", "InvokeMember", "Type.InvokeMember"),
        ("Assembly", "GetType", "Assembly.GetType"),
        ("Assembly", "Load", "Assembly.Load"),
        ("Assembly", "LoadFrom", "Assembly.LoadFrom"),
        ("Assembly", "LoadFile", "Assembly.LoadFile"),
        ("Delegate", "CreateDelegate", "Delegate.CreateDelegate"),
    ];

    public async Task<object> FindReflectionUsage(string? projectName = null)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var findings = new List<object>();
        var byApi = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolvedNames = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var target = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (target is null) continue;

                    var api = Match(target);
                    if (api is null) continue;

                    var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var name = NameOf(argument);
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                    byApi[api] = byApi.GetValueOrDefault(api) + 1;

                    string? resolvedSymbol = null;
                    string? resolvedFile = null;
                    if (name is not null && LooksLikeAnIdentifier(name))
                    {
                        var match = compilation.GetSymbolsWithName(candidate => candidate == name,
                                SymbolFilter.TypeAndMember, CancellationToken.None)
                            .FirstOrDefault();

                        if (match is not null)
                        {
                            resolvedSymbol = match.ToDisplayString();
                            resolvedFile = match.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
                            resolvedNames.Add(name);
                        }
                    }

                    findings.Add(new
                    {
                        Project = project.Name,
                        document.FilePath,
                        Line = line,
                        Api = api,
                        Target = name ?? "(runtime value)",
                        ResolvedInSolution = resolvedSymbol is not null,
                        ResolvedSymbol = resolvedSymbol,
                        ResolvedFile = resolvedFile,
                    });
                }
            }
        }

        return new
        {
            projectFilter = projectName ?? "all",
            totalLookups = findings.Count,
            byApi,
            resolvedInSolution = resolvedNames.Count,
            resolvedNames = resolvedNames.ToList(),
            findings,
            note = "Reflection is enumerated, not followed: a symbol reached only by one of these lookups still has " +
                   "no reference in the call graph. Names under resolvedNames are the ones a dead-code result must " +
                   "treat as live by convention.",
        };
    }

    private static string? Match(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType is null) return null;

        var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns is not ("System" or "System.Reflection")) return null;

        foreach (var (type, member, api) in ReflectiveApis)
            if (containingType.Name == type && method.Name == member)
                return api;

        return null;
    }

    /// <summary>The name the lookup uses: a string literal, <c>nameof(…)</c>, or <c>typeof(…)</c>.</summary>
    private static string? NameOf(ExpressionSyntax? expression) => expression switch
    {
        null => null,
        LiteralExpressionSyntax literal when literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)
            => literal.Token.ValueText,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } invocation
            when invocation.ArgumentList.Arguments.Count == 1
            => invocation.ArgumentList.Arguments[0].Expression.ToString(),
        TypeOfExpressionSyntax typeOf => typeOf.Type.ToString(),
        _ => null,
    };

    private static bool LooksLikeAnIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');
}
