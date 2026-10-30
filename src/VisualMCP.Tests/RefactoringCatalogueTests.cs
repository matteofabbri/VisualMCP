using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The "close the Rider catalogue" batch: move to folder, convert to record, introduce part of a string,
/// move static members, move an instance method, inline a helper class.
/// </summary>
public sealed class RefactoringCatalogueTests
{
    [Fact]
    public async Task MoveToFolder_MovesTheFileIntoTheFolderThatMatchesItsNamespace()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe.Sub;

            internal class C
            {
            }
            """));

        var service = new MoveToFolderService(workspace);
        var result = await service.MoveToFolder(workspace.PathOf("C.cs"), dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.True(File.Exists(Path.Combine(workspace.RootDirectory, "Sub", "C.cs")));
        Assert.False(File.Exists(workspace.PathOf("C.cs")));
    }

    [Fact]
    public async Task ConvertToRecord_PlainFormFlipsTheKeyword()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Widget
            {
                public int Value { get; set; }
            }
            """));

        var service = new ConvertToRecordService(workspace);
        var result = await service.ConvertToRecord(workspace.PathOf("C.cs"), "Widget", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("internal record Widget", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ConvertToRecord_PositionalFormReplacesTheConstructorAndTheAutoProperties()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Point
            {
                public int X { get; }
                public int Y { get; }

                public Point(int x, int y)
                {
                    X = x;
                    Y = y;
                }
            }
            """));

        var service = new ConvertToRecordService(workspace);
        var result = await service.ConvertToRecord(workspace.PathOf("C.cs"), "Point", positional: true, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.True(text.Contains("internal record Point(int X, int Y)"), text);
        Assert.DoesNotContain("public Point(", text);
        Assert.DoesNotContain("public int X { get; }", text);
    }

    [Fact]
    public async Task IntroducePartOfString_ExtractsTheSubstringIntoAConstant()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal static class C
            {
                public static string Greeting() => "Hello world";
            }
            """));

        var service = new IntroducePartOfStringService(workspace);
        var result = await service.IntroducePartOfString(
            workspace.PathOf("C.cs"), 5, "world", constantName: "Subject", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private const string Subject = \"world\";", text);
        Assert.True(text.Contains("\"Hello \" + Subject"), text);
    }

    [Fact]
    public async Task MoveStaticMembers_MovesTheMemberAndRequiresItsNewOwner()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", """
            namespace Probe;

            internal static class Source
            {
                public static int Twice(int value) => value * 2;

                public static int Quad(int value) => Twice(Twice(value));
            }

            internal static class Target
            {
            }
            """));

        var service = new MoveStaticMembersService(workspace);
        var result = await service.MoveStaticMembers(
            workspace.PathOf("A.cs"), "Source", new[] { "Twice" }, "Target", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("A.cs").Replace("\r\n", "\n");
        Assert.True(text.Contains("Target.Twice(Target.Twice(value))"), text);
        Assert.True(text.Contains("    public static int Twice(int value) => value * 2;"), text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "public static int Twice"));
    }

    [Fact]
    public async Task MoveInstanceMethod_TurnsThisIntoAParameterAndRewritesTheCallSites()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                namespace Probe;

                internal class Account
                {
                    private int _balance;

                    public int Balance => _balance;

                    public int Doubled() => Balance * 2;
                }

                internal static class Helpers
                {
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Run(Account account) => account.Doubled();
                }
                """));

        var service = new MoveInstanceMethodService(workspace);
        var result = await service.MoveInstanceMethod(
            workspace.PathOf("A.cs"), "Account", "Doubled", "Helpers", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public static int Doubled(Account self) => self.Balance * 2;", workspace.Read("A.cs"));
        Assert.Contains("Helpers.Doubled(account)", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task FunctionToIndexer_TurnsAMethodIntoAnIndexerAndRewritesTheCalls()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Bag
            {
                private readonly int[] _values = new int[3];

                public int Get(int index) => _values[index];

                public static int Use(Bag bag) => bag.Get(1);
            }
            """));

        var service = new IndexerConversionsService(workspace);
        var result = await service.FunctionToIndexer(workspace.PathOf("C.cs"), "Get", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("public int this[int index]", text);
        Assert.Contains("bag[1]", text);
        Assert.DoesNotContain("bag.Get(1)", text);
    }

    [Fact]
    public async Task IndexerToFunction_ProducesGetAndSetAndRewritesTheElementAccesses()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Bag
            {
                private readonly int[] _values = new int[3];

                public int this[int index]
                {
                    get { return _values[index]; }
                    set { _values[index] = value; }
                }

                public static int Use(Bag bag) => bag[1];

                public static void Put(Bag bag) => bag[2] = 9;
            }
            """));

        var service = new IndexerConversionsService(workspace);
        var result = await service.IndexerToFunction(workspace.PathOf("C.cs"), dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("public int Get(int index)", text);
        Assert.Contains("public void Set(int index, int value)", text);
        Assert.Contains("bag.Get(1)", text);
    }

    [Fact]
    public async Task InlineClass_PushesTheHelperMembersIntoTheOwnerAndDropsTheField()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", """
            namespace Probe;

            internal class Owner
            {
                private readonly Helper _helper = new Helper();

                public int Compute(int value) => _helper.Twice(value) + _helper.Offset;
            }

            internal class Helper
            {
                private readonly int _offset = 1;

                public int Offset => _offset;

                public int Twice(int value) => value * 2;
            }
            """));

        var service = new InlineClassService(workspace);
        var result = await service.InlineClass(workspace.PathOf("A.cs"), "Owner", "_helper", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("A.cs").Replace("\r\n", "\n");
        Assert.True(text.Contains("Twice(value) + Offset"), text);
        Assert.DoesNotContain("class Helper", text);
        Assert.DoesNotContain("_helper", text);
        Assert.Contains("public int Twice(int value) => value * 2;", text);
    }

    [Fact]
    public async Task ImplicitUsings_Disable_WritesThePropertyAndTheExplicitUsings()
    {
        using var workspace = FakeWorkspace.Create(
            ("Probe.GlobalUsings.g.cs", """
                global using System.Text;
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Size() => new StringBuilder().Length;
                }
                """));

        var service = new ImplicitUsingsService(workspace);
        var result = await service.ToggleImplicitUsings(enabled: false, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("<ImplicitUsings>disable</ImplicitUsings>", workspace.Read("Probe.csproj"));
        Assert.Contains("using System.Text;", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task ImplicitUsings_Enable_WritesTheProperty()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
            }
            """));

        var service = new ImplicitUsingsService(workspace);
        var result = await service.ToggleImplicitUsings(enabled: true, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("<ImplicitUsings>enable</ImplicitUsings>", workspace.Read("Probe.csproj"));
    }

    [Fact]
    public async Task ConvertToNonGlobalUsing_RemovesTheDirectiveAndAddsItWhereNeeded()
    {
        using var workspace = FakeWorkspace.Create(
            ("Global.cs", """
                global using System.Text;
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Size() => new StringBuilder().Length;
                }
                """));

        var service = new ConvertToNonGlobalUsingService(workspace);
        var result = await service.ConvertToNonGlobalUsing(workspace.PathOf("Global.cs"), "System.Text", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain("global using System.Text;", workspace.Read("Global.cs"));
        Assert.Contains("using System.Text;", workspace.Read("Use.cs"));
    }
}
