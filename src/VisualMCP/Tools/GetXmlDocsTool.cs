using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Xml.Linq;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class GetXmlDocsTool
{
    [McpServerTool, Description("Return the XML documentation comment for a named symbol, parsed into structured fields (summary, params, returns, exceptions, remarks). Requires LoadSolution first.")]
    public static async Task<object> GetXmlDocs(
        [Description("Symbol name (type, method, property, field, event)")] string symbolName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.All);

        var symbols = candidates
            .Where(s => containingType is null ||
                        (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
            return new { error = $"Symbol '{symbolName}' not found." };

        var results = symbols.Select(sym =>
        {
            var raw = sym.GetDocumentationCommentXml()?.Trim();
            if (string.IsNullOrEmpty(raw))
                return (object)new { Symbol = sym.ToDisplayString(), Kind = sym.Kind.ToString(), HasDocs = false };

            var parsed = ParseXmlDoc(raw);
            return (object)new
            {
                Symbol   = sym.ToDisplayString(),
                Kind     = sym.Kind.ToString(),
                HasDocs  = true,
                RawXml   = raw,
                Parsed   = parsed,
            };
        }).ToList();

        return new { symbolName, matchCount = results.Count, results };
    }

    private static object ParseXmlDoc(string xml)
    {
        try
        {
            // Wrap in root element in case there are multiple top-level nodes
            var doc = XDocument.Parse($"<root>{xml}</root>");
            var root = doc.Root!;

            return new
            {
                Summary    = InnerText(root.Element("summary")),
                Returns    = InnerText(root.Element("returns")),
                Remarks    = InnerText(root.Element("remarks")),
                Params     = root.Elements("param").Select(e => new
                {
                    Name = e.Attribute("name")?.Value,
                    Text = e.Value.Trim(),
                }).ToList(),
                TypeParams = root.Elements("typeparam").Select(e => new
                {
                    Name = e.Attribute("name")?.Value,
                    Text = e.Value.Trim(),
                }).ToList(),
                Exceptions = root.Elements("exception").Select(e => new
                {
                    Cref = e.Attribute("cref")?.Value,
                    Text = e.Value.Trim(),
                }).ToList(),
                SeeAlso    = root.Elements("seealso").Select(e => e.Attribute("cref")?.Value).ToList(),
            };
        }
        catch
        {
            return new { raw = xml };
        }
    }

    private static string? InnerText(XElement? el) =>
        el is null ? null : el.Value.Trim() is { Length: > 0 } t ? t : null;
}
