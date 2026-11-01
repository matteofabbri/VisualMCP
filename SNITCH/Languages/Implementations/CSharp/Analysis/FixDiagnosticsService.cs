using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.Gate;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The compiler's own diagnostics are fixed instead of reported. <c>apply_code_fix</c> runs an
/// analyzer's <c>CodeFixProvider</c>, and compiler diagnostics (<c>CS####</c>) ship none — so this is
/// the family that had no automatic fix at all. It covers the mechanical members: a local nothing
/// reads, a private field nothing uses, an event nothing raises, a member that hides an inherited one,
/// a <c>new</c> that hides nothing, a duplicate using. Every edit is accepted only when the project
/// compiles no worse than before, and a written run is recorded in the solution's edit history.
/// </summary>
public interface IFixDiagnosticsService
{
    Task<object> FixAsync(
        string? projectName = null,
        string? filePath = null,
        string? diagnosticIds = null,
        int maxFixes = 200,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

public sealed class FixDiagnosticsService : IFixDiagnosticsService
{
    /// <summary>What this service can rewrite, and the shape of the rewrite. Nothing else is touched.</summary>
    private static readonly IReadOnlyDictionary<string, string> Supported =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CS0168"] = "remove a local that is declared and never used (a side-effecting initializer becomes a discard)",
            ["CS0219"] = "remove a local that is assigned but never read",
            ["CS0169"] = "remove a private field that is never used",
            ["CS0067"] = "remove an event that is never used",
            ["CS0105"] = "remove a using directive that appears twice",
            ["CS0108"] = "mark a member that hides an inherited one with 'new'",
            ["CS0114"] = "mark a member that hides an inherited one with 'new'",
            ["CS0109"] = "remove a 'new' modifier that hides nothing",
        };

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public FixDiagnosticsService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> FixAsync(
        string? projectName = null,
        string? filePath = null,
        string? diagnosticIds = null,
        int maxFixes = 200,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var ids = SelectIds(diagnosticIds, out var unknown);
        if (unknown.Count > 0)
            return new
            {
                error = $"These ids have no automatic fix here: {string.Join(", ", unknown)}.",
                supported = Supported.Select(entry => new { id = entry.Key, fix = entry.Value }).ToList(),
                note = "get_diagnostics lists every compiler diagnostic; this tool rewrites only the mechanical family above.",
            };

        if (maxFixes < 1) maxFixes = 1;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var (projects, selectionError) = SelectProjects(solution, projectName);
        if (selectionError is not null) return selectionError;

        var plans = new List<FilePlan>();
        var skipped = new List<object>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var found = 0;
        var remainingBudget = maxFixes;

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .Where(diagnostic => ids.Contains(diagnostic.Id))
                .Where(diagnostic => diagnostic.Location.IsInSource && diagnostic.Location.SourceTree is not null)
                .Where(diagnostic => filePath is null ||
                                     PathEquals(diagnostic.Location.SourceTree!.FilePath, filePath))
                .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .ToList();

            if (diagnostics.Count == 0) continue;

            var documentsByPath = project.Documents
                .Where(document => document.FilePath is not null)
                .ToDictionary(document => Path.GetFullPath(document.FilePath!), StringComparer.FromComparison(HostSystem.PathComparison));

            foreach (var group in diagnostics.GroupBy(diagnostic =>
                         Path.GetFullPath(diagnostic.Location.SourceTree!.FilePath)))
            {
                if (!documentsByPath.TryGetValue(group.Key, out var document))
                {
                    foreach (var diagnostic in group)
                    {
                        found++;
                        Count(byId, diagnostic.Id);
                        skipped.Add(Describe(diagnostic, null, "the diagnostic is not on a document of the project (generated or metadata code)"));
                    }
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var text = await document.GetTextAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null)
                {
                    foreach (var diagnostic in group)
                    {
                        found++;
                        Count(byId, diagnostic.Id);
                        skipped.Add(Describe(diagnostic, document.FilePath, "the document has no syntax tree"));
                    }
                    continue;
                }

                var edits = new List<PlanEdit>();
                foreach (var diagnostic in group)
                {
                    found++;
                    Count(byId, diagnostic.Id);

                    if (remainingBudget <= 0)
                    {
                        skipped.Add(Describe(diagnostic, document.FilePath, $"maxFixes ({maxFixes}) reached; raise it or run again"));
                        continue;
                    }

                    var edit = Plan(diagnostic, root, text, model);
                    if (edit.Problem is not null)
                    {
                        skipped.Add(Describe(diagnostic, document.FilePath, edit.Problem));
                        continue;
                    }

                    edits.Add(edit.Edit!);
                    remainingBudget--;
                }

                if (edits.Count == 0) continue;

                var (newText, applied, rejected) = Apply(text, edits);
                skipped.AddRange(rejected);

                if (string.Equals(newText, text.ToString(), StringComparison.Ordinal))
                {
                    foreach (var edit in applied)
                        skipped.Add(new { id = edit.Id, file = document.FilePath, line = edit.Line, reason = "the rewrite produced the same text" });
                    continue;
                }

                // The gate: adding the fix alone must not turn a warning into a new compiler error.
                // The project verdict is cached, and a document edit is re-parsed on top of it.
                var baseline = await CompilationCache.Shared.GetAsync(project, cancellationToken);
                var projected = baseline.WithDocument(document, SourceText.From(newText, text.Encoding ?? System.Text.Encoding.UTF8));
                if (projected.Messages.Count > baseline.Messages.Count)
                {
                    var introduced = projected.Messages.Except(baseline.Messages, StringComparer.Ordinal).FirstOrDefault()
                                     ?? "a new compiler error";
                    foreach (var edit in applied)
                        skipped.Add(new { id = edit.Id, file = document.FilePath, line = edit.Line, reason = "the fix would add a compiler error: " + introduced });
                    continue;
                }

                plans.Add(new FilePlan(document, text.ToString(), newText, applied));
            }
        }

        if (plans.Count == 0)
            return new
            {
                tool = "fix_diagnostics",
                scope = new { project = projectName ?? "all", file = filePath },
                dryRun,
                found,
                @fixed = 0,
                changedFiles = 0,
                byId,
                skipped,
                compilerErrors = (object?)null,
                summary = found == 0
                    ? "No fixable compiler diagnostic in scope."
                    : $"{found} diagnostic(s) in scope, none of them fixable automatically (see skipped).",
                note = "dryRun defaults to true; nothing was written.",
            };

        // One solution-level verdict over every planned document: two edits that are each harmless
        // can still collide, and that collision must not reach the files.
        var afterSolution = solution;
        foreach (var plan in plans)
            afterSolution = afterSolution.WithDocumentText(plan.Document.Id, SourceText.From(plan.NewText, System.Text.Encoding.UTF8));

        var affectedProjects = plans.Select(plan => plan.Document.Project.Id).Distinct().ToList();
        var gate = await RefactoringCompilerGate.CompareAsync(solution, afterSolution, affectedProjects, cancellationToken);

        if (!gate.Passed)
            return new
            {
                tool = "fix_diagnostics",
                scope = new { project = projectName ?? "all", file = filePath },
                dryRun,
                found,
                @fixed = 0,
                changedFiles = 0,
                byId,
                skipped,
                compilerErrors = new { before = gate.Before, after = gate.After },
                error = $"Refused: the fixes together would add {gate.Added} compiler error(s). Nothing was written.",
                errors = gate.NewErrors,
            };

        if (!dryRun)
        {
            foreach (var plan in plans)
            {
                _store?.RecordEdit(
                    "fix_diagnostics",
                    plan.Document.FilePath ?? plan.Document.Name,
                    string.Join(",", plan.Edits.Select(edit => edit.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal)),
                    plan.Before,
                    plan.NewText);

                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, plan.Document, plan.NewText, cancellationToken);
            }
        }

        var remaining = 0;
        foreach (var projectId in affectedProjects)
        {
            var project = afterSolution.GetProject(projectId);
            if (project is null) continue;

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            remaining += compilation.GetDiagnostics(cancellationToken)
                .Count(diagnostic => diagnostic.Location.IsInSource && ids.Contains(diagnostic.Id));
        }

        var fixedCount = plans.Sum(plan => plan.Edits.Count);

        return new
        {
            tool = "fix_diagnostics",
            scope = new { project = projectName ?? "all", file = filePath },
            dryRun,
            found,
            @fixed = fixedCount,
            changedFiles = plans.Count,
            byId,
            files = plans.Select(plan => new
            {
                file = plan.Document.FilePath,
                fixes = plan.Edits.Select(edit => new
                {
                    id = edit.Id,
                    line = edit.Line,
                    change = edit.What,
                }).ToList(),
                preview = RefactoringFileWriter.Diff(plan.Before, plan.NewText),
            }).ToList(),
            skipped,
            compilerErrors = new { before = gate.Before, after = gate.After },
            remaining,
            summary = dryRun
                ? $"Preview: {fixedCount} fix(es) in {plans.Count} file(s) resolve {fixedCount} of {found} diagnostic(s); pass dryRun=false to write."
                : $"Fixed {fixedCount} of {found} diagnostic(s) in {plans.Count} file(s); {remaining} remain in scope.",
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files. Each fix passed the compiler gate."
                : "Files written and recorded in the edit history (revert_edit undoes one).",
        };
    }

    /// <summary>The ids to fix: the caller's list, or the whole supported set.</summary>
    private static HashSet<string> SelectIds(string? diagnosticIds, out List<string> unknown)
    {
        unknown = new List<string>();
        var requested = (diagnosticIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.ToUpperInvariant())
            .ToList();

        if (requested.Count == 0) return Supported.Keys.ToHashSet(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in requested)
        {
            if (Supported.ContainsKey(id)) ids.Add(id);
            else unknown.Add(id);
        }

        return ids;
    }

    private static (List<Project> Projects, object? Error) SelectProjects(Solution solution, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return (solution.Projects.ToList(), null);

        var project = solution.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));

        return project is null
            ? (new List<Project>(), new
            {
                error = $"No project named '{projectName}'.",
                projects = solution.Projects.Select(candidate => candidate.Name).ToList(),
            })
            : (new List<Project> { project }, null);
    }

    /// <summary>
    /// The rewrite for one diagnostic, as a splice on the original text. Text, not a node replacement:
    /// several diagnostics in one file would otherwise shift each other's spans.
    /// </summary>
    private static PlannedEdit Plan(Diagnostic diagnostic, SyntaxNode root, SourceText text, SemanticModel? model)
    {
        var line = text.Lines.GetLineFromPosition(diagnostic.Location.SourceSpan.Start).LineNumber + 1;

        if (line < 1) line = 1;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        switch (diagnostic.Id)
        {
            case "CS0168":
            case "CS0219":
                return PlanUnusedLocal(diagnostic.Id, node, text, model, line);

            case "CS0169":
                return PlanUnusedField(diagnostic.Id, node, text, line);

            case "CS0067":
                return PlanUnusedEvent(diagnostic.Id, node, text, line);

            case "CS0105":
                return PlanDuplicateUsing(diagnostic.Id, node, text, line);

            case "CS0108":
            case "CS0114":
                return PlanAddNew(diagnostic.Id, node, text, line);

            case "CS0109":
                return PlanRemoveNew(diagnostic.Id, node, text, line);

            default:
                return PlannedEdit.Skip(diagnostic.Id, line, "no rewrite is defined for this id");
        }
    }

    private static PlannedEdit PlanUnusedLocal(string id, SyntaxNode node, SourceText text, SemanticModel? model, int line)
    {
        var declarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration || declaration.Variables.Count != 1)
            return PlannedEdit.Skip(id, line, "the declaration has more than one variable");

        if (declaration.Parent is not LocalDeclarationStatementSyntax local ||
            local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) ||
            local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
            return PlannedEdit.Skip(id, line, "not a plain local declaration (a using declaration disposes its value)");

        // No initializer: nothing can be observed, the statement goes.
        if (declarator.Initializer is null)
        {
            var span = WholeLine(text, local.Span);
            return PlannedEdit.Make(id, line, span.Start, span.Length, string.Empty, "removed the declaration");
        }

        // A compile-time constant has no side effect and no inferred type for a discard, so the
        // statement goes too. The compiler only raises CS0219 for exactly this case.
        if (IsConstant(declarator.Initializer.Value, model))
        {
            var span = WholeLine(text, local.Span);
            return PlannedEdit.Make(id, line, span.Start, span.Length, string.Empty, "removed the declaration");
        }

        // Everything else may have a side effect (a call, a property read, an await): keep it.
        return PlannedEdit.Make(
            id, line, local.SpanStart, local.Span.Length,
            "_ = " + declarator.Initializer.Value + ";",
            "replaced with a discard assignment, so the initializer still runs");
    }

    private static PlannedEdit PlanUnusedField(string id, SyntaxNode node, SourceText text, int line)
    {
        var declarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration || declaration.Variables.Count != 1)
            return PlannedEdit.Skip(id, line, "the field declaration has more than one variable");

        if (declaration.Parent is not FieldDeclarationSyntax field)
            return PlannedEdit.Skip(id, line, "the diagnostic is not on a field declaration");

        if (field.AttributeLists.Count > 0)
            return PlannedEdit.Skip(id, line, "the field carries attributes, which may read it by reflection");

        var span = WholeLine(text, field.Span);
        return PlannedEdit.Make(id, line, span.Start, span.Length, string.Empty, "removed the field");
    }

    private static PlannedEdit PlanUnusedEvent(string id, SyntaxNode node, SourceText text, int line)
    {
        var declarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration || declaration.Variables.Count != 1)
            return PlannedEdit.Skip(id, line, "the event declaration has more than one variable");

        if (declaration.Parent is not EventFieldDeclarationSyntax declarationSyntax)
            return PlannedEdit.Skip(id, line, "the diagnostic is not on an event field");

        if (declarationSyntax.AttributeLists.Count > 0)
            return PlannedEdit.Skip(id, line, "the event carries attributes");

        var span = WholeLine(text, declarationSyntax.Span);
        return PlannedEdit.Make(id, line, span.Start, span.Length, string.Empty, "removed the event");
    }

    private static PlannedEdit PlanDuplicateUsing(string id, SyntaxNode node, SourceText text, int line)
    {
        var directive = node.FirstAncestorOrSelf<UsingDirectiveSyntax>();
        if (directive is null)
            return PlannedEdit.Skip(id, line, "the diagnostic is not on a using directive");

        var span = WholeLine(text, directive.Span);
        return PlannedEdit.Make(id, line, span.Start, span.Length, string.Empty, "removed the duplicated using");
    }

    private static PlannedEdit PlanAddNew(string id, SyntaxNode node, SourceText text, int line)
    {
        if (node.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member)
            return PlannedEdit.Skip(id, line, "the diagnostic is not on a member declaration");

        if (member.Modifiers.Any(SyntaxKind.NewKeyword))
            return PlannedEdit.Skip(id, line, "the member is already marked 'new'");

        if (member.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return PlannedEdit.Skip(id, line, "the member is already an override");

        // After the last existing modifier, so the order stays the one the language expects
        // ("public new void", not "new public void").
        if (member.Modifiers.Count > 0)
            return PlannedEdit.Make(id, line, member.Modifiers.Last().Span.End, 0, " new", "added the 'new' modifier");

        if (member.AttributeLists.Count > 0)
            return PlannedEdit.Skip(id, line, "the member has attributes and no modifier to insert before");

        return PlannedEdit.Make(id, line, member.SpanStart, 0, "new ", "added the 'new' modifier");
    }

    private static PlannedEdit PlanRemoveNew(string id, SyntaxNode node, SourceText text, int line)
    {
        if (node.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member)
            return PlannedEdit.Skip(id, line, "the diagnostic is not on a member declaration");

        SyntaxToken? newToken = null;
        foreach (var modifier in member.Modifiers)
        {
            if (!modifier.IsKind(SyntaxKind.NewKeyword)) continue;
            newToken = modifier;
            break;
        }

        if (newToken is not { } token)
            return PlannedEdit.Skip(id, line, "no 'new' modifier on the declaration");

        // Take the space after the keyword too, or the next token starts with a leading space.
        var end = token.Span.End;
        while (end < text.Length && text[end] is ' ' or '\t') end++;

        return PlannedEdit.Make(id, line, token.SpanStart, end - token.SpanStart, string.Empty, "removed the redundant 'new'");
    }

    /// <summary>
    /// A compile-time constant cannot have a side effect, so removing the declaration loses nothing.
    /// The semantic model is what knows it: `1 + 2` and `nameof(X)` are constants without being literals.
    /// </summary>
    private static bool IsConstant(ExpressionSyntax expression, SemanticModel? model) =>
        model is not null ? model.GetConstantValue(expression).HasValue : IsPureLiteral(expression);

    /// <summary>A literal (or a cast of one) cannot have a side effect.</summary>
    private static bool IsPureLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        DefaultExpressionSyntax => true,
        CastExpressionSyntax cast => IsPureLiteral(cast.Expression),
        ParenthesizedExpressionSyntax parenthesized => IsPureLiteral(parenthesized.Expression),
        _ => false,
    };

    /// <summary>
    /// The span of the whole line holding <paramref name="span"/> when nothing else sits on it, so a
    /// removed declaration takes its indentation and line break with it instead of leaving a blank line.
    /// A statement that shares its line with other code loses only its own span.
    /// </summary>
    private static (int Start, int Length) WholeLine(SourceText text, TextSpan span)
    {
        var line = text.Lines.GetLineFromPosition(span.Start);
        var before = text.ToString(TextSpan.FromBounds(line.Start, span.Start));
        var start = before.Trim().Length == 0 ? line.Start : span.Start;

        var end = span.End;
        var rest = text.ToString(TextSpan.FromBounds(Math.Min(end, line.End), line.End));
        if (rest.Trim().Length == 0) end = line.EndIncludingLineBreak;

        return (start, end - start);
    }

    /// <summary>
    /// Applies the splices bottom-up and drops the ones that overlap an already applied edit, so a
    /// nested diagnostic cannot corrupt the text it sits inside.
    /// </summary>
    private static (string Text, List<PlanEdit> Applied, List<object> Rejected) Apply(SourceText source, List<PlanEdit> edits)
    {
        var applied = new List<PlanEdit>();
        var rejected = new List<object>();
        var nextStart = int.MaxValue;
        var text = source.ToString();

        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            if (edit.Start + edit.Length > nextStart)
            {
                rejected.Add(new { id = edit.Id, line = edit.Line, reason = "the edit overlaps another fix in the same file" });
                continue;
            }

            text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
            applied.Add(edit);
            nextStart = edit.Start;
        }

        applied.Reverse();
        return (text, applied, rejected);
    }

    private static void Count(Dictionary<string, int> byId, string id) =>
        byId[id] = byId.TryGetValue(id, out var count) ? count + 1 : 1;

    private static object Describe(Diagnostic diagnostic, string? file, string reason) => new
    {
        id = diagnostic.Id,
        file,
        line = diagnostic.Location.IsInSource
            ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1
            : 0,
        reason,
    };

    private static bool PathEquals(string? left, string right) =>
        left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), HostSystem.PathComparison);

    private sealed record FilePlan(Document Document, string Before, string NewText, List<PlanEdit> Edits);

    private sealed record PlanEdit(string Id, int Line, int Start, int Length, string Replacement, string What);

    private readonly record struct PlannedEdit(PlanEdit? Edit, string? Problem)
    {
        public static PlannedEdit Make(string id, int line, int start, int length, string replacement, string what) =>
            new(new PlanEdit(id, line, start, length, replacement, what), null);

        public static PlannedEdit Skip(string id, int line, string problem) => new(null, $"{id}: {problem}");
    }
}
