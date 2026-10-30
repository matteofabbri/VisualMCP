namespace Snitch.Languages.Razor;

/// <summary>One finding from the Razor rules, with everything needed to fix it in place.</summary>
public sealed record RazorFinding(
    string RuleId,
    string Category,
    string Title,
    string Severity,
    string? Cwe,
    string Message,
    string Suggestion,
    string FilePath,
    int Line,
    int Column,
    string Snippet);

/// <summary>
/// The metadata of every Razor rule. Findings take their category, title, severity and remediation
/// from here, so a rule id cannot drift away from what the tools report about it.
/// </summary>
public sealed record RazorRuleDefinition(
    string Id,
    string Category,
    string Title,
    string Severity,
    string? Cwe,
    string Suggestion);

public static class RazorRuleCatalog
{
    public static readonly IReadOnlyList<RazorRuleDefinition> Rules =
    [
        // ── Security ────────────────────────────────────────────────────────────
        new("RZ1001", "Security", "Unencoded HTML output (XSS)", "High", "CWE-79",
            "Let Razor encode the value (@Model.X) or sanitise it with an HTML sanitiser before Html.Raw; never pass request data to a raw sink."),
        new("RZ1002", "Security", "Model data inside a script block (XSS)", "High", "CWE-79",
            "Serialise the value with @Json.Serialize (or System.Text.Json) so it is JavaScript-safe; HTML encoding does not protect a <script> block."),
        new("RZ1003", "Security", "Model data in an inline event handler (XSS)", "High", "CWE-79",
            "Attach the handler in a script file with addEventListener and pass data through data-* attributes or a JSON payload."),
        new("RZ1004", "Security", "javascript: or data:text/html URI (XSS)", "High", "CWE-79",
            "Allow only http(s) URLs for links and image sources; reject or normalise javascript:, vbscript: and data:text/html."),
        new("RZ1005", "Security", "POST form without anti-forgery protection (CSRF)", "Medium", "CWE-352",
            "Render @Html.AntiForgeryToken() inside the form or use the form tag helper (asp-action/asp-page injects the token), and validate it on POST."),
        new("RZ1006", "Security", "Password field submitted with GET", "Medium", "CWE-598",
            "POST credentials, never GET them: a GET form puts the password in the URL, history and server logs."),
        new("RZ1007", "Security", "target=_blank without noopener", "Low", "CWE-1022",
            "Add rel=\"noopener noreferrer\" to a link that opens a new tab."),
        new("RZ1008", "Security", "Hardcoded credential in markup", "High", "CWE-798",
            "Remove the literal: read the secret from configuration, user secrets or a vault, and never render it into the page."),
        new("RZ1009", "Security", "Cleartext http:// resource", "Medium", "CWE-319",
            "Serve the resource over https://; a plain-http subresource is mixed content and can be modified in transit."),
        new("RZ1010", "Security", "Iframe src built from data", "Medium", "CWE-79",
            "Allowlist the framed URLs (host and scheme) before binding them to an iframe src."),
        new("RZ1011", "Security", "Inline script or event handler (CSP)", "Low", "CWE-693",
            "Move the script into a file served with a nonce/hash, and attach handlers with addEventListener so a strict CSP can be enforced."),

        // ── Markup ──────────────────────────────────────────────────────────────
        new("RZ2001", "Markup", "Unclosed HTML element", "Medium", null,
            "Close the element, or use a void element (<input>, <img>, …) where no content follows."),
        new("RZ2002", "Markup", "Stray or mismatched closing tag", "Medium", null,
            "Remove the extra closing tag or fix the element it was meant to close."),
        new("RZ2003", "Markup", "Duplicate id attribute", "Medium", null,
            "Make the id unique; duplicates break label association, getElementById and aria references."),
        new("RZ2006", "Markup", "Button inside a form without a type", "Low", null,
            "Add type=\"submit\" for the submit button or type=\"button\" for a button that only runs script."),
        new("RZ2007", "Markup", "Form control without a name", "Medium", null,
            "Add a name attribute (or asp-for) — a control without a name is not posted with the form."),
        new("RZ2013", "Markup", "Nested form", "Medium", null,
            "Remove the inner form or move it outside: HTML does not allow nested forms, and the inner one is dropped."),
        new("RZ2014", "Markup", "Duplicate attribute", "Low", null,
            "Remove the duplicate attribute; browsers keep only the first one."),
        new("RZ2016", "Markup", "Form without an action in an MVC view", "Low", null,
            "Add asp-action/asp-controller (or action) so the form posts to the intended endpoint instead of the view's URL."),
        new("RZ2017", "Markup", "Deprecated element or attribute", "Low", null,
            "Use the HTML5 element and CSS instead (e.g. <div> with text-align instead of <center>/align)."),

        // ── Accessibility ───────────────────────────────────────────────────────
        new("RZ2004", "Accessibility", "Image without alt text", "Low", null,
            "Add alt text that conveys the image's purpose, or alt=\"\" when the image is decorative."),
        new("RZ2005", "Accessibility", "Label not associated with a control", "Low", null,
            "Give the label a for attribute matching the control's id, or nest the control inside the label."),
        new("RZ2008", "Accessibility", "Missing viewport meta tag", "Low", null,
            "Add <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" /> to the layout."),
        new("RZ2009", "Accessibility", "html element without a lang attribute", "Low", null,
            "Add lang=\"en\" (or the page's language) to <html> so screen readers use the right pronunciation."),

        // ── Maintainability / Razor ─────────────────────────────────────────────
        new("RZ2010", "Maintainability", "ViewBag/ViewData in a Razor view", "Low", null,
            "Use a strongly typed view model (@model) — ViewBag/ViewData fail at runtime on a typo and are invisible to the compiler."),
        new("RZ2011", "Maintainability", "Legacy Html helper", "Low", null,
            "Prefer the tag helper (<partial>, asp-action) over Html.Partial/RenderPartial/Action/RenderAction."),
        new("RZ2012", "Maintainability", "Large code block in a view", "Low", null,
            "Move the logic to the page model, controller or a helper; a view should render, not compute."),
        new("RZ2015", "Razor", "Unbalanced Razor syntax", "High", null,
            "Fix the unclosed C# block, expression, string or comment — the rest of the file will not parse as intended."),
    ];

    private static readonly Dictionary<string, RazorRuleDefinition> ById =
        Rules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Severities = ["Info", "Low", "Medium", "High", "Critical"];

    public static RazorRuleDefinition Get(string id) =>
        ById.TryGetValue(id, out var rule)
            ? rule
            : throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Razor rule id.");

    public static bool TryGet(string id, out RazorRuleDefinition rule) => ById.TryGetValue(id, out rule!);

    /// <summary>1 = Info … 5 = Critical; 0 for an unknown label.</summary>
    public static int Rank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "info" => 1,
        "low" => 2,
        "medium" => 3,
        "high" => 4,
        "critical" => 5,
        _ => 0,
    };

    public static bool IsSecurity(string category) =>
        category.Equals("Security", StringComparison.OrdinalIgnoreCase);
}
