using Snitch.Execution;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;

namespace VisualMCP.Translation;

/// <summary>
/// The dump a dumper produced, or why it could not and what would fix it.
/// <see cref="Tool"/> names the interpreter/compiler that answered (<c>python3</c>, <c>clang++</c>) or
/// <c>supplied dump</c> when the caller passed one.
/// </summary>
public sealed record SourceDump(string? Json, string? Error, string? Hint, string Tool);

/// <summary>
/// Produces the AST dump a Python or C++ metacode reader consumes, and translates one into canonical
/// pseudocode. Both readers in the library read a dump rather than the source, so this service runs the
/// dumper — <c>python -m ast</c> through the four-line script, <c>clang -Xclang -ast-dump=json</c>. A
/// caller that already has a dump skips the external process, which is how a machine without Python or
/// clang still translates.
/// </summary>
public interface ISourceDumpTranslationService
{
    /// <summary>Translates <paramref name="path"/> (or the supplied <paramref name="dumpJson"/>) for one language.</summary>
    Task<object> TranslateAsync(string language, string? path, string? dumpJson, int timeoutSeconds);

    /// <summary>
    /// The dump alone, without translating it: <paramref name="path"/>, or <paramref name="sourceText"/>
    /// written to a temporary file so the dumper can read it. The caller hands the JSON to the metacode
    /// pipeline itself — which is what <c>metacode_translate</c> and <c>metacode_compare</c> do, so a
    /// <c>.cpp</c> path goes through the same readers and options as every other language.
    /// </summary>
    Task<SourceDump> ProduceDumpAsync(string language, string? path, string? sourceText, int timeoutSeconds);
}

public sealed class SourceDumpTranslationService : ISourceDumpTranslationService
{
    /// <summary>The dumper documented in <c>docs/metacode.md</c>: CPython's own <c>ast</c> module as JSON.</summary>
    private const string PythonDumper = """
        import ast, json, sys

        def dump(node):
            if isinstance(node, ast.AST):
                out = {"_type": type(node).__name__}
                for field in node._fields:
                    out[field] = dump(getattr(node, field, None))
                if hasattr(node, "lineno"):
                    out["lineno"], out["col_offset"] = node.lineno, node.col_offset
                    out["end_lineno"], out["end_col_offset"] = node.end_lineno, node.end_col_offset
                return out
            if isinstance(node, list):
                return [dump(item) for item in node]
            return node

        source = open(sys.argv[1], encoding="utf-8").read()
        print(json.dumps(dump(ast.parse(source, filename=sys.argv[1])), indent=2))
        """;

    public async Task<object> TranslateAsync(string language, string? path, string? dumpJson, int timeoutSeconds)
    {
        var (kind, error) = Normalize(language);
        if (error is not null) return new { error };

        if (!string.IsNullOrWhiteSpace(dumpJson))
            return Translate(kind, dumpJson, path, "supplied dump");

        if (string.IsNullOrWhiteSpace(path))
            return new { error = "A file path is required (or pass dumpJson with the dump produced elsewhere)." };
        if (!File.Exists(path))
            return new { error = $"File not found: {path}" };

        var dump = await ProduceDumpAsync(kind, path, null, timeoutSeconds);
        if (dump.Error is not null)
            return new { error = dump.Error, language = kind, hint = dump.Hint };

        return Translate(kind, dump.Json!, path, dump.Tool);
    }

    public async Task<SourceDump> ProduceDumpAsync(string language, string? path, string? sourceText, int timeoutSeconds)
    {
        var (kind, error) = Normalize(language);
        if (error is not null) return new SourceDump(null, error, null, language);

        var timeout = Math.Clamp(timeoutSeconds < 1 ? 120 : timeoutSeconds, 1, 900);

        string? temporary = null;
        try
        {
            var target = path;
            if (string.IsNullOrWhiteSpace(target))
            {
                if (string.IsNullOrWhiteSpace(sourceText))
                    return new SourceDump(null, "A file path or source text is required.", null, kind);

                // The dumper is run against a file, so text becomes one. A C++ translation unit that
                // includes a header next to the caller's text cannot resolve it: pass the path instead.
                var extension = kind == "python" ? ".py" : ".cpp";
                temporary = Path.Combine(Path.GetTempPath(), $"visualmcp-ast-{Guid.NewGuid():N}{extension}");
                await File.WriteAllTextAsync(temporary, sourceText);
                target = temporary;
            }
            else if (!File.Exists(target))
            {
                return new SourceDump(null, $"File not found: {target}", null, kind);
            }

            var (json, dumpError, tool) = kind == "python"
                ? await ProducePythonDumpAsync(target, timeout)
                : await ProduceCppDumpAsync(target, timeout);

            return dumpError is null
                ? new SourceDump(json, null, null, tool)
                : new SourceDump(null, dumpError, Hint(kind), tool);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { /* best-effort */ }
            }
        }
    }

    private static string Hint(string kind) => kind == "python"
        ? "Install Python 3 (the standard library's ast module is all this needs), or pass dumpJson."
        : "Install Clang, or pass dumpJson with the output of 'clang -Xclang -ast-dump=json -fsyntax-only <file>'.";

    /// <summary>The translation half, without an external process: public so the pipeline is testable.</summary>
    public static object TranslateDump(string language, string json, string? path)
    {
        var (kind, error) = Normalize(language);
        if (error is not null) return new { error };
        return Translate(kind, json, path, "supplied dump");
    }

    private static object Translate(string kind, string json, string? path, string source)
    {
        var translator = new MetacodeTranslator(kind == "python"
            ? [new PythonFrontend()]
            : [new CppFrontend()]);

        var name = path is null ? "dump" : Path.GetFileName(path);
        var document = translator.Translate(kind == "python"
            ? MetacodeSource.FromPythonAst(json, name)
            : MetacodeSource.FromCppAst(json, name));

        var pseudocode = new PseudocodeRenderer().Render(document);

        return new
        {
            language = kind,
            path,
            source,
            frontend = translator.Frontends[0].Name,
            declarationCount = document.Modules.Sum(module => module.Declarations.Count),
            diagnosticCount = document.Diagnostics.Count,
            diagnostics = document.Diagnostics
                .Take(50)
                .Select(diagnostic => new
                {
                    code = diagnostic.Code,
                    severity = diagnostic.Severity.ToString(),
                    line = diagnostic.Origin.StartLine,
                    message = diagnostic.Message,
                })
                .ToList(),
            pseudocode = ProcessRunner.Truncate(pseudocode, 24_000),
        };
    }

    /// <summary>Runs the Python dumper against <paramref name="path"/>, trying python3 then python.</summary>
    private static async Task<(string? json, string? error, string tool)> ProducePythonDumpAsync(string path, int timeoutSeconds)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"visualmcp-ast-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, PythonDumper);

        try
        {
            var failures = new List<string>();
            foreach (var interpreter in new[] { "python3", "python" })
            {
                string output;
                try
                {
                    var (exitCode, timedOut, stdout, stderr, _) =
                        await ProcessRunner.RunAsync(interpreter, $"\"{scriptPath.Replace('\\', '/')}\" \"{path.Replace('\\', '/')}\"",
                            Path.GetDirectoryName(Path.GetFullPath(path))!, timeoutSeconds);

                    if (timedOut) return (null, $"The Python AST dump with '{interpreter}' timed out after {timeoutSeconds}s.", interpreter);
                    if (exitCode != 0)
                    {
                        failures.Add($"{interpreter}: {ProcessRunner.Truncate((stderr + stdout).Trim(), 800)}");
                        continue;
                    }
                    output = stdout;
                }
                catch (Exception ex)
                {
                    failures.Add($"{interpreter}: {ex.Message}");
                    continue;
                }

                if (!output.TrimStart().StartsWith('{'))
                {
                    failures.Add($"{interpreter}: the dumper did not produce JSON ({ProcessRunner.Truncate(output.Trim(), 300)}).");
                    continue;
                }

                return (output, null, interpreter);
            }

            return (null, "No Python 3 interpreter could produce the AST dump. " + string.Join(" | ", failures), "python");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>Runs clang's JSON AST dump against <paramref name="path"/>, trying clang++ then clang (`clang` first for a .c, which is C and not a C++ translation unit).</summary>
    private static async Task<(string? json, string? error, string tool)> ProduceCppDumpAsync(string path, int timeoutSeconds)
    {
        var failures = new List<string>();
        var compilers = Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase)
            ? new[] { "clang", "clang++" }
            : new[] { "clang++", "clang" };
        foreach (var compiler in compilers)
        {
            string output;
            try
            {
                var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
                    compiler,
                    $"-Xclang -ast-dump=json -fsyntax-only \"{path.Replace('\\', '/')}\"",
                    Path.GetDirectoryName(Path.GetFullPath(path))!,
                    timeoutSeconds);

                if (timedOut) return (null, $"The clang AST dump timed out after {timeoutSeconds}s.", compiler);

                // clang reports warnings on stderr and still writes the JSON to stdout; a genuine
                // parse failure leaves stdout empty (exit code is 1 for a syntax error).
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    failures.Add($"{compiler}: {ProcessRunner.Truncate((stderr + stdout).Trim(), 800)}");
                    continue;
                }
                if (exitCode != 0 && !stdout.TrimStart().StartsWith('{'))
                {
                    failures.Add($"{compiler}: exit {exitCode}: {ProcessRunner.Truncate(stderr.Trim(), 800)}");
                    continue;
                }
                output = stdout;
            }
            catch (Exception ex)
            {
                failures.Add($"{compiler}: {ex.Message}");
                continue;
            }

            if (!output.TrimStart().StartsWith('{'))
            {
                failures.Add($"{compiler}: the compiler did not produce JSON ({ProcessRunner.Truncate(output.Trim(), 300)}).");
                continue;
            }

            return (output, null, compiler);
        }

        return (null, "No Clang compiler could produce the AST dump. " + string.Join(" | ", failures), "clang");
    }

    private static (string kind, string? error) Normalize(string language)
    {
        var kind = (language ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            "python" or "py" => ("python", null),
            "cpp" or "c++" or "cxx" => ("cpp", null),
            _ => (kind, $"Unsupported language '{language}'. Use 'python' or 'cpp'."),
        };
    }
}
