using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// make_non_static: the inverse of make_static. The declaration always loses `static`; a call site is
/// rewritten only when an instance of the type is already in scope there, and the whole operation is
/// refused when one is not — nothing is invented.
/// </summary>
public sealed class MakeNonStaticTests
{
    [Fact]
    public async Task A_qualified_call_from_a_local_is_routed_through_it()
    {
        using var workspace = FakeWorkspace.Create(
            ("Counter.cs", """
                namespace Probe;

                public class Counter
                {
                    public static int Total() => 1;
                }
                """),
            ("Use.cs", """
                namespace Probe;

                public class Use
                {
                    public int Run()
                    {
                        var counter = new Counter();
                        return Counter.Total();
                    }
                }
                """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Counter.cs"), "Total", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public int Total()", workspace.Read("Counter.cs"));
        Assert.DoesNotContain("static", workspace.Read("Counter.cs"));
        Assert.Contains("return counter.Total();", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task A_field_of_the_type_is_the_receiver()
    {
        using var workspace = FakeWorkspace.Create(
            ("Logger.cs", """
                namespace Probe;

                public class Logger
                {
                    public static void Write(string message) { }
                }
                """),
            ("Service.cs", """
                namespace Probe;

                public class Service
                {
                    private readonly Logger _logger = new();

                    public void Run() => Logger.Write("x");
                }
                """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Logger.cs"), "Write", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("_logger.Write(\"x\")", workspace.Read("Service.cs"));
        Assert.Contains("public void Write(string message)", workspace.Read("Logger.cs"));
    }

    [Fact]
    public async Task An_unqualified_call_inside_the_type_stays_unqualified()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", """
            namespace Probe;

            public class Counter
            {
                public static int Total() => 1;

                public int Twice() => Total() * 2;
            }
            """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Counter.cs"), "Total", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Counter.cs");
        Assert.Contains("public int Total()", text);
        Assert.Contains("Twice() => Total() * 2", text);
    }

    [Fact]
    public async Task A_member_with_no_references_still_loses_static()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", """
            namespace Probe;

            public class Counter
            {
                public static int Total() => 1;
            }
            """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Counter.cs"), "Total", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public int Total()", workspace.Read("Counter.cs"));
        Assert.DoesNotContain("static", workspace.Read("Counter.cs"));
    }

    [Fact]
    public async Task A_call_site_without_an_instance_refuses_the_whole_operation()
    {
        using var workspace = FakeWorkspace.Create(
            ("Counter.cs", """
                namespace Probe;

                public class Counter
                {
                    public static int Total() => 1;
                }
                """),
            ("Use.cs", """
                namespace Probe;

                public class Use
                {
                    public static int Run() => Counter.Total();
                }
                """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Counter.cs"), "Total", dryRun: false);

        Assert.Contains("no instance", ResultAssert.Error(result));
        Assert.Contains("public static int Total()", workspace.Read("Counter.cs"));
        Assert.Contains("Counter.Total()", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task A_static_property_becomes_an_instance_property()
    {
        using var workspace = FakeWorkspace.Create(
            ("Settings.cs", """
                namespace Probe;

                public class Settings
                {
                    public static int Timeout => 30;
                }
                """),
            ("Use.cs", """
                namespace Probe;

                public class Use
                {
                    public int Read()
                    {
                        var settings = new Settings();
                        return Settings.Timeout;
                    }
                }
                """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Settings.cs"), "Timeout", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public int Timeout => 30", workspace.Read("Settings.cs"));
        Assert.Contains("return settings.Timeout;", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task A_dry_run_reports_the_change_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", """
            namespace Probe;

            public class Counter
            {
                public static int Total() => 1;
            }
            """));

        var result = await new MakeNonStaticService(workspace)
            .MakeNonStatic(workspace.PathOf("Counter.cs"), "Total", dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.True(ResultAssert.Json(result).GetProperty("dryRun").GetBoolean());
        Assert.Contains("public static int Total()", workspace.Read("Counter.cs"));
    }
}
