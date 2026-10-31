using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Platform;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Lowers a method's complexity the way professional tooling does it: apply the refactorings that
/// remove decisions from the method — extract a nested block into its own method, name a tangled
/// boolean, flatten a tail <c>if</c> into a guard clause — re-measure, and repeat until the target is
/// met or nothing applicable is left.
/// <para>
/// Every step is verified against the compiler before it is kept, and the whole run is rolled back
/// to the last state that compiled, so a move that the syntax allowed but the semantics rejected
/// never reaches the file.
/// </para>
/// </summary>
public interface IReduceComplexityService
{
    /// <summary>The tool-facing call: plans the moves, applies them, and projects the run onto the JSON contract.</summary>
    Task<object> ReduceComplexity(
        string filePath,
        int? line = null,
        string? methodName = null,
        int target = 10,
        int maxSteps = 6,
        bool dryRun = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same run as <see cref="ReduceComplexity"/>, returned as data. <c>fix_complexity</c> calls
    /// this once per method and aggregates the outcomes, so the orchestrator does not parse JSON.
    /// </summary>
    Task<ReduceComplexityOutcome> ReduceAsync(
        ReduceComplexityRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One "lower this unit" request.</summary>
public sealed record ReduceComplexityRequest(
    string FilePath,
    int? Line = null,
    string? MethodName = null,
    int Target = 10,
    int MaxSteps = 6,
    bool DryRun = true);

/// <summary>
/// What one reduce run decided, as data: the scores before and after, the steps kept, the moves the
/// planner refused and why, the compiler verdict, the remaining suggestions and the preview diff.
/// </summary>
public sealed record ReduceComplexityOutcome
{
    public string? Error { get; init; }

    /// <summary>The tool's full error body, when the refusal carries extra detail (the units of a file).</summary>
    public object? ErrorDetails { get; init; }

    public string? Method { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
    public int Target { get; init; }
    public bool DryRun { get; init; }
    public ComplexityMetrics? Before { get; init; }
    public ComplexityMetrics? After { get; init; }
    public bool Applied { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public IReadOnlyDictionary<string, string> Rejected { get; init; } = new Dictionary<string, string>();
    public int CompilerErrorsBefore { get; init; }
    public int CompilerErrorsAfter { get; init; }
    public IReadOnlyList<ComplexitySuggestion> Suggestions { get; init; } = [];
    public string? Preview { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed partial class ReduceComplexityService : IReduceComplexityService
{
    private readonly IRoslynWorkspace _workspace;

    public ReduceComplexityService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <inheritdoc />
    public async Task<object> ReduceComplexity(
        string filePath,
        int? line = null,
        string? methodName = null,
        int target = 10,
        int maxSteps = 6,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ReduceAsync(
            new ReduceComplexityRequest(filePath, line, methodName, target, maxSteps, dryRun), cancellationToken);

        if (outcome.Error is not null)
            return outcome.ErrorDetails ?? new { error = outcome.Error };

        return new
        {
            @refactoring = "reduce_complexity",
            method = outcome.Method,
            file = outcome.File,
            line = outcome.Line,
            target = outcome.Target,
            before = Summary(outcome.Before!),
            after = Summary(outcome.After!),
            applied = outcome.Applied,
            dryRun = outcome.DryRun,
            steps = outcome.Steps,
            rejected = outcome.Rejected,
            compilerErrors = new { before = outcome.CompilerErrorsBefore, after = outcome.CompilerErrorsAfter },
            suggestions = outcome.Suggestions.Select(Describe),
            preview = outcome.Preview,
            note = outcome.Note,
        };
    }

    /// <summary>The typed core behind the tool: the orchestrator aggregates several outcomes per call.</summary>
    public async Task<ReduceComplexityOutcome> ReduceAsync(
        ReduceComplexityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath)) return Failed("A file path is required.");
        if (request.Target < 1) return Failed("The target must be at least 1.");

        var maxSteps = Math.Max(1, request.MaxSteps);
        var target = request.Target;
        var dryRun = request.DryRun;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return Failed("No C# solution could be auto-located from the working directory. Call load_solution first.");

        string path;
        try { path = Path.GetFullPath(request.FilePath); }
        catch (Exception ex) { return Failed($"Invalid path '{request.FilePath}': {ex.Message}"); }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, HostSystem.PathComparison));
        if (document is null) return Failed($"{path} is not a document of the loaded solution.");

        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null || model is null) return Failed($"Could not read the syntax tree of {Path.GetFileName(path)}.");

        var unit = LocateUnit(root, request.Line, request.MethodName);
        if (unit is null)
        {
            var error = request.Line is not null
                ? $"No method, lambda or local function covers line {request.Line}."
                : $"No unit named '{request.MethodName}' in {Path.GetFileName(path)}.";

            return new ReduceComplexityOutcome
            {
                Error = error,
                ErrorDetails = new
                {
                    error,
                    units = ComplexityAnalyzer.Units(root)
                        .Select(candidate => ComplexityAnalyzer.Analyze(candidate, model, document.FilePath))
                        .Select(metrics => $"{metrics.Name} (line {metrics.Line})")
                        .Take(20)
                        .ToList(),
                },
            };
        }

        var baseline = ComplexityAnalyzer.Analyze(unit, model, document.FilePath);
        var name = baseline.Name;

        if (Meets(baseline, target))
            return Outcome(document, baseline, baseline, [], dryRun, applied: false, target,
                note: $"Already at or below the target ({baseline.Cognitive} cognitive, {baseline.Cyclomatic} cyclomatic vs target {target}).");

        var snapshots = new List<Snapshot> { new((await document.GetTextAsync(cancellationToken)).ToString(), baseline, []) };
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = document;
        var currentRoot = root;
        var currentModel = model;
        var currentUnit = unit;

        for (var step = 0; step < maxSteps; step++)
        {
            var metrics = ComplexityAnalyzer.Analyze(currentUnit, currentModel, document.FilePath);
            if (Meets(metrics, target)) break;

            var suggestion = ComplexityPlanner.Suggest(currentUnit, currentModel, cancellationToken)
                .FirstOrDefault(candidate => candidate.AutoApplicable && !rejected.ContainsKey(Key(candidate)));

            if (suggestion is null)
            {
                File.AppendAllText(@"D:\REPOSITORY\vmcp-tmp\reduce-trace.txt",
                    $"step={step} metrics={metrics.Cyclomatic}/{metrics.Cognitive} noSuggestion len={currentUnit.Span.Length}" + Environment.NewLine);
                break;
            }

            var attempt = currentUnit.SyntaxTree.Options.Language == LanguageNames.VisualBasic
                ? suggestion.Kind switch
                {
                    "extract-condition" => ApplyVisualBasicConditionExtraction(
                        currentRoot, currentModel, currentUnit, suggestion.Line, cancellationToken),
                    "guard-clause" => ApplyVisualBasicGuardClause(currentModel, currentUnit, suggestion.Line),
                    "extract-method" => (null, "extracting a block into a method is a hand edit in Visual Basic"),
                    _ => (null, "not auto-applicable"),
                }
                : suggestion.Kind switch
                {
                    "extract-method" => ApplyExtraction(currentRoot, currentModel, currentUnit, suggestion.Line, cancellationToken),
                    "extract-condition" => ApplyConditionExtraction(currentRoot, currentModel, currentUnit, suggestion.Line, cancellationToken),
                    "guard-clause" => ApplyGuardClause(currentRoot, currentModel, currentUnit, suggestion.Line),
                    _ => (null, "not auto-applicable"),
                };

            if (attempt.Root is null)
            {
                File.AppendAllText(@"D:\REPOSITORY\vmcp-tmp\reduce-trace.txt",
                    $"suggestion={suggestion.Kind}@{suggestion.Line} REFUSED: {attempt.Error}" + Environment.NewLine);
                rejected[Key(suggestion)] = attempt.Error ?? "the move was refused";
                continue;
            }

            var text = attempt.Root.ToFullString();
            var nextDocument = current.Project.Solution
                .WithDocumentText(current.Id, SourceText.From(text))
                .GetDocument(current.Id)!;

            var nextRoot = await nextDocument.GetSyntaxRootAsync(cancellationToken);
            var nextModel = await nextDocument.GetSemanticModelAsync(cancellationToken);
            if (nextRoot is null || nextModel is null) break;

            var nextUnit = FindUnit(nextRoot, name, UnitOrdinal(root, unit));
            File.AppendAllText(@"D:\REPOSITORY\vmcp-tmp\reduce-trace.txt",
                $"suggestion={suggestion.Kind}@{suggestion.Line} rootNull={attempt.Root is null} err={attempt.Error} " +
                $"nextUnit={nextUnit?.GetType().Name ?? "null"} name={name} ordinal={UnitOrdinal(root, unit)}" + Environment.NewLine);
            if (nextUnit is null) break;

            var nextMetrics = ComplexityAnalyzer.Analyze(nextUnit, nextModel, document.FilePath);

            // A move that does not actually lower the score is not worth a write.
            if (nextMetrics.Cognitive >= metrics.Cognitive && nextMetrics.Cyclomatic >= metrics.Cyclomatic)
            {
                rejected[Key(suggestion)] = "the move did not lower the score";
                continue;
            }

            var description = DescribeStep(suggestion);
            snapshots.Add(new Snapshot(text, nextMetrics, [.. snapshots[^1].Steps, description]));

            current = nextDocument;
            currentRoot = nextRoot;
            currentModel = nextModel;
            currentUnit = nextUnit;
        }

        var final = snapshots[^1];
        if (snapshots.Count == 1)
            return Outcome(document, baseline, baseline, [], dryRun, applied: false, target,
                note: "No move the reducer can prove safe applies to this method; see 'suggestions' for the ones that need a human.",
                suggestions: ComplexityPlanner.Suggest(unit, model, cancellationToken));

        // ── The compiler gate, with rollback to the last state that built ────────────────
        var gate = await GateAsync(solution, document, final.Text, cancellationToken);
        var rolledBack = false;

        while (!gate.Passed && snapshots.Count > 1)
        {
            snapshots.RemoveAt(snapshots.Count - 1);
            final = snapshots[^1];
            gate = await GateAsync(solution, document, final.Text, cancellationToken);
            rolledBack = true;
        }

        var applied = snapshots.Count > 1;

        if (applied && !dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, final.Text, cancellationToken);

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();

        return Outcome(document, baseline, final.Metrics, final.Steps, dryRun, applied, target,
            note: !applied
                ? "No move was applicable."
                : dryRun
                    ? "Preview only: pass dryRun=false to write the file."
                    : "File written.",
            suggestions: ComplexityPlanner.Suggest(rolledBack ? unit : currentUnit, rolledBack ? model : currentModel, cancellationToken)
                .Where(suggestion => !suggestion.AutoApplicable)
                .ToList(),
            rejected,
            gateBefore: gate.Before,
            gateAfter: gate.After,
            preview: applied ? RefactoringFileWriter.Diff(oldText, final.Text) : null);
    }

    private sealed record Snapshot(string Text, ComplexityMetrics Metrics, IReadOnlyList<string> Steps);

    private static bool Meets(ComplexityMetrics metrics, int target) =>
        metrics.Cognitive <= target && metrics.Cyclomatic <= target;

    private static string Key(ComplexitySuggestion suggestion) => suggestion.Kind + "@" + suggestion.Line;

    private static string DescribeStep(ComplexitySuggestion suggestion) =>
        $"{suggestion.Kind} at line {suggestion.Line}: {suggestion.Description}";

    private static object Summary(ComplexityMetrics metrics) => new
    {
        metrics.Cyclomatic,
        metrics.Cognitive,
        metrics.MaxNesting,
        metrics.MaintainabilityIndex,
        metrics.Risk,
    };

    private static object Describe(ComplexitySuggestion suggestion) => new
    {
        suggestion.Kind,
        suggestion.Line,
        suggestion.Description,
        suggestion.BlockedBy,
    };

    // ── Locating the unit ────────────────────────────────────────────────────────────────

    private static SyntaxNode? LocateUnit(SyntaxNode root, int? line, string? methodName)
    {
        var units = ComplexityAnalyzer.Units(root).ToList();

        if (line is not null)
            return units
                .Where(candidate => StartLine(candidate) <= line && EndLine(candidate) >= line)
                .OrderBy(candidate => candidate.Span.Length)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(methodName)) return null;

        return units.FirstOrDefault(candidate => NameOf(candidate).Equals(methodName, StringComparison.OrdinalIgnoreCase));
    }

    private static SyntaxNode? FindUnit(SyntaxNode root, string name, int ordinal)
    {
        var matches = ComplexityAnalyzer.Units(root)
            .Where(candidate => NameOf(candidate) == name)
            .ToList();

        return ordinal < matches.Count ? matches[ordinal] : matches.FirstOrDefault();
    }

    private static int UnitOrdinal(SyntaxNode root, SyntaxNode unit)
    {
        var name = NameOf(unit);
        var matches = ComplexityAnalyzer.Units(root).Where(candidate => NameOf(candidate) == name).ToList();
        return Math.Max(0, matches.IndexOf(unit));
    }

    private static string NameOf(SyntaxNode unit) =>
        unit.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? ComplexityAnalyzer.NameOf(unit)
            : unit switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                AnonymousFunctionExpressionSyntax => "lambda",
                _ => unit.Kind().ToString(),
            };

    // ── Move 1: a nested block becomes its own method ────────────────────────────────────

    private static (SyntaxNode? Root, string? Error) ApplyExtraction(
        SyntaxNode root, SemanticModel model, SyntaxNode unit, int startLine, CancellationToken cancellationToken)
    {
        if (unit is not MethodDeclarationSyntax host || host.Body is null)
            return (null, "the enclosing unit is not a method with a body");

        var owner = host.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (owner is null) return (null, "the method is not inside a type declaration");
        if (model.GetDeclaredSymbol(owner, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return (null, "the containing type did not resolve");

        var block = host.Body
            .DescendantNodes(child => child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<BlockSyntax>()
            .Where(candidate => StartLine(candidate) == startLine)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (block is null) return (null, $"no nested block starts on line {startLine}");

        var selected = block.Statements.ToList();
        var helperName = UniqueMemberName(ownerSymbol, "Extract");

        var extraction = BlockExtraction.TryCreate(
            root, model, host, owner, ownerSymbol, selected, helperName, cancellationToken, out var refusal);

        if (extraction is null) return (null, refusal);

        var indentation = BlockExtraction.IndentationOf(host);
        return (BlockExtraction.BuildRoot(root, owner, host, block, 0, selected.Count - 1, extraction, indentation), null);
    }

    // ── Move 2: a tangled boolean becomes a named predicate ──────────────────────────────

    private static (SyntaxNode? Root, string? Error) ApplyConditionExtraction(
        SyntaxNode root, SemanticModel model, SyntaxNode unit, int startLine, CancellationToken cancellationToken)
    {
        if (unit is not MethodDeclarationSyntax host)
            return (null, "the enclosing unit is not a method");

        var owner = host.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (owner is null) return (null, "the method is not inside a type declaration");
        if (model.GetDeclaredSymbol(owner, cancellationToken) is not INamedTypeSymbol ownerSymbol)
            return (null, "the containing type did not resolve");

        var condition = ConditionAt(host, startLine);
        if (condition is null) return (null, $"no condition starts on line {startLine}");

        if (condition.DescendantNodesAndSelf().Any(node =>
                node is AnonymousFunctionExpressionSyntax or AwaitExpressionSyntax or AssignmentExpressionSyntax))
            return (null, "the condition contains a lambda, an await or an assignment");

        // What the predicate needs: the locals and parameters it reads, in source order.
        var parameters = new List<(string Type, string Name)>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is not ILocalSymbol and not IParameterSymbol) continue;
            if (!seen.Add(symbol)) continue;

            switch (symbol)
            {
                case IParameterSymbol parameter when parameter.RefKind != RefKind.None || parameter.IsParams:
                    return (null, $"a parameter of '{host.Identifier.ValueText}' is passed by ref/params and cannot be forwarded");
                case IParameterSymbol parameter:
                    parameters.Add((parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), parameter.Name));
                    break;
                case ILocalSymbol local when local.Type.TypeKind == TypeKind.Error:
                    return (null, $"local '{local.Name}' has no resolvable type");
                case ILocalSymbol local:
                    parameters.Add((local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), local.Name));
                    break;
            }
        }

        var helperName = UniqueMemberName(ownerSymbol, "Is" + Pascal(host.Identifier.ValueText) + "Condition");

        var modifiers = TokenList(Token(SyntaxKind.PrivateKeyword));
        if (host.Modifiers.Any(SyntaxKind.StaticKeyword))
            modifiers = modifiers.Add(Token(SyntaxKind.StaticKeyword));

        var helper = MethodDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)), Identifier(helperName))
            .WithModifiers(modifiers)
            .WithParameterList(ParameterList(SeparatedList(parameters
                .Select(parameter => Parameter(Identifier(parameter.Name)).WithType(ParseTypeName(parameter.Type))))))
            .WithExpressionBody(ArrowExpressionClause(condition.WithoutTrivia().NormalizeWhitespace()))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        helper = BlockExtraction.NormalizeAt(helper, BlockExtraction.IndentationOf(host));

        var invocation = ParseExpression($"{helperName}({string.Join(", ", parameters.Select(parameter => parameter.Name))})");

        var newHost = host.ReplaceNode(condition, invocation.WithTriviaFrom(condition));
        var index = owner.Members.IndexOf(host);

        var newOwner = owner.Members
            .Replace(host, newHost)
            .Insert(index + 1, helper
                .WithLeadingTrivia(new SyntaxTriviaList(ElasticCarriageReturnLineFeed, Whitespace(BlockExtraction.IndentationOf(host))))
                .WithTrailingTrivia(ElasticCarriageReturnLineFeed));

        return (root.ReplaceNode(owner, owner.WithMembers(newOwner)), null);
    }

    private static ExpressionSyntax? ConditionAt(MethodDeclarationSyntax host, int startLine)
    {
        foreach (var node in host.DescendantNodes(child =>
                     child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            var condition = node switch
            {
                IfStatementSyntax @if => @if.Condition,
                WhileStatementSyntax @while => @while.Condition,
                DoStatementSyntax @do => @do.Condition,
                _ => null,
            };

            if (condition is not null && StartLine(condition) == startLine) return condition;
        }

        return null;
    }

    // ── Move 3: a tail `if` becomes a guard clause ───────────────────────────────────────

    private static (SyntaxNode? Root, string? Error) ApplyGuardClause(
        SyntaxNode root, SemanticModel model, SyntaxNode unit, int startLine)
    {
        if (unit is not MethodDeclarationSyntax host || host.Body is not { } body)
            return (null, "the enclosing unit is not a method with a body");

        if (body.Statements.Count < 2) return (null, "the method has nothing to flatten");
        if (body.Statements[^1] is not IfStatementSyntax tail || StartLine(tail) != startLine)
            return (null, $"no 'if' is the last statement on line {startLine}");
        if (tail.Else is not null) return (null, "the 'if' has an else branch");

        // Moving the body out of the if is only faithful when falling off the end is the same as
        // returning: that is what a void method does, and nothing else.
        if (model.GetDeclaredSymbol(host) is IMethodSymbol { ReturnsVoid: false })
            return (null, "the method returns a value, so losing the 'if' changes what it returns");

        var branches = tail.Statement is BlockSyntax block ? block.Statements.ToList() : [tail.Statement];        var ifIndentation = IndentationOf(tail);
        var branchIndentation = branches.Count == 0 ? ifIndentation : IndentationOf(branches[0]);
        var delta = Math.Max(0, branchIndentation.Length - ifIndentation.Length);

        var dedented = new List<StatementSyntax>();
        foreach (var statement in branches)
        {
            var reparsed = ParseStatement(Dedent(statement.ToFullString(), delta)) as StatementSyntax;
            if (reparsed is null) return (null, "a statement of the 'if' body could not be re-indented");

            // A brace-less `if (c) Do();` carries no newline, so the moved statement would land at
            // column 0. Give it the 'if' own indentation.
            if (!reparsed.GetLeadingTrivia().ToString().Contains('\n'))
                reparsed = reparsed.WithLeadingTrivia(
                    new SyntaxTriviaList(ElasticCarriageReturnLineFeed, Whitespace(ifIndentation)));

            dedented.Add(reparsed);
        }

        var guard = IfStatement(Negate(tail.Condition), ReturnStatement())
            .NormalizeWhitespace()
            .WithLeadingTrivia(tail.GetLeadingTrivia())
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        var statements = body.Statements.Take(body.Statements.Count - 1).Concat([guard]).Concat(dedented).ToList();
        var newHost = host.WithBody(body.WithStatements(List(statements)));

        return (root.ReplaceNode(host, newHost), null);
    }

    /// <summary>`!x`, `a &amp;&amp; b` → `!(a &amp;&amp; b)`, `!(...)` → the operand.</summary>
    private static ExpressionSyntax Negate(ExpressionSyntax condition)
    {
        if (condition is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression))
            return negation.Operand.WithTriviaFrom(condition);

        var atomic = condition is IdentifierNameSyntax
                              or MemberAccessExpressionSyntax
                              or InvocationExpressionSyntax
                              or ElementAccessExpressionSyntax
                              or LiteralExpressionSyntax
                              or ObjectCreationExpressionSyntax
                              or ThisExpressionSyntax;

        var operand = atomic
            ? condition.WithoutTrivia()
            : ParenthesizedExpression(condition.WithoutTrivia());

        return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, operand).WithTriviaFrom(condition);
    }

    /// <summary>Shifts a block of source left by <paramref name="columns"/>, which is what moving it out of a nest costs.</summary>
    private static string Dedent(string text, int columns)
    {
        if (columns == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var strip = 0;
            while (strip < columns && strip < lines[index].Length && lines[index][strip] == ' ') strip++;
            lines[index] = lines[index][strip..];
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The whitespace that precedes this node on its own line. Read from the source line rather than
    /// from the leading trivia: the newline belongs to the previous token, so a statement that
    /// follows another one carries indentation without a line break.
    /// </summary>
    private static string IndentationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var line = node.SyntaxTree.GetText().Lines[span.StartLinePosition.Line].ToString();
        return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static string UniqueMemberName(INamedTypeSymbol owner, string prefix)
    {
        if (owner.GetMembers(prefix).Length == 0) return prefix;

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = prefix + suffix;
            if (owner.GetMembers(candidate).Length == 0) return candidate;
        }

        return prefix + Guid.NewGuid().ToString("N")[..6];
    }

    private static string Pascal(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static int StartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static async Task<CompilerGate> GateAsync(
        Solution solution, Document document, string text, CancellationToken cancellationToken)
    {
        var projected = solution.WithDocumentText(document.Id, SourceText.From(text));
        return await RefactoringCompilerGate.CompareAsync(
            solution, projected, [document.Project.Id], cancellationToken);
    }

    private static ReduceComplexityOutcome Outcome(
        Document document,
        ComplexityMetrics baseline,
        ComplexityMetrics final,
        IReadOnlyList<string> steps,
        bool dryRun,
        bool applied,
        int target,
        string note,
        IReadOnlyList<ComplexitySuggestion>? suggestions = null,
        IReadOnlyDictionary<string, string>? rejected = null,
        int gateBefore = 0,
        int gateAfter = 0,
        string? preview = null) => new()
    {
        Method = baseline.FullName,
        File = document.FilePath,
        Line = baseline.Line,
        Target = target,
        DryRun = dryRun,
        Before = baseline,
        After = final,
        Applied = applied,
        Steps = steps,
        Rejected = rejected ?? new Dictionary<string, string>(),
        CompilerErrorsBefore = gateBefore,
        CompilerErrorsAfter = gateAfter,
        Suggestions = suggestions ?? [],
        Preview = preview,
        Note = note,
    };

    private static ReduceComplexityOutcome Failed(string error) => new() { Error = error };
}
