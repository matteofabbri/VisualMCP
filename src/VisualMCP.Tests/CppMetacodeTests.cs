using ModelContextProtocol.Protocol;
using Snitch.Metacode;
using Snitch.Metacode.Rendering;
using VisualMCP.Translation;
using VisualMCP.Tools.Metacode;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// C++ and Python as metacode languages rather than a side tool: the two dump readers are part of the
/// default pipeline, a saved dump is recognised by its content, and metacode_translate /
/// metacode_compare run the dumper themselves for a .cpp/.py path. The compile-and-dump half is skipped
/// on a machine without clang/Python; the dump-to-pseudocode half runs everywhere.
/// </summary>
public sealed class CppMetacodeTests
{
    private const string ClangDump = """
        {
          "kind": "TranslationUnitDecl",
          "inner": [
            {
              "kind": "FunctionDecl",
              "name": "add",
              "type": { "qualType": "int (int)" },
              "inner": [
                { "kind": "ParmVarDecl", "name": "a", "type": { "qualType": "int" } },
                {
                  "kind": "CompoundStmt",
                  "inner": [
                    { "kind": "ReturnStmt", "inner": [{ "kind": "IntegerLiteral", "value": "1" }] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string PythonDump = """
        {
          "_type": "Module",
          "body": [
            {
              "_type": "FunctionDef",
              "name": "add",
              "args": {
                "_type": "arguments",
                "posonlyargs": [],
                "args": [{ "_type": "arg", "arg": "a" }],
                "vararg": null,
                "kwonlyargs": [],
                "kw_defaults": [],
                "kwarg": null,
                "defaults": []
              },
              "body": [
                {
                  "_type": "Return",
                  "value": {
                    "_type": "BinOp",
                    "left": { "_type": "Name", "id": "a" },
                    "op": { "_type": "Add" },
                    "right": { "_type": "Constant", "value": 1 }
                  }
                }
              ],
              "decorator_list": [],
              "returns": null
            }
          ]
        }
        """;

    [Fact]
    public void A_clang_dump_translates_through_the_default_pipeline()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCppAst(ClangDump, "sample.cpp"));

        Assert.Equal(MetacodeLanguage.Cpp, document.Language);
        Assert.Empty(document.Diagnostics);

        var pseudocode = new PseudocodeRenderer().Render(document);
        Assert.Contains("add(a", pseudocode);
        Assert.Contains("RETURN 1", pseudocode);
        Assert.DoesNotContain("RAW[cpp]", pseudocode);
    }

    [Fact]
    public void A_cpython_dump_translates_through_the_default_pipeline()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPythonAst(PythonDump, "sample.py"));

        Assert.Equal(MetacodeLanguage.Python, document.Language);
        Assert.Empty(document.Diagnostics);
        Assert.Contains("RETURN a + 1", new PseudocodeRenderer().Render(document));
    }

    [Fact]
    public void A_saved_clang_dump_is_recognised_from_its_content()
    {
        var path = TempFile("dump.json", ClangDump);
        try
        {
            var source = MetacodeSource.FromFile(path);

            Assert.Equal(MetacodeLanguage.Cpp, source.Language);
            var document = MetacodeTranslator.Default.Translate(source);
            Assert.DoesNotContain(document.Diagnostics, d => d.Code == MetacodeDiagnosticCodes.NoFrontend);
            Assert.Contains("RETURN 1", new PseudocodeRenderer().Render(document));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void A_saved_cpython_dump_is_recognised_from_its_content()
    {
        var path = TempFile("dump.json", PythonDump);
        try
        {
            var source = MetacodeSource.FromFile(path);

            Assert.Equal(MetacodeLanguage.Python, source.Language);
            Assert.DoesNotContain(
                MetacodeTranslator.Default.Translate(source).Diagnostics,
                d => d.Code == MetacodeDiagnosticCodes.NoFrontend);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task metacode_translate_reads_a_clang_dump_as_a_language()
    {
        var result = Assert.IsType<CallToolResult>(
            await MetacodeTranslateTool.Translate(new SourceDumpTranslationService(), text: ClangDump, language: "cpp"));

        var text = Text(result);
        Assert.Contains("add(a", text);
        Assert.Contains("RETURN 1", text);
        Assert.DoesNotContain("RAW[cpp]", text);
    }

    [Fact]
    public async Task metacode_translate_refuses_json_that_is_not_a_clang_dump()
    {
        var result = Assert.IsType<CallToolResult>(
            await MetacodeTranslateTool.Translate(new SourceDumpTranslationService(), text: "{\"a\": 1}", language: "cpp"));

        Assert.True(result.IsError);
        Assert.Contains("TranslationUnitDecl", Text(result));
    }

    [Fact]
    public async Task metacode_translate_runs_the_dumper_for_a_cpp_path()
    {
        if (!CompilerIsPresent("clang++") && !CompilerIsPresent("clang"))
            return; // no clang on this machine: the dump paths above are the ones under test.

        var path = TempFile("sample.cpp", "int add(int a)\n{\n    return 1;\n}\n");
        try
        {
            var result = Assert.IsType<CallToolResult>(
                await MetacodeTranslateTool.Translate(new SourceDumpTranslationService(), path: path, timeoutSeconds: 120));

            Assert.NotEqual(true, result.IsError);
            var text = Text(result);
            Assert.Contains("add(a", text);
            Assert.Contains("RETURN 1", text);
            Assert.Contains(" · cpp · ", text); // the header line names the language
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task metacode_translate_says_how_to_install_the_dumper_when_it_is_missing()
    {
        if (CompilerIsPresent("clang++") || CompilerIsPresent("clang"))
            return; // clang is here: the path above exercises the real dump instead.

        var path = TempFile("sample.cpp", "int add(int a)\n{\n    return 1;\n}\n");
        try
        {
            var result = Assert.IsType<CallToolResult>(
                await MetacodeTranslateTool.Translate(new SourceDumpTranslationService(), path: path));

            Assert.True(result.IsError);
            var text = Text(result);
            Assert.Contains("No Clang compiler could produce the AST dump", text);
            Assert.Contains("Install Clang", text);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task metacode_compare_pairs_a_cpp_dump_with_its_pseudocode()
    {
        var rendered = new PseudocodeRenderer().Render(
            MetacodeTranslator.Default.Translate(MetacodeSource.FromCppAst(ClangDump)));

        var result = Assert.IsType<CallToolResult>(await MetacodeCompareTool.Compare(
            new SourceDumpTranslationService(),
            leftText: ClangDump,
            leftLanguage: "cpp",
            rightText: rendered,
            rightLanguage: "pseudo"));

        Assert.NotEqual(true, result.IsError);
        var comparison = Text(result);
        Assert.True(comparison.Contains("identical", StringComparison.Ordinal), comparison);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    /// <summary>A file in its own temporary folder, so the dumper's working directory holds only it.</summary>
    private static string TempFile(string name, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vmcp-cpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Delete(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best-effort: a locked temp folder is not a test failure
        }
    }

    private static bool CompilerIsPresent(string name)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
