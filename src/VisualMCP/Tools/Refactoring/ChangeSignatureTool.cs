using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ChangeSignatureTool
{
    [McpServerTool, Description(
        "Change a method's parameter list: reorder, remove, or add parameters. " +
        "Updates the declaration and all call sites in the solution. " +
        "Equivalent to ReSharper 'Change Signature'. Requires LoadSolution first.\n\n" +
        "newParameterOrder: array of existing parameter names in the desired order. " +
        "Omit a name to remove it (only safe if the parameter has a default value or you also supply a defaultForRemoved). " +
        "additionalParams: array of objects {name, type, defaultValue} to append.")]
    public static async Task<object> ChangeSignature(
        [Description("Method name to modify")] string methodName,
        [Description("Containing type name to disambiguate")] string? containingType = null,
        [Description("New order of existing parameters â€” array of parameter names, e.g. [\"b\",\"a\"]. Omit a name to remove it.")] string[]? newParameterOrder = null,
        [Description("New parameters to add at the end â€” array of JSON objects with fields: name, type, defaultValue (optional). E.g. [{\"name\":\"timeout\",\"type\":\"int\",\"defaultValue\":\"30\"}]")] NewParameter[]? additionalParams = null,
        [Description("For removed required parameters: literal expression to insert at every call site, keyed by parameter name. E.g. {\"count\": \"0\"}")] Dictionary<string, string>? defaultForRemoved = null,
        [Description("Dry run â€” show changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

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

        // Build the target parameter sequence
        var orderedNames = newParameterOrder?.ToList() ?? originalParams;
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

        // â”€â”€ Locate the declaration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var declLoc = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLoc is null)
            return new { error = "Method has no source location." };

        var tree = declLoc.SourceTree!;
        var sourceDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tree);
        if (sourceDoc?.FilePath is null)
            return new { error = "Could not locate source document." };

        var root = await sourceDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null) return new { error = "Could not parse source file." };

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.Ordinal) &&
                                 m.FullSpan.Contains(declLoc.SourceSpan.Start));
        if (methodNode is null)
            return new { error = "Could not locate method declaration node." };

        // Build new parameter list for the declaration
        var oldParams = methodNode.ParameterList.Parameters;
        var paramByName = oldParams.ToDictionary(p => p.Identifier.Text);

        var newParamNodes = new List<ParameterSyntax>();
        foreach (var name in orderedNames)
        {
            if (paramByName.TryGetValue(name, out var pn))
                newParamNodes.Add(pn);
        }

        // Append additional parameters
        foreach (var extra in additionalParams ?? [])
        {
            var pText = extra.DefaultValue is not null
                ? $"{extra.Type} {extra.Name} = {extra.DefaultValue}"
                : $"{extra.Type} {extra.Name}";
            var parsed = SyntaxFactory.ParseParameterList($"({pText})").Parameters[0];
            newParamNodes.Add(parsed);
        }

        var newParamList = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(newParamNodes));

        var newMethodNode = methodNode.WithParameterList(newParamList);
        var newRoot = root.ReplaceNode(methodNode, newMethodNode);

        var filesToWrite = new Dictionary<string, string>
        {
            [sourceDoc.FilePath] = newRoot.ToFullString()
        };

        // â”€â”€ Update call sites â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var refs = await SymbolFinder.FindReferencesAsync(method, solution);
        var callSiteCount = 0;

        foreach (var group in refs.SelectMany(r => r.Locations)
                                  .GroupBy(l => l.Document.FilePath))
        {
            var docPath = group.Key;
            if (docPath is null) continue;

            var refDoc = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == docPath);
            if (refDoc is null) continue;

            var refRoot = await refDoc.GetSyntaxRootAsync();
            if (refRoot is null) continue;

            // Collect invocations at these locations, sort descending
            var invocations = group
                .Select(loc =>
                {
                    var node = refRoot.FindNode(loc.Location.SourceSpan);
                    return node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                })
                .Where(inv => inv is not null)
                .Cast<InvocationExpressionSyntax>()
                .Distinct()
                .OrderByDescending(inv => inv.SpanStart)
                .ToList();

            if (invocations.Count == 0) continue;

            var text = filesToWrite.TryGetValue(docPath, out var existing)
                ? existing
                : (await refDoc.GetTextAsync()).ToString();

            foreach (var inv in invocations)
            {
                var oldArgs = inv.ArgumentList.Arguments;
                // Map old positional args to param names
                var namedArgs = new Dictionary<string, string>();
                for (int i = 0; i < oldArgs.Count; i++)
                {
                    var arg = oldArgs[i];
                    if (arg.NameColon is not null)
                        namedArgs[arg.NameColon.Name.Identifier.Text] = arg.Expression.ToString();
                    else if (i < originalParams.Count)
                        namedArgs[originalParams[i]] = arg.Expression.ToString();
                }

                // Build new argument list following new order
                var newArgStrings = new List<string>();
                foreach (var name in orderedNames)
                {
                    if (namedArgs.TryGetValue(name, out var val))
                        newArgStrings.Add(val);
                    else if (defaultForRemoved?.TryGetValue(name, out var def) == true)
                        newArgStrings.Add(def!);
                    // else: removed param with explicit default â€” skip it
                }

                foreach (var extra in additionalParams ?? [])
                {
                    newArgStrings.Add(extra.DefaultValue ?? "default");
                }

                var newArgList = string.Join(", ", newArgStrings);
                var invSpan = inv.ArgumentList.Span;
                text = text[..invSpan.Start] + $"({newArgList})" + text[(invSpan.Start + invSpan.Length)..];
                callSiteCount++;
            }

            filesToWrite[docPath] = text;
        }

        if (dryRun)
            return new
            {
                dryRun = true,
                method = method.ToDisplayString(),
                originalParams,
                newParams = orderedNames.Concat(additionalParams?.Select(p => p.Name) ?? []).ToList(),
                removed = removedNames.ToList(),
                callSitesAffected = callSiteCount,
            };

        foreach (var (path, text) in filesToWrite)
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            method = method.ToDisplayString(),
            originalParams,
            newParams = orderedNames.Concat(additionalParams?.Select(p => p.Name) ?? []).ToList(),
            removed = removedNames.ToList(),
            callSitesUpdated = callSiteCount,
            filesModified = filesToWrite.Count,
        };
    }

    public sealed class NewParameter
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? DefaultValue { get; set; }
    }
}
