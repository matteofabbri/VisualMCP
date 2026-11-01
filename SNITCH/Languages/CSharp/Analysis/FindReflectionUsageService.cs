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

                    var api = ReflectiveLookups.Match(target);
                    if (api is null) continue;

                    var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var name = ReflectiveLookups.NameOf(argument, model);
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                    byApi[api] = byApi.GetValueOrDefault(api) + 1;

                    // typeof(T) resolves through the semantic model, which is exact for a nested or generic
                    // type; every other lookup has to be resolved by the name the call carries.
                    var resolved = argument is TypeOfExpressionSyntax typeOf
                        ? (ISymbol?)model.GetTypeInfo(typeOf.Type).Type
                        : null;
                    if (resolved is null && name is not null)
                        resolved = ReflectiveLookups.Resolve(compilation, name);

                    // A symbol from a referenced assembly is not in the solution: only a source declaration
                    // counts as resolved.
                    var inSolution = resolved is not null && resolved.Locations.Any(location => location.IsInSource);
                    var resolvedFile = inSolution
                        ? resolved!.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath
                        : null;

                    if (inSolution && name is not null) resolvedNames.Add(name);

                    findings.Add(new
                    {
                        Project = project.Name,
                        document.FilePath,
                        Line = line,
                        Api = api,
                        Target = name ?? "(runtime value)",
                        ResolvedInSolution = inSolution,
                        ResolvedSymbol = inSolution ? resolved!.ToDisplayString() : null,
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
                   "no reference in the call graph. find_unused_symbols applies the same names (its reflectionInvoked " +
                   "list) instead of calling those symbols dead. A name built at run time is not guessed, so what " +
                   "such a lookup reaches can still look dead.",
        };
    }
}
