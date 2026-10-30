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
        "• action=decompile — decompile a binary and STORE what it finds in .visualMCP/visualmcp.db. A MANAGED assembly " +
        "(.NET, CLR metadata) becomes one metacode tree per type under assembly://<file>/<type>, queryable with " +
        "metacode_index (trees, complexity) and comparable with metacode_compare. A NATIVE binary (no CLR metadata) is " +
        "handed to Ghidra — installed on demand if this machine has none — and its recovered functions go into the " +
        "dedicated tables native_artifact, native_function (Ghidra's C + canonical pseudocode + complexity per function) " +
        "and native_call (call edges), keyed on the file's SHA-256. Keyed on the binary's MVID (managed) or SHA-256 " +
        "(native): the same binary is not decompiled twice unless force=true. This is the only decompile call that " +
        "PERSISTS its output; decompile_type / decompile_assembly return text and keep nothing.\n" +
        "• action=rewrite — replace the code of an existing declaration with what 'source' describes, emit it as C#, and " +
        "write it back to the file the declaration came from. 'source' is metacode pseudocode by default (language=pseudo) " +
        "or C# (language=cs). Previewed unless dryRun=false; every write is compiler-gated (refused when it would add an " +
        "error) and refreshes the stored tree.\n" +
        "• action=insert — append a NEW member, generated the same way, to an existing type. Same gate and preview.\n" +
        "A rewrite/insert answers with the generated C#, the line diff and the compiler verdict.")]
    public static async Task<object> Metacoding(
        IMetacodeCodingService coding,
        [Description("decompile | rewrite | insert.")] string action,
        [Description("decompile: path to the binary to decompile and store — a managed .dll/.exe (ILSpy) or a native PE/ELF/Mach-O (Ghidra).")]
        string? assemblyPath = null,
        [Description("decompile (managed only): only types whose full name contains this text (case-insensitive).")] string? nameFilter = null,
        [Description("decompile (managed only): only types in this namespace or a sub-namespace (prefix match, case-insensitive).")] string? namespaceFilter = null,
        [Description("decompile (managed only): maximum types to store (0 = all).")] int maxTypes = 0,
        [Description("decompile: decompile again even when the store already holds this binary (MVID for managed, SHA-256 for native).")]
        bool force = false,
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
                    assemblyPath!, nameFilter, namespaceFilter, maxTypes, force, cancellationToken);

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
