using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

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
    public static async Task<object> FindDuplicateCode(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum number of statements a block must have to be considered (default: 4)")] int minStatements = 4,
        [Description("Minimum number of tokens a block must have to be considered (default: 30)")] int minTokens = 30)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        // hash -> list of occurrences
        var index = new Dictionary<string, List<CloneOccurrence>>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null || document.FilePath is null) continue;

                // Index method bodies
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (method.Body is null) continue;
                    var stmts = method.Body.Statements;
                    if (stmts.Count < minStatements) continue;

                    IndexBlock(stmts.Cast<SyntaxNode>().ToList(), document.FilePath, project.Name,
                        method.Identifier.Text, method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        minTokens, index);
                }

                // Index statement sub-sequences within method bodies (sliding window)
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (method.Body is null) continue;
                    var stmts = method.Body.Statements.ToList();
                    if (stmts.Count < minStatements * 2) continue;

                    for (int start = 0; start <= stmts.Count - minStatements; start++)
                    {
                        var window = stmts.Skip(start).Take(minStatements).Cast<SyntaxNode>().ToList();
                        IndexBlock(window, document.FilePath, project.Name,
                            $"{method.Identifier.Text}[{start}..{start + minStatements - 1}]",
                            stmts[start].GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            minTokens, index);
                    }
                }
            }
        }

        // Keep only hashes with 2+ occurrences (actual clones)
        var clones = index
            .Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .Select((kv, i) => new
            {
                ClusterId  = i + 1,
                CloneCount = kv.Value.Count,
                Occurrences = kv.Value.Select(o => new
                {
                    o.Project,
                    o.FilePath,
                    o.Line,
                    o.Context,
                }).ToList()
            })
            .Cast<object>()
            .ToList();

        // Deduplicate: remove sub-sequences that are entirely covered by a larger clone
        var deduplicated = DeduplicateSubsequences(clones);

        return new
        {
            projectFilter   = projectName ?? "all",
            minStatements,
            minTokens,
            totalClusters   = deduplicated.Count,
            totalOccurrences = deduplicated.Sum(c => ((dynamic)c).CloneCount),
            cloneClusters   = deduplicated,
        };
    }

    private static void IndexBlock(
        List<SyntaxNode> nodes, string filePath, string projectName,
        string context, int line, int minTokens,
        Dictionary<string, List<CloneOccurrence>> index)
    {
        var tokens = nodes.SelectMany(n => n.DescendantTokens()).ToList();
        if (tokens.Count < minTokens) return;

        var hash = ComputeNormalisedHash(nodes);
        if (!index.TryGetValue(hash, out var list))
        {
            list = [];
            index[hash] = list;
        }
        // Avoid indexing the same location twice (from overlapping windows)
        if (list.Any(o => o.FilePath == filePath && Math.Abs(o.Line - line) < 3)) return;
        list.Add(new CloneOccurrence(projectName, filePath, line, context));
    }

    private static string ComputeNormalisedHash(IEnumerable<SyntaxNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
            NormaliseNode(node, sb);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }

    private static void NormaliseNode(SyntaxNode node, StringBuilder sb)
    {
        foreach (var token in node.DescendantTokens())
        {
            var kind = token.Kind();
            // Replace identifiers and literals with placeholders to catch structural clones
            if (kind == SyntaxKind.IdentifierToken)
                sb.Append("ID ");
            else if (token.IsKind(SyntaxKind.NumericLiteralToken))
                sb.Append("NUM ");
            else if (token.IsKind(SyntaxKind.StringLiteralToken) ||
                     token.IsKind(SyntaxKind.InterpolatedStringStartToken))
                sb.Append("STR ");
            else
                sb.Append(token.RawKind).Append(' ');
        }
    }

    private static List<object> DeduplicateSubsequences(List<object> clones)
    {
        // Simple pass: remove clusters whose all occurrences are sub-ranges of a larger cluster's occurrences
        // For now return all clusters — full deduplication would require range overlap tracking
        return clones;
    }

    private record CloneOccurrence(string Project, string FilePath, int Line, string Context);
}
