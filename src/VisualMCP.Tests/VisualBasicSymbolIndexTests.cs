using System.Text.Json;
using Snitch.Metacode.Symbols;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The symbol index over Visual Basic: the same tables and the same query shapes the C# tests cover,
/// fed by the VB frontend — declarations with their VB signatures, uses classified by their VB syntax
/// (a compound assignment is a read-write, <c>Implements</c> is an implement edge), the hierarchy the
/// two statements <c>Inherits</c> and <c>Implements</c> spell, and the metacode tree of a declaration.
/// </summary>
public sealed class VisualBasicSymbolIndexTests
{
    [Fact]
    public async Task Build_IndexesVbDeclarationsWithTheirOwnSignatures()
    {
        using var index = new IndexHarness(("Counter.vb", """
            Public Class Counter
                Private _value As Integer

                Public Property Count As Integer

                Public Function Add(delta As Integer) As Integer
                    _value += delta
                    Return _value
                End Function
            End Class
            """));

        var build = await index.BuildAsync();
        Assert.Equal(1, build.GetProperty("indexed").GetInt32());
        Assert.Equal(0, build.GetProperty("unsupported").GetInt32());
        Assert.True(build.GetProperty("declarations").GetInt32() >= 4);

        var method = Assert.Single(
            ResultAssert.Json(index.Service.Find("Add")).GetProperty("matches").EnumerateArray());
        Assert.Equal("Public Function Add(delta As Integer) As Integer", method.GetProperty("signature").GetString());
        Assert.Equal("Method", method.GetProperty("kind").GetString());
        Assert.Equal("Global.Counter", method.GetProperty("container").GetString());
        Assert.Equal(6, method.GetProperty("line").GetInt32());

        var field = Assert.Single(
            ResultAssert.Json(index.Service.Find("_value")).GetProperty("matches").EnumerateArray());
        Assert.Equal("Private _value As Integer", field.GetProperty("signature").GetString());
        Assert.Equal("Field", field.GetProperty("kind").GetString());

        var property = Assert.Single(
            ResultAssert.Json(index.Service.Find("Count")).GetProperty("matches").EnumerateArray());
        Assert.Equal("Public Property Count As Integer", property.GetProperty("signature").GetString());
        Assert.Equal("Property", property.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task References_ClassifyCompoundAssignmentAsReadWrite()
    {
        using var index = new IndexHarness(("Counter.vb", """
            Public Class Counter
                Private _value As Integer

                Public Function Add(delta As Integer) As Integer
                    _value += delta
                    Return _value
                End Function

                Public Sub Reset()
                    _value = 0
                End Sub

                Public Function Total() As Integer
                    Return Add(1)
                End Function
            End Class
            """));

        await index.BuildAsync();
        var references = ResultAssert.Json(index.Service.References("_value"));

        Assert.Equal(3, references.GetProperty("count").GetInt32());

        var kinds = Kinds(references);
        Assert.Contains("read", kinds);
        Assert.Contains("write", kinds);
        Assert.Contains("read-write", kinds);

        var write = references.GetProperty("references").EnumerateArray()
            .Single(reference => reference.GetProperty("kind").GetString() == "write");
        Assert.Contains("Reset", write.GetProperty("container").GetString());
        Assert.Contains("_value = 0", write.GetProperty("snippet").GetString());

        var call = Assert.Single(
            ResultAssert.Json(index.Service.References("Add", kind: "call")).GetProperty("references").EnumerateArray());
        Assert.Equal("call", call.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Hierarchy_ReadsInheritsAndImplements()
    {
        using var index = new IndexHarness(("Shapes.vb", """
            Public Interface IShape
                Sub Draw()
            End Interface

            Public MustInherit Class ShapeBase
            End Class

            Public Class Square
                Inherits ShapeBase
                Implements IShape

                Public Sub Draw() Implements IShape.Draw
                End Sub
            End Class
            """));

        await index.BuildAsync();

        var square = Assert.Single(
            ResultAssert.Json(index.Service.Hierarchy("Square")).GetProperty("symbols").EnumerateArray());
        Assert.Contains(
            square.GetProperty("extends").EnumerateArray(),
            edge => (edge.GetProperty("name").GetString() ?? "").Contains("ShapeBase"));
        Assert.Contains(
            square.GetProperty("implements").EnumerateArray(),
            edge => (edge.GetProperty("name").GetString() ?? "").Contains("IShape"));

        var shape = Assert.Single(
            ResultAssert.Json(index.Service.Hierarchy("IShape")).GetProperty("symbols").EnumerateArray());
        Assert.Contains(
            shape.GetProperty("implementedBy").EnumerateArray(),
            edge => (edge.GetProperty("name").GetString() ?? "").Contains("Square"));

        var baseType = Assert.Single(
            ResultAssert.Json(index.Service.Hierarchy("ShapeBase")).GetProperty("symbols").EnumerateArray());
        Assert.Contains(
            baseType.GetProperty("extendedBy").EnumerateArray(),
            edge => (edge.GetProperty("name").GetString() ?? "").Contains("Square"));
    }

    [Fact]
    public async Task Outline_ListsTheMembersOfAVbType()
    {
        using var index = new IndexHarness(("Counter.vb", """
            Public Class Counter
                Private _value As Integer
                Public Property Count As Integer

                Public Function Add(delta As Integer) As Integer
                    Return _value + delta
                End Function
            End Class
            """));

        await index.BuildAsync();

        var outline = ResultAssert.Json(index.Service.Outline(symbol: "Counter"));
        var names = outline.GetProperty("members").EnumerateArray()
            .Select(member => member.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["Counter", "_value", "Count", "Add"], names);
    }

    [Fact]
    public async Task Metacode_IsThePseudocodeOfTheVbDeclaration()
    {
        using var index = new IndexHarness(("Counter.vb", """
            Public Class Counter
                Private _value As Integer

                Public Function Add(delta As Integer) As Integer
                    _value += delta
                    Return _value
                End Function
            End Class
            """));

        await index.BuildAsync();

        var type = Assert.Single(
            ResultAssert.Json(index.Service.Metacode(symbol: "Counter")).GetProperty("trees").EnumerateArray());
        var typeText = type.GetProperty("text").GetString()!;
        Assert.Contains("CLASS Counter", typeText);
        Assert.Contains("FIELD _value : int", typeText);
        Assert.Contains("METHOD Add(delta : int) : int", typeText);

        var member = Assert.Single(
            ResultAssert.Json(index.Service.Metacode(symbol: "Counter.Add")).GetProperty("trees").EnumerateArray());
        var memberText = member.GetProperty("text").GetString()!;
        Assert.StartsWith("PUBLIC METHOD Add(delta : int) : int", memberText);
        Assert.Contains("_value += delta", memberText);
        Assert.Equal(4, member.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Build_IndexesAMixedSolution()
    {
        using var index = new IndexHarness(
            visualBasic: ("Greeter.vb", """
                Public Class Greeter
                    Public Function Greet(name As String) As String
                        Return "Hello " & name
                    End Function
                End Class
                """),
            csharp: ("Caller.cs", """
                internal class Caller
                {
                    public string Run(Greeter greeter) => greeter.Greet("x");
                }
                """));

        var build = await index.BuildAsync();
        Assert.Equal(2, build.GetProperty("indexed").GetInt32());
        Assert.Equal(0, build.GetProperty("unsupported").GetInt32());

        // The C# caller is a use of the VB method: the index is one store, not one per language.
        var references = ResultAssert.Json(index.Service.References("Greet"));
        Assert.Contains(
            references.GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("kind").GetString() == "call"
                         && (reference.GetProperty("file").GetString() ?? "").EndsWith("Caller.cs", StringComparison.Ordinal));
    }

    private static HashSet<string> Kinds(JsonElement json) =>
        json.GetProperty("byKind").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A VB solution (one project, or a VB project plus a C# one) and the real index service.</summary>
    private sealed class IndexHarness : IDisposable
    {
        public IndexHarness(params (string Name, string Source)[] files)
        {
            Workspace = VbStyleTests.VisualBasicWorkspace.Create(files[0].Name, files[0].Source);
            Store = new VisualMcpStore(Workspace);
            Service = new SymbolIndexService(Workspace, Store);
        }

        /// <summary>A VB file in one project and a C# file in another, so cross-language uses are indexed.</summary>
        public IndexHarness((string Name, string Source) visualBasic, (string Name, string Source) csharp)
        {
            Workspace = VbStyleTests.VisualBasicWorkspace.CreateWithCSharpFile(visualBasic, csharp);
            Store = new VisualMcpStore(Workspace);
            Service = new SymbolIndexService(Workspace, Store);
        }

        public VbStyleTests.VisualBasicWorkspace Workspace { get; }

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
