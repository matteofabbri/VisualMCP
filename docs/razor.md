# Razor page analysis

VisualMCP reads Razor pages as **markup**, not as C# code: `.cshtml` and `.razor` files are not documents of
the Roslyn project (they are `Content`, and for the Razor SDK another toolchain compiles them), so they do
not appear in `solution.Projects[*].Documents`. The engine enumerates them from disk starting at the project
folders and scans them with a dedicated Razor/HTML parser, without adding packages: no
`Microsoft.AspNetCore.Razor.Language`, no external `dotnet`.

Three analysis tools, all read-only:

| Tool | What it answers |
|---|---|
| `razor_pages` | Which pages exist and how they are wired: `page`/`view`/`layout`/`partial`/Blazor component, `@page` route, `@model`, layout, sections, `@inject`, `OnGet`/`OnPost` handlers, code-behind, markup counts |
| `analyze_razor_pages` | What is wrong in the markup: broken HTML, accessibility, Razor misuse, maintainability — `RZ2xxx` rules |
| `find_razor_vulnerabilities` | What is exploitable: XSS, CSRF, secrets, unsafe URLs, CSP — `RZ1xxx` rules, each with a CWE |

The **write side** is `razor_edit` (`RazorEditService.cs`, in the same folder): it applies one verified
change at a time (attribute, directive or line range), re-scans the file before writing and reports which
findings it resolved or introduced, using the same rules as this page. This document covers the analysis;
the details of the operations are in the tool description.

`projectName` filters to one project of the solution. `path` accepts a folder or a single file, and in that
case **no loaded solution is needed**: it is the way to analyse a repo VisualMCP has not opened.

## Architecture

Everything lives in `SNITCH/Languages/Razor/` and is registered by `AddSnitch`:

| File | Role |
|---|---|
| `RazorModel.cs` | The model: directives, expressions, code blocks, the HTML element tree (with parent/children), 1-based positions |
| `RazorScanner.cs` | The scanner: separates markup from C#, knows `@* *@`, `@{ }`, `@( )`, `@await`, `@code`/`@functions`, `@section`, `@if`/`@foreach`/…, HTML tags with attributes, raw `<script>`/`<style>` |
| `RazorRuleCatalog.cs` | Id, category, title, severity, CWE and remediation of every rule — the findings take their metadata from here |
| `RazorRules.cs` | The rules: one pass per document, findings with `file`, `line`, `column`, `snippet` |
| `RazorAnalysis.cs` | File discovery (project folders, `bin`/`obj`/… exclusions), classification, ordering and the JSON shape of the findings |
| `RazorPagesService.cs` / `AnalyzeRazorPagesService.cs` / `FindRazorVulnerabilitiesService.cs` | The three entry points |
| `RazorEditService.cs` | The write side (`razor_edit`): verified splices, re-scan before writing, edits recorded in `edit_history` |

Data flow: `RazorAnalysis.CollectAsync` → `RazorScanner.Scan` → `RazorRules.Run` → service filter. The
scanner does **not** descend into `@{ … }` and `@code { … }` (that is pure C#): markup problems inside them
are not seen, and that is a declared choice. The body of `@if`, `@foreach`, `@section` and `@using (...)`
is markup instead and is analysed, loop variables included, so `@item.Name` inside a `<script>` is
recognised as model data.

## Security rules (`RZ1xxx`)

| Id | Severity | CWE | What it detects |
|---|---|---|---|
| `RZ1001` | High | 79 | `@Html.Raw(...)` / `new HtmlString(...)` with an unencoded value (inside an attribute too) |
| `RZ1002` | High | 79 | `@Model…`, `@ViewBag…` or an `@foreach` variable written inside `<script>` |
| `RZ1003` | High | 79 | Model data inside an inline handler (`onclick="…@Model…"`) |
| `RZ1004` | High | 79 | `javascript:`, `vbscript:` or `data:text/html` URI in `href`/`src`/`action` |
| `RZ1005` | Medium | 352 | POST form without a token: no `@Html.AntiForgeryToken()`, no tag helper (`asp-*`), or `asp-antiforgery="false"` / `[IgnoreAntiforgeryToken]` |
| `RZ1006` | Medium | 598 | `type="password"` input in a GET form (password in URL and logs) |
| `RZ1007` | Low | 1022 | `target="_blank"` without `rel="noopener noreferrer"` |
| `RZ1008` | High | 798 | Literal credential in the markup (`type="password" value="…"`, `data-api-key="…"`, assignment in a code block) |
| `RZ1009` | Medium | 319 | Resource loaded over `http://` (mixed content); `w3.org` and XML schemas are excluded |
| `RZ1010` | Medium | 79 | `src` of an `<iframe>` built from data |
| `RZ1011` | Low | 693 | Inline `<script>`/`<style>`, `style` attribute or inline handler: blocked by a strict CSP |

`RZ1001` does not fire on a literal, nor on a value already passed to `Encode`/`Sanitize`/`HtmlEncoder`/
`JavaScriptEncoder`/`Json.Serialize`. `RZ1002` does not fire on `@Json.Serialize`, `@Html.Encode`,
`@Url.Action` and the like.

## Markup, accessibility and maintainability rules (`RZ2xxx`)

| Id | Category | Severity | What it detects |
|---|---|---|---|
| `RZ2001` | Markup | Medium | Element never closed (tags with optional end, e.g. `<li>`, `<p>`, are excluded) |
| `RZ2002` | Markup | Medium | Orphaned or mismatched closing tag |
| `RZ2003` | Markup | Medium | Duplicate `id` in the rendered composition (the page, its layout and the partials either renders) |
| `RZ2004` | Accessibility | Low | `<img>` without `alt` |
| `RZ2005` | Accessibility | Low | `<label>` without `for` and without a nested control |
| `RZ2006` | Markup | Low | `<button>` in a form without `type` (default: submit) |
| `RZ2007` | Markup | Medium | Form control without `name`/`asp-for`: it is not submitted |
| `RZ2008` | Accessibility | Low | Missing `<meta name="viewport">` |
| `RZ2009` | Accessibility | Low | `<html>` without `lang` |
| `RZ2010` | Maintainability | Low | `ViewBag`/`ViewData` (untyped) |
| `RZ2011` | Maintainability | Low | Legacy helpers `Html.Partial`/`RenderPartial`/`Action`/`RenderAction`/`BeginForm` |
| `RZ2012` | Maintainability | Low | `@{ }`/`@code`/`@functions` code block over 25 lines |
| `RZ2013` | Markup | Medium | Nested form |
| `RZ2014` | Markup | Low | Duplicate attribute on the same element |
| `RZ2015` | Razor | High | Unbalanced Razor syntax (block, expression, comment, string, tag) |
| `RZ2016` | Markup | Low | Form without `action` and without `asp-*` in an MVC view |
| `RZ2017` | Markup | Low | Obsolete element or attribute (`center`, `font`, `marquee`, `align`, …) |

The rules `RZ2006`, `RZ2007`, `RZ1005` apply to `.cshtml` only: in Blazor (`<EditForm>`, `@bind`) they
would make no sense, so they are excluded to avoid noise.

## Filters and output

`analyze_razor_pages` accepts `category` (`Markup`, `Accessibility`, `Maintainability`, `Razor`), `rule`
(e.g. `RZ2004`), `minSeverity` (default `Low`) and `maxFindings` (default 200). It returns the counts by
severity/category/rule **over the filtered total**, plus the returned findings — so `truncated: true` does
not hide the size of the problem.

`find_razor_vulnerabilities` accepts `minSeverity` and `cwe` (`79` or `CWE-79`) and returns `bySeverity`,
`byCwe`, `byRule` and the findings.

Every finding has the same shape in both tools:

```json
{
  "ruleId": "RZ1001", "category": "Security", "title": "Unencoded HTML output (XSS)",
  "severity": "High", "cwe": "CWE-79",
  "message": "'Html.Raw(Model.Html)' writes 'Model.Html' to the response without HTML encoding.",
  "suggestion": "Let Razor encode the value (@Model.X) or sanitise it …",
  "file": "…/Views/Product/Index.cshtml", "line": 12, "column": 9,
  "snippet": "@Html.Raw(Model.Description)"
}
```

## Limits

- It is a **heuristic** analysis: no data flow. A value coming from a view model or JavaScript is not
  tracked; a clean result is not a proof of security.
- Markup inside `@{ … }`/`@code { … }` is not descended into (it is C#).
- `RZ1005` resolves the partials a form renders (`<partial name=…>`, `@Html.Partial("…")`, the
  `RenderPartial` family) and looks for the token inside them, following nested includes with a cycle
  guard; the search is textual, so a token built by a helper the rule does not name can still be reported,
  and a partial it cannot resolve counts as having no token. Duplicate `id`s are still checked per file.
- Dynamic values (`method="@Model.Method"`) are not guessed: the rules that depend on them stay silent
  instead of guessing.
- Files over 2 MB and unreadable folders are reported in `skipped`/`unreadable`, not analysed.

## Adding a rule

1. Add the entry to `RazorRuleCatalog.Rules` (id `RZxxxx`, category, severity, CWE if it is a security rule,
   remediation) — findings cannot exist without metadata.
2. Add the check to `RazorRules.Run`, with `Add(findings, document, "RZxxxx", message, line, column)`.
3. Add a case to `RazorTests` that fires and one that must **not** fire.
4. Update the table in this file.
