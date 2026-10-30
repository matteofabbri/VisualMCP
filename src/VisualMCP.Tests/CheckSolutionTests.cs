using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Snitch.IO;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// check_solution is the first call an agent makes: a Visual Basic solution must not be reported
/// as "not supported", because the language-neutral tools and the VB style engine do work on one.
/// </summary>
public sealed class CheckSolutionTests
{
    [Fact]
    public void A_Visual_Basic_only_solution_reports_its_projects()
    {
        using var workspace = new VisualBasicOnlyWorkspace();

        var result = new IoService(workspace).CheckSolution();

        Assert.True(result.IsLoaded);
        Assert.Equal(0, result.CSharpProjectCount);
        Assert.Equal(1, result.VisualBasicProjectCount);
        Assert.Contains("Visual Basic", result.Message!);
        Assert.Contains("simplify_vb_code", result.Message!);
    }

    private sealed class VisualBasicOnlyWorkspace : IRoslynWorkspace
    {
        private readonly AdhocWorkspace _workspace = new();
        private readonly Solution _solution;

        public VisualBasicOnlyWorkspace()
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "VbOnly",
                "VbOnly",
                LanguageNames.VisualBasic,
                compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            _solution = _workspace.CurrentSolution
                .AddProject(projectInfo)
                .AddDocument(
                    DocumentId.CreateNewId(projectId),
                    "Module1.vb",
                    SourceText.From("Public Module Module1\nEnd Module\n"));
        }

        public Solution CurrentSolution => _solution;

        public string? LoadedSolutionPath => "VbOnly.sln";

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(_solution);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => LoadedSolutionPath;

        public bool TryUpdateDocument(DocumentId documentId, SourceText text) => true;

        public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text) => null;

        public bool TryRemoveDocument(DocumentId documentId) => false;

        public void Dispose() => _workspace.Dispose();
    }
}
