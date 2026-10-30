using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Style;

namespace VisualMCP.Tools.CSharp.Style;

[McpServerToolType]
public static class SimplifyCodeTool
{
    [McpServerTool(Name = "simplify_code"), Description(
        "Apply the IDE's mechanical style suggestions — the quick-fix popup equivalents — at the scope you pick. " +
        "Supported rules: IDE0001 simplify name; IDE0002 simplify member access; IDE0003 remove 'this.'; IDE0004 remove " +
        "unnecessary cast; IDE0011 braces (only when the .editorconfig sets csharp_prefer_braces); IDE0016 guard → ?? throw; " +
        "IDE0017 object initializer; IDE0018 inline out variable; IDE0019 as + null check → pattern; " +
        "IDE0020/IDE0038 is + cast → pattern; IDE0021/IDE0022/IDE0023/IDE0024/IDE0025/IDE0026/IDE0027/IDE0053/IDE0061 " +
        "expression-bodied constructors, methods, operators, properties, indexers, accessors, lambdas and local functions; " +
        "IDE0028/IDE0300/IDE0301 collection expressions (new List<T>() → [], new T[] { a, b } → [a, b], " +
        "new T[0] → []); IDE0029/IDE0030/IDE0074 null checks → ?? / ??=; IDE0031 null propagation → ?.; IDE0033 " +
        "tuple.Item1 → tuple.Name; IDE0034 default(T) → default; IDE0035 remove unreachable code; " +
        "IDE0036 order modifiers (csharp_preferred_modifier_order); " +
        "IDE0037 inferred member names; IDE0040 accessibility modifiers " +
        "(dotnet_style_require_accessibility_modifiers); IDE0041 == null → is null; " +
        "IDE0042 deconstruction from a tuple; IDE0044 add readonly to fields; IDE0045 if/else assignments → ?:; " +
        "IDE0046 if/else returns → ?:; IDE0047 remove " +
        "parentheses; IDE0049 Int32 → int; IDE0051 remove a private member nothing in the solution references; " +
        "IDE0054 compound assignment; IDE0052 remove a private field that is written but never read; " +
        "IDE0058 unused expression value → _ = expr; IDE0059 remove a store overwritten before it is read; " +
        "IDE0060 remove an unused parameter and the argument at every call site (edits across files); " +
        "IDE0055 format the file (Roslyn formatter with the project's .editorconfig; opt-in, not part of rules=all); " +
        "IDE0056 index operator → ^; IDE0057 Substring → range; IDE0062 static local function when it captures nothing; " +
        "IDE0063 using block → using declaration; " +
        "IDE0066 switch statement → switch expression; IDE0070 hand-written GetHashCode → System.HashCode.Combine; IDE0071 simplify " +
        "interpolation; IDE0075 x ? true : false → x; " +
        "IDE0082 typeof(X).Name → nameof(X); IDE0083 !(x is T) → x is not T; IDE0080 remove a null-forgiving ! the flow " +
        "analysis proves unnecessary; IDE0090 new Foo() → new(); IDE0100 " +
        "x == true → x; IDE0120 xs.Where(p).Any() → xs.Any(p); IDE0121 Where(o => o is T).Cast<T>() → OfType<T>(); " +
        "IDE0150 x is object → x != null; IDE0160/IDE0161 namespace form (csharp_style_namespace_declarations); " +
        "IDE0170 extended property pattern; IDE0180 tuple swap; IDE0200 " +
        "unnecessary lambda → method group; IDE0220 explicit cast in foreach " +
        "(dotnet_style_prefer_foreach_explicit_cast_in_source); IDE0230 UTF-8 string literal; IDE0240/IDE0241 remove a " +
        "#nullable directive that changes nothing; IDE0250/IDE0251 readonly struct and member; IDE0260 " +
        "(o as T)?.M == v → o is T { M: v }; IDE0280 " +
        "ArgumentNullException(\"x\") → nameof(x); IDE0302 stackalloc → collection expression; IDE0320 static lambda when it " +
        "captures nothing; IDE0340 nameof(List<int>) → nameof(List<>); IDE0380 remove an unnecessary unsafe modifier; " +
        "IDE0010/IDE0072 complete a switch over an enum when it has no default (IDE0010) or has a discard arm (IDE0072). " +
        "Every rewrite is verified by the compiler: a file whose change would add " +
        "errors is reported under 'refused' and left untouched, and the nodes a rewrite produces are reformatted by " +
        "Roslyn's formatter so the file keeps its indentation. The option-driven rules (IDE0011, IDE0036, IDE0160, " +
        "IDE0161) read the project's .editorconfig and do nothing while the option is unset; styleOptions in the " +
        "result reports what was found. Every write is recorded in the solution's edit history (edit_history). " +
        "Rules it cannot rewrite (formatting, naming, layout, or " +
        "ones that would change behaviour) are answered with the reason instead of being ignored. dryRun defaults to " +
        "true and returns a before/after pair per edit plus a line diff; pass dryRun=false to write. " +
        "IDE0005 (unused usings), IDE0032 (use an auto-property) and IDE0290 (primary constructor) are accepted too: " +
        "this tool delegates them to optimize_usings, property_to_auto and convert_to_primary_constructor, and the " +
        "result carries one report per id under 'delegated' (the style half, if any, stays under 'style'). They are " +
        "not part of rules=all — ask for them by id.")]
    public static Task<object> SimplifyCode(
        ISimplifyCodeService simplify,
        [Description("document (needs filePath) | project (needs projectName) | solution")] string scope = "solution",
        [Description("File to simplify, for scope=document (absolute or relative).")] string? filePath = null,
        [Description("Project to simplify, for scope=project.")] string? projectName = null,
        [Description("Rule ids to apply, e.g. 'IDE0028,IDE0041', or omit for all supported.")] string? rules = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => simplify.SimplifyAsync(scope, filePath, projectName, rules, dryRun);
}
