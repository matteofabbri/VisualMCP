using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snitch.Languages.CSharp.Reflection;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// find_assembly_api: "which types/members of this DLL are called X" in one call.
/// It exists because the recorded history shows inspect_assembly called once per
/// type name (49 times on one Roslyn DLL), so the assertions are about the shape
/// the model reads back, not about the .NET reflection calls.
/// </summary>
public sealed class AssemblyApiSearchTests : IClassFixture<ProbeAssembly>
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly ProbeAssembly _probe;
    private readonly InspectAssemblyService _inspect = new();

    public AssemblyApiSearchTests(ProbeAssembly probe) => _probe = probe;

    private JsonElement Find(string pattern, string? kind = null, int maxMatches = 60) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            _inspect.FindApi(_probe.DllPath, pattern, kind, maxMatches), Wire)).RootElement;

    [Fact]
    public void A_type_name_returns_the_type_with_all_its_members()
    {
        var result = Find("Widget$");

        Assert.Equal(1, result.GetProperty("matchedTypeCount").GetInt32());
        var type = result.GetProperty("types")[0];
        Assert.Equal("Demo.Api.Widget", type.GetProperty("fullName").GetString());
        Assert.Equal("type", type.GetProperty("matchedBy").GetString());

        // This is the "one inspect_assembly per typeFilter" case: all members, not a filtered subset.
        var methods = type.GetProperty("members").GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains(methods, m => m!.Contains("Create"));
        Assert.Contains(methods, m => m!.Contains("Reset"));
        Assert.Contains(type.GetProperty("members").GetProperty("properties").EnumerateArray(),
            p => p.GetString()!.Contains("Id"));
    }

    [Fact]
    public void A_member_name_returns_only_the_matching_signatures()
    {
        var result = Find("^Save$");

        var type = Assert.Single(result.GetProperty("types").EnumerateArray());
        Assert.Equal("Demo.Api.IWidgetStore", type.GetProperty("fullName").GetString());
        Assert.Equal("member", type.GetProperty("matchedBy").GetString());

        var methods = type.GetProperty("members").GetProperty("methods").EnumerateArray().ToList();
        Assert.Contains(methods, m => m.GetString()!.Contains("Save(Widget widget)"));
        Assert.DoesNotContain(methods, m => m.GetString()!.Contains("Get(int id)"));
    }

    [Fact]
    public void A_kind_filter_keeps_only_that_type_kind()
    {
        var result = Find(".", "enum");

        var type = Assert.Single(result.GetProperty("types").EnumerateArray());
        Assert.Equal("Demo.Api.WidgetKind", type.GetProperty("fullName").GetString());
        Assert.Equal("enum", type.GetProperty("kind").GetString());

        var fields = type.GetProperty("members").GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains(fields, f => f!.Contains("Small"));
        Assert.Contains(fields, f => f!.Contains("Large"));
        Assert.DoesNotContain(fields, f => f!.Contains("value__"));
    }

    [Fact]
    public void A_member_kind_filter_ignores_a_matching_type_name()
    {
        // "Widget" names three types; kind=method says only member matches count, so a type
        // whose name matches but that has no method matching is not an answer.
        var result = Find(".", "method");

        Assert.True(result.GetProperty("matchedTypeCount").GetInt32() >= 2);
        foreach (var type in result.GetProperty("types").EnumerateArray())
        {
            Assert.Equal("member", type.GetProperty("matchedBy").GetString());
            var methods = type.GetProperty("members").GetProperty("methods");
            Assert.NotEqual(0, methods.GetArrayLength());
            Assert.Equal(0, type.GetProperty("members").GetProperty("properties").GetArrayLength());
        }
    }

    [Fact]
    public void maxMatches_truncates_and_names_what_was_left_out()
    {
        var result = Find(".", null, maxMatches: 1);

        Assert.Equal(1, result.GetProperty("returnedTypeCount").GetInt32());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(result.GetProperty("leftOut").EnumerateArray());
    }

    [Fact]
    public void An_invalid_pattern_is_an_error_and_not_an_exception()
    {
        var result = Find("([");

        Assert.Contains("Invalid regex", result.GetProperty("error").GetString());
        Assert.False(result.TryGetProperty("types", out _));
    }

    [Fact]
    public void A_missing_assembly_is_an_error()
    {
        var result = JsonDocument.Parse(JsonSerializer.Serialize(
            _inspect.FindApi(Path.Combine(Path.GetTempPath(), "does-not-exist.dll"), "X", null, 10), Wire))
            .RootElement;

        Assert.Contains("Assembly not found", result.GetProperty("error").GetString());
    }
}

/// <summary>A tiny assembly emitted once per test class run, so the tests never depend on a machine DLL.</summary>
public sealed class ProbeAssembly : IDisposable
{
    private const string Source = """
        namespace Demo.Api;

        public sealed class Widget
        {
            public int Id { get; set; }
            public void Reset() { }
            public static Widget Create(int id) => new Widget { Id = id };
        }

        public interface IWidgetStore
        {
            Widget Get(int id);
            void Save(Widget widget);
        }

        public enum WidgetKind { Small, Large }
        """;

    public string DllPath { get; }

    public ProbeAssembly() => DllPath = Emit();

    private static string Emit()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "Probe.Api.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "Probe.Api",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var directory = Path.Combine(Path.GetTempPath(), "vmcp-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dll = Path.Combine(directory, "Probe.Api.dll");

        var emit = compilation.Emit(dll);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Select(d => d.ToString())));
        return dll;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(DllPath)!, recursive: true); }
        catch { /* best effort: the temp folder is not worth failing a test for */ }
    }
}
