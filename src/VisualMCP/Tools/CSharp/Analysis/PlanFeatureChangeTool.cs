using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class PlanFeatureChangeTool
{
    [McpServerTool, Description(
        "Call this tool when you need to radically change or replace a feature. " +
        "Given a type or member name, it produces a complete, ordered change plan: " +
        "what to understand first, what will break, which files to touch, " +
        "which tests to run, and what to verify afterward. " +
        "Do NOT attempt to build this plan yourself by reading source files — " +
        "this tool uses Roslyn's full semantic model to resolve the entire impact graph " +
        "(callers, implementors, derived types, coverage, metrics, diagnostics) in one pass. " +
        "Requires load_solution first.")]
    public static async Task<object> PlanFeatureChange(
        [Description("Name of the type, interface, method, or property that is the core of the feature to change")] string symbolName,
        [Description("Optional: containing type name to disambiguate (e.g. if the method name is not unique)")] string? containingType = null,
        [Description("Include transitive callers (callers of callers) up to this depth — 0 means direct only (default: 1)")] int callerDepth = 1)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // ── 1. Resolve the target symbol ──────────────────────────────────────
        var allDeclarations = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.All);

        var symbols = allDeclarations
            .Where(s => containingType is null ||
                        (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
            return new { error = $"No symbol named '{symbolName}' found. Check spelling or provide containingType to disambiguate." };

        var target = symbols.Count == 1
            ? symbols[0]
            : symbols.FirstOrDefault(s => s.Kind == SymbolKind.NamedType) ?? symbols[0];

        var targetLoc = target.Locations.FirstOrDefault(l => l.IsInSource);

        // ── 2. Complexity metrics for the target ──────────────────────────────
        var metrics = await GetMetricsAsync(target, solution);

        // ── 3. Direct references across solution ──────────────────────────────
        var allRefs     = await SymbolFinder.FindReferencesAsync(target, solution);
        var refLocations = allRefs
            .SelectMany(r => r.Locations)
            .Select(l => new
            {
                FilePath = l.Document.FilePath,
                Line     = l.Location.GetLineSpan().StartLinePosition.Line + 1,
                Project  = l.Document.Project.Name,
            })
            .OrderBy(r => r.Project).ThenBy(r => r.FilePath).ThenBy(r => r.Line)
            .ToList<object>();

        var affectedProjects = allRefs
            .SelectMany(r => r.Locations)
            .Select(l => l.Document.Project.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // ── 4. Direct callers ─────────────────────────────────────────────────
        var directCallers = new List<CallerInfo>();
        if (target.Kind == SymbolKind.Method || target.Kind == SymbolKind.Property)
        {
            var callerRefs = await SymbolFinder.FindCallersAsync(target, solution);
            foreach (var c in callerRefs.Where(c => c.IsDirect))
            {
                var loc = c.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                directCallers.Add(new CallerInfo(
                    c.CallingSymbol.ToDisplayString(),
                    c.CallingSymbol.ContainingType?.Name ?? "",
                    c.CallingSymbol.ContainingAssembly?.Name ?? "",
                    loc?.SourceTree?.FilePath,
                    loc?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                    IsTestMethod(c.CallingSymbol)));
            }
        }

        // ── 5. Transitive callers (up to callerDepth) ─────────────────────────
        var transitiveLayers = new List<List<object>>();
        if (callerDepth > 0)
        {
            var frontier = directCallers.Select(c => c.FullName).ToHashSet();
            var visited  = new HashSet<string>(frontier) { target.ToDisplayString() };

            for (int depth = 0; depth < callerDepth && frontier.Count > 0; depth++)
            {
                var nextFrontier = new HashSet<string>();
                var layerCallers = new List<object>();

                foreach (var callerName in frontier)
                {
                    var callerSymbols = await SymbolFinder.FindSourceDeclarationsAsync(
                        solution,
                        n => callerName.Contains(n, StringComparison.OrdinalIgnoreCase),
                        SymbolFilter.Member);

                    foreach (var cs in callerSymbols.Take(5)) // limit per node
                    {
                        var upCallers = await SymbolFinder.FindCallersAsync(cs, solution);
                        foreach (var uc in upCallers.Where(u => u.IsDirect))
                        {
                            var display = uc.CallingSymbol.ToDisplayString();
                            if (visited.Contains(display)) continue;
                            visited.Add(display);
                            nextFrontier.Add(display);

                            var loc = uc.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                            layerCallers.Add(new
                            {
                                Caller   = display,
                                FilePath = loc?.SourceTree?.FilePath,
                                Line     = loc?.GetLineSpan().StartLinePosition.Line + 1,
                                IsTest   = IsTestMethod(uc.CallingSymbol),
                            });
                        }
                    }
                }

                if (layerCallers.Count > 0)
                    transitiveLayers.Add(layerCallers);

                frontier = nextFrontier;
            }
        }

        // ── 6. Derived types / implementors ───────────────────────────────────
        var derivedTypes  = new List<object>();
        var implementors  = new List<object>();

        if (target is INamedTypeSymbol namedType)
        {
            IEnumerable<INamedTypeSymbol> derived = namedType.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindDerivedInterfacesAsync(namedType, solution, transitive: true)
                : await SymbolFinder.FindDerivedClassesAsync(namedType, solution, transitive: true);

            derivedTypes = derived.Select(d =>
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                return (object)new
                {
                    Name     = d.ToDisplayString(),
                    Kind     = d.TypeKind.ToString(),
                    FilePath = loc?.SourceTree?.FilePath,
                    Line     = loc?.GetLineSpan().StartLinePosition.Line + 1,
                };
            }).ToList();

            if (namedType.TypeKind == TypeKind.Interface)
            {
                var impls = await SymbolFinder.FindImplementationsAsync(namedType, solution);
                implementors = impls.OfType<INamedTypeSymbol>().Select(i =>
                {
                    var loc = i.Locations.FirstOrDefault(l => l.IsInSource);
                    return (object)new
                    {
                        Name     = i.ToDisplayString(),
                        FilePath = loc?.SourceTree?.FilePath,
                        Line     = loc?.GetLineSpan().StartLinePosition.Line + 1,
                    };
                }).ToList();
            }
        }

        // ── 7. Existing diagnostics on affected files ─────────────────────────
        var affectedFilePaths = refLocations
            .Select(r => ((dynamic)r).FilePath?.ToString())
            .Where(p => p != null)
            .Concat(targetLoc?.SourceTree?.FilePath != null ? [targetLoc.SourceTree.FilePath] : [])
            .Distinct()
            .ToHashSet();

        var diagnostics = new List<object>();
        foreach (var project in solution.Projects.Where(p => affectedProjects.Contains(p.Name)))
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;
            foreach (var d in compilation.GetDiagnostics().Where(d =>
                d.Severity >= DiagnosticSeverity.Warning &&
                d.Location.IsInSource &&
                affectedFilePaths.Contains(d.Location.SourceTree?.FilePath)))
            {
                var loc = d.Location.GetLineSpan();
                diagnostics.Add(new
                {
                    Severity = d.Severity.ToString(),
                    Id       = d.Id,
                    Message  = d.GetMessage(),
                    FilePath = loc.Path,
                    Line     = loc.StartLinePosition.Line + 1,
                });
            }
        }

        // ── 8. Tests that directly call into the affected symbol ──────────────
        var directTestCallers = directCallers.Where(c => c.IsTest).ToList();
        var indirectTests = transitiveLayers
            .SelectMany(l => l)
            .Where(c => ((dynamic)c).IsTest == true)
            .Select(c => (string)(((dynamic)c).Caller?.ToString() ?? ""))
            .Distinct()
            .ToList();

        // ── 9. Assemble the ordered change plan ───────────────────────────────
        var riskScore = ComputeRisk(
            refCount:          refLocations.Count,
            callerCount:       directCallers.Count,
            derivedCount:      derivedTypes.Count + implementors.Count,
            hasTests:          directTestCallers.Count > 0,
            cyclomaticComplexity: metrics?.CyclomaticComplexity ?? 0,
            projectsAffected:  affectedProjects.Count);

        var plan = BuildChangePlan(
            target, targetLoc, metrics, riskScore,
            refLocations, affectedProjects,
            directCallers, transitiveLayers,
            derivedTypes, implementors,
            directTestCallers, indirectTests,
            diagnostics);

        return new
        {
            symbol           = target.ToDisplayString(),
            symbolKind       = target.Kind.ToString(),
            filePath         = targetLoc?.SourceTree?.FilePath,
            line             = targetLoc?.GetLineSpan().StartLinePosition.Line + 1,
            riskScore,
            riskLabel        = RiskLabel(riskScore),
            metrics,
            impactSummary    = new
            {
                directReferenceCount  = refLocations.Count,
                directCallerCount     = directCallers.Count,
                transitiveCallerCount = transitiveLayers.Sum(l => l.Count),
                derivedTypeCount      = derivedTypes.Count,
                implementorCount      = implementors.Count,
                affectedProjectCount  = affectedProjects.Count,
                directTestCount       = directTestCallers.Count,
                indirectTestCount     = indirectTests.Count,
                existingDiagnostics   = diagnostics.Count,
            },
            changePlan       = plan,
        };
    }

    // ── Plan builder ──────────────────────────────────────────────────────────

    private static List<object> BuildChangePlan(
        ISymbol target,
        Location? targetLoc,
        MethodMetrics? metrics,
        int riskScore,
        List<object> refs,
        List<string> affectedProjects,
        List<CallerInfo> directCallers,
        List<List<object>> transitiveLayers,
        List<object> derivedTypes,
        List<object> implementors,
        List<CallerInfo> directTests,
        List<string> indirectTests,
        List<object> diagnostics)
    {
        var steps = new List<object>();
        int step  = 1;

        // ── Phase 0: Understand ───────────────────────────────────────────────
        steps.Add(Phase("0 – Understand the current state", step++, "mandatory",
            $"Read the full implementation of '{target.ToDisplayString()}' at {targetLoc?.SourceTree?.FilePath}:{targetLoc?.GetLineSpan().StartLinePosition.Line + 1}.",
            hint: metrics is not null
                ? $"Complexity: CC={metrics.CyclomaticComplexity}, LOC={metrics.LinesOfCode}, Depth={metrics.MaxNestingDepth}."
                : null));

        if (diagnostics.Count > 0)
            steps.Add(Phase("0 – Fix pre-existing diagnostics first", step++, "mandatory",
                $"There are {diagnostics.Count} warnings/errors in the affected files. Fix them before changing anything — they will otherwise make it impossible to know whether new errors came from your change.",
                data: diagnostics));

        // ── Phase 1: Safety net ───────────────────────────────────────────────
        if (directTests.Count == 0 && indirectTests.Count == 0)
            steps.Add(Phase("1 – Create a safety net (no tests found)", step++, "mandatory",
                $"No tests currently exercise '{target.Name}'. Write characterisation tests that capture the existing behaviour before changing anything. " +
                "Without a safety net a radical change has no feedback signal."));
        else
        {
            var testNames = directTests.Select(t => t.FullName)
                .Concat(indirectTests)
                .Distinct().Take(10).ToList();
            steps.Add(Phase("1 – Run baseline tests and record current output", step++, "mandatory",
                $"Run the {directTests.Count + indirectTests.Count} tests that currently cover this feature to establish a green baseline before touching anything.",
                data: testNames.Cast<object>().ToList()));
        }

        // ── Phase 2: Contract / interface ─────────────────────────────────────
        if (implementors.Count > 0)
            steps.Add(Phase("2 – Update the interface contract", step++, "mandatory",
                $"'{target.Name}' is an interface with {implementors.Count} implementor(s). " +
                "Define the new contract on the interface first, then update each implementor. " +
                "Consider creating a new interface and keeping the old one temporarily to avoid a big-bang change.",
                data: implementors.Take(20).ToList()));

        if (derivedTypes.Count > 0)
            steps.Add(Phase("2 – Handle derived types", step++, "mandatory",
                $"{derivedTypes.Count} type(s) derive from '{target.Name}'. " +
                "Decide for each whether to inherit the new behaviour, override it, or replace it entirely.",
                data: derivedTypes.Take(20).ToList()));

        // ── Phase 3: Change the core ──────────────────────────────────────────
        steps.Add(Phase("3 – Implement the change in the core symbol", step++, "mandatory",
            $"Change '{target.ToDisplayString()}' at {targetLoc?.SourceTree?.FilePath}. " +
            "Keep the old implementation commented or behind a feature flag until all callers are updated."));

        // ── Phase 4: Update callers ───────────────────────────────────────────
        if (directCallers.Count > 0)
        {
            var callerFiles = directCallers
                .Where(c => !c.IsTest)
                .Select(c => c.FilePath)
                .Distinct()
                .ToList();

            steps.Add(Phase("4 – Update direct callers", step++, "mandatory",
                $"Update the {directCallers.Count(c => !c.IsTest)} non-test caller(s) spread across {callerFiles.Count} file(s). " +
                "Work file by file. Build after each file to catch regressions early.",
                data: directCallers.Where(c => !c.IsTest)
                    .Select(c => new { c.FullName, c.FilePath, c.Line })
                    .Cast<object>().ToList()));
        }

        if (transitiveLayers.Count > 0)
        {
            var transitiveCount = transitiveLayers.Sum(l => l.Count);
            steps.Add(Phase("4 – Review transitive callers", step++, riskScore >= 6 ? "mandatory" : "recommended",
                $"{transitiveCount} transitive caller(s) found {transitiveLayers.Count} level(s) up the call stack. " +
                "These may need adapting if the change alters return types, exceptions, or semantics.",
                data: transitiveLayers.SelectMany(l => l).Take(30).ToList()));
        }

        // ── Phase 5: References ───────────────────────────────────────────────
        if (refs.Count > directCallers.Count)
        {
            var nonCallerRefs = refs.Count - directCallers.Count;
            steps.Add(Phase("5 – Review non-call references", step++, "recommended",
                $"{nonCallerRefs} reference(s) that are not direct calls (field assignments, typeof, attribute usage, reflection strings, etc.) " +
                "may also need updating.",
                data: refs.Take(30).ToList()));
        }

        // ── Phase 6: Projects ─────────────────────────────────────────────────
        if (affectedProjects.Count > 1)
            steps.Add(Phase("5 – Build each affected project in dependency order", step++, "mandatory",
                $"The change spans {affectedProjects.Count} projects: {string.Join(", ", affectedProjects)}. " +
                "Build them in dependency order (leaves first) and fix errors before moving to the next project.",
                data: affectedProjects.Cast<object>().ToList()));

        // ── Phase 7: Tests ────────────────────────────────────────────────────
        steps.Add(Phase("6 – Update and run tests", step++, "mandatory",
            directTests.Count > 0
                ? $"Update the {directTests.Count} test(s) that directly target this feature, then run the full suite. " +
                  "If tests reveal unexpected breakage, do not suppress them — fix the implementation."
                : "Write new tests for the changed behaviour, covering the happy path, edge cases, and error conditions. Then run the full suite.",
            data: directTests.Select(t => new { t.FullName, t.FilePath, t.Line }).Cast<object>().Take(20).ToList()));

        // ── Phase 8: Quality check ────────────────────────────────────────────
        steps.Add(Phase("7 – Run quality checks on changed files", step++, "recommended",
            "After the change is green: run get_diagnostics, find_code_smells, and get_metrics on the modified files. " +
            "The new implementation should not regress on cyclomatic complexity or introduce new smells."));

        // ── Phase 9: Documentation ────────────────────────────────────────────
        steps.Add(Phase("8 – Update documentation", step++, "recommended",
            $"Update XML doc comments on '{target.Name}' and any public-API callers whose behaviour has changed. " +
            "Run find_undocumented_public_api to catch gaps."));

        return steps;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object Phase(string title, int stepNumber, string priority, string description,
        string? hint = null, List<object>? data = null) => new
    {
        Step        = stepNumber,
        Title       = title,
        Priority    = priority,
        Description = description,
        Hint        = hint,
        Data        = data,
    };

    private static int ComputeRisk(int refCount, int callerCount, int derivedCount,
        bool hasTests, int cyclomaticComplexity, int projectsAffected)
    {
        int score = 0;
        if (refCount      > 20)  score += 2;
        else if (refCount > 5)   score += 1;
        if (callerCount   > 10)  score += 2;
        else if (callerCount > 2) score += 1;
        if (derivedCount  > 5)   score += 2;
        else if (derivedCount > 0) score += 1;
        if (!hasTests)            score += 2;
        if (cyclomaticComplexity > 15) score += 2;
        else if (cyclomaticComplexity > 8) score += 1;
        if (projectsAffected > 3) score += 2;
        else if (projectsAffected > 1) score += 1;
        return Math.Min(score, 10);
    }

    private static string RiskLabel(int score) => score switch
    {
        >= 8 => "Critical — very high blast radius, proceed with extreme care",
        >= 6 => "High — significant impact, plan carefully and keep changes incremental",
        >= 4 => "Medium — moderate impact, follow all mandatory steps",
        >= 2 => "Low — limited impact, standard change process applies",
        _    => "Minimal — isolated change, low risk",
    };

    private static bool IsTestMethod(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol m) return false;
        return m.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.Name ?? "";
            return name is "FactAttribute" or "TheoryAttribute" or "TestAttribute"
                       or "TestMethodAttribute" or "TestCaseAttribute"
                       or "Fact" or "Theory" or "Test" or "TestMethod" or "TestCase";
        });
    }

    private static async Task<MethodMetrics?> GetMetricsAsync(ISymbol symbol, Solution solution)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) return null;

        var doc = solution.GetDocument(loc.SourceTree);
        if (doc is null) return null;

        var root  = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        if (root is null || model is null) return null;

        // Find the method node at the target location
        var node = root.FindNode(loc.SourceSpan);
        var method = node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
                  ?? node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault() as SyntaxNode;

        if (method is null) return null;

        int cc    = CyclomaticComplexity(method);
        var span  = method.GetLocation().GetLineSpan();
        int loc2  = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
        int depth = MaxNestingDepth(method);

        return new MethodMetrics(cc, loc2, depth);
    }

    private static int CyclomaticComplexity(SyntaxNode node)
    {
        int cc = 1;
        foreach (var n in node.DescendantNodes())
        {
            cc += n switch
            {
                IfStatementSyntax                                                        => 1,
                ForStatementSyntax                                                       => 1,
                ForEachStatementSyntax                                                   => 1,
                WhileStatementSyntax                                                     => 1,
                DoStatementSyntax                                                        => 1,
                CaseSwitchLabelSyntax                                                    => 1,
                CasePatternSwitchLabelSyntax                                             => 1,
                SwitchExpressionArmSyntax                                                => 1,
                CatchClauseSyntax                                                        => 1,
                ConditionalExpressionSyntax                                              => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression)  => 1,
                _                                                                        => 0,
            };
        }
        return cc;
    }

    private static int MaxNestingDepth(SyntaxNode node)
    {
        int max = 0;
        void Walk(SyntaxNode n, int d)
        {
            if (d > max) max = d;
            foreach (var c in n.ChildNodes())
                Walk(c, c is BlockSyntax or SwitchSectionSyntax ? d + 1 : d);
        }
        Walk(node, 0);
        return max;
    }

    private record CallerInfo(
        string FullName,
        string ContainingType,
        string Assembly,
        string? FilePath,
        int Line,
        bool IsTest);

    private record MethodMetrics(
        int CyclomaticComplexity,
        int LinesOfCode,
        int MaxNestingDepth);
}
