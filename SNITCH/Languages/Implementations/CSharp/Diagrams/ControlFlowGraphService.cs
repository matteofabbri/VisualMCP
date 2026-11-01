using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Diagrams;

/// <summary>
/// The control-flow graph of one method body, as Mermaid or DOT: Roslyn's basic blocks with their branch
/// conditions and their try/catch regions. The IDE draws this graph; the server produces its data.
/// </summary>
public interface IControlFlowGraphService
{
    Task<object> ExportAsync(string? filePath, int line, string? methodName, string? containingType, string format);
}

public sealed class ControlFlowGraphService : IControlFlowGraphService
{
    private readonly IRoslynWorkspace _workspace;
    public ControlFlowGraphService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ExportAsync(
        string? filePath = null, int line = 0, string? methodName = null, string? containingType = null,
        string format = "mermaid")
    {
        var kind = (format ?? "mermaid").Trim().ToLowerInvariant();
        if (kind is not ("mermaid" or "dot")) return new { error = "format must be mermaid or dot." };

        var (document, declaration, error) = await ResolveAsync(filePath, line, methodName, containingType);
        if (error is not null) return new { error };
        if (document is null || declaration is null) return new { error = "The method was not found." };

        var model = await document.GetSemanticModelAsync();
        if (model is null) return new { error = "No semantic model for the document." };

        if (model.GetOperation(declaration) is not IMethodBodyOperation body)
            return new
            {
                error = "This declaration has no method body to analyse.",
                hint = "Pass a method or a constructor; a property accessor or a field initializer has no " +
                       "IMethodBodyOperation of its own.",
            };

        ControlFlowGraph graph;
        try { graph = ControlFlowGraph.Create(body); }
        catch (Exception exception) { return new { error = $"The control-flow graph could not be built: {exception.Message}" }; }

        var blocks = graph.Blocks;
        var labels = new Dictionary<BasicBlock, string>();
        for (var index = 0; index < blocks.Length; index++) labels[blocks[index]] = "B" + index;

        var builder = new StringBuilder();
        var edges = 0;

        if (kind == "mermaid")
        {
            builder.AppendLine("flowchart TD");
            for (var index = 0; index < blocks.Length; index++)
                builder.Append("  B").Append(index).Append("[\"").Append(Escape(Label(blocks[index]))).AppendLine("\"]");

            foreach (var block in blocks)
            {
                var from = labels[block];
                if (block.FallThroughSuccessor?.Destination is { } fallThrough)
                {
                    builder.Append("  ").Append(from).Append(" --> ").AppendLine(labels[fallThrough]);
                    edges++;
                }

                if (block.ConditionalSuccessor?.Destination is { } conditional)
                {
                    var condition = block.BranchValue?.Syntax.ToString() ?? "otherwise";
                    builder.Append("  ").Append(from).Append(" -- \"").Append(Escape(Trim(condition, 60)))
                        .Append("\" --> ").AppendLine(labels[conditional]);
                    edges++;
                }
            }
        }
        else
        {
            builder.AppendLine("digraph cfg {");
            builder.AppendLine("  rankdir=TB;");
            builder.AppendLine("  node [shape=box, fontname=\"Segoe UI\", fontsize=10];");
            for (var index = 0; index < blocks.Length; index++)
                builder.Append("  B").Append(index).Append(" [label=\"").Append(Escape(Label(blocks[index]))).AppendLine("\"];");

            foreach (var block in blocks)
            {
                var from = labels[block];
                if (block.FallThroughSuccessor?.Destination is { } fallThrough)
                {
                    builder.Append("  ").Append(from).Append(" -> ").AppendLine(labels[fallThrough] + ";");
                    edges++;
                }

                if (block.ConditionalSuccessor?.Destination is { } conditional)
                {
                    var condition = block.BranchValue?.Syntax.ToString() ?? "otherwise";
                    builder.Append("  ").Append(from).Append(" -> ").Append(labels[conditional])
                        .Append(" [label=\"").Append(Escape(Trim(condition, 60))).AppendLine("\"];");
                    edges++;
                }
            }

            builder.Append('}');
        }

        return new
        {
            tool = "control_flow_graph",
            method = (model.GetDeclaredSymbol(declaration) as IMethodSymbol)?.ToDisplayString() ?? "method",
            format = kind,
            blockCount = blocks.Length,
            edgeCount = edges,
            regions = blocks.Select(block => block.EnclosingRegion.Kind.ToString()).Where(value => value != "Root")
                .Distinct().ToList(),
            graph = builder.ToString().TrimEnd(),
            note = "Basic blocks are numbered in the order the compiler produced them; a label shows the block's " +
                   "kind, its region when it is not the root, and its last operations.",
        };
    }

    private static string Label(BasicBlock block)
    {
        var parts = new List<string>();
        if (block.Kind != BasicBlockKind.Block) parts.Add(block.Kind.ToString());
        if (block.EnclosingRegion.Kind != ControlFlowRegionKind.Root) parts.Add(block.EnclosingRegion.Kind.ToString());

        var operations = block.Operations
            .Select(operation => operation.Syntax.ToString())
            .Where(text => text.Length > 0)
            .TakeLast(2)
            .ToList();
        if (operations.Count > 0) parts.Add(Trim(string.Join("; ", operations), 90));

        return parts.Count == 0 ? "block" : string.Join(" | ", parts);
    }

    private static string Trim(string text, int length)
    {
        var single = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return single.Length <= length ? single : single[..(length - 1)] + "…";
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<(Document? Document, BaseMethodDeclarationSyntax? Declaration, string? Error)> ResolveAsync(
        string? filePath, int line, string? methodName, string? containingType)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return (null, null, "No C# solution could be auto-loaded from the working directory.");

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var full = Path.GetFullPath(filePath);
            var document = solution.Projects.SelectMany(project => project.Documents)
                .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                             string.Equals(Path.GetFullPath(candidate.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (document is null) return (null, null, $"{full} is not part of the loaded solution.");

            var root = await document.GetSyntaxRootAsync();
            if (root is null) return (null, null, "The document has no syntax root.");
            if (line < 1) return (null, null, "A 1-based line is required (or pass methodName).");

            var text = await document.GetTextAsync();
            if (line > text.Lines.Count) return (null, null, $"Line {line} is out of range.");

            var position = text.Lines[line - 1].Start;
            var declaration = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
                .Where(candidate => candidate.FullSpan.Contains(position))
                .OrderBy(candidate => candidate.Span.Length)
                .FirstOrDefault();

            return declaration is null
                ? (document, null, $"Line {line} is not inside a method or constructor.")
                : (document, declaration, null);
        }

        if (string.IsNullOrWhiteSpace(methodName)) return (null, null, "Pass filePath+line, or methodName.");

        var name = methodName.Trim();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var symbol in compilation.GetSymbolsWithName(name, SymbolFilter.Member))
            {
                if (symbol is not IMethodSymbol method) continue;
                if (containingType is not null &&
                    !(method.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;

                var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
                if (reference is null) continue;
                var declaration = await reference.GetSyntaxAsync() as BaseMethodDeclarationSyntax;
                if (declaration is null) continue;

                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null) continue;
                return (document, declaration, null);
            }
        }

        return (null, null, $"No method named '{name}' was found in the solution.");
    }
}
