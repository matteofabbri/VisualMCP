using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Translation;

namespace VisualMCP.Tools.Translation;

[McpServerToolType]
public static class TranslateSourceFileTools
{
    [McpServerTool(Name = "translate_python_file"), Description(
        "Translate a Python file into canonical pseudocode: it runs CPython's own ast module (the dumper " +
        "documented in docs/metacode.md) and feeds the JSON to the metacode Python frontend, returning the " +
        "pseudocode plus the origin lines and any construct that could not be modelled. Pass dumpJson to skip " +
        "the interpreter and translate a dump produced elsewhere. Use it to read a Python file in the same " +
        "neutral shape as C#, or to compare two implementations line by line with metacode_compare.")]
    public static Task<object> TranslatePythonFile(
        ISourceDumpTranslationService translation,
        [Description("The .py file to translate. Required unless dumpJson is given.")] string? path = null,
        [Description("Optional: the AST dump JSON, as produced by the four-line dumper (skips running Python).")] string? dumpJson = null,
        [Description("Timeout in seconds for the interpreter (default 120, max 900).")] int timeoutSeconds = 120)
        => translation.TranslateAsync("python", path, dumpJson, timeoutSeconds);

    [McpServerTool(Name = "translate_cpp_file"), Description(
        "Translate a C++ file into canonical pseudocode: it runs `clang -Xclang -ast-dump=json -fsyntax-only` " +
        "and feeds the JSON to the metacode C++ frontend, returning the pseudocode plus the origin lines and " +
        "any construct that could not be modelled. Clang is looked for on PATH first and then in the LLVM toolset " +
        "Visual Studio ships, so an install without clang on PATH still works. A translation unit that includes " +
        "the standard library dumps every declaration of every header (hundreds of MB): pass filter=\"<name>\" to " +
        "keep only the declarations whose qualified name contains it — a namespace or a type narrows it to your code. " +
        "Pass dumpJson to skip clang and translate a dump produced elsewhere. Use it to read a C/C++ file in the " +
        "same neutral shape as C#, or with metacode_compare.")]
    public static Task<object> TranslateCppFile(
        ISourceDumpTranslationService translation,
        [Description("The .cpp/.cc/.h file to translate. Required unless dumpJson is given.")] string? path = null,
        [Description("Optional: the clang JSON AST dump (skips running the compiler).")] string? dumpJson = null,
        [Description("Optional: keep only the declarations whose qualified name contains this text " +
                     "(clang -ast-dump-filter). Use your namespace to leave the standard library out.")] string? filter = null,
        [Description("Optional: extra clang arguments for this project, passed verbatim and resolved from the " +
                     "file's folder — '-std=c++20 -I../include -DFOO=1'.")] string? clangArgs = null,
        [Description("Timeout in seconds for the compiler (default 120, max 900).")] int timeoutSeconds = 120)
        => translation.TranslateAsync("cpp", path, dumpJson, timeoutSeconds, filter, clangArgs);
}
