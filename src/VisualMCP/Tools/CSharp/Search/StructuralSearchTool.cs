using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Search;

namespace VisualMCP.Tools.CSharp.Search;

[McpServerToolType]
public static class StructuralSearchTool
{
    [McpServerTool(Name = "structural_search"), Description(
        "ReSharper's Structural Search and Replace, in C# or Visual Basic. The pattern is ordinary code " +
        "with $name$ holes, matched against the syntax tree (not the text): 'Console.WriteLine($argument$)' " +
        "finds every call, 'if ($condition$) $body$;' finds every guard in C# (a hole in statement position " +
        "carries its semicolon; in VB the same shape is 'If $condition$ Then' followed by the body on the " +
        "next line), '$x$ == null' / '$x$ Is Nothing' every null test. With replacement the " +
        "matches are rewritten in place — the compiler gate refuses an edit that would add errors — and " +
        "without it the call returns where the pattern matches and what each hole bound. The pattern is " +
        "read with the language of the documents it searches; language=auto tries each language in scope " +
        "and reports which ones parsed it. Use it INSTEAD OF regex search when the intent is a code shape; " +
        "regex still wins for text that is not code.")]
    public static Task<object> StructuralSearch(
        IStructuralSearchService svc,
        [Description("Pattern with $name$ holes, in the searched language, e.g. 'if ($condition$) throw new Exception($message$);'")] string pattern,
        [Description("Optional: replacement with the same holes, in the same language. Omit for a search.")] string? replacement = null,
        [Description("document | project | solution (default)")] string scope = "solution",
        [Description("For scope=document: the file to search")] string? filePath = null,
        [Description("For scope=project: the project to search")] string? projectName = null,
        [Description("Preview only (default true). Pass false to write the replacement.")] bool dryRun = true,
        [Description("Stop after this many matches; a replacement that hits more is refused rather than applied partially")] int maxResults = 200,
        [Description("auto (default) | cs | vb — the language the pattern is written in")] string? language = null)
        => svc.Execute(pattern, replacement, scope, filePath, projectName, dryRun, maxResults, language);
}
