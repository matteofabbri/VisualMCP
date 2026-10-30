using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Search;

/// <summary>
/// ReSharper's Structural Search and Replace: a pattern is ordinary C# with <c>$name$</c> holes,
/// matched against the syntax tree of the solution and, when a replacement is given, rewritten in
/// place. Unlike text search this respects the tree — trivia, comments and string contents are never
/// mistaken for code — and unlike a symbol query it can ask for a shape ("a call to X whose argument
/// is null") that no single symbol name describes.
/// </summary>
public interface IStructuralSearchService
{
    /// <summary>
    /// Finds (and with <paramref name="replacement"/>, rewrites) every place that matches
    /// <paramref name="pattern"/>. A null replacement is a plain search.
    /// </summary>
    Task<object> Execute(
        string pattern,
        string? replacement,
        string scope,
        string? filePath,
        string? projectName,
        bool dryRun,
        int maxResults,
        CancellationToken cancellationToken = default);
}

public sealed class StructuralSearchService : IStructuralSearchService
{
    /// <summary>
    /// <c>$x$</c> is not a valid C# identifier, so the pattern is parsed with this prefix in place of
    /// the dollars; every occurrence of it in the parsed tree is a hole.
    /// </summary>
    private const string HolePrefix = "__ssr_";

    private static readonly Regex Hole = new(@"\$([A-Za-z_][A-Za-z0-9_]*)\$", RegexOptions.Compiled);

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public StructuralSearchService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> Execute(
        string pattern,
        string? replacement,
        string scope,
        string? filePath,
        string? projectName,
        bool dryRun,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return new { error = "A pattern is required." };

        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        if (maxResults <= 0) maxResults = 200;

        var parsed = Parse(pattern, out var patternError);
        if (parsed is null) return new { error = patternError };

        ParsedPattern? replacementPattern = null;
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            replacementPattern = Parse(replacement, out var replacementError);
            if (replacementPattern is null) return new { error = replacementError };

            var unbound = replacementPattern.Placeholders.Except(parsed.Placeholders, StringComparer.Ordinal).ToList();
            if (unbound.Count > 0)
                return new
                {
                    error = "The replacement uses placeholders the pattern does not bind: " +
                            string.Join(", ", unbound.Select(name => "$" + name + "$")) + ".",
                    patternPlaceholders = parsed.Placeholders.Select(name => "$" + name + "$").ToList(),
                };
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var (documents, selectionError) = SelectDocuments(solution, level, filePath, projectName);
        if (selectionError is not null) return selectionError;

        var matches = new List<StructuralMatch>();
        var truncated = false;

        foreach (var document in documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root is null) continue;

            foreach (var candidate in Candidates(root, parsed))
            {
                if (candidate.Span.Length == 0) continue;

                var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!TryMatch(parsed.Root, candidate, bindings)) continue;

                var position = text.Lines.GetLinePosition(candidate.SpanStart);
                matches.Add(new StructuralMatch(
                    document, candidate, bindings, position.Line + 1, position.Character + 1,
                    candidate.ToString().Trim()));

                if (matches.Count > maxResults) { truncated = true; break; }
            }

            if (truncated) break;
        }

        // A parent's replacement carries its children away: applying both would edit a node that no
        // longer exists, so only the outermost match of a nest is kept.
        var outermost = matches
            .Take(maxResults)
            .Where(match => !matches.Take(maxResults).Any(other => other != match && other.Node.Span.Contains(match.Node.Span)))
            .ToList();

        if (replacementPattern is null)
        {
            return new
            {
                tool = "structural_search",
                pattern,
                scope = level,
                matchCount = outermost.Count,
                truncated,
                matches = outermost
                    .Select(match => new
                    {
                        @file = match.Document.FilePath,
                        line = match.Line,
                        column = match.Column,
                        text = match.Text,
                        bindings = match.Bindings
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .ToDictionary(pair => "$" + pair.Key + "$", pair => pair.Value),
                    })
                    .ToList(),
                note = outermost.Count == 0
                    ? "No node matches the pattern in this scope."
                    : "Pass replacement to rewrite; the call is a search only when replacement is omitted.",
            };
        }

        if (outermost.Count == 0)
        {
            return new
            {
                tool = "structural_replace",
                pattern, replacement, scope = level,
                matchCount = 0, applied = false, dryRun,
                note = "No node matches the pattern in this scope; nothing to replace.",
            };
        }

        if (truncated)
            return new
            {
                error = $"The pattern matches more than {maxResults} nodes; refusing to rewrite a partial set. " +
                        "Narrow the pattern or the scope, or raise maxResults.",
                matchCount = matches.Count,
            };

        // Rewrite each document bottom-up: replacing the last span first keeps every earlier span valid.
        var changed = new Dictionary<DocumentId, string>();
        var previews = new List<object>();

        foreach (var group in outermost.GroupBy(match => match.Document.Id))
        {
            var document = group.First().Document;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root is null) continue;

            // One ReplaceNodes pass, not a ReplaceNode per match: after the first replacement the
            // other nodes belong to the old tree, and replacing them there is a silent no-op.
            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
            foreach (var match in group)
            {
                var built = BuildReplacement(replacementPattern, match, text, out var buildError);
                if (built is null)
                    return new { error = buildError, text = match.Text, line = match.Line, file = match.Document.FilePath };

                replacements[match.Node] = built;
                previews.Add(new
                {
                    @file = document.FilePath,
                    line = match.Line,
                    column = match.Column,
                    before = match.Text,
                    after = built.ToString().Trim(),
                });
            }

            root = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            changed[document.Id] = root.ToFullString();
        }

        var after = solution;
        foreach (var (documentId, newText) in changed)
            after = after.WithDocumentText(documentId, SourceText.From(newText));

        var projectIds = changed.Keys
            .Select(id => solution.GetDocument(id)?.Project.Id)
            .OfType<ProjectId>()
            .Distinct()
            .ToList();

        var gate = await RefactoringCompilerGate.CompareAsync(solution, after, projectIds, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the structural replacement");

        if (!dryRun)
        {
            foreach (var documentId in changed.Keys)
            {
                var current = _workspace.CurrentSolution?.GetDocument(documentId);
                if (current is null) continue;

                var before = (await current.GetTextAsync(cancellationToken)).ToString();
                _store?.RecordEdit("structural_replace", current.FilePath ?? current.Name, pattern, before, changed[documentId]);
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, current, changed[documentId], cancellationToken);
            }
        }

        return new
        {
            tool = "structural_replace",
            pattern,
            replacement,
            scope = level,
            matchCount = outermost.Count,
            filesChanged = changed.Count,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            replacements = previews,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : "Files written; every edit is recorded in the solution's edit history.",
        };
    }

    // ── pattern parsing ──────────────────────────────────────────────────────

    private enum PatternMode { Expression, Statement, Member }

    /// <summary>A parsed pattern: the tree with holes, how it was parsed, and the holes it names.</summary>
    private sealed record ParsedPattern(
        SyntaxNode Root,
        PatternMode Mode,
        IReadOnlyList<string> Placeholders,
        bool IsExpressionWildcard,
        bool IsStatementWildcard);

    private sealed record StructuralMatch(
        Document Document,
        SyntaxNode Node,
        Dictionary<string, string> Bindings,
        int Line,
        int Column,
        string Text);

    /// <summary>
    /// Parses the pattern as a statement first (so a trailing <c>;</c> means "a statement"), then an
    /// expression, then a member declaration. A pattern that is a single <c>$x$</c> is a wildcard over
    /// every expression; <c>$x$;</c> a wildcard over every statement.
    /// </summary>
    private static ParsedPattern? Parse(string text, out string? error)
    {
        error = null;
        var normalized = Hole.Replace(text, match => HolePrefix + match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "The pattern is empty.";
            return null;
        }

        var placeholders = Hole.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (SyntaxFactory.ParseStatement(normalized) is { } statement && !statement.ContainsDiagnostics)
        {
            var isStatementWildcard =
                statement is ExpressionStatementSyntax { Expression: IdentifierNameSyntax identifier } &&
                IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(statement, PatternMode.Statement, placeholders,
                IsExpressionWildcard: false, isStatementWildcard);
        }

        if (SyntaxFactory.ParseExpression(normalized) is { } expression && !expression.ContainsDiagnostics)
        {
            var isExpressionWildcard =
                expression is IdentifierNameSyntax identifier && IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(expression, PatternMode.Expression, placeholders,
                isExpressionWildcard, IsStatementWildcard: false);
        }

        if (SyntaxFactory.ParseMemberDeclaration(normalized) is { } member && !member.ContainsDiagnostics)
            return new ParsedPattern(member, PatternMode.Member, placeholders,
                IsExpressionWildcard: false, IsStatementWildcard: false);

        error = $"'{text}' is not a valid C# expression, statement or member declaration. " +
                "Write a hole as $name$ (for example: Console.WriteLine($argument$)); a hole in statement " +
                "position carries its own semicolon (for example: if ($condition$) $body$;).";
        return null;
    }

    private static bool IsHole(string text) => text.StartsWith(HolePrefix, StringComparison.Ordinal);

    // ── matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The nodes a pattern can match. A wildcard is offered every expression (or statement); a normal
    /// pattern is offered only nodes of its own kind, which keeps a solution-wide search linear.
    /// </summary>
    private static IEnumerable<SyntaxNode> Candidates(SyntaxNode root, ParsedPattern pattern)
    {
        if (pattern.IsExpressionWildcard) return root.DescendantNodes().OfType<ExpressionSyntax>();
        if (pattern.IsStatementWildcard) return root.DescendantNodes().OfType<StatementSyntax>();

        return root.DescendantNodes().Where(node => node.RawKind == pattern.Root.RawKind);
    }

    /// <summary>
    /// Structural equality with holes. A hole binds the text of whatever sits in its position — an
    /// identifier, a whole expression, a type, a statement — and every further occurrence of the same
    /// hole must bind the same text.
    /// </summary>
    private static bool TryMatch(SyntaxNode pattern, SyntaxNode candidate, Dictionary<string, string> bindings)
    {
        if (pattern is IdentifierNameSyntax identifier && IsHole(identifier.Identifier.ValueText))
            return Bind(bindings, identifier.Identifier.ValueText[HolePrefix.Length..], candidate.ToString());

        // `$body$` in a statement position stands for one statement of any shape, a block included.
        if (pattern is ExpressionStatementSyntax { Expression: IdentifierNameSyntax hole } &&
            IsHole(hole.Identifier.ValueText))
            return Bind(bindings, hole.Identifier.ValueText[HolePrefix.Length..], candidate.ToString());

        if (pattern.RawKind != candidate.RawKind) return false;

        var patternChildren = pattern.ChildNodesAndTokens().GetEnumerator();
        var candidateChildren = candidate.ChildNodesAndTokens().GetEnumerator();

        while (true)
        {
            var hasPattern = patternChildren.MoveNext();
            var hasCandidate = candidateChildren.MoveNext();
            if (hasPattern != hasCandidate) return false;
            if (!hasPattern) return true;

            var patternChild = patternChildren.Current;
            var candidateChild = candidateChildren.Current;

            if (patternChild.IsToken)
            {
                if (!candidateChild.IsToken) return false;

                var patternToken = patternChild.AsToken();
                var candidateToken = candidateChild.AsToken();

                // A hole in a name position (`$name$`, or the name in a member declaration).
                if (IsHole(patternToken.ValueText))
                {
                    if (!Bind(bindings, patternToken.ValueText[HolePrefix.Length..], candidateToken.Text))
                        return false;
                    continue;
                }

                if (patternToken.RawKind != candidateToken.RawKind) return false;
                if (patternToken.ValueText != candidateToken.ValueText) return false;
                continue;
            }

            if (candidateChild.IsToken) return false;
            if (!TryMatch(patternChild.AsNode()!, candidateChild.AsNode()!, bindings)) return false;
        }
    }

    private static bool Bind(Dictionary<string, string> bindings, string name, string text)
    {
        if (!bindings.TryGetValue(name, out var existing))
        {
            bindings[name] = text;
            return true;
        }

        // Formatting differences must not make `$x$ + $x$` fail on `(a + b)` written twice.
        return string.Equals(Compact(existing), Compact(text), StringComparison.Ordinal);
    }

    private static string Compact(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    // ── replacement ──────────────────────────────────────────────────────────

    /// <summary>
    /// Substitutes the bound text into the replacement pattern, re-indents its continuation lines to
    /// the match's column, parses the result as the same kind of node the match was, and gives it the
    /// original node's own leading/trailing trivia so the surrounding layout is untouched.
    /// </summary>
    private static SyntaxNode? BuildReplacement(
        ParsedPattern replacement,
        StructuralMatch match,
        SourceText sourceText,
        out string? error)
    {
        error = null;

        var text = replacement.Root.ToFullString();
        foreach (var (name, bound) in match.Bindings.OrderByDescending(pair => pair.Key.Length))
        {
            var hole = Regex.Escape(HolePrefix + name);
            var trimmed = bound.TrimEnd();

            // A statement hole is written `$body$;` in the replacement. When the bound text is itself a
            // terminated statement or a block, the `;` would be left behind as an empty statement —
            // harmless in a block, a parse error as the embedded statement of an `if`.
            var holePattern = trimmed.EndsWith(';') || trimmed.EndsWith('}')
                ? hole + @"[ \t]*;?"
                : hole + @"\b";

            text = Regex.Replace(text, holePattern, _ => bound);
        }

        text = Reindent(text, IndentationOf(sourceText, match.Node.SpanStart));

        var parsed = ParseFor(match.Node, text);
        if (parsed is null || parsed.ContainsDiagnostics)
        {
            error = $"The replacement does not parse as a {Describe(match.Node)} after substitution: {text.Trim()}";
            return null;
        }

        return parsed
            .WithLeadingTrivia(match.Node.GetLeadingTrivia())
            .WithTrailingTrivia(match.Node.GetTrailingTrivia());
    }

    /// <summary>Indents the lines after the first so a multi-line substitute lines up with its site.</summary>
    private static string Reindent(string text, string indentation)
    {
        if (text.IndexOf('\n') < 0 || indentation.Length == 0) return text;

        var builder = new StringBuilder(text.Length + indentation.Length * 4);
        var lines = text.Split('\n');
        builder.Append(lines[0]);
        for (var index = 1; index < lines.Length; index++)
        {
            builder.AppendLine();
            var line = lines[index].TrimEnd('\r');
            if (line.Length > 0) builder.Append(indentation).Append(line);
        }

        return builder.ToString();
    }

    private static string IndentationOf(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, position));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }

    private static string Describe(SyntaxNode node) => node switch
    {
        StatementSyntax => "statement",
        ExpressionSyntax => "expression",
        MemberDeclarationSyntax => "member declaration",
        _ => "node",
    };

    private static SyntaxNode? ParseFor(SyntaxNode original, string text)
    {
        if (original is StatementSyntax)
            return SyntaxFactory.ParseStatement(text);

        if (original is ExpressionSyntax)
            return SyntaxFactory.ParseExpression(text);

        if (original is MemberDeclarationSyntax)
            return SyntaxFactory.ParseMemberDeclaration(text);

        return null;
    }

    // ── scope ────────────────────────────────────────────────────────────────

    private static (List<Document> Documents, object? Error) SelectDocuments(
        Solution solution, string scope, string? filePath, string? projectName)
    {
        IEnumerable<Document> documents = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .SelectMany(project => project.Documents)
            .Where(document => document.SourceCodeKind == SourceCodeKind.Regular && document.FilePath is not null);

        switch (scope)
        {
            case "document":
                if (string.IsNullOrWhiteSpace(filePath))
                    return ([], new { error = "scope=document needs filePath." });

                var path = Path.GetFullPath(filePath);
                documents = documents.Where(document =>
                    string.Equals(Path.GetFullPath(document.FilePath!), path, StringComparison.OrdinalIgnoreCase));

                if (!documents.Any())
                    return ([], new { error = $"{path} is not a document of the loaded solution." });
                break;

            case "project":
                if (string.IsNullOrWhiteSpace(projectName))
                    return ([], new { error = "scope=project needs projectName." });

                documents = documents.Where(document =>
                    string.Equals(document.Project.Name, projectName, StringComparison.OrdinalIgnoreCase));

                if (!documents.Any())
                    return ([], new { error = $"No project named '{projectName}'." });
                break;
        }

        return (documents.ToList(), null);
    }
}
