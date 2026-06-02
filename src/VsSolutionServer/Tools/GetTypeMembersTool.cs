using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Workspace;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class GetTypeMembersTool
{
    [McpServerTool, Description("Return all members of a type (methods, properties, fields, events, constructors) with full signatures and XML docs. Requires LoadSolution first.")]
    public static async Task<object> GetTypeMembers(
        [Description("Full or partial type name (class, interface, struct, record, enum)")] string typeName,
        [Description("Include inherited members from base types (default: false)")] bool includeInherited = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var types = candidates.OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 0)
            return new { error = $"Type '{typeName}' not found in the loaded solution." };

        var results = types.Select(type =>
        {
            var members = (includeInherited
                ? type.GetMembers().Concat(GetInheritedMembers(type))
                : type.GetMembers())
                .Where(m => m.Kind != SymbolKind.NamedType)   // skip nested types
                .Select(m => new
                {
                    Name          = m.Name,
                    Kind          = m.Kind.ToString(),
                    Signature     = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Accessibility = m.DeclaredAccessibility.ToString(),
                    IsStatic      = m.IsStatic,
                    IsAbstract    = m.IsAbstract,
                    IsOverride    = m.IsOverride,
                    IsVirtual     = m.IsVirtual,
                    XmlDoc        = m.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
                    FilePath      = m.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    Line          = m.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line + 1,
                })
                .OrderBy(m => m.Kind)
                .ThenBy(m => m.Name)
                .ToList();

            var loc = type.Locations.FirstOrDefault(l => l.IsInSource);
            return new
            {
                Type          = type.ToDisplayString(),
                Kind          = type.TypeKind.ToString(),
                Accessibility = type.DeclaredAccessibility.ToString(),
                BaseType      = type.BaseType?.ToDisplayString(),
                Interfaces    = type.Interfaces.Select(i => i.ToDisplayString()).ToList(),
                XmlDoc        = type.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
                FilePath      = loc?.SourceTree?.FilePath,
                Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                MemberCount   = members.Count,
                Members       = members,
            };
        }).ToList();

        return new { typeName, matchCount = results.Count, results };
    }

    private static IEnumerable<ISymbol> GetInheritedMembers(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var m in current.GetMembers())
                yield return m;
            current = current.BaseType;
        }
    }
}
