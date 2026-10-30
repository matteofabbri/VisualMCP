using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The second batch of Rider refactorings (property_to_function, inline_parameter, extract_superclass,
/// convert_anonymous_to_type, rename_namespace, move_to_namespace). Same recipe as RefactoringTests:
/// a real solution over temp files, the service runs with dryRun: false, and the assertions read the
/// file that was actually written.
/// </summary>
public sealed class RefactoringCompletionTests
{
    [Fact]
    public async Task PropertyToFunction_TurnsGetterAndSetterIntoMethodsAndRewritesUsages()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", """
                namespace Probe;

                internal class C
                {
                    private int _value;

                    public int Value
                    {
                        get { return _value; }
                        set { _value = value; }
                    }
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Read(C c) => c.Value;

                    public static void Write(C c, int v) => c.Value = v;

                    public static void Add(C c, int v) => c.Value += v;
                }
                """));

        var service = new PropertyToFunctionService(workspace);
        var result = await service.PropertyToFunction(workspace.PathOf("C.cs"), "Value", dryRun: false);
        ResultAssert.Succeeded(result);

        var declaration = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("public int GetValue()", declaration);
        Assert.Contains("public void SetValue(int value)", declaration);
        Assert.DoesNotContain("public int Value", declaration);

        var usages = workspace.Read("Use.cs");
        Assert.Contains("c.GetValue()", usages);
        Assert.Contains("c.SetValue(v)", usages);
        Assert.Contains("c.SetValue(c.GetValue() + (v))", usages);
    }

    [Fact]
    public async Task PropertyToFunction_RefusesWhenThePropertyIsPassedByRef()
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

                public void Bump() => Bump(ref Value);
            }
            """));

        var service = new PropertyToFunctionService(workspace);
        var result = await service.PropertyToFunction(workspace.PathOf("C.cs"), "Value", dryRun: true);

        Assert.Contains("ref/out/in", ResultAssert.Error(result));
    }

    [Fact]
    public async Task InlineParameter_SubstitutesAFixedArgumentAndRemovesTheParameter()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", """
                namespace Probe;

                internal static class C
                {
                    public const int Limit = 3;

                    public static int Clamp(int value, int limit)
                    {
                        return System.Math.Min(value, limit);
                    }
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Run(int value) => C.Clamp(value, C.Limit);
                }
                """));

        var service = new InlineParameterService(workspace);
        var result = await service.InlineParameter(workspace.PathOf("C.cs"), "Clamp", "limit", dryRun: false);
        ResultAssert.Succeeded(result);

        var declaration = workspace.Read("C.cs");
        Assert.Contains("public static int Clamp(int value)", declaration);
        Assert.DoesNotContain("int limit", declaration);
        Assert.Contains("System.Math.Min(value, C.Limit)", declaration);

        Assert.Contains("C.Clamp(value)", workspace.Read("Use.cs"));
    }

    [Fact]
    public async Task InlineParameter_RefusesAnArgumentThatReadsACallerLocal()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", """
                namespace Probe;

                internal static class C
                {
                    public static int Clamp(int value, int limit) => System.Math.Min(value, limit);
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Run(int value)
                    {
                        var local = 3;
                        return C.Clamp(value, local);
                    }
                }
                """));

        var service = new InlineParameterService(workspace);
        var result = await service.InlineParameter(workspace.PathOf("C.cs"), "Clamp", "limit", dryRun: true);

        Assert.Contains("local/parameter", ResultAssert.Error(result));
    }

    [Fact]
    public async Task InlineParameter_RefusesCallSitesThatPassDifferentValues()
    {
        using var workspace = FakeWorkspace.Create(
            ("C.cs", """
                namespace Probe;

                internal static class C
                {
                    public static int Clamp(int value, int limit) => System.Math.Min(value, limit);
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal static class Use
                {
                    public static int Three(int value) => C.Clamp(value, 3);

                    public static int Four(int value) => C.Clamp(value, 4);
                }
                """));

        var service = new InlineParameterService(workspace);
        var result = await service.InlineParameter(workspace.PathOf("C.cs"), "Clamp", "limit", dryRun: true);

        Assert.Contains("different values", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ExtractSuperclass_MovesTheSelectionToANewBaseClass()
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

                public int Doubled() => _value * 2;
            }
            """));

        var service = new ExtractSuperclassService(workspace);
        var result = await service.ExtractSuperclass(
            workspace.PathOf("C.cs"), "C", new[] { "_value", "Value", "Doubled" }, "Base", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("class C : Base", workspace.Read("C.cs"));
        Assert.DoesNotContain("Doubled", workspace.Read("C.cs"));

        var baseClass = workspace.Read("Base.cs").Replace("\r\n", "\n");
        Assert.Contains("internal class Base", baseClass);
        Assert.Contains("private int _value;", baseClass);
        Assert.Contains("public int Doubled() => _value * 2;", baseClass);
        Assert.Contains("namespace Probe;", baseClass);
    }

    [Fact]
    public async Task ExtractSuperclass_RefusesAMemberThatReadsSomethingStayingBehind()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private int _value;

                public int Doubled() => _value * 2;
            }
            """));

        var service = new ExtractSuperclassService(workspace);
        var result = await service.ExtractSuperclass(
            workspace.PathOf("C.cs"), "C", new[] { "Doubled" }, "Base", dryRun: true);

        Assert.Contains("_value", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ConvertAnonymousToType_GeneratesAClassAndRewritesTheCreation()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal static class C
            {
                public static object Make(int id, string name) => new { Id = id, Name = name };
            }
            """));

        var service = new ConvertAnonymousToTypeService(workspace);
        var result = await service.ConvertAnonymousToType(workspace.PathOf("C.cs"), 5, "Person", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("new Person(id, name)", workspace.Read("C.cs"));

        var generated = workspace.Read("Person.cs").Replace("\r\n", "\n");
        Assert.Contains("internal sealed class Person", generated);
        Assert.Contains("public Person(int id, string name)", generated);
        Assert.Contains("public int Id { get; }", generated);
        Assert.Contains("public string Name { get; }", generated);
    }

    [Fact]
    public async Task RenameNamespace_UpdatesTheDeclarationsAndTheUsings()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                namespace Probe.Core;

                internal static class A
                {
                    public static int Value() => 1;
                }
                """),
            ("B.cs", """
                using Probe.Core;

                namespace Probe.App;

                internal static class B
                {
                    public static int Use() => A.Value();
                }
                """));

        var service = new RenameNamespaceService(workspace);
        var result = await service.RenameNamespace("Probe.Core", "Kernel", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("namespace Probe.Kernel;", workspace.Read("A.cs"));
        Assert.Contains("using Probe.Kernel;", workspace.Read("B.cs"));
        Assert.DoesNotContain("Probe.Core", workspace.Read("B.cs"));
    }

    [Fact]
    public async Task MoveToNamespace_ChangesTheNamespaceAndFixesTheUsings()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                namespace Probe.Old;

                internal static class A
                {
                    public static int Value() => 1;
                }
                """),
            ("B.cs", """
                using Probe.Old;

                namespace Probe.App;

                internal static class B
                {
                    public static int Use() => A.Value();
                }
                """));

        var service = new MoveToNamespaceService(workspace);
        var result = await service.MoveToNamespace(workspace.PathOf("A.cs"), "Probe.New", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("namespace Probe.New;", workspace.Read("A.cs"));

        var consumer = workspace.Read("B.cs").Replace("\r\n", "\n");
        Assert.Contains("using Probe.New;", consumer);
        Assert.DoesNotContain("using Probe.Old;", consumer);
    }

    [Fact]
    public async Task MoveToNamespace_RefusesWhenAQualifiedReferenceWouldBreak()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", """
                namespace Probe.Old;

                internal static class A
                {
                    public static int Value() => 1;
                }
                """),
            ("B.cs", """
                namespace Probe.App;

                internal static class B
                {
                    public static int Use() => Probe.Old.A.Value();
                }
                """));

        var service = new MoveToNamespaceService(workspace);
        var result = await service.MoveToNamespace(workspace.PathOf("A.cs"), "Probe.New", dryRun: true);

        Assert.Contains("Refused", ResultAssert.Error(result));
    }
}
