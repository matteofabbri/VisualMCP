using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindDuplicateCodeTool
{
    [McpServerTool, Description(
        "Find duplicate or near-duplicate code blocks (clone detection) across the solution using " +
        "syntax-tree hashing. Normalises identifier names and literal values so structural clones " +
        "are detected even when variable names differ. Reports cloned method bodies, statement " +
        "sequences, and class-level blocks grouped by clone cluster. " +
        "Do NOT search for duplicates yourself — text search misses structural clones and is " +
        "unreliable across formatting differences. Requires load_solution first.")]
    public static Task<object> FindDuplicateCode(
        IFindDuplicateCodeService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum number of statements a block must have to be considered (default: 4)")] int minStatements = 4,
        [Description("Minimum number of tokens a block must have to be considered (default: 30)")] int minTokens = 30)
        => svc.FindDuplicateCode(projectName, minStatements, minTokens);
}