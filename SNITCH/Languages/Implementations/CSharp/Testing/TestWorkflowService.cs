using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// The part of the test workflow that running a suite does not give: run ONE test or class without
/// hand-building a <c>--filter</c>, re-run exactly the tests that failed last time, know which tests
/// reach a symbol, and scaffold a test class that compiles. Results are structured (TRX), and the
/// last run is kept in <c>.visualMCP/last-test-run.json</c> so "re-run the failures" needs no
/// argument.
/// </summary>
public interface ITestWorkflowService
{
    Task<object> RunTestAsync(string? testName, string? className, string? projectName, string? filter,
        int timeoutSeconds);

    Task<object> RerunFailedAsync(string? projectName, int timeoutSeconds);

    Task<object> TestsCoveringSymbolAsync(string symbolName, string? containingType, int maxResults);

    Task<object> GenerateTestsAsync(string typeName, string? projectName, string? testProjectName,
        string? className, bool dryRun, string? memberName = null);

    /// <summary>Finds the tests that reference a symbol and runs exactly those.</summary>
    Task<object> RunCoveringTestsAsync(string symbolName, string? containingType, string? projectName,
        int timeoutSeconds);

    /// <summary>Which tests a change can affect: the tests that reference the symbols declared by the files git says changed.</summary>
    Task<object> ImpactAnalysisAsync(string? baseRef, string? projectName, int maxResults);
}

public sealed class TestWorkflowService : ITestWorkflowService
{
    private static readonly string[] TestFrameworkMarkers = ["xunit", "nunit", "mstest"];

    private readonly IRoslynWorkspace _workspace;
    public TestWorkflowService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── running one test / re-running the failures ──────────────────────────
    public async Task<object> RunTestAsync(string? testName, string? className, string? projectName,
        string? filter, int timeoutSeconds = 300)
    {
        if (string.IsNullOrWhiteSpace(testName) && string.IsNullOrWhiteSpace(className) &&
            string.IsNullOrWhiteSpace(filter))
            return new
            {
                error = "Pass testName, className or a raw filter.",
                examples = new[] { "testName=Fully.Qualified.Name", "className=MyTests", "filter=FullyQualifiedName~Parser" },
            };

        string composed;
        if (!string.IsNullOrWhiteSpace(filter))
            composed = filter.Trim();
        else if (!string.IsNullOrWhiteSpace(testName))
            composed = testName!.Contains('.')
                ? $"FullyQualifiedName={testName.Trim()}"
                : $"FullyQualifiedName~{testName.Trim()}";
        else
            composed = $"FullyQualifiedName~{className!.Trim()}";

        return await RunAsync(projectName, composed, timeoutSeconds, why: $"filter {composed}");
    }

    public async Task<object> RerunFailedAsync(string? projectName, int timeoutSeconds = 300)
    {
        var directory = SolutionDirectory();
        var record    = TestRunRecordStore.Read(directory);
        var failures  = record?.AllFailures().ToList() ?? [];

        if (failures.Count == 0)
            return new
            {
                error = "No failed tests are recorded: run_test (or run_tests) has not produced a failure since the record was last written.",
                record = TestRunRecordStore.PathFor(directory),
            };

        // An explicit project re-runs every recorded failure there; otherwise each project re-runs its own.
        if (!string.IsNullOrWhiteSpace(projectName))
            return await RunAsync(projectName, RunTestsService.ComposeFailureFilter(failures.Select(entry => entry.Test)),
                timeoutSeconds, why: $"re-running {failures.Count} recorded failure(s)");

        var groups = record!.Projects.Where(group => group.Failed.Count > 0).ToList();
        if (groups.Count == 1)
            return await RunAsync(groups[0].Project, RunTestsService.ComposeFailureFilter(groups[0].Failed),
                timeoutSeconds, why: $"re-running {groups[0].Failed.Count} recorded failure(s)");

        var runs = new List<object>();
        foreach (var group in groups)
            runs.Add(await RunAsync(group.Project, RunTestsService.ComposeFailureFilter(group.Failed),
                timeoutSeconds, why: $"re-running {group.Failed.Count} recorded failure(s)"));

        return new
        {
            verdict = "ran",
            projects = runs,
            record = TestRunRecordStore.PathFor(directory),
            note = "Each project re-runs the failures it recorded.",
        };
    }

    private async Task<object> RunAsync(string? projectName, string filter, int timeoutSeconds, string why)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var project = ResolveTestProject(solution, projectName, out var error);
        if (project is null) return new { error };

        var directory = Path.GetDirectoryName(project.FilePath!)!;
        var results = Path.Combine(Path.GetTempPath(), $"vmcp-trx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);

        try
        {
            var arguments = new List<string>
            {
                "test", project.FilePath!,
                "--filter", filter,
                "--logger", "trx;LogFileName=run.trx",
                "--results-directory", results,
            };

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            try { process.StandardInput.Close(); } catch { /* EOF for the child */ }

            var timeout = Math.Clamp(timeoutSeconds, 10, 1800);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);

            var timedOut = false;
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            var output = timedOut ? string.Empty : SafeAwait(stdout);
            var errors = timedOut ? string.Empty : SafeAwait(stderr);

            var trx = Directory.Exists(results)
                ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).FirstOrDefault()
                : null;

            var resultsList = trx is null ? (Ran: false, Passed: 0, Failed: new List<string>(), Messages: new List<object>())
                : ParseTrx(trx);

            if (resultsList.Failed.Count > 0 || resultsList.Passed > 0)
                WriteRecord(project.Name, resultsList.Failed);

            return new
            {
                project = project.Name,
                filter,
                why,
                timedOut,
                exitCode = timedOut ? (int?)null : SafeExit(process),
                succeeded = !timedOut && resultsList.Failed.Count == 0 && resultsList.Passed > 0,
                ran = resultsList.Ran,
                passed = resultsList.Passed,
                failed = resultsList.Failed.Count,
                failedTests = resultsList.Failed,
                failureMessages = resultsList.Messages,
                record = TestRunRecordStore.PathFor(SolutionDirectory()),
                output = Trim(timedOut ? "(timed out; the run was stopped)" : output, 4000),
                stderr = Trim(errors, 2000),
                note = resultsList.Ran
                    ? "The failed tests of this run are recorded: rerun_failed re-runs exactly them."
                    : "No TRX result was produced (the filter matched nothing, or the project is not a test project).",
            };
        }
        finally
        {
            try { Directory.Delete(results, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── which tests reach a symbol ──────────────────────────────────────────
    public async Task<object> TestsCoveringSymbolAsync(string symbolName, string? containingType, int maxResults = 200)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return new { error = "A symbol name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var candidates = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindSourceDeclarationsAsync(
            solution, name => name.Equals(symbolName, StringComparison.Ordinal), SymbolFilter.All);

        var symbols = candidates
            .Where(symbol => containingType is null ||
                             (symbol.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0) return new { error = $"Symbol '{symbolName}' not found." };
        if (symbols.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {symbols.Count} symbols named '{symbolName}'. Pass containingType.",
                candidates = symbols.Select(symbol => symbol.ToDisplayString()).Take(10).ToList(),
            };

        var symbol = symbols[0];
        var tests = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution))
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                var document = location.Document;
                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                var method = root.FindToken(location.Location.SourceSpan.Start).Parent?
                    .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (method is null) continue;

                if (!IsTestMethod(method)) continue;

                var model = await document.GetSemanticModelAsync();
                var declared = model?.GetDeclaredSymbol(method);
                var name = declared?.ToDisplayString() ?? method.Identifier.ValueText;
                if (!seen.Add(name)) continue;

                tests.Add(new
                {
                    test = name,
                    kind = method.AttributeLists.SelectMany(list => list.Attributes)
                        .Select(attribute => attribute.Name.ToString().Split('.').Last())
                        .FirstOrDefault(attribute => attribute is "Fact" or "Theory" or "Test" or "TestCase" or "DataTestMethod"),
                    file = document.FilePath,
                    line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                });
            }
        }

        return new
        {
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            coveringTests = tests.Take(Math.Clamp(maxResults, 1, 1000)).ToList(),
            count = tests.Count,
            note = "Direct references from test methods. A test that reaches the symbol through another " +
                   "method, a fixture or reflection is not traced here; get_test_coverage_map says whether " +
                   "the lines are covered at all.",
        };
    }

    /// <summary>
    /// The covering tests of a symbol, run in one call: <c>tests_covering_symbol</c> names them and
    /// <c>run_test</c> runs a filter, so the two are composed here instead of by the caller.
    /// </summary>
    public async Task<object> RunCoveringTestsAsync(string symbolName, string? containingType,
        string? projectName = null, int timeoutSeconds = 300)
    {
        var covering = await TestsCoveringSymbolAsync(symbolName, containingType, 200);

        var json = System.Text.Json.JsonSerializer.SerializeToNode(covering);
        if (json is System.Text.Json.Nodes.JsonObject failure && failure["error"] is not null)
            return covering;

        var names = (json?["tests"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(entry => entry?["test"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

        if (names.Count == 0)
            return new { error = $"No test references '{symbolName}': there is nothing to run.", symbol = symbolName };

        // The display name carries the parameter list; vstest matches the name without it.
        var filter = string.Join("|", names
            .Select(name => name.Split('(')[0])
            .Distinct(StringComparer.Ordinal)
            .Select(name => "FullyQualifiedName~" + name));

        var run = await RunTestAsync(null, null, projectName, filter, timeoutSeconds);

        return new
        {
            symbol = symbolName,
            tests = names,
            filter,
            run,
            note = "The filter is built from the tests that reference the symbol, so a run whose filter matches nothing means the naming changed.",
        };
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Split('.').Last() is
                "Fact" or "Theory" or "Test" or "TestCase" or "DataTestMethod" or "TestMethod");

    // ── scaffolding a test class ────────────────────────────────────────────
    public async Task<object> GenerateTestsAsync(string typeName, string? projectName = null,
        string? testProjectName = null, string? className = null, bool dryRun = false,
        string? memberName = null)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "A type name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var type = await FindTypeAsync(solution, typeName, projectName);
        if (type is null) return new { error = $"Type '{typeName}' not found{((projectName is null) ? "" : $" in project '{projectName}'")}." };

        var testProject = ResolveTestProject(solution, testProjectName, out var projectError);
        if (testProject is null) return new { error = projectError };

        var name = string.IsNullOrWhiteSpace(className) ? type.Name + "Tests" : className!;
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
            return new { error = $"'{name}' is not a valid class name." };

        if (type.ContainingNamespace is { IsGlobalNamespace: false } containing)
        {
            var existing = type.ContainingNamespace.ToDisplayString() + "." + name;
            if (solution.Projects.SelectMany(project => project.Documents)
                    .Any(document => document.Name.Equals(name + ".cs", StringComparison.OrdinalIgnoreCase)))
                return new { error = $"A file named {name}.cs already exists in the solution; pass className." };

            existing = existing.TrimEnd('.');
        }

        var directory = Path.GetDirectoryName(testProject.FilePath!)!;
        var filePath = Path.Combine(directory, name + ".cs");
        if (File.Exists(filePath)) return new { error = $"{filePath} already exists; pass className for another name." };

        var namespaceName = (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
            ? ns.ToDisplayString() + ".Tests"
            : "Tests";

        var source = BuildTestClass(type, name, namespaceName, memberName);

        // ── Gate: the new file must compile inside the test project ─────────────────────
        var updated = testProject.AddDocument(name + ".cs", Microsoft.CodeAnalysis.Text.SourceText.From(source), filePath: filePath);
        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, updated.Project.Solution, [testProject.Id]);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"the generated {name} test class");

        if (!dryRun)
        {
            await File.WriteAllTextAsync(filePath, source);
            _workspace.TryAddDocument(testProject.Id, name + ".cs", filePath,
                Microsoft.CodeAnalysis.Text.SourceText.From(source));
        }

        return new
        {
            generated = name,
            type = type.ToDisplayString(),
            testProject = testProject.Name,
            file = filePath,
            tests = type.GetMembers().OfType<IMethodSymbol>()
                .Count(method => method.MethodKind == MethodKind.Ordinary &&
                                 method.DeclaredAccessibility == Accessibility.Public &&
                                 !method.IsStatic && !method.IsGenericMethod),
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file. The bodies are scaffolds — fill in Arrange/Assert."
                : "File written. The bodies compile but assert nothing yet: they mark what to test.",
        };
    }

    private static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string typeName, string? projectName)
    {
        foreach (var project in solution.Projects)
        {
            if (projectName is not null && !project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                continue;

            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            var type = compilation.GetTypeByMetadataName(typeName);
            if (type is not null) return type;

            foreach (var candidate in AllTypes(compilation.GlobalNamespace))
                if (candidate.Name == typeName || candidate.ToDisplayString() == typeName)
                    return candidate;
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var nested in AllTypes(child)) yield return nested;
                continue;
            }

            if (member is not INamedTypeSymbol type) continue;

            yield return type;
            foreach (var nested in type.GetTypeMembers()) yield return nested;
        }
    }

    private static string BuildTestClass(INamedTypeSymbol type, string className, string namespaceName,
        string? memberName = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using Xunit;");
        builder.AppendLine();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {className}");
        builder.AppendLine("{");

        var constructor = type.InstanceConstructors.FirstOrDefault(candidate =>
            candidate.Parameters.Length == 0 && candidate.DeclaredAccessibility == Accessibility.Public);

        var typeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var methods = type.GetMembers().OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Ordinary &&
                             method.DeclaredAccessibility == Accessibility.Public &&
                             !method.IsStatic && !method.IsGenericMethod &&
                             (memberName is null || string.Equals(method.Name, memberName, StringComparison.Ordinal)))
            .ToList();

        if (methods.Count == 0)
        {
            builder.AppendLine("    [Fact]");
            builder.AppendLine("    public void Type_HasSomethingWorthTesting()");
            builder.AppendLine("    {");
            builder.AppendLine("        // TODO: this type exposes no public instance methods.");
            builder.AppendLine("        Assert.True(true);");
            builder.AppendLine("    }");
        }

        foreach (var method in methods)
        {
            var testName = $"{method.Name}_WhenGivenInput_DoesSomething";
            builder.AppendLine($"    [Fact]");
            builder.AppendLine($"    public void {testName}()");
            builder.AppendLine("    {");
            builder.AppendLine("        // Arrange");
            builder.AppendLine(constructor is null
                ? $"        var sut = default({typeName})!; // TODO: no public parameterless constructor"
                : $"        var sut = new {typeName}();");
            builder.AppendLine();
            builder.AppendLine("        // Act");

            var arguments = string.Join(", ", method.Parameters.Select(parameter => "default!"));
            builder.AppendLine(method.ReturnsVoid
                ? $"        sut.{method.Name}({arguments});"
                : $"        var result = sut.{method.Name}({arguments});");

            builder.AppendLine();
            builder.AppendLine("        // Assert");
            builder.AppendLine(method.ReturnsVoid
                ? "        Assert.True(true); // TODO: assert the effect"
                : method.ReturnType.IsValueType
                    ? "        Assert.NotEqual(default, result); // TODO: assert the value"
                    : "        Assert.NotNull(result); // TODO: assert the value");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        if (builder[^1] == '\n' && builder.Length > 1)
            builder.Length -= Environment.NewLine.Length;

        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Test impact analysis, the cheap half of it: git says which files changed, the symbols those
    /// files declare are looked up, and the test methods that reference them are the tests worth
    /// running. It names them and prints the filter; it does not run them.
    /// </summary>
    public async Task<object> ImpactAnalysisAsync(string? baseRef = null, string? projectName = null,
        int maxResults = 200)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var directory = SolutionDirectory();
        if (directory is null) return new { error = "The solution has no folder on disk." };

        var reference = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef!.Trim();
        var (exitCode, timedOut, stdout, stderr, _) =
            await Snitch.Execution.ProcessRunner.RunAsync("git", $"diff --name-only {reference}", directory, 60);

        if (timedOut) return new { error = $"git diff timed out for '{reference}'.", baseRef = reference };
        if (exitCode != 0) return new { error = $"git diff failed: {stderr.Trim()}", baseRef = reference };

        var changed = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        maxResults = Math.Clamp(maxResults, 1, 1000);
        var tests = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scannedSymbols = 0;
        var missingDocuments = new List<string>();

        foreach (var relative in changed)
        {
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            var document = solution.Projects.SelectMany(project => project.Documents)
                .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                             string.Equals(Path.GetFullPath(candidate.FilePath), full, StringComparison.OrdinalIgnoreCase));

            if (document is null)
            {
                missingDocuments.Add(relative);
                continue;
            }

            var model = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            if (model is null || root is null) continue;

            var declarations = root.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .Where(member => member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .Take(50)
                .ToList();

            foreach (var declaration in declarations)
            {
                var symbol = model.GetDeclaredSymbol(declaration);
                if (symbol is null) continue;
                scannedSymbols++;

                foreach (var found in await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution))
                {
                    foreach (var location in found.Locations.Where(location => location.Location.IsInSource))
                    {
                        var siteRoot = await location.Document.GetSyntaxRootAsync();
                        var method = siteRoot?.FindToken(location.Location.SourceSpan.Start).Parent?
                            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                        if (method is null || !IsTestMethod(method)) continue;

                        var name = method.Identifier.ValueText;
                        if (!seen.Add(name)) continue;

                        tests.Add(new
                        {
                            test = name,
                            project = location.Document.Project.Name,
                            file = location.Document.FilePath,
                            line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            via = symbol.Name,
                        });

                        if (tests.Count >= maxResults) break;
                    }

                    if (tests.Count >= maxResults) break;
                }

                if (tests.Count >= maxResults) break;
            }

            if (tests.Count >= maxResults) break;
        }

        var filter = string.Join("|", tests
            .Select(test => ((dynamic)test).test?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"FullyQualifiedName~{name}"));

        return new
        {
            baseRef = reference,
            changedFiles = changed.Count,
            changedCSharpFiles = changed,
            notInSolution = missingDocuments.Count > 0 ? missingDocuments : null,
            symbolsScanned = scannedSymbols,
            coveringTests = tests,
            count = tests.Count,
            runCommand = filter.Length == 0 ? null : $"run_test(filter: \"{filter}\")",
            note = "Direct references only: a test that reaches the change through another method or " +
                   "reflection is not listed. Uncommitted work counts as changed against the reference.",
        };
    }

    // ── plumbing ────────────────────────────────────────────────────────────
    private static Project? ResolveTestProject(Solution solution, string? projectName, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = solution.Projects.FirstOrDefault(project =>
                project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

            if (named is null)
            {
                error = $"Project '{projectName}' not found in the solution.";
                return null;
            }

            return named;
        }

        var testProjects = solution.Projects
            .Where(project => project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                              (project.FilePath is not null &&
                               File.Exists(project.FilePath) &&
                               TestFrameworkMarkers.Any(marker =>
                                   File.ReadAllText(project.FilePath).Contains(marker, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        if (testProjects.Count == 1) return testProjects[0];

        error = testProjects.Count == 0
            ? "No test project found in the solution (a name containing 'Test', or a project referencing xunit/nunit/mstest)."
            : $"Several test projects; pass projectName. Candidates: {string.Join(", ", testProjects.Select(p => p.Name))}";
        return null;
    }

    private string? SolutionDirectory()
    {
        var solution = _workspace.CurrentSolution;
        var path = solution?.FilePath ?? _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetDirectoryName(path);

        return solution?.Projects.Select(project => project.FilePath)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => Path.GetDirectoryName(file!))
            .FirstOrDefault();
    }

    private void WriteRecord(string project, List<string> failed) =>
        TestRunRecordStore.Write(SolutionDirectory(), [new FailedTestGroup(project, failed)]);

    private static (bool Ran, int Passed, List<string> Failed, List<object> Messages) ParseTrx(string path)
    {
        try
        {
            var document = XDocument.Load(path);
            XNamespace ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var definitions = document.Descendants(ns + "UnitTest")
                .Select(element => element.Attribute("id")?.Value)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal);

            var passed = 0;
            var failed = new List<string>();
            var messages = new List<object>();

            foreach (var result in document.Descendants(ns + "UnitTestResult"))
            {
                var outcome = result.Attribute("outcome")?.Value ?? string.Empty;
                var name = result.Attribute("testName")?.Value ?? "(unknown)";

                if (outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)) passed++;
                else if (outcome is "Failed" or "Error" or "Timeout" or "Aborted")
                {
                    failed.Add(name);
                    if (messages.Count < 5)
                    {
                        var message = result.Descendants(ns + "Message").FirstOrDefault()?.Value;
                        messages.Add(new { test = name, message = Trim(message ?? "(no message)", 500) });
                    }
                }
            }

            return (definitions.Count > 0 || passed > 0 || failed.Count > 0, passed, failed, messages);
        }
        catch
        {
            return (false, 0, new List<string>(), new List<object>());
        }
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…(truncated)";

    private static string SafeAwait(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;

    private static int SafeExit(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }
}
