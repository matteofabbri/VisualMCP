using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The declaration-side operations of Rider's "Transform parameters" on a Visual Basic method:
/// make-optional, make-required and change-type. The two call-site rewrites (wrap and
/// replace-with-property) are refused here with the reason; the C# path keeps them.
/// </summary>
internal static partial class VbRefactoringTail
{
    /// <summary>Null when no VB method with that name exists, so the C# service can continue.</summary>
    public static async Task<object?> TransformParametersAsync(IRoslynWorkspace workspace, string methodName,
        string operation, string[] parameters, string? containingType, string? typeName, string? newType,
        string? defaultValue, Dictionary<string, string>? defaults, bool dryRun, CancellationToken cancellationToken)
    {
        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return null;

        var methods = (await SymbolFinder.FindSourceDeclarationsAsync(solution,
                name => name.Equals(methodName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Member))
            .OfType<IMethodSymbol>()
            .Where(method => VbRefactoringSupport.IsVisualBasic(method))
            .Where(method => containingType is null ||
                             string.Equals(method.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (methods.Count == 0) return null;
        if (methods.Count > 1)
            return new { error = $"Several Visual Basic methods named '{methodName}'; address one declaration at a time." };

        var method = methods[0];
        var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference?.SyntaxTree.FilePath is null)
            return new { error = $"'{methodName}' has no source declaration." };

        var document = VbRefactoringSupport.DocumentAt(solution, reference.SyntaxTree.FilePath);
        if (document is null) return new { error = $"Could not locate the document of '{methodName}'." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var statement = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Select(block => block.SubOrFunctionStatement)
            .FirstOrDefault(candidate => candidate.Span.Contains(reference.Span) ||
                                         reference.Span.Contains(candidate.Span))
            ?? root.DescendantNodes().OfType<MethodStatementSyntax>()
                .FirstOrDefault(candidate => candidate.Span.Contains(reference.Span) || reference.Span.Contains(candidate.Span));

        if (statement is null)
            return new { error = $"Could not locate the declaration of '{methodName}'." };

        var list = statement.ParameterList;
        if (list is null) return new { error = $"'{methodName}' has no parameter list." };

        var methodBlock = statement.Parent as MethodBlockSyntax;

        var selected = new List<ParameterSyntax>();
        foreach (var name in parameters.Distinct(StringComparer.Ordinal))
        {
            var parameter = list.Parameters.FirstOrDefault(candidate =>
                string.Equals(candidate.Identifier.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase));
            if (parameter is null) return new { error = $"'{name}' is not a parameter of '{methodName}'." };
            selected.Add(parameter);
        }

        if (selected.Count == 0) return new { error = "At least one parameter is required." };

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var edits = new List<TextEdit>();

        switch (operation)
        {
            case "make-optional":
                foreach (var parameter in selected)
                {
                    var value = (defaults is not null &&
                                 defaults.TryGetValue(parameter.Identifier.Identifier.ValueText, out var specific)
                            ? specific
                            : defaultValue) ?? "Nothing";

                    if (!parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.OptionalKeyword)))
                        edits.Add(new TextEdit(document, new TextSpan(parameter.SpanStart, 0), "Optional "));

                    if (parameter.Default?.Value is { } current)
                        edits.Add(new TextEdit(document, current.Span, value));
                    else
                        edits.Add(new TextEdit(document, new TextSpan(parameter.Span.End, 0), $" = {value}"));
                }

                break;

            case "make-required":
                foreach (var parameter in selected)
                {
                    var optional = parameter.Modifiers.FirstOrDefault(token => token.IsKind(SyntaxKind.OptionalKeyword));
                    if (!optional.IsKind(SyntaxKind.None))
                    {
                        var end = Math.Min(text.Length, optional.Span.End);
                        if (end < text.Length && text[end] == ' ') end++;
                        edits.Add(new TextEdit(document, TextSpan.FromBounds(optional.SpanStart, end), string.Empty));
                    }

                    if (parameter.Default is { } current)
                    {
                        var start = current.SpanStart;
                        while (start > 0 && text[start - 1] == ' ') start--;
                        edits.Add(new TextEdit(document, TextSpan.FromBounds(start, current.Span.End), string.Empty));
                    }
                }

                break;

            case "change-type":
                if (string.IsNullOrWhiteSpace(newType))
                    return new { error = "The 'change-type' operation needs newType." };

                foreach (var parameter in selected)
                {
                    if (parameter.AsClause is not SimpleAsClauseSyntax asClause)
                        return new { error = $"'{parameter.Identifier.Identifier.ValueText}' has no As <type> clause." };
                    edits.Add(new TextEdit(document, asClause.Type.Span, newType));
                }

                break;

            case "wrap":
                if (methodBlock is null) return new { error = "The method body is required for 'wrap'." };
                return await WrapParametersAsync(workspace, solution, document, model, method, methodBlock, list,
                    selected, typeName, dryRun, cancellationToken);

            case "replace-with-property":
                if (methodBlock is null) return new { error = "The method body is required for 'replace-with-property'." };
                return await ReplaceWithPropertyAsync(workspace, solution, document, model, method, methodBlock, list,
                    selected, dryRun, cancellationToken);

            default:
                return new
                {
                    error = $"Unknown operation '{operation}'. Use make-optional, make-required, change-type, wrap or replace-with-property.",
                };
        }

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken,
            $"the '{operation}' transformation");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "transform_parameters",
            method = method.ToDisplayString(),
            operation,
            parameters = selected.Select(parameter => parameter.Identifier.Identifier.ValueText).ToList(),
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    // ── wrap: the parameters become one parameter object ─────────────────────────
    private static async Task<object> WrapParametersAsync(IRoslynWorkspace workspace, Solution solution,
        Document document, SemanticModel model, IMethodSymbol method, MethodBlockSyntax methodBlock,
        ParameterListSyntax list, List<ParameterSyntax> selected, string? typeName, bool dryRun,
        CancellationToken cancellationToken)
    {
        var indices = selected.Select(parameter => list.Parameters.IndexOf(parameter)).OrderBy(index => index).ToList();
        for (var position = 1; position < indices.Count; position++)
            if (indices[position] != indices[position - 1] + 1)
                return new { error = "The wrapped parameters must be consecutive in the declaration." };

        var type = string.IsNullOrWhiteSpace(typeName) ? method.Name + "Args" : typeName!;
        if (!SyntaxFacts.IsValidIdentifier(type))
            return new { error = $"'{type}' is not a valid class name." };

        var owner = methodBlock.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (owner is null || model.GetDeclaredSymbol(owner.BlockStatement, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return new { error = "The method is not inside a named type." };

        if (ownerSymbol.ContainingType?.GetMembers(type).Any() == true ||
            ownerSymbol.ContainingNamespace.GetMembers(type).Any())
            return new { error = $"'{type}' already exists in this scope." };

        var properties = new List<(string Name, string TypeText)>();
        foreach (var index in indices)
        {
            var parameter = list.Parameters[index];
            var typeText = TypeTextOf(parameter.AsClause);
            if (string.IsNullOrWhiteSpace(typeText))
                return new { error = $"'{parameter.Identifier.Identifier.ValueText}' has no As <type> clause." };
            properties.Add((parameter.Identifier.Identifier.ValueText, typeText!));
        }

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var edits = new Dictionary<DocumentId, List<TextEdit>>();

        AddEdit(edits, document,
            TextSpan.FromBounds(selected[0].SpanStart, selected[^1].Span.End), $"args As {type}");

        foreach (var parameter in selected)
        {
            var symbol = model.GetDeclaredSymbol(parameter, cancellationToken);
            if (symbol is not IParameterSymbol parameterSymbol) continue;

            foreach (var reference in await SymbolFinder.FindReferencesAsync(parameterSymbol, solution, cancellationToken))
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (selected.Any(candidate => candidate.Span.Contains(location.Location.SourceSpan))) continue;
                    AddEdit(edits, location.Document, location.Location.SourceSpan,
                        $"args.{parameterSymbol.Name}");
                }
        }

        var callSites = 0;
        foreach (var reference in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (methodBlock.Span.Contains(location.Location.SourceSpan)) continue;

                var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                    return new
                    {
                        error = $"'{method.Name}' is used at {Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} in a way that is not " +
                                "a plain call; the parameter object cannot be built there.",
                    };

                if (invocation.ArgumentList.OpenParenToken.IsMissing || invocation.ArgumentList.CloseParenToken.IsMissing)
                    return new { error = "A call without parentheses cannot be rewritten into a parameter object." };
                if (invocation.ArgumentList.Arguments.Any(argument => argument.IsNamed))
                    return new { error = "A call with named arguments cannot be rewritten into a parameter object." };

                var arguments = invocation.ArgumentList.Arguments;
                if (indices[^1] >= arguments.Count)
                    return new { error = $"A call to '{method.Name}' omits one of the wrapped arguments." };

                var texts = indices.Select(index => arguments[index].ToString()).ToList();
                AddEdit(edits, location.Document,
                    TextSpan.FromBounds(arguments[indices[0]].SpanStart, arguments[indices[^1]].Span.End),
                    $"New {type}({string.Join(", ", texts)})");
                callSites++;
            }

        var outermost = owner.AncestorsAndSelf().OfType<TypeBlockSyntax>().Last();
        var indent = IndentationOf(text, outermost.SpanStart) + "    ";
        var builder = new System.Text.StringBuilder();
        builder.Append(newLine).Append(indent).Append("Public NotInheritable Class ").Append(type).Append(newLine);
        foreach (var (name, typeText) in properties)
            builder.Append(indent).Append("    Public Property ").Append(name).Append(" As ").Append(typeText).Append(newLine);

        builder.Append(indent).Append("    Public Sub New(")
            .Append(string.Join(", ", properties.Select(property => $"{property.Name} As {property.TypeText}")))
            .Append(')').Append(newLine);
        foreach (var (name, _) in properties)
            builder.Append(indent).Append("        Me.").Append(name).Append(" = ").Append(name).Append(newLine);
        builder.Append(indent).Append("    End Sub").Append(newLine).Append(indent).Append("End Class").Append(newLine);

        AddEdit(edits, document, new TextSpan(AfterEndOfType(text, outermost), 0), builder.ToString());

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(), dryRun,
            cancellationToken, "the wrap");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "transform_parameters",
            method = method.ToDisplayString(),
            operation = "wrap",
            parameterObject = type,
            parameters = properties.Select(property => property.Name).ToList(),
            callSites,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── replace-with-property: the parameter becomes a property of the receiver ──
    private static async Task<object> ReplaceWithPropertyAsync(IRoslynWorkspace workspace, Solution solution,
        Document document, SemanticModel model, IMethodSymbol method, MethodBlockSyntax methodBlock,
        ParameterListSyntax list, List<ParameterSyntax> selected, bool dryRun, CancellationToken cancellationToken)
    {
        if (selected.Count != 1) return new { error = "replace-with-property replaces one parameter at a time." };

        var parameter = selected[0];
        var index = list.Parameters.IndexOf(parameter);
        var name = parameter.Identifier.Identifier.ValueText;
        var typeText = TypeTextOf(parameter.AsClause);
        if (string.IsNullOrWhiteSpace(typeText))
            return new { error = $"'{name}' has no As <type> clause." };

        var statement = methodBlock.SubOrFunctionStatement;
        if (statement.Modifiers.Any(token => token.IsKind(SyntaxKind.SharedKeyword)))
            return new { error = "The method is Shared: there is no instance to hold the property." };

        var owner = methodBlock.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (owner is null || model.GetDeclaredSymbol(owner.BlockStatement, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return new { error = "The method is not inside a named type." };
        if (ownerSymbol.GetMembers(name).Length > 0)
            return new { error = $"'{ownerSymbol.Name}' already has a member named '{name}'." };

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        var escape = EscapeIdentifier(name);

        AddEdit(edits, document, ParameterRemoval(list.Parameters, index), string.Empty);
        AddEdit(edits, document, new TextSpan(BeforeEndOfType(text, owner), 0),
            $"{IndentationOf(text, owner.SpanStart)}    Public Property {escape} As {typeText}{newLine}");

        if (model.GetDeclaredSymbol(parameter, cancellationToken) is IParameterSymbol symbol)
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (parameter.Span.Contains(location.Location.SourceSpan)) continue;
                    AddEdit(edits, location.Document, location.Location.SourceSpan, $"Me.{escape}");
                }

        var callSites = 0;
        foreach (var reference in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (methodBlock.Span.Contains(location.Location.SourceSpan)) continue;

                var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                    return new { error = $"'{method.Name}' is used in a way that is not a plain call." };
                if (invocation.ArgumentList.OpenParenToken.IsMissing || invocation.ArgumentList.CloseParenToken.IsMissing)
                    return new { error = "A call without parentheses cannot receive the assignment." };
                if (invocation.ArgumentList.Arguments.Any(argument => argument.IsNamed))
                    return new { error = "A call with named arguments cannot receive the assignment." };

                var arguments = invocation.ArgumentList.Arguments;
                if (index >= arguments.Count)
                    return new { error = $"A call to '{method.Name}' omits '{name}'." };
                if (invocation.AncestorsAndSelf().OfType<LambdaExpressionSyntax>().Any())
                    return new { error = "The call is inside a lambda: the assignment would run before it." };

                var enclosing = invocation.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                if (enclosing is null)
                    return new { error = "The call is not inside a statement; there is nowhere to assign the property." };

                var receiver = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Expression,
                    _ => null,
                };

                if (receiver is null)
                {
                    var callOwner = invocation.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
                    var callOwnerSymbol = callOwner is null
                        ? null
                        : model.GetDeclaredSymbol(callOwner.BlockStatement, cancellationToken);
                    if (callOwnerSymbol is null || !SymbolEqualityComparer.Default.Equals(callOwnerSymbol, ownerSymbol))
                        return new { error = $"The call to '{method.Name}' has no receiver to assign the property on." };
                }
                else if (receiver is not (SimpleNameSyntax or MeExpressionSyntax))
                    return new { error = "The call's receiver is not a name: the assignment cannot be spelled." };

                var targetText = await location.Document.GetTextAsync(cancellationToken);
                var lineStart = targetText.Lines.GetLineFromPosition(
                    Math.Clamp(enclosing.SpanStart, 0, targetText.Length)).Start;
                var indentation = IndentationOf(targetText, enclosing.SpanStart);
                var receiverText = receiver?.ToString() ?? "Me";

                AddEdit(edits, location.Document, new TextSpan(lineStart, 0),
                    $"{indentation}{receiverText}.{escape} = {arguments[index]}{NewLineOf(targetText)}");
                AddEdit(edits, location.Document, ArgumentRemoval(invocation, index), string.Empty);
                callSites++;
            }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(), dryRun,
            cancellationToken, "the replacement");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "transform_parameters",
            method = method.ToDisplayString(),
            operation = "replace-with-property",
            property = name,
            propertyType = typeText,
            callSites,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }
}
