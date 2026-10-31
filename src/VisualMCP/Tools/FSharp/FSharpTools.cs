using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.FSharp;

namespace VisualMCP.Tools.FSharp;

[McpServerToolType]
public static class FSharpTools
{
    [McpServerTool(Name = "fsharp_files"), Description(
        "The declaration inventory F# has no symbol index for: every .fs/.fsi/.fsx file under the solution (or under " +
        "`path`), with its line count, the modules and namespaces it declares, its types, its top-level functions, " +
        "how many namespaces it opens, and the findings it carries. bin, obj, packages and .paket are skipped. An " +
        "indentation-aware scan, not a compiler: it lists what the text declares and resolves nothing. Read-only.")]
    public static Task<object> FSharpFiles(
        IFSharpService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500)
        => svc.InventoryAsync(path, maxFiles);

    [McpServerTool(Name = "analyze_fsharp"), Description(
        "Pattern-based F# diagnostics over the solution's .fs/.fsi/.fsx files. Rules: FS0001 a tab in the " +
        "indentation, FS0002 Option.get / Option.Value (throws on None), FS0003 List.head / Seq.head / List.item " +
        "and friends (throw on an empty collection), FS0004 Unchecked.defaultof / unbox, FS0005 comparison with " +
        "null, FS0006 a catch-all 'with _ ->' that swallows the exception, FS0007 a mutable binding, FS0008 " +
        "printf/printfn, FS0009 failwith, FS0010 an 'open' declared twice. Comments and string literals are masked " +
        "first. F# is not a Roslyn language, so this resolves no symbol and type-checks nothing — the compiler's " +
        "own FS#### diagnostics come from build_project. Read-only.")]
    public static Task<object> AnalyzeFSharp(
        IFSharpService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Minimum severity: info (default), warning or error.")] string minSeverity = "info",
        [Description("Only this rule id, e.g. FS0002.")] string? rule = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500,
        [Description("Maximum findings to return (default 200; the total is always reported).")] int maxFindings = 200)
        => svc.AnalyzeAsync(path, minSeverity, rule, maxFiles, maxFindings);
}
