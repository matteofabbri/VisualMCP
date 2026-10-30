using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.Gate;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.Style;

/// <summary>
/// The style-suggestion pipeline, shared by every language: scope selection, up to three passes per
/// document, the compiler gate that refuses a file whose rewrite would add errors, the refusal report,
/// and the edit history. What a language supplies is a rule set and an <see cref="IStyleLanguage"/> —
/// how to tell a statement from an expression, how to read and rebuild a block's statement list, and
/// how its <c>.editorconfig</c> keys map onto <see cref="StyleOptions"/>.
/// </summary>
public class StyleRewriteService
{
    private const int MaxPassesPerDocument = 3;

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;
    private readonly IStyleRuleSet _ruleSet;
    private readonly IStyleLanguage _language;
    private readonly string _refactoring;

    public StyleRewriteService(
        IRoslynWorkspace workspace,
        IVisualMcpStore? store,
        IStyleRuleSet ruleSet,
        IStyleLanguage language,
        string refactoring)
    {
        _workspace = workspace;
        _store = store;
        _ruleSet = ruleSet;
        _language = language;
        _refactoring = refactoring;
    }

    public IReadOnlyList<StyleRule> Rules => _ruleSet.Supported;

    public IReadOnlyList<(string Id, string Reason)> NotRewritten => _ruleSet.NotRewritten;

    public virtual async Task<object> SimplifyAsync(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        var enabled = SelectRewrites(rules, out var unknown);
        if (unknown.Count > 0)
            return new
            {
                error = $"These ids are not rewritable by {_refactoring}: {string.Join(", ", unknown)}.",
                supported = _ruleSet.SupportedIds,
                notRewritten = _ruleSet.NotRewritten
                    .Where(entry => unknown.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(entry => new { id = entry.Id, reason = entry.Reason })
                    .ToList(),
            };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory. Call load_solution first." };

        var (documents, selectionError) = await SelectDocumentsAsync(solution, level, filePath, projectName, cancellationToken);
        if (selectionError is not null) return selectionError;

        var edits = new List<StyleMatch>();
        var accepted = new List<PlannedDocument>();
        var refused = new List<object>();
        var observed = new List<StyleOptions>();
        var spanMatches = new List<StyleMatch>();
        var semantics = new SemanticAudit();

        // Plan in three phases so a suggestion whose edits name another project is still one atomic
        // change: (1) every document's syntax rewrites, (2) every multi-document suggestion resolved
        // against the whole solution, (3) the compiler gate per project, then accept a multi-document
        // suggestion only when every document it planned survived.
        var plans = new List<ProjectPlan>();

        foreach (var projectGroup in documents.GroupBy(document => document.Project.Id))
        {
            var project = solution.GetProject(projectGroup.Key)!;
            var planned = new List<PlannedDocument>();

            foreach (var document in projectGroup)
            {
                var text = await document.GetTextAsync(cancellationToken);
                var file = document.FilePath ?? document.Name;
                var documentFailures = new List<SemanticFailure>();
                var newText = await RewriteAsync(document, enabled, edits, spanMatches, observed, documentFailures, semantics, cancellationToken);

                // A claimed-equivalence rule that changed the operation tree is a defect in the rule, not
                // a suggestion to apply: the file is left alone instead of written with new semantics.
                if (documentFailures.Count > 0)
                {
                    refused.Add(new
                    {
                        file,
                        reason = "a rule that claims equivalence changed the operation tree",
                        error = string.Join("; ", documentFailures.Select(failure =>
                            $"{failure.Rule} line {failure.Line}: {string.Join(", ", failure.Differences)}")),
                    });
                    continue;
                }

                if (newText is null || string.Equals(text.ToString(), newText, StringComparison.Ordinal)) continue;

                planned.Add(new PlannedDocument(document, text.ToString(), newText));
            }

            plans.Add(new ProjectPlan(project, planned));
        }

        var spanPlans = PlanSpanEdits(solution, spanMatches, plans, edits, refused);

        foreach (var plan in plans)
        {
            var planned = plan.Planned;
            if (planned.Count == 0) continue;

            // The gate: the project must not gain compiler errors. The "before" verdict is reused across
            // calls (CompilationCache), and each candidate is judged by re-parsing only its own document
            // on top of that compilation, so isolating an offender no longer costs a full compile.
            var before = await CompilationCache.Shared.GetAsync(plan.Project, cancellationToken);
            var together = before.With(planned
                .Select(change => (change.Document, (SourceText)SourceText.From(change.NewText)))
                .ToList());

            if (together.Messages.Count <= before.Messages.Count)
            {
                accepted.AddRange(planned);
                continue;
            }

            foreach (var candidate in planned)
            {
                var alone = before.WithDocument(candidate.Document, SourceText.From(candidate.NewText));
                if (alone.Messages.Count <= before.Messages.Count)
                {
                    accepted.Add(candidate);
                    continue;
                }

                var introduced = alone.Messages.Except(before.Messages, StringComparer.Ordinal).FirstOrDefault() ?? "unknown error";
                refused.Add(new
                {
                    file = candidate.Document.FilePath,
                    reason = "the rewrite would add compiler errors",
                    error = introduced,
                });
            }
        }

        // A multi-document suggestion is one change: if any document it planned was refused, none of it
        // is applied — the documents it did get accepted are withdrawn before anything is written.
        var acceptedFiles = accepted
            .Where(change => change.Document.FilePath is not null)
            .Select(change => change.Document.FilePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var spanPlan in spanPlans)
        {
            var missing = spanPlan.Changes.FirstOrDefault(change =>
                change.Document.FilePath is null || !acceptedFiles.Contains(change.Document.FilePath));
            if (missing is null) continue;

            foreach (var change in spanPlan.Changes) accepted.Remove(change);
            edits.Remove(spanPlan.Match);
            refused.Add(new
            {
                file = spanPlan.Match.File,
                reason = "one of the files this suggestion edits was refused, so none of it was applied",
                error = missing.Document.FilePath,
            });
        }

        if (!dryRun)
        {
            foreach (var change in accepted)
            {
                await Snitch.Languages.CSharp.Refactoring.RefactoringFileWriter.WriteAndUpdateAsync(
                    _workspace, change.Document, change.NewText, cancellationToken);

                // The solution's local history: which rules touched this file, and the content hashes
                // that tell a real change from a rewrite that only reported itself.
                var applied = edits
                    .Where(edit => string.Equals(edit.File, change.Document.FilePath, StringComparison.OrdinalIgnoreCase))
                    .Select(edit => edit.Rule.Id)
                    .Distinct()
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();

                _store?.RecordEdit(
                    _refactoring,
                    change.Document.FilePath ?? change.Document.Name,
                    applied.Count > 0 ? string.Join(",", applied) : null,
                    change.OldText,
                    change.NewText);
            }
        }

        var acceptedPaths = accepted.Select(change => change.Document.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reported = edits.Where(edit => acceptedPaths.Contains(edit.File)).ToList();

        // An option-driven rule that found no option did nothing on purpose; say so instead of
        // returning an empty result that reads like a bug.
        object? styleOptions = null;
        if (enabled.Any(rewrite => rewrite.RequiredOption is not null))
        {
            // The ids are the ones this run actually kept waiting for an option, so the explanation is
            // true for every language instead of naming the C# rules from the neutral pipeline.
            var optionDriven = enabled
                .Where(rewrite => rewrite.RequiredOption is not null)
                .SelectMany(rewrite => rewrite.Rules)
                .Select(rule => rule.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            styleOptions = new
            {
                source = ".editorconfig",
                braces = Summarize(observed.Select(option => option.Braces.ToString())),
                namespaces = Summarize(observed.Select(option => option.Namespaces.ToString())),
                modifierOrder = Summarize(observed.Select(option => option.ModifierOrder.Count == 0
                    ? "not set"
                    : string.Join(' ', option.ModifierOrder))),
                preferIsNot = Summarize(observed.Select(option => option.PreferIsNot.ToString())),
                note = $"{string.Join(", ", optionDriven)} only act when the repository configures their option.",
            };
        }

        return new
        {
            @refactoring = _refactoring,
            scope = level,
            rules = enabled.SelectMany(rewrite => rewrite.Rules).Select(rule => rule.Id).Distinct().ToList(),
            dryRun,
            applied = !dryRun,
            suggestions = reported.GroupBy(edit => edit.Rule.Id)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            filesChanged = accepted.Count,
            totalEdits = reported.Count,
            edits = reported.Take(200).Select(edit => new
            {
                rule = edit.Rule.Id,
                title = edit.Rule.Title,
                file = edit.File,
                line = edit.Line,
                before = Compress(edit.Before),
                after = Compress(edit.After),
            }).ToList(),
            files = accepted.Take(50).Select(change => new
            {
                file = change.Document.FilePath,
                preview = Snitch.Languages.CSharp.Refactoring.RefactoringFileWriter.Diff(change.OldText, change.NewText),
            }).ToList(),
            styleOptions,
            semantics = semantics.Checked == 0
                ? null
                : new
                {
                    compared = semantics.Checked,
                    verified = semantics.Checked - semantics.Failures.Count,
                    note = "Rules that claim equivalence are compared operation-by-operation, not only against the compiler. A node with no operation of its own is not compared.",
                    failures = semantics.Failures,
                },
            refused,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : "Files written.",
        };
    }

    /// <summary>One value when every document agrees, "mixed" when they do not, "not set" when unset.</summary>
    private static string Summarize(IEnumerable<string> values)
    {
        var distinct = values.Where(value => value != nameof(BracePreference.None)).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count switch
        {
            0 => "not set",
            1 => distinct[0],
            _ => "mixed",
        };
    }

    // ── selection ────────────────────────────────────────────────────────────
    private List<StyleRewrite> SelectRewrites(string? rules, out List<string> unknown)
    {
        unknown = new List<string>();

        // "all" means every local suggestion: the whole-file formatting pass has to be asked for by id.
        if (string.IsNullOrWhiteSpace(rules) || rules.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return _ruleSet.All.Where(rewrite => !rewrite.IsOptIn).ToList();

        var wanted = rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = new List<StyleRewrite>();

        foreach (var id in wanted)
        {
            var matches = _ruleSet.All
                .Where(rewrite => rewrite.Rules.Any(rule => rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0) unknown.Add(id);
            else selected.AddRange(matches.Where(rewrite => !selected.Contains(rewrite)));
        }

        return selected;
    }

    private async Task<(IReadOnlyList<Document> Documents, object? Error)> SelectDocumentsAsync(
        Solution solution, string scope, string? filePath, string? projectName, CancellationToken cancellationToken)
    {        if (scope == "document")
        {
            if (string.IsNullOrWhiteSpace(filePath)) return ([], new { error = "scope=document needs filePath." });

            var full = Path.GetFullPath(filePath);
            var document = solution.Projects.SelectMany(project => project.Documents)
                .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                             string.Equals(Path.GetFullPath(candidate.FilePath), full, StringComparison.OrdinalIgnoreCase));

            return document is null
                ? ([], new { error = $"{full} is not a document of the loaded solution." })
                : ([document], null);
        }

        if (scope == "project")
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return ([], new
                {
                    error = "scope=project needs projectName.",
                    projects = solution.Projects.Select(project => project.Name).ToList(),
                });

            var project = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null)
                return ([], new { error = $"No project '{projectName}'.", projects = solution.Projects.Select(p => p.Name).ToList() });

            return (await ProjectDocumentsAsync(project, cancellationToken), null);
        }

        var all = new List<Document>();
        foreach (var project in solution.Projects)
            all.AddRange(await ProjectDocumentsAsync(project, cancellationToken));

        return (all, null);
    }

    private async Task<List<Document>> ProjectDocumentsAsync(Project project, CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var document in project.Documents)
        {
            if (document.FilePath is null) continue;
            if (!document.FilePath.EndsWith(_language.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (await document.GetSyntaxRootAsync(cancellationToken) is null) continue;

            documents.Add(document);
        }

        return documents;
    }

    // ── rewriting ────────────────────────────────────────────────────────────
    /// <summary>
    /// Rewrites one document in up to <see cref="MaxPassesPerDocument"/> passes: a nested suggestion
    /// (a redundant cast inside a collection initializer) is only reachable once the outer one is gone.
    /// Each pass rebuilds the semantic model, because the previous pass produced a tree the document's
    /// own model no longer covers.
    /// </summary>
    private async Task<string?> RewriteAsync(
        Document document,
        List<StyleRewrite> rewrites,
        List<StyleMatch> edits,
        List<StyleMatch> spanMatches,
        List<StyleOptions> observed,
        List<SemanticFailure> semanticFailures,
        SemanticAudit semantics,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || tree is null || compilation is null || model is null) return null;

        var options = _language.ReadOptions(document, tree);
        observed.Add(options);
        var workspace = document.Project.Solution.Workspace;

        var touched = false;

        for (var pass = 0; pass < MaxPassesPerDocument; pass++)
        {
            var context = new StyleContext(model, options, workspace, document.Project.Solution, cancellationToken,
                pristine: pass == 0);

            // A match inside another match would be discarded with its parent: leave it to the next pass.
            var outer = Outermost(Collect(root, context, rewrites));
            if (outer.Count == 0) break;

            var file = document.FilePath ?? document.Name;

            // A multi-document suggestion is not this document's business: it is collected and applied
            // once the whole project has been planned, because its edits name other files.
            var multiDocument = outer.Where(match => match.SpanEdits is { Count: > 0 }).ToList();
            if (multiDocument.Count > 0)
            {
                foreach (var match in multiDocument) spanMatches.Add(match with { File = file });
                outer = outer.Where(match => match.SpanEdits is not { Count: > 0 }).ToList();
                if (outer.Count == 0) break;
            }

            // The second level of the gate. A rule that claims equivalence must produce the same
            // operation tree: compiling proves syntax, this proves meaning for the rules that opt in.
            foreach (var match in outer.Where(match => match.Rule.ClaimsEquivalence))
            {
                var differences = Verify(match, root, tree, compilation, model, cancellationToken);

                // Null means the node has no operation of its own (a type name, a parenthesised
                // expression): there is nothing to prove, and claiming a proof would be worse than
                // saying nothing.
                if (differences is null) continue;

                semantics.Checked++;
                if (differences.Count == 0) continue;

                var failure = new SemanticFailure(match.Rule.Id, match.Line, file, differences);
                semantics.Failures.Add(failure);
                semanticFailures.Add(failure);
            }

            if (semanticFailures.Count > 0) return null;

            // Two worlds: expression matches are plain node replacements; statement matches span one
            // or more statements of an enclosing block, so that block's statement list is rebuilt —
            // the shape's statements come out, the replacement's statements go in. A removal is the
            // same move with nothing to put back.
            //
            // Two rules can match the same node (IDE0001 and IDE0049 both want to rewrite System.Int32):
            // one node gets one replacement, the first in catalogue order owning it, so the choice is
            // deterministic and the replacement map never sees the same key twice.
            var nonStatementMatches = outer
                .Where(match => !_language.IsStatement(match.Node))
                .GroupBy(match => match.Node)
                .Select(group => group.First())
                .ToList();

            var removals = nonStatementMatches.Where(match => match.Removes).ToList();
            var expressionMatches = nonStatementMatches.Where(match => !match.Removes).ToList();
            var statementMatches = outer.Where(match => _language.IsStatement(match.Node)).ToList();

            if (expressionMatches.Count > 0 || removals.Count > 0)
            {
                if (expressionMatches.Count > 0)
                {
                    var replacements = expressionMatches.ToDictionary(match => match.Node, match => match.Replacement);
                    root = root.ReplaceNodes(replacements.Keys, (original, _) =>
                        // A whole-file replacement (a directive edit) is not annotated: formatting the
                        // root would reformat every line of the file for a one-line change.
                        original.Parent is null
                            ? replacements[original]
                            : replacements[original].WithAdditionalAnnotations(Formatter.Annotation));
                    edits.AddRange(expressionMatches.Select(match => match with { File = file }));
                }

                foreach (var removal in removals)
                {
                    root = root!.RemoveNode(removal.Node, SyntaxRemoveOptions.KeepNoTrivia);
                    edits.Add(removal with { File = file });
                }

                touched = true;

                // Replacing a node rebuilds every node of the tree — siblings included — so each
                // statement match found above now points at a node that is no longer in it, and
                // splicing it would silently change nothing. Rebind, then find the statement shapes
                // again in the tree that phase one produced.
                tree = root.SyntaxTree;
                compilation = compilation.ReplaceSyntaxTree(
                    compilation.SyntaxTrees.FirstOrDefault(candidate =>
                        string.Equals(candidate.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)) ?? tree,
                    tree);
                model = compilation.GetSemanticModel(tree);

                statementMatches = Outermost(
                    Collect(root, new StyleContext(
                            model, options, workspace, document.Project.Solution, cancellationToken, pristine: false), rewrites)
                        .Where(match => _language.IsStatement(match.Node)).ToList());
            }

            foreach (var group in statementMatches
                         .GroupBy(match => match.Node.Parent)
                         .Where(group => group.Key is not null && _language.StatementsOf(group.Key).Count > 0))
            {
                var block = group.Key!;
                var statements = _language.StatementsOf(block);
                var groupMatches = group.ToList();

                var newStatements = new List<SyntaxNode>();
                var applied = new List<StyleMatch>();

                for (var index = 0; index < statements.Count; index++)
                {
                    var statement = statements[index];
                    var match = groupMatches.FirstOrDefault(candidate => candidate.Node == statement);
                    if (match is null)
                    {
                        newStatements.Add(statement);
                        continue;
                    }

                    var last = match.Last ?? match.Node;
                    while (index < statements.Count && statements[index] != last)
                        index++;

                    var end = statements[Math.Min(index, statements.Count - 1)];

                    applied.Add(match);

                    // A removal takes the statements out and puts nothing back.
                    if (match.Removes) continue;

                    // The shape's text was parsed back, which drops the trivia around it: the blank
                    // line after the replaced block belongs to the file, not to the suggestion.
                    var additions = _language.IsShapeWrapper(match.Replacement)
                        ? _language.StatementsOf(match.Replacement).ToList()
                        : _language.IsStatement(match.Replacement) ? [match.Replacement] : [];

                    if (additions.Count > 0)
                    {
                        additions[0] = additions[0].WithLeadingTrivia(statement.GetLeadingTrivia());
                        additions[^1] = additions[^1].WithTrailingTrivia(end.GetTrailingTrivia());
                        newStatements.AddRange(additions.Select(added => added.WithAdditionalAnnotations(Formatter.Annotation)));
                    }
                }

                if (newStatements.Count != statements.Count ||
                    newStatements.Where((statement, index) => statement != statements[index]).Any())
                {
                    root = root.ReplaceNode(block, _language.WithStatements(block, newStatements));
                    touched = true;
                    edits.AddRange(applied.Select(match => match with { File = file }));
                }
            }
            tree = root.SyntaxTree;
            compilation = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)) ?? tree,
                tree);
            model = compilation.GetSemanticModel(tree);
        }

        if (!touched) return root.ToFullString();

        // Only the nodes this engine produced carry the annotation, so the formatter fixes their
        // indentation — a statement spliced out of a removed block would otherwise land at column 0 —
        // without reformatting the rest of the file.
        var reformatted = await Formatter.FormatAsync(
            document.WithSyntaxRoot(root), Formatter.Annotation, options: null, cancellationToken);

        return (await reformatted.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Replaces the matched node with its own replacement in a throwaway tree and reads the operation of
    /// the node that comes out. Nothing is written: this is a probe, which is why a failure can be a
    /// refusal instead of a rollback.
    /// </summary>
    private static IReadOnlyList<string>? Verify(
        StyleMatch match,
        SyntaxNode root,
        SyntaxTree tree,
        Compilation compilation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var before = model.GetOperation(match.Node, cancellationToken);

        // GetOperation walks up to the nearest operation when the node is not one (a type name in a
        // signature returns the enclosing method body). Comparing that with the replacement's
        // operation would compare two unrelated things, so such a match is simply not verifiable.
        if (before is null || before.Syntax != match.Node) return null;

        var marker = new SyntaxAnnotation();
        var probeRoot = root.ReplaceNode(match.Node, match.Replacement.WithAdditionalAnnotations(marker));
        var probeTree = tree.WithRootAndOptions(probeRoot, tree.Options);
        var probeCompilation = compilation.ReplaceSyntaxTree(tree, probeTree);
        var probeModel = probeCompilation.GetSemanticModel(probeTree);

        // The node has to come from the tree the model was built over, not from the detached root:
        // GetOperation refuses a node that does not belong to its tree.
        var replacement = probeTree.GetRoot(cancellationToken).GetAnnotatedNodes(marker).FirstOrDefault();
        var after = replacement is null ? null : probeModel.GetOperation(replacement, cancellationToken);
        if (after is null || after.Syntax != replacement) return null;

        return SemanticEquivalence.Differences(before, after);
    }

    /// <summary>How many claimed-equivalence rewrites were checked, and the ones that failed.</summary>
    private sealed class SemanticAudit
    {
        public int Checked { get; set; }

        public List<SemanticFailure> Failures { get; } = [];
    }

    private static List<StyleMatch> Collect(SyntaxNode root, StyleContext context, List<StyleRewrite> rewrites)
    {
        var matches = new List<StyleMatch>();

        // The root itself is offered to the matchers: the formatting rule works on the whole unit.
        foreach (var node in root.DescendantNodesAndSelf())
        {
            foreach (var rewrite in rewrites)
            {
                if (rewrite.TryMatch(node, context) is not { } match) continue;
                matches.Add(match);
                break;
            }
        }

        return matches;
    }

    /// <summary>
    /// Drops a match that sits inside another one: the parent's replacement carries its node away, so
    /// applying both would be an edit on a node that is gone. The next pass finds the inner one again.
    /// </summary>
    private static List<StyleMatch> Outermost(List<StyleMatch> matches) =>
        matches.Where(match => !matches.Any(other => other != match && other.Node.Span.Contains(match.Node.Span)))
               .ToList();

    /// <summary>
    /// Resolves the suggestions whose edits live in several documents — a removed parameter and the
    /// argument every call site passed to it. Each target file is patched from its original text with
    /// its edits applied last-first, so earlier spans stay valid. The files may belong to any project of
    /// the solution: the plan is added to whichever project owns each document, and each of those
    /// projects goes through the compiler gate above.
    /// </summary>
    private static List<SpanPlan> PlanSpanEdits(
        Solution solution,
        List<StyleMatch> matches,
        List<ProjectPlan> plans,
        List<StyleMatch> edits,
        List<object> refused)
    {
        var resolved = new List<SpanPlan>();

        foreach (var match in matches)
        {
            var groups = match.SpanEdits!
                .GroupBy(edit => edit.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var changes = new List<PlannedDocument>();
            string? blocker = null;
            string? reason = null;

            foreach (var group in groups)
            {
                // A file that is already being rewritten as a syntax tree has new text: the offsets of
                // this suggestion point into the text it is about to replace.
                if (plans.Any(plan => plan.Planned.Any(change => SamePath(change.Document, group.Key))) ||
                    changes.Any(change => SamePath(change.Document, group.Key)))
                {
                    blocker = group.Key;
                    reason = "the edit spans a file that another suggestion also rewrites";
                    break;
                }

                var document = FindDocument(solution, group.Key);
                if (document is null)
                {
                    blocker = group.Key;
                    reason = "the edit names a file that is not part of the loaded solution";
                    break;
                }

                var before = document.GetTextAsync().GetAwaiter().GetResult().ToString();
                var after = before;

                foreach (var edit in group.OrderByDescending(edit => edit.Start))
                {
                    if (edit.Start < 0 || edit.Start + edit.Length > after.Length)
                    {
                        blocker = group.Key;
                        reason = "the edit points outside the file it names";
                        break;
                    }

                    after = string.Concat(after.AsSpan(0, edit.Start), edit.Replacement, after.AsSpan(edit.Start + edit.Length));
                }

                if (blocker is not null) break;
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    changes.Add(new PlannedDocument(document, before, after));
            }

            if (blocker is not null)
            {
                refused.Add(new { file = match.File, reason, error = blocker });
                continue;
            }

            if (changes.Count == 0) continue;

            resolved.Add(new SpanPlan(match, changes));
            foreach (var change in changes)
                plans.FirstOrDefault(plan => plan.Project.Id == change.Document.Project.Id)?.Planned.Add(change);

            edits.Add(match);
        }

        return resolved;
    }

    private static Document? FindDocument(Solution solution, string path) =>
        solution.Projects.SelectMany(project => project.Documents).FirstOrDefault(document => SamePath(document, path));

    private static bool SamePath(Document document, string path) =>
        document.FilePath is not null && string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase);

    private static string Compress(string text)
    {
        var single = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
        return single.Length <= 160 ? single : single[..160] + "…";
    }

    // ── compiler gate ────────────────────────────────────────────────────────
    private sealed record PlannedDocument(Document Document, string OldText, string NewText);

    /// <summary>One project's planned new texts, gated together.</summary>
    private sealed record ProjectPlan(Project Project, List<PlannedDocument> Planned);

    /// <summary>A multi-document suggestion and the documents it planned, for the all-or-nothing check.</summary>
    private sealed record SpanPlan(StyleMatch Match, List<PlannedDocument> Changes);
}
