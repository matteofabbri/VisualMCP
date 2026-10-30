using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeTestQualityTool
{
    [McpServerTool(Name = "analyze_test_quality"), Description(
        "What the test suite says about itself: tests with no assertion (they cannot fail), empty tests, " +
        "names like 'Test1' (unreadable when it fails at 3am) and async void (the runner cannot observe " +
        "the failure). Recognises xUnit, NUnit and MSTest attributes and the usual assertion styles " +
        "(Assert.*, FluentAssertions .Should, Moq .Verify).")]
    public static Task<object> AnalyzeTestQuality(
        IAnalyzeTestQualityService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("How many flagged tests to return (default 100)")] int top = 100)
        => svc.Analyze(projectName, top);
}
