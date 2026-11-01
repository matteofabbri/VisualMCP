using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IFindDuplicateCodeService
{
    Task<object> FindDuplicateCode(string? projectName, int minStatements, int minTokens);
}

/// <summary>
/// Clone detection over statement blocks: a method body and every sliding window of it are hashed with
/// identifiers and literals replaced by placeholders, so two blocks that differ only in names and
/// values land on the same hash. A hash with two or more occurrences is a clone cluster.
/// <para>
/// The blocks come from C# method declarations and from VB method, constructor and operator blocks;
/// the walk is the same one, the token classification is the only per-language part.
/// </para>
/// </summary>
public sealed partial class FindDuplicateCodeService : IFindDuplicateCodeService
{
    private readonly IRoslynWorkspace _workspace;
    public FindDuplicateCodeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindDuplicateCode(
        string? projectName = null,
        int minStatements = 4,
        int minTokens = 30)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
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

                foreach (var body in MethodBodies(root))
                {
                    // Index the whole body
                    if (body.Statements.Count >= minStatements)
                    {
                        IndexBlock(body.Statements.Take(body.Statements.Count).ToList(), document.FilePath, project.Name,
                            body.Name, body.Line, minTokens, index);
                    }

                    // Index statement sub-sequences within the body (sliding window)
                    if (body.Statements.Count < minStatements * 2) continue;

                    for (var start = 0; start <= body.Statements.Count - minStatements; start++)
                    {
                        var window = body.Statements.Skip(start).Take(minStatements).ToList();
                        IndexBlock(window, document.FilePath, project.Name,
                            $"{body.Name}[{start}..{start + minStatements - 1}]",
                            LineOf(window[0]), minTokens, index);
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

    /// <summary>One indexable body: its statements, the name it is reported under and its first line.</summary>
    private sealed record MethodBody(IReadOnlyList<SyntaxNode> Statements, string Name, int Line);

    private static IEnumerable<MethodBody> MethodBodies(SyntaxNode root) =>
        root.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicMethodBodies(root)
            : root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Body is not null)
                .Select(method => new MethodBody(
                    method.Body!.Statements.Cast<SyntaxNode>().ToList(),
                    method.Identifier.Text,
                    LineOf(method)));

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

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
            // Replace identifiers and literals with placeholders to catch structural clones
            if (IsIdentifierToken(token))
                sb.Append("ID ");
            else if (IsNumericLiteralToken(token))
                sb.Append("NUM ");
            else if (IsStringLiteralToken(token))
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
