# What the Visual Basic sample demonstrates

Measured on 2026-10-30 with VisualMCP 1.0.0.0 (the build after the fixes listed at the bottom), .NET SDK
10.0.401, on Windows 10.0.26300. Active solution: `samples/VbDemo/VbDemo.sln` (3 projects, 19 documents,
0 workspace diagnostics on load).

## The short version

A Visual Basic solution loads, builds, tests and runs. Its symbols are navigable through Roslyn.
`simplify_vb_code` rewrites real code under a compiler gate, and the 27 tests prove the rewrite kept the
behaviour. The analyses that are C#-only return nothing (or an explicit reason) instead of guessing.

## Tool by tool

| Call | Result |
|---|---|
| `load_solution` | 3 projects, 19 documents, 0 workspace diagnostics |
| `check_solution` | `isLoaded: true`, `csharpProjectCount: 0`, `visualBasicProjectCount: 3`; the message names the tools that work and the C#-only ones |
| `build_project` | 0 errors, 0 warnings (also after the style rewrite) |
| `run_tests` | 27/27 passed, both before and after the rewrite |
| `run_project VbDemo.Cli` | `books: 2`, `1999: The Pragmatic Programmer (Hunt)`, `leap 2024: 366`, `isbn 0-306-40615-2: True` |
| `get_project_info VbDemo.Core` | language `Visual Basic`, 9 documents, 167 metadata references |
| `get_diagnostics` | 0 errors, 0 warnings |
| `find_symbol Book` | 2 exact matches: the type and the `BookAddedEventArgs.Book` property |
| `find_references BookAddedEventArgs` | 3 references across `ILibraryService.vb` and `LibraryService.vb` (the same symbol appears twice in the result: see “Reporting quirks”) |
| `find_implementations ILibraryService` | 1 implementor, 11 members mapped through the VB `Implements` clauses |
| `get_type_members LibraryService` | 15 members, including the compiler-synthesised `add_`/`remove_`/`get_` accessors |
| `read_symbol LibraryService` | only `Public Class LibraryService` — body extraction is C#-only |
| `preview_rename LibraryService → BookService` | 3 files, 7 changes: the Roslyn renamer resolves VB symbols |
| `text_index` | 17 documents / 683 lines indexed; `LibraryService` found in all five files, `StyleGallery.Witness` at line 36 |
| `simplify_vb_code` (all rules, document) | 18 edits, 14 ids, 0 refused; `.editorconfig` read (`preferIsNot: "PreferIsNot"`) |
| `inspect_code VbDemo.Core` | 10 CA findings: CA1510 ×3, CA1307 ×2, CA1002 ×2, CA1062 ×2, CA2211 ×1; 57 analyzer crash reports (AD0001) ignored |
| `find_unused_symbols VbDemo.Core` | correct names (`Repository.All`, `Repository.Count`, `PlainThing`), each reported twice |
| `solution_map` | 3 VB projects; `types: 0`, `areas: []`, `entryPoints: []`, 19 documents skipped |
| `reference_index status` | 0 documents, 0 symbols: C# only |
| `metacode_index status` | `sources: 19`, `documents: 0`: VB is stored natively, with no metacode |
| `metacode_translate` | `MC0007` — no frontend in the pipeline reads Visual Basic |
| `complexity_report` | `No method, lambda or local function covers line 29` (C# syntax tree only) |
| `analyze_complexity` | `methodCount: 0`: C# only |
| `analyze_test_quality VbDemo.Tests` | `testCount: 0`: C# only |
| `tests_covering_symbol LibraryService.Add` | 0 covering tests: direct references from C# tests only |

## The style demo in detail

`simplify_vb_code`, `scope: "document"`, `filePath: ".../StyleGallery.vb"`, every rule enabled:

- 15 rule ids selected, 18 edits across 14 ids, `refused: []`.
- The suggestion counts were `IDE0001: 3`, `IDE0081: 2`, `IDE0003: 2`, and one each for `IDE0002`,
  `IDE0041`, `IDE0029`, `IDE0031`, `IDE0030`, `IDE0047`, `IDE0100`, `IDE0075`, `IDE0084`, `IDE0004`,
  `IDE0054`.
- `IDE0049` does **not** appear in that run: `IDE0001` matches the same `System.Int32` node and comes
  first in the catalogue. One node gets one rewrite. With `rules: "IDE0049"` the same file yields 2
  edits (`System.Int32` → `Integer`), so the rule itself is exercised.
- `IDE0084` fires only because `samples/VbDemo/.editorconfig` sets
  `visual_basic_style_prefer_isnot_expression = true`; the response echoes
  `styleOptions.preferIsNot: "PreferIsNot"`.
- With `dryRun: false` the file was written (4863 → 4672 characters), `build_project` stayed green,
  `run_tests` stayed 27/27, the CLI printed the same four lines, and `edit_history` + `revert_edit`
  restored the file to its 4863-character “before” text. The demo input is intact.

## Three engine defects the demo found (fixed in this tree)

1. **Same-node collision.** With every rule enabled, `IDE0001` and `IDE0049` both match `System.Int32`.
   `StyleRewriteService` built the replacement map with `ToDictionary`, so the second match threw
   `ArgumentException` and the whole call failed. The service now groups matches by node and keeps the
   first in catalogue order. Regression test:
   `VbStyleTests.VbSimplify_WithEveryRuleEnabled_AppliesOneRewritePerNode`.
2. **Conditional access with no receiver.** `IDE0031` rewrites `If(s IsNot Nothing, s.Trim(), Nothing)`
   to `s?.Trim()`. In VB the inner `Trim` is a `MemberAccessExpressionSyntax` whose `Expression` is
   `Nothing` (the receiver is the conditional access). The next pass called `GetSymbolInfo(null)` and
   threw `ArgumentNullException`. The VB rules now skip receiver-less member accesses. Regression test:
   `VbStyleTests.VbSimplify_WithEveryRuleEnabled_SurvivesTheConditionalAccessItCreates`.
3. **`check_solution` called a VB solution unsupported.** It answered `isLoaded: false` and “do not query
   it further” whenever there was no C# project. It now reports `visualBasicProjectCount` and names the
   tools that apply, so an agent does not abandon a workspace it can use. Regression test:
   `CheckSolutionTests.A_Visual_Basic_only_solution_reports_its_projects`.

While preparing the build, the tree also had an unrelated red build from a concurrent change:
`DisassemblyService.cs` used `MetadataTokens` without `using System.Reflection.Metadata.Ecma335;`. That
line was added so the solution compiles; it is not part of the VB work.

## Reporting quirks found (not fixed here)

- `run_tests` finds no test project in the VB solution (`testProjectsDetected: []`, `projectName:
  "qaswa"`), yet it runs `dotnet test` and parses the TRX correctly: 27/27.
- `find_references` and `find_unused_symbols` report the same VB symbol twice. The names and locations
  are correct; the duplicate is in enumeration, not in resolution.
- `get_type_members` and `find_implementations` include the compiler-synthesised event accessors and
  property getters (`add_BookAdded`, `get_Count`). These are real symbols, but a VB user does not read
  them as members.
- `inspect_code` reports some CA findings twice and ignores 57 analyzer crashes (AD0001) from analyzers
  that are C#-only.

## Still C#-only (documented, not hidden)

- `read_symbol` extracts a declaration body with C# syntax; on a VB type it returns the first line.
- `solution_map` parses C# trees: VB projects appear, their types, namespaces and entry points do not.
- `reference_index` and `metacode_index` have no VB frontend (the latter keeps the native text only).
- `complexity_report`, `analyze_complexity`, `analyze_test_quality` and `tests_covering_symbol` are C#-only.
- `inspect_code` runs only the language-agnostic CA analyzers over VB.
- `find_unused_symbols` works on VB but duplicates each symbol (see above).

## Reproducing the demo

From the repository root:

```powershell
dotnet build samples\VbDemo\VbDemo.sln
dotnet test  samples\VbDemo\VbDemo.sln
```

Then, through VisualMCP:

```text
load_solution   D:\REPOSITORY\VisualMCP\samples\VbDemo\VbDemo.sln
check_solution
build_project
run_tests
run_project     VbDemo.Cli
simplify_vb_code scope=document filePath=D:\REPOSITORY\VisualMCP\samples\VbDemo\VbDemo.Core\StyleGallery.vb dryRun=true
```

Switching back to the repository itself is `load_solution` with `D:\REPOSITORY\VisualMCP\VisualMCP.sln`.
