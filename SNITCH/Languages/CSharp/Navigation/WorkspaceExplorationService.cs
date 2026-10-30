using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Navigation;

/// <summary>
/// The exploration tools the IDE gives as windows and this server gives as data: the ASP.NET
/// endpoints (attribute routing and minimal APIs), the TODO/FIXME comments, a fuzzy file search, and a
/// stack trace mapped back to source. Syntax-level, so each one answers in a single pass.
/// </summary>
public interface IWorkspaceExplorationService
{
    Task<object> FindEndpointsAsync(string? projectName, int maxResults);
    Task<object> FindTodosAsync(string? projectName, int maxResults);
    Task<object> FindFileAsync(string query, int maxResults);
    Task<object> AnalyzeStackTraceAsync(string stackTrace, int maxResults);
}

public sealed class WorkspaceExplorationService : IWorkspaceExplorationService
{
    private static readonly string[] Verbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];
    private static readonly string[] Markers = ["TODO", "FIXME", "HACK", "XXX"];
    private static readonly Regex StackFrame = new(
        @"^\s+at (?<method>.+?)(?: in (?<file>.+?):line (?<line>\d+))?$", RegexOptions.Compiled);

    private readonly IRoslynWorkspace _workspace;
    public WorkspaceExplorationService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── endpoints ───────────────────────────────────────────────────────────
    public async Task<object> FindEndpointsAsync(string? projectName = null, int maxResults = 500)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        maxResults = Math.Clamp(maxResults, 1, 5000);
        var endpoints = new List<object>();
        var truncated = false;

        foreach (var project in Projects(solution, projectName))
        {
            foreach (var document in project.Documents.Where(document => document.SupportsSyntaxTree))
            {
                if (endpoints.Count >= maxResults) { truncated = true; break; }

                var root = await document.GetSyntaxRootAsync() as CompilationUnitSyntax;
                if (root is null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var verb = VerbOf(method);
                    if (verb is null) continue;

                    var route = RouteOf(method) ?? string.Empty;
                    endpoints.Add(new
                    {
                        kind = "controller",
                        verb,
                        route,
                        method = method.Identifier.ValueText,
                        containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText,
                        file = document.FilePath,
                        line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;

                    var name = access.Name.Identifier.ValueText;
                    if (name is not ("MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch")) continue;
                    if (invocation.ArgumentList.Arguments.Count == 0) continue;

                    var pattern = invocation.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax;
                    if (pattern is null) continue;

                    if (endpoints.Count >= maxResults) { truncated = true; break; }

                    endpoints.Add(new
                    {
                        kind = "minimalApi",
                        verb = name[3..].ToUpperInvariant(),
                        route = pattern.Token.ValueText,
                        method = access.Expression.ToString(),
                        containingType = (string?)null,
                        file = document.FilePath,
                        line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }
            }
        }

        return new
        {
            projectFilter = projectName ?? "all",
            count = endpoints.Count,
            truncated,
            byVerb = endpoints
                .GroupBy(endpoint => ((dynamic)endpoint).verb?.ToString() ?? "?")
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            endpoints,
            note = "Attribute routing and minimal APIs from the syntax. A route composed from a constant, a " +
                   "convention or a MapGroup prefix is reported as far as its literal shows.",
        };
    }

    // ── TODO / FIXME ────────────────────────────────────────────────────────
    public async Task<object> FindTodosAsync(string? projectName = null, int maxResults = 500)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        maxResults = Math.Clamp(maxResults, 1, 5000);
        var todos = new List<object>();

        foreach (var project in Projects(solution, projectName))
        {
            foreach (var document in project.Documents.Where(document => document.SupportsSyntaxTree))
            {
                if (todos.Count >= maxResults) break;

                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                foreach (var trivia in root.DescendantTrivia())
                {
                    if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                        !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;

                    var text = trivia.ToString();
                    var marker = Markers.FirstOrDefault(candidate =>
                        text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
                    if (marker is null) continue;

                    if (todos.Count >= maxResults) break;

                    todos.Add(new
                    {
                        marker,
                        text = text.TrimStart('/', '*', ' ').Trim(),
                        file = document.FilePath,
                        line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    });
                }
            }
        }

        return new
        {
            projectFilter = projectName ?? "all",
            count = todos.Count,
            byMarker = todos
                .GroupBy(todo => ((dynamic)todo).marker?.ToString() ?? "?")
                .ToDictionary(group => group.Key, group => group.Count()),
            todos,
        };
    }

    // ── files by name ───────────────────────────────────────────────────────
    public async Task<object> FindFileAsync(string query, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "A name or a fragment is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        maxResults = Math.Clamp(maxResults, 1, 500);

        var matches = new List<(int Rank, string Path)>();
        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            if (document.FilePath is null) continue;

            var fileName = Path.GetFileName(document.FilePath);
            var rank = MatchRank(query.Trim(), fileName);
            if (rank > 0) matches.Add((rank, document.FilePath));
        }

        var ordered = matches
            .GroupBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Rank).First())
            .OrderByDescending(match => match.Rank)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(match => new
            {
                file = match.Path,
                name = Path.GetFileName(match.Path),
                matchKind = match.Rank switch { 4 => "exact", 3 => "prefix", 2 => "camelHumps", _ => "contains" },
            })
            .ToList();

        return new
        {
            query,
            count = ordered.Count,
            files = ordered,
            note = "Files of the loaded solution. Ranking: exact name, then prefix, then CamelHumps " +
                   "(e.g. 'IOM' finds InlineFieldService), then contains.",
        };
    }

    // ── stack trace → source ────────────────────────────────────────────────
    public async Task<object> AnalyzeStackTraceAsync(string stackTrace, int maxResults = 100)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return new { error = "A stack trace is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        maxResults = Math.Clamp(maxResults, 1, 500);
        var frames = new List<object>();
        var unresolved = 0;

        foreach (var line in stackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = StackFrame.Match(line);
            if (!match.Success) continue;
            if (frames.Count >= maxResults) break;

            var methodName = match.Groups["method"].Value;
            string? file = match.Groups["file"].Success ? match.Groups["file"].Value.Trim() : null;
            int? lineNumber = match.Groups["line"].Success ? int.TryParse(match.Groups["line"].Value, out var parsed) ? parsed : null : null;

            if (file is null)
            {
                var resolved = await ResolveFrameAsync(solution, methodName);
                if (resolved is null) { unresolved++; continue; }
                (file, lineNumber) = resolved.Value;
            }

            frames.Add(new { method = methodName, file, line = lineNumber, raw = line.Trim() });
        }

        return new
        {
            frames = frames.Count,
            resolved = frames.Count - unresolved,
            unresolved,
            byFile = frames
                .GroupBy(frame => ((dynamic)frame).file?.ToString() ?? "?")
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            stack = frames,
            note = "A frame with an explicit file:line is taken as given; a frame without one is looked up " +
                   "by method name in the solution, so the first declaration wins. Framework frames stay unresolved.",
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static IEnumerable<Project> Projects(Solution solution, string? projectName) =>
        projectName is null
            ? solution.Projects
            : solution.Projects.Where(project => project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

    private static string? VerbOf(MethodDeclarationSyntax method)
    {
        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString().Split('.').Last();
            if (!name.StartsWith("Http", StringComparison.Ordinal)) continue;

            var verb = name[4..];
            if (Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) return verb.ToUpperInvariant();
        }

        // [Route] alone is an endpoint: the verb comes from the method name convention in MVC.
        return method.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Split('.').Last() is "Route")
            ? "ANY"
            : null;
    }

    private static string? RouteOf(MethodDeclarationSyntax method)
    {
        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
        {
            if (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal &&
                literal.Token.ValueText.Length > 0 &&
                attribute.Name.ToString().Contains("Route", StringComparison.OrdinalIgnoreCase))
                return literal.Token.ValueText;

            if (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax httpLiteral &&
                attribute.Name.ToString().StartsWith("Http", StringComparison.Ordinal))
                return httpLiteral.Token.ValueText;
        }

        return null;
    }

    private static int MatchRank(string query, string name)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (CamelHumps(name).Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static string CamelHumps(string name) => new(name.Where(char.IsUpper).ToArray());

    private static async Task<(string File, int? Line)?> ResolveFrameAsync(Solution solution, string frame)
    {
        // "Namespace.Type.Method(int)" → Method
        var name = frame;
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) name = name[(lastDot + 1)..];
        if (name.Length == 0) return null;

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, candidate => candidate.Equals(name, StringComparison.Ordinal), SymbolFilter.Member);

        var symbol = declarations.FirstOrDefault();
        var location = symbol?.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location?.SourceTree?.FilePath is null) return null;

        return (location.SourceTree.FilePath, location.GetLineSpan().StartLinePosition.Line + 1);
    }
}
