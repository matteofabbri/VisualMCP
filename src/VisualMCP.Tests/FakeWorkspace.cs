using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace VisualMCP.Tests;

/// <summary>
/// The refactoring services only need <see cref="IRoslynWorkspace"/>: this builds a real solution over
/// real files in a temp folder, so the services write through their normal path and the test can read
/// the result from disk. Implements the mutation contract too, so chained calls behave as in the server.
/// </summary>
internal sealed class FakeWorkspace : IRoslynWorkspace
{
    private readonly AdhocWorkspace _workspace = new();
    private readonly string _directory;
    private Solution _solution = null!;

    private FakeWorkspace()
    {
        _directory = Path.Combine(Path.GetTempPath(), "vmcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public static FakeWorkspace Create(params (string Name, string Source)[] files) => Build(files, editorConfig: null);

    /// <summary>
    /// A workspace whose project also references the given assemblies (the test framework, a library),
    /// for the tools whose gate must compile against something more than the platform.
    /// </summary>
    public static FakeWorkspace CreateWithReferences(string[] references, params (string Name, string Source)[] files)
    {
        var fake = Build(files, editorConfig: null);
        var project = fake._solution.Projects.Single();
        var withReferences = project.AddMetadataReferences(
            references.Where(File.Exists).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)));

        fake._solution = withReferences.Solution;
        return fake;
    }

    /// <summary>
    /// A workspace whose project carries an <c>.editorconfig</c>, so the option-driven rules can be
    /// tested through the real Roslyn pipeline instead of hand-built options.
    /// </summary>
    public static FakeWorkspace CreateWithEditorConfig(string editorConfig, params (string Name, string Source)[] files) =>
        Build(files, editorConfig);

    private static FakeWorkspace Build((string Name, string Source)[] files, string? editorConfig)
    {
        var fake = new FakeWorkspace();

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Probe",
            "Probe",
            LanguageNames.CSharp,
            filePath: Path.Combine(fake._directory, "Probe.csproj"),
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular),
            metadataReferences: TrustedPlatformReferences());

        var solution = fake._workspace.CurrentSolution.AddProject(projectInfo);
        foreach (var (name, source) in files)
        {
            var path = fake.PathOf(name);
            File.WriteAllText(path, source);
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), name, SourceText.From(source), filePath: path);
        }

        if (editorConfig is not null)
        {
            var configPath = Path.Combine(fake._directory, ".editorconfig");
            File.WriteAllText(configPath, editorConfig);
            var configInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                ".editorconfig",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(editorConfig), VersionStamp.Create(), configPath)),
                filePath: configPath);

            solution = solution.AddAnalyzerConfigDocuments([configInfo]);
        }

        // A real file, because the store derives the folder for .visualMCP from the solution path:
        // without it every test would share the system temp folder's database.
        File.WriteAllText(fake.PathOf("Probe.sln"), string.Empty);

        // A real project file too: the implicit-usings toggle edits it on disk.
        File.WriteAllText(fake.PathOf("Probe.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        fake._solution = solution;
        return fake;
    }

    /// <summary>
    /// Several projects in one solution, each referencing the ones declared before it. The multi-file
    /// suggestions are resolved against the whole solution, so a test has to be able to put the two
    /// files in two different projects.
    /// </summary>
    public static FakeWorkspace CreateProjects(params (string Project, (string Name, string Source)[] Files)[] projects)
    {
        var fake = new FakeWorkspace();
        var solution = fake._workspace.CurrentSolution;
        var ids = new List<ProjectId>();

        foreach (var (name, files) in projects)
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath: Path.Combine(fake._directory, name + ".csproj"),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: true),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular),
                metadataReferences: TrustedPlatformReferences(),
                projectReferences: ids.Select(id => new ProjectReference(id)).ToList());

            solution = solution.AddProject(projectInfo);
            foreach (var (fileName, source) in files)
            {
                var path = fake.PathOf(fileName);
                File.WriteAllText(path, source);
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), fileName, SourceText.From(source), filePath: path);
            }

            ids.Add(projectId);
        }

        File.WriteAllText(fake.PathOf("Probe.sln"), string.Empty);
        fake._solution = solution;
        return fake;
    }

    public string PathOf(string name) => Path.Combine(_directory, name);

    /// <summary>The temp folder that backs the fake solution (not named Directory: that would shadow System.IO.Directory).</summary>
    public string RootDirectory => _directory;

    public string Read(string name) => File.ReadAllText(PathOf(name));

    public Solution? CurrentSolution => _solution;

    public string? LoadedSolutionPath => null;

    public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) => Task.FromResult<Solution?>(_solution);

    public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public string? DiscoverSolutionPath() => PathOf("Probe.sln");

    public bool TryUpdateDocument(DocumentId documentId, SourceText text)
    {
        _solution = _solution.WithDocumentText(documentId, text);
        return true;
    }

    public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text)
    {
        if (_solution.GetProject(projectId) is null) return null;

        var documentId = DocumentId.CreateNewId(projectId);
        _solution = _solution.AddDocument(documentId, name, text, filePath: filePath);
        return documentId;
    }

    public bool TryRemoveDocument(DocumentId documentId)
    {
        if (_solution.GetDocument(documentId) is null) return false;

        _solution = _solution.RemoveDocument(documentId);
        return true;
    }

    public void Dispose()
    {
        _workspace.Dispose();
        DeleteBestEffort(_directory);
    }

    /// <summary>
    /// A temp folder left behind is not a test failure, and git writes its object files read-only on
    /// Windows: a plain recursive delete throws UnauthorizedAccessException on them.
    /// </summary>
    private static void DeleteBestEffort(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // fall through to the attribute-clearing retry
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder stays; the test result is what matters.
        }
    }

    private static List<MetadataReference> TrustedPlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var file &&
                           (file.StartsWith("System.", StringComparison.Ordinal) ||
                            file is "netstandard.dll" or "mscorlib.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}

/// <summary>Result assertions shared by the refactoring tests.</summary>
internal static class ResultAssert
{
    /// <summary>Fails with the service's own error text when the call reported one.</summary>
    public static void Succeeded(object result)
    {
        var json = JsonSerializer.SerializeToElement(result);
        if (json.TryGetProperty("error", out var error))
            throw new Xunit.Sdk.XunitException(
                "service returned an error: " + error.GetString() + Environment.NewLine + json.GetRawText());
    }

    /// <summary>The error text of a call that was expected to be refused.</summary>
    public static string Error(object result)
    {
        var json = JsonSerializer.SerializeToElement(result);
        if (!json.TryGetProperty("error", out var error) || error.GetString() is not { } text)
            throw new Xunit.Sdk.XunitException("expected a refusal, got: " + json.GetRawText());

        return text;
    }

    /// <summary>The result as JSON, for assertions on individual fields.</summary>
    public static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);
}
