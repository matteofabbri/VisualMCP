using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    /// <summary>
    /// ReSharper's "Import type": adds the <c>using</c> that brings the type named on the line into scope.
    /// The namespace is resolved from the compilation (source and referenced assemblies), so an ambiguous
    /// simple name is refused with the candidates instead of guessing one.
    /// </summary>
    private Outcome ImportType(Target target, string? text)
    {
        var requested = text?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            var onLine = target.Root.DescendantNodes()
                .Where(node => LineOf(node) == target.Line)
                .OfType<TypeSyntax>()
                .OrderBy(node => node.Span.Length)
                .FirstOrDefault();

            if (onLine is null)
                return Outcome.Fail($"No type on line {target.Line}: pass the type name in text.");

            requested = onLine.ToString();
        }

        var candidates = ResolveNamespaces(target.Compilation, requested);
        if (candidates.Count == 0)
            return Outcome.Fail($"'{requested}' is not declared in the source or in any referenced assembly.");

        if (candidates.Count > 1)
            return Outcome.Fail(
                $"'{requested}' is declared in {candidates.Count} namespaces: {string.Join(", ", candidates)}. " +
                "Pass the fully qualified name in text to choose one.");

        var ns = candidates[0];
        if (string.IsNullOrEmpty(ns))
            return Outcome.Fail($"'{requested}' is in the global namespace: no using directive can bring it into scope.");

        if (target.Root is not CompilationUnitSyntax unit)
            return Outcome.Fail("The document has no compilation unit to add a using to.");

        var position = target.Root.DescendantNodes().FirstOrDefault(node => LineOf(node) == target.Line)?.SpanStart ?? 0;
        var simpleName = requested[(requested.LastIndexOf('.') + 1)..];
        if (IsSymbolVisible(target.Model, position, simpleName))
            return Outcome.Fail($"'{simpleName}' is already in scope on line {target.Line}: nothing to import.");

        var newText = AddUsings(target.Text.ToString(), unit, [ns]);
        return new Outcome(newText, [$"added `using {ns};`"]);
    }

    /// <summary>The namespaces that declare the type: the exact metadata name when qualified, else every match.</summary>
    /// <remarks>
    /// The search walks the source module and every referenced assembly, because
    /// <c>Compilation.GetSymbolsWithName</c> only sees declarations in source and the type to import is
    /// usually a metadata one (<c>StringBuilder</c>, <c>JsonSerializer</c>).
    /// </remarks>
    private static List<string> ResolveNamespaces(Compilation compilation, string requested)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);

        if (requested.Contains('.', StringComparison.Ordinal))
        {
            if (compilation.GetTypeByMetadataName(requested) is { } exact)
                namespaces.Add(NamespaceOf(exact));
            return namespaces.ToList();
        }

        Collect(compilation.SourceModule.GlobalNamespace, requested, namespaces);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                Collect(assembly.GlobalNamespace, requested, namespaces);
        }

        return namespaces.ToList();
    }

    private static void Collect(INamespaceSymbol container, string name, ISet<string> found)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    Collect(child, name, found);
                    break;
                case INamedTypeSymbol type when string.Equals(type.Name, name, StringComparison.Ordinal):
                    found.Add(NamespaceOf(type));
                    break;
            }
        }
    }

    private static string NamespaceOf(INamedTypeSymbol type)
    {
        var outer = type;
        while (outer.ContainingType is not null)
            outer = outer.ContainingType;

        var ns = outer.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    /// <summary>Whether a type with this simple name is reachable unqualified at that position.</summary>
    private static bool IsSymbolVisible(SemanticModel model, int position, string name) =>
        model.LookupSymbols(position, name: name).Any(symbol => symbol is INamedTypeSymbol);

    /// <summary>
    /// Whether the namespace is already in scope, probed through a type that only that namespace provides:
    /// <c>LookupSymbols</c> answers for the real position, so an implicit global using counts.
    /// </summary>
    private static bool IsNamespaceVisible(SemanticModel model, int position, string ns) => ns switch
    {
        "System" => IsSymbolVisible(model, position, "ArgumentNullException") ||
                    IsSymbolVisible(model, position, "InvalidOperationException"),
        "System.Linq" => IsSymbolVisible(model, position, "Enumerable"),
        "System.Collections.Generic" => IsSymbolVisible(model, position, "KeyNotFoundException") ||
                                        IsSymbolVisible(model, position, "Dictionary"),
        _ => true,
    };

    /// <summary>Inserts the directives after the last existing using, with the file's own newline.</summary>
    private static string AddUsings(string text, CompilationUnitSyntax unit, IReadOnlyList<string> namespaces)
    {
        if (namespaces.Count == 0)
            return text;

        var newLine = NewLineOf(text);
        var directives = string.Concat(namespaces.Select(ns => "using " + ns + ";" + newLine));

        if (unit.Usings.Count == 0)
            return directives + text;

        var last = unit.Usings[unit.Usings.Count - 1];
        var lineEnd = text.IndexOf('\n', last.Span.End);
        return lineEnd < 0
            ? text.Insert(last.Span.End, newLine + directives)
            : text.Insert(lineEnd + 1, directives);
    }

    /// <summary>The namespaces the edited position cannot see yet, in insertion order.</summary>
    private static List<string> MissingNamespaces(Target target, int position, params string[] namespaces) =>
        namespaces
            .Where(ns => !IsNamespaceVisible(target.Model, position, ns))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
