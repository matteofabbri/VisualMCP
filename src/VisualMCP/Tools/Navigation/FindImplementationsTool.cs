using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool, Description("Find all types that implement a given interface, and all members that implement its methods/properties. Requires LoadSolution to have been called first.")]
    public static async Task<object> FindImplementations(
        [Description("Full or partial name of the interface (e.g. 'IMyService' or 'MyNamespace.IMyService')")] string interfaceName)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        // 1. Locate the interface symbol across all projects
        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)
                 || name.Equals(StripLeadingI(interfaceName), StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var interfaces = candidates
            .OfType<INamedTypeSymbol>()
            .Where(s => s.TypeKind == TypeKind.Interface)
            .ToList();

        if (interfaces.Count == 0)
            return new { error = $"No interface named '{interfaceName}' found in the loaded solution." };

        var results = new List<object>();

        foreach (var iface in interfaces)
        {
            // 2. Find all types that implement this interface
            var implementors = await SymbolFinder.FindImplementationsAsync(iface, solution);

            var typeResults = new List<object>();
            foreach (var impl in implementors.OfType<INamedTypeSymbol>())
            {
                var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);

                // 3. For each implementing type, find which members implement each interface member
                var memberImpls = new List<object>();
                foreach (var ifaceMember in iface.GetMembers())
                {
                    var implMember = impl.FindImplementationForInterfaceMember(ifaceMember);
                    if (implMember is null) continue;

                    var mLoc = implMember.Locations.FirstOrDefault(l => l.IsInSource);
                    memberImpls.Add(new
                    {
                        InterfaceMember = ifaceMember.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        ImplementingMember = implMember.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FilePath = mLoc?.SourceTree?.FilePath,
                        Line = mLoc?.GetLineSpan().StartLinePosition.Line + 1,
                        IsExplicit = IsExplicitImplementation(implMember),
                    });
                }

                typeResults.Add(new
                {
                    Name = impl.ToDisplayString(),
                    Kind = impl.TypeKind.ToString(),
                    FilePath = loc?.SourceTree?.FilePath,
                    Line = loc?.GetLineSpan().StartLinePosition.Line + 1,
                    MemberImplementations = memberImpls,
                });
            }

            results.Add(new
            {
                Interface = iface.ToDisplayString(),
                FilePath = iface.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                MemberCount = iface.GetMembers().Length,
                ImplementorCount = typeResults.Count,
                Implementors = typeResults,
            });
        }

        return new
        {
            interfaceName,
            matchedInterfaces = results.Count,
            results
        };
    }

    private static bool IsExplicitImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol   m => m.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol p => p.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol    e => e.ExplicitInterfaceImplementations.Length > 0,
        _                 => false,
    };

    private static string StripLeadingI(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;
}
