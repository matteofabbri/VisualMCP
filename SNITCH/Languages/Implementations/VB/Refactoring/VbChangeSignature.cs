using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Change signature" for a Visual Basic method: reorder, remove and add parameters on the
/// declaration and at every plain call site, then keep the change only when the project still compiles.
/// </summary>
/// <remarks>
/// The rewrite is a set of text splices rather than a rebuilt syntax tree: a VB call site with no
/// parentheses (<c>M value</c>) and one with them (<c>M(value)</c>) are edited in place, each argument is
/// re-emitted from the expression the compiler resolved, and the declaration's parameter list is written
/// from the parameter nodes themselves. A use that is not a plain call — <c>AddressOf M</c>, a
/// delegate conversion — has no argument list to rewrite, so the whole operation is refused instead of
/// silently breaking it.
/// </remarks>
internal static class VbChangeSignature
{
    public static async Task<object?> ChangeSignatureAsync(
        IRoslynWorkspace workspace,
        Solution solution,
        IMethodSymbol method,
        string[]? newParameterOrder,
        IReadOnlyList<(string Name, string Type, string? DefaultValue)> additionalParams,
        Dictionary<string, string>? defaultForRemoved,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var originalParams = method.Parameters.Select(parameter => parameter.Name).ToList();
        var orderedNames = newParameterOrder?.ToList() ?? originalParams;
        var removedNames = originalParams.Except(orderedNames).ToHashSet(StringComparer.Ordinal);

        var problematic = removedNames
            .Where(name =>
            {
                var parameter = method.Parameters.FirstOrDefault(candidate => candidate.Name == name);
                return parameter is not null && !parameter.HasExplicitDefaultValue &&
                       (defaultForRemoved is null || !defaultForRemoved.ContainsKey(name));
            })
            .ToList();

        if (problematic.Count > 0)
            return new
            {
                error = $"Cannot remove required parameter(s) '{string.Join(", ", problematic)}' without supplying " +
                        "defaultForRemoved values for each.",
            };

        var unknown = orderedNames.Where(name => !originalParams.Contains(name, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            return new
            {
                error = $"Unknown parameter(s) '{string.Join(", ", unknown)}' in newParameterOrder.",
                parameters = originalParams,
            };

        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
            as MethodStatementSyntax;
        if (declaration?.ParameterList is null)
            return new { error = "Could not locate the Visual Basic method declaration." };

        var declarationPath = declaration.SyntaxTree.FilePath;
        if (declarationPath is null) return new { error = "The declaration has no file on disk." };

        var declarationDocument = DocumentAt(solution, declarationPath);
        if (declarationDocument is null) return new { error = "Could not locate the declaration's document." };

        // ── call sites: every plain call, or nothing ─────────────────────────────
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var calls = new List<(Document Document, InvocationExpressionSyntax Call)>();
        var notPlainCalls = new List<string>();

        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            var document = solution.GetDocument(location.Document.Id);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
            if (document?.FilePath is null || root is null) continue;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var call = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (call?.ArgumentList is null)
            {
                var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                notPlainCalls.Add($"{Path.GetFileName(document.FilePath)}:{line} ({node})");
                continue;
            }

            if (calls.Any(existing => existing.Document.Id == document.Id && existing.Call.Span == call.Span)) continue;
            calls.Add((document, call));
        }

        if (notPlainCalls.Count > 0)
            return new
            {
                error = $"{notPlainCalls.Count} use(s) are not plain calls (AddressOf or a delegate conversion); " +
                        "the signature change would break them. Nothing was written.",
                uses = notPlainCalls.Take(10).ToList(),
            };

        // ── the edits, per file ──────────────────────────────────────────────────
        var editsByDocument = new Dictionary<DocumentId, List<Edit>>();
        var previews = new List<object>();

        var parameterText = string.Join(", ", orderedNames.Select(name =>
            declaration.ParameterList.Parameters.First(parameter =>
                string.Equals(parameter.Identifier.Identifier.ValueText, name, StringComparison.Ordinal)).ToString().Trim())
            .Concat(additionalParams.Select(parameter => AddedParameterText(parameter))));

        AddEdit(editsByDocument, declarationDocument, new Edit(
            declaration.ParameterList.Span, $"({parameterText})"));

        foreach (var (document, call) in calls)
        {
            var original = call.ToString();
            var replacement = RewriteCall(call, originalParams, orderedNames, additionalParams, defaultForRemoved);
            AddEdit(editsByDocument, document, new Edit(call.ArgumentList.Span, replacement));

            previews.Add(new
            {
                file = document.FilePath,
                line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                before = original,
                after = original.Replace(call.ArgumentList.ToString(), replacement),
            });
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (documentId, edits) in editsByDocument)
        {
            var document = solution.GetDocument(documentId);
            if (document?.FilePath is null) continue;

            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            texts[document.FilePath] = Apply(text, edits);
        }

        // ── the gate: the project must still compile ─────────────────────────────
        var after = solution;
        foreach (var (path, text) in texts)
        {
            var document = DocumentAt(solution, path);
            if (document is null) continue;
            after = after.WithDocumentText(document.Id, Microsoft.CodeAnalysis.Text.SourceText.From(text));
        }

        var projectIds = editsByDocument.Keys
            .Select(documentId => solution.GetDocument(documentId)?.Project.Id)
            .OfType<ProjectId>()
            .Distinct()
            .ToList();

        var gate = await RefactoringCompilerGate.CompareAsync(solution, after, projectIds, cancellationToken);
        if (!gate.Passed)
            return new
            {
                error = $"Refused: the signature change would add {gate.Added} compiler error(s) " +
                        $"({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
            };

        var files = texts.Select(entry => new
        {
            file = entry.Key,
            preview = RefactoringFileWriter.Diff(File.Exists(entry.Key) ? File.ReadAllText(entry.Key) : string.Empty, entry.Value),
        }).ToList();

        var newParams = orderedNames.Concat(additionalParams.Select(parameter => parameter.Name)).ToList();

        if (dryRun)
            return new
            {
                dryRun = true,
                language = "Visual Basic",
                method = method.ToDisplayString(),
                originalParams,
                newParams,
                removed = removedNames.ToList(),
                callSitesAffected = calls.Count,
                callSites = previews,
                files,
                compilerErrors = new { before = gate.Before, after = gate.After },
                note = "Preview only: pass dryRun=false to write the files.",
            };

        foreach (var (path, text) in texts)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, path, text, cancellationToken);

        return new
        {
            dryRun = false,
            language = "Visual Basic",
            method = method.ToDisplayString(),
            originalParams,
            newParams,
            removed = removedNames.ToList(),
            callSitesUpdated = calls.Count,
            callSites = previews,
            filesModified = texts.Count,
            files,
            compilerErrors = new { before = gate.Before, after = gate.After },
            note = "Files written.",
        };
    }

    /// <summary>
    /// The new argument list for one call, as text. An argument the call supplied is re-emitted from the
    /// expression the compiler resolved, a removed parameter takes the caller's replacement when there is
    /// one, and an added parameter gets its value (or <c>Nothing</c>).
    /// </summary>
    private static string RewriteCall(
        InvocationExpressionSyntax call,
        IReadOnlyList<string> originalParams,
        IReadOnlyList<string> orderedNames,
        IReadOnlyList<(string Name, string Type, string? DefaultValue)> additionalParams,
        Dictionary<string, string>? defaultForRemoved)
    {
        var arguments = call.ArgumentList.Arguments;
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var expression = argument.GetExpression()?.ToString().Trim();
            if (expression is null) continue;

            if (argument is SimpleArgumentSyntax { NameColonEquals: { } nameColon })
                byName[nameColon.Name.Identifier.ValueText] = expression;
            else if (index < originalParams.Count)
                byName[originalParams[index]] = expression;
        }

        var values = new List<string>();
        foreach (var name in orderedNames)
        {
            if (byName.TryGetValue(name, out var expression))
                values.Add(expression);
            else if (defaultForRemoved?.TryGetValue(name, out var replacement) == true)
                values.Add(replacement);
            // else: a removed parameter with an explicit default — the call omits it.
        }

        values.AddRange(additionalParams.Select(parameter => parameter.DefaultValue ?? "Nothing"));

        var parenthesised = !call.ArgumentList.OpenParenToken.IsMissing;
        var joined = string.Join(", ", values);

        return parenthesised ? $"({joined})" : joined;
    }

    /// <summary>An added parameter, in VB's spelling: a default value makes it <c>Optional</c>.</summary>
    private static string AddedParameterText((string Name, string Type, string? DefaultValue) parameter) =>
        parameter.DefaultValue is null
            ? $"{parameter.Name} As {parameter.Type}"
            : $"Optional {parameter.Name} As {parameter.Type} = {parameter.DefaultValue}";

    private static void AddEdit(
        Dictionary<DocumentId, List<Edit>> edits, Document document, Edit edit)
    {
        if (!edits.TryGetValue(document.Id, out var list)) edits[document.Id] = list = [];
        list.Add(edit);
    }

    private static string Apply(string text, List<Edit> edits)
    {
        var builder = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start))
        {
            builder.Remove(edit.Span.Start, edit.Span.Length);
            builder.Insert(edit.Span.Start, edit.Text);
        }

        return builder.ToString();
    }

    private static Document? DocumentAt(Solution solution, string path) =>
        solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(document => document.FilePath is not null &&
                                       string.Equals(Path.GetFullPath(document.FilePath), Path.GetFullPath(path),
                                           Snitch.Platform.HostSystem.PathComparison));

    private sealed record Edit(Microsoft.CodeAnalysis.Text.TextSpan Span, string Text);
}
