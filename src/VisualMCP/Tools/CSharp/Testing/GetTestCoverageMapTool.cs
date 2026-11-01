using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class GetTestCoverageMapTool
{
    [McpServerTool, Description(
        "When you need to know which code is exercised by tests, use this to run the suite with coverage collection and get per-class and per-method line-coverage rates " +
        "plus the branch coverage Cobertura reports. Requires 'coverlet.collector' in the test projects. The working-directory solution auto-loads on first use.")]
    public static Task<object> GetTestCoverageMap(
        IGetTestCoverageMapService svc,
        [Description("Optional: the test project to run (by name); omit to run the whole solution")] string? projectName = null,
        [Description("Minimum line coverage % to include in results (default: 0 = all)")] double minCoverage = 0)
        => svc.GetTestCoverageMap(projectName, minCoverage);
}