using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The statement- and declaration-shape refactorings that used to stop at a C# syntax tree: a local, a
/// field, a parameter, a part of a string, a negated Boolean and a copied type. Every operation resolves
/// the target through the VB semantic model, replaces the exact spans it can prove and asks the compiler
/// gate before writing; a shape the model cannot prove is refused with the reason.
/// </summary>
internal static partial class VbRefactoringTail
{
    private static readonly SymbolDisplayFormat VbTypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    // ── introduce_variable ───────────────────────────────────────────────────
    public static async Task<object> IntroduceVariableAsync(IRoslynWorkspace workspace, string filePath, int line,
        string expressionText, string variableName, bool dryRun, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(variableName))
            return new { error = $"'{variableName}' is not a valid Visual Basic identifier." };
        if (string.IsNullOrWhiteSpace(expressionText))
            return new { error = "The exact expression text is required." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count) return new { error = $"Line {line} is out of range." };

        var textLine = text.Lines[line - 1];
        var expression = ExpressionOnLine(root, textLine, expressionText);
        if (expression is null)
            return new { error = $"Expression '{expressionText}' was not found as a single expression on line {line}." };

        if (model.GetTypeInfo(expression, cancellationToken).Type is not { } type ||
            type.TypeKind is TypeKind.Error or TypeKind.Unknown)
            return new { error = "The expression has no resolvable type; nothing to declare." };
        if (type.SpecialType == SpecialType.System_Void)
            return new { error = "A void expression cannot be stored in a local." };

        var statement = expression.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is null)
            return new { error = "The expression is not inside a statement." };

        var container = expression.Ancestors().OfType<MethodBlockSyntax>().FirstOrDefault()
                        ?? (SyntaxNode?)expression.Ancestors().OfType<AccessorBlockSyntax>().FirstOrDefault()
                        ?? expression.Ancestors().OfType<PropertyBlockSyntax>().FirstOrDefault();
        if (container is null)
            return new { error = "The expression is not inside a method or property accessor; a local would have no scope." };

        var occurrences = OutermostOccurrences(container, expressionText);
        if (occurrences.Count == 0)
            return new { error = $"'{expressionText}' was not found again for replacement." };

        var typeText = type.ToMinimalDisplayString(model, expression.SpanStart, VbTypeFormat);
        var newLine = NewLineOf(text);
        var indentation = IndentationOf(text, statement.SpanStart);
        var declaration = $"{indentation}Dim {variableName} As {typeText} = {expressionText}{newLine}";

        var edits = occurrences
            .OrderByDescending(occurrence => occurrence.SpanStart)
            .Select(occurrence => new TextEdit(document, occurrence.Span, variableName))
            .ToList();
        edits.Add(new TextEdit(document, new TextSpan(text.Lines.GetLineFromPosition(
            Math.Clamp(statement.SpanStart, 0, text.Length)).Start, 0), declaration));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "introducing the local");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "introduce_variable",
            variable = variableName,
            inferredType = typeText,
            expression = expressionText,
            occurrencesReplaced = occurrences.Count,
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

    // ── introduce_field ──────────────────────────────────────────────────────
    public static async Task<object> IntroduceFieldAsync(IRoslynWorkspace workspace, string filePath, int line,
        string expressionText, string fieldName, bool dryRun, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(fieldName))
            return new { error = $"'{fieldName}' is not a valid Visual Basic identifier." };
        if (string.IsNullOrWhiteSpace(expressionText))
            return new { error = "The exact expression text is required." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count) return new { error = $"Line {line} is out of range." };

        var expression = ExpressionOnLine(root, text.Lines[line - 1], expressionText);
        if (expression is null)
            return new { error = $"Expression '{expressionText}' was not found as a single expression on line {line}." };

        if (model.GetTypeInfo(expression, cancellationToken).Type is not { } type ||
            type.TypeKind is TypeKind.Error or TypeKind.Unknown)
            return new { error = "The expression has no resolvable type; nothing to declare." };
        if (type.SpecialType == SpecialType.System_Void)
            return new { error = "A void expression cannot initialise a field." };

        var owner = expression.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (owner is null) return new { error = "The expression is not inside a type declaration." };
        if (owner is InterfaceBlockSyntax)
            return new { error = "An interface cannot declare a field." };

        if (model.GetDeclaredSymbol(owner.BlockStatement, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{owner.BlockStatement.Identifier.ValueText}' did not resolve to a type." };
        if (typeSymbol.GetMembers(fieldName).Length > 0)
            return new { error = $"'{typeSymbol.ToDisplayString()}' already has a member named '{fieldName}'." };

        var contextUse = FindNonHoistableUse(expression, model, cancellationToken);
        if (contextUse is not null)
            return new { error = $"The expression reads {contextUse}; a field initializer cannot use it." };

        var member = expression.Ancestors().OfType<MethodBlockSyntax>().FirstOrDefault()
                     ?? (SyntaxNode?)expression.Ancestors().OfType<PropertyBlockSyntax>().FirstOrDefault();
        if (member is null)
            return new { error = "The expression is not inside a method or property; a field would have no single owner." };

        var occurrences = OutermostOccurrences(member, expressionText);
        if (occurrences.Count == 0)
            return new { error = $"'{expressionText}' was not found again for replacement." };

        var typeText = type.ToMinimalDisplayString(model, expression.SpanStart, VbTypeFormat);
        var newLine = NewLineOf(text);
        var indentation = owner.Members.Count > 0
            ? IndentationOf(text, owner.Members[0].SpanStart)
            : IndentationOf(text, owner.SpanStart) + "    ";

        var declaration = $"{indentation}Private ReadOnly {fieldName} As {typeText} = {expressionText}{newLine}";
        var insertion = AfterTypeHeader(text, owner);

        var edits = occurrences
            .OrderByDescending(occurrence => occurrence.SpanStart)
            .Select(occurrence => new TextEdit(document, occurrence.Span, fieldName))
            .ToList();
        edits.Add(new TextEdit(document, new TextSpan(insertion, 0), declaration));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "introducing the field");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "introduce_field",
            type = typeSymbol.ToDisplayString(),
            field = fieldName,
            fieldType = typeText,
            expression = expressionText,
            replacements = occurrences.Count,
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

    // ── introduce_part_of_string ─────────────────────────────────────────────
    public static async Task<object> IntroducePartOfStringAsync(IRoslynWorkspace workspace, string filePath, int line,
        string part, string constantName, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(part)) return new { error = "The substring to extract is required." };
        if (!SyntaxFacts.IsValidIdentifier(constantName))
            return new { error = $"'{constantName}' is not a valid Visual Basic identifier." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var text = await document.GetTextAsync(cancellationToken);
        var literals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Where(literal =>
            {
                var span = literal.GetLocation().GetLineSpan();
                return line >= span.StartLinePosition.Line + 1 && line <= span.EndLinePosition.Line + 1;
            })
            .Where(literal => literal.Token.ValueText.Contains(part, StringComparison.Ordinal))
            .ToList();

        if (literals.Count == 0)
            return new { error = $"No string literal containing \"{part}\" on line {line} of {Path.GetFileName(filePath)}." };
        if (literals.Count > 1)
            return new { error = $"{literals.Count} string literals on line {line} contain \"{part}\"; use a line with one of them." };

        var literal = literals[0];
        var value = literal.Token.ValueText;
        var index = value.IndexOf(part, StringComparison.Ordinal);
        if (value.IndexOf(part, index + 1, StringComparison.Ordinal) >= 0)
            return new { error = $"\"{part}\" occurs more than once in \"{value}\"; the extraction would be ambiguous." };

        var owner = literal.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (owner is null) return new { error = "The literal is not inside a type declaration." };
        if (owner is InterfaceBlockSyntax) return new { error = "An interface cannot declare a constant." };

        if (model.GetDeclaredSymbol(owner.BlockStatement, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{owner.BlockStatement.Identifier.ValueText}' did not resolve to a type." };
        if (typeSymbol.GetMembers(constantName).Length > 0)
            return new { error = $"'{typeSymbol.ToDisplayString()}' already has a member named '{constantName}'." };

        var prefix = value[..index];
        var suffix = value[(index + part.Length)..];
        var pieces = new List<string>();
        if (prefix.Length > 0) pieces.Add(VbLiteral(prefix));
        pieces.Add(constantName);
        if (suffix.Length > 0) pieces.Add(VbLiteral(suffix));
        var replacement = string.Join(" & ", pieces);

        var newLine = NewLineOf(text);
        var indentation = owner.Members.Count > 0
            ? IndentationOf(text, owner.Members[0].SpanStart)
            : IndentationOf(text, owner.SpanStart) + "    ";
        var declaration = $"{indentation}Private Const {constantName} As String = {VbLiteral(part)}{newLine}";

        var edits = new List<TextEdit>
        {
            new(document, literal.Span, replacement),
            new(document, new TextSpan(AfterTypeHeader(text, owner), 0), declaration),
        };

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the extraction");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "introduce_part_of_string",
            constant = constantName,
            value = part,
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

    // ── introduce_parameter ──────────────────────────────────────────────────
    public static async Task<object> IntroduceParameterAsync(IRoslynWorkspace workspace, string filePath, int line,
        string expressionText, string parameterName, bool dryRun, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(parameterName))
            return new { error = $"'{parameterName}' is not a valid Visual Basic identifier." };
        if (string.IsNullOrWhiteSpace(expressionText))
            return new { error = "The exact expression text is required." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count) return new { error = $"Line {line} is out of range." };

        var expression = ExpressionOnLine(root, text.Lines[line - 1], expressionText);
        if (expression is null)
            return new { error = $"Expression '{expressionText}' was not found as a single expression on line {line}." };

        var constant = model.GetConstantValue(expression, cancellationToken);
        var isType = expression is GetTypeExpressionSyntax;
        var isNameOf = expression is NameOfExpressionSyntax;
        var isNothing = expression.IsKind(SyntaxKind.NothingLiteralExpression);
        if (!constant.HasValue && !isType && !isNameOf && !isNothing)
            return new
            {
                error = $"'{expressionText}' is not fixed at compile time, so no call site can pass it without inventing a value. " +
                        "The VB path handles constants, Nothing, GetType(...) and NameOf(...).",
            };

        if (!constant.HasValue && isNothing)
            return new { error = "'Nothing' has no type here; pass a value of a known type instead." };

        if (model.GetTypeInfo(expression, cancellationToken).Type is not { } type ||
            type.TypeKind is TypeKind.Error or TypeKind.Unknown)
            return new { error = "The expression has no resolvable type; the parameter would have none either." };

        var method = expression.Ancestors().OfType<MethodBlockSyntax>().FirstOrDefault();
        if (method is null) return new { error = "The expression is not inside a method." };

        if (model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken) is not IMethodSymbol symbol)
            return new { error = "The enclosing method did not resolve." };

        if (symbol.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
            return new { error = $"'{symbol.Name}' is not an ordinary method or constructor." };
        if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride || symbol.IsExtern)
            return new { error = $"'{symbol.Name}' is abstract/virtual/override/extern: its signature must not change here." };
        if (symbol.ExplicitInterfaceImplementations.Length > 0 ||
            (symbol.ContainingType is { } ownerType && ownerType.AllInterfaces
                .SelectMany(reference => reference.GetMembers(symbol.Name))
                .Any(interfaceMember => SymbolEqualityComparer.Default.Equals(
                    ownerType.FindImplementationForInterfaceMember(interfaceMember), symbol))))
            return new { error = $"'{symbol.Name}' implements an interface member: the signature must stay identical." };
        if (symbol.Parameters.Any(parameter => parameter.Name == parameterName))
            return new { error = $"'{parameterName}' is already a parameter of '{symbol.Name}'." };

        var occurrences = OutermostOccurrences(method, expressionText);
        if (occurrences.Count == 0)
            return new { error = $"'{expressionText}' was not found again for replacement." };

        var typeText = type.ToMinimalDisplayString(model, expression.SpanStart, VbTypeFormat);
        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        var (parameterInsertion, parameterText) = ParameterInsertion(method, parameterName, typeText);
        AddEdit(edits, document, new TextSpan(parameterInsertion, 0), parameterText);

        foreach (var occurrence in occurrences)
            AddEdit(edits, document, occurrence.Span, parameterName);

        var callSites = 0;
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (method.Span.Contains(location.Location.SourceSpan)) continue;

                var target = location.Document;
                var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node.AncestorsAndSelf().Any(ancestor => ancestor is XmlNodeSyntax)) continue;

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                    return new
                    {
                        error = $"'{symbol.Name}' is used at {Path.GetFileName(target.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} in a way that is not a plain call " +
                                "(a delegate or an AddressOf): the argument cannot be inserted.",
                    };

                if (invocation.ArgumentList.OpenParenToken.IsMissing || invocation.ArgumentList.CloseParenToken.IsMissing)
                    return new
                    {
                        error = $"The call to '{symbol.Name}' at {Path.GetFileName(target.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} has no parentheses; " +
                                "parenthesise it before adding an argument.",
                    };

                if (invocation.ArgumentList.Arguments.Any(argument => argument.IsNamed))
                    return new
                    {
                        error = $"The call to '{symbol.Name}' at {Path.GetFileName(target.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} uses a named argument; " +
                                "a positional parameter cannot follow it.",
                    };

                var close = invocation.ArgumentList.CloseParenToken.SpanStart;
                var prefix = invocation.ArgumentList.Arguments.Count == 0 ? string.Empty : ", ";
                AddEdit(edits, target, new TextSpan(close, 0), prefix + expressionText);
                callSites++;
            }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(),
            dryRun, cancellationToken, "introducing the parameter");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "introduce_parameter",
            method = symbol.ToDisplayString(),
            parameter = parameterName,
            parameterType = typeText,
            occurrencesReplaced = occurrences.Count,
            callSites = callSites,
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
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── invert_boolean ───────────────────────────────────────────────────────
    public static async Task<object> InvertBooleanAsync(IRoslynWorkspace workspace, string filePath, string memberName,
        string? containingType, string? newName, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, memberName,
                StringComparison.OrdinalIgnoreCase))
            .Where(method => method.SubOrFunctionStatement.IsKind(SyntaxKind.FunctionStatement))
            .Where(method => string.Equals(TypeTextOf(method.SubOrFunctionStatement.AsClause), "Boolean",
                StringComparison.OrdinalIgnoreCase))
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken)?
                                 .ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No bool-returning Function named '{memberName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several Functions named '{memberName}'; address one overload at a time." };

        var method = candidates[0];
        if (model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken) is not IMethodSymbol symbol)
            return new { error = $"'{memberName}' did not resolve." };

        if (method.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            return new { error = "The Function yields: an iterator has no Boolean result to invert." };

        var returns = method.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(statement => !statement.Ancestors().OfType<LambdaExpressionSyntax>().Any())
            .ToList();
        if (returns.Count == 0)
            return new { error = $"'{memberName}' has no Return statement to invert." };
        if (returns.Any(statement => statement.Expression is null))
            return new { error = $"'{memberName}' has a 'Return' without a value: it cannot be inverted." };

        var text = await document.GetTextAsync(cancellationToken);
        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        foreach (var statement in returns)
        {
            var target = statement.Expression!;
            AddEdit(edits, document, target.Span, Negated(target));
        }

        var renamedTo = (string?)null;
        if (!string.IsNullOrWhiteSpace(newName) &&
            !string.Equals(newName, memberName, StringComparison.OrdinalIgnoreCase))
        {
            if (!SyntaxFacts.IsValidIdentifier(newName))
                return new { error = $"'{newName}' is not a valid Visual Basic identifier." };
            if (symbol.ContainingType is { } ownerType && ownerType.GetMembers(newName).Length > 0)
                return new { error = $"'{ownerType.ToDisplayString()}' already has a member named '{newName}'." };

            AddEdit(edits, document, method.SubOrFunctionStatement.Identifier.Span, newName);
            renamedTo = newName;

            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (method.Span.Contains(location.Location.SourceSpan)) continue;

                    var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                    if (targetRoot is null) continue;

                    var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    var name = node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
                    if (name is null) continue;

                    AddEdit(edits, location.Document, name.Span, newName);
                }
        }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(),
            dryRun, cancellationToken, $"inverting '{memberName}'");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "invert_boolean",
            member = symbol.ToDisplayString(),
            invertedReturns = returns.Count,
            renamedTo,
            filesModified = outcome.Files.Count,
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

    // ── copy_type ────────────────────────────────────────────────────────────
    public static async Task<object> CopyTypeAsync(IRoslynWorkspace workspace, string filePath, string typeName,
        string newName, bool dryRun, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
            return new { error = $"'{newName}' is not a valid Visual Basic identifier." };
        if (string.Equals(typeName, newName, StringComparison.Ordinal))
            return new { error = "The names are identical." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;
        var text = await document.GetTextAsync(cancellationToken);

        var candidates = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .Where(block => string.Equals(block.BlockStatement.Identifier.ValueText, typeName, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) return new { error = $"No type '{typeName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"'{typeName}' has {candidates.Count} declarations in the file; copy one partial at a time." };

        var block = candidates[0];
        if (model.GetDeclaredSymbol(block.BlockStatement, cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var scope = (INamespaceOrTypeSymbol?)symbol.ContainingType ?? symbol.ContainingNamespace;
        if (scope is not null && scope.GetMembers(newName).Length > 0)
            return new { error = $"'{newName}' already exists in the same scope." };

        // VB constructors are always "Sub New", so the copy only has to rename the declaration itself.
        var source = text.ToString(block.Span);
        var identifierStart = block.BlockStatement.Identifier.SpanStart - block.SpanStart;
        var copy = source[..identifierStart] + newName + source[(identifierStart + block.BlockStatement.Identifier.Span.Length)..];

        var indentation = IndentationOf(text, block.SpanStart);
        var newLine = NewLineOf(text);
        var insertion = AfterEndOfType(text, block);
        var blockText = newLine + indentation + copy + newLine;

        var applied = await ApplyAsync(workspace, solution,
            [new TextEdit(document, new TextSpan(insertion, 0), blockText)],
            dryRun, cancellationToken, "copying the type");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "copy_type",
            type = symbol.ToDisplayString(),
            newType = newName,
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

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void AddEdit(Dictionary<DocumentId, List<TextEdit>> edits, Document document, TextSpan span,
        string replacement)
    {
        if (!edits.TryGetValue(document.Id, out var list))
        {
            list = [];
            edits[document.Id] = list;
        }

        list.Add(new TextEdit(document, span, replacement));
    }

    private static ExpressionSyntax? ExpressionOnLine(SyntaxNode root, TextLine line, string expressionText) =>
        root.DescendantNodes(line.Span)
            .OfType<ExpressionSyntax>()
            .Where(candidate => string.Equals(candidate.ToString(), expressionText, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

    private static List<ExpressionSyntax> OutermostOccurrences(SyntaxNode container, string expressionText)
    {
        var occurrences = container.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(candidate => string.Equals(candidate.ToString(), expressionText, StringComparison.Ordinal))
            .ToList();

        return occurrences
            .Where(candidate => !occurrences.Any(other => other != candidate && other.Span.Contains(candidate.Span)))
            .ToList();
    }

    private static (int Position, string Text) ParameterInsertion(MethodBlockSyntax method, string parameterName,
        string typeText)
    {
        var statement = method.SubOrFunctionStatement;
        var list = statement.ParameterList;

        if (list is null || list.OpenParenToken.IsMissing || list.CloseParenToken.IsMissing)
            return (statement.Identifier.Span.End, $"({parameterName} As {typeText})");

        if (list.Parameters.Count > 0)
            return (list.Parameters.Last().Span.End, $", {parameterName} As {typeText}");

        return (list.OpenParenToken.Span.End, $"{parameterName} As {typeText}");
    }

    /// <summary>What makes the expression unusable in a field initializer, or null when it is context-free.</summary>
    private static string? FindNonHoistableUse(ExpressionSyntax expression, SemanticModel model,
        CancellationToken cancellationToken)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            if (node is MeExpressionSyntax) return "'Me'";
            if (node is MyBaseExpressionSyntax) return "'MyBase'";
            if (node is AwaitExpressionSyntax) return "an awaited value";

            if (node is not IdentifierNameSyntax and not SimpleNameSyntax) continue;

            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is null) continue;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
                return $"'{symbol.Name}' (a local or parameter)";
            if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
            if (symbol.IsStatic) continue;

            return $"the instance member '{symbol.Name}'";
        }

        return null;
    }

    /// <summary><c>True</c>/<c>False</c> flip; <c>Not x</c> unwraps; anything else is parenthesised behind a <c>Not</c>.</summary>
    private static string Negated(ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.TrueLiteralExpression)) return "False";
        if (expression.IsKind(SyntaxKind.FalseLiteralExpression)) return "True";

        if (expression is UnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.NotExpression))
            return unary.Operand.ToString();

        return "Not (" + expression + ")";
    }

    /// <summary>A Visual Basic string literal: a quote inside the value is written twice, not escaped.</summary>
    private static string VbLiteral(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
