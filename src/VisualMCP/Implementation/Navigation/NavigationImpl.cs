using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Navigation;

internal static class NavigationImpl
{
    private static async Task<(Microsoft.CodeAnalysis.Solution? sln, object? err)> SolutionAsync()
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return (null, new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." });
        return (solution, null);
    }

    // ── find_symbol ───────────────────────────────────────────────────────────
    internal static async Task<object> FindSymbolAsync(string symbolName, bool partialMatch)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        IEnumerable<ISymbol> symbols = partialMatch
            ? await SymbolFinder.FindSourceDeclarationsAsync(solution!, name => name.Contains(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.TypeAndMember)
            : await SymbolFinder.FindSourceDeclarationsAsync(solution!, symbolName, ignoreCase: true, filter: SymbolFilter.TypeAndMember);

        var matches = symbols.Select(s =>
        {
            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            return new
            {
                Name = s.Name,
                Kind = s.Kind.ToString(),
                ContainingType = s.ContainingType?.ToDisplayString(),
                ContainingNamespace = s.ContainingNamespace?.ToDisplayString(),
                FullyQualifiedName = s.ToDisplayString(),
                FilePath = loc?.SourceTree?.FilePath,
                Line = loc?.GetLineSpan().StartLinePosition.Line + 1,
            };
        }).ToList();

        return new { symbolName, partialMatch, matchCount = matches.Count, matches };
    }

    // ── find_references ───────────────────────────────────────────────────────
    internal static async Task<object> FindReferencesAsync(string symbolName, string? kind)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
            solution!, name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.All);

        if (kind is not null)
            declarations = declarations.Where(s => s.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase));

        var symbols = declarations.ToList();
        if (symbols.Count == 0) return new { error = $"No symbol named '{symbolName}' found." };

        var results = new List<object>();
        foreach (var symbol in symbols)
        {
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution!);
            foreach (var refGroup in refs)
            {
                var locations = refGroup.Locations.Select(l => new
                {
                    FilePath = l.Document.FilePath,
                    Line     = l.Location.GetLineSpan().StartLinePosition.Line + 1,
                    Column   = l.Location.GetLineSpan().StartLinePosition.Character + 1,
                }).OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList();

                results.Add(new { Symbol = symbol.ToDisplayString(), Kind = symbol.Kind.ToString(), RefCount = locations.Count, References = locations });
            }
        }

        return new { symbolName, symbolsFound = symbols.Count, results };
    }

    // ── find_implementations ──────────────────────────────────────────────────
    internal static async Task<object> FindImplementationsAsync(string interfaceName)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution!,
            name => name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) || name.Equals(StripLeadingI(interfaceName), StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var interfaces = candidates.OfType<INamedTypeSymbol>().Where(s => s.TypeKind == TypeKind.Interface).ToList();
        if (interfaces.Count == 0) return new { error = $"No interface named '{interfaceName}' found in the loaded solution." };

        var results = new List<object>();
        foreach (var iface in interfaces)
        {
            var implementors = await SymbolFinder.FindImplementationsAsync(iface, solution!);
            var typeResults = new List<object>();
            foreach (var impl in implementors.OfType<INamedTypeSymbol>())
            {
                var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
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

        return new { interfaceName, matchedInterfaces = results.Count, results };
    }

    private static bool IsExplicitImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol p => p.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol e => e.ExplicitInterfaceImplementations.Length > 0,
        _ => false,
    };

    private static string StripLeadingI(string name) =>
        name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    // ── find_callers ──────────────────────────────────────────────────────────
    internal static async Task<object> FindCallersAsync(string symbolName, string? containingType)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution!, name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Member);

        var symbols = candidates.Where(s => containingType is null || (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (symbols.Count == 0) return new { error = $"No member named '{symbolName}' found." };

        var results = new List<object>();
        foreach (var symbol in symbols)
        {
            var callers = await SymbolFinder.FindCallersAsync(symbol, solution!);
            var callerList = callers.Where(c => c.IsDirect).Select(c =>
            {
                var callerLoc = c.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                var callSites = c.Locations.Select(l => new { FilePath = l.SourceTree?.FilePath, Line = l.GetLineSpan().StartLinePosition.Line + 1 }).ToList();
                return new
                {
                    Caller = c.CallingSymbol.ToDisplayString(),
                    CallerKind = c.CallingSymbol.Kind.ToString(),
                    FilePath = callerLoc?.SourceTree?.FilePath,
                    Line = callerLoc?.GetLineSpan().StartLinePosition.Line + 1,
                    CallSites = callSites,
                };
            }).OrderBy(c => c.Caller).ToList();

            results.Add(new { Symbol = symbol.ToDisplayString(), Kind = symbol.Kind.ToString(), CallerCount = callerList.Count, Callers = callerList });
        }

        return new { symbolName, matchCount = results.Count, results };
    }

    // ── find_derived_types ────────────────────────────────────────────────────
    internal static async Task<object> FindDerivedTypesAsync(string typeName, bool transitive)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution!, name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type);

        var types = candidates.OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 0) return new { error = $"Type '{typeName}' not found in the loaded solution." };

        var results = new List<object>();
        foreach (var type in types)
        {
            IEnumerable<INamedTypeSymbol> derived = type.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindDerivedInterfacesAsync(type, solution!, transitive)
                : await SymbolFinder.FindDerivedClassesAsync(type, solution!, transitive);

            var derivedList = derived.Select(d =>
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                return new
                {
                    Name = d.ToDisplayString(),
                    Kind = d.TypeKind.ToString(),
                    Accessibility = d.DeclaredAccessibility.ToString(),
                    IsAbstract = d.IsAbstract,
                    IsSealed = d.IsSealed,
                    FilePath = loc?.SourceTree?.FilePath,
                    Line = loc?.GetLineSpan().StartLinePosition.Line + 1,
                };
            }).OrderBy(d => d.Name).ToList();

            results.Add(new { BaseType = type.ToDisplayString(), Kind = type.TypeKind.ToString(), DerivedCount = derivedList.Count, Transitive = transitive, DerivedTypes = derivedList });
        }

        return new { typeName, matchCount = results.Count, results };
    }

    // ── get_type_members ──────────────────────────────────────────────────────
    internal static async Task<object> GetTypeMembersAsync(string typeName, bool includeInherited)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution!, name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type);

        var types = candidates.OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 0) return new { error = $"Type '{typeName}' not found in the loaded solution." };

        var results = types.Select(type =>
        {
            var members = (includeInherited ? type.GetMembers().Concat(GetInheritedMembers(type)) : type.GetMembers())
                .Where(m => m.Kind != SymbolKind.NamedType)
                .Select(m => new
                {
                    Name = m.Name,
                    Kind = m.Kind.ToString(),
                    Signature = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Accessibility = m.DeclaredAccessibility.ToString(),
                    IsStatic = m.IsStatic,
                    IsAbstract = m.IsAbstract,
                    IsOverride = m.IsOverride,
                    IsVirtual = m.IsVirtual,
                    XmlDoc = m.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
                    FilePath = m.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    Line = m.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line + 1,
                }).OrderBy(m => m.Kind).ThenBy(m => m.Name).ToList();

            var loc = type.Locations.FirstOrDefault(l => l.IsInSource);
            return new
            {
                Type = type.ToDisplayString(),
                Kind = type.TypeKind.ToString(),
                Accessibility = type.DeclaredAccessibility.ToString(),
                BaseType = type.BaseType?.ToDisplayString(),
                Interfaces = type.Interfaces.Select(i => i.ToDisplayString()).ToList(),
                XmlDoc = type.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
                FilePath = loc?.SourceTree?.FilePath,
                Line = loc?.GetLineSpan().StartLinePosition.Line + 1,
                MemberCount = members.Count,
                Members = members,
            };
        }).ToList();

        return new { typeName, matchCount = results.Count, results };
    }

    private static IEnumerable<ISymbol> GetInheritedMembers(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var m in current.GetMembers()) yield return m;
            current = current.BaseType;
        }
    }

    // ── get_symbol_info ───────────────────────────────────────────────────────
    internal static async Task<object> GetSymbolInfoAsync(string filePath, int line, int column)
    {
        var (solution, err) = await SolutionAsync();
        if (err is not null) return err;

        var document = solution!.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"File '{filePath}' is not part of the loaded solution." };

        var text = await document.GetTextAsync();
        if (line < 1 || line > text.Lines.Count)
            return new { error = $"Line {line} is out of range (file has {text.Lines.Count} lines)." };

        var textLine = text.Lines[line - 1];
        var col = Math.Clamp(column - 1, 0, Math.Max(0, textLine.End - textLine.Start - 1));
        var position = textLine.Start + col;

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null) return new { error = "Could not get semantic model for this document." };

        var token = root.FindToken(position);
        var node = token.Parent;
        var declared = node is not null ? model.GetDeclaredSymbol(node) : null;
        var symInfo = node is not null ? model.GetSymbolInfo(node) : default;
        var typeInfo = node is not null ? model.GetTypeInfo(node) : default;
        var symbol = declared ?? symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault();

        if (symbol is null)
            return new { filePath, line, column, tokenText = token.Text, info = "No symbol resolved at this position.", inferredType = typeInfo.Type?.ToDisplayString() };

        var defLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return new
        {
            filePath, line, column,
            tokenText = token.Text,
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            containingType = symbol.ContainingType?.ToDisplayString(),
            containingNs = symbol.ContainingNamespace?.ToDisplayString(),
            accessibility = symbol.DeclaredAccessibility.ToString(),
            isStatic = symbol.IsStatic,
            inferredType = typeInfo.Type?.ToDisplayString(),
            xmlDoc = symbol.GetDocumentationCommentXml()?.Trim() is { Length: > 0 } d ? d : null,
            definedAt = defLoc is null ? null : new { filePath = defLoc.SourceTree?.FilePath, line = defLoc.GetLineSpan().StartLinePosition.Line + 1 },
        };
    }
}
