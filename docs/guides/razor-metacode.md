[Docs home](../README.md) › Guides › **Razor, metacode and decompilation**

# Razor, metacode and decompilation

Three ways of reading code that is not plain C# in a Roslyn project: Razor pages, canonical pseudocode,
and compiled assemblies (managed or native).

## Razor and Blazor pages

`.cshtml` and `.razor` files are **not** Roslyn documents: the project treats them as `Content`, and the
Razor SDK compiles them with a different toolchain. VisualMCP therefore reads them **from disk**, with a
dedicated Razor/HTML scanner, and maps them to a page model.

That also means the analysis tools accept a folder or a single file and **do not need a loaded
solution** — useful for auditing a repository you have not opened.

### The four tools

| Tool | Answers |
|---|---|
| `razor_pages` | What exists and how it is wired: kind (page/view/layout/partial/Blazor component), `@page` route, `@model`, layout, sections, `@inject`, handlers, code-behind, markup counts |
| `analyze_razor_pages` | What is wrong with the markup: broken HTML, accessibility, Razor misuse, maintainability (`RZ2xxx`) |
| `find_razor_vulnerabilities` | What is exploitable: XSS, CSRF, secrets, unsafe URLs, CSP (`RZ1xxx`, each with a CWE) |
| `razor_edit` | The write side: six verified text splices |

Filters: `projectName` restricts to one project; `path` takes a folder or a file; `category`, `rule`,
`minSeverity` (default `Low`) and `maxFindings` (default 200) narrow the output. Findings report counts
by severity/category/rule over the **filtered total**, so `truncated: true` does not hide the size of the
problem.

### Security rules (`RZ1xxx`)

| Id | Severity | CWE | What it detects |
|---|---|---|---|
| `RZ1001` | High | 79 | `@Html.Raw(...)` / `new HtmlString(...)` with an unencoded value |
| `RZ1002` | High | 79 | Model, `ViewBag` or loop variable written inside `<script>` |
| `RZ1003` | High | 79 | Model data inside an inline handler (`onclick="… @Model…"`) |
| `RZ1004` | High | 79 | `javascript:`, `vbscript:` or `data:text/html` in `href`/`src`/`action` |
| `RZ1005` | Medium | 352 | POST form with no anti-forgery token |
| `RZ1006` | Medium | 598 | Password input in a GET form |
| `RZ1007` | Low | 1022 | `target="_blank"` without `rel="noopener noreferrer"` |
| `RZ1008` | High | 798 | Literal credential in markup or a code block |
| `RZ1009` | Medium | 319 | Subresource loaded over `http://` |
| `RZ1010` | Medium | 79 | `iframe src` built from data |
| `RZ1011` | Low | 693 | Inline script/style or handler that a strict CSP would block |

`RZ1001` does not fire on a literal or on a value already passed to `Encode`/`Sanitize`/`HtmlEncoder`/
`JavaScriptEncoder`/`Json.Serialize`. `RZ1002` does not fire on `@Json.Serialize`, `@Html.Encode`,
`@Url.Action` and similar.

### Markup, accessibility and maintainability (`RZ2xxx`)

| Id | Category | Severity | What it detects |
|---|---|---|---|
| `RZ2001` | Markup | Medium | Element never closed (optional-end tags such as `<li>` and `<p>` excluded) |
| `RZ2002` | Markup | Medium | Orphan or mismatched closing tag |
| `RZ2003` | Markup | Medium | Duplicate `id` in the same file |
| `RZ2004` | Accessibility | Low | `<img>` without `alt` |
| `RZ2005` | Accessibility | Low | `<label>` without `for` and without a nested control |
| `RZ2006` | Markup | Low | `<button>` in a form without `type` |
| `RZ2007` | Markup | Medium | Form control without `name`/`asp-for` |
| `RZ2008` | Accessibility | Low | Missing `<meta name="viewport">` |
| `RZ2009` | Accessibility | Low | `<html>` without `lang` |
| `RZ2010` | Maintainability | Low | `ViewBag`/`ViewData` |
| `RZ2011` | Maintainability | Low | Legacy `Html.Partial`/`RenderPartial`/`Action`/`RenderAction`/`BeginForm` |
| `RZ2012` | Maintainability | Low | Code block over 25 lines |
| `RZ2013` | Markup | Medium | Nested form |
| `RZ2014` | Markup | Low | Duplicate attribute on one element |
| `RZ2015` | Razor | High | Unbalanced Razor syntax |
| `RZ2016` | Markup | Low | MVC form without `action` and without `asp-*` |
| `RZ2017` | Markup | Low | Obsolete element or attribute (`center`, `font`, `marquee`, `align`, …) |

`RZ2006`, `RZ2007` and `RZ1005` apply to `.cshtml` only: they would be noise in Blazor (`<EditForm>`,
`@bind`).

Every finding has the same shape: `ruleId`, `category`, `title`, `severity`, `cwe`, `message`,
`suggestion`, `file`, `line`, `column`, `snippet`.

### Editing a page

`razor_edit` performs one verified splice per call:

| Operation | What it does |
|---|---|
| `set-attribute` | Set an attribute on the nth element with a tag; adds it when absent, replaces it when present |
| `remove-attribute` | Remove an attribute, whitespace included |
| `add-directive` | Insert a directive (`model`, `using`, `inject`, `layout`, `inherits`, `addTagHelper`) after the last directive |
| `insert-lines` / `replace-lines` / `delete-lines` | By 1-based line range, for edits that are not an attribute |

Safety:

- **`expect`** takes the text those lines (or that attribute value) must currently hold. If the file no
  longer matches, the edit is refused — that is what stops an edit from landing on a changed file.
- The page is **re-scanned before writing**. If the splice would raise the unbalanced-syntax count, the
  file is left untouched and the new issues are returned.
- The result reports the diff and the rule findings the edit **resolved and introduced** (adding
  `rel="noopener noreferrer"` fixes `RZ1007`).
- The write is recorded in the edit history, so `revert_edit` undoes it.
- `dryRun` defaults to `true`.

### Razor limits

- It is a **heuristic** analysis: no data flow. A value that arrives from a view model or JavaScript is not
  tracked. A clean result is not a security proof.
- Markup inside `@{ … }` and `@code { … }` is not descended: that is C#.
- `RZ1005` resolves the partials a form renders (`<partial name=…>`, `@Html.Partial("…")`, the
  `RenderPartial` family) and looks for the token inside them, nested includes included. The search is
  textual, so a token built by a helper the rule does not name can still be reported, and a partial it
  cannot resolve counts as having no token. Duplicate ids (`RZ2003`) are compared across the rendered
  composition: the page, the layout it declares (`@layout` or `Layout = "…"`) or the one the nearest
  `_ViewStart.cshtml` selects for it, and the partials either renders.
- Dynamic values (`method="@Model.Method"`) are not guessed: rules that depend on them stay silent.
- Files over 2 MB and unreadable folders are reported as `skipped`/`unreadable`, not analysed.

## Metacode

Metacode is one canonical pseudocode model for syntax trees from different languages. The same algorithm
prints the same body, line for line, whichever frontend produced it.

```
metacode_translate  path=Services/OrderService.cs
metacode_translate  path=Pages/Checkout.cshtml
metacode_compare    leftPath=OrderService.cs rightPath=OrderService.kt.mc
```

### What it looks like

- Uppercase keywords, one statement per line, operator precedence preserved.
- A construct the model has no node for stays visible as `RAW[lang] … // reason`, plus a diagnostic
  (`MC0001`–`MC0008`) listed after the text. Incomplete translation is visible, never silent.
- `showOrigin=true` prefixes declarations and statements with `// @file:line`, so a line can be traced
  back to the source.
- `normalizeControlFlow` turns a C-style `for` into a `WHILE` so every language prints the same loop;
  `flattenNestedBlocks` folds a block whose only statement is a block.

### Inputs

| Input | How |
|---|---|
| C# | `language=cs` or a `.cs` path — Roslyn syntax only, no compilation, no solution |
| Visual Basic | `language=vb` or a `.vb` path |
| Java | `language=java` or a `.java` path — also what JADX writes |
| JavaScript | `language=js` or a `.js`/`.mjs`/`.cjs`/`.jsx` path — an ES module first, a classic script second |
| Razor / Blazor | `language=razor` or a `.cshtml`/`.razor` path |
| Ghidra snapshot | A `.json` with `imageBase` + `functions`, or `language=ghidra` |
| Canonical pseudocode | `language=pseudo` or a `.mc` path |

JavaScript is parsed by Acornima (the .NET port of acorn) and lowered here, so the language is read whole:
modules and CommonJS, classes with private fields and accessors, arrow functions, template literals,
destructuring, `async`/`await`, optional chaining. Two things are deliberately not read — **JSX** and
**TypeScript annotations** are not ECMAScript, and a file containing either is reported as a syntax error at
its own line instead of being half-read. A top-level `const` becomes a field of the module; the rest of what
the file does at load time becomes the body of `__module__`, the same entry point Python's module-level code
uses.

A Razor page becomes a page class: `@model` and `@inject` are properties, `@code`/`@functions` members
are lowered as C#, an `@section Name { … }` becomes a `Section_Name` method, and the markup body is the body
of a `Render` method. Every control construct is a node of the model — `@if`/`@else`, `@foreach`, `@for`,
`@while`, `@do … while`, `@switch` with its `case`/`default` sections, `@try`/`catch`/`finally`, `@lock`,
`@using (…)`, a `@{ … }` block — with a braced or a brace-less body; the HTML between them is `RAW[razor]`.
A construct whose header or body cannot be read is one `RAW` block with a diagnostic.

The model is also an **output**: any language can join by emitting canonical pseudocode. The library
contains Python and C++ AST-JSON readers, and they are part of the default pipeline: `metacode_translate`
/ `metacode_compare` put a `.py`/`.cpp` path through the dumper themselves (CPython's `ast` module,
`clang -ast-dump=json`), and `translate_python_file` / `translate_cpp_file` do the same when the dump is
what you want back; all of them also take a dump you supply. Rust joins the other way round: its reader
parses the source itself, so a `.rs` path (or `language=rust`) is lowered with no toolchain at all.

### Decompiling managed assemblies

| Tool | What it does |
|---|---|
| `decompile_type` | One type from a `.dll` back to C#, with ILSpy |
| `decompile_assembly` | A whole module, or a filtered/paged set of types; can export one file per type |
| `inspect_assembly` | The public API — types, base types, interfaces, members, signatures — without running anything |
| `decompile_to_metacode` | ILSpy → C# → the metacode model, printed as pseudocode instead of C# |
| `decompile_package_type` / `inspect_package` | The same, starting from a NuGet package id and version |

`decompile_to_metacode` reports the type count, hidden types and errors, and can page through a large
assembly with `skip`/`maxTypes`. It is the right tool when you want to understand *what a library does*
without reading unfamiliar C# idioms.

### Recovering native binaries

`analyze_native_binary` runs **Ghidra headless** on a PE/ELF/Mach-O file, exports every function's
decompiled C, normalizes it and lowers it into the same model:

```
analyze_native_binary  binaryPath=C:\Windows\System32\notepad.exe maxFunctions=5
```

- It is a **real external run**: tens of seconds, bounded by `timeoutSeconds` (the whole process tree is
  killed) and Ghidra's own `analysisTimeoutSeconds`.
- Narrow with `function=` (name or entry address) or `maxFunctions`.
- The result includes the prototype, the entry address, the snapshot path and the log path.
- The snapshot is re-readable by `metacode_translate` **without Ghidra**.
- Ghidra is installed on demand into the per-user tools folder when no installation is found; point at
  an existing copy with `ghidraHome`, `headlessPath` or `GHIDRA_INSTALL_DIR`, and optionally a JDK
  through `javaHome` (Ghidra needs JDK 21+).
- The run is also persisted in the solution database: `native_artifact` (one row per binary, keyed on
  its SHA-256), `native_function` (Ghidra's C plus canonical pseudocode and complexity per function)
  and `native_call` (the call edges). `save=false` prints only. `metacoding action=decompile` on a
  file with no CLR metadata takes the same route automatically.

### Decompiled code lives in the same AST

`metacoding action=decompile` stores what it decompiles in the **same tables** as the solution's own
code (`mc_document`, `mc_tree`, `mc_node`): managed types under `assembly://<file>/<type>`, a native
image's functions under `native://<file>`. Every row carries two provenance columns — `source_path`
(the source file, or the **binary** for decompiled code) and `decompiled` (0 = read, 1 = decompiled) —
so `metacode_index`, the complexity report and a raw SQL query never confuse the two. The solution's own
files are all in the store too: a text file no frontend reads (a README, a `.csproj`, a JSON config) is
a single `file` node describing it, not a gap.

After the binary, the folder it came from is swept into the same AST (`siblings`, on by default): its
text files are read, its other managed assemblies decompiled, and up to two native images handed to
Ghidra. The sweep is bounded by `maxSiblings` (default 25 files) and its result comes back as a `sweep`
object with the counts and the reasons anything was skipped.

### Metacode limits

- Normalization is declarative, not an interpreter: constructs the C reader does not model (the comma
  operator, multi-level pointers, calling conventions) stay `RAW` with a diagnostic.
- **Razor is an input, like every other language.** The model keeps its own AST and produces source only
  where an emitter exists — C# today — so an edit can only be written back to a `.cs` file. That holds for
  a page, a `.vb` file and a JavaScript module alike; none of them is a special case, and none of them has
  an emitter to write it back with. Put the C# in a class or in the page's code-behind model.
- JavaScript is read as ECMAScript, so JSX and TypeScript annotations stop the parse (one `MC0001` naming
  the line). Inside the language, `with`, `debugger`, a labeled `break`/`continue`, the comma operator, a
  tagged template, a dynamic `import()` and an object literal with a spread or an accessor stay `RAW[js]`;
  `for … in`, `>>>`, an optional call and a generator are lowered with a `LossyLowering` that says what was
  lost. A re-export is printed as the import it mirrors.
- Decompiled code is **text**. Rename, navigation and the semantic analysis tools operate on source
  documents, not on decompiled output.
- ILSpy reconstructs C# from IL; the result is readable but not the original source, and names can be
  compiler-generated.
- Ghidra's analysis is heuristic. A recovered prototype is a best effort, not ground truth.

---

Next: [Build, test, packages and modernization](runtime.md)
