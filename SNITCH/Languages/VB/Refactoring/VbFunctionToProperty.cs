using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Method to property" for Visual Basic: a parameterless <c>Function</c> becomes a
/// <c>ReadOnly Property</c> whose <c>Get</c> holds the old body, and every call site — with parentheses
/// (<c>GetCount()</c>) or without them (<c>GetCount</c>), the two spellings VB allows — reads the
/// property instead. A use that is not a plain call (<c>AddressOf</c>) is refused, because converting it
/// would change what the expression means.
/// </summary>
internal static class VbFunctionToProperty
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace,
        Solution solution,
        Document document,
        string methodName,
        string? containingType,
        string? propertyName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (document.FilePath is null) return new { error = "The document has no file on disk." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root is null || model is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        var candidates = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => method.SubOrFunctionStatement.Identifier.ValueText == methodName)
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken)?.ContainingType?.Name,
                                 containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No method '{methodName}' in {Path.GetFileName(document.FilePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several methods named '{methodName}' (overloads); converting one would collide with the property name." };

        var block = candidates[0];
        var statement = block.SubOrFunctionStatement;

        if (statement.IsKind(SyntaxKind.SubStatement))
            return new { error = $"'{methodName}' is a Sub; a property needs a value." };
        if (statement.AsClause is null)
            return new { error = $"'{methodName}' declares no return type; add 'As <type>' first so the property has one." };
        if (statement.ParameterList?.Parameters.Count > 0)
            return new { error = $"'{methodName}' takes parameters; a property cannot." };
        if (statement.TypeParameterList is not null)
            return new { error = $"'{methodName}' is generic; a property cannot be." };
        if (statement.ImplementsClause is not null)
            return new { error = $"'{methodName}' implements an interface member; the interface declares a method, not a property." };
        if (statement.HandlesClause is not null)
            return new { error = $"'{methodName}' handles an event; its signature is not the method's to change." };
        if (statement.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)))
            return new { error = $"'{methodName}' is Async; a property cannot be." };
        if (statement.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.OverridesKeyword) || modifier.IsKind(SyntaxKind.OverridableKeyword) ||
                modifier.IsKind(SyntaxKind.MustOverrideKeyword)))
            return new { error = $"'{methodName}' is overridable or overrides: overrides would stop binding." };

        if (model.GetDeclaredSymbol(statement, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var owner = method.ContainingType;
        if (owner is null) return new { error = $"'{methodName}' has no containing type." };

        var targetName = propertyName ?? DefaultPropertyName(methodName);
        if (!SyntaxFacts.IsValidIdentifier(targetName))
            return new { error = $"'{targetName}' is not a valid identifier; pass propertyName explicitly." };
        if (owner.GetMembers(targetName).Length > 0)
            return new { error = $"'{owner.ToDisplayString()}' already has a member named '{targetName}'; pass another propertyName." };

        // ── every use must be a plain call ────────────────────────────────────────────────────
        var changes = new Dictionary<DocumentId, List<TextChange>> { [document.Id] = [] };
        var unsupported = new List<string>();
        var callSites = 0;

        foreach (var reference in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id == document.Id && block.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null || referenceRoot is null) continue;

                // "AddressOf M" is a delegate conversion, not a call: a property cannot stand in for it.
                if (node.AncestorsAndSelf().OfType<UnaryExpressionSyntax>()
                    .Any(expression => expression.IsKind(SyntaxKind.AddressOfExpression)))
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (AddressOf)");
                    continue;
                }

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                Add(changes, location.Document.Id, invocation is not null &&
                                                    invocation.Expression.Span.Contains(location.Location.SourceSpan)
                    ? new TextChange(invocation.Span, Rewrite(invocation.Expression, targetName))
                    : new TextChange(location.Location.SourceSpan, targetName));

                callSites++;
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) are not plain calls, so converting would change their meaning; " +
                        "nothing was written.",
                uses = unsupported.Take(10).ToList(),
            };

        // ── the declaration: Function body → Get accessor ─────────────────────────────────────
        var indent = IndentationOf(text, block.SpanStart);
        var replacement = BuildProperty(statement, block, targetName, indent, NewlineOf(text));
        changes[document.Id].Add(new TextChange(block.Span, replacement));

        // ── the projection the compiler judges ────────────────────────────────────────────────
        var after = solution;
        foreach (var (documentId, edits) in changes)
        {
            var target = after.GetDocument(documentId);
            if (target is null) continue;
            after = after.WithDocumentText(documentId, (await target.GetTextAsync(cancellationToken)).WithChanges(edits));
        }

        var gate = await RefactoringCompilerGate.CompareAsync(solution, after, [document.Project.Id], cancellationToken);
        if (!gate.Passed)
            return new
            {
                error = $"Refused: converting '{methodName}' would add {gate.Added} compiler error(s) " +
                        $"({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
                preview = RefactoringFileWriter.Diff(
                    text.ToString(), (await after.GetDocument(document.Id)!.GetTextAsync(cancellationToken)).ToString()),
            };

        var files = new List<object>();
        foreach (var (documentId, _) in changes)
        {
            var target = solution.GetDocument(documentId);
            if (target?.FilePath is null) continue;

            var before = (await target.GetTextAsync(cancellationToken)).ToString();
            var now = (await after.GetDocument(documentId)!.GetTextAsync(cancellationToken)).ToString();

            if (!dryRun)
                await RefactoringFileWriter.WriteAndUpdateAsync(workspace, target, now, cancellationToken);

            files.Add(new
            {
                file = target.FilePath,
                preview = RefactoringFileWriter.Diff(before, now),
            });
        }

        return new
        {
            @refactoring = "function_to_property",
            language = "Visual Basic",
            method = method.ToDisplayString(),
            property = targetName,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            callSitesRewritten = callSites,
            filesChanged = files.Count,
            files,
            compilerErrors = new { before = gate.Before, after = gate.After },
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>`GetCount` → `Count`; anything else keeps its name (capitalised).</summary>
    private static string DefaultPropertyName(string methodName)
    {
        var name = methodName.Length > 3 && methodName.StartsWith("Get", StringComparison.Ordinal)
            ? methodName[3..]
            : methodName;

        return name.Length == 0 ? methodName : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// The property that replaces the method block: the declaration keeps the method's modifiers and
    /// return type, the old body becomes the <c>Get</c> accessor, and its statements are indented one
    /// level deeper so the file still reads the way VB writes it.
    /// </summary>
    private static string BuildProperty(
        MethodStatementSyntax statement, MethodBlockSyntax block, string propertyName, string indent, string newline)
    {
        var builder = new StringBuilder();

        if (statement.Modifiers.Count > 0) builder.Append(statement.Modifiers.ToString()).Append(' ');
        builder.Append("ReadOnly Property ").Append(propertyName).Append(" As ").Append(statement.AsClause!.Type);
        builder.Append(newline).Append(indent).Append("    Get");

        // The newline that followed the Function header is the *trailing* trivia of its last token, so
        // the statements start at their indentation: the line break is written here, not inherited.
        var body = block.Statements.ToFullString().TrimEnd();
        if (body.Length > 0) builder.Append(newline).Append(Reindent(body, newline));

        builder.Append(newline).Append(indent).Append("    End Get");
        builder.Append(newline).Append(indent).Append("End Property");

        return builder.ToString();
    }

    private static string Reindent(string body, string newline)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        return string.Join(newline, lines.Select(line => line.Trim().Length == 0 ? line.TrimEnd() : "    " + line));
    }

    private static string Rewrite(ExpressionSyntax expression, string propertyName) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Expression + "." + propertyName,
        _ => propertyName,
    };

    private static void Add(Dictionary<DocumentId, List<TextChange>> changes, DocumentId documentId, TextChange change)
    {
        if (!changes.TryGetValue(documentId, out var list)) changes[documentId] = list = [];
        list.Add(change);
    }

    private static string IndentationOf(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, position));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }

    private static string NewlineOf(SourceText text) =>
        text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
