using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Testing;

[McpServerToolType]
public static class RunTestsTool
{
    [McpServerTool, Description(
        "Run all tests in the loaded solution using 'dotnet test' and return structured results " +
        "(pass/fail/skip counts, duration, and failure details). Requires LoadSolution to have been called first.")]
    public static async Task<object> RunTests(
        [Description("Optional: run only projects whose name contains this string (case-insensitive)")] string? projectFilter = null,
        [Description("Optional: extra arguments forwarded verbatim to 'dotnet test' (e.g. '--no-build', '--configuration Release')")] string? extraArgs = null)
    {
        var service  = RoslynWorkspaceService.Instance;
        var solution = await service.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var solutionPath = service.LoadedSolutionPath!;

        // Identify test projects from the Roslyn model
        var testProjects = solution.Projects
            .Where(IsTestProject)
            .Where(p => projectFilter is null ||
                        (p.Name?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(p => p.Name)
            .ToList();

        // Run dotnet test against the solution (or individual projects if filtered)
        var tmpDir = Path.Combine(Path.GetTempPath(), $"mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var target  = solutionPath;
            var allArgs = BuildArgs(target, tmpDir, projectFilter, extraArgs);

            var (exitCode, stdout, stderr, elapsed) = await ExecAsync("dotnet", allArgs);

            // Parse every .trx produced under tmpDir
            var trxFiles = Directory.GetFiles(tmpDir, "*.trx", SearchOption.AllDirectories);
            var projectResults = trxFiles.Select(f => ParseTrx(f)).ToList();

            var totalPassed  = projectResults.Sum(r => r.Passed);
            var totalFailed  = projectResults.Sum(r => r.Failed);
            var totalSkipped = projectResults.Sum(r => r.Skipped);
            var totalTests   = projectResults.Sum(r => r.Total);

            return new
            {
                solutionPath,
                testProjectsDetected = testProjects,
                summary = new
                {
                    total    = totalTests,
                    passed   = totalPassed,
                    failed   = totalFailed,
                    skipped  = totalSkipped,
                    outcome  = exitCode == 0 ? "Passed" : "Failed",
                    durationMs = (long)elapsed.TotalMilliseconds,
                },
                projectResults,
                // Include raw stdout only when there are no TRX files (build errors, etc.)
                rawOutput = trxFiles.Length == 0 ? new { stdout, stderr } : null,
            };
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static bool IsTestProject(Project p)
    {
        if (p.FilePath is null || !File.Exists(p.FilePath)) return false;
        var xml = File.ReadAllText(p.FilePath);
        return xml.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildArgs(string target, string resultsDir, string? filter, string? extra)
    {
        var parts = new List<string>
        {
            "test",
            $"\"{target}\"",
            $"--logger \"trx\"",
            $"--results-directory \"{resultsDir}\"",
        };
        if (filter is not null)
            parts.Add($"--filter \"FullyQualifiedName~{filter}\"");
        if (extra is not null)
            parts.Add(extra);
        return string.Join(" ", parts);
    }

    private static async Task<(int exitCode, string stdout, string stderr, TimeSpan elapsed)> ExecAsync(
        string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        await proc.WaitForExitAsync();
        sw.Stop();

        return (proc.ExitCode, await stdoutTask, await stderrTask, sw.Elapsed);
    }

    private static TrxProjectResult ParseTrx(string trxPath)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var doc = XDocument.Load(trxPath);

        // Project name from the TestRun name attribute ("projectName @ timestamp")
        var runName    = doc.Root?.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(trxPath);
        var projectName = runName.Contains('@') ? runName[..runName.LastIndexOf('@')].Trim() : runName;

        // Counters
        var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
        int total    = Attr(counters, "total");
        int passed   = Attr(counters, "passed");
        int failed   = Attr(counters, "failed");
        int skipped  = Attr(counters, "notExecuted") + Attr(counters, "disconnected");

        // Failed test details
        var failures = doc.Descendants(ns + "UnitTestResult")
            .Where(e => e.Attribute("outcome")?.Value is "Failed" or "Error")
            .Select(e =>
            {
                var output = e.Element(ns + "Output");
                var msg    = output?.Element(ns + "ErrorInfo")?.Element(ns + "Message")?.Value?.Trim();
                var trace  = output?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace")?.Value?.Trim();
                return new
                {
                    TestName   = e.Attribute("testName")?.Value,
                    Duration   = e.Attribute("duration")?.Value,
                    Message    = msg,
                    StackTrace = trace,
                };
            })
            .ToList();

        return new TrxProjectResult(projectName, total, passed, failed, skipped, failures.Cast<object>().ToList());

        int Attr(XElement? el, string attr) =>
            el is null ? 0 : int.TryParse(el.Attribute(attr)?.Value, out var v) ? v : 0;
    }
}

internal record TrxProjectResult(
    string ProjectName,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<object> FailedTests);
