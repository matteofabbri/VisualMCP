using System.Text.RegularExpressions;

namespace Snitch.Languages.WebMarkup;

/// <summary>
/// The markup and stylesheet siblings of the Razor and SQL scanners: HTML is read as tags (with a
/// quote-aware scanner, so a '&gt;' inside an attribute does not end the tag) and CSS as rules over a
/// masked copy of the file. The rule set is the accessibility and safety floor a page is expected to
/// keep — alt text, a language, a viewport, no duplicate ids, no deprecated tags, no inline handlers —
/// plus the stylesheet shapes that predict maintenance pain. It never claims to render a page.
/// </summary>
public static class MarkupAnalyzer
{
    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    public static readonly string[] HtmlExtensions = [".html", ".htm"];
    public static readonly string[] CssExtensions = [".css", ".scss"];

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    private static readonly HashSet<string> Deprecated = new(StringComparer.OrdinalIgnoreCase)
    {
        "center", "font", "marquee", "blink", "big", "strike", "tt", "frame", "frameset", "applet",
    };

    private static readonly Regex TagPattern = new(@"<(/?)([A-Za-z][\w:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", Options);
    private static readonly Regex AttributePattern = new(@"(?<name>[A-Za-z_:][-\w:.]*)\s*=\s*(?<value>""[^""]*""|'[^']*'|[^\s>]+)", Options);
    private static readonly Regex IdPattern = new(@"\bid\s*=\s*(?<quote>[""'])(?<value>[^""']+)\k<quote>", Options);
    private static readonly Regex InlineHandler = new(@"\son[a-z]+\s*=", Options);
    private static readonly Regex HttpSubresource = new(
        @"<(?:script|img|iframe|link|source)\b[^>]*\b(?:src|href)\s*=\s*[""']http://", Options);
    private static readonly Regex CommentStart = new(@"<!--", Options);

    private static readonly Regex ImportantRule = new(@"!important\b", Options);
    private static readonly Regex IdSelector = new(@"(?:^|[,{}])\s*#[-A-Za-z_][-\w]*\s*(?=[,{])", Options);
    private static readonly Regex ImportRule = new(@"@import\b", Options);
    private static readonly Regex EmptyRule = new(@"\{(\s*)\}", Options);
    private static readonly Regex ScriptUrl = new(@"url\s*\(\s*[""']?\s*javascript:", Options);
    private static readonly Regex ExpressionRule = new(@"\bexpression\s*\(", Options);

    public static bool IsHtml(string path) =>
        HtmlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsCss(string path) =>
        CssExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["HTML0001"] = "<img> without alt",
        ["HTML0002"] = "<html> without lang",
        ["HTML0003"] = "No viewport meta in <head>",
        ["HTML0004"] = "Duplicate id",
        ["HTML0005"] = "<label> with neither for= nor a nested control",
        ["HTML0006"] = "Nested <form>",
        ["HTML0007"] = "Deprecated element",
        ["HTML0008"] = "Inline event handler (onclick= …)",
        ["HTML0009"] = "target=\"_blank\" without rel=\"noopener\"",
        ["HTML0010"] = "Link with an empty or '#' href",
        ["HTML0011"] = "<button> without type",
        ["HTML0012"] = "Form control without name",
        ["HTML0013"] = "Subresource loaded over http://",
        ["HTML0014"] = "Unclosed or mismatched tag",
        ["HTML0015"] = "Class used in markup and defined in no scanned stylesheet",
        ["CSS0001"] = "!important",
        ["CSS0002"] = "ID selector in a stylesheet",
        ["CSS0003"] = "Empty rule",
        ["CSS0004"] = "@import",
        ["CSS0005"] = "The same selector is declared twice",
        ["CSS0006"] = "Unbalanced braces",
        ["CSS0007"] = "javascript: URL",
        ["CSS0008"] = "IE expression()",
        ["CSS0009"] = "A selector that matches no element in the scanned pages",
    };

    /// <summary>
    /// The class and id tokens a selector mentions. Read from the selector text only, so a value inside a
    /// declaration (a colour, a URL) is never mistaken for a class the stylesheet defines.
    /// </summary>
    private static readonly Regex ClassMention = new(@"(?<![\w-])\.(-?[_A-Za-z][-\w]*)", RegexOptions.Compiled);
    private static readonly Regex IdMention = new(@"(?<![\w-])#(-?[_A-Za-z][-\w]*)", RegexOptions.Compiled);
    private static readonly Regex CssRuleSelector = new(@"^[^{};@]+$", RegexOptions.Compiled);

    /// <summary>The single-colon spellings that are pseudo-elements in CSS 3.</summary>
    private static readonly HashSet<string> LegacyPseudoElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "before", "after", "first-line", "first-letter",
    };

    // ── HTML ───────────────────────────────────────────────────────────────────

    public static MarkupAnalysis AnalyzeHtml(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var lineStarts = LineStarts(text);
        var masked = MaskHtml(text, out var rawRanges);
        var findings = new List<MarkupFinding>();
        var ids = new List<string>();

        void Add(string rule, string severity, string category, string message, int offset, string fix)
        {
            var (line, column) = Position(lineStarts, offset);
            findings.Add(new MarkupFinding(rule, severity, category, message, filePath, line, column,
                Snippet(text, lineStarts, line), fix));
        }

        var tags = ScanTags(masked);
        var openForms = 0;
        var nodes = new List<MarkupNode>();
        var openElements = new List<int>();

        foreach (var tag in tags)
        {
            var name = tag.Name.ToLowerInvariant();

            if (name == "form")
            {
                if (tag.Closing)
                {
                    openForms = Math.Max(0, openForms - 1);
                }
                else
                {
                    if (openForms > 0)
                        Add("HTML0006", "error", "Correctness",
                            "A form cannot be nested inside another form.", tag.Offset,
                            "Close the outer form first, or use a single form with several submit actions.");
                    openForms++;
                }
            }

            if (tag.Closing)
            {
                // Pop what this closing tag closes. HTML allows optional end tags (a <li> or a <p> closes
                // when the next one starts), so an element still open closes with its ancestor, and a
                // stray closing tag changes nothing.
                for (var depth = openElements.Count - 1; depth >= 0; depth--)
                {
                    if (!string.Equals(nodes[openElements[depth]].Tag, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    openElements.RemoveRange(depth, openElements.Count - depth);
                    break;
                }

                continue;
            }

            var attributes = ReadAttributes(masked, tag);

            var (nodeLine, nodeColumn) = Position(lineStarts, tag.Offset);
            nodes.Add(new MarkupNode(
                name,
                attributes.TryGetValue("id", out var nodeId) ? nodeId : null,
                attributes.TryGetValue("class", out var classValue)
                    ? classValue.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>(),
                nodeLine,
                nodeColumn,
                Parent: openElements.Count > 0 ? openElements[^1] : -1,
                Order: nodes.Count,
                Attributes: attributes));

            if (!VoidElements.Contains(name) && !tag.SelfClosing)
                openElements.Add(nodes.Count - 1);

            if (Deprecated.Contains(name))
                Add("HTML0007", "warning", "Maintainability",
                    $"<{name}> is obsolete and its rendering is not defined by HTML.", tag.Offset,
                    "Replace it with the CSS that expresses the same intent.");

            if (name == "img" && !attributes.ContainsKey("alt"))
                Add("HTML0001", "warning", "Accessibility",
                    "The image has no alt text.", tag.Offset,
                    "Add alt=\"…\"; use alt=\"\" only when the image is decorative.");

            if (name == "html" && !attributes.ContainsKey("lang"))
                Add("HTML0002", "warning", "Accessibility",
                    "The document does not declare its language.", tag.Offset,
                    "Add lang=\"…\" to <html>: screen readers choose the pronunciation from it.");

            if (name == "a")
            {
                if (attributes.TryGetValue("href", out var href) && (href.Length == 0 || href == "#"))
                    Add("HTML0010", "info", "Accessibility",
                        "The link points nowhere, so it is not reachable by keyboard as an action.", tag.Offset,
                        "Use a <button> for an action, or a real URL for a link.");

                if (attributes.TryGetValue("target", out var target) &&
                    string.Equals(target, "_blank", StringComparison.OrdinalIgnoreCase) &&
                    !attributes.ContainsKey("rel"))
                    Add("HTML0009", "warning", "Security",
                        "target=\"_blank\" without rel lets the opened page replace this one (tabnabbing).", tag.Offset,
                        "Add rel=\"noopener noreferrer\".");
            }

            if (name == "button" && !attributes.ContainsKey("type"))
                Add("HTML0011", "info", "Correctness",
                    "A <button> defaults to type=\"submit\", which submits the surrounding form.", tag.Offset,
                    "Add type=\"button\" unless submitting is the intent.");

            if (name is "input" or "select" or "textarea" &&
                !attributes.ContainsKey("name") &&
                !attributes.ContainsKey("disabled") &&
                !(attributes.TryGetValue("type", out var type) && type is "button" or "submit" or "reset" or "image"))
                Add("HTML0012", "info", "Correctness",
                    $"The <{name}> has no name, so its value is not submitted.", tag.Offset,
                    "Give it a name, or disable it when it carries no data.");

            if (name == "label" && !attributes.ContainsKey("for") && !HasNestedControl(masked, tag))
                Add("HTML0005", "warning", "Accessibility",
                    "The label is attached to no control.", tag.Offset,
                    "Add for=\"id\" matching the control, or nest the control inside the label.");

            if (attributes.TryGetValue("id", out var id))
                ids.Add(id);

            // An inline style is CSS the browser applies too: the three safety rules that need no
            // selector are checked on the attribute value.
            if (attributes.TryGetValue("style", out var inlineStyle))
            {
                if (ImportantRule.IsMatch(inlineStyle))
                    Add("CSS0001", "info", "Maintainability",
                        "!important overrides the cascade and makes the next override a fight.", tag.Offset,
                        "Move the declaration to a rule and raise its specificity instead.");
                if (ScriptUrl.IsMatch(inlineStyle))
                    Add("CSS0007", "error", "Security",
                        "A javascript: URL in an inline style executes script.", tag.Offset,
                        "Remove it: CSS never needs a script URL.");
                if (ExpressionRule.IsMatch(inlineStyle))
                    Add("CSS0008", "warning", "Security",
                        "IE expression() evaluates script inside a style.", tag.Offset,
                        "Remove it; no supported browser runs it.");
            }
        }

        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var first = tags.FirstOrDefault(tag => !tag.Closing &&
                ReadAttributes(masked, tag).TryGetValue("id", out var id) && id == duplicate.Key);
            if (first is not null)
                Add("HTML0004", "warning", "Correctness",
                    $"The id '{duplicate.Key}' appears {duplicate.Count()} times.", first.Offset,
                    "Ids must be unique: a duplicated one breaks label binding, anchors and getElementById.");
        }

        if (tags.Any(tag => !tag.Closing && tag.Name.Equals("html", StringComparison.OrdinalIgnoreCase)) &&
            !HasViewport(masked))
            Add("HTML0003", "info", "Accessibility",
                "The document has no viewport meta, so a phone renders it at desktop width.",
                tags.FirstOrDefault(tag => !tag.Closing && tag.Name.Equals("head", StringComparison.OrdinalIgnoreCase))?.Offset ?? 0,
                "Add <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">.");

        foreach (Match match in InlineHandler.Matches(masked))
            Add("HTML0008", "warning", "Security",
                "An inline event handler is not covered by a Content-Security-Policy and mixes script into markup.",
                match.Index, "Attach the handler from a script with addEventListener.");

        foreach (Match match in HttpSubresource.Matches(masked))
            Add("HTML0013", "warning", "Security",
                "A subresource is loaded over http://, which the browser blocks on an https page.",
                match.Index, "Load it over https://, or from a relative URL.");

        findings.AddRange(Balance(tags, masked, filePath, lineStarts));

        // A <style> block is real CSS the browser applies, so its rules run through the same analyzer as
        // an external sheet; the positions are remapped to the page. A stylesheet built in JavaScript
        // (CSS-in-JS) stays outside this scan.
        var inline = AnalyzeInlineStyles(text, filePath, lineStarts, tags);
        findings.AddRange(inline.Findings);

        return new MarkupAnalysis(
            "html", lineStarts.Length, tags.Count(tag => !tag.Closing), ids,
            findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList(),
            nodes,
            inline.Selectors,
            inline.Classes);
    }

    /// <summary>The CSS of every &lt;style&gt; block, with findings and selectors moved onto the page's lines.</summary>
    private static (List<MarkupFinding> Findings, List<CssSimpleSelector> Selectors, List<string> Classes)
        AnalyzeInlineStyles(string text, string filePath, int[] lineStarts, List<HtmlTag> tags)
    {
        var findings = new List<MarkupFinding>();
        var selectors = new List<CssSimpleSelector>();
        var classes = new List<string>();

        foreach (var tag in tags)
        {
            if (tag.Closing || !tag.Name.Equals("style", StringComparison.OrdinalIgnoreCase)) continue;

            var close = text.IndexOf("</style", tag.End, StringComparison.OrdinalIgnoreCase);
            var bodyStart = tag.End + 1;
            var bodyEnd = close < 0 ? text.Length : close;
            if (bodyEnd <= bodyStart) continue;

            var body = text[bodyStart..bodyEnd];
            var analysis = AnalyzeCss(body, filePath);
            var (startLine, startColumn) = Position(lineStarts, bodyStart);

            (int Line, int Column) Remap(int line, int column) =>
                (startLine + line - 1, line == 1 ? startColumn + column - 1 : column);

            foreach (var finding in analysis.Findings)
            {
                var (line, column) = Remap(finding.Line, finding.Column);
                findings.Add(finding with
                {
                    Line = line,
                    Column = column,
                    Snippet = Snippet(text, lineStarts, line),
                });
            }

            foreach (var selector in analysis.Selectors ?? [])
            {
                var (line, column) = Remap(selector.Line, selector.Column);
                selectors.Add(selector with { Line = line, Column = column });
            }

            classes.AddRange(analysis.Classes ?? []);
        }

        return (findings, selectors, classes);
    }

    /// <summary>The tag scanner's balance: an unclosed element or a close that does not match.</summary>
    private static IEnumerable<MarkupFinding> Balance(
        List<HtmlTag> tags, string masked, string filePath, int[] lineStarts)
    {
        var open = new Stack<HtmlTag>();

        foreach (var tag in tags)
        {
            var name = tag.Name.ToLowerInvariant();
            if (IsVoid(name)) continue;

            if (!tag.Closing)
            {
                if (tag.SelfClosing) continue;
                open.Push(tag);
                continue;
            }

            if (open.Count == 0)
            {
                yield return Finding("HTML0014", "error", "The close tag has no matching open tag.",
                    tag, filePath, lineStarts);
                continue;
            }

            // Pop until the matching open tag; anything popped on the way was left open.
            if (open.Peek().Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                open.Pop();
                continue;
            }

            var match = open.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                yield return Finding("HTML0014", "error", $"</{name}> does not match <{open.Peek().Name}>.", tag, filePath, lineStarts);
                continue;
            }

            while (open.Count > 0 && open.Peek() != match)
                yield return Finding("HTML0014", "error",
                    $"<{open.Pop().Name}> is never closed before </{name}>.", tag, filePath, lineStarts);
            open.Pop();
        }

        while (open.Count > 0)
            yield return Finding("HTML0014", "error", $"<{open.Pop().Name}> is never closed.", tags.Count > 0 ? tags[^1] : default,
                filePath, lineStarts);
    }

    private static MarkupFinding Finding(
        string rule, string severity, string message, HtmlTag? tag, string filePath, int[] lineStarts)
    {
        var (line, column) = Position(lineStarts, tag?.Offset ?? 0);
        var text = tag?.Text ?? string.Empty;
        return new MarkupFinding(rule, severity, "Correctness", message, filePath, line, column,
            text.Length <= 160 ? text : text[..160] + "…",
            "Close the element, or make it self-closing when it holds nothing.");
    }

    private static bool IsVoid(string name) => VoidElements.Contains(name);

    // ── CSS ────────────────────────────────────────────────────────────────────

    public static MarkupAnalysis AnalyzeCss(string text, string filePath = "(text)")
    {
        text ??= string.Empty;
        var lineStarts = LineStarts(text);
        var masked = MaskCss(text);
        var findings = new List<MarkupFinding>();

        void Add(string rule, string severity, string category, string message, int offset, string fix)
        {
            var (line, column) = Position(lineStarts, offset);
            findings.Add(new MarkupFinding(rule, severity, category, message, filePath, line, column,
                Snippet(text, lineStarts, line), fix));
        }

        foreach (Match match in ImportantRule.Matches(masked))
            Add("CSS0001", "info", "Maintainability",
                "!important overrides the cascade and makes the next override a fight.", match.Index,
                "Raise the specificity of the rule, or restructure the selector.");

        foreach (Match match in IdSelector.Matches(masked))
            Add("CSS0002", "info", "Maintainability",
                "An ID selector cannot be reused and outranks every class.", match.Index,
                "Select by class or attribute instead.");

        foreach (Match match in ImportRule.Matches(masked))
            Add("CSS0004", "info", "Performance",
                "@import serialises the download of the imported sheet.", match.Index,
                "Combine the sheets at build time, or link them from the document.");

        foreach (Match match in EmptyRule.Matches(masked))
            Add("CSS0003", "info", "Maintainability",
                "The rule declares nothing.", match.Index, "Remove it, or write the declarations it was meant to hold.");

        foreach (Match match in ScriptUrl.Matches(masked))
            Add("CSS0007", "error", "Security",
                "A javascript: URL in CSS executes script.", match.Index, "Remove it: CSS never needs a script URL.");

        foreach (Match match in ExpressionRule.Matches(masked))
            Add("CSS0008", "warning", "Security",
                "IE expression() evaluates script inside a style.", match.Index, "Remove it; no supported browser runs it.");

        var balance = Braces(masked);
        if (balance.Open != balance.Close)
        {
            var (line, column) = Position(lineStarts, balance.ProblemOffset);
            findings.Add(new MarkupFinding("CSS0006", "error", "Lexical",
                $"{balance.Open} opening brace(s) but {balance.Close} closing one(s).", filePath, line, column,
                Snippet(text, lineStarts, line), "Balance the braces: everything after the imbalance is misread."));
        }

        foreach (var duplicate in DuplicateSelectors(masked))
            Add("CSS0005", "info", "Maintainability",
                $"The selector '{duplicate.Selector}' is declared again.", duplicate.Offset,
                "Merge the two blocks: a later declaration silently wins over the earlier one.");

        var selectors = SimpleSelectors(masked, lineStarts).ToList();
        var classes = RuleSelectors(masked)
            .SelectMany(rule => ClassesIn(rule.Selector))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MarkupAnalysis(
            "css", lineStarts.Length, balance.Open, [],
            findings.OrderBy(finding => finding.Line).ThenBy(finding => finding.Column).ToList(),
            Selectors: selectors, Classes: classes);
    }

    // ── selector index ─────────────────────────────────────────────────────────

    /// <summary>The selector of every rule, as written, at the offset it starts.</summary>
    private static IEnumerable<(string Selector, int Offset)> RuleSelectors(string masked)
    {
        var start = 0;

        for (var index = 0; index < masked.Length; index++)
        {
            if (masked[index] == '}')
            {
                start = index + 1;
                continue;
            }

            if (masked[index] != '{') continue;

            var raw = masked[start..index];
            var selector = raw.Trim();
            var offset = start + (raw.Length - raw.TrimStart().Length);
            start = index + 1;

            if (selector.Length == 0 || selector.StartsWith('@') || !CssRuleSelector.IsMatch(selector))
                continue;

            yield return (selector, offset);
        }
    }

    /// <summary>
    /// Every selector of the stylesheet, simple or complex. A complex one keeps its compound parts and the
    /// combinators between them, so CSS0009 can ask whether the whole chain exists in the page index; a part
    /// only a browser can decide (a dynamic pseudo-class, :nth-child, :has) makes the selector "not
    /// checkable", and it is then indexed for its classes but never reported as unmatched.
    /// </summary>
    private static IEnumerable<CssSimpleSelector> SimpleSelectors(string masked, int[] lineStarts)
    {
        foreach (var (selectorText, offset) in RuleSelectors(masked))
        {
            foreach (var rawPart in SplitSelectorList(selectorText))
            {
                var text = rawPart.Trim();
                if (text.Length == 0) continue;
                if (!TryParseComplexSelector(text, out var complex)) continue;

                var (line, column) = Position(lineStarts,
                    offset + selectorText.IndexOf(rawPart, StringComparison.Ordinal));
                var subject = complex.Parts[^1];
                yield return new CssSimpleSelector(
                    text, subject.Tag, subject.Classes?.FirstOrDefault(), subject.Id, line, column,
                    complex, complex.Checkable);
            }
        }
    }

    /// <summary>A comma-separated selector list, split only on the commas outside brackets, parentheses and quotes.</summary>
    private static List<string> SplitSelectorList(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var round = 0;
        var square = 0;
        var quote = '\0';

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            if (character is '"' or '\'') quote = character;
            else if (character == '(') round++;
            else if (character == ')') round = Math.Max(0, round - 1);
            else if (character == '[') square++;
            else if (character == ']') square = Math.Max(0, square - 1);
            else if (character == ',' && round == 0 && square == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>The selector grammar: compound parts joined by a descendant, child or sibling combinator.</summary>
    private static bool TryParseComplexSelector(string text, out CssComplexSelector selector)
    {
        selector = null!;
        var parts = new List<CssSelectorPart>();
        var index = 0;
        var combinator = CssCombinator.None;

        while (index < text.Length)
        {
            var sawSpace = false;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                sawSpace = true;
                index++;
            }

            if (index >= text.Length) break;

            if (parts.Count > 0)
            {
                var character = text[index];
                if (character is '>' or '+' or '~')
                {
                    combinator = character switch
                    {
                        '>' => CssCombinator.Child,
                        '+' => CssCombinator.AdjacentSibling,
                        _ => CssCombinator.GeneralSibling,
                    };
                    index++;
                    while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                }
                else if (sawSpace)
                {
                    combinator = CssCombinator.Descendant;
                }
                else
                {
                    return false;
                }

                if (index >= text.Length) return false;
            }

            if (!TryParseCompound(text, ref index, out var part)) return false;
            parts.Add(part with { Combinator = combinator });
            combinator = CssCombinator.None;

            if (index < text.Length && text[index] is not ('>' or '+' or '~') && !char.IsWhiteSpace(text[index]))
                return false;
        }

        if (parts.Count == 0) return false;
        selector = new CssComplexSelector(text, parts, parts.All(part => part.Checkable));
        return true;
    }

    /// <summary>One compound part: a tag, classes, an id, attribute conditions and pseudo-classes.</summary>
    private static bool TryParseCompound(string text, ref int index, out CssSelectorPart part)
    {
        part = null!;
        var start = index;

        string? tag = null;
        string? id = null;
        var classes = new List<string>();
        var attributes = new List<CssAttributeCondition>();
        var pseudoClasses = new List<string>();
        var negations = new List<CssSelectorPart>();
        var alternatives = new List<CssSelectorPart>();
        var checkable = true;
        var pseudoElement = false;

        if (index < text.Length && text[index] == '*')
        {
            index++;
        }
        else if (index < text.Length && (char.IsLetter(text[index]) || text[index] is '_' or '-'))
        {
            tag = ReadName(text, ref index);
        }

        while (index < text.Length)
        {
            var character = text[index];

            if (character == '.')
            {
                index++;
                var name = ReadName(text, ref index);
                if (name.Length == 0) return false;
                classes.Add(name);
            }
            else if (character == '#')
            {
                index++;
                var name = ReadName(text, ref index);
                if (name.Length == 0) return false;
                id = name;
            }
            else if (character == '[')
            {
                if (!TryParseAttribute(text, ref index, out var attribute)) return false;
                attributes.Add(attribute);
            }
            else if (character == ':')
            {
                var doubleColon = index + 1 < text.Length && text[index + 1] == ':';
                index += doubleColon ? 2 : 1;

                var name = ReadName(text, ref index).ToLowerInvariant();
                if (name.Length == 0) return false;

                // A pseudo-element (::before, or the legacy :before) styles the element but does not change
                // which element the selector selects.
                if (doubleColon || LegacyPseudoElements.Contains(name))
                {
                    pseudoElement = true;
                    continue;
                }

                if (index < text.Length && text[index] == '(')
                {
                    var inner = ReadParenthesized(text, ref index);
                    if (inner is null) return false;

                    if (name is "not" or "is" or "where")
                    {
                        var list = new List<CssSelectorPart>();
                        var parsed = true;
                        foreach (var item in SplitSelectorList(inner))
                        {
                            var trimmed = item.Trim();
                            var cursor = 0;
                            if (trimmed.Length == 0 || !TryParseCompound(trimmed, ref cursor, out var innerPart) ||
                                cursor != trimmed.Length)
                            {
                                parsed = false;
                                break;
                            }

                            list.Add(innerPart);
                        }

                        if (!parsed || list.Count == 0)
                        {
                            checkable = false;
                        }
                        else
                        {
                            if (name == "not") negations.AddRange(list);
                            else alternatives.AddRange(list);
                            checkable &= list.All(innerPart => innerPart.Checkable);
                        }
                    }
                    else
                    {
                        // :nth-child(), :has(), :lang() … need more of the page than this index holds.
                        checkable = false;
                    }

                    continue;
                }

                if (name is "first-child" or "last-child" or "only-child" or "empty" or "root")
                    pseudoClasses.Add(name);
                else
                    checkable = false; // :hover, :focus, :checked … only the browser can decide
            }
            else
            {
                break;
            }
        }

        if (index == start) return false;

        part = new CssSelectorPart(tag, classes, id, attributes, pseudoClasses, negations, alternatives,
            checkable, pseudoElement);
        return true;
    }

    private static string ReadName(string text, ref int index)
    {
        var start = index;
        if (index < text.Length && (char.IsLetter(text[index]) || text[index] is '_' or '-')) index++;
        while (index < text.Length &&
               (char.IsLetterOrDigit(text[index]) || text[index] is '-' or '_'))
            index++;
        return text[start..index];
    }

    /// <summary>The text inside a pair of parentheses, with nesting; null when they are unbalanced.</summary>
    private static string? ReadParenthesized(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '(') return null;
        var depth = 0;
        var start = index + 1;
        var quote = '\0';

        for (; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            if (character is '"' or '\'') quote = character;
            else if (character == '(') depth++;
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var inner = text[start..index];
                    index++;
                    return inner;
                }
            }
        }

        return null;
    }

    /// <summary>An attribute condition: <c>[name]</c>, <c>[name=value]</c>, <c>[name*=value i]</c>.</summary>
    private static bool TryParseAttribute(string text, ref int index, out CssAttributeCondition condition)
    {
        condition = null!;
        index++; // the '['

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        var name = ReadName(text, ref index);
        if (name.Length == 0) return false;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

        if (index < text.Length && text[index] == ']')
        {
            index++;
            condition = new CssAttributeCondition(name, null, null, false);
            return true;
        }

        string @operator;
        if (index + 1 < text.Length && text[index + 1] == '=' && text[index] is '~' or '|' or '^' or '$' or '*')
        {
            @operator = text[index] + "=";
            index += 2;
        }
        else if (index < text.Length && text[index] == '=')
        {
            @operator = "=";
            index++;
        }
        else
        {
            return false;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

        string value;
        if (index < text.Length && text[index] is '"' or '\'')
        {
            var quote = text[index++];
            var end = text.IndexOf(quote, index);
            if (end < 0) return false;
            value = text[index..end];
            index = end + 1;
        }
        else
        {
            var start = index;
            while (index < text.Length && text[index] != ']' && !char.IsWhiteSpace(text[index])) index++;
            value = text[start..index];
        }

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        var ignoreCase = false;
        if (index < text.Length && text[index] is 'i' or 'I')
        {
            ignoreCase = true;
            index++;
        }
        else if (index < text.Length && text[index] is 's' or 'S')
        {
            index++;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || text[index] != ']') return false;
        index++;

        condition = new CssAttributeCondition(name, @operator, value, ignoreCase);
        return true;
    }

    /// <summary>Whether an element index entry can satisfy a simple selector.</summary>
    public static bool Matches(MarkupNode node, CssSimpleSelector selector) =>
        (selector.Tag is null || string.Equals(node.Tag, selector.Tag, StringComparison.OrdinalIgnoreCase)) &&
        (selector.Class is null ||
         node.Classes.Contains(selector.Class, StringComparer.OrdinalIgnoreCase)) &&
        (selector.Id is null || string.Equals(node.Id, selector.Id, StringComparison.Ordinal));

    /// <summary>
    /// Whether a page's element index satisfies a selector — the whole chain, when the selector has
    /// combinators. The index is one page's, because a parent and a sibling are indices into it.
    /// </summary>
    public static bool Matches(IReadOnlyList<MarkupNode> nodes, MarkupNode node, CssSimpleSelector selector)
    {
        if (selector.Complex is null) return Matches(node, selector);
        if (!selector.Complex.Checkable) return false;

        var parts = selector.Complex.Parts;
        var current = node;
        if (!PartMatches(nodes, current, parts[^1])) return false;

        for (var position = parts.Count - 2; position >= 0; position--)
        {
            var part = parts[position];
            MarkupNode? matched = parts[position + 1].Combinator switch
            {
                CssCombinator.Child => current.Parent >= 0 && PartMatches(nodes, nodes[current.Parent], part)
                    ? nodes[current.Parent]
                    : null,
                CssCombinator.AdjacentSibling => PreviousSibling(nodes, current) is { } adjacent &&
                                                 PartMatches(nodes, adjacent, part)
                    ? adjacent
                    : null,
                CssCombinator.GeneralSibling => PreviousSiblings(nodes, current)
                    .FirstOrDefault(sibling => PartMatches(nodes, sibling, part)),
                _ => Ancestors(nodes, current).FirstOrDefault(ancestor => PartMatches(nodes, ancestor, part)),
            };

            if (matched is null) return false;
            current = matched;
        }

        return true;
    }

    private static bool PartMatches(IReadOnlyList<MarkupNode> nodes, MarkupNode node, CssSelectorPart part)
    {
        if (part.Tag is not null && !string.Equals(node.Tag, part.Tag, StringComparison.OrdinalIgnoreCase))
            return false;
        if (part.Id is not null && !string.Equals(node.Id, part.Id, StringComparison.Ordinal))
            return false;

        if (part.Classes is { } classes &&
            classes.Any(className => !node.Classes.Contains(className, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (part.Attributes is { } attributes &&
            attributes.Any(condition => !AttributeMatches(node, condition)))
            return false;

        if (part.PseudoClasses is { } pseudoClasses &&
            pseudoClasses.Any(pseudo => !PseudoMatches(nodes, node, pseudo)))
            return false;

        if (part.Not is { Count: > 0 } negations &&
            negations.Any(negation => PartMatches(nodes, node, negation)))
            return false;

        if (part.IsAny is { Count: > 0 } alternatives &&
            !alternatives.Any(alternative => PartMatches(nodes, node, alternative)))
            return false;

        return true;
    }

    private static bool PseudoMatches(IReadOnlyList<MarkupNode> nodes, MarkupNode node, string pseudo)
    {
        var first = !nodes.Take(node.Order).Any(candidate => candidate.Parent == node.Parent);
        var last = !nodes.Skip(node.Order + 1).Any(candidate => candidate.Parent == node.Parent);

        return pseudo switch
        {
            "root" => node.Parent < 0 && string.Equals(node.Tag, "html", StringComparison.OrdinalIgnoreCase),
            "first-child" => first,
            "last-child" => last,
            "only-child" => first && last,
            "empty" => !nodes.Any(candidate => candidate.Parent == node.Order),
            _ => false,
        };
    }

    private static bool AttributeMatches(MarkupNode node, CssAttributeCondition condition)
    {
        if (node.Attributes is null || !node.Attributes.TryGetValue(condition.Name, out var value)) return false;
        if (condition.Operator is null || condition.Value is null) return true;

        var comparison = condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return condition.Operator switch
        {
            "=" => string.Equals(value, condition.Value, comparison),
            "~=" => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(word => string.Equals(word, condition.Value, comparison)),
            "|=" => string.Equals(value, condition.Value, comparison) ||
                    value.StartsWith(condition.Value + "-", comparison),
            "^=" => condition.Value.Length > 0 && value.StartsWith(condition.Value, comparison),
            "$=" => condition.Value.Length > 0 && value.EndsWith(condition.Value, comparison),
            "*=" => condition.Value.Length > 0 && value.Contains(condition.Value, comparison),
            _ => false,
        };
    }

    private static IEnumerable<MarkupNode> Ancestors(IReadOnlyList<MarkupNode> nodes, MarkupNode node)
    {
        var parent = node.Parent;
        while (parent >= 0 && parent < nodes.Count)
        {
            yield return nodes[parent];
            parent = nodes[parent].Parent;
        }
    }

    private static MarkupNode? PreviousSibling(IReadOnlyList<MarkupNode> nodes, MarkupNode node)
    {
        for (var index = node.Order - 1; index >= 0; index--)
            if (nodes[index].Parent == node.Parent)
                return nodes[index];
        return null;
    }

    private static IEnumerable<MarkupNode> PreviousSiblings(IReadOnlyList<MarkupNode> nodes, MarkupNode node)
    {
        for (var index = node.Order - 1; index >= 0; index--)
            if (nodes[index].Parent == node.Parent)
                yield return nodes[index];
    }

    /// <summary>The class and id tokens a selector mentions, complex selectors included.</summary>
    public static IReadOnlyList<string> ClassesIn(string selector) =>
        ClassMention.Matches(selector).Select(match => match.Groups[1].Value).ToList();

    public static IReadOnlyList<string> IdsIn(string selector) =>
        IdMention.Matches(selector).Select(match => match.Groups[1].Value).ToList();

    private static (int Open, int Close, int ProblemOffset) Braces(string masked)
    {
        var open = 0;
        var close = 0;
        var problem = 0;

        for (var index = 0; index < masked.Length; index++)
        {
            if (masked[index] == '{') open++;
            else if (masked[index] == '}')
            {
                close++;
                if (close > open) problem = index;
            }
        }

        return (open, close, open > close ? masked.LastIndexOf('{') : problem);
    }

    private static IEnumerable<(string Selector, int Offset)> DuplicateSelectors(string masked)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        var depth = 0;

        for (var index = 0; index < masked.Length; index++)
        {
            if (masked[index] == '}')
            {
                depth = Math.Max(0, depth - 1);
                start = index + 1;
                continue;
            }

            if (masked[index] != '{') continue;

            var selector = masked[start..index].Trim();
            start = index + 1;

            // Only a top-level rule is compared: the same selector inside a nested @media block (or
            // another at-rule) is a different rule, not a redeclaration.
            if (depth == 0 && selector.Length > 0 && !selector.StartsWith('@') &&
                !seen.TryAdd(selector, start))
                yield return (selector, start);

            depth++;
        }
    }

    // ── masks ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blank comments and the bodies of raw-text elements (so a '&lt;' inside a script does not become a
    /// tag). Attribute values are kept: they are what the rules read.
    /// </summary>
    private static string MaskHtml(string text, out List<(int Start, int End)> rawRanges)
    {
        var chars = text.ToCharArray();
        var ranges = new List<(int Start, int End)>();
        var index = 0;

        void Blank(int from, int to)
        {
            for (var position = from; position < to && position < chars.Length; position++)
                if (chars[position] != '\n' && chars[position] != '\r') chars[position] = ' ';
        }

        while (index < text.Length)
        {
            if (text[index] == '<' && index + 3 < text.Length &&
                text[index + 1] == '!' && text[index + 2] == '-' && text[index + 3] == '-')
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    Blank(index, text.Length);
                    break;
                }

                Blank(index, end + 3);
                index = end + 3;
                continue;
            }

            if (text[index] == '<' && index + 1 < text.Length && char.IsLetter(text[index + 1]))
            {
                var nameEnd = index + 1;
                while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '-' or ':'))
                    nameEnd++;
                var name = text[(index + 1)..nameEnd];

                var tagEnd = FindTagEnd(text, index);
                if (tagEnd < 0) break;

                if (RawTextElements.Contains(name) && !IsSelfClosing(text, index, tagEnd))
                {
                    var close = text.IndexOf("</" + name, tagEnd + 1, StringComparison.OrdinalIgnoreCase);
                    var bodyStart = tagEnd + 1;
                    var bodyEnd = close < 0 ? text.Length : close;
                    ranges.Add((bodyStart, bodyEnd));
                    Blank(bodyStart, bodyEnd);
                    index = bodyEnd;
                    continue;
                }

                index = tagEnd + 1;
                continue;
            }

            index++;
        }

        rawRanges = ranges;
        return new string(chars);
    }

    private static bool IsSelfClosing(string text, int tagStart, int tagEnd) =>
        tagEnd > tagStart && text[tagEnd - 1] == '/';

    /// <summary>Quote-aware: a '&gt;' inside an attribute value does not end the tag.</summary>
    private static int FindTagEnd(string text, int start)
    {
        var quote = '\0';
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            if (character is '"' or '\'') quote = character;
            else if (character == '>') return index;
        }

        return -1;
    }

    private static string MaskCss(string text)
    {
        var chars = text.ToCharArray();
        var index = 0;
        var depth = 0;

        void Blank(int from, int to)
        {
            for (var position = from; position < to && position < chars.Length; position++)
                if (chars[position] != '\n' && chars[position] != '\r') chars[position] = ' ';
        }

        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) { Blank(index, text.Length); break; }
                Blank(index, end + 2);
                index = end + 2;
                continue;
            }

            // SCSS also has line comments.
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = text.IndexOf('\n', index);
                if (end < 0) end = text.Length;
                Blank(index, end);
                index = end;
                continue;
            }

            if (text[index] is '"' or '\'')
            {
                var quote = text[index];
                var end = text.IndexOf(quote, index + 1);

                // Inside a declaration a string is a value: blanking it stops a keyframe, a URL or a
                // content string from being read as a rule. In a selector it is an attribute value that
                // the match needs, so it is kept.
                if (depth > 0)
                {
                    if (end < 0) { Blank(index + 1, text.Length); break; }
                    Blank(index + 1, end);
                }
                else if (end < 0)
                {
                    break;
                }

                index = end + 1;
                continue;
            }

            if (text[index] == '{') depth++;
            else if (text[index] == '}') depth = Math.Max(0, depth - 1);

            index++;
        }

        return new string(chars);
    }

    // ── tags ───────────────────────────────────────────────────────────────────

    private sealed record HtmlTag(string Name, int Offset, int End, bool Closing, bool SelfClosing, string Text);

    private static List<HtmlTag> ScanTags(string masked)
    {
        var tags = new List<HtmlTag>();

        foreach (Match match in TagPattern.Matches(masked))
        {
            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value;
            var rest = match.Groups[3].Value;

            // <!DOCTYPE> and <?xml> are not elements.
            if (name.StartsWith('!') || name.StartsWith('?')) continue;

            tags.Add(new HtmlTag(
                name, match.Index, match.Index + match.Length - 1, closing,
                rest.TrimEnd().EndsWith('/'), match.Value));
        }

        return tags;
    }

    private static Dictionary<string, string> ReadAttributes(string masked, HtmlTag tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern.Matches(tag.Text))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value.Trim('"', '\'');
            attributes.TryAdd(name, value);
        }

        return attributes;
    }

    private static bool HasNestedControl(string masked, HtmlTag tag)
    {
        var close = masked.IndexOf("</label", tag.Offset, StringComparison.OrdinalIgnoreCase);
        var body = masked[tag.End..(close < 0 ? masked.Length : close)];
        return body.Contains("<input", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("<select", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("<textarea", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasViewport(string masked) =>
        Regex.IsMatch(masked, @"<meta\b[^>]*name\s*=\s*[""']viewport[""']", Options);

    // ── positions ──────────────────────────────────────────────────────────────

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n')
                starts.Add(index + 1);
        return starts.ToArray();
    }

    private static (int Line, int Column) Position(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        if (index < 0) index = ~index - 1;
        if (index < 0) index = 0;
        return (index + 1, offset - lineStarts[index] + 1);
    }

    private static string Snippet(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length) return string.Empty;
        var start = lineStarts[line - 1];
        var end = line < lineStarts.Length ? lineStarts[line] : text.Length;
        var snippet = text[start..end].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }
}

/// <summary>One markup or stylesheet finding.</summary>
public sealed record MarkupFinding(
    string RuleId, string Severity, string Category, string Message, string File, int Line, int Column,
    string Snippet, string? Fix);

/// <summary>One element of the page index: what a selector can be matched against.</summary>
/// <param name="Parent">The index of the enclosing element in the same page's list, or -1 at the root.</param>
/// <param name="Order">The element's index in the page list, which is document order.</param>
/// <param name="Attributes">The attributes as read, which is what an attribute selector matches.</param>
public sealed record MarkupNode(
    string Tag, string? Id, IReadOnlyList<string> Classes, int Line, int Column,
    int Parent = -1, int Order = 0, IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>How a compound selector is joined to the one on its left.</summary>
public enum CssCombinator
{
    None,
    Descendant,
    Child,
    AdjacentSibling,
    GeneralSibling,
}

/// <summary>An attribute condition: <c>[name]</c>, <c>[name=value]</c>, <c>[name^=value i]</c>.</summary>
public sealed record CssAttributeCondition(string Name, string? Operator, string? Value, bool IgnoreCase);

/// <summary>One compound selector: a tag, classes, an id, attribute conditions and pseudo-classes.</summary>
/// <param name="Checkable">False when only a browser could decide it (<c>:hover</c>, <c>:nth-child()</c>).</param>
/// <param name="Combinator">How this part joins the part on its left (None for the first).</param>
public sealed record CssSelectorPart(
    string? Tag = null,
    IReadOnlyList<string>? Classes = null,
    string? Id = null,
    IReadOnlyList<CssAttributeCondition>? Attributes = null,
    IReadOnlyList<string>? PseudoClasses = null,
    IReadOnlyList<CssSelectorPart>? Not = null,
    IReadOnlyList<CssSelectorPart>? IsAny = null,
    bool Checkable = true,
    bool PseudoElement = false,
    CssCombinator Combinator = CssCombinator.None);

/// <summary>A selector as written: its compound parts, with the combinators between them.</summary>
public sealed record CssComplexSelector(string Text, IReadOnlyList<CssSelectorPart> Parts, bool Checkable);

/// <summary>A selector with the element it ends on; <paramref name="Complex"/> carries the whole chain.</summary>
public sealed record CssSimpleSelector(
    string Text, string? Tag, string? Class, string? Id, int Line, int Column,
    CssComplexSelector? Complex = null, bool Checkable = true);

/// <summary>The facts a scan of one markup file produces.</summary>
public sealed record MarkupAnalysis(
    string Kind, int Lines, int Elements, IReadOnlyList<string> Ids, IReadOnlyList<MarkupFinding> Findings,
    IReadOnlyList<MarkupNode>? Nodes = null, IReadOnlyList<CssSimpleSelector>? Selectors = null,
    IReadOnlyList<string>? Classes = null);
