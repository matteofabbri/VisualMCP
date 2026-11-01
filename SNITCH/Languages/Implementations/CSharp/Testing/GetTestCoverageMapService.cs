using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Xml.Linq;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

public interface IGetTestCoverageMapService
{
    Task<object> GetTestCoverageMap(string? projectName, double minCoverage);
}

public sealed class GetTestCoverageMapService : IGetTestCoverageMapService
{
    private readonly IRoslynWorkspace _workspace;
    public GetTestCoverageMapService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GetTestCoverageMap(
        string? projectName = null,
        double minCoverage = 0)
    {
        var service  = _workspace;
        var solution = await service.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        var solutionPath = service.LoadedSolutionPath;
        var target = solutionPath;
        string? scope = null;

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var project = TestSuiteRunner.ResolveTestProject(solution, projectName, out var resolveError);
            if (project?.FilePath is null)
                return new
                {
                    error = resolveError ?? $"Project '{projectName}' has no file on disk.",
                    projects = solution.Projects.Select(candidate => candidate.Name).ToList(),
                };

            target = project.FilePath;
            scope  = project.Name;
        }

        if (string.IsNullOrWhiteSpace(target))
            return new { error = "The solution has no path on disk: pass projectName (a test project) instead." };

        var tmpDir = Path.Combine(Path.GetTempPath(), $"mcp-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var args = $"test \"{target}\" --collect \"XPlat Code Coverage\" --results-directory \"{tmpDir}\" 2>&1";

            var psi = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Cobertura XML produced by XPlat Code Coverage
            var coverageFiles = Directory.GetFiles(tmpDir, "coverage.cobertura.xml", SearchOption.AllDirectories);

            if (coverageFiles.Length == 0)
                return new
                {
                    error  = "No coverage file produced. Ensure test projects reference 'coverlet.collector'.",
                    stdout = stdout.Length > 2000 ? stdout[^2000..] : stdout,
                    stderr = stderr.Length > 2000 ? stderr[^2000..] : stderr,
                };

            var allPackages = new List<object>();
            double totalLines = 0, coveredLines = 0, validBranches = 0, coveredBranches = 0;

            foreach (var file in coverageFiles)
            {
                var (packages, tl, cl, vb, cb) = ParseCobertura(file, minCoverage);
                allPackages.AddRange(packages);
                totalLines      += tl;
                coveredLines    += cl;
                validBranches   += vb;
                coveredBranches += cb;
            }

            return new
            {
                projectFilter      = scope ?? "all",
                target,
                minCoverage,
                overallLineCoverage = totalLines > 0 ? Math.Round(coveredLines / totalLines * 100, 2) : 0,
                coveredLines        = (int)coveredLines,
                totalLines          = (int)totalLines,
                overallBranchCoverage = validBranches > 0 ? Math.Round(coveredBranches / validBranches * 100, 2) : 0,
                coveredBranches     = (int)coveredBranches,
                totalBranches       = (int)validBranches,
                packages            = allPackages,
            };
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static (List<object> packages, double totalLines, double coveredLines, double validBranches,
        double coveredBranches) ParseCobertura(string path, double minCoverage)
    {
        var doc  = XDocument.Load(path);
        var root = doc.Root!;

        var packages   = new List<object>();
        double total   = 0, covered = 0, branchValid = 0, branchCovered = 0;

        foreach (var pkg in root.Descendants("package"))
        {
            var classes = new List<object>();
            foreach (var cls in pkg.Elements("classes").Elements("class"))
            {
                var lineRate = double.TryParse(cls.Attribute("line-rate")?.Value, out var lr) ? lr * 100 : 0;
                if (lineRate < minCoverage) continue;

                var branchRate = double.TryParse(cls.Attribute("branch-rate")?.Value, out var br) ? br * 100 : 0;
                var classBranchValid = int.TryParse(cls.Attribute("branches-valid")?.Value, out var cv) ? cv : 0;
                var classBranchCovered = int.TryParse(cls.Attribute("branches-covered")?.Value, out var cc) ? cc : 0;
                branchValid += classBranchValid;
                branchCovered += classBranchCovered;

                var methods = cls.Elements("methods").Elements("method").Select(m => new
                {
                    Name       = m.Attribute("name")?.Value,
                    Signature  = m.Attribute("signature")?.Value,
                    LineRate   = double.TryParse(m.Attribute("line-rate")?.Value, out var mr) ? Math.Round(mr * 100, 2) : 0,
                }).ToList();

                var classLines   = cls.Descendants("line").ToList();
                var classTotal   = classLines.Count;
                var classCovered = classLines.Count(l => int.TryParse(l.Attribute("hits")?.Value, out var h) && h > 0);
                total   += classTotal;
                covered += classCovered;

                classes.Add(new
                {
                    ClassName      = cls.Attribute("name")?.Value,
                    FilePath       = cls.Attribute("filename")?.Value,
                    LineCoverage   = Math.Round(lineRate, 2),
                    TotalLines     = classTotal,
                    CoveredLines   = classCovered,
                    BranchCoverage = Math.Round(branchRate, 2),
                    TotalBranches  = classBranchValid,
                    CoveredBranches = classBranchCovered,
                    Methods        = methods,
                });
            }

            if (classes.Count > 0)
                packages.Add(new
                {
                    PackageName = pkg.Attribute("name")?.Value,
                    Classes     = classes,
                });
        }

        return (packages, total, covered, branchValid, branchCovered);
    }
}
