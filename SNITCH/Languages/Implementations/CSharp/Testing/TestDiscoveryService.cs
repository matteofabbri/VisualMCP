using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>One test method as the source declares it: framework, data rows, location.</summary>
public sealed record DiscoveredTest(
    string Project,
    string Class,
    string Method,
    string Framework,
    bool Parameterized,
    IReadOnlyList<string> DataRows,
    bool HasDynamicData,
    string? File,
    int Line);

/// <summary>
/// Static test discovery: reads the test attributes from the source instead of running the suite, so the
/// test-case model (including the <c>[Theory]</c>/<c>[TestCase]</c>/<c>[DataRow]</c> rows written inline) is
/// available before a run. Rows supplied by <c>MemberData</c>/<c>TestCaseSource</c>/<c>DynamicData</c> are
/// named but not evaluated — that is the one part a runner must produce.
/// </summary>
public interface ITestDiscoveryService
{
    Task<object> ListAsync(string? projectName, string? filter, int maxResults);
}

public sealed class TestDiscoveryService : ITestDiscoveryService
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "DataRow", "DynamicData",
    };

    private static readonly HashSet<string> DataAttributes = new(StringComparer.Ordinal)
    {
        "InlineData", "DataRow", "TestCase",
    };

    private static readonly HashSet<string> DynamicAttributes = new(StringComparer.Ordinal)
    {
        "MemberData", "ClassData", "TestCaseSource", "DynamicData",
    };

    private readonly IRoslynWorkspace _workspace;
    public TestDiscoveryService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, string? filter = null, int maxResults = 500)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory. Call load_solution with an explicit path." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var limit = Math.Clamp(maxResults < 1 ? 500 : maxResults, 1, 5000);
        var requested = string.IsNullOrWhiteSpace(filter) ? null : filter!.Trim();
        var tests = new List<DiscoveredTest>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (method.AttributeLists.Count == 0) continue;

                    var attributes = method.AttributeLists
                        .SelectMany(list => list.Attributes)
                        .Select(attribute => (Name: LastSegment(attribute.Name.ToString()), Attribute: attribute))
                        .ToList();

                    var testAttributes = attributes.Where(entry => TestAttributes.Contains(entry.Name)).ToList();
                    if (testAttributes.Count == 0) continue;

                    var framework = FrameworkOf(testAttributes.Select(entry => entry.Name));
                    var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "(top-level)";
                    var methodName = method.Identifier.ValueText;

                    if (requested is not null &&
                        !($"{className}.{methodName}").Contains(requested, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var rows = new List<string>();
                    var dynamic = false;
                    foreach (var (name, attribute) in attributes)
                    {
                        if (DataAttributes.Contains(name))
                        {
                            var arguments = attribute.ArgumentList?.Arguments
                                .Select(argument => argument.Expression.ToString()) ?? [];
                            rows.Add(string.Join(", ", arguments));
                        }
                        else if (DynamicAttributes.Contains(name))
                        {
                            dynamic = true;
                            var source = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString();
                            rows.Add($"dynamic: {source ?? name}");
                        }
                    }

                    tests.Add(new DiscoveredTest(
                        project.Name,
                        className,
                        methodName,
                        framework,
                        Parameterized: testAttributes.Any(entry => entry.Name is "Theory" or "TestCase" or "DataTestMethod" or "TestCaseSource" or "DynamicData") || rows.Count > 0,
                        rows,
                        dynamic,
                        document.FilePath,
                        method.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }
        }

        var ordered = tests
            .OrderBy(test => test.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(test => test.Class, StringComparer.Ordinal)
            .ThenBy(test => test.Method, StringComparer.Ordinal)
            .ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            filter = requested,
            total = ordered.Count,
            parameterized = ordered.Count(test => test.Parameterized),
            byFramework = ordered.GroupBy(test => test.Framework, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            tests = ordered.Take(limit).Select(test => new
            {
                project = test.Project,
                @class = test.Class,
                method = test.Method,
                framework = test.Framework,
                parameterized = test.Parameterized,
                dataRows = test.DataRows,
                hasDynamicData = test.HasDynamicData,
                file = test.File,
                test.Line,
            }).ToList(),
            truncated = ordered.Count > limit,
            note = "Discovery reads the source: inline data rows are listed, MemberData/TestCaseSource/DynamicData are named " +
                   "but not evaluated (the runner produces their cases). Run them with run_test or run_tests.",
        };
    }

    private static string LastSegment(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0) trimmed = trimmed[(dot + 1)..];
        // C# allows the Attribute suffix to be omitted; the syntax usually keeps what was written.
        return trimmed.EndsWith("Attribute", StringComparison.Ordinal) ? trimmed[..^"Attribute".Length] : trimmed;
    }

    private static string FrameworkOf(IEnumerable<string> attributes)
    {
        var names = attributes.ToHashSet(StringComparer.Ordinal);
        if (names.Contains("Fact") || names.Contains("Theory")) return "xUnit";
        if (names.Contains("TestMethod") || names.Contains("DataTestMethod") || names.Contains("DataRow")) return "MSTest";
        return names.Contains("Test") || names.Contains("TestCase") || names.Contains("TestCaseSource") ? "NUnit" : "unknown";
    }
}
