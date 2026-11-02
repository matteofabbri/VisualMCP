using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Decides whether a solution should get the PCN pack without being told. The name is the cheap
/// signal (<c>Predicting</c>, <c>PCN</c>); when the name says nothing, a type with the PCN signature
/// (precision, error, activity and weight members, or an UpdateActivity/UpdateWeights method) is the
/// structural one. The pack's rules are themselves scoped to those types, so this only decides
/// whether the four analyzers run at all.
/// </summary>
internal static class PcnDetection
{
    private const int DocumentLimit = 250;

    public static async Task<bool> LooksLikePcnAsync(
        Solution solution, CancellationToken cancellationToken = default)
    {
        if (HasPredictiveName(solution.FilePath)) return true;

        if (solution.Projects.Any(project =>
                HasPredictiveName(project.Name) || HasPredictiveName(project.AssemblyName)))
            return true;

        var inspected = 0;

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (inspected++ >= DocumentLimit) return false;

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null) continue;

                if (root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(IsPcnShape))
                    return true;
            }
        }

        return false;
    }

    private static bool IsPcnShape(TypeDeclarationSyntax type)
    {
        if (HasPredictiveName(type.Identifier.ValueText)) return true;

        var names = type.Members
            .SelectMany(member => member switch
            {
                MethodDeclarationSyntax method => new[] { method.Identifier.ValueText },
                PropertyDeclarationSyntax property => new[] { property.Identifier.ValueText },
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
                _ => Enumerable.Empty<string>(),
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var signals = 0;
        if (names.Overlaps(["Pi", "Precision", "Prec"])) signals++;
        if (names.Overlaps(["E", "Error", "Residual"])) signals++;
        if (names.Overlaps(["A", "Activity", "Mu"])) signals++;
        if (names.Overlaps(["W", "V", "Weight", "Weights"])) signals++;
        if (names.Any(name => name.Contains("UpdateActivity", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("UpdateWeights", StringComparison.OrdinalIgnoreCase)))
            signals++;

        return signals >= 3;
    }

    private static bool HasPredictiveName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("predict", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("pcn", StringComparison.OrdinalIgnoreCase));
}
