using System.Text.Json;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// One test per refactoring. Each builds a real (tiny) solution over temp files, runs the service with
/// dryRun: false and asserts on the file that was actually written — the same check the author did by
/// hand before, now run on every build.
/// </summary>
public sealed class RefactoringTests
{
    [Fact]
    public async Task PropertyToAuto_SimplifiesTrivialAccessorsAndDropsTheField()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private int _value = 3;

                public int Value
                {
                    get { return _value; }
                    set { _value = value; }
                }
            }
            """));

        var service = new PropertyToAutoService(workspace);
        var result = await service.PropertyToAuto(workspace.PathOf("C.cs"), "Value", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Value { get; set; } = 3;", text);
        Assert.DoesNotContain("_value", text);

        // The backing field was the first member: removing it must not leave the blank line that
        // separated the two members dangling under the opening brace.
        var normalized = text.Replace("\r\n", "\n");
        Assert.Contains("{\n    public int Value { get; set; } = 3;", normalized);
        Assert.DoesNotContain("{\n\n", normalized);
    }

    [Fact]
    public async Task PropertyToAuto_RefusesWhenTheFieldIsUsedElsewhere()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private int _value;

                public int Value
                {
                    get { return _value; }
                    set { _value = value; }
                }

                public void Reset() => _value = 0;
            }
            """));

        var service = new PropertyToAutoService(workspace);
        var result = await service.PropertyToAuto(workspace.PathOf("C.cs"), "Value", dryRun: true);

        Assert.Contains("outside the property", ResultAssert.Error(result));
    }

    [Fact]
    public async Task TypeToPartial_MarksEveryDeclaration()
    {
        using var workspace = FakeWorkspace.Create(("Split.cs", """
            namespace Probe;

            internal class Split
            {
                public int Number { get; set; }
            }
            """));

        var service = new TypeToPartialService(workspace);
        var result = await service.TypeToPartial(workspace.PathOf("Split.cs"), "Split", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("internal partial class Split", workspace.Read("Split.cs"));
    }

    [Fact]
    public async Task MakeStatic_RewritesTheCallSite()
    {
        using var workspace = FakeWorkspace.Create(
            ("Target.cs", """
                namespace Probe;

                internal class Target
                {
                    public int Pure(int value) => value * 2;
                }
                """),
            ("Caller.cs", """
                namespace Probe;

                internal static class Caller
                {
                    public static int Run()
                    {
                        var target = new Target();
                        return target.Pure(3);
                    }
                }
                """));

        var service = new MakeStaticService(workspace);
        var result = await service.MakeStatic(workspace.PathOf("Target.cs"), "Pure", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public static int Pure(int value)", workspace.Read("Target.cs"));
        Assert.Contains("Target.Pure(3)", workspace.Read("Caller.cs"));
    }

    [Fact]
    public async Task MakeStatic_RefusesAMemberTouchingInstanceState()
    {
        using var workspace = FakeWorkspace.Create(("Target.cs", """
            namespace Probe;

            internal class Target
            {
                private int _value;

                public int Reads() => _value;
            }
            """));

        var service = new MakeStaticService(workspace);
        var result = await service.MakeStatic(workspace.PathOf("Target.cs"), "Reads", dryRun: true);

        Assert.Contains("cannot become static", ResultAssert.Error(result));
    }

    [Fact]
    public async Task FunctionToProperty_RenamesAndRewritesTheCall()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private readonly int _count = 7;

                public int GetCount() => _count;

                public int Use() => GetCount();
            }
            """));

        var service = new FunctionToPropertyService(workspace);
        var result = await service.FunctionToProperty(workspace.PathOf("C.cs"), "GetCount", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Count { get => _count; }", text);
        Assert.Contains("public int Use() => Count;", text);
    }

    [Fact]
    public async Task FunctionToProperty_RefusesMethodGroupUse()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int GetValue() => 1;

                public System.Func<int> Handler() => GetValue;
            }
            """));

        var service = new FunctionToPropertyService(workspace);
        var result = await service.FunctionToProperty(workspace.PathOf("C.cs"), "GetValue", dryRun: true);

        Assert.Contains("not plain calls", ResultAssert.Error(result));
    }

    [Fact]
    public async Task StaticToExtension_RotatesTheReceiverAndAddsTheUsing()
    {
        using var workspace = FakeWorkspace.Create(
            ("Text.cs", """
                namespace Probe.Extensions;

                internal static class TextExtensions
                {
                    public static string Shout(string text) => text + "!";
                }
                """),
            ("Consumer.cs", """
                namespace Probe.Consumer;

                internal static class Consumer
                {
                    public static string Run() => Probe.Extensions.TextExtensions.Shout("hi");
                }
                """));

        var service = new StaticToExtensionService(workspace);
        var result = await service.StaticToExtension(workspace.PathOf("Text.cs"), "Shout", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("this string text", workspace.Read("Text.cs"));
        var consumer = workspace.Read("Consumer.cs");
        Assert.Contains("using Probe.Extensions;", consumer);
        Assert.Contains("\"hi\".Shout()", consumer);
    }

    [Fact]
    public async Task IntroduceField_HoistsTheLiteral()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Add(int value) => value + 41;
            }
            """));

        var service = new IntroduceFieldService(workspace);
        var result = await service.IntroduceField(workspace.PathOf("C.cs"), 5, "41", "_offset", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private readonly int _offset = 41;", text);
        Assert.Contains("value + _offset", text);
    }

    [Fact]
    public async Task IntroduceParameter_AddsTheParameterAndUpdatesTheSameFileCallSite()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Offset(int value)
                {
                    return value + 50;
                }

                public int Run() => Offset(3);
            }
            """));

        var service = new IntroduceParameterService(workspace);
        var result = await service.IntroduceParameter(workspace.PathOf("C.cs"), 7, "50", "offset", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Offset(int value, int offset)", text);
        Assert.Contains("return value + offset;", text);
        Assert.Contains("Offset(3, 50)", text);
    }

    [Fact]
    public async Task IntroduceParameter_RefusesAnExpressionThatReadsState()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Echo(int value)
                {
                    return value;
                }
            }
            """));

        var service = new IntroduceParameterService(workspace);
        var result = await service.IntroduceParameter(workspace.PathOf("C.cs"), 7, "value", "input", dryRun: true);

        Assert.Contains("depends on where it is evaluated", ResultAssert.Error(result));
    }

    [Fact]
    public async Task IntroduceParameter_AcceptsAConstantExpression()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Scale(int value) => value * (1 << 3);

                public int Run() => Scale(2);
            }
            """));

        var service = new IntroduceParameterService(workspace);
        var result = await service.IntroduceParameter(workspace.PathOf("C.cs"), 5, "1 << 3", "factor", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Scale(int value, int factor)", text);
        Assert.Contains("value * (factor)", text);
        Assert.Contains("Scale(2, 1 << 3)", text);
    }

    [Fact]
    public async Task ExtractMethod_PullsTheTailIntoANewMethod()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Compute(int input)
                {
                    var doubled = input * 2;
                    var total = doubled + 100;
                    return total;
                }
            }
            """));

        var service = new ExtractMethodService(workspace);
        var result = await service.ExtractMethod(workspace.PathOf("C.cs"), 8, 9, "Tail", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private int Tail(int doubled)", text);
        Assert.Contains("return Tail(doubled);", text);
    }

    [Fact]
    public async Task ExtractMethod_ExtractsAnAwaitingBlockAsAsyncTask()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Threading.Tasks;

            namespace Probe;

            internal class C
            {
                public async Task<int> LoadAsync(int input)
                {
                    var doubled = input * 2;
                    await Task.Delay(1);
                    return doubled;
                }
            }
            """));

        var service = new ExtractMethodService(workspace);
        var result = await service.ExtractMethod(workspace.PathOf("C.cs"), 10, 11, "LoadTail", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private async Task<int> LoadTail(int doubled)", text);
        Assert.Contains("return await LoadTail(doubled);", text);
    }

    [Fact]
    public async Task ExtractMethod_DoesNotBecomeAsyncForAnAwaitInsideANestedLambda()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System;
            using System.Threading.Tasks;

            namespace Probe;

            internal class C
            {
                public Task Build()
                {
                    Func<Task> work = async () => await Task.Delay(1);
                    return work();
                }
            }
            """));

        var service = new ExtractMethodService(workspace);
        var result = await service.ExtractMethod(workspace.PathOf("C.cs"), 10, 11, "BuildTail", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private Task BuildTail()", text);
        Assert.Contains("return BuildTail();", text);
    }

    [Fact]
    public async Task ExtractMethod_RefusesABlockThatWritesAPreExistingLocal()    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Mutate(int value)
                {
                    var total = 0;
                    total = total + value;
                    total = total * 2;
                }
            }
            """));

        var service = new ExtractMethodService(workspace);
        var result = await service.ExtractMethod(workspace.PathOf("C.cs"), 8, 9, "Accumulate", dryRun: true);

        Assert.Contains("writes locals declared before it", ResultAssert.Error(result));
    }

    [Fact]
    public async Task InvertIf_SwapsBranchesAndNegatesTheCondition()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Pick(int value)
                {
                    if (value > 10)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """));

        var service = new InvertIfService(workspace);
        var result = await service.InvertIf(workspace.PathOf("C.cs"), 7, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (!(value > 10))", text);
        Assert.True(text.IndexOf("return 2;", StringComparison.Ordinal) <
                    text.IndexOf("return 1;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddParams_AddsTheModifier()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Count(int[] values) => values.Length;
            }
            """));

        var service = new AddParamsModifierService(workspace);
        var result = await service.AddParams(workspace.PathOf("C.cs"), "Count", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("params int[] values", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ChangeNullability_AddsTheAnnotationWhenNothingBreaks()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private string _spare = "y";
            }
            """));

        var service = new ChangeNullabilityService(workspace);
        var result = await service.ChangeNullability(workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("private string? _spare", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ChangeNullability_RefusesWhenItWouldIntroduceDiagnostics()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string? Optional { get; set; }
            }
            """));

        var service = new ChangeNullabilityService(workspace);
        var result = await service.ChangeNullability(workspace.PathOf("C.cs"), 5, dryRun: true);

        Assert.Contains("would introduce", ResultAssert.Error(result));
    }

    [Fact]
    public async Task RenameTupleComponent_RenamesTheAccessAndTheSpelling()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public (int Count, int Total) Sum()
                {
                    return (1, 2);
                }

                public int Use()
                {
                    var pair = Sum();
                    var unrelated = new System.Collections.Generic.List<int>().Count;
                    return pair.Count + pair.Total + unrelated;
                }
            }
            """));

        var service = new RenameTupleComponentService(workspace);
        var result = await service.RenameTupleComponent(workspace.PathOf("C.cs"), 5, "Count", "ItemCount", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("(int ItemCount, int Total)", text);
        Assert.Contains("pair.ItemCount", text);
        Assert.Contains("List<int>().Count", text);
    }

    [Fact]
    public async Task GlobalUsings_MovesEveryPlainUsingIntoOneFile()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                using System.Text;

                namespace Probe;

                internal class A
                {
                    public string Build() => new StringBuilder().Append(1).ToString();
                }
                """),
            ("B.cs", """
                using System.Collections.Generic;

                namespace Probe;

                internal class B
                {
                    public int Count() => new List<int> { 1, 2 }.Count;
                }
                """));

        var service = new GlobalUsingsService(workspace);
        var result = await service.MoveToGlobalUsings(dryRun: false);
        ResultAssert.Succeeded(result);

        var globalPath = Path.Combine(workspace.RootDirectory, "GlobalUsings.cs");
        Assert.True(File.Exists(globalPath));
        var global = File.ReadAllText(globalPath);
        Assert.Contains("global using System.Text;", global);
        Assert.Contains("global using System.Collections.Generic;", global);

        Assert.DoesNotContain("using System.Text;", workspace.Read("A.cs"));
        Assert.DoesNotContain("using System.Collections.Generic;", workspace.Read("B.cs"));
    }

    [Fact]
    public async Task GlobalUsings_RefusesWhenItWouldBreakTheCompilation()
    {
        // Two namespaces that both define 'Widget': importing both globally makes every use ambiguous.
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                using Probe.One;

                namespace Probe
                {
                    internal class A
                    {
                        public int Use() => new Widget().Size;
                    }
                }

                namespace Probe.One
                {
                    internal class Widget { public int Size => 1; }
                }
                """),
            ("B.cs", """
                using Probe.Two;

                namespace Probe
                {
                    internal class B
                    {
                        public int Use() => new Widget().Size;
                    }
                }

                namespace Probe.Two
                {
                    internal class Widget { public int Size => 2; }
                }
                """));

        var service = new GlobalUsingsService(workspace);
        var result = await service.MoveToGlobalUsings(dryRun: true);

        Assert.Contains("would add", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ChangeType_IsAppliedWhenTheProjectStillCompiles()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;

            namespace Probe;

            internal class C
            {
                private List<int> _items = new List<int>();

                public int Count() => _items.Count;
            }
            """));

        var service = new ChangeTypeService(workspace);
        var result = await service.ChangeType(workspace.PathOf("C.cs"), 7, "IReadOnlyList<int>", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("private IReadOnlyList<int> _items", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ChangeType_RefusesWhenTheUsagesDoNotSurvive()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;

            namespace Probe;

            internal class C
            {
                private List<int> _items = new List<int>();

                public int Count() => _items.Count;
            }
            """));

        var service = new ChangeTypeService(workspace);
        var result = await service.ChangeType(workspace.PathOf("C.cs"), 7, "int", dryRun: true);

        Assert.Contains("would add", ResultAssert.Error(result));
    }

    [Fact]
    public async Task MoveTypeToOuterScope_ExtractsTheTypeAndRewritesReferences()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Outer
            {
                internal class Inner
                {
                    public int Value => 1;
                }

                public int Use() => new Inner().Value;
            }

            internal class Consumer
            {
                public int Read() => new Outer.Inner().Value;
            }
            """));

        var service = new MoveTypeToOuterScopeService(workspace);
        var result = await service.MoveTypeToOuterScope(workspace.PathOf("C.cs"), "Inner", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("internal class Inner", text);
        Assert.DoesNotContain("Outer.Inner", text);
        Assert.Contains("new Inner().Value", text);
    }

    [Fact]
    public async Task CopyType_DuplicatesTheTypeAndRenamesItsConstructor()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Original
            {
                private readonly int _value;

                public Original(int value) => _value = value;

                public int Value => _value;
            }
            """));

        var service = new CopyTypeService(workspace);
        var result = await service.CopyType(workspace.PathOf("C.cs"), "Original", "Copy", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("internal class Copy", text);
        Assert.Contains("public Copy(int value)", text);
    }

    [Fact]
    public async Task ConstructorToFactory_PrivatisesTheConstructorAndRewritesNew()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private readonly int _value;

                public C(int value) => _value = value;

                public int Value => _value;

                public static C Build() => new C(3);
            }
            """));

        var service = new ConstructorToFactoryService(workspace);
        var result = await service.ConstructorToFactory(workspace.PathOf("C.cs"), "C", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("private C(int value)", text);
        Assert.Contains("public static C Create(int value)", text);
        Assert.Contains("C.Create(3)", text);
        Assert.Contains("return new C(value);", text);
    }

    [Fact]
    public async Task UseBaseType_ReplacesTheTypeWhenTheUsagesAllowIt()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal interface IHasCount
            {
                int Count { get; }
            }

            internal class Counter : IHasCount
            {
                public int Count => 2;
            }

            internal class Holder
            {
                private Counter _counter = new Counter();

                public int Read() => _counter.Count;
            }
            """));

        var service = new UseBaseTypeService(workspace);
        var result = await service.UseBaseType(workspace.PathOf("C.cs"), 15, "IHasCount", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("private IHasCount _counter", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ConvertToPrimaryConstructor_MovesTheAssignmentsIntoInitialisers()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private readonly int _value;

                public C(int value)
                {
                    _value = value;
                }

                public int Value => _value;
            }
            """));

        var service = new ConvertToPrimaryConstructorService(workspace);
        var result = await service.ConvertToPrimaryConstructor(workspace.PathOf("C.cs"), "C", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("internal class C(int value)", text);
        Assert.Contains("private readonly int _value = value;", text);
        Assert.DoesNotContain("public C(int value)", text);
    }

    [Fact]
    public async Task PushMembersDown_MovesTheMemberIntoTheOnlyDerivedType()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Base
            {
                public int Helper() => 42;
            }

            internal class Derived : Base
            {
                public int Use() => Helper();
            }
            """));

        var service = new PushMembersDownService(workspace);
        var result = await service.PushMembersDown(workspace.PathOf("C.cs"), "Helper", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        var baseBody = text[..text.IndexOf("internal class Derived", StringComparison.Ordinal)];
        var derivedBody = text[text.IndexOf("internal class Derived", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Helper", baseBody);
        Assert.Contains("Helper", derivedBody);
    }

    [Fact]
    public async Task PushMembersDown_RefusesWhenTheBaseCannotBeCollapsed()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Base
            {
                public int Helper() => 42;

                public int Use() => Helper();
            }

            internal class Derived : Base
            {
            }
            """));

        var service = new PushMembersDownService(workspace);
        var result = await service.PushMembersDown(workspace.PathOf("C.cs"), "Helper", dryRun: true);

        Assert.Contains("used outside", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ExtractClass_MovesTheMembersAndForwardsFromTheOriginal()
    {
        using var workspace = FakeWorkspace.Create(("Host.cs", """
            namespace Probe;

            internal class Host
            {
                private int _count;

                public int Count() => _count;

                public void Bump() => _count++;

                public int Unrelated() => 7;
            }
            """));

        var service = new ExtractClassService(workspace);
        var result = await service.ExtractClass(
            workspace.PathOf("Host.cs"), "Host", new[] { "_count", "Count", "Bump" }, "Counter", dryRun: false);
        ResultAssert.Succeeded(result);

        var newFile = Path.Combine(workspace.RootDirectory, "Counter.cs");
        Assert.True(File.Exists(newFile));
        var counter = File.ReadAllText(newFile);
        Assert.Contains("internal sealed class Counter", counter);
        Assert.Contains("private int _count;", counter);

        var host = workspace.Read("Host.cs").Replace("\r\n", "\n");
        Assert.Contains("private readonly Counter _counter = new Counter();", host);
        Assert.Contains("public int Count() => _counter.Count();", host);
        Assert.Contains("public void Bump() => _counter.Bump();", host);
        Assert.Contains("public int Unrelated() => 7;", host);
        Assert.DoesNotContain("private int _count;", host);

        // The holder and the shims sit at member indentation, and no blank line is left under the
        // opening brace where the moved field used to be.
        Assert.Contains("{\n    private readonly Counter _counter", host);
        Assert.DoesNotContain("{\n\n", host);

        var extracted = counter.Replace("\r\n", "\n");
        Assert.Contains("internal sealed class Counter\n{\n    private int _count;\n", extracted);
    }

    [Fact]
    public async Task ExtractClass_RefusesAMemberThatReadsStateStayingBehind()
    {
        using var workspace = FakeWorkspace.Create(("Host.cs", """
            namespace Probe;

            internal class Host
            {
                private int _count;
                private int _offset;

                public int Count() => _count + _offset;
            }
            """));

        var service = new ExtractClassService(workspace);
        var result = await service.ExtractClass(
            workspace.PathOf("Host.cs"), "Host", new[] { "_count", "Count" }, "Counter", dryRun: true);

        Assert.Contains("which is not being extracted", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ChangeSignature_ReordersTheParametersAndTheCallAboveTheDeclaration()
    {
        // The call site sits *before* the declaration in the same file: rewriting it textually would
        // land on the wrong offsets once the parameter list changes length.
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Run() => Compute(1, 2);

                public int Compute(int a, int b) => a - b;
            }
            """));

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature("Compute", "C", new[] { "b", "a" });
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Compute(int b, int a) => a - b;", text);
        Assert.Contains("Compute(2, 1)", text);
        Assert.DoesNotContain("Compute(1, 2)", text);
    }

    [Fact]
    public async Task ChangeSignature_PreviewsEveryCallSite()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Run() => Compute(1, 2);

                public int Compute(int a, int b) => a - b;
            }
            """));

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature("Compute", "C", new[] { "b", "a" }, dryRun: true);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        var callSites = json.GetProperty("callSites");
        Assert.Equal(1, callSites.GetArrayLength());
        Assert.Equal("Compute(1, 2)", callSites[0].GetProperty("before").GetString());
        Assert.Equal("Compute(2, 1)", callSites[0].GetProperty("after").GetString());
        Assert.Equal(5, callSites[0].GetProperty("line").GetInt32());
        Assert.True(json.TryGetProperty("files", out var files) && files.GetArrayLength() == 1);
    }

    [Fact]
    public async Task ChangeSignature_RefusesAMethodGroupUse()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System;

            namespace Probe;

            internal class C
            {
                public int Compute(int a) => a;

                public void Wire()
                {
                    Func<int, int, int> f = Compute;
                    _ = f;
                }
            }
            """));

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature(
            "Compute", "C", new[] { "a" },
            additionalParams: [new ChangeSignatureService.NewParameter { Name = "scale", Type = "int", DefaultValue = "1" }]);

        Assert.Contains("not plain calls", ResultAssert.Error(result));
        Assert.DoesNotContain("int scale", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task Refactorings_RefuseWhenTheFileChangedOnDisk()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private int _value = 3;

                public int Value
                {
                    get { return _value; }
                    set { _value = value; }
                }
            }
            """));

        // Someone else edited the file after the solution was loaded.
        var path = workspace.PathOf("C.cs");
        File.WriteAllText(path, workspace.Read("C.cs").Replace("_value = 3", "_value = 4"));

        var service = new PropertyToAutoService(workspace);
        var result = await service.PropertyToAuto(path, "Value", dryRun: false);

        Assert.Contains("changed on disk", ResultAssert.Error(result));
        Assert.Contains("_value = 4", File.ReadAllText(path));
    }
}
