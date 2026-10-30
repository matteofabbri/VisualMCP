using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Metacode.Symbols;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The symbol index: which member is declared where, every place it is used and in what way, who uses
/// it and what it uses, what a type extends and implements, and the outline of a type or a file — all
/// read back from the database, never from the solution.
/// </summary>
public sealed class SymbolIndexTests
{
    [Fact]
    public async Task Find_ReportsSignatureLocationAndContainer()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                public int Twice(int value) => value * 2;

                public int Use() => Twice(2);
            }
            """));

        var build = await index.BuildAsync();
        Assert.Equal(1, build.GetProperty("indexed").GetInt32());
        Assert.True(build.GetProperty("declarations").GetInt32() >= 3);

        var found = ResultAssert.Json(index.Service.Find("Twice"));
        var match = Assert.Single(found.GetProperty("matches").EnumerateArray());

        Assert.Equal("public int Twice(int value)", match.GetProperty("signature").GetString());
        Assert.Equal("Method", match.GetProperty("kind").GetString());
        Assert.Equal("public", match.GetProperty("accessibility").GetString());
        Assert.Equal(3, match.GetProperty("line").GetInt32());
        Assert.Equal("global::C", match.GetProperty("container").GetString());
        Assert.Equal(index.Workspace.PathOf("C.cs"), match.GetProperty("file").GetString());
        Assert.Equal(1, match.GetProperty("uses").GetInt32());
    }

    [Fact]
    public async Task References_SayHowTheFieldIsUsedAndWhere()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class Counter
            {
                private int _value;

                public int Value => _value;

                public void Add(int delta) => _value += delta;

                public int Next() => ++_value;

                public void Reset() => _value = 0;

                public int Read() => _value;
            }
            """));

        await index.BuildAsync();
        var refs = ResultAssert.Json(index.Service.References("_value"));

        Assert.Equal(5, refs.GetProperty("count").GetInt32());

        var byKind = Kinds(refs);
        Assert.Contains("read", byKind);
        Assert.Contains("write", byKind);
        Assert.Contains("read-write", byKind);

        var write = refs.GetProperty("references").EnumerateArray()
            .Single(reference => reference.GetProperty("kind").GetString() == "write");
        Assert.Contains("Reset()", write.GetProperty("container").GetString());
        Assert.Contains("_value = 0", write.GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Callers_And_Callees_AreTheTwoHalvesOfTheCallHierarchy()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                public int Twice(int value) => value * 2;

                public int Use() => Twice(2) + Twice(3);
            }
            """));

        await index.BuildAsync();

        var callers = ResultAssert.Json(index.Service.Callers("Twice"));
        var caller = Assert.Single(callers.GetProperty("callers").EnumerateArray());
        Assert.Contains("Use()", caller.GetProperty("container").GetString());
        Assert.Equal(2, caller.GetProperty("uses").GetInt32());
        Assert.Contains("call", caller.GetProperty("kinds").EnumerateArray().Select(kind => kind.GetString()));

        var callees = ResultAssert.Json(index.Service.Callees("Use"));
        var callee = callees.GetProperty("callees").EnumerateArray()
            .Single(entry => (entry.GetProperty("name").GetString() ?? "").Contains("Twice"));
        Assert.Equal(2, callee.GetProperty("uses").GetInt32());
        Assert.True(callee.GetProperty("declared").GetBoolean());
    }

    [Fact]
    public async Task Hierarchy_RecordsInheritImplementAndOverride()
    {
        using var index = new IndexHarness(("C.cs", """
            internal interface IShape
            {
                double Area();
            }

            internal abstract class Shape : IShape
            {
                public abstract double Area();
            }

            internal sealed class Square : Shape
            {
                public override double Area() => 1;
            }
            """));

        await index.BuildAsync();

        var shape = ResultAssert.Json(index.Service.Hierarchy("IShape"));
        var contract = Assert.Single(shape.GetProperty("symbols").EnumerateArray());

        var implementedBy = contract.GetProperty("implementedBy").EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString() ?? "")
            .ToList();
        Assert.Contains(implementedBy, key => key.EndsWith("Shape", StringComparison.Ordinal));
        Assert.Contains(implementedBy, key => key.EndsWith("Square", StringComparison.Ordinal));

        var member = ResultAssert.Json(index.Service.Hierarchy("Area"));
        var contractMember = member.GetProperty("symbols").EnumerateArray()
            .Single(entry => (entry.GetProperty("key").GetString() ?? "").Contains("IShape.Area", StringComparison.Ordinal));

        var implementedMembers = contractMember.GetProperty("implementedBy").EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString() ?? "")
            .ToList();
        Assert.Contains(implementedMembers, key => key.Contains("Shape.Area()", StringComparison.Ordinal));
        Assert.Contains(implementedMembers, key => key.Contains("Square.Area()", StringComparison.Ordinal));

        var square = ResultAssert.Json(index.Service.Hierarchy("Square.Area"));
        var method = Assert.Single(square.GetProperty("symbols").EnumerateArray());

        var overrides = method.GetProperty("overrides").EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString() ?? "")
            .ToList();
        Assert.Contains(overrides, key => key.Contains("Shape.Area()", StringComparison.Ordinal));

        var derived = ResultAssert.Json(index.Service.Hierarchy("Shape"));
        var baseType = Assert.Single(derived.GetProperty("symbols").EnumerateArray());
        Assert.Contains(
            baseType.GetProperty("extendedBy").EnumerateArray().Select(entry => entry.GetProperty("key").GetString() ?? ""),
            key => key.Contains("Square", StringComparison.Ordinal));
        Assert.Contains(
            baseType.GetProperty("implements").EnumerateArray().Select(entry => entry.GetProperty("key").GetString() ?? ""),
            key => key.Contains("IShape", StringComparison.Ordinal));
    }

    [Fact]
    public async Task References_ClassifyTypePositions()
    {
        using var index = new IndexHarness(("C.cs", """
            using System;
            using System.Collections.Generic;

            internal sealed class MarkerAttribute : Attribute
            {
            }

            internal class Counter
            {
                public int Value => 1;
            }

            internal class Uses
            {
                private readonly Counter _field = new Counter();

                public List<Counter> Items { get; } = new();

                public Counter Build(Counter parameter)
                {
                    var copy = (Counter)parameter;
                    var type = typeof(Counter);
                    var created = new Counter();
                    var isCounter = created is Counter;
                    _ = nameof(Counter);
                    _ = default(Counter);
                    return isCounter ? copy : created;
                }

                [Marker]
                public void Legacy() { }
            }
            """));

        await index.BuildAsync();
        var refs = ResultAssert.Json(index.Service.References("Counter"));
        var byKind = Kinds(refs);

        Assert.Contains("field-type", byKind);
        Assert.Contains("parameter-type", byKind);
        Assert.Contains("return-type", byKind);
        Assert.Contains("type-argument", byKind);
        Assert.Contains("instantiate", byKind);
        Assert.Contains("cast", byKind);
        Assert.Contains("typeof", byKind);
        Assert.Contains("pattern", byKind);
        Assert.Contains("nameof", byKind);
        Assert.Contains("default", byKind);

        // An attribute name is a reference to the attribute type, not to the constructor its arguments pick.
        Assert.Contains("attribute", Kinds(ResultAssert.Json(index.Service.References("MarkerAttribute"))));
    }

    [Fact]
    public async Task Outline_ListsATypeAndAFileInSourceOrder()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                private int _first;

                public int Second { get; set; }

                public void Third() { }
            }
            """));

        await index.BuildAsync();

        var outline = ResultAssert.Json(index.Service.Outline(symbol: "C"));
        var names = outline.GetProperty("members").EnumerateArray()
            .Select(member => member.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["C", "_first", "Second", "Third"], names);

        var file = ResultAssert.Json(index.Service.Outline(path: index.Workspace.PathOf("C.cs")));
        Assert.Equal(4, file.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Unused_ListsPrivateMembersNothingUses()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                private int Unused() => 1;

                public int Used() => Helper();

                private int Helper() => 2;
            }
            """));

        await index.BuildAsync();

        var unused = ResultAssert.Json(index.Service.Unused());
        var names = unused.GetProperty("declarations").EnumerateArray()
            .Select(declaration => declaration.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("Unused", names);
        Assert.DoesNotContain("Helper", names);
        Assert.DoesNotContain("Used", names);   // public: could be called from outside the solution
    }

    [Fact]
    public async Task Build_SkipsUnchangedDocumentsAndIndexesChangedOnes()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                public int F() => 1;
            }
            """));

        await index.BuildAsync();

        var second = await index.BuildAsync();
        Assert.Equal(0, second.GetProperty("indexed").GetInt32());
        Assert.Equal(1, second.GetProperty("skipped").GetInt32());

        var forced = await index.BuildAsync(force: true);
        Assert.Equal(1, forced.GetProperty("indexed").GetInt32());

        var document = index.Workspace.CurrentSolution!.Projects
            .SelectMany(project => project.Documents)
            .Single(candidate => candidate.Name == "C.cs");

        index.Workspace.TryUpdateDocument(document.Id, SourceText.From("""
            internal class C
            {
                public int F() => 1;
                public int G() => 2;
            }
            """));

        var changed = await index.BuildAsync();
        Assert.Equal(1, changed.GetProperty("indexed").GetInt32());
        Assert.Equal(0, changed.GetProperty("skipped").GetInt32());

        var found = ResultAssert.Json(index.Service.Find("G"));
        Assert.Single(found.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task Describe_ReportsDeclarationsUsesAndCallers()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                public int Twice(int value) => value * 2;

                public int Use() => Twice(2);
            }
            """));

        await index.BuildAsync();

        var described = ResultAssert.Json(index.Service.Describe("Twice"));
        var symbol = Assert.Single(described.GetProperty("symbols").EnumerateArray());

        Assert.Equal("public int Twice(int value)", symbol.GetProperty("signature").GetString());
        var declaration = Assert.Single(symbol.GetProperty("declarations").EnumerateArray());
        Assert.Equal(3, declaration.GetProperty("line").GetInt32());
        Assert.True(declaration.GetProperty("endLine").GetInt32() >= 3);

        var uses = symbol.GetProperty("uses").EnumerateArray();
        Assert.Contains(uses, use => use.GetProperty("kind").GetString() == "call" && use.GetProperty("count").GetInt32() == 1);

        var caller = Assert.Single(symbol.GetProperty("callers").EnumerateArray());
        Assert.Contains("Use()", caller.GetProperty("container").GetString());
    }

    [Fact]
    public async Task Find_MatchesSubstringsWhenAsked()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class C
            {
                public int Twice(int value) => value * 2;

                public int TwiceAsMuch(int value) => value * 4;
            }
            """));

        await index.BuildAsync();

        var exact = ResultAssert.Json(index.Service.Find("Twice"));
        Assert.Single(exact.GetProperty("matches").EnumerateArray());

        var partial = ResultAssert.Json(index.Service.Find("Twice", partial: true));
        Assert.Equal(2, partial.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SecondaryConstructorsAndOperatorsAreNamedForTheReader()
    {
        using var index = new IndexHarness(("C.cs", """
            internal readonly struct Money
            {
                private readonly int _amount;

                public Money(int amount) => _amount = amount;

                public static Money operator +(Money left, Money right) => new(left._amount + right._amount);
            }
            """));

        await index.BuildAsync();

        var constructor = ResultAssert.Json(index.Service.Find("Money"));
        Assert.Contains(
            constructor.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("signature").GetString()),
            signature => signature is not null && signature.StartsWith("public Money(int amount", StringComparison.Ordinal));

        var reference = ResultAssert.Json(index.Service.References("_amount"));
        Assert.Contains("read", Kinds(reference));
    }

    [Fact]
    public async Task Metacode_ReturnsTheTreeOfATypeAndOfOneMember()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class Counter
            {
                private int _value;

                public void Add(int delta)
                {
                    _value += delta;
                }
            }
            """));

        await index.BuildAsync();

        var type = ResultAssert.Json(index.Service.Metacode(symbol: "Counter"));
        var typeTree = Assert.Single(type.GetProperty("trees").EnumerateArray());
        var typeText = typeTree.GetProperty("text").GetString()!;

        Assert.Contains("CLASS Counter", typeText);
        Assert.Contains("FIELD _value", typeText);
        Assert.Contains("METHOD Add(delta : int)", typeText);
        Assert.Contains("END CLASS", typeText);

        var member = ResultAssert.Json(index.Service.Metacode(symbol: "Counter.Add"));
        var memberTree = Assert.Single(member.GetProperty("trees").EnumerateArray());
        var memberText = memberTree.GetProperty("text").GetString()!;

        Assert.StartsWith("PUBLIC METHOD Add(delta : int)", memberText);
        Assert.Contains("_value += delta", memberText);   // the body is in the tree, not just the signature
        Assert.DoesNotContain("CLASS Counter", memberText);
        Assert.Equal(5, memberTree.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Metacode_ByFile_ListsEveryTreeInSourceOrder()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class First
            {
                public int Value => 1;
            }

            internal class Second
            {
                public int Other => 2;
            }
            """));

        await index.BuildAsync();

        var file = ResultAssert.Json(index.Service.Metacode(path: index.Workspace.PathOf("C.cs")));
        var names = file.GetProperty("trees").EnumerateArray()
            .Select(tree => tree.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["First", "Value", "Second", "Other"], names);
    }

    [Fact]
    public async Task Metacode_NormalizesControlFlowLikeTheTranslator()
    {
        using var index = new IndexHarness(("C.cs", """
            internal class Loops
            {
                public int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                        total += i;
                    return total;
                }
            }
            """));

        await index.BuildAsync();

        var tree = Assert.Single(
            ResultAssert.Json(index.Service.Metacode(symbol: "Loops.Sum")).GetProperty("trees").EnumerateArray());
        var text = tree.GetProperty("text").GetString()!;

        Assert.Contains("WHILE i < n DO", text);
        Assert.DoesNotContain("FOR ", text);
    }

    private static HashSet<string> Kinds(JsonElement json) =>
        json.GetProperty("byKind").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A fake solution, the real store and the real index service, disposed in the right order.</summary>
    private sealed class IndexHarness : IDisposable
    {
        public IndexHarness(params (string Name, string Source)[] files)
        {
            Workspace = FakeWorkspace.Create(files);
            Store = new VisualMcpStore(Workspace);
            Service = new SymbolIndexService(Workspace, Store);
        }

        public FakeWorkspace Workspace { get; }

        public VisualMcpStore Store { get; }

        public SymbolIndexService Service { get; }

        public async Task<JsonElement> BuildAsync(string? projectName = null, bool force = false) =>
            ResultAssert.Json(await Service.BuildAsync(projectName, force));

        public void Dispose()
        {
            Service.Dispose();
            Store.Dispose();
            Workspace.Dispose();
        }
    }
}
