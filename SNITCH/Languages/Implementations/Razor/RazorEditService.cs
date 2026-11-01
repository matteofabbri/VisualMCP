using Snitch.Platform;
using Snitch.Storage;

namespace Snitch.Languages.Razor;

/// <summary>
/// One requested change to a Razor file. The operations are markup-aware where they can be — an
/// attribute is set on a chosen element, a directive goes after the last directive — and line-based
/// where the caller knows exactly which lines to touch.
/// </summary>
public sealed record RazorEditRequest
{
    /// <summary>The <c>.cshtml</c>/<c>.razor</c> file to edit.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// <c>set-attribute</c>, <c>remove-attribute</c>, <c>add-directive</c>, <c>insert-lines</c>,
    /// <c>replace-lines</c> or <c>delete-lines</c>.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>Element name for the attribute operations, e.g. <c>a</c> or <c>form</c>.</summary>
    public string? Tag { get; init; }

    /// <summary>Which occurrence of <see cref="Tag"/>, 1-based, in document order.</summary>
    public int Nth { get; init; } = 1;

    /// <summary>The attribute to set or remove.</summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// The new value. Null writes a valueless attribute (<c>disabled</c>); the value is written
    /// verbatim, so a Razor expression is allowed (<c>@Model.Url</c>).
    /// </summary>
    public string? AttributeValue { get; init; }

    /// <summary>The text to insert or substitute, for the line operations.</summary>
    public string? Text { get; init; }

    /// <summary>First line of a line operation (1-based).</summary>
    public int FromLine { get; init; }

    /// <summary>Last line of a line operation (1-based, inclusive); 0 means <see cref="FromLine"/>.</summary>
    public int ToLine { get; init; }

    /// <summary>
    /// The text the caller expects to find. When given, the edit is refused unless the range (or the
    /// attribute value) still holds exactly that, which is what stops an edit from landing on a file
    /// somebody changed in the meantime.
    /// </summary>
    public string? Expect { get; init; }

    /// <summary>The directive for <c>add-directive</c>, without the leading <c>@</c>.</summary>
    public string? Directive { get; init; }

    /// <summary>For <c>insert-lines</c>: <c>before</c>, <c>after</c> (default), <c>start</c> or <c>end</c>.</summary>
    public string Position { get; init; } = "after";

    /// <summary>true (the tool default) previews only; false writes the file.</summary>
    public bool DryRun { get; init; } = true;
}

/// <summary>
/// Editing for Razor markup: the counterpart of <see cref="IRazorPagesService"/> and
/// <see cref="IAnalyzeRazorPagesService"/>, which only read. Every edit is a single text splice
/// computed from the scanner's model, verified in memory against the scanner itself (an edit that
/// raises the syntax-issue count is refused and nothing is written), recorded in the solution's edit
/// history and therefore revertible with <c>revert_edit</c>.
/// </summary>
public interface IRazorEditService
{
    Task<object> EditAsync(RazorEditRequest request, CancellationToken cancellationToken = default);
}

public sealed class RazorEditService : IRazorEditService
{
    /// <summary>How many rule ids the before/after comparison lists.</summary>
    private const int MaxReportedRules = 12;

    private readonly IVisualMcpStore? _store;

    /// <summary>
    /// The store is optional so the service is usable without a loaded solution (a plain edit still
    /// works); when it is there, the write is recorded and becomes revertible.
    /// </summary>
    public RazorEditService(IVisualMcpStore? store = null) => _store = store;

    public async Task<object> EditAsync(RazorEditRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return new { error = "A path is required: point it at the .cshtml or .razor file to edit." };

        var fullPath = Resolve(request.Path);
        if (!File.Exists(fullPath))
            return new { error = $"File not found: {fullPath}" };
        if (!IsRazorFile(fullPath))
            return new { error = $"'{fullPath}' is not a .cshtml or .razor file." };

        var operation = NormalizeOperation(request.Operation);
        if (operation is null)
            return new
            {
                error = $"Unknown operation '{request.Operation}'.",
                operations = SupportedOperations,
            };

        var beforeText = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var before = Scan(fullPath, beforeText);
        var beforeFindings = RazorRules.Run(before);

        var span = Build(operation, request, fullPath, beforeText, before);
        if (span.Problem is not null)
            return new { error = span.Problem, file = fullPath, operation };

        var afterText = FileText.NormalizeFor(fullPath, beforeText.Remove(span.Offset, span.Length).Insert(span.Offset, span.Replacement));
        if (string.Equals(afterText, beforeText, StringComparison.Ordinal))
        {
            return new
            {
                file = fullPath,
                operation,
                changed = false,
                applied = false,
                note = span.Note ?? "The file already holds the requested text.",
            };
        }

        // The gate: Razor has no compiler here, so the scanner is the verifier. An edit that leaves
        // more unbalanced markup than it found is refused instead of written.
        var after = Scan(fullPath, afterText);
        var issuesBefore = before.SyntaxIssues.Count;
        var issuesAfter = after.SyntaxIssues.Count;
        if (issuesAfter > issuesBefore)
        {
            return new
            {
                error = $"Refused: the edit would raise the unbalanced-syntax count from {issuesBefore} to {issuesAfter}. Nothing was written.",
                file = fullPath,
                operation,
                newIssues = after.SyntaxIssues.Skip(issuesBefore).Take(MaxReportedRules).Select(issue => new
                {
                    ruleId = issue.RuleId,
                    line = issue.Line,
                    column = issue.Column,
                    message = issue.Message,
                }),
            };
        }

        var afterFindings = RazorRules.Run(after);
        var diff = TextDiff.Lines(beforeText, afterText);

        if (request.DryRun)
        {
            return new
            {
                file = fullPath,
                operation,
                applied = false,
                dryRun = true,
                changed = true,
                note = "Preview only: pass dryRun=false to write the file.",
                diff,
                findings = Delta(beforeFindings, afterFindings),
            };
        }

        await FileText.WriteAsync(fullPath, afterText, cancellationToken);

        var recorded = false;
        if (_store is not null)
        {
            try
            {
                _store.RecordEdit("razor_edit", fullPath, operation, beforeText, afterText);
                recorded = _store.EditCount() > 0;
            }
            catch (Exception)
            {
                // A store that cannot be written (no solution, read-only folder) must not turn a
                // written edit into a failed one: the file changed either way.
                recorded = false;
            }
        }

        return new
        {
            file = fullPath,
            operation,
            applied = true,
            changed = true,
            linesBefore = before.LineCount,
            linesAfter = after.LineCount,
            charsBefore = beforeText.Length,
            charsAfter = afterText.Length,
            history = recorded ? "recorded (revert with revert_edit)" : "unavailable (no solution store)",
            diff,
            findings = Delta(beforeFindings, afterFindings),
        };
    }

    // ── Operations ─────────────────────────────────────────────────────────────

    private static readonly string[] SupportedOperations =
    [
        "set-attribute", "remove-attribute", "add-directive", "insert-lines", "replace-lines", "delete-lines",
    ];

    private Splice Build(string operation, RazorEditRequest request, string path, string text, RazorDocument document) =>
        operation switch
        {
            "set-attribute" => SetAttribute(request, text, document),
            "remove-attribute" => RemoveAttribute(request, text, document),
            "add-directive" => AddDirective(request, text, document),
            "insert-lines" => InsertLines(request, text),
            "replace-lines" => ReplaceLines(request, text),
            "delete-lines" => DeleteLines(request, text),
            _ => Splice.Fail($"Unknown operation '{operation}'."),
        };

    /// <summary>Adds the attribute, or replaces its value when it is already there.</summary>
    private static Splice SetAttribute(RazorEditRequest request, string text, RazorDocument document)
    {
        if (string.IsNullOrWhiteSpace(request.AttributeName))
            return Splice.Fail("set-attribute needs attribute=<name>.");
        if (string.IsNullOrWhiteSpace(request.Tag))
            return Splice.Fail("set-attribute needs tag=<element name>, e.g. tag=a.");

        var element = Select(document, request.Tag, request.Nth, out var error);
        if (element is null) return Splice.Fail(error!);

        var name = request.AttributeName.Trim();
        var existing = element.Attribute(name);
        var rendered = request.AttributeValue is null
            ? " " + name
            : " " + name + "=\"" + Escape(request.AttributeValue) + "\"";

        if (existing is null)
        {
            var tagEnd = StartTagEnd(text, element);
            if (tagEnd < 0)
                return Splice.Fail($"The <{element.Name}> start tag at line {element.Line} is not closed; fix the markup first.");

            // A self-closing tag keeps its ` />`: the attribute goes before the space, not between it
            // and the slash.
            var insertAt = tagEnd;
            if (tagEnd > 0 && text[tagEnd - 1] == '/')
            {
                insertAt = tagEnd - 1;
                if (insertAt > 0 && char.IsWhiteSpace(text[insertAt - 1]))
                    insertAt--;
            }

            var needsSpace = insertAt > 0 && !char.IsWhiteSpace(text[insertAt - 1]);
            return new Splice(insertAt, 0, (needsSpace ? " " : string.Empty) + rendered.TrimStart());
        }

        if (!existing.HasValue)
            return new Splice(existing.Offset, name.Length, request.AttributeValue is null ? name : name + "=\"" + Escape(request.AttributeValue) + "\"");

        var valueSpan = AttributeValueSpan(text, existing);
        if (valueSpan is null)
            return Splice.Fail($"Could not locate the value of {name} on <{element.Name}> at line {existing.Line}.");

        var current = text.Substring(valueSpan.Value.Start, valueSpan.Value.Length);
        if (existing.ValueHasRazor && request.Expect is null)
            return Splice.Fail(
                $"{name} on <{element.Name}> (line {existing.Line}) holds the Razor expression '{Shorten(current)}'. " +
                "Pass expect=<that text> to overwrite it deliberately.");

        if (request.Expect is not null && !Matches(current, request.Expect))
            return Splice.Fail($"Expected '{Shorten(request.Expect)}' but the value is '{Shorten(current)}'. Nothing was written.");

        return new Splice(valueSpan.Value.Start, valueSpan.Value.Length, Escape(request.AttributeValue ?? string.Empty));
    }

    private static Splice RemoveAttribute(RazorEditRequest request, string text, RazorDocument document)
    {
        if (string.IsNullOrWhiteSpace(request.AttributeName))
            return Splice.Fail("remove-attribute needs attribute=<name>.");
        if (string.IsNullOrWhiteSpace(request.Tag))
            return Splice.Fail("remove-attribute needs tag=<element name>, e.g. tag=div.");

        var element = Select(document, request.Tag, request.Nth, out var error);
        if (element is null) return Splice.Fail(error!);

        var name = request.AttributeName.Trim();
        var existing = element.Attribute(name);
        if (existing is null)
            return new Splice(0, 0, string.Empty) { Note = $"<{element.Name}> at line {element.Line} has no {name} attribute." };

        var end = AttributeEnd(text, existing);
        if (end < 0)
            return Splice.Fail($"Could not determine where {name} ends on <{element.Name}> at line {existing.Line}.");

        // One preceding whitespace character goes with it, so removing does not leave a double space.
        var start = existing.Offset;
        if (start > 0 && char.IsWhiteSpace(text[start - 1]))
            start--;

        return new Splice(start, end - start, string.Empty);
    }

    /// <summary>Inserts a directive after the last one the page already declares.</summary>
    private static Splice AddDirective(RazorEditRequest request, string text, RazorDocument document)
    {
        if (string.IsNullOrWhiteSpace(request.Directive))
            return Splice.Fail("add-directive needs directive=<text>, e.g. directive=model MyViewModel (no leading @).");

        var body = request.Directive.Trim().TrimStart('@').Trim();
        if (body.Length == 0) return Splice.Fail("add-directive got an empty directive.");

        var name = body.Split(' ', '\t')[0];
        var value = body.Length > name.Length ? body[(name.Length + 1)..].Trim() : string.Empty;

        if (document.Directives.Any(directive =>
                directive.Name.Equals(name, StringComparison.Ordinal) &&
                directive.Value.Equals(value, StringComparison.Ordinal)))
        {
            return new Splice(0, 0, string.Empty) { Note = $"@{(value.Length > 0 ? name + " " + value : name)} is already there." };
        }

        var afterLine = document.Directives.Count == 0 ? 0 : document.Directives.Max(directive => directive.Line);
        var newline = Newline(text);
        var line = "@" + (value.Length > 0 ? name + " " + value : name);

        if (afterLine == 0)
            return new Splice(0, 0, line + newline);

        var offset = LineEndOffset(document.LineStarts, afterLine, text.Length);

        // A last directive on a final line without a newline: the new directive needs one before it,
        // or it would be glued to the end of that line.
        if (offset == text.Length && text.Length > 0 && text[^1] != '\n')
            return new Splice(offset, 0, newline + line + newline);

        return new Splice(offset, 0, line + newline);
    }

    private static Splice InsertLines(RazorEditRequest request, string text)
    {
        if (request.Text is null) return Splice.Fail("insert-lines needs text.");
        if (request.FromLine <= 0) return Splice.Fail("insert-lines needs fromLine (or position=start|end).");

        var newline = Newline(text);
        var position = (request.Position ?? "after").Trim().ToLowerInvariant();
        var starts = RazorText.LineStarts(text);
        if (!TryCheckExpect(request, text, starts, request.FromLine, request.FromLine, out var mismatch))
            return Splice.Fail(mismatch!);

        int offset;
        switch (position)
        {
            case "start":
                offset = 0;
                break;
            case "end":
                offset = text.Length;
                break;
            case "before":
                offset = LineStartOffset(starts, request.FromLine, text.Length);
                break;
            case "after":
                offset = LineEndOffset(starts, request.FromLine, text.Length);
                break;
            default:
                return Splice.Fail($"Unknown position '{request.Position}'; use before, after, start or end.");
        }

        var payload = NormalizeNewlines(request.Text, newline);
        if (offset == text.Length && text.Length > 0 && text[^1] != '\n')
            payload = newline + payload;
        if (offset < text.Length && payload.Length > 0 && payload[^1] != '\n')
            payload += newline;

        return new Splice(offset, 0, payload);
    }

    private static Splice ReplaceLines(RazorEditRequest request, string text)
    {
        if (request.Text is null) return Splice.Fail("replace-lines needs text.");
        if (request.FromLine <= 0) return Splice.Fail("replace-lines needs fromLine.");

        var toLine = request.ToLine <= 0 ? request.FromLine : request.ToLine;
        var starts = RazorText.LineStarts(text);
        if (!InRange(starts, request.FromLine) || !InRange(starts, toLine))
            return Splice.Fail($"Line range {request.FromLine}..{toLine} is outside the file (it has {starts.Length} line(s)).");
        if (toLine < request.FromLine)
            return Splice.Fail($"toLine ({toLine}) is before fromLine ({request.FromLine}).");
        if (!TryCheckExpect(request, text, starts, request.FromLine, toLine, out var mismatch))
            return Splice.Fail(mismatch!);

        var offset = LineStartOffset(starts, request.FromLine, text.Length);
        var end = LineEndOffset(starts, toLine, text.Length);
        var newline = Newline(text);
        var payload = NormalizeNewlines(request.Text, newline);
        if (payload.Length > 0 && end > offset && text[end - 1] == '\n' && payload[^1] != '\n')
            payload += newline;

        return new Splice(offset, end - offset, payload);
    }

    private static Splice DeleteLines(RazorEditRequest request, string text)
    {
        if (request.FromLine <= 0) return Splice.Fail("delete-lines needs fromLine.");

        var toLine = request.ToLine <= 0 ? request.FromLine : request.ToLine;
        var starts = RazorText.LineStarts(text);
        if (!InRange(starts, request.FromLine) || !InRange(starts, toLine))
            return Splice.Fail($"Line range {request.FromLine}..{toLine} is outside the file (it has {starts.Length} line(s)).");
        if (toLine < request.FromLine)
            return Splice.Fail($"toLine ({toLine}) is before fromLine ({request.FromLine}).");
        if (!TryCheckExpect(request, text, starts, request.FromLine, toLine, out var mismatch))
            return Splice.Fail(mismatch!);

        return new Splice(
            LineStartOffset(starts, request.FromLine, text.Length),
            LineEndOffset(starts, toLine, text.Length) - LineStartOffset(starts, request.FromLine, text.Length),
            string.Empty);
    }

    // ── Verification ───────────────────────────────────────────────────────────

    /// <summary>Compares the range the caller named against the text it expects to find there.</summary>
    private static bool TryCheckExpect(
        RazorEditRequest request, string text, int[] starts, int fromLine, int toLine, out string? error)
    {
        error = null;
        if (request.Expect is null) return true;

        var start = LineStartOffset(starts, fromLine, text.Length);
        var end = LineEndOffset(starts, toLine, text.Length);
        var actual = text[start..end];

        if (Matches(actual, request.Expect)) return true;

        error = $"Expected '{Shorten(request.Expect)}' on lines {fromLine}..{toLine}, found '{Shorten(actual)}'. " +
                "Nothing was written: re-read the lines and pass the text they hold now.";
        return false;
    }

    /// <summary>
    /// Same text, ignoring the line-ending style and the trailing newline of the range: what the
    /// caller sees in read_file is what it passes back.
    /// </summary>
    private static bool Matches(string actual, string expected) =>
        string.Equals(
            actual.Replace("\r\n", "\n").TrimEnd('\n'),
            expected.Replace("\r\n", "\n").TrimEnd('\n'),
            StringComparison.Ordinal);

    // ── Model helpers ──────────────────────────────────────────────────────────

    /// <summary>The element the caller asked for, or an explanation of what exists instead.</summary>
    private static RazorElement? Select(RazorDocument document, string tag, int nth, out string? error)
    {
        error = null;
        var matches = document.AllElements()
            .Where(element => element.Name.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            error = $"No <{tag.Trim()}> element in the file.";
            return null;
        }

        var index = nth <= 0 ? 1 : nth;
        if (index > matches.Count)
        {
            var lines = string.Join(", ", matches.Take(MaxReportedRules).Select(element => element.Line));
            error = $"<{tag.Trim()}> occurs {matches.Count} time(s); nth={index} is out of range. Lines: {lines}.";
            return null;
        }

        return matches[index - 1];
    }

    /// <summary>The index of the <c>&gt;</c> that ends the start tag, or -1 when the tag never ends.</summary>
    private static int StartTagEnd(string text, RazorElement element)
    {
        var quote = '\0';
        for (var i = element.Offset + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '<') return -1;
            if (c == '>') return i;
        }

        return -1;
    }

    /// <summary>The text span of an attribute's value, without its quotes.</summary>
    private static (int Start, int Length)? AttributeValueSpan(string text, RazorAttribute attribute)
    {
        var i = attribute.Offset + attribute.Name.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '=') return null;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return null;

        if (text[i] is '"' or '\'')
        {
            var quote = text[i];
            var start = i + 1;
            var end = text.IndexOf(quote, start);
            return end < 0 ? null : (start, end - start);
        }

        var unquotedEnd = i;
        while (unquotedEnd < text.Length && !char.IsWhiteSpace(text[unquotedEnd]) && text[unquotedEnd] != '>')
            unquotedEnd++;
        return (i, unquotedEnd - i);
    }

    /// <summary>Index just past an attribute (name, optional value, optional quotes).</summary>
    private static int AttributeEnd(string text, RazorAttribute attribute)
    {
        var i = attribute.Offset + attribute.Name.Length;
        var end = i;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '=') return end;

        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return i;

        if (text[i] is '"' or '\'')
        {
            var close = text.IndexOf(text[i], i + 1);
            return close < 0 ? -1 : close + 1;
        }

        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>') i++;
        return i;
    }

    // ── Line helpers ───────────────────────────────────────────────────────────

    private static int LineStartOffset(int[] starts, int line, int length) =>
        line < 1 ? 0 : line > starts.Length ? length : starts[line - 1];

    /// <summary>Start of the line after <paramref name="line"/>: the end of the line including its newline.</summary>
    private static int LineEndOffset(int[] starts, int line, int length) =>
        line < 1 ? 0 : line >= starts.Length ? length : starts[line];

    private static bool InRange(int[] starts, int line) =>
        line >= 1 && line <= starts.Length;

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string NormalizeNewlines(string text, string newline) =>
        newline == "\n" ? text.Replace("\r\n", "\n") : text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    // ── Small shared bits ──────────────────────────────────────────────────────

    /// <summary>Scans the text and classifies the page the way the read-only tools do.</summary>
    private static RazorDocument Scan(string path, string text)
    {
        var document = RazorScanner.Scan(path, text);
        document.Kind = RazorPageClassifier.Classify(document, path);
        return document;
    }

    /// <summary>What the edit did to the rule findings, by rule id: counts are comparable, lines are not.</summary>
    private static object Delta(List<RazorFinding> before, List<RazorFinding> after)
    {
        var beforeCounts = Counts(before);
        var afterCounts = Counts(after);

        var resolved = beforeCounts.Keys.Where(id => afterCounts.GetValueOrDefault(id) < beforeCounts[id]).OrderBy(id => id).ToList();
        var introduced = afterCounts.Keys.Where(id => beforeCounts.GetValueOrDefault(id) < afterCounts[id]).OrderBy(id => id).ToList();

        return new
        {
            before = before.Count,
            after = after.Count,
            resolved = resolved.Select(id => new
            {
                ruleId = id,
                title = RazorRuleCatalog.TryGet(id, out var rule) ? rule.Title : id,
                count = beforeCounts[id] - afterCounts.GetValueOrDefault(id),
            }),
            introduced = introduced.Select(id => new
            {
                ruleId = id,
                title = RazorRuleCatalog.TryGet(id, out var rule) ? rule.Title : id,
                count = afterCounts[id] - beforeCounts.GetValueOrDefault(id),
            }),
        };
    }

    private static Dictionary<string, int> Counts(List<RazorFinding> findings) =>
        findings
            .GroupBy(finding => finding.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string Resolve(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));

    private static bool IsRazorFile(string path) =>
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOperation(string operation) => operation.Trim().ToLowerInvariant().Replace('_', '-') switch
    {
        "set-attribute" or "set-attr" or "attribute" or "set" => "set-attribute",
        "remove-attribute" or "remove-attr" or "remove" => "remove-attribute",
        "add-directive" or "directive" => "add-directive",
        "insert-lines" or "insert" => "insert-lines",
        "replace-lines" or "replace" => "replace-lines",
        "delete-lines" or "delete" => "delete-lines",
        _ => null,
    };

    private static string Escape(string value) =>
        value.Contains('"') ? value.Replace("\"", "&quot;") : value;

    private static string Shorten(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 80 ? collapsed : collapsed[..77] + "...";
    }

    /// <summary>One splice: the replacement for <c>text[Offset .. Offset+Length)</c>, or the reason it cannot be made.</summary>
    private sealed record Splice(int Offset, int Length, string Replacement, string? Note = null)
    {
        public string? Problem { get; init; }

        public static Splice Fail(string message) => new(0, 0, string.Empty) { Problem = message };
    }
}
