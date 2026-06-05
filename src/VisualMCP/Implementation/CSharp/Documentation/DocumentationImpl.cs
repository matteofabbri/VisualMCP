using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Xml.Linq;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.CSharp.Documentation;

internal static class DocumentationImpl
{
    // ── get_xml_docs ──────────────────────────────────────────────────────────
    internal static async Task<object> GetXmlDocsAsync(string symbolName, string? containingType)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.All);

        var symbols = candidates
            .Where(s => containingType is null || (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0) return new { error = $"Symbol '{symbolName}' not found." };

        var results = symbols.Select(sym =>
        {
            var raw = sym.GetDocumentationCommentXml()?.Trim();
            if (string.IsNullOrEmpty(raw))
                return (object)new { Symbol = sym.ToDisplayString(), Kind = sym.Kind.ToString(), HasDocs = false };

            return (object)new { Symbol = sym.ToDisplayString(), Kind = sym.Kind.ToString(), HasDocs = true, RawXml = raw, Parsed = ParseXmlDoc(raw) };
        }).ToList();

        return new { symbolName, matchCount = results.Count, results };
    }

    private static object ParseXmlDoc(string xml)
    {
        try
        {
            var doc = XDocument.Parse($"<root>{xml}</root>");
            var root = doc.Root!;
            return new
            {
                Summary    = InnerText(root.Element("summary")),
                Returns    = InnerText(root.Element("returns")),
                Remarks    = InnerText(root.Element("remarks")),
                Params     = root.Elements("param").Select(e => new { Name = e.Attribute("name")?.Value, Text = e.Value.Trim() }).ToList(),
                TypeParams = root.Elements("typeparam").Select(e => new { Name = e.Attribute("name")?.Value, Text = e.Value.Trim() }).ToList(),
                Exceptions = root.Elements("exception").Select(e => new { Cref = e.Attribute("cref")?.Value, Text = e.Value.Trim() }).ToList(),
                SeeAlso    = root.Elements("seealso").Select(e => e.Attribute("cref")?.Value).ToList(),
            };
        }
        catch { return new { raw = xml }; }
    }

    private static string? InnerText(XElement? el) =>
        el is null ? null : el.Value.Trim() is { Length: > 0 } t ? t : null;

    // ── find_undocumented_public_api ──────────────────────────────────────────
    internal static async Task<object> FindUndocumentedAsync(string? projectName, bool includeProtected)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects.Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (projectName is not null && projects.Count == 0) return new { error = $"Project '{projectName}' not found." };

        var undocumented = new List<object>();

        foreach (var project in projects)
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    var sym = model.GetDeclaredSymbol(node);
                    if (sym is null || sym.IsImplicitlyDeclared) continue;

                    var access = sym.DeclaredAccessibility;
                    bool isPublic    = access == Accessibility.Public;
                    bool isProtected = access is Accessibility.Protected or Accessibility.ProtectedOrInternal;
                    if (!isPublic && !(includeProtected && isProtected)) continue;

                    if (sym is IMethodSymbol m && m.MethodKind is
                        MethodKind.PropertyGet or MethodKind.PropertySet or
                        MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.Destructor) continue;

                    var doc = sym.GetDocumentationCommentXml()?.Trim();
                    if (string.IsNullOrEmpty(doc))
                    {
                        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                        undocumented.Add(new
                        {
                            Symbol = sym.ToDisplayString(),
                            Kind = sym.Kind.ToString(),
                            Accessibility = access.ToString(),
                            Project = project.Name,
                            FilePath = loc?.SourceTree?.FilePath,
                            Line = loc?.GetLineSpan().StartLinePosition.Line + 1,
                        });
                    }
                }
            }

        var byKind = undocumented.GroupBy(s => ((dynamic)s).Kind.ToString()).ToDictionary(g => g.Key, g => g.Count());
        return new { projectFilter = projectName ?? "all", includeProtected, undocumentedCount = undocumented.Count, byKind, undocumented };
    }

    // ── read_project_docs ─────────────────────────────────────────────────────
    internal static async Task<object> ReadProjectDocsAsync(bool includeContent, string? nameFilter, int maxChars)
    {
        var svc = RoslynWorkspaceService.Instance;
        await svc.EnsureSolutionLoadedAsync();

        var root = svc.LoadedSolutionPath is { } sln ? Path.GetDirectoryName(sln)! : Directory.GetCurrentDirectory();
        var (resolvedRoot, docs) = ProjectDocsService.Get(root);

        var filtered = docs.Where(d => nameFilter is null || d.RelativePath.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var budget = Math.Clamp(maxChars, 1000, 400_000);
        var truncatedForBudget = false;

        var files = filtered.Select(d =>
        {
            string? content = null;
            if (includeContent && d.Content is not null)
            {
                if (budget <= 0) truncatedForBudget = true;
                else if (d.Content.Length > budget) { content = d.Content[..budget] + "\n…(truncated — content budget reached)"; budget = 0; truncatedForBudget = true; }
                else { content = d.Content; budget -= d.Content.Length; }
            }
            return new { d.RelativePath, d.SizeBytes, contentOmitted = d.ContentOmitted, content };
        }).ToList();

        return new { root = resolvedRoot, fileCount = filtered.Count, totalDocFiles = docs.Count, contentBudgetExhausted = truncatedForBudget, files };
    }
}
