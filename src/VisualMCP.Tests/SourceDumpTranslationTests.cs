using VisualMCP.Translation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// translate_python_file / translate_cpp_file: the translation half runs without Python or clang, so the
/// pipeline from an AST dump to canonical pseudocode is pinned by tests on every machine.
/// </summary>
public sealed class SourceDumpTranslationTests
{
    private const string PythonAdd = """
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

    private const string CppAdd = """
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

    [Fact]
    public void A_python_dump_becomes_pseudocode()
    {
        var result = ResultAssert.Json(SourceDumpTranslationService.TranslateDump("python", PythonAdd, "sample.py"));

        Assert.Equal("python", result.GetProperty("language").GetString());
        Assert.Equal("Python", result.GetProperty("frontend").GetString());
        Assert.Equal(0, result.GetProperty("diagnosticCount").GetInt32());

        var pseudocode = result.GetProperty("pseudocode").GetString()!;
        Assert.Contains("add(a", pseudocode);
        Assert.Contains("RETURN a + 1", pseudocode);
    }

    [Fact]
    public void A_cpp_dump_becomes_pseudocode()
    {
        var result = ResultAssert.Json(SourceDumpTranslationService.TranslateDump("cpp", CppAdd, "sample.cpp"));

        Assert.Equal("cpp", result.GetProperty("language").GetString());
        Assert.Equal("C++", result.GetProperty("frontend").GetString());
        Assert.Equal(0, result.GetProperty("diagnosticCount").GetInt32());

        var pseudocode = result.GetProperty("pseudocode").GetString()!;
        Assert.Contains("add(a", pseudocode);
        Assert.Contains("RETURN 1", pseudocode);
    }

    [Fact]
    public void SourceTextInsteadOfADumpIsReportedAsAParseError()
    {
        var result = ResultAssert.Json(SourceDumpTranslationService.TranslateDump("python", "def f():\n    pass\n", "x.py"));

        Assert.True(result.GetProperty("diagnosticCount").GetInt32() > 0);
        Assert.Contains("AST dumper", result.GetProperty("diagnostics")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void An_unknown_language_is_refused()
    {
        var result = ResultAssert.Json(SourceDumpTranslationService.TranslateDump("rust", "{}", "x.rs"));

        Assert.Contains("Unsupported language", result.GetProperty("error").GetString());
    }

    [Fact]
    public void A_missing_pathWithoutADumpIsRefused()
    {
        var service = new SourceDumpTranslationService();
        var result = ResultAssert.Json(service.TranslateAsync("python", null, null, 10).GetAwaiter().GetResult());

        Assert.Contains("file path is required", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_real_python_dumper_is_used_whenAnInterpreterIsAvailable()
    {
        if (!InterpreterIsPresent("python") && !InterpreterIsPresent("python3"))
            return; // no Python on this machine: the dumpJson path above is the one under test.

        var directory = Path.Combine(Path.GetTempPath(), "vmcp-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "sample.py");
            await File.WriteAllTextAsync(file, "def add(a):\n    return a + 1\n");

            var service = new SourceDumpTranslationService();
            var result = ResultAssert.Json(await service.TranslateAsync("python", file, null, 120));

            Assert.NotEqual(default, result.GetProperty("declarationCount").GetInt32());
            Assert.Contains("a + 1", result.GetProperty("pseudocode").GetString());
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best-effort */ }
        }
    }

    private static bool InterpreterIsPresent(string name)
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
