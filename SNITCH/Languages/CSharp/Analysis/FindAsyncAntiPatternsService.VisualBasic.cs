using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The async anti-patterns in Visual Basic: blocking on a task (<c>.Result</c>, <c>.Wait()</c>,
/// <c>.GetAwaiter().GetResult()</c>), <c>Async Sub</c> outside an event handler, <c>Await</c> in a
/// loop, <c>Task.Run</c> around a synchronous lambda, and a missing <c>ConfigureAwait(false)</c>.
/// </summary>
public sealed partial class FindAsyncAntiPatternsService
{
    private static void CollectVisualBasic(
        Document document, Project project, SyntaxNode root, SemanticModel model, List<object> findings,
        bool checkConfigureAwait)
    {
        // ── .Result / .Wait() / .GetAwaiter().GetResult() blocking ────────────
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = access.Name.Identifier.Text;
            if (name is not ("Result" or "Wait" or "GetResult")) continue;

            var receiver = model.GetTypeInfo(access.Expression).Type;
            if (receiver is null || !IsTaskType(receiver)) continue;

            var antiPattern = name switch
            {
                "Result" => ".Result blocking call",
                "Wait" => ".Wait() blocking call",
                _ => ".GetAwaiter().GetResult() blocking call",
            };

            Report(antiPattern, access, document, project, findings,
                $"Blocking on async code with {name} can cause deadlocks in sync contexts. Use Await instead.");
        }

        // ── Async Sub outside an event handler ────────────────────────────────
        foreach (var method in root.DescendantNodes().OfType<MethodBlockSyntax>())
        {
            var statement = method.SubOrFunctionStatement;
            if (!statement.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
            if (!statement.SubOrFunctionKeyword.IsKind(SyntaxKind.SubKeyword)) continue;

            var symbol = model.GetDeclaredSymbol(method);
            if (symbol is null || IsEventHandler(symbol)) continue;

            Report("async void", method, document, project, findings,
                "Async Sub swallows exceptions. Use Async Function … As Task instead.");
        }

        // ── Await inside a loop ───────────────────────────────────────────────
        foreach (var awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            var loop = awaitExpression.Ancestors().FirstOrDefault(ancestor => ancestor
                is ForBlockSyntax or ForEachBlockSyntax or WhileBlockSyntax or DoLoopBlockSyntax);

            if (loop is null) continue;

            // A lambda inside the loop is its own execution: awaiting there is not a sequential loop wait.
            var interveningLambda = awaitExpression.Ancestors()
                .TakeWhile(ancestor => ancestor != loop)
                .Any(ancestor => ancestor is LambdaExpressionSyntax);

            if (interveningLambda) continue;

            var loopKind = loop switch
            {
                ForBlockSyntax => "for",
                ForEachBlockSyntax => "foreach",
                WhileBlockSyntax => "while",
                DoLoopBlockSyntax => "do-loop",
                _ => "loop",
            };

            Report($"await in {loopKind} loop", awaitExpression, document, project, findings,
                "Sequential awaits in loops can be slow. Consider batching with Task.WhenAll or refactoring.");
        }

        // ── Task.Run wrapping synchronous code ────────────────────────────────
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
            if (access.Name.Identifier.Text != "Run") continue;

            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol) continue;
            if (symbol.ContainingType?.Name != "Task") continue;

            if (invocation.ArgumentList?.Arguments.FirstOrDefault() is not SimpleArgumentSyntax argument) continue;
            if (argument.Expression is not LambdaExpressionSyntax lambda) continue;
            if (IsAsyncLambda(lambda)) continue;

            Report("Task.Run with sync lambda", invocation, document, project, findings,
                "Task.Run offloads sync work to thread pool. If the work is truly CPU-bound this is fine; " +
                "if it wraps blocking I/O, use async I/O instead.");
        }

        // ── missing ConfigureAwait(false) ─────────────────────────────────────
        if (!checkConfigureAwait) return;

        foreach (var awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            if (awaitExpression.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax access &&
                access.Name.Identifier.Text == "ConfigureAwait")
                continue;

            var awaitedType = model.GetTypeInfo(awaitExpression.Expression).Type;
            if (awaitedType is null || !IsTaskType(awaitedType)) continue;

            Report("missing ConfigureAwait(false)", awaitExpression, document, project, findings,
                "Library code should use .ConfigureAwait(false) to avoid capturing the synchronization context.");
        }
    }

    /// <summary>Whether a VB lambda is declared <c>Async</c>; a single-line lambda cannot be.</summary>
    private static bool IsAsyncLambda(LambdaExpressionSyntax lambda) =>
        lambda.SubOrFunctionHeader?.Modifiers.Any(SyntaxKind.AsyncKeyword) == true;
}
