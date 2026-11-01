using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The reflective APIs a static call graph cannot follow, read from the semantic model: the name a lookup
/// asks for — a string literal, <c>nameof</c>, <c>typeof</c> or any compile-time constant expression such as
/// a concatenation of literals — and the API that asks for it.
/// </summary>
/// <remarks>
/// Shared by the two tools that have to agree on this boundary: <c>find_reflection_usage</c> reports the
/// lookups, and <c>find_unused_symbols</c> treats the names they ask for as live, so a symbol reached only
/// reflectively is not called dead.
/// </remarks>
public static class ReflectiveLookups
{
    /// <summary>The reflective APIs worth reporting: containing type, member, and the label used in the answer.</summary>
    private static readonly (string Type, string Member, string Api)[] Apis =
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

    /// <summary>The API label for a call, or null when it is not one of the reflective lookups.</summary>
    public static string? Match(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        if (containingType is null) return null;

        var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns is not ("System" or "System.Reflection")) return null;

        foreach (var (type, member, api) in Apis)
            if (containingType.Name == type && method.Name == member)
                return api;

        return null;
    }

    /// <summary>
    /// The name the lookup asks for. A literal, <c>nameof(…)</c> and a concatenation of constants all fold
    /// to the one string the call receives; a <c>typeof</c> argument is reported by its written type name.
    /// Anything computed at run time returns null — the lookup is enumerated, not guessed.
    /// </summary>
    public static string? NameOf(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null) return null;

        if (model.GetConstantValue(expression) is { HasValue: true, Value: string constant } && constant.Length > 0)
            return constant;

        return expression is TypeOfExpressionSyntax typeOf ? typeOf.Type.ToString() : null;
    }

    /// <summary>
    /// The symbol a lookup name designates: by simple name, or — when the name is qualified — by metadata
    /// name first and then by its last segment, which is how <c>Type.GetType("Probe.Foo")</c> and
    /// <c>Assembly.GetType("Probe.Foo, Probe")</c> reach a declaration in the solution.
    /// </summary>
    public static ISymbol? Resolve(Compilation compilation, string name)
    {
        var metadataName = MetadataName(name);
        if (!LooksLikeAnIdentifier(metadataName)) return null;

        var direct = compilation.GetSymbolsWithName(candidate => candidate == metadataName,
                SymbolFilter.TypeAndMember, CancellationToken.None)
            .FirstOrDefault();
        if (direct is not null) return direct;

        if (!metadataName.Contains('.', StringComparison.Ordinal)) return null;

        if (compilation.GetTypeByMetadataName(metadataName) is { } type) return type;

        var simple = SimpleName(metadataName);
        return simple is null
            ? null
            : compilation.GetSymbolsWithName(candidate => candidate == simple,
                    SymbolFilter.TypeAndMember, CancellationToken.None)
                .FirstOrDefault();
    }

    /// <summary>
    /// The name a symbol could be looked up by: the assembly qualification dropped, and a qualified type name
    /// reduced to its last segment, so <c>Probe.Foo, Probe</c> matches the declaration whose
    /// <see cref="ISymbol.Name"/> is <c>Foo</c>.
    /// </summary>
    public static string? SimpleName(string name)
    {
        var typePart = MetadataName(name);
        var separator = typePart.LastIndexOfAny(['.', '+', '/']);
        var last = separator < 0 ? typePart : typePart[(separator + 1)..];
        return last.Length > 0 && char.IsLetter(last[0]) ? last : null;
    }

    /// <summary>The type part of an assembly-qualified name: <c>Probe.Foo, Probe</c> → <c>Probe.Foo</c>.</summary>
    public static string MetadataName(string name)
    {
        var comma = name.IndexOf(',');
        return (comma < 0 ? name : name[..comma]).Trim();
    }

    /// <summary>The shape a lookup name has to have before it can designate a symbol.</summary>
    public static bool LooksLikeAnIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '+' or '/');
}
