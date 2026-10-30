using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Search;

namespace VisualMCP.Tools.CSharp.Search;

[McpServerToolType]
public static class StructuralSearchTool
{
    [McpServerTool(Name = "structural_search"), Description(
        "ReSharper's Structural Search and Replace. The pattern is ordinary C# with $name$ holes, matched " +
        "against the syntax tree (not the text): 'Console.WriteLine($argument$)' finds every call, " +
        "'if ($condition$) $body$;' finds every guard (a hole in statement position carries its semicolon), " +
        "'$x$ == null' every null test. With replacement the " +
        "matches are rewritten in place — the compiler gate refuses an edit that would add errors — and " +
        "without it the call returns where the pattern matches and what each hole bound. Use it INSTEAD OF " +
        "regex search when the intent is a code shape; regex still wins for text that is not code.")]
    public static Task<object> StructuralSearch(
        IStructuralSearchService svc,
        [Description("C# pattern with $name$ holes, e.g. 'if ($condition$) throw new Exception($message$);'")] string pattern,
        [Description("Optional: C# replacement with the same holes, e.g. 'if ($condition$) throw new ArgumentException($message$);'. Omit for a search.")] string? replacement = null,
        [Description("document | project | solution (default)")] string scope = "solution",
        [Description("For scope=document: the file to search")] string? filePath = null,
        [Description("For scope=project: the project to search")] string? projectName = null,
        [Description("Preview only (default true). Pass false to write the replacement.")] bool dryRun = true,
        [Description("Stop after this many matches; a replacement that hits more is refused rather than applied partially")] int maxResults = 200)
        => svc.Execute(pattern, replacement, scope, filePath, projectName, dryRun, maxResults);
}
