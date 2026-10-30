using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The refactoring batch: inline a field, extension → static, safe delete of a file, members to a
/// partial file, invert a boolean (with and without the rename). Each one is driven through the
/// service and checked on the file, plus the case it must refuse.
/// </summary>
public sealed class RefactoringBatchTests
{
    [Fact]
    public async Task InlineField_SubstitutesTheInitializerAndRemovesTheField()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                private const int Size = 10;

                public int Get() => Size;
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new InlineFieldService(workspace);

        var result = ResultAssert.Json(await service.InlineField(workspace.PathOf("C.cs"), "Size", null, dryRun: false));
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("Size", text);
        Assert.Contains("(10)", text);
    }

    [Fact]
    public async Task InlineField_RefusesAFieldWrittenOutsideItsDeclaration()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                private int _count = 1;

                public void Bump() => _count = 2;
                public int Get() => _count;
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new InlineFieldService(workspace);

        Assert.Contains("written outside", ResultAssert.Error(
            await service.InlineField(workspace.PathOf("C.cs"), "_count", null, dryRun: true)));
    }

    [Fact]
    public async Task ExtensionToStatic_MovesTheReceiverIntoTheArguments()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Value() => 40;
            }

            internal static class Ext
            {
                public static int Plus(this C c, int add) => c.Value() + add;
            }

            internal static class Use
            {
                public static int Run() => new C().Plus(2);
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ExtensionToStaticService(workspace);

        var result = ResultAssert.Json(await service.Convert(workspace.PathOf("C.cs"), "Plus", "Ext", dryRun: false));
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("this C c", text);
        Assert.Contains("Ext.Plus(new C(), 2)", text);
    }

    [Fact]
    public async Task SafeDeleteFile_RefusesWhenSomethingOutsideUsesIt()
    {
        const string helper = """
            namespace Probe;

            internal static class Helper
            {
                public static int Value() => 1;
            }
            """;

        var workspace = FakeWorkspace.Create(
            ("Helper.cs", helper),
            ("Consumer.cs", "namespace Probe; internal static class Consumer { public static int Use() => Helper.Value(); }"));

        using var scope = workspace;
        var service = new SafeDeleteFileService(workspace);

        var result = ResultAssert.Json(await service.DeleteFile(workspace.PathOf("Helper.cs"), dryRun: true));
        Assert.Contains("used outside itself", result.GetProperty("error").GetString());
        Assert.True(File.Exists(workspace.PathOf("Helper.cs")));

        // The consumer can go: then the helper is unreferenced and the delete is allowed.
        var consumer = ResultAssert.Json(await service.DeleteFile(workspace.PathOf("Consumer.cs"), dryRun: false));
        Assert.True(consumer.TryGetProperty("deleted", out _), consumer.GetRawText());

        var helperResult = ResultAssert.Json(await service.DeleteFile(workspace.PathOf("Helper.cs"), dryRun: false));
        Assert.True(helperResult.GetProperty("deleted").GetBoolean());
        Assert.False(File.Exists(workspace.PathOf("Helper.cs")));
    }

    [Fact]
    public async Task ExtractMembersToPartial_WritesBothFilesAndMarksTheTypePartial()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Value() => 1;
                public int Doubled() => Value() * 2;
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ExtractMembersToPartialService(workspace);

        var result = ResultAssert.Json(await service.Extract(
            workspace.PathOf("C.cs"), "C", ["Doubled"], suffix: "Math", dryRun: false));
        ResultAssert.Succeeded(result);

        var original = workspace.Read("C.cs");
        Assert.Contains("partial class C", original);
        Assert.DoesNotContain("Doubled", original);

        var newFile = workspace.PathOf("C.Math.cs");
        Assert.True(File.Exists(newFile));
        var moved = File.ReadAllText(newFile);
        Assert.Contains("namespace Probe;", moved);
        Assert.Contains("partial class C", moved);
        Assert.Contains("int Doubled()", moved);
    }

    [Fact]
    public async Task InvertBoolean_NegatesTheExpressionBody()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool IsPositive(int value) => value > 0;
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new InvertBooleanService(workspace);

        ResultAssert.Succeeded(await service.Invert(workspace.PathOf("C.cs"), "IsPositive", "C", null, dryRun: false));

        var text = workspace.Read("C.cs");
        Assert.Contains("=> !(value > 0)", text);
    }

    [Fact]
    public async Task InvertBoolean_RenamesAcrossTheSolution()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool IsReady() => true;
            }

            internal class Use
            {
                public bool Check() => new C().IsReady();
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new InvertBooleanService(workspace);

        var result = ResultAssert.Json(await service.Invert(
            workspace.PathOf("C.cs"), "IsReady", "C", "IsNotReady", dryRun: false));
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("IsNotReady() => false", text);
        Assert.Contains("new C().IsNotReady()", text);
    }

    [Fact]
    public async Task InvertBoolean_RefusesAReturnWithoutValue()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool Check(bool flag)
                {
                    if (flag) return true;
                    return false;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new InvertBooleanService(workspace);

        // Both returns carry a value: the inversion is allowed.
        ResultAssert.Succeeded(await service.Invert(workspace.PathOf("C.cs"), "Check", "C", null, dryRun: true));

        const string voidReturn = """
            namespace Probe;

            internal class D
            {
                public bool Check(bool flag)
                {
                    if (flag)
                    {
                        return true;
                    }

                    return;
                }
            }
            """;

        var other = FakeWorkspace.Create(("D.cs", voidReturn));
        using var otherScope = other;
        Assert.Contains("without a value", ResultAssert.Error(
            await new InvertBooleanService(other).Invert(other.PathOf("D.cs"), "Check", "D", null, dryRun: true)));
    }
}
