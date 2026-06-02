using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Testing;

[McpServerToolType]
public static class GetTestCoverageMapTool
{
    [McpServerTool, Description("Run tests with code coverage collection and return per-class and per-method line coverage rates. Requires LoadSolution first and 'coverlet.collector' in test projects.")]
    public static async Task<object> GetTestCoverageMap(
        [Description("Optional: restrict to a single test project by name")] string? projectName = null,
        [Description("Minimum line coverage % to include in results (default: 0 = all)")] double minCoverage = 0)
    {
        var service  = RoslynWorkspaceService.Instance;
        var solution = service.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var solutionPath = service.LoadedSolutionPath!;
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mcp-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var target = solutionPath;
            var filter = projectName is not null ? $"--filter \"FullyQualifiedName~{projectName}\"" : "";
            var args   = $"test \"{target}\" {filter} --collect \"XPlat Code Coverage\" --results-directory \"{tmpDir}\" --no-build 2>&1";

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
            double totalLines = 0, coveredLines = 0;

            foreach (var file in coverageFiles)
            {
                var (packages, tl, cl) = ParseCobertura(file, minCoverage);
                allPackages.AddRange(packages);
                totalLines   += tl;
                coveredLines += cl;
            }

            return new
            {
                projectFilter      = projectName ?? "all",
                minCoverage,
                overallLineCoverage = totalLines > 0 ? Math.Round(coveredLines / totalLines * 100, 2) : 0,
                coveredLines        = (int)coveredLines,
                totalLines          = (int)totalLines,
                packages            = allPackages,
            };
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static (List<object> packages, double totalLines, double coveredLines) ParseCobertura(string path, double minCoverage)
    {
        var doc  = XDocument.Load(path);
        var root = doc.Root!;

        var packages   = new List<object>();
        double total   = 0, covered = 0;

        foreach (var pkg in root.Descendants("package"))
        {
            var classes = new List<object>();
            foreach (var cls in pkg.Elements("classes").Elements("class"))
            {
                var lineRate = double.TryParse(cls.Attribute("line-rate")?.Value, out var lr) ? lr * 100 : 0;
                if (lineRate < minCoverage) continue;

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
                    ClassName    = cls.Attribute("name")?.Value,
                    FilePath     = cls.Attribute("filename")?.Value,
                    LineCoverage = Math.Round(lineRate, 2),
                    TotalLines   = classTotal,
                    CoveredLines = classCovered,
                    Methods      = methods,
                });
            }

            if (classes.Count > 0)
                packages.Add(new
                {
                    PackageName = pkg.Attribute("name")?.Value,
                    Classes     = classes,
                });
        }

        return (packages, total, covered);
    }
}
