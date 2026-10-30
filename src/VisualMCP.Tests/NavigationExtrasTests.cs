using Snitch.Languages.CSharp.Navigation;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The navigation/refactoring additions: CamelHumps ranking in find_symbol, the call hierarchy in both
/// directions, and removing a params modifier behind the compiler gate.
/// </summary>
public sealed class NavigationExtrasTests
{
    private const string Chain = """
        namespace Demo;

        public static class LevelThree
        {
            public static void Leaf() { }
        }

        public static class LevelTwo
        {
            public static void Middle() => LevelThree.Leaf();
        }

        public static class CheckParameterSafety
        {
            public static void Top() => LevelTwo.Middle();
        }
        """;

    [Fact]
    public async Task FindSymbol_RanksCamelHumpsAndKeepsExactMatchesFirst()
    {
        var workspace = FakeWorkspace.Create(("Chain.cs", Chain));
        using var scope = workspace;
        var navigation = new NavigationService(workspace);

        var humps = ResultAssert.Json(await navigation.FindSymbolAsync("CPS", false));
        var humpsMatch = humps.GetProperty("matches").EnumerateArray()
            .First(match => match.GetProperty("name").GetString() == "CheckParameterSafety");
        Assert.Equal("camelHumps", humpsMatch.GetProperty("matchKind").GetString());

        var exact = ResultAssert.Json(await navigation.FindSymbolAsync("LevelTwo", false));
        Assert.Equal("exact", exact.GetProperty("matches")[0].GetProperty("matchKind").GetString());
    }

    [Fact]
    public async Task CallHierarchy_ReturnsCallersAndCallees()
    {
        var workspace = FakeWorkspace.Create(("Chain.cs", Chain));
        using var scope = workspace;
        var hierarchy = new CallHierarchyService(workspace);

        var result = ResultAssert.Json(await hierarchy.BuildAsync("Middle", "LevelTwo", "both", 2));

        var incoming = result.GetProperty("incoming");
        Assert.Contains(incoming.GetProperty("children").EnumerateArray(),
            child => child.GetProperty("name").GetString()!.Contains("Top"));

        var outgoing = result.GetProperty("outgoing");
        Assert.Contains(outgoing.GetProperty("children").EnumerateArray(),
            child => child.GetProperty("name").GetString()!.Contains("Leaf"));
    }

    [Fact]
    public async Task RemoveParams_AppliesWhenEveryCallSitePassesAnArray()
    {
        const string source = """
            namespace Demo;

            public static class Api
            {
                public static int Sum(params int[] values) => values.Length;

                public static int Use() => Sum(new[] { 1, 2, 3 });
            }
            """;

        var workspace = FakeWorkspace.Create(("Api.cs", source));
        using var scope = workspace;
        var service = new RemoveParamsModifierService(workspace);

        var result = ResultAssert.Json(await service.RemoveParams(workspace.PathOf("Api.cs"), "Sum", null, dryRun: false));
        Assert.Equal("remove_params", result.GetProperty("refactoring").GetString());
        Assert.Contains("public static int Sum(int[] values)", workspace.Read("Api.cs"));
    }

    [Fact]
    public async Task RemoveParams_RefusesWhenACallSitePassesTheElements()
    {
        const string source = """
            namespace Demo;

            public static class Api
            {
                public static int Sum(params int[] values) => values.Length;

                public static int Use() => Sum(1, 2, 3);
            }
            """;

        var workspace = FakeWorkspace.Create(("Api.cs", source));
        using var scope = workspace;
        var service = new RemoveParamsModifierService(workspace);

        var result = ResultAssert.Json(await service.RemoveParams(workspace.PathOf("Api.cs"), "Sum", null, dryRun: false));
        Assert.Contains("Refused", result.GetProperty("error").GetString());
        Assert.Contains("params int[] values", workspace.Read("Api.cs"));
    }
}
