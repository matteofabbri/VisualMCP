using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Workspace;

namespace Snitch.Tests;

/// <summary>
/// Exercises the inspect_code engine end to end on an in-memory solution: the shipped packs must
/// load, produce findings against deliberately sloppy code, and honour the id/file filters.
/// This is what proves the engine works without any IDE or analyzer package in the inspected
/// project.
/// </summary>
[TestFixture]
public class InspectionEngineTests
{
    private const string ViolatingSource = """
        using System.Threading.Tasks;

        public class Sample
        {
            public int Unused(int argument)
            {
                var unusedLocal = 42;
                return 0;
            }

            public int Block(Task<int> task) => task.Result;
        }
        """;

    private const string CleanSource = """
        public static class Sample
        {
            public static int Add(int left, int right) => left + right;
        }
        """;

    [Test]
    public async Task Packs_Produce_Findings_In_Rider_Shape()
    {
        var report = await Inspect(ViolatingSource);

        Assert.That(report.State, Is.EqualTo("completed"), report.Error);
        Assert.That(report.Packs, Is.Not.Empty, "no inspection pack could be loaded");
        Assert.That(report.Total, Is.GreaterThan(0), "sloppy code produced no findings at all");
        Assert.That(report.Findings, Is.Not.Empty);

        Assert.That(report.ByInspection.Keys, Has.Some.StartsWith("RCS"),
            "the Roslynator pack must run (ReSharper-style inspections)");

        var finding = report.Findings[0];
        Assert.That(finding.InspectionId, Is.Not.Empty);
        Assert.That(finding.Message, Is.Not.Empty);
        Assert.That(finding.Line, Is.GreaterThan(0));
        Assert.That(finding.Severity, Is.AnyOf("ERROR", "WARNING", "WEAK_WARNING", "HINT"));
    }

    [Test]
    public async Task Id_Filter_Includes_And_Excludes()
    {
        var onlyRoslynator = await Inspect(ViolatingSource, ids: "RCS*");
        Assert.That(onlyRoslynator.ByInspection.Keys, Has.All.StartsWith("RCS"));

        var withoutRoslynator = await Inspect(ViolatingSource, excludeIds: "RCS*");
        Assert.That(withoutRoslynator.ByInspection.Keys, Has.None.StartsWith("RCS"));
    }

    [Test]
    public async Task Severity_Floor_Narrows_The_Report()
    {
        var everything = await Inspect(ViolatingSource, minSeverity: "info");
        var important = await Inspect(ViolatingSource, minSeverity: "warning");

        Assert.That(everything.Total, Is.GreaterThan(0));
        Assert.That(important.Total, Is.LessThan(everything.Total),
            "raising the floor to 'warning' must drop the suggestion-level rules");
        Assert.That(important.BySeverity.Keys, Is.SubsetOf(new[] { "ERROR", "WARNING" }));
    }

    [Test]
    public async Task Unknown_Project_Is_Reported_As_Failure()
    {
        var report = await Inspect(ViolatingSource, projectName: "DoesNotExist");

        Assert.That(report.State, Is.EqualTo("failed"));
        Assert.That(report.Error, Does.Contain("DoesNotExist"));
    }

    [Test]
    public async Task Clean_Source_Is_Quieter_Than_Sloppy_Source()
    {
        var sloppy = await Inspect(ViolatingSource);
        var clean = await Inspect(CleanSource);

        Assert.That(clean.Total, Is.LessThan(sloppy.Total));
    }

    private static async Task<InspectionReport> Inspect(
        string source,
        string? ids = null,
        string? excludeIds = null,
        string? projectName = null,
        string minSeverity = "info")
    {
        using var workspace = new AdhocWorkspace();

        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var filePath = Path.Combine(Path.GetTempPath(), "visualmcp-inspections", "Sample.cs");

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Sample", "Sample", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(projectId,
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(projectId,
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location))
            .AddDocument(documentId, "Sample.cs", SourceText.From(source), filePath: filePath);

        var service = new InspectCodeService(new StubWorkspace(solution));

        return await service.RunAsync(
            projectName: projectName,
            filePath: null,
            packs: "",
            minSeverity: minSeverity,
            ids: ids,
            excludeIds: excludeIds,
            enableAll: true,
            maxResults: 100,
            timeoutSeconds: 180);
    }

    /// <summary>Minimal <see cref="IRoslynWorkspace"/> handing back a solution built in memory.</summary>
    private sealed class StubWorkspace(Solution solution) : IRoslynWorkspace
    {
        public Solution? CurrentSolution => solution;

        public string? LoadedSolutionPath => Path.Combine(Path.GetTempPath(), "Sample.sln");

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(solution);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => null;

        public void Dispose() { }
    }
}
