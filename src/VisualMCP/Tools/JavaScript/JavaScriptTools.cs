using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.JavaScript;

namespace VisualMCP.Tools.JavaScript;

[McpServerToolType]
public static class JavaScriptTools
{
    [McpServerTool(Name = "js_files"), Description(
        "What the solution's JavaScript/TypeScript is: every .js/.jsx/.mjs/.cjs/.ts/.tsx file under the solution " +
        "(or under `path`), with its line count, whether it is TypeScript and JSX, the modules it imports, the " +
        "relative ones resolved to the file they point at, the module graph and its import cycles, the names it " +
        "exports, its function and class counts and how many findings it carries. node_modules, dist, build, out, " +
        "coverage and the other generated folders are skipped. Use it to find the source before reading it; " +
        "analyze_js lists the findings. Read-only.")]
    public static Task<object> JsFiles(
        IJavaScriptService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500)
        => svc.InventoryAsync(path, maxFiles);

    [McpServerTool(Name = "analyze_js"), Description(
        "Pattern-based JavaScript/TypeScript diagnostics over the solution's script files — the JS/TS half of " +
        "analyze_sql / analyze_razor_pages. Rules: JS0001 unterminated string/template/comment, JS0002 eval, " +
        "JS0003 new Function, JS0004 innerHTML/outerHTML assigned a non-literal, JS0005 dangerouslySetInnerHTML, " +
        "JS0006 document.write, JS0007 loose equality, JS0008 var, JS0009 console, JS0010 debugger, JS0011 empty " +
        "catch, JS0012 @ts-ignore/@ts-nocheck, JS0013 target=_blank without rel=noopener, JS0014 JSX <img> without " +
        "alt, JS0015 explicit any (TypeScript only), JS0016 setTimeout/setInterval given a string, JS0017 a " +
        "relative import that points at no file. It masks comments, strings and template literals first, so a " +
        "keyword inside them does not fire. A pattern scanner, not a parser: a relative import is resolved " +
        "against the file system, but a package specifier and a type are not. Read-only.")]
    public static Task<object> AnalyzeJs(
        IJavaScriptService svc,
        [Description("A file or a directory (default: the solution directory).")] string? path = null,
        [Description("Minimum severity: info (default), warning or error.")] string minSeverity = "info",
        [Description("Only this rule id, e.g. JS0007.")] string? rule = null,
        [Description("Maximum files to read (default 500).")] int maxFiles = 500,
        [Description("Maximum findings to return (default 200; the total is always reported).")] int maxFindings = 200)
        => svc.AnalyzeAsync(path, minSeverity, rule, maxFiles, maxFindings);

    [McpServerTool(Name = "js_symbols"), Description(
        "The symbol table of the solution's JavaScript/TypeScript: which declarations a file makes, which " +
        "declaration a name resolves to, every reference to it — inside its module and through the imports " +
        "other modules take from it — and the declarations nothing reads. Lexical, not a type model: a " +
        "tokeniser over the masked source plus one scope per brace block resolves a name to the declaration " +
        "in the innermost enclosing scope, and an imported name through its binding to the exporting module " +
        "(a relative specifier is resolved against the file system, a package one is not). JSX component " +
        "tags are references; a member reached through an untyped object, a computed member name and eval " +
        "are not resolved. action=outline (needs file), find (name), refs (name, optional file) or unused. " +
        "Read-only.")]
    public static Task<object> JsSymbols(
        IJavaScriptSymbolService svc,
        [Description("outline | find | refs | unused")] string action = "find",
        [Description("The name to find or follow (find and refs).")] string? name = null,
        [Description("The declaring file, for outline and to disambiguate refs.")] string? file = null,
        [Description("A file or a directory to scan (default: the solution directory).")] string? path = null,
        [Description("Match the name as a substring instead of exactly (find).")] bool partial = false,
        [Description("Maximum rows to return (default 200).")] int maxResults = 200)
        => svc.SymbolsAsync(action, name, file, path, partial, maxResults);
}
