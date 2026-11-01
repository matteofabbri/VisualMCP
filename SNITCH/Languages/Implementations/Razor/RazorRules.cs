using System.Text.RegularExpressions;

namespace Snitch.Languages.Razor;

/// <summary>
/// The Razor rule engine. It runs every rule over one parsed document and returns the findings with
/// their catalogue metadata; the MCP services only filter (problems vs security) and shape the reply,
/// so both tools report the same evidence.
/// </summary>
public static class RazorRules
{
    public const int LargeCodeBlockLines = 25;

    private static readonly Regex RawSinkPattern = new(
        @"(?:Html\s*\.\s*Raw|HtmlString)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex RootIdentifierPattern = new(
        @"^([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex CodeSecretPattern = new(
        @"(?i)\b(password|passwd|secret|api_?key|access_?token|private_?key|connection_?string)\b\s*=\s*""([^""]{3,})""",
        RegexOptions.Compiled);

    private static readonly HashSet<string> EventHandlerAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "onclick", "ondblclick", "onchange", "oninput", "onsubmit", "onreset", "onfocus", "onblur",
        "onkeydown", "onkeyup", "onkeypress", "onload", "onerror", "onmouseover", "onmouseout",
        "onmousedown", "onmouseup", "onmousemove", "onmouseenter", "onmouseleave", "oncontextmenu",
        "onwheel", "onscroll", "onpaste", "oncopy", "oncut", "ondrag", "ondragstart", "ondragend",
        "ondrop", "ontouchstart", "ontouchend", "ontouchmove", "onanimationend", "ontransitionend",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction", "poster", "data",
    };

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "apikey", "api_key", "api-key",
        "accesstoken", "access_token", "access-token", "privatekey", "private_key", "private-key",
        "connectionstring", "connection_string", "connection-string",
    };

    private static readonly HashSet<string> DeprecatedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "center", "font", "marquee", "big", "strike", "tt", "blink",
    };

    private static readonly HashSet<string> DeprecatedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "align", "bgcolor",
    };

    private static readonly HashSet<string> NonPostingInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "submit", "reset", "image",
    };

    // ── Entry point ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs every rule over one document. <paramref name="resolvePartial"/> is what lets a rule look
    /// inside what a page renders: the anti-forgery check sees a token that lives in the partial a form
    /// includes, and the duplicate-id check compares the page with its declared layout and the partials
    /// either renders. Without it the rules stay per file.
    /// </summary>
    public static List<RazorFinding> Run(RazorDocument document, Func<string, RazorDocument?>? resolvePartial = null)
    {
        var findings = new List<RazorFinding>();

        AddSyntaxIssues(document, findings);
        CheckRawHtml(document, findings);
        CheckScriptExpressions(document, findings);
        CheckInlineEventHandlers(document, findings);
        CheckScriptUris(document, findings);
        CheckAntiForgery(document, findings, resolvePartial);
        CheckPasswordOverGet(document, findings);
        CheckTargetBlank(document, findings);
        CheckHardcodedSecrets(document, findings);
        CheckCleartextResources(document, findings);
        CheckDynamicIframe(document, findings);
        CheckInlineContent(document, findings);
        CheckDuplicateIds(document, findings, resolvePartial);
        CheckImages(document, findings);
        CheckLabels(document, findings);
        CheckButtons(document, findings);
        CheckControlNames(document, findings);
        CheckDocumentHead(document, findings);
        CheckUntypedModel(document, findings);
        CheckLegacyHelpers(document, findings);
        CheckLargeCodeBlocks(document, findings);
        CheckNestedForms(document, findings);
        CheckDuplicateAttributes(document, findings);
        CheckFormAction(document, findings);
        CheckDeprecatedMarkup(document, findings);

        return findings
            .GroupBy(f => (f.RuleId, f.FilePath, f.Line, f.Column, f.Message))
            .Select(group => group.First())
            .ToList();
    }

    // ── Security ───────────────────────────────────────────────────────────────

    private static void AddSyntaxIssues(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var issue in document.SyntaxIssues)
            Add(findings, document, issue.RuleId, issue.Message, issue.Line, issue.Column);
    }

    private static void CheckRawHtml(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var expression in document.Expressions)
        {
            foreach (Match match in RawSinkPattern.Matches(expression.Text))
            {
                var argument = ArgumentAt(expression.Text, expression.Text.IndexOf('(', match.Index));
                if (IsEncoded(argument)) continue;

                Add(findings, document, "RZ1001",
                    $"'{Shorten(expression.Text)}' writes '{Shorten(argument)}' to the response without HTML encoding.",
                    expression.Line, expression.Column);
            }
        }

        // A raw sink can also sit inside an attribute value: title="@Html.Raw(Model.Title)".
        foreach (var element in document.AllElements())
        {
            foreach (var attribute in element.Attributes)
            {
                if (!attribute.ValueHasRazor || attribute.Value is null) continue;
                if (!RawSinkPattern.IsMatch(attribute.Value)) continue;

                Add(findings, document, "RZ1001",
                    $"Attribute '{attribute.Name}' on <{element.Name}> writes raw HTML without encoding.",
                    attribute.Line, attribute.Column);
            }
        }
    }

    private static void CheckScriptExpressions(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var expression in document.Expressions.Where(e => e.InsideScript))
        {
            if (RawSinkPattern.IsMatch(expression.Text)) continue; // already RZ1001
            if (IsScriptSafe(expression.Text)) continue;

            var root = RootIdentifier(expression.Text);
            if (root is null) continue;

            var isModelData = root is "Model" or "ViewBag" or "ViewData" or "TempData";
            if (!isModelData && !document.LoopVariables.Contains(root)) continue;

            Add(findings, document, "RZ1002",
                $"'{Shorten(expression.Text)}' is written into a <script> block, where HTML encoding does not apply: " +
                "a value containing </script> or a quote can break out of the literal.",
                expression.Line, expression.Column);
        }
    }

    private static void CheckInlineEventHandlers(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            foreach (var attribute in element.Attributes)
            {
                if (!EventHandlerAttributes.Contains(attribute.Name)) continue;
                if (!attribute.ValueHasRazor) continue;

                Add(findings, document, "RZ1003",
                    $"<{element.Name} {attribute.Name}=\"…\"> embeds data in a JavaScript event handler, " +
                    "where HTML encoding does not prevent script injection.",
                    attribute.Line, attribute.Column);
            }
        }
    }

    private static void CheckScriptUris(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            foreach (var attribute in element.Attributes)
            {
                if (!UrlAttributes.Contains(attribute.Name)) continue;
                if (attribute.Value is null || attribute.ValueHasRazor) continue;

                var value = Normalise(attribute.Value);
                if (value.StartsWith("javascript:", StringComparison.Ordinal) ||
                    value.StartsWith("vbscript:", StringComparison.Ordinal) ||
                    value.StartsWith("data:text/html", StringComparison.Ordinal))
                {
                    Add(findings, document, "RZ1004",
                        $"'{attribute.Name}' uses a script URI ('{Shorten(attribute.Value.Trim())}').",
                        attribute.Line, attribute.Column);
                }
            }
        }
    }

    private static void CheckAntiForgery(
        RazorDocument document, List<RazorFinding> findings, Func<string, RazorDocument?>? resolvePartial)
    {
        if (!IsCshtml(document)) return;

        if (document.IgnoresAntiForgery)
        {
            var directive = document.Directives
                .FirstOrDefault(d => d.Name == "attribute" &&
                                     d.Value.Contains("IgnoreAntiforgeryToken", StringComparison.Ordinal));
            Add(findings, document, "RZ1005",
                "This page declares [IgnoreAntiforgeryToken]: every POST to it is accepted without an anti-forgery token.",
                directive?.Line ?? 1, directive?.Column ?? 1);
            return;
        }

        foreach (var form in document.AllElements().Where(e => e.Is("form")))
        {
            if (form.IsDynamic("method")) continue;

            var method = form.Literal("method")?.Trim().ToLowerInvariant();
            if (method != "post") continue;

            var antiforgery = form.Attribute("asp-antiforgery");
            if (antiforgery is { HasValue: true, ValueHasRazor: false } &&
                antiforgery.Value!.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                Add(findings, document, "RZ1005",
                    "The form sets asp-antiforgery=\"false\", so the tag helper does not render a token.",
                    form.Line, form.Column);
                continue;
            }

            // Any asp-* attribute means the form tag helper ran, and it injects the token by default.
            if (form.HasAttributePrefix("asp-")) continue;
            if (HasAntiForgeryToken(document, form, resolvePartial)) continue;

            Add(findings, document, "RZ1005",
                "POST form without @Html.AntiForgeryToken() and without the form tag helper: the request carries no CSRF token.",
                form.Line, form.Column);
        }
    }

    private static void CheckPasswordOverGet(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var form in document.AllElements().Where(e => e.Is("form")))
        {
            if (form.IsDynamic("method")) continue;
            var method = form.Literal("method")?.Trim().ToLowerInvariant();
            if (method is "post") continue;

            foreach (var input in form.Descendants().Where(e => e.Is("input")))
            {
                var type = input.Literal("type");
                if (!string.Equals(type?.Trim(), "password", StringComparison.OrdinalIgnoreCase)) continue;

                Add(findings, document, "RZ1006",
                    "A password input is submitted with GET, so the password ends up in the URL, browser history and server logs.",
                    input.Line, input.Column);
            }
        }
    }

    private static void CheckTargetBlank(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            if (!element.Is("a") && !element.Is("area") && !element.Is("form")) continue;

            var target = element.Literal("target")?.Trim();
            if (!string.Equals(target, "_blank", StringComparison.OrdinalIgnoreCase)) continue;
            if (element.IsDynamic("rel")) continue;

            var rel = element.Literal("rel") ?? string.Empty;
            if (rel.Contains("noopener", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("noreferrer", StringComparison.OrdinalIgnoreCase))
                continue;

            Add(findings, document, "RZ1007",
                $"<{element.Name} target=\"_blank\"> without rel=\"noopener\": the opened page can navigate the opener window.",
                element.Line, element.Column);
        }
    }

    private static void CheckHardcodedSecrets(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            var value = element.Literal("value");
            var type = element.Literal("type");
            var name = element.Literal("name") ?? element.Literal("id") ?? element.Literal("asp-for");

            if (value is not null && value.Trim().Length >= 3 &&
                !value.TrimStart().StartsWith("{{", StringComparison.Ordinal))
            {
                var isPassword = string.Equals(type?.Trim(), "password", StringComparison.OrdinalIgnoreCase);
                var secretName = name is not null &&
                                 !name.Contains("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase) &&
                                 LooksLikeSecret(name);

                if (isPassword || secretName)
                {
                    Add(findings, document, "RZ1008",
                        $"A literal credential is rendered in <{element.Name}> (name '{name ?? type}'): '{Shorten(value)}'.",
                        element.Line, element.Column);
                }
            }

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Value is null || attribute.ValueHasRazor) continue;
                if (!LooksLikeSecret(attribute.Name)) continue;
                if (attribute.Value.Trim().Length < 3) continue;
                if (attribute.Value.TrimStart().StartsWith("{{", StringComparison.Ordinal)) continue;

                Add(findings, document, "RZ1008",
                    $"Attribute '{attribute.Name}' contains a literal credential: '{Shorten(attribute.Value)}'.",
                    attribute.Line, attribute.Column);
            }
        }

        foreach (var block in document.CodeBlocks.Where(b => b.Kind is "block" or "code" or "functions"))
        {
            foreach (Match match in CodeSecretPattern.Matches(block.Text))
            {
                Add(findings, document, "RZ1008",
                    $"The code block assigns a literal credential to '{match.Groups[1].Value}': '{Shorten(match.Groups[2].Value)}'.",
                    block.Line, 1);
            }
        }
    }

    private static void CheckCleartextResources(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            foreach (var attribute in element.Attributes)
            {
                if (!UrlAttributes.Contains(attribute.Name)) continue;
                if (attribute.Value is null || attribute.ValueHasRazor) continue;
                if (attribute.Name.Contains("xmlns", StringComparison.OrdinalIgnoreCase)) continue;

                var value = attribute.Value.Trim();
                if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Contains("w3.org", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Contains("schemas.", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Contains("schemaLocation", StringComparison.OrdinalIgnoreCase)) continue;

                Add(findings, document, "RZ1009",
                    $"'{attribute.Name}' loads '{Shorten(value)}' over plain http.",
                    attribute.Line, attribute.Column);
            }
        }
    }

    private static void CheckDynamicIframe(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var iframe in document.AllElements().Where(e => e.Is("iframe")))
        {
            if (!iframe.IsDynamic("src")) continue;

            Add(findings, document, "RZ1010",
                "The iframe src is built from data; without an allowlist a crafted value can point at hostile content.",
                iframe.Line, iframe.Column);
        }
    }

    private static void CheckInlineContent(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            if (element.Is("script"))
            {
                if (element.HasAttribute("src")) continue;
                if (string.IsNullOrWhiteSpace(element.RawText)) continue;

                Add(findings, document, "RZ1011",
                    "Inline <script> block: a Content-Security-Policy without 'unsafe-inline' will block it.",
                    element.Line, element.Column);
            }
            else if (element.Is("style"))
            {
                if (string.IsNullOrWhiteSpace(element.RawText)) continue;

                Add(findings, document, "RZ1011",
                    "Inline <style> block: a strict Content-Security-Policy will block it.",
                    element.Line, element.Column);
            }

            foreach (var attribute in element.Attributes)
            {
                if (attribute.Name.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    Add(findings, document, "RZ1011",
                        $"Inline style attribute on <{element.Name}>: a strict Content-Security-Policy will block it.",
                        attribute.Line, attribute.Column);
                }

                if (EventHandlerAttributes.Contains(attribute.Name) && !attribute.ValueHasRazor)
                {
                    Add(findings, document, "RZ1011",
                        $"Inline event handler '{attribute.Name}' on <{element.Name}>: a strict Content-Security-Policy will block it.",
                        attribute.Line, attribute.Column);
                }
            }
        }
    }

    // ── Markup ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An id must be unique in the document the browser renders, which is the page plus the layout it
    /// declares plus every partial either renders. The page's own duplicates are reported as before; with
    /// a partial resolver the composition is compared as one document, and a collision is reported on the
    /// occurrence that comes later, naming the file and line of the first. A duplicate that lives inside
    /// one partial is left to that partial's own analysis, so it is not repeated for every page that
    /// includes it.
    /// </summary>
    private static void CheckDuplicateIds(
        RazorDocument document, List<RazorFinding> findings, Func<string, RazorDocument?>? resolvePartial)
    {
        var own = IdsIn(document);
        var seen = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        foreach (var id in own)
        {
            if (seen.TryAdd(id.Id, id)) continue;

            Add(findings, document, "RZ2003",
                $"id '{id.Id}' is already used on line {seen[id.Id].Line}; ids must be unique in a document.",
                id.Line, id.Column);
        }

        if (resolvePartial is null) return;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { document.FilePath };
        var composition = new List<RazorDocument>();
        CollectComposition(document, resolvePartial, visited, depth: 0, composition);

        var first = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        foreach (var id in own) first.TryAdd(id.Id, id);

        foreach (var id in composition.SelectMany(IdsIn))
        {
            if (first.TryAdd(id.Id, id)) continue;

            var earlier = first[id.Id];
            // A partial colliding with itself is that file's own problem, reported when it is analysed.
            if (!ReferenceEquals(earlier.Document, id.Document))
                Add(findings, id.Document, "RZ2003",
                    $"id '{id.Id}' is also used on line {earlier.Line} of {Path.GetFileName(earlier.Document.FilePath)}; " +
                    "ids must be unique in the rendered document.",
                    id.Line, id.Column);
        }
    }

    /// <summary>The ids a document declares, in document order, with the document that declares them.</summary>
    private static List<DocumentId> IdsIn(RazorDocument document)
    {
        var ids = new List<DocumentId>();
        foreach (var element in document.AllElements())
        {
            var attribute = element.Attribute("id");
            if (attribute is not { HasValue: true, ValueHasRazor: false } || string.IsNullOrWhiteSpace(attribute.Value))
                continue;

            ids.Add(new DocumentId(document, attribute.Value.Trim(), attribute.Line, attribute.Column));
        }

        return ids;
    }

    /// <summary>
    /// The documents rendered into the same DOM as <paramref name="document"/>: the layout it declares
    /// (and that layout's own layout), then every partial each renders. A cycle, a name that resolves to
    /// nothing and a nesting deeper than eight levels all stop the walk.
    /// </summary>
    private static void CollectComposition(
        RazorDocument document, Func<string, RazorDocument?> resolvePartial, HashSet<string> visited,
        int depth, List<RazorDocument> into)
    {
        if (depth >= 8) return;

        if (!string.IsNullOrWhiteSpace(document.LayoutDirective))
        {
            var layout = resolvePartial(document.LayoutDirective.Trim());
            if (layout is not null && visited.Add(layout.FilePath))
            {
                CollectComposition(layout, resolvePartial, visited, depth + 1, into);
                into.Add(layout);
            }
        }

        foreach (var name in PartialNamesIn(document, 0, document.Text.Length))
        {
            var partial = resolvePartial(name);
            if (partial is null || !visited.Add(partial.FilePath)) continue;

            CollectComposition(partial, resolvePartial, visited, depth + 1, into);
            into.Add(partial);
        }
    }

    private readonly record struct DocumentId(RazorDocument Document, string Id, int Line, int Column);

    private static void CheckImages(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var image in document.AllElements().Where(e => e.Is("img")))
        {
            if (image.HasAttribute("alt")) continue;

            Add(findings, document, "RZ2004",
                "<img> without an alt attribute: screen readers announce the file name or nothing.",
                image.Line, image.Column);
        }
    }

    private static void CheckLabels(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var label in document.AllElements().Where(e => e.Is("label")))
        {
            if (label.HasAttribute("for") || label.HasAttribute("asp-for")) continue;
            if (label.Descendants().Any(e => e.Is("input") || e.Is("select") || e.Is("textarea")))
                continue;

            Add(findings, document, "RZ2005",
                "<label> without a for attribute and without a nested control labels nothing.",
                label.Line, label.Column);
        }
    }

    private static void CheckButtons(RazorDocument document, List<RazorFinding> findings)
    {
        if (!IsCshtml(document)) return;

        foreach (var button in document.AllElements().Where(e => e.Is("button")))
        {
            if (Ancestor(button, "form") is null) continue;
            if (button.HasAttribute("type")) continue;
            if (button.HasAttributePrefix("asp-")) continue;

            Add(findings, document, "RZ2006",
                "<button> inside a form defaults to type=\"submit\"; add type=\"button\" if it is not meant to post.",
                button.Line, button.Column);
        }
    }

    private static void CheckControlNames(RazorDocument document, List<RazorFinding> findings)
    {
        if (!IsCshtml(document)) return;

        foreach (var control in document.AllElements()
                     .Where(e => e.Is("input") || e.Is("select") || e.Is("textarea")))
        {
            if (Ancestor(control, "form") is null) continue;
            if (control.HasAttribute("name") || control.HasAttribute("asp-for")) continue;

            var type = control.Literal("type")?.Trim();
            if (type is not null && NonPostingInputTypes.Contains(type)) continue;

            Add(findings, document, "RZ2007",
                $"<{control.Name}> in a form has no name attribute, so the browser does not post its value.",
                control.Line, control.Column);
        }
    }

    private static void CheckDocumentHead(RazorDocument document, List<RazorFinding> findings)
    {
        var html = document.AllElements().FirstOrDefault(e => e.Is("html"));
        if (html is null) return;

        if (!html.HasAttribute("lang"))
        {
            Add(findings, document, "RZ2009",
                "<html> has no lang attribute, so assistive technology cannot pick the right language.",
                html.Line, html.Column);
        }

        var hasViewport = html.Descendants().Any(meta =>
            meta.Is("meta") &&
            string.Equals(meta.Literal("name")?.Trim(), "viewport", StringComparison.OrdinalIgnoreCase));

        if (!hasViewport)
        {
            Add(findings, document, "RZ2008",
                "No <meta name=\"viewport\"> in the document: mobile browsers render the page at desktop width.",
                html.Line, html.Column);
        }
    }

    private static void CheckNestedForms(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var form in document.AllElements().Where(e => e.Is("form")))
        {
            if (Ancestor(form, "form") is null) continue;

            Add(findings, document, "RZ2013",
                "The form is nested inside another form; HTML drops the inner one, so its fields are never posted.",
                form.Line, form.Column);
        }
    }

    private static void CheckDuplicateAttributes(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            foreach (var group in element.Attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() < 2) continue;

                foreach (var duplicate in group.Skip(1))
                {
                    Add(findings, document, "RZ2014",
                        $"Attribute '{duplicate.Name}' appears twice on <{element.Name}>; only the first one is used.",
                        duplicate.Line, duplicate.Column);
                }
            }
        }
    }

    private static void CheckFormAction(RazorDocument document, List<RazorFinding> findings)
    {
        if (document.Kind != RazorPageKind.View) return;

        foreach (var form in document.AllElements().Where(e => e.Is("form")))
        {
            if (form.HasAttribute("action") || form.HasAttributePrefix("asp-")) continue;

            Add(findings, document, "RZ2016",
                "The form has no action and no asp-action/asp-controller, so it posts to the view's own URL.",
                form.Line, form.Column);
        }
    }

    private static void CheckDeprecatedMarkup(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var element in document.AllElements())
        {
            if (DeprecatedElements.Contains(element.Name))
            {
                Add(findings, document, "RZ2017",
                    $"<{element.Name}> is obsolete in HTML5; use an element and CSS instead.",
                    element.Line, element.Column);
            }

            foreach (var attribute in element.Attributes)
            {
                if (!DeprecatedAttributes.Contains(attribute.Name)) continue;

                Add(findings, document, "RZ2017",
                    $"Attribute '{attribute.Name}' on <{element.Name}> is obsolete in HTML5; use CSS instead.",
                    attribute.Line, attribute.Column);
            }
        }
    }

    // ── Maintainability ────────────────────────────────────────────────────────

    private static void CheckUntypedModel(RazorDocument document, List<RazorFinding> findings)
    {
        var reported = new HashSet<int>();

        foreach (var expression in document.Expressions)
        {
            var root = RootIdentifier(expression.Text);
            if (root is not ("ViewBag" or "ViewData")) continue;
            if (!reported.Add(expression.Line)) continue;

            Add(findings, document, "RZ2010",
                $"'{Shorten(expression.Text)}' uses ViewBag/ViewData, which the compiler cannot check.",
                expression.Line, expression.Column);
        }

        foreach (var block in document.CodeBlocks)
        {
            if (!block.Text.Contains("ViewBag", StringComparison.Ordinal) &&
                !block.Text.Contains("ViewData", StringComparison.Ordinal))
                continue;
            if (!reported.Add(block.Line)) continue;

            Add(findings, document, "RZ2010",
                $"The {block.Kind} block uses ViewBag/ViewData, which the compiler cannot check.",
                block.Line, 1);
        }
    }

    private static void CheckLegacyHelpers(RazorDocument document, List<RazorFinding> findings)
    {
        string[] legacy =
        [
            "Html.Partial(", "Html.RenderPartial(", "Html.Action(", "Html.RenderAction(", "Html.BeginForm(",
        ];

        foreach (var expression in document.Expressions)
        {
            var normalised = Regex.Replace(expression.Text, @"\s+", string.Empty);
            var helper = legacy.FirstOrDefault(h => normalised.StartsWith(h, StringComparison.Ordinal));
            if (helper is null) continue;

            Add(findings, document, "RZ2011",
                $"'{helper[..^1]}' is the legacy helper; prefer the <partial> tag helper or asp-action.",
                expression.Line, expression.Column);
        }
    }

    private static void CheckLargeCodeBlocks(RazorDocument document, List<RazorFinding> findings)
    {
        foreach (var block in document.CodeBlocks.Where(b => b.Kind is "block" or "code" or "functions"))
        {
            var lines = block.EndLine - block.Line;
            if (lines <= LargeCodeBlockLines) continue;

            Add(findings, document, "RZ2012",
                $"The {block.Kind} block is {lines} lines long (>{LargeCodeBlockLines}); move the logic out of the view.",
                block.Line, 1);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void Add(
        List<RazorFinding> findings,
        RazorDocument document,
        string ruleId,
        string message,
        int line,
        int column,
        string? suggestion = null)
    {
        var rule = RazorRuleCatalog.Get(ruleId);
        var snippet = document.LineText(line).Trim();
        if (snippet.Length > 180)
            snippet = snippet[..177] + "...";

        findings.Add(new RazorFinding(
            rule.Id, rule.Category, rule.Title, rule.Severity, rule.Cwe,
            message, suggestion ?? rule.Suggestion, document.FilePath, line, column, snippet));
    }

    private static bool HasAntiForgeryToken(
        RazorDocument document, RazorElement form, Func<string, RazorDocument?>? resolvePartial)
    {
        if (form.Descendants().Any(e => e.HasAttribute("asp-antiforgery")))
            return true;

        if (form.Descendants().Any(e =>
                e.Is("input") &&
                string.Equals(e.Literal("name")?.Trim(), "__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (document.Expressions.Any(e =>
                e.Offset >= form.Offset &&
                e.Offset <= form.EndOffset &&
                e.Text.Contains("AntiForgeryToken", StringComparison.OrdinalIgnoreCase)))
            return true;

        // The token may live in a partial the form renders: <partial name="_X" />, @Html.Partial("_X"),
        // @await Html.RenderPartialAsync("_X"). The resolver reads those files; without one the rule
        // stays per file, as before.
        if (resolvePartial is null) return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var partial in PartialNamesIn(document, form.Offset, form.EndOffset))
            if (PartialCarriesToken(partial, resolvePartial, visited, depth: 0))
                return true;

        return false;
    }

    private static readonly Regex PartialCallPattern = new(
        @"(?:Html\s*\.\s*)?(?:Render)?Partial(?:Async)?\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>The partial names a span renders: the <c>&lt;partial name=…&gt;</c> tag and the Html.Partial family.</summary>
    private static IEnumerable<string> PartialNamesIn(RazorDocument document, int start, int end)
    {
        foreach (var element in document.AllElements())
        {
            if (!element.Is("partial")) continue;
            if (element.Offset < start || element.Offset > end) continue;

            var name = element.Literal("name");
            if (!string.IsNullOrWhiteSpace(name)) yield return name.Trim();
        }

        foreach (var expression in document.Expressions)
        {
            if (expression.Offset < start || expression.Offset > end) continue;

            var match = PartialCallPattern.Match(expression.Text);
            if (match.Success) yield return match.Groups[1].Value;
        }
    }

    /// <summary>
    /// Whether a partial renders the token, following the partials it includes in turn. A cycle, a name
    /// that resolves to nothing and a nesting deeper than eight levels all stop the walk and answer no.
    /// </summary>
    private static bool PartialCarriesToken(
        string name, Func<string, RazorDocument?> resolvePartial, HashSet<string> visited, int depth)
    {
        if (depth >= 8 || !visited.Add(name)) return false;

        var partial = resolvePartial(name);
        if (partial is null) return false;

        if (partial.Text.Contains("AntiForgeryToken", StringComparison.OrdinalIgnoreCase) ||
            partial.Text.Contains("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var nested in PartialNamesIn(partial, 0, partial.Text.Length))
            if (PartialCarriesToken(nested, resolvePartial, visited, depth + 1))
                return true;

        return false;
    }

    private static RazorElement? Ancestor(RazorElement element, string name)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Is(name)) return parent;
        }
        return null;
    }

    private static bool IsRazorMarkup(RazorDocument document) =>
        document.FilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    private static bool IsCshtml(RazorDocument document) => IsRazorMarkup(document);

    private static string? RootIdentifier(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("await ", StringComparison.Ordinal))
            trimmed = trimmed[6..].TrimStart();
        var match = RootIdentifierPattern.Match(trimmed);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsScriptSafe(string text)
    {
        var normalised = Regex.Replace(text, @"\s+", string.Empty);
        string[] safePrefixes =
        [
            "Json.Serialize", "JsonSerializer.Serialize", "System.Text.Json.JsonSerializer.Serialize",
            "Html.Encode", "HtmlEncoder.", "JavaScriptEncoder.", "Url.Action", "Url.Content",
        ];
        return safePrefixes.Any(prefix => normalised.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsEncoded(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return false;

        var value = argument.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"' &&
            !value[1..^1].Contains('"'))
            return true;

        string[] encoders =
        [
            "Encode", "Sanitize", "Sanitizer", "HtmlEncoder", "JavaScriptEncoder",
            "Json.Serialize", "JsonSerializer.Serialize",
        ];
        return encoders.Any(encoder => value.Contains(encoder, StringComparison.Ordinal));
    }

    /// <summary>The text between the parenthesis at <paramref name="openParen"/> and its match.</summary>
    private static string ArgumentAt(string text, int openParen)
    {
        if (openParen < 0 || openParen >= text.Length || text[openParen] != '(')
            return string.Empty;

        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipString(text, i);
                continue;
            }
            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return text[(openParen + 1)..i];
            }
        }
        return text[(openParen + 1)..];
    }

    private static int SkipString(string text, int quote)
    {
        var i = quote + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == '"') return i;
            i++;
        }
        return text.Length - 1;
    }

    private static int SkipChar(string text, int quote)
    {
        var i = quote + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == '\'') return i;
            i++;
        }
        return text.Length - 1;
    }

    private static bool LooksLikeSecret(string name)
    {
        var normalised = name.Replace(".", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
        return SecretNames.Any(secret => normalised.Contains(secret.Replace("_", string.Empty).Replace("-", string.Empty),
            StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalise(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, length);
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
        return collapsed.Length <= 80 ? collapsed : collapsed[..77] + "...";
    }

    private static bool Is(this RazorElement element, string name) =>
        element.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
