using Snitch.Languages.CSharp.Refactoring;
using Snitch.Platform;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Line endings: detection, normalisation, and the guarantee that a refactoring does not change the
/// convention of the file it rewrites — the complaint that produced this code.
/// </summary>
public sealed class LineEndingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vmcp-eol-" + Guid.NewGuid().ToString("N"));

    public LineEndingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── detection ────────────────────────────────────────────────────────────
    [Fact]
    public void CountAndNormalize_HandleBothStylesAndLoneCarriageReturns()
    {
        Assert.Equal((2, 0), LineEndings.Count("a\r\nb\r\n"));
        Assert.Equal((0, 2), LineEndings.Count("a\nb\n"));
        Assert.Equal((1, 1), LineEndings.Count("a\r\nb\n"));
        Assert.True(LineEndings.HasMixed("a\r\nb\n"));
        Assert.False(LineEndings.HasMixed("a\nb\n"));

        Assert.Equal("a\nb\n", LineEndings.Normalize("a\r\nb\r\n", NewLineStyle.Lf));
        Assert.Equal("a\r\nb\r\n", LineEndings.Normalize("a\nb\n", NewLineStyle.CrLf));
        // A lone CR (an old Mac line) is a line break too.
        Assert.Equal("a\nb\n", LineEndings.Normalize("a\rb\r", NewLineStyle.Lf));
    }

    [Fact]
    public void Detect_UsesTheMajorityAndFallsBackWhenThereIsNoNewline()
    {
        Assert.Equal(NewLineStyle.CrLf, LineEndings.Detect("a\r\nb\r\nc\n", NewLineStyle.Lf));
        Assert.Equal(NewLineStyle.Lf, LineEndings.Detect("a\nb\nc\r\n", NewLineStyle.CrLf));
        Assert.Equal(NewLineStyle.Lf, LineEndings.Detect("no newline", NewLineStyle.Lf));
    }

    [Fact]
    public void ForFile_KeepsTheExistingConvention()
    {
        var lf = Write("lf.cs", "class A\n{\n}\n");
        var crlf = Write("crlf.cs", "class A\r\n{\r\n}\r\n");

        Assert.Equal(NewLineStyle.Lf, LineEndings.ForFile(lf));
        Assert.Equal(NewLineStyle.CrLf, LineEndings.ForFile(crlf));
    }

    [Fact]
    public void ForNewFile_FollowsEditorConfigThenGitAttributesThenSiblingsThenTheMachine()
    {
        var editorConfig = Write(".editorconfig", "root = true\n[*]\nend_of_line = lf\n");
        Assert.Equal(NewLineStyle.Lf, LineEndings.ForNewFile(Path.Combine(_directory, "New.cs")));
        File.Delete(editorConfig);

        Write(".gitattributes", "* text=auto eol=crlf\n");
        Assert.Equal(NewLineStyle.CrLf, LineEndings.ForNewFile(Path.Combine(_directory, "New.cs")));
        File.Delete(Path.Combine(_directory, ".gitattributes"));

        // No declaration: the files already beside it decide.
        Write("One.cs", "class A\r\n{\r\n}\r\n");
        Write("Two.cs", "class B\r\n{\r\n}\r\n");
        Assert.Equal(NewLineStyle.CrLf, LineEndings.ForNewFile(Path.Combine(_directory, "New.cs")));

        File.Delete(Path.Combine(_directory, "One.cs"));
        File.Delete(Path.Combine(_directory, "Two.cs"));
        Assert.Equal(LineEndings.DefaultStyle, LineEndings.ForNewFile(Path.Combine(_directory, "New.cs")));
    }

    [Fact]
    public void HostSystem_ReportsThePlatformAndItsConvention()
    {
        Assert.NotEqual(OperatingSystemFamily.Unknown, HostSystem.Family);
        Assert.Equal(HostSystem.IsWindows ? "\r\n" : "\n", HostSystem.DefaultNewLine);
        Assert.Equal(HostSystem.IsWindows, HostSystem.IsCaseInsensitiveFileSystem);
        Assert.Contains(HostSystem.Family.ToString(), HostSystem.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    // ── writing ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task FileText_KeepsTheBomAndNormalisesToTheFile()
    {
        var path = Write("bom.cs", "\uFEFFclass A\n{\n}\n");

        await FileText.WriteAsync(path, "class A\r\n{\r\n}\r\n");

        Assert.True(FileText.HasBom(path));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var text = await File.ReadAllTextAsync(path);
        Assert.Equal("class A\n{\n}\n", text); // LF kept, CRLF normalised away
    }

    [Fact]
    public async Task ARefactoring_KeepsTheLineEndingsOfAnLfFile()
    {
        // The bug: Roslyn emits CRLF for the nodes it rebuilds, so rewriting an LF file used to leave
        // it half LF and half CRLF.
        var workspace = FakeWorkspace.Create(("C.cs",
            "namespace Probe;\n\ninternal class C\n{\n    private int _value = 3;\n\n" +
            "    public int Value\n    {\n        get { return _value; }\n        set { _value = value; }\n    }\n}\n"));

        using var _ = workspace;

        var service = new PropertyToAutoService(workspace);
        var result = await service.PropertyToAuto(workspace.PathOf("C.cs"), "Value", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Value { get; set; } = 3;", text);
        Assert.DoesNotContain("\r", text);
    }

    // ── the migration tool ───────────────────────────────────────────────────
    [Fact]
    public async Task FixLineEndings_RepairsAMixedFileAndLeavesUniformOnesAlone()
    {
        Write("mixed.cs", "line one\r\nline two\nline three\r\n");
        Write("fine.cs", "a\nb\n");

        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var service = new FixLineEndingsService(workspace);

        var result = await service.FixAsync("directory", _directory, null, null, ".cs", dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Wire(result);
        Assert.Equal(1, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(1, json.GetProperty("alreadyCorrect").GetInt32());

        var mixed = File.ReadAllText(Path.Combine(_directory, "mixed.cs"));
        Assert.DoesNotContain("\n", mixed.Replace("\r\n", string.Empty));
        Assert.Equal(NewLineStyle.CrLf, LineEndings.Detect(mixed, NewLineStyle.Lf));
    }

    [Fact]
    public async Task FixLineEndings_CanForceOneStyleAndPreviewFirst()
    {
        var path = Write("crlf.cs", "a\r\nb\r\n");

        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var service = new FixLineEndingsService(workspace);

        var preview = await service.FixAsync("file", path, null, "lf", null, dryRun: true);
        ResultAssert.Succeeded(preview);
        Assert.Equal(1, ResultAssert.Wire(preview).GetProperty("filesChanged").GetInt32());
        Assert.Contains("\r\n", File.ReadAllText(path)); // nothing written

        var applied = await service.FixAsync("file", path, null, "lf", null, dryRun: false);
        ResultAssert.Succeeded(applied);
        Assert.Equal("lf", ResultAssert.Wire(applied).GetProperty("style").GetString());
        Assert.DoesNotContain("\r", File.ReadAllText(path));
    }

    [Fact]
    public async Task FixLineEndings_RejectsAnUnknownStyle()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var service = new FixLineEndingsService(workspace);

        var error = ResultAssert.Error(
            await service.FixAsync("directory", _directory, null, "mac", null, dryRun: true));

        Assert.Contains("not a newline style", error);
    }
}
