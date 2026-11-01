using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The extension-method family and the anonymous-type conversion on a Visual Basic document. VB spells
/// an extension method <c>&lt;Extension&gt; Function M(Me receiver As T)</c> and an anonymous object
/// <c>New With {.Name = value}</c>; both shapes are rewritten in place and the compiler gate decides.
/// </summary>
internal static partial class VbRefactoringTail
{
    // ── static_to_extension ──────────────────────────────────────────────────
    public static async Task<object> StaticToExtensionAsync(IRoslynWorkspace workspace, string filePath,
        string methodName, string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, methodName,
                StringComparison.Ordinal))
            .Where(method => model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken) is IMethodSymbol
                { IsStatic: true })
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken)?
                                 .ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No Shared or Module-level method '{methodName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several Shared methods named '{methodName}'; address one declaration at a time." };

        var method = candidates[0];
        if (model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken) is not IMethodSymbol symbol)
            return new { error = $"'{methodName}' did not resolve to a method." };

        if (symbol.ContainingType?.TypeKind != TypeKind.Module)
            return new { error = "A Visual Basic extension method must be declared in a Module." };
        if (symbol.Parameters.Length == 0)
            return new { error = $"'{methodName}' has no parameter to become the receiver." };
        if (symbol.Parameters[0].RefKind != RefKind.None || symbol.Parameters[0].IsParams)
            return new { error = "The first parameter is ByRef or ParamArray: it cannot become the receiver." };
        if (method.SubOrFunctionStatement.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => string.Equals(attribute.Name.ToString(), "Extension", StringComparison.OrdinalIgnoreCase)))
            return new { error = $"'{methodName}' is already an extension method." };

        var text = await document.GetTextAsync(cancellationToken);
        var statement = method.SubOrFunctionStatement;
        var indentation = IndentationOf(text, statement.SpanStart);

        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        AddEdit(edits, document, new TextSpan(statement.SpanStart, 0),
            $"<Extension>{NewLineOf(text)}{indentation}");

        if (!root.DescendantNodes().OfType<ImportsStatementSyntax>()
                .SelectMany(import => import.ImportsClauses.OfType<SimpleImportsClauseSyntax>())
                .Any(clause => string.Equals(clause.Name.ToString(), "System.Runtime.CompilerServices", StringComparison.Ordinal)))
        {
            AddEdit(edits, document, new TextSpan(TopLevelImportInsertion((CompilationUnitSyntax)root, text), 0),
                $"Imports System.Runtime.CompilerServices{NewLineOf(text)}");
        }

        var callSites = 0;
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (statement.Span.Contains(location.Location.SourceSpan)) continue;

                var target = location.Document;
                var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
                var targetModel = await target.GetSemanticModelAsync(cancellationToken);
                if (targetRoot is null || targetModel is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node.AncestorsAndSelf().Any(ancestor => ancestor is XmlNodeSyntax)) continue;

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                    return new
                    {
                        error = $"'{methodName}' is used at {Path.GetFileName(target.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} in a way that is not a plain call " +
                                "(a delegate or an AddressOf): there is no receiver to move.",
                    };

                if (invocation.ArgumentList.Arguments.Count == 0)
                    return new { error = $"The call to '{methodName}' at {Path.GetFileName(target.FilePath)}:" +
                                         $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} passes no receiver." };
                if (invocation.ArgumentList.Arguments.Any(argument => argument.IsNamed))
                    return new { error = $"The call to '{methodName}' at {Path.GetFileName(target.FilePath)}:" +
                                         $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} uses a named argument; " +
                                         "moving the receiver past it would change what it binds to." };

                var callee = invocation.Expression;
                var receiverArgument = ExpressionOf(invocation.ArgumentList.Arguments[0]);
                if (receiverArgument is null)
                    return new { error = "The first argument is not an expression." };

                var name = callee switch
                {
                    MemberAccessExpressionSyntax access => access.Name.ToString(),
                    SimpleNameSyntax simple => simple.ToString(),
                    _ => methodName,
                };

                var receiverText = receiverArgument.ToString();
                if (receiverArgument is not (SimpleNameSyntax or InvocationExpressionSyntax or
                    MemberAccessExpressionSyntax or ParenthesizedExpressionSyntax))
                    receiverText = $"({receiverText})";

                AddEdit(edits, target, callee.Span, $"{receiverText}.{name}");
                AddEdit(edits, target, ArgumentRemoval(invocation, 0), string.Empty);
                callSites++;
            }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(),
            dryRun, cancellationToken, $"converting '{methodName}' to an extension method");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "static_to_extension",
            method = symbol.ToDisplayString(),
            receiver = symbol.Parameters[0].Type.ToDisplayString(),
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

    // ── extension_to_static ──────────────────────────────────────────────────
    public static async Task<object> ExtensionToStaticAsync(IRoslynWorkspace workspace, string filePath,
        string methodName, string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, methodName,
                StringComparison.Ordinal))
            .Where(method => IsExtensionMethod(method))
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken)?
                                 .ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No extension method '{methodName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several extension methods named '{methodName}'; address one declaration at a time." };

        var method = candidates[0];
        if (model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken) is not IMethodSymbol symbol ||
            symbol.ContainingType is not { } owner)
            return new { error = $"'{methodName}' did not resolve." };

        var text = await document.GetTextAsync(cancellationToken);
        var statement = method.SubOrFunctionStatement;
        var edits = new Dictionary<DocumentId, List<TextEdit>>();

        // The <Extension> attribute: its whole list when it is alone, otherwise just this attribute.
        foreach (var list in statement.AttributeLists)
        {
            var index = list.Attributes.IndexOf(attribute =>
                string.Equals(attribute.Name.ToString(), "Extension", StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;

            if (list.Attributes.Count == 1)
                AddEdit(edits, document, WholeLines(text, list.Span), string.Empty);
            else if (index < list.Attributes.Count - 1)
                AddEdit(edits, document, TextSpan.FromBounds(list.Attributes[index].Span.Start,
                    list.Attributes[index + 1].Span.Start), string.Empty);
            else
                AddEdit(edits, document, TextSpan.FromBounds(list.Attributes[index - 1].Span.End,
                    list.Attributes[index].Span.End), string.Empty);
        }

        var callSites = 0;
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (statement.Span.Contains(location.Location.SourceSpan)) continue;

                var target = location.Document;
                var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
                var targetModel = await target.GetSemanticModelAsync(cancellationToken);
                if (targetRoot is null || targetModel is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node.AncestorsAndSelf().Any(ancestor => ancestor is XmlNodeSyntax)) continue;

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                    return new
                    {
                        error = $"'{methodName}' is used at {Path.GetFileName(target.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} in a way that is not a plain call " +
                                "(a delegate or an AddressOf): there is no receiver to move into the arguments.",
                    };

                if (invocation.Expression is not MemberAccessExpressionSyntax access)
                    continue; // A bare call inside the module stays valid once the method is static.

                if (targetModel.GetSymbolInfo(access.Expression, cancellationToken).Symbol is INamedTypeSymbol)
                    continue; // Already the static spelling.

                var receiverText = access.Expression.ToString();
                var staticName = SpellingOf(owner, targetRoot);
                var open = invocation.ArgumentList.OpenParenToken.Span.End;
                var prefix = invocation.ArgumentList.Arguments.Count == 0 ? receiverText : receiverText + ", ";

                AddEdit(edits, target, access.Span, $"{staticName}.{access.Name}");
                AddEdit(edits, target, new TextSpan(open, 0), prefix);
                callSites++;
            }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(),
            dryRun, cancellationToken, $"converting '{methodName}' to a static method");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "extension_to_static",
            method = symbol.ToDisplayString(),
            type = owner.ToDisplayString(),
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

    // ── convert_anonymous_to_type ────────────────────────────────────────────
    public static async Task<object> ConvertAnonymousToTypeAsync(IRoslynWorkspace workspace, string filePath,
        int line, string? typeName, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var creations = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().ToList();
        var creation = creations.FirstOrDefault(candidate =>
        {
            var span = candidate.GetLocation().GetLineSpan();
            return line >= span.StartLinePosition.Line + 1 && line <= span.EndLinePosition.Line + 1;
        });

        if (creation is null)
            return new
            {
                error = $"No anonymous object creation on line {line} of {Path.GetFileName(filePath)}.",
                anonymousCreations = creations
                    .Select(candidate => candidate.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                    .ToList(),
            };

        var name = string.IsNullOrWhiteSpace(typeName) ? "AnonymousType" : typeName!;
        if (!SyntaxFacts.IsValidIdentifier(name))
            return new { error = $"'{name}' is not a valid Visual Basic identifier." };

        var enclosingType = creation.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (enclosingType is null)
            return new { error = "The creation is not inside a type declaration." };
        if (model.GetDeclaredSymbol(enclosingType.BlockStatement, cancellationToken) is not INamedTypeSymbol owner)
            return new { error = "The enclosing type did not resolve." };
        if ((owner.ContainingType is { } nested && nested.GetMembers(name).Any()) ||
            owner.ContainingNamespace.GetMembers(name).Any())
            return new { error = $"'{name}' already exists in the enclosing scope." };

        var initializer = creation.Initializer;
        if (initializer is null)
            return new { error = "The anonymous creation has no member initializer." };

        var members = new List<(string Name, ExpressionSyntax Value)>();
        foreach (var item in initializer.Initializers)
        {
            switch (item)
            {
                case NamedFieldInitializerSyntax named when named.Name is not null:
                    members.Add((named.Name.Identifier.ValueText, named.Expression));
                    break;
                case InferredFieldInitializerSyntax inferred:
                    var inferredName = inferred.Expression is MemberAccessExpressionSyntax access
                        ? access.Name.Identifier.ValueText
                        : inferred.Expression is IdentifierNameSyntax identifier
                            ? identifier.Identifier.ValueText
                            : null;
                    if (inferredName is null)
                        return new { error = $"The member '{inferred.Expression}' has no name to infer; name it with '.Name = …'." };
                    members.Add((inferredName, inferred.Expression));
                    break;
            }
        }

        if (members.Count == 0)
            return new { error = "The anonymous creation declares no member." };

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var namespaceBlock = creation.Ancestors().OfType<NamespaceBlockSyntax>().FirstOrDefault();
        var indentation = namespaceBlock is null ? string.Empty : "    ";
        var memberIndent = indentation + "    ";

        var parameterNames = new List<string>();
        var properties = new List<string>();
        var assignments = new List<string>();
        foreach (var (memberName, value) in members)
        {
            var type = model.GetTypeInfo(value, cancellationToken).Type;
            if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Unknown)
                return new { error = $"The member '{memberName}' has no resolvable type." };

            var typeText = type.ToMinimalDisplayString(model, value.SpanStart, VbTypeFormat);
            var parameterName = char.ToLowerInvariant(memberName[0]) + memberName[1..];
            if (string.Equals(parameterName, memberName, StringComparison.Ordinal))
                parameterName = "value" + memberName;

            parameterNames.Add($"{parameterName} As {typeText}");
            properties.Add($"{memberIndent}Public ReadOnly Property {memberName} As {typeText}");
            assignments.Add($"{memberIndent}    Me.{memberName} = {parameterName}");
        }

        var classText = new System.Text.StringBuilder();
        classText.Append(indentation).Append("Friend NotInheritable Class ").Append(name).Append(newLine);
        foreach (var property in properties) classText.Append(property).Append(newLine).Append(newLine);
        classText.Append(memberIndent).Append("Public Sub New(").Append(string.Join(", ", parameterNames)).Append(')').Append(newLine);
        foreach (var assignment in assignments) classText.Append(assignment).Append(newLine);
        classText.Append(memberIndent).Append("End Sub").Append(newLine);
        classText.Append(indentation).Append("End Class").Append(newLine);

        var insertion = namespaceBlock is not null
            ? text.Lines.GetLineFromPosition(Math.Clamp(namespaceBlock.EndNamespaceStatement.SpanStart, 0, text.Length)).Start
            : text.Length;

        var replacement = $"New {name}(" + string.Join(", ", members.Select(member => member.Value.ToString())) + ")";
        var edits = new List<TextEdit>
        {
            new(document, creation.Span, replacement),
            new(document, new TextSpan(insertion, 0), newLine + classText),
        };

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the conversion");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "convert_anonymous_to_type",
            type = name,
            members = members.Select(member => member.Name).ToList(),
            file = document.FilePath,
            line,
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

    private static bool IsExtensionMethod(MethodBlockSyntax method) =>
        method.SubOrFunctionStatement.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => string.Equals(attribute.Name.ToString(), "Extension", StringComparison.OrdinalIgnoreCase));
}
