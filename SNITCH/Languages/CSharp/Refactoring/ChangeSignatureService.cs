using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

public interface IChangeSignatureService
{
    Task<object> ChangeSignature(string methodName, string? containingType, string[]? newParameterOrder, ChangeSignatureService.NewParameter[]? additionalParams, Dictionary<string, string>? defaultForRemoved, bool dryRun);
}

public sealed class ChangeSignatureService : IChangeSignatureService
{
    private readonly IRoslynWorkspace _workspace;
    public ChangeSignatureService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ChangeSignature(
        [Description("Method name to modify")] string methodName,
        [Description("Containing type name to disambiguate")] string? containingType = null,
        [Description("New order of existing parameters — array of parameter names, e.g. [\"b\",\"a\"]. Omit a name to remove it.")] string[]? newParameterOrder = null,
        [Description("New parameters to add at the end — array of JSON objects with fields: name, type, defaultValue (optional). E.g. [{\"name\":\"timeout\",\"type\":\"int\",\"defaultValue\":\"30\"}]")] NewParameter[]? additionalParams = null,
        [Description("For removed required parameters: literal expression to insert at every call site, keyed by parameter name. E.g. {\"count\": \"0\"}")] Dictionary<string, string>? defaultForRemoved = null,
        [Description("Dry run — show changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // Find the method symbol
        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(methodName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Member);

        var methods = candidates
            .OfType<IMethodSymbol>()
            .Where(m => containingType is null ||
                        m.ContainingType.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();

        if (methods.Count == 0)
            return new { error = $"Method '{methodName}' not found." };
        if (methods.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {methods.Count} methods named '{methodName}'. Specify containingType.",
                candidates = methods.Select(m => m.ToDisplayString()).ToList(),
            };

        var method = methods[0];
        var originalParams = method.Parameters.Select(p => p.Name).ToList();

        // A Visual Basic declaration takes the same operation, written against VB syntax: a call site may
        // have no parentheses, and an argument is named with ':=' rather than ':'.
        if (method.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbChangeSignature.ChangeSignatureAsync(
                       _workspace,
                       solution,
                       method,
                       newParameterOrder,
                       (additionalParams ?? [])
                       .Select(parameter => (parameter.Name, parameter.Type, parameter.DefaultValue))
                       .ToList(),
                       defaultForRemoved,
                       dryRun,
                       CancellationToken.None)
                   ?? new { error = "The Visual Basic signature change produced no result." };

        // Build the target parameter sequence
        var orderedNames = newParameterOrder?.ToList() ?? originalParams;
        var addedParams = additionalParams ?? [];
        var removedNames = originalParams.Except(orderedNames).ToHashSet();

        // Validate: removed params with no default and no defaultForRemoved
        var problematic = removedNames
            .Where(n =>
            {
                var p = method.Parameters.FirstOrDefault(x => x.Name == n);
                return p is not null && !p.HasExplicitDefaultValue &&
                       (defaultForRemoved is null || !defaultForRemoved.ContainsKey(n));
            })
            .ToList();

        if (problematic.Count > 0)
            return new
            {
                error = $"Cannot remove required parameter(s) '{string.Join(", ", problematic)}' without supplying defaultForRemoved values for each.",
            };

        // ── Locate the declaration ────────────────────────────────────────
        var declLoc = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLoc is null)
            return new { error = "Method has no source location." };

        var sourceDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null &&
                                 string.Equals(d.FilePath, declLoc.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase));
        if (sourceDoc?.FilePath is null)
            return new { error = "Could not locate source document." };

        if (await sourceDoc.GetSyntaxRootAsync() is not CompilationUnitSyntax root)
            return new { error = "Could not parse source file." };

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.Ordinal) &&
                                 m.Span.Contains(declLoc.SourceSpan.Start));
        if (methodNode is null)
            return new { error = "Could not locate method declaration node." };

        // Build new parameter list for the declaration
        var paramByName = methodNode.ParameterList.Parameters.ToDictionary(p => p.Identifier.Text);

        var newParamNodes = new List<ParameterSyntax>();
        foreach (var name in orderedNames)
        {
            if (paramByName.TryGetValue(name, out var pn))
                newParamNodes.Add(pn);
        }

        foreach (var extra in addedParams)
        {
            var pText = extra.DefaultValue is not null
                ? $"{extra.Type} {extra.Name} = {extra.DefaultValue}"
                : $"{extra.Type} {extra.Name}";
            newParamNodes.Add(ParseParameterList($"({pText})").Parameters[0]);
        }

        // WithParameters keeps the original parentheses and their trivia (a rebuilt ParameterList()
        // would emit "int b, int a)=> x" — the space before the arrow lives on the closing paren).
        var newParamList = methodNode.ParameterList.WithParameters(SeparatedList(newParamNodes,
            Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space),
                Math.Max(0, newParamNodes.Count - 1))));

        // ── Call sites: collect the invocations, refuse anything else ─────────────────────
        var references = await SymbolFinder.FindReferencesAsync(method, solution);
        var invocationsByDocument = new Dictionary<DocumentId, List<InvocationExpressionSyntax>>();
        var notPlainCalls = new List<string>();

        foreach (var location in references.SelectMany(r => r.Locations).Where(l => l.Location.IsInSource))
        {
            var document = solution.GetDocument(location.Document.Id);
            var referenceRoot = document is null ? null : await document.GetSyntaxRootAsync();
            if (document is null || referenceRoot is null) continue;

            var node = referenceRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null)
            {
                var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                notPlainCalls.Add($"{Path.GetFileName(location.Document.FilePath)}:{line} ({node})");
                continue;
            }

            if (!invocationsByDocument.TryGetValue(document.Id, out var list))
                invocationsByDocument[document.Id] = list = new List<InvocationExpressionSyntax>();
            if (!list.Any(existing => existing.Span == invocation.Span))
                list.Add(invocation);
        }

        // A method group (delegate conversion) has no argument list to rewrite: changing the
        // signature would break it, so nothing is written.
        if (notPlainCalls.Count > 0)
            return new
            {
                error = $"{notPlainCalls.Count} use(s) are not plain calls (method group or delegate); " +
                        "the signature change would break them. Nothing was written.",
                uses = notPlainCalls.Take(10).ToList(),
            };

        // ── Rewrite: the declaration's own file first, then every other call-site file ────
        var filesToWrite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previews = new List<(string File, int Line, string Before, string After)>();
        var callSiteCount = 0;

        foreach (var (documentId, invocations) in invocationsByDocument)
        {
            var document = solution.GetDocument(documentId);
            var documentRoot = document is null ? null : await document.GetSyntaxRootAsync();
            if (document?.FilePath is null || documentRoot is null) continue;

            // Preview first, from the nodes as they are.
            foreach (var invocation in invocations.OrderBy(invocation => invocation.SpanStart))
            {
                var rewritten = RewriteInvocation(invocation, originalParams, orderedNames, addedParams, defaultForRemoved);
                previews.Add((document.FilePath,
                    invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    invocation.ToString(), rewritten.ToString()));
            }

            SyntaxNode newDocumentRoot;
            if (documentId == sourceDoc.Id)
            {
                // The declaration is annotated before the calls are rewritten: replacing the calls
                // above it shifts its span, so it must be found by annotation, not by offset.
                var annotation = new SyntaxAnnotation("change_signature_declaration");
                var annotated = documentRoot.ReplaceNode(
                    methodNode, methodNode.WithAdditionalAnnotations(annotation));

                var callsToRewrite = annotated.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(candidate => invocations.Any(original => original.Span == candidate.Span))
                    .ToList();

                var withCalls = annotated.ReplaceNodes(callsToRewrite,
                    (original, _) => RewriteInvocation(original, originalParams, orderedNames, addedParams, defaultForRemoved));

                var declaration = withCalls.GetAnnotatedNodes(annotation).OfType<MethodDeclarationSyntax>().Single();
                newDocumentRoot = withCalls.ReplaceNode(declaration, declaration.WithParameterList(newParamList));
            }
            else
            {
                var callsToRewrite = documentRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(candidate => invocations.Any(original => original.Span == candidate.Span))
                    .ToList();
                newDocumentRoot = documentRoot.ReplaceNodes(callsToRewrite,
                    (original, _) => RewriteInvocation(original, originalParams, orderedNames, addedParams, defaultForRemoved));
            }

            callSiteCount += invocations.Count;
            filesToWrite[document.FilePath] = newDocumentRoot.ToFullString();
        }

        if (!filesToWrite.ContainsKey(sourceDoc.FilePath))
        {
            var newRoot = root.ReplaceNode(methodNode, methodNode.WithParameterList(newParamList));
            filesToWrite[sourceDoc.FilePath] = newRoot.ToFullString();
        }

        // ── Gate: the whole solution must still compile ───────────────────────────────────
        var projected = solution;
        foreach (var (path, text) in filesToWrite)
        {
            var document = solution.Projects.SelectMany(project => project.Documents)
                .FirstOrDefault(candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (document is not null)
                projected = projected.WithDocumentText(document.Id, SourceText.From(text));
        }

        var before = (await ErrorsAsync(solution)).Count;
        var afterErrors = await ErrorsAsync(projected);
        if (afterErrors.Count > before)
            return new
            {
                error = $"Refused: the signature change would add {afterErrors.Count - before} compiler error(s) " +
                        $"({before} → {afterErrors.Count}).",
                errors = afterErrors.Take(8).ToList(),
            };

        // ── Report and write ─────────────────────────────────────────────────────────────
        var files = filesToWrite.Select(entry =>
        {
            var original = File.Exists(entry.Key) ? File.ReadAllText(entry.Key) : string.Empty;
            return new
            {
                file = entry.Key,
                preview = RefactoringFileWriter.Diff(original, entry.Value),
            };
        }).ToList();

        var callSites = previews.Select(preview => new
        {
            file = preview.File,
            line = preview.Line,
            before = preview.Before,
            after = preview.After,
        }).ToList();

        if (dryRun)
            return new
            {
                dryRun = true,
                method = method.ToDisplayString(),
                originalParams,
                newParams = orderedNames.Concat(addedParams.Select(p => p.Name)).ToList(),
                removed = removedNames.ToList(),
                callSitesAffected = callSiteCount,
                callSites,
                files,
                compilerErrors = new { before, after = afterErrors.Count },
                note = "Preview only: pass dryRun=false to write the files.",
            };

        foreach (var (path, text) in filesToWrite)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, path, text);

        return new
        {
            dryRun = false,
            method = method.ToDisplayString(),
            originalParams,
            newParams = orderedNames.Concat(addedParams.Select(p => p.Name)).ToList(),
            removed = removedNames.ToList(),
            callSitesUpdated = callSiteCount,
            callSites,
            filesModified = filesToWrite.Count,
            files,
            compilerErrors = new { before, after = afterErrors.Count },
            note = "Files written.",
        };
    }

    /// <summary>
    /// Rebuilds one call's argument list in the new order. Arguments are nodes, not text offsets, so
    /// the declaration's own file — where the parameter list changes too — stays correct.
    /// </summary>
    private static InvocationExpressionSyntax RewriteInvocation(
        InvocationExpressionSyntax invocation,
        IReadOnlyList<string> originalParams,
        IReadOnlyList<string> orderedNames,
        IReadOnlyList<NewParameter> addedParams,
        Dictionary<string, string>? defaultForRemoved)
    {
        var oldArguments = invocation.ArgumentList.Arguments;
        var byName = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        for (var index = 0; index < oldArguments.Count; index++)
        {
            var argument = oldArguments[index];
            if (argument.NameColon is not null)
                byName[argument.NameColon.Name.Identifier.Text] = argument.Expression;
            else if (index < originalParams.Count)
                byName[originalParams[index]] = argument.Expression;
        }

        var newArguments = new List<ArgumentSyntax>();
        foreach (var name in orderedNames)
        {
            if (byName.TryGetValue(name, out var expression))
                newArguments.Add(Argument(expression));
            else if (defaultForRemoved?.TryGetValue(name, out var text) == true)
                newArguments.Add(Argument(ParseExpression(text)));
            // else: removed parameter with an explicit default — the call omits it.
        }

        foreach (var extra in addedParams)
            newArguments.Add(Argument(ParseExpression(extra.DefaultValue ?? "default")));

        var separated = SeparatedList(newArguments.Select((argument, index) =>
            index == 0 ? argument : argument.WithLeadingTrivia(Space)));

        return invocation.WithArgumentList(invocation.ArgumentList.WithArguments(separated));
    }

    private static async Task<List<string>> ErrorsAsync(Solution solution)
    {
        var errors = new List<string>();
        foreach (var project in solution.Projects)
        {
            if (!project.Language.Equals(LanguageNames.CSharp, StringComparison.Ordinal)) continue;

            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error) continue;

                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                var file = Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? project.Name);
                errors.Add($"{diagnostic.Id} {file}:{line} {diagnostic.GetMessage()}");
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    public sealed class NewParameter
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? DefaultValue { get; set; }
    }
}
