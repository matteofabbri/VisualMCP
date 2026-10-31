using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// Turns one document into the rows the index stores: every declaration with its signature and span,
/// every use with the kind of use and the member it happens inside — which is what makes "who calls
/// this" and "what does this use" a query instead of a scan — plus the relationships the syntax does
/// not name: an override and an interface implementation. The same walk also lowers the tree into
/// metacode and stores one tree per declaration.
/// </summary>
/// <remarks>
/// Only members and types are indexed. Locals, parameters and type parameters are left out on purpose:
/// the question is "who uses this member", not "where is this variable used". A use inside a local
/// function or a property accessor is attributed to the nearest declared member, so a caller always
/// has a declaration to point at.
/// </remarks>
public sealed class SymbolIndexBuilder
{
    private readonly IReadOnlyList<ISymbolIndexFrontend> _frontends;
    private readonly Func<MetacodeTranslator> _translator;

    public SymbolIndexBuilder(
        IEnumerable<ISymbolIndexFrontend>? frontends = null, Func<MetacodeTranslator>? translator = null)
    {
        _frontends = frontends?.ToList() ?? [new CSharpSymbolIndexFrontend(), new VisualBasicSymbolIndexFrontend()];

        // The frontends keep per-document state (their diagnostics), so one shared translator is not
        // thread-safe while the build runs a document per thread: each document gets its own, with the
        // readers for every language the index accepts.
        _translator = translator ?? (() => new MetacodeTranslator([new CSharpFrontend(), new VisualBasicFrontend()]));
    }

    /// <summary>The frontend that understands the tree, or null when none does.</summary>
    public ISymbolIndexFrontend? FrontendFor(SyntaxTree tree) => _frontends.FirstOrDefault(f => f.CanHandle(tree));

    /// <summary>
    /// The content hash a document is stamped with. It is read before the expensive work, so a build
    /// can skip a document whose text did not change without building its semantic model.
    /// </summary>
    public async Task<string> HashAsync(Document document, CancellationToken cancellationToken = default)
    {
        var text = await document.GetTextAsync(cancellationToken);
        return SymbolIndexStore.ContentHash(text.ToString());
    }

    /// <summary>Reads a document and returns what it contributes, or null for a language with no frontend.</summary>
    public async Task<SymbolDocumentFacts?> BuildAsync(
        Document document, string project, string hash, CancellationToken cancellationToken = default)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null) return null;

        var frontend = FrontendFor(model.SyntaxTree);
        if (frontend is null) return null;

        var path = document.FilePath ?? model.SyntaxTree.FilePath;
        return Analyze(model.SyntaxTree, model, frontend, project, path, hash);
    }

    /// <summary>The same walk over a tree that is already in memory (used by the tests and by callers with a model).</summary>
    public SymbolDocumentFacts Analyze(
        SyntaxTree tree,
        SemanticModel model,
        ISymbolIndexFrontend frontend,
        string project,
        string path,
        string hash)
    {
        var root = tree.GetRoot();
        var declarations = new List<SymbolDeclaration>();
        var usages = new List<SymbolUsage>();
        var declared = new List<(SyntaxNode Node, ISymbol Symbol, string Key)>();

        foreach (var node in root.DescendantNodes())
        {
            if (!frontend.IsDeclaration(node)) continue;
            if (frontend.DeclaredSymbol(node, model) is not { } symbol || !IsIndexable(symbol)) continue;

            var key = SymbolKeys.For(symbol);
            declared.Add((node, symbol, key));
            declarations.Add(Declare(node, symbol, frontend, project, path, hash));
        }

        foreach (var node in root.DescendantNodes())
        {
            if (!frontend.IsReferenceName(node)) continue;

            var info = model.GetSymbolInfo(node);
            var symbol = info.Symbol
                         ?? model.GetTypeInfo(node).Type
                         ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is null) continue;

            // An attribute name binds to the constructor its arguments select; the reference a reader
            // makes is to the attribute type, which is also what the declaration index has a row for.
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } attribute }
                && frontend.IsAttributeName(node))
                symbol = attribute;

            if (!IsIndexable(symbol)) continue;

            var kind = frontend.Classify(node, symbol, model);
            var container = ContainerKeyAt(model, node, frontend);
            usages.Add(Use(node, symbol, kind, container, path, hash));
        }

        usages.AddRange(Relationships(declared, path, hash));

        return new SymbolDocumentFacts(path, project, hash, declarations, usages, Trees(tree, declarations, path, hash));
    }

    /// <summary>
    /// The same tree, as metacode: the translator lowers the Roslyn syntax, then every declaration of
    /// the model is paired with the symbol the index knows it by — so a lookup can return the
    /// pseudocode of exactly the declaration it found, and a type carries its members inside.
    /// </summary>
    private IReadOnlyList<SymbolMetacode> Trees(
        SyntaxTree tree, IReadOnlyList<SymbolDeclaration> declarations, string path, string hash)
    {
        MetacodeDocument document;
        try
        {
            document = _translator().Translate(MetacodeSource.FromSyntaxTree(tree));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A document the translator cannot read keeps its symbols: the index degrades, it does not fail.
            return [];
        }

        var rows = new List<SymbolMetacode>();
        var renderer = new PseudocodeRenderer();

        var byPosition = declarations
            .GroupBy(declaration => (declaration.Line, declaration.Column))
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var module in document.Modules)
            Collect(module.Declarations, parent: null);

        return rows;

        void Collect(IReadOnlyList<MetacodeDeclaration> nodes, SymbolDeclaration? parent)
        {
            foreach (var node in nodes)
            {
                var declaration = Match(node, parent);
                if (declaration is not null)
                {
                    rows.Add(new SymbolMetacode(
                        declaration.Key, path, declaration.Kind, declaration.Name, declaration.Line,
                        renderer.Render(node), hash));
                }

                if (node is MetacodeTypeDeclaration type) Collect(type.Members, declaration ?? parent);
            }
        }

        SymbolDeclaration? Match(MetacodeDeclaration node, SymbolDeclaration? parent)
        {
            if (node.Origin.IsKnown && byPosition.TryGetValue((node.Origin.StartLine, node.Origin.StartColumn), out var exact))
                return exact;

            // A pass may move a declaration's origin: fall back to the name under the same container,
            // and to the line only when the name is ambiguous (overloads).
            var name = NameOf(node);
            if (name is null || parent is null) return null;

            var candidates = declarations
                .Where(candidate => candidate.ContainerKey == parent.Key && candidate.Name == name)
                .ToList();

            return candidates.FirstOrDefault(candidate => candidate.Line == node.Origin.StartLine)
                   ?? (candidates.Count == 1 ? candidates[0] : null);
        }
    }

    private static string? NameOf(MetacodeDeclaration declaration) => declaration switch
    {
        MetacodeTypeDeclaration type => type.Name,
        MetacodeFunction function => function.Name,
        MetacodeProperty property => property.Name,
        MetacodeField field => field.Name,
        MetacodeEvent @event => @event.Name,
        MetacodeEnumMember member => member.Name,
        MetacodeTypeAlias alias => alias.Name,
        _ => null,
    };

    /// <summary>
    /// The relationships a reader expects and the syntax never spells: <c>override</c> names no base
    /// member, and an interface implementation is nothing but a matching signature. Both are recorded
    /// as uses of the base member, located at the member that overrides or implements it.
    /// </summary>
    private static IEnumerable<SymbolUsage> Relationships(
        List<(SyntaxNode Node, ISymbol Symbol, string Key)> declared, string path, string hash)
    {
        var edges = new List<SymbolUsage>();

        foreach (var group in declared.GroupBy(member => member.Symbol.ContainingType, SymbolEqualityComparer.Default))
        {
            if (group.Key is not INamedTypeSymbol type) continue;

            // The type implements every interface it — or a base of it — lists. Recorded once at the
            // type, so "which types implement IFoo" does not depend on which file declares the member.
            var typeKey = SymbolKeys.For(type);
            var anchor = group.FirstOrDefault(member => member.Key == typeKey).Node ?? group.First().Node;
            foreach (var @interface in type.AllInterfaces)
                edges.Add(Use(anchor, @interface, SymbolUsageKind.Implement, typeKey, path, hash));

            var implemented = ImplementedMembers(type);
            var seen = new HashSet<(string Interface, string Implementation)>(new EdgeComparer());

            foreach (var (node, symbol, key) in group)
            {
                if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;

                if (Overridden(symbol) is { } overridden)
                    edges.Add(Use(node, overridden, SymbolUsageKind.Override, key, path, hash));

                // A member implements what it implements, and what any member it overrides implements:
                // the override in a derived type is the implementation a caller ends up running.
                for (var current = symbol; current is not null; current = Overridden(current))
                {
                    if (!implemented.TryGetValue(SymbolKeys.For(current), out var interfaceMembers)) continue;

                    foreach (var interfaceMember in interfaceMembers)
                    {
                        if (!seen.Add((SymbolKeys.For(interfaceMember), key))) continue;
                        edges.Add(Use(node, interfaceMember, SymbolUsageKind.Implement, key, path, hash));
                    }
                }
            }
        }

        return edges;
    }

    /// <summary>For each member of a type, the interface members it implements, explicit or implicit.</summary>
    private static Dictionary<string, List<ISymbol>> ImplementedMembers(INamedTypeSymbol type)
    {
        var map = new Dictionary<string, List<ISymbol>>(StringComparer.Ordinal);

        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (type.FindImplementationForInterfaceMember(member) is not { } implementation) continue;

                var key = SymbolKeys.For(implementation);
                if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                list.Add(member);
            }
        }

        return map;
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    /// <summary>One row per (interface member, implementing member): the override chain would repeat itself.</summary>
    private sealed class EdgeComparer : IEqualityComparer<(string Interface, string Implementation)>
    {
        public bool Equals((string Interface, string Implementation) left, (string Interface, string Implementation) right) =>
            string.Equals(left.Interface, right.Interface, StringComparison.Ordinal) &&
            string.Equals(left.Implementation, right.Implementation, StringComparison.Ordinal);

        public int GetHashCode((string Interface, string Implementation) pair) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(pair.Interface), StringComparer.Ordinal.GetHashCode(pair.Implementation));
    }

    /// <summary>
    /// The member a use happens inside, normalized to something the index knows: an accessor is
    /// reported as its property, a local function as its method, and a synthesized entry point as its
    /// type, so a caller never points at a row that does not exist.
    /// </summary>
    private static string ContainerKeyAt(SemanticModel model, SyntaxNode node, ISymbolIndexFrontend frontend)
    {
        var symbol = model.GetEnclosingSymbol(node.SpanStart);

        // A type's header — its base list, its attributes, an inheritance statement — is inside no
        // member, so the enclosing symbol there is the namespace. What the use belongs to is the type.
        if (symbol is null or INamespaceSymbol)
        {
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                if (!frontend.IsDeclaration(ancestor)) continue;
                if (frontend.DeclaredSymbol(ancestor, model) is not INamedTypeSymbol type) continue;

                symbol = type;
                break;
            }
        }

        while (symbol is not null)
        {
            if (symbol is IMethodSymbol { AssociatedSymbol: { } associated }) symbol = associated;

            if (symbol is INamespaceSymbol) return SymbolKeys.For(symbol);

            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
                || (symbol.IsImplicitlyDeclared && symbol is IMethodSymbol))
            {
                symbol = symbol.ContainingSymbol;
                continue;
            }

            if (IsIndexable(symbol)) return SymbolKeys.For(symbol);
            symbol = symbol.ContainingSymbol;
        }

        return "";
    }

    private static SymbolDeclaration Declare(
        SyntaxNode node, ISymbol symbol, ISymbolIndexFrontend frontend, string project, string path, string hash)
    {
        var span = node.Span;
        var lines = node.GetLocation().GetLineSpan();
        var container = symbol.ContainingSymbol;

        return new SymbolDeclaration(
            Key: SymbolKeys.For(symbol),
            Name: DisplayName(symbol),
            Display: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Signature: frontend.Signature(node, symbol),
            Kind: symbol.Kind.ToString(),
            Accessibility: AccessibilityText(symbol),
            Modifiers: ModifierText(symbol),
            ContainerKey: container is null ? "" : SymbolKeys.For(container),
            ContainerName: container?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "",
            Namespace: symbol.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() : "",
            IsStatic: symbol.IsStatic,
            IsAbstract: symbol.IsAbstract,
            IsVirtual: symbol.IsVirtual,
            IsOverride: symbol.IsOverride,
            IsExtension: symbol is IMethodSymbol { IsExtensionMethod: true },
            Arity: symbol switch
            {
                IMethodSymbol method => method.Arity,
                INamedTypeSymbol type => type.Arity,
                _ => 0,
            },
            Summary: Summary(symbol),
            Project: project,
            Path: path,
            Start: span.Start,
            Length: span.Length,
            Line: lines.StartLinePosition.Line + 1,
            Column: lines.StartLinePosition.Character + 1,
            EndLine: lines.EndLinePosition.Line + 1,
            EndColumn: lines.EndLinePosition.Character + 1,
            DocumentHash: hash);
    }

    private static SymbolUsage Use(
        SyntaxNode node, ISymbol symbol, string kind, string container, string path, string hash)
    {
        var span = node.Span;
        var lines = node.GetLocation().GetLineSpan();

        return new SymbolUsage(
            SymbolKeys.For(symbol),
            DisplayName(symbol),
            kind,
            container,
            path,
            span.Start,
            span.Length,
            lines.StartLinePosition.Line + 1,
            lines.StartLinePosition.Character + 1,
            hash);
    }

    /// <summary>The name a reader searches for: a constructor is its type, a destructor is <c>~Type</c>.</summary>
    private static string DisplayName(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } method =>
            method.ContainingType?.Name ?? method.Name,
        IMethodSymbol { MethodKind: MethodKind.Destructor } method => "~" + (method.ContainingType?.Name ?? method.Name),
        _ => symbol.Name,
    };

    /// <summary>Members and types: the granularity "who uses this" is asked at.</summary>
    private static bool IsIndexable(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => !type.IsImplicitlyDeclared,
        IMethodSymbol method => method.MethodKind
            is MethodKind.Ordinary
            or MethodKind.Constructor
            or MethodKind.StaticConstructor
            or MethodKind.Destructor
            or MethodKind.UserDefinedOperator
            or MethodKind.Conversion
            or MethodKind.ExplicitInterfaceImplementation,
        IPropertySymbol or IEventSymbol or IFieldSymbol => true,
        _ => false,
    };

    private static string AccessibilityText(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };

    private static string ModifierText(ISymbol symbol)
    {
        var parts = new List<string>();
        if (symbol.IsStatic) parts.Add("static");
        if (symbol.IsAbstract) parts.Add("abstract");
        if (symbol.IsVirtual) parts.Add("virtual");
        if (symbol.IsOverride) parts.Add("override");
        if (symbol.IsSealed && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum }) parts.Add("sealed");
        if (symbol.IsExtern) parts.Add("extern");

        switch (symbol)
        {
            case IMethodSymbol { IsAsync: true }:
                parts.Add("async");
                break;
            case IMethodSymbol { IsExtensionMethod: true }:
                parts.Add("extension");
                break;
            case IFieldSymbol { IsConst: true } field:
                parts.Add("const");
                if (field.IsReadOnly) parts.Add("readonly");
                break;
            case IFieldSymbol field:
                if (field.IsReadOnly) parts.Add("readonly");
                if (field.IsVolatile) parts.Add("volatile");
                if (field.IsRequired) parts.Add("required");
                break;
            case IPropertySymbol { IsRequired: true }:
                parts.Add("required");
                break;
        }

        return string.Join(" ", parts);
    }

    /// <summary>The first paragraph of the XML documentation, when the project parses it.</summary>
    private static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null) return null;

            var text = string.Join(" ", summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length switch
            {
                0 => null,
                > 500 => text[..500],
                _ => text,
            };
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
