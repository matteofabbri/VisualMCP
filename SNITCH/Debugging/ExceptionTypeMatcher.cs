using Microsoft.CodeAnalysis;

namespace Snitch.Debugging;

/// <summary>
/// The type conditions of <c>setExceptionBreakpoints</c>, evaluated here instead of by the adapter. A DAP
/// type condition is matched against the thrown type's full name exactly, so naming a base class matches
/// nothing; this walks the hierarchy the solution resolves, which is what "break on MyException" means.
/// A condition prefixed <c>!</c> excludes its whole branch. When the type cannot be resolved the answer
/// falls back to comparing names, so an unresolved solution never hides a stop it cannot reason about.
/// </summary>
public static class ExceptionTypeMatcher
{
    public static bool Matches(Compilation? compilation, string? thrownFullName, string? thrownSimpleName,
        IReadOnlyList<string> conditions)
    {
        var thrown = FirstNonEmpty(thrownFullName, thrownSimpleName);
        // An unknown type is shown rather than hidden: a filter must not swallow what it cannot identify.
        if (thrown is null) return true;

        var positives = 0;
        var matchedPositive = false;

        foreach (var raw in conditions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var condition = raw.Trim();
            var negated = condition.StartsWith("!", StringComparison.Ordinal);
            var wanted = (negated ? condition[1..] : condition).Trim();
            if (wanted.Length == 0) continue;
            if (!negated) positives++;

            if (!IsAssignable(compilation, thrown, wanted)) continue;
            if (negated) return false;
            matchedPositive = true;
        }

        return positives == 0 || matchedPositive;
    }

    /// <summary>Whether <paramref name="thrown"/> is <paramref name="wanted"/> or derives from it.</summary>
    public static bool IsAssignable(Compilation? compilation, string thrown, string wanted)
    {
        var thrownSymbol = Resolve(compilation, thrown);
        var wantedSymbol = Resolve(compilation, wanted);

        if (thrownSymbol is not null && wantedSymbol is not null)
        {
            for (var current = thrownSymbol; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, wantedSymbol.OriginalDefinition))
                    return true;
            }

            return false;
        }

        return SameName(thrown, wanted);
    }

    private static INamedTypeSymbol? Resolve(Compilation? compilation, string name)
    {
        if (compilation is null) return null;

        var metadataName = name.Replace("+", ".").Trim();
        var byMetadata = compilation.GetTypeByMetadataName(metadataName);
        if (byMetadata is not null) return byMetadata;

        // A simple name: the solution's own namespaces first, then the framework ones, so "Exception"
        // means System.Exception unless the solution declares its own.
        var simple = metadataName[(metadataName.LastIndexOf('.') + 1)..];
        if (simple.Length == 0) return null;

        return FindIn(compilation.Assembly.GlobalNamespace, simple)
               ?? compilation.GetTypeByMetadataName("System." + simple);
    }

    private static INamedTypeSymbol? FindIn(INamespaceOrTypeSymbol scope, string simpleName)
    {
        foreach (var member in scope.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol space:
                {
                    var found = FindIn(space, simpleName);
                    if (found is not null) return found;
                    break;
                }
                case INamedTypeSymbol type:
                {
                    if (string.Equals(type.Name, simpleName, StringComparison.Ordinal)) return type;
                    var nested = FindIn(type, simpleName);
                    if (nested is not null) return nested;
                    break;
                }
            }
        }

        return null;
    }

    private static bool SameName(string thrown, string wanted)
    {
        var left = thrown.Replace('+', '.');
        var right = wanted.Replace('+', '.');
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        var leftSimple = left[(left.LastIndexOf('.') + 1)..];
        var rightSimple = right[(right.LastIndexOf('.') + 1)..];
        return string.Equals(leftSimple, rightSimple, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
