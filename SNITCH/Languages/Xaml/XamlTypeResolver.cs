using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>The type a binding path is checked against, and where that type came from.</summary>
public sealed record XamlDataContext(string? TypeName, string Source, ITypeSymbol? Symbol)
{
    public bool Resolved => Symbol is not null;
}

/// <summary>One step of a binding path and whether the type it was checked against declares it.</summary>
public sealed record XamlPathStep(string Name, string? TypeName, bool Found);

/// <summary>
/// The result of checking one binding: valid, missing-member, not-checked (with the reason) — the
/// data the binding inspector reports and the rule turns into a finding.
/// </summary>
public sealed record XamlBindingCheck(
    XamlBinding Binding,
    string Status,
    string? DataType,
    string? DataTypeSource,
    List<XamlPathStep> Steps,
    string? MissingMember,
    string? Message)
{
    /// <summary>True when the binding was checked and a segment did not exist.</summary>
    public bool Failed => Status == "missing-member";
}

/// <summary>
/// Resolves the XAML side through Roslyn: an <c>x:DataType</c> name against the project's compilation
/// (through the file's own xmlns prefixes), the code-behind's DataContext type from the assignments
/// that set it, and the members a binding path walks. Everything unresolved stays unresolved instead
/// of being guessed — a binding is only reported when the type behind it was found.
/// </summary>
public sealed class XamlTypeResolver
{
    private readonly Solution _solution;
    private readonly string? _projectName;
    private readonly Dictionary<string, ITypeSymbol?> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XamlDataContext?> _dataContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol?> _codeBehind = new(StringComparer.Ordinal);
    private readonly XamlProjectFacts? _projectFacts;

    private XamlTypeResolver(Solution solution, string? projectName, XamlProjectFacts? projectFacts)
    {
        _solution = solution;
        _projectName = projectName;
        _projectFacts = projectFacts;
    }

    public static XamlTypeResolver? Create(Solution? solution, string? projectName, XamlProjectFacts? projectFacts = null) =>
        solution is null ? null : new XamlTypeResolver(solution, projectName, projectFacts);

    public XamlProjectFacts? ProjectFacts => _projectFacts;

    // ── namespaces the default XAML namespace of each dialect stands for ─────
    private static readonly Dictionary<XamlDialect, string[]> DialectNamespaces = new()
    {
        [XamlDialect.Wpf] =
        [
            "System.Windows.Controls", "System.Windows", "System.Windows.Shapes", "System.Windows.Media",
            "System.Windows.Documents", "System.Windows.Controls.Primitives", "System.Windows.Navigation",
        ],
        [XamlDialect.WinUi] =
        [
            "Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml", "Microsoft.UI.Xaml.Media", "Microsoft.UI.Xaml.Shapes",
            "Microsoft.UI.Xaml.Controls.Primitives", "Microsoft.UI.Xaml.Documents",
        ],
        [XamlDialect.Uwp] =
        [
            "Windows.UI.Xaml.Controls", "Windows.UI.Xaml", "Windows.UI.Xaml.Media", "Windows.UI.Xaml.Shapes",
            "Windows.UI.Xaml.Controls.Primitives", "Windows.UI.Xaml.Documents",
        ],
        [XamlDialect.Maui] =
        [
            "Microsoft.Maui.Controls", "Microsoft.Maui.Controls.Shapes", "Microsoft.Maui.Graphics", "Microsoft.Maui",
        ],
        [XamlDialect.Avalonia] =
        [
            "Avalonia.Controls", "Avalonia", "Avalonia.Media", "Avalonia.Controls.Primitives",
            "Avalonia.Controls.Documents", "Avalonia.Styling",
        ],
    };

    // ── resolving a type name as the markup spells it ────────────────────────
    public async Task<ITypeSymbol?> ResolveTypeAsync(XamlDocument document, string? rawName)
    {
        var name = NormalizeTypeName(rawName);
        if (name is null) return null;

        var cacheKey = document.FilePath + "|" + name;
        if (_types.TryGetValue(cacheKey, out var cached)) return cached;

        var resolved = await ResolveCoreAsync(document, name);
        _types[cacheKey] = resolved;
        return resolved;
    }

    private async Task<ITypeSymbol?> ResolveCoreAsync(XamlDocument document, string name)
    {
        var compilation = await CompilationForAsync(document.Project);
        if (compilation is null) return null;

        // local:Foo (and avares:…) resolves through the file's own xmlns: prefix declaration.
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var prefix = name[..colon];
            var local = name[(colon + 1)..];
            if (document.Namespaces.TryGetValue(prefix, out var uri))
            {
                var clrNamespace = ClrNamespace(uri);
                if (clrNamespace is not null)
                {
                    var candidate = compilation.GetTypeByMetadataName(clrNamespace + "." + local);
                    if (candidate is not null) return candidate;

                    // A nested type or a wrong namespace still has the simple name worth looking for.
                    return FindByFullNameSuffix(compilation, clrNamespace + "." + local);
                }
            }

            // A dialect that re-declares the presentation namespace (e.g. Avalonia's) carries no prefix.
            return FindBySimpleName(compilation, local);
        }

        // A dotted name is a CLR name spelled in full.
        if (name.Contains('.'))
        {
            var direct = compilation.GetTypeByMetadataName(name);
            if (direct is not null) return direct;

            return FindByFullNameSuffix(compilation, name);
        }

        // A simple name: the dialect's own namespace table first, because "Button" is ambiguous
        // across every UI framework the project might reference.
        if (_projectFacts is not null &&
            DialectNamespaces.TryGetValue(_projectFacts.Dialect, out var namespaces))
        {
            foreach (var ns in namespaces)
            {
                var candidate = compilation.GetTypeByMetadataName(ns + "." + name);
                if (candidate is not null) return candidate;
            }
        }

        return FindBySimpleName(compilation, name);
    }

    /// <summary>ClrNamespace from <c>clr-namespace:Ns;assembly=A</c> or <c>using:Ns</c> (WinUI/UWP).</summary>
    private static string? ClrNamespace(string uri)
    {
        var text = uri.Trim();

        if (text.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
            return text["using:".Length..].Trim();

        if (!text.StartsWith("clr-namespace:", StringComparison.OrdinalIgnoreCase)) return null;

        var body = text["clr-namespace:".Length..];
        var semicolon = body.IndexOf(';');
        return (semicolon < 0 ? body : body[..semicolon]).Trim();
    }

    /// <summary>Identity of the type name as a CLR metadata name (<c>{x:Type local:Foo}</c> → <c>local:Foo</c>).</summary>
    public static string? NormalizeTypeName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        var text = rawName.Trim();

        if (XamlMarkup.TryParse(text, out var extension))
        {
            if (extension.Type is not ("Type" or "x:Type")) return null;
            text = extension.Value("TypeName") ?? (extension.Positional.Count > 0 ? extension.Positional[0] : null) ?? string.Empty;
            if (text.Length == 0) return null;
            text = text.Trim();
        }

        return text.Length == 0 ? null : text;
    }

    private static ITypeSymbol? FindBySimpleName(Compilation compilation, string name)
    {
        // Source declarations only: a simple name is far too ambiguous across references to pick one.
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (declaration.Identifier.Text != name) continue;
                if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol) return symbol;
            }
        }

        return null;
    }

    private static ITypeSymbol? FindByFullNameSuffix(Compilation compilation, string name)
    {
        var direct = compilation.GetTypeByMetadataName(name);
        if (direct is not null) return direct;

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol) continue;
                if (symbol.ToDisplayString() == name ||
                    symbol.ToDisplayString().EndsWith("." + name, StringComparison.Ordinal))
                    return symbol;
            }
        }

        return null;
    }

    // ── what a type offers a binding path ───────────────────────────────────
    public ITypeSymbol? MemberType(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member.DeclaredAccessibility != Accessibility.Public) continue;

                switch (member)
                {
                    case IPropertySymbol property when !property.IsStatic:
                        return property.Type;
                    case IFieldSymbol field when !field.IsStatic:
                        return field.Type;
                    case IEventSymbol @event when !@event.IsStatic:
                        return @event.Type;
                }
            }
        }

        // An interface member is reachable through an implementation the path may be typed as.
        foreach (var @interface in type.AllInterfaces)
            foreach (var member in @interface.GetMembers(name))
                if (member is IPropertySymbol property && property.DeclaredAccessibility == Accessibility.Public)
                    return property.Type;

        return null;
    }

    public bool HasMember(ITypeSymbol type, string name) => MemberType(type, name) is not null;

    // ── the code-behind type and the DataContext it sets ────────────────────
    public async Task<INamedTypeSymbol?> CodeBehindAsync(XamlDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.ClassName)) return null;

        var key = document.Project + "|" + document.ClassName;
        if (_codeBehind.TryGetValue(key, out var cached)) return cached;

        var compilation = await CompilationForAsync(document.Project);
        var symbol = compilation?.GetTypeByMetadataName(document.ClassName!) as INamedTypeSymbol;
        _codeBehind[key] = symbol;
        return symbol;
    }

    /// <summary>
    /// The DataContext type: written by an assignment in the code-behind
    /// (<c>DataContext = new MainViewModel()</c>, <c>BindingContext = …</c>) or by a design-time
    /// <c>d:DataContext</c>. Two different types assigned means the scan cannot pick one, and it says so.
    /// </summary>
    public async Task<XamlDataContext?> DataContextAsync(XamlDocument document)
    {
        var key = document.Project + "|" + document.FilePath + "|" + document.ClassName;
        if (_dataContexts.TryGetValue(key, out var cached)) return cached;

        var result = await FindDataContextAsync(document);
        _dataContexts[key] = result;
        return result;
    }

    private async Task<XamlDataContext?> FindDataContextAsync(XamlDocument document)
    {
        var codeBehind = await CodeBehindAsync(document);
        var compilation = await CompilationForAsync(document.Project);

        if (codeBehind is not null && compilation is not null)
        {
            var assigned = new List<(string Name, string Source, ITypeSymbol Symbol)>();

            foreach (var reference in codeBehind.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                var model = compilation.GetSemanticModel(tree);

                foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    var target = assignment.Left switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.Text,
                        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
                        _ => null,
                    };

                    if (target is not ("DataContext" or "BindingContext")) continue;

                    var type = model.GetTypeInfo(assignment.Right).Type;
                    if (type is null || type.SpecialType == SpecialType.System_Object) continue;
                    if (type.TypeKind is TypeKind.Error or TypeKind.Dynamic) continue;

                    var name = type.ToDisplayString();
                    if (assigned.Any(candidate => candidate.Name == name)) continue;
                    assigned.Add((name, "code-behind DataContext", type));
                }
            }

            if (assigned.Count == 1)
                return new XamlDataContext(assigned[0].Name, assigned[0].Source, assigned[0].Symbol);

            if (assigned.Count > 1)
                return new XamlDataContext(
                    string.Join(" | ", assigned.Select(candidate => candidate.Name)),
                    "code-behind DataContext (ambiguous)",
                    null);
        }

        if (!string.IsNullOrWhiteSpace(document.DesignDataContextType))
        {
            var symbol = await ResolveTypeAsync(document, document.DesignDataContextType);
            return new XamlDataContext(document.DesignDataContextType, "d:DataContext", symbol);
        }

        return null;
    }

    // ── compilation access ───────────────────────────────────────────────────
    private readonly Dictionary<string, Compilation?> _compilations = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Compilation?> CompilationForAsync(string? projectName)
    {
        var key = projectName ?? string.Empty;
        if (_compilations.TryGetValue(key, out var cached)) return cached;

        var project = _projectName is not null
            ? _solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(_projectName, StringComparison.OrdinalIgnoreCase))
            : null;

        project ??= projectName is null
            ? _solution.Projects.FirstOrDefault()
            : _solution.Projects.FirstOrDefault(candidate =>
                  candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)) ??
              _solution.Projects.FirstOrDefault();

        var compilation = project is null ? null : await project.GetCompilationAsync();
        _compilations[key] = compilation;
        return compilation;
    }
}
