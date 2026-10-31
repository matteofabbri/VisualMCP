using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The localization view: which <c>.resx</c> keys nothing references, which keys a translation is missing,
/// and the user-facing strings still written in code. The first two are exact (keys versus references); the
/// third is a heuristic, and the answer says so.
/// </summary>
public interface ILocalizationService
{
    Task<object> ReportAsync(string? projectName, int maxResults);
}

public sealed class LocalizationService : ILocalizationService
{
    private static readonly Regex CultureName = new(@"^[a-z]{2,3}(?:-[A-Za-z]{2,4})?$", RegexOptions.Compiled);

    private readonly IRoslynWorkspace _workspace;
    public LocalizationService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ReportAsync(string? projectName = null, int maxResults = 100)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory." };

        var directory = SolutionDirectory(solution);
        if (directory is null) return new { error = "The solution has no folder on disk." };

        var resourceFiles = Directory.EnumerateFiles(directory, "*.resx", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resx = resourceFiles.Select(ParseResx).Where(file => file is not null).Select(file => file!).ToList();
        var allKeys = resx.SelectMany(file => file.Keys).ToHashSet(StringComparer.Ordinal);

        var (referenced, literals) = await CollectReferencesAsync(solution, projectName, allKeys);

        var unused = resx
            .Where(file => file.Culture is null)
            .SelectMany(file => file.Keys.Where(key => !referenced.Contains(key))
                .Select(key => new { file = file.Path, key }))
            .ToList();

        var missing = new List<object>();
        foreach (var neutral in resx.Where(file => file.Culture is null))
        {
            var baseName = Path.GetFileNameWithoutExtension(neutral.Path);
            foreach (var translation in resx.Where(file =>
                         file.Culture is not null &&
                         Path.GetFileNameWithoutExtension(file.Path).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var absent = neutral.Keys.Where(key => !translation.Keys.Contains(key)).ToList();
                var extra = translation.Keys.Where(key => !neutral.Keys.Contains(key)).ToList();
                if (absent.Count == 0 && extra.Count == 0) continue;

                missing.Add(new
                {
                    culture = translation.Culture,
                    file = translation.Path,
                    baseFile = neutral.Path,
                    missingKeys = absent.Take(50).ToList(),
                    missingCount = absent.Count,
                    extraKeys = extra.Take(50).ToList(),
                    extraCount = extra.Count,
                });
            }
        }

        var limit = Math.Clamp(maxResults < 1 ? 100 : maxResults, 1, 2000);
        var hardCoded = literals.Take(limit).ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            resourceFiles = resx.Select(file => new { file = file.Path, culture = file.Culture, keys = file.Keys.Count }).ToList(),
            resourceKeyCount = allKeys.Count,
            referencedKeyCount = referenced.Count,
            unusedKeys = unused.Take(limit).ToList(),
            unusedKeyCount = unused.Count,
            missingTranslations = missing,
            hardCodedCandidates = hardCoded,
            hardCodedCandidateCount = literals.Count,
            truncated = unused.Count > limit || literals.Count > limit,
            note = "Unused keys and missing translations compare the .resx key sets with the GetString/GetObject literals " +
                   "and the generated Resources properties the code names — exact for those access patterns, not for a " +
                   "key built at runtime. Hard-coded candidates are literals with three or more words in non-test " +
                   "projects: a heuristic, not a finding.",
        };
    }

    private sealed record ResxFile(string Path, string? Culture, HashSet<string> Keys);

    private static ResxFile? ParseResx(string path)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(path);
            var keys = document.Descendants()
                .Where(element => element.Name.LocalName == "data")
                .Select(element => element.Attribute("name")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name) && name![0] != '>')
                .Select(name => name!.Trim())
                .ToHashSet(StringComparer.Ordinal);

            // Strings.resx is the neutral file; Strings.it.resx / Strings.pt-BR.resx carry a culture.
            var fileName = Path.GetFileNameWithoutExtension(path);
            var lastDot = fileName.LastIndexOf('.');
            var candidate = lastDot > 0 ? fileName[(lastDot + 1)..] : null;
            var culture = candidate is not null && CultureName.IsMatch(candidate) ? candidate : null;

            return new ResxFile(path, culture, keys);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Keys the code names, plus the literals that look like user-facing text.</summary>
    private async Task<(HashSet<string> Referenced, List<object> HardCoded)> CollectReferencesAsync(
        Solution solution, string? projectName, HashSet<string> keys)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var hardCoded = new List<object>();

        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)) continue;
            var isTest = project.Name.Contains("test", StringComparison.OrdinalIgnoreCase);

            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        _ => null,
                    };

                    if (name is not ("GetString" or "GetObject" or "GetStream")) continue;
                    if (invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal) continue;
                    if (!literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)) continue;

                    referenced.Add(literal.Token.ValueText);
                }

                foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (!keys.Contains(access.Name.Identifier.ValueText)) continue;
                    if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol property) continue;
                    if (!property.ContainingType.Name.EndsWith("Resources", StringComparison.OrdinalIgnoreCase) &&
                        !property.ContainingType.Name.EndsWith("Resource", StringComparison.OrdinalIgnoreCase))
                        continue;

                    referenced.Add(access.Name.Identifier.ValueText);
                }

                if (isTest) continue;

                foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                {
                    if (!literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)) continue;

                    var value = literal.Token.ValueText;
                    if (value.Length < 15 || value.Count(char.IsLetter) < 10) continue;
                    if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) continue;
                    if (keys.Contains(value)) continue;

                    var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    hardCoded.Add(new { project = project.Name, file = document.FilePath, line, text = Trim(value) });
                }
            }
        }

        return (referenced, hardCoded);
    }

    private static string Trim(string text) => text.Length <= 100 ? text : text[..99] + "…";

    private static string? SolutionDirectory(Solution solution) =>
        solution.Projects.Select(project => project.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(Path.GetFullPath(path!)))
            .FirstOrDefault(directory => directory is not null);
}
