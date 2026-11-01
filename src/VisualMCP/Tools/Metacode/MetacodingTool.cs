using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Coding;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodingTool
{
    [McpServerTool(Name = "metacoding"), Description(
        "Write code through the METACODE model — the operations that are more than a mechanical style rule. The trivial " +
        "improvements stay where they are (simplify_code for style quick-fixes, the Refactoring tools for rename/extract/" +
        "change-signature, code_cleanup for a whole-file sweep); use THIS for the three that start or end in a metacode " +
        "tree:\n" +
        "• action=decompile — decompile a binary and STORE what it finds in .visualMCP/visualmcp.db, in the same AST the " +
        "solution's own files live in. A MANAGED assembly (.NET, CLR metadata) becomes one metacode tree per type under " +
        "assembly://<file>/<type>, queryable with metacode_index (trees, complexity) and comparable with " +
        "metacode_compare. A JAVA binary (.class/.dex/.jar/.apk/.aar/.war, or a zip that carries .class entries) is " +
        "handed to JADX — installed on demand on Windows/Linux/macOS if this machine has none, and needing a JDK — and " +
        "every decompiled .java file becomes one metacode document under java://<archive>/<unit>, after the Java frontend " +
        "lowers it into the model. A NATIVE binary (no CLR metadata, no Java bytecode) is handed to Ghidra — installed on " +
        "demand if this machine has none — and its recovered functions are stored TWICE: in the dedicated tables " +
        "native_artifact / native_function (Ghidra's C + canonical pseudocode + complexity per function) / native_call " +
        "(call edges), keyed on the file's SHA-256, AND as one metacode document under native://<file> (its functions as " +
        "the declarations). " +
        "Every stored row records the binary in source_path and carries the decompiled flag, so a query never confuses " +
        "decompiled code with code read from a source file. Keyed on the binary's MVID (managed) or SHA-256 (native and " +
        "Java): the same binary is not decompiled twice unless force=true. With siblings=true (the default) the folder " +
        "the binary came from is swept after it: its text files are read into the store (a file no frontend reads becomes " +
        "a `file` node) and its other binaries are decompiled too, up to maxSiblings files, 2 native images and 1 Java " +
        "archive — so one call fills the AST with the whole folder instead of one file. This is the only decompile call " +
        "that PERSISTS its output; decompile_type / decompile_assembly return text and keep nothing.\n" +
        "• action=rewrite — replace the code of an existing declaration with what 'source' describes, emit it as C#, and " +
        "write it back to the file the declaration came from. 'source' is metacode pseudocode by default (language=pseudo) " +
        "or C# (language=cs). Previewed unless dryRun=false; every write is compiler-gated (refused when it would add an " +
        "error) and refreshes the stored tree.\n" +
        "• action=insert — append a NEW member, generated the same way, to an existing type. Same gate and preview.\n" +
        "A rewrite/insert answers with the generated C#, the line diff and the compiler verdict.")]
    public static async Task<object> Metacoding(
        IMetacodeCodingService coding,
        [Description("decompile | rewrite | insert.")] string action,
        [Description("decompile: path to the binary to decompile and store — a managed .dll/.exe (ILSpy), a Java .class/.dex/.jar/.apk/.aar/.war (JADX) or a native PE/ELF/Mach-O (Ghidra).")]
        string? assemblyPath = null,
        [Description("decompile (managed only): only types whose full name contains this text (case-insensitive).")] string? nameFilter = null,
        [Description("decompile (managed only): only types in this namespace or a sub-namespace (prefix match, case-insensitive).")] string? namespaceFilter = null,
        [Description("decompile (managed only): maximum types to store (0 = all).")] int maxTypes = 0,
        [Description("decompile: decompile again even when the store already holds this binary (MVID for managed, SHA-256 for native and Java).")]
        bool force = false,
        [Description("decompile: after the binary, sweep its folder — read the text files next to it and decompile its other binaries into the same AST (default true).")]
        bool siblings = true,
        [Description("decompile: how many sibling files the sweep may process (default 25). Native siblings are capped at 2 per sweep, Java archives at 1.")]
        int maxSiblings = 25,
        [Description("rewrite: the declaration to replace — a name ('Divide'), a qualified name ('Calculator.Divide') or a declaration key.")]
        string? symbol = null,
        [Description("insert: the type to add the member to — simple ('Calculator') or qualified ('Demo.Calculator').")]
        string? intoType = null,
        [Description("rewrite/insert: the new code. Canonical pseudocode by default, or C# when language=cs.")] string? source = null,
        [Description("rewrite/insert: 'pseudo' (default) or 'cs'.")] string? language = null,
        [Description("rewrite/insert: preview only (default true). Pass false to write the file.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        switch ((action ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "decompile":
                if (string.IsNullOrWhiteSpace(assemblyPath))
                    return new { error = "decompile needs assemblyPath." };

                return await coding.DecompileAsync(
                    assemblyPath!, nameFilter, namespaceFilter, maxTypes, force, siblings, maxSiblings, cancellationToken);

            case "rewrite":
                if (string.IsNullOrWhiteSpace(symbol))
                    return new { error = "rewrite needs symbol (the declaration to replace) and source." };

                return await coding.RewriteAsync(symbol!, source ?? string.Empty, language, dryRun, cancellationToken);

            case "insert":
                if (string.IsNullOrWhiteSpace(intoType))
                    return new { error = "insert needs intoType (the type to extend) and source." };

                return await coding.InsertAsync(intoType!, source ?? string.Empty, language, dryRun, cancellationToken);

            default:
                return new { error = "action must be decompile, rewrite or insert." };
        }
    }
}
