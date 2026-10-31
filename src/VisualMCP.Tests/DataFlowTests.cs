using Snitch.Languages.CSharp.Analysis.DataFlow;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// trace_data_flow and find_throw_sites: the two questions the pattern-based security scan cannot
/// answer. Each test states one reach — a source that arrives at a sink, a sanitizer that stops it, a
/// parameter that becomes a source — and the throw search proves direct, indirect and boundary.
/// </summary>
public sealed class DataFlowTests
{
    private static async Task<System.Text.Json.JsonElement> TraceAsync(string source, int depth = 2)
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", source));
        var service = new TraceDataFlowService(workspace);
        var result = await service.Trace(null, null, depth, 200);
        ResultAssert.Succeeded(result);
        return ResultAssert.Json(result);
    }

    private static async Task<System.Text.Json.JsonElement> ThrowsAsync(string source, string exception, bool transitive = true)
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", source));
        var service = new FindThrowSitesService(workspace);
        var result = await service.Find(exception, transitive, null, 200);
        ResultAssert.Succeeded(result);
        return ResultAssert.Json(result);
    }

    [Fact]
    public async Task AConsoleValueReachesAFileOperation()
    {
        var json = await TraceAsync("""
            using System;
            using System.IO;

            public class Probe
            {
                public void Run()
                {
                    var path = Console.ReadLine();
                    File.ReadAllText(path);
                }
            }
            """);

        Assert.Equal(1, json.GetProperty("flowCount").GetInt32());
        var flow = json.GetProperty("flows")[0];
        Assert.Equal("CWE-22", flow.GetProperty("sink").GetProperty("cwe").GetString());
        Assert.Equal("Path traversal", flow.GetProperty("sink").GetProperty("kind").GetString());
        Assert.Equal("path", flow.GetProperty("sink").GetProperty("operation").GetString());
        Assert.Contains("console input", flow.GetProperty("source").GetProperty("kind").GetString());
        Assert.True(flow.GetProperty("hops").GetInt32() >= 1);
    }

    [Fact]
    public async Task AParsedNumberIsNotTaintedAnyMore()
    {
        var json = await TraceAsync("""
            using System;
            using System.IO;

            public class Probe
            {
                public void Run()
                {
                    var id = int.Parse(Console.ReadLine());
                    File.ReadAllText(id.ToString());
                }
            }
            """);

        Assert.Equal(0, json.GetProperty("flowCount").GetInt32());
    }

    [Fact]
    public async Task AGuardedParameterIsASource()
    {
        var json = await TraceAsync("""
            using System;
            using System.IO;

            public sealed class FromQueryAttribute : Attribute { }

            public class Probe
            {
                public void Run([FromQuery] string name)
                {
                    File.ReadAllText(name);
                }
            }
            """);

        Assert.Equal(1, json.GetProperty("flowCount").GetInt32());
        Assert.Equal("model-bound parameter", json.GetProperty("flows")[0].GetProperty("source").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TaintFollowsAnArgumentIntoTheCalleeOnlyWhenDepthAllowsIt()
    {
        const string source = """
            using System;
            using System.IO;

            public class Probe
            {
                public void Entry()
                {
                    var query = Console.ReadLine();
                    Load(query);
                }

                private static void Load(string path) => File.ReadAllText(path);
            }
            """;

        var withDepth = await TraceAsync(source, depth: 1);
        Assert.Equal(1, withDepth.GetProperty("flowCount").GetInt32());
        Assert.Contains(
            withDepth.GetProperty("flows")[0].GetProperty("steps").EnumerateArray(),
            step => step.GetProperty("what").GetString() == "passed to Load");

        var intraOnly = await TraceAsync(source, depth: 0);
        Assert.Equal(0, intraOnly.GetProperty("flowCount").GetInt32());
    }

    [Fact]
    public async Task AProcessStartedWithRequestDataIsCommandInjection()
    {
        var json = await TraceAsync("""
            using System;
            using System.Diagnostics;

            public class Probe
            {
                public void Run()
                {
                    Process.Start(Environment.GetEnvironmentVariable("TARGET"));
                }
            }
            """);

        Assert.Equal(1, json.GetProperty("flowCount").GetInt32());
        Assert.Equal("CWE-78", json.GetProperty("flows")[0].GetProperty("sink").GetProperty("cwe").GetString());
    }

    [Fact]
    public async Task AValueStoredInAFieldIsFollowedWithinTheMethod()
    {
        var json = await TraceAsync("""
            using System;
            using System.IO;

            public class Probe
            {
                private string _path;

                public void Run()
                {
                    _path = Console.ReadLine();
                    File.ReadAllText(_path);
                }
            }
            """);

        Assert.Equal(1, json.GetProperty("flowCount").GetInt32());
        Assert.Contains(
            json.GetProperty("flows")[0].GetProperty("steps").EnumerateArray(),
            step => step.GetProperty("what").GetString() == "stored in field _path");
    }

    [Fact]
    public async Task AFieldWrittenInOneMethodIsFollowedInAnother()
    {
        var json = await TraceAsync("""
            using System;
            using System.IO;

            public class Probe
            {
                private string _path;

                public void Store() => _path = Console.ReadLine();

                public void Sink() => File.ReadAllText(_path);
            }
            """);

        Assert.Equal(1, json.GetProperty("flowCount").GetInt32());
        Assert.Contains(
            json.GetProperty("flows")[0].GetProperty("steps").EnumerateArray(),
            step => step.GetProperty("what").GetString() == "stored in field _path");
    }

    [Fact]
    public async Task AFieldThatNeverHoldsATaintedValueProducesNoFlow()
    {
        var json = await TraceAsync("""
            using System.IO;

            public class Probe
            {
                private string _path = "default.txt";

                public void Run()
                {
                    _path = "other.txt";
                    File.ReadAllText(_path);
                }
            }
            """);

        Assert.Equal(0, json.GetProperty("flowCount").GetInt32());
    }

    [Fact]
    public async Task CleanCodeHasNoFlows()
    {
        var json = await TraceAsync("""
            using System.IO;

            public class Probe
            {
                public string Run(string path) => File.ReadAllText(path ?? "default.txt");
            }
            """);

        Assert.Equal(0, json.GetProperty("flowCount").GetInt32());
        Assert.Equal("all", json.GetProperty("projectFilter").GetString());
    }

    [Fact]
    public async Task TheFilterSelectsOneWeakness()
    {
        var source = """
            using System;
            using System.Diagnostics;
            using System.IO;

            public class Probe
            {
                public void Run()
                {
                    Process.Start(Console.ReadLine());
                    File.ReadAllText(Console.ReadLine());
                }
            }
            """;

        using var workspace = FakeWorkspace.Create(("Probe.cs", source));
        var service = new TraceDataFlowService(workspace);

        var all = ResultAssert.Json(await service.Trace(null, null, 2, 200));
        Assert.Equal(2, all.GetProperty("flowCount").GetInt32());

        var commandOnly = ResultAssert.Json(await service.Trace(null, "78", 2, 200));
        Assert.Equal(1, commandOnly.GetProperty("flowCount").GetInt32());
        Assert.Equal("CWE-78", commandOnly.GetProperty("flows")[0].GetProperty("sink").GetProperty("cwe").GetString());
    }

    [Fact]
    public async Task DirectAndIndirectThrowersAreSeparated()
    {
        var json = await ThrowsAsync("""
            using System;

            public class Probe
            {
                public void Top() => Middle();

                private static void Middle() => throw new InvalidOperationException("boom");

                public void Catches()
                {
                    try { Middle(); }
                    catch (InvalidOperationException) { }
                }
            }
            """, "InvalidOperationException");

        Assert.Equal("System.InvalidOperationException", json.GetProperty("resolvedType").GetString());
        Assert.Equal(1, json.GetProperty("directCount").GetInt32());

        var throwers = json.GetProperty("throwers");
        Assert.Equal(2, throwers.GetArrayLength());
        Assert.Contains(throwers.EnumerateArray(), entry =>
            entry.GetProperty("method").GetString() == "Probe.Middle" &&
            entry.GetProperty("kind").GetString() == "direct");

        var top = Assert.Single(
            throwers.EnumerateArray(),
            entry => entry.GetProperty("method").GetString() == "Probe.Top");
        Assert.Equal("indirect", top.GetProperty("kind").GetString());
        Assert.Equal(1, top.GetProperty("path").GetArrayLength());

        Assert.Equal(1, json.GetProperty("boundaryCount").GetInt32());
        Assert.Equal("Probe.Catches", json.GetProperty("boundaries")[0].GetProperty("method").GetString());
    }

    [Fact]
    public async Task WithoutTransitiveOnlyTheDirectThrowIsReturned()
    {
        var json = await ThrowsAsync("""
            using System;

            public class Probe
            {
                public void Top() => Middle();
                private static void Middle() => throw new ArgumentException("boom");
            }
            """, "ArgumentException", transitive: false);

        Assert.Equal(1, json.GetProperty("throwerCount").GetInt32());
        Assert.Equal("direct", json.GetProperty("throwers")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TheBaseExceptionMatchesEveryDerivedType()
    {
        var json = await ThrowsAsync("""
            using System;

            public class Probe
            {
                public void A() => throw new InvalidOperationException();
                public void B() => throw new ArgumentException();
            }
            """, "Exception");

        Assert.Equal(2, json.GetProperty("directCount").GetInt32());
        Assert.Contains("System.Exception", json.GetProperty("resolvedType").GetString());
    }

    [Fact]
    public async Task AnUnknownExceptionNameIsRefusedByTheResolverNotByAnEmptyList()
    {
        var json = await ThrowsAsync("""
            public class Probe { public void A() { } }
            """, "NotAnExceptionAtAll");

        Assert.Equal(0, json.GetProperty("directCount").GetInt32());
        Assert.Equal("System.NotAnExceptionAtAll", json.GetProperty("resolvedType").GetString());
    }
}
