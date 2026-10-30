# VbDemo — a Visual Basic solution used as a live demonstration

This folder is a small, self-contained Visual Basic solution inside the VisualMCP repository. It exists
so the Visual Basic surface of the server can be exercised — and measured — against real code instead of
a synthetic snippet. It is **not** part of `VisualMCP.sln`: the C# build is unaffected, and the server
discovers it as a second solution because it scans every `.sln` under its working directory (the root
solution stays the active one until you call `load_solution`).

What the demo actually proves, tool by tool, is in [DEMO.md](DEMO.md). Every number there was measured
on 2026-10-30 against the build in this tree.

## Layout

| Path | What it is |
|---|---|
| `VbDemo.sln` | Three Visual Basic projects, all `net10.0` |
| `VbDemo.Core` | The library: `Book`, `LibraryService` (event, LINQ, iterator, async, `SyncLock`), `Repository(Of T)`, `Isbn`, and `StyleGallery` |
| `VbDemo.Cli` | A console app that prints a few lines through the library |
| `VbDemo.Tests` | 27 MSTest tests (MSTest 4.0.2), all green |
| `.editorconfig` | `visual_basic_style_prefer_isnot_expression = true` — the option `IDE0084` needs a direction from |
| `Directory.Build.props` | `Option Strict On`, `Option Explicit On`, `Option Infer On` for the whole demo |

`StyleGallery.vb` is deliberately unidiomatic: one member per Visual Basic code-style rule. It is the
input of the style demo, and the tests pin the behaviour of every member, so a rewrite is proven to
preserve it. It is the one file with `Option Strict Off`: the legacy `x = Nothing` shape (`IDE0041`) is a
compiler error under `Option Strict On` (`BC32013`), which is exactly why the rule exists.

## Build and test

```powershell
dotnet build samples\VbDemo\VbDemo.sln
dotnet test  samples\VbDemo\VbDemo.sln
dotnet run --project samples\VbDemo\VbDemo.Cli\VbDemo.Cli.vbproj
```

## The demo, through VisualMCP

```text
load_solution  samples/VbDemo/VbDemo.sln
check_solution   → isLoaded: true, csharpProjectCount: 0, visualBasicProjectCount: 3
build_project    → 0 errors, 0 warnings
run_tests        → 27/27 passed
run_project      → VbDemo.Cli: books: 2 ...
simplify_vb_code → scope=document, filePath=samples/VbDemo/VbDemo.Core/StyleGallery.vb, dryRun=true
                   18 edits, 14 ids, 0 refused (IDE0049 is shadowed by IDE0001: see DEMO.md)
```

The rest of the sequence — navigation, references, rename preview, text index, and the tools that are
still C#-only — is in [DEMO.md](DEMO.md).

## The style gallery

| Id | Shape in the file |
|---|---|
| IDE0001 | `System.Collections.Generic.List(Of String)` → `List(Of String)` |
| IDE0002 | `StyleGallery.Witness` → `Witness` |
| IDE0003 | `Me._text` → `_text` |
| IDE0004 | `CType(value, Integer)` → `value` |
| IDE0049 | `System.Int32` → `Integer` (per-rule run; with every rule on, IDE0001 owns the node) |
| IDE0041 | `value = Nothing` → `value Is Nothing` (legacy `Option Strict Off` shape) |
| IDE0029 | `If(book IsNot Nothing, book, …)` → `If(book, …)` |
| IDE0030 | `If(value.HasValue, value, 0)` → `If(value, 0)` |
| IDE0031 | `If(book IsNot Nothing, book.Title, Nothing)` → `book?.Title` |
| IDE0047 | `(book.Title)` → `book.Title` |
| IDE0054 | `total = total + 1` → `total += 1` |
| IDE0100 | `adult = True` → `adult` |
| IDE0075 | `If(positive, True, False)` → `positive` |
| IDE0084 | `Not (left Is right)` → `left IsNot right` |
| IDE0081 | `ByVal left As Integer` → `left As Integer` |

## Running the style demo for real

`simplify_vb_code` with `dryRun: false` rewrites `StyleGallery.vb` in place. The file is then no longer
the demo input: the build stays green and the 27 tests still pass, but to repeat the demo you have to
put it back. Two ways:

- `edit_history` with `file` set to the gallery path returns the edit id; `revert_edit` with that id
  restores the previous text exactly (this is the route DEMO.md used).
- If the sample is committed in your working tree, `git checkout -- samples/VbDemo/VbDemo.Core/StyleGallery.vb`.

Do not tidy the gallery by hand: every member is there to feed one rule, and the test suite depends on
its shapes (not on its spelling).
