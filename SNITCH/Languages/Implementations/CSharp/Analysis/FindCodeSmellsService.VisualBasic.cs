using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// Code smells in Visual Basic: the same list the C# half reports — async void, an empty catch,
/// <c>Await</c> inside a lock, a long or deeply nested method, too many parameters, a class with too
/// many public methods — read from VB's block syntax. VB spells the async-and-void shape as
/// <c>Async Sub</c>, and its lock as <c>SyncLock</c>.
/// </summary>
public sealed partial class FindCodeSmellsService
{
    private static void CollectVisualBasic(
        Document document, Project project, SyntaxNode root, SemanticModel model, List<object> smells,
        int maxMethodLines, int maxParams, int maxNesting, int maxPublicMethods)
    {
        // ── async void ────────────────────────────────────────────────────────
        foreach (var method in root.DescendantNodes().OfType<MethodBlockSyntax>())
        {
            var statement = method.SubOrFunctionStatement;
            if (!statement.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
            if (!statement.SubOrFunctionKeyword.IsKind(SyntaxKind.SubKeyword)) continue;

            var symbol = model.GetDeclaredSymbol(method);
            if (symbol is not null && IsEventHandler(symbol)) continue;

            Report("async void", method, document, project, smells,
                "Async Sub methods swallow exceptions; use Async Function … As Task instead.");
        }

        // ── empty catch ───────────────────────────────────────────────────────
        foreach (var @catch in root.DescendantNodes().OfType<CatchBlockSyntax>())
        {
            if (@catch.Statements.Count == 0)
                Report("empty catch", @catch, document, project, smells,
                    "Empty catch silently swallows exceptions.");
        }

        // ── await inside SyncLock ─────────────────────────────────────────────
        foreach (var @lock in root.DescendantNodes().OfType<SyncLockBlockSyntax>())
        {
            if (@lock.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                Report("await in lock", @lock, document, project, smells,
                    "Await inside SyncLock can cause deadlocks; use SemaphoreSlim instead.");
        }

        // ── long method / too many params / deep nesting ──────────────────────
        foreach (var node in root.DescendantNodes())
        {
            if (!TryDescribeMethod(node, out var parameters)) continue;

            var span = node.GetLocation().GetLineSpan();
            var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            var depth = NestingDepth(node);

            if (lines > maxMethodLines)
                Report("long method", node, document, project, smells,
                    $"Method has {lines} lines (threshold: {maxMethodLines}).");

            if (parameters > maxParams)
                Report("too many parameters", node, document, project, smells,
                    $"Method has {parameters} parameters (threshold: {maxParams}).");

            if (depth > maxNesting)
                Report("deep nesting", node, document, project, smells,
                    $"Method reaches nesting depth {depth} (threshold: {maxNesting}).");
        }

        // ── large class ───────────────────────────────────────────────────────
        foreach (var type in root.DescendantNodes().OfType<TypeBlockSyntax>())
        {
            if (model.GetDeclaredSymbol(type) is not INamedTypeSymbol symbol) continue;

            var publicMethods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Count(method => method.DeclaredAccessibility == Accessibility.Public && !method.IsImplicitlyDeclared);

            if (publicMethods > maxPublicMethods)
                Report("large class", type, document, project, smells,
                    $"Class has {publicMethods} public methods (threshold: {maxPublicMethods}).");
        }
    }

    private static bool TryDescribeMethod(SyntaxNode node, out int parameters)
    {
        switch (node)
        {
            case MethodBlockSyntax method:
                parameters = method.SubOrFunctionStatement.ParameterList?.Parameters.Count ?? 0;
                return true;
            case ConstructorBlockSyntax constructor:
                parameters = constructor.SubNewStatement.ParameterList?.Parameters.Count ?? 0;
                return true;
            case OperatorBlockSyntax @operator:
                parameters = @operator.OperatorStatement.ParameterList?.Parameters.Count ?? 0;
                return true;
            default:
                parameters = 0;
                return false;
        }
    }

    /// <summary>How deep the block statements nest inside one method body.</summary>
    private static int NestingDepth(SyntaxNode method)
    {
        var max = 0;
        Walk(method, 0);
        return max;

        void Walk(SyntaxNode node, int depth)
        {
            if (depth > max) max = depth;

            foreach (var child in node.ChildNodes())
                Walk(child, IsNestingBlock(child) ? depth + 1 : depth);
        }

        static bool IsNestingBlock(SyntaxNode node) => node
            is MultiLineIfBlockSyntax or ElseIfBlockSyntax or ElseBlockSyntax
            or SingleLineIfStatementSyntax or SingleLineElseClauseSyntax
            or ForOrForEachBlockSyntax or WhileBlockSyntax or DoLoopBlockSyntax
            or UsingBlockSyntax or SyncLockBlockSyntax or WithBlockSyntax
            or TryBlockSyntax or CatchBlockSyntax or FinallyBlockSyntax
            or SelectBlockSyntax or CaseBlockSyntax;
    }
}
