# Agent ergonomics — the round-trip backlog

This page is the work list for one question: **how many calls does it take to answer a question?**
[gaps.md](gaps.md) is about what the product does not do; this page is about how much navigation
the product forces on the agent that uses it.

Every item here was found in the session store, not invented: the numbers come from
`~/.zcode/cli/db/db.sqlite` (the ZCode sessions of this project, read-only) and from
[scripts/analyze-induced-chains.py](../scripts/analyze-induced-chains.py).

---

## Why the counts on this page are a floor

All of it is measured on **our own sessions**, which are the easiest case that exists: the model
has the project history, the conventions, the memory, the names of the files. A client's agent
starts from zero, in a solution it has never seen, and pays for every orientation call the first
time.

Two consequences:

- The counts below are a **floor**. If an expert session still spends 12 calls to answer "what does
  Visual Basic cover", an unfamiliar client session spends more.
- A round-trip is not only latency. It is a **place where the answer can be wrong**: the model
  reconstructs a fact from three greps and a read, and reconstructs it differently each time. The
  documentation drift in this repository is the proof — the style coverage sat in the pages as
  84/14 in one place and 87/15 in another, and was corrected **by hand**, page by page. Nothing
  computes that number, so the next rule added will drift the pages again in the same way. The
  chain that produced the wrong page is the chain this backlog removes.

So the target is not a shorter log. It is an answer that the product computes once, the same way
every time, in one call.

---

## How the numbers were produced

```bash
python scripts/analyze-induced-chains.py --project VisualMCP --detail 8
```

Snapshot: 2026-10-29, **51 sessions of this project, 10,249 recorded tool calls**, of which 4,185
VisualMCP. The script's own summary for the same data: **244 induced round-trips** over 4,176
VisualMCP calls in 736 turns.

Definitions used throughout:

| Term | Meaning |
|---|---|
| **induced call** | call B exists only because call A's output taught the model a name (a path, a symbol, a type) it did not have before |
| **paging** | a second `read_file` of a file already read in the same turn |
| **tour** | `list_directory` followed by two or more reads within the next five calls |
| **capability-shaped search** | a `search_text` whose pattern names a language, a platform, a rule catalogue or a parity claim (`VisualBasic`, `LanguageNames`, `Xaml`, `Supported`, `NotRewritten`, `ReSharper`…) instead of an identifier |

---

## The chains, measured

| Chain | Calls | What the second call is really asking |
|---|---:|---|
| `search_text` → `read_file` | **134** | "now show me the code around that match" |
| `read_file` → same file again in the turn | **46** | "the range I guessed was wrong" |
| `list_directory` → `read_file` | **47** | "which of these files is the one?" |
| `search_text` → same file, second pattern | **27** | "and does it also say…?" |
| `build_project` → `read_file` | **9** | "show me the line the error is on" |
| `locate_nuget_package` → `inspect_assembly` | **5** | "now inspect what you just found" |
| Total | **268** | |

Two findings behind those rows:

- **141 of the 275 `list_directory` calls are over code directories** where the symbol index could
  answer directly. In the 7 tours sampled by hand, **7 out of 7 read files outside the directory
  that was just listed**: the listing was an orientation probe, not an answer.
- **1,957 raw navigation calls** (`search_text` 771, `read_file` 710, `list_directory` 275,
  `read_files` 111, `read_tree` 90) against **104 symbol-level calls** (`find_symbol` 54,
  `read_symbol` 17, `search_code` 16, `find_references` 8, `get_type_members` 7,
  `find_implementations` 1, `get_symbol_info` 1, and zero for `find_callers`, `find_derived_types`,
  `reference_index`, `metacode_index`, `text_index`). The semantic layer is used at 5% of the
  textual one.
- **40 of 771 `search_text` calls are capability questions** — "which languages does this support",
  "which rules are rewritten" — answered by grepping the catalogue files by hand.

The turn shown in the sessions UI is the case study: **12 calls** to answer "what does Visual Basic
cover, and is there already a public comparison page" — five greps over different roots for the
language gates, three `search_code` for symbol lookup, two document reads, one model read and one
directory listing.

---

## Work items

State: ⬜ not started · 🟨 in progress · ✅ shipped.

| # | Item | Addresses | State |
|---|---|---|---:|
| W1 | `read_files` with line ranges | 46 paging calls | ⬜ |
| W2 | `search_text` with a `detail` mode | 134 + 27 calls | ⬜ |
| W3 | `read_tree` with a per-file summary; fold `list_directory` into it | 47 + 61 tours | ⬜ |
| W4 | `build_project` returns the source line of each diagnostic | 9 calls | ⬜ |
| W5 | `inspect_package` resolves dependency assemblies | 5 calls | ⬜ |
| W6 | `solution_map` — the map the model rebuilds by hand | the orientation probes | ⬜ |
| W7 | `capability_report` — language × area × proof × page | 40 capability searches, the 12-call turn | ⬜ |
| W8 | Coverage numbers in the docs generated from W7 | the hand-typed counts in the pages | ⬜ |
| W9 | Routing: the built-in `Read`/`Edit`/`Grep` still win | Edit 3,131 · Read 1,331 | ⬜ |

### W1 — `read_files` with line ranges

**Change.** Accept `{path, fromLine, toLine}` entries in `paths`, today a flat list of paths.
`read_file` keeps its single-range form.

**Where.** [src/VisualMCP/Tools/IO/ReadFilesTool.cs](../src/VisualMCP/Tools/IO/ReadFilesTool.cs).

**Done when.** `read_files` can read two ranges of two files in one call, and a repeated
`read_file` on the same file inside one turn disappears from the analysis.

### W2 — `search_text` with a `detail` mode

**Change.** One search tool, three shapes of the same answer:
`detail: "lines"` (today's `search_text`), `"member"` (today's `search_code` — the hit resolved to
the declaration that contains it), `"file"` (each matching file once, with the matching lines and
its top-level outline). Keep `search_code` as an alias so existing prompts do not break.

**Where.** [src/VisualMCP/Tools/Search/SearchTextTool.cs](../src/VisualMCP/Tools/Search/SearchTextTool.cs),
[src/VisualMCP/Tools/Search/SearchCodeTool.cs](../src/VisualMCP/Tools/Search/SearchCodeTool.cs).

**Why a mode and not a second tool.** The two tools are the same question at two resolutions, and
the model already pays for guessing which one it needs: when it guesses `lines` it must then read
the file (134 times), when it guesses `member` it gets more than it asked for (`search_code` is used
16 times in total, against 771 for `search_text`). Also accept a list of patterns in one call — that
is the 27 same-file-second-pattern calls.

**Done when.** A "find it and show me the code" question is one call, and `search_text`'s
`→ read_file` rate in the analysis is under 5%.

### W3 — `read_tree` with a per-file summary; fold `list_directory` into it

**Change.** `read_tree` gains `summary: true`: for each file, one line — the first doc comment or
the type it declares for code, the first heading for Markdown. `list_directory` becomes `read_tree`
with `depth: 1` (a deprecated alias, not a second implementation): 275 + 90 calls are two tools for
one question, and 44 times a `list_directory` is followed by another `list_directory`.

**Where.** [src/VisualMCP/Tools/IO/ReadTreeTool.cs](../src/VisualMCP/Tools/IO/ReadTreeTool.cs),
[src/VisualMCP/Tools/IO/ListDirectoryTool.cs](../src/VisualMCP/Tools/IO/ListDirectoryTool.cs).

**Done when.** A tour ("which file in here do I open?") is answered by the listing itself — measured
as: in the sampled tours, the file read is inside the listed directory, or there is no read at all.

### W4 — `build_project` returns the source line of each diagnostic

**Change.** Each error/warning in the structured result carries the offending source line (and one
line of context) when the file exists. Nine `build_project` → `read_file` chains exist only to fetch
a line the build already knows the location of.

**Where.** [src/VisualMCP/Tools/Execution/BuildProjectTool.cs](../src/VisualMCP/Tools/Execution/BuildProjectTool.cs).

**Done when.** The CS#### / analyzer finding can be acted on without a read.

### W5 — `inspect_package` resolves dependency assemblies

**Change.** `inspect_package`/`inspect_packages` accept the assembly a package *depends on* (or a
`dependencies: true` flag that inspects the direct dependencies too), so
`locate_nuget_package` → `inspect_assembly` stops being a two-step. The remaining four chains are
exactly this: the model finds a package, then wants a DLL that package pulled in.

**Where.** [src/VisualMCP/Tools/CSharp/Packages/InspectPackageTool.cs](../src/VisualMCP/Tools/CSharp/Packages/InspectPackageTool.cs).

### W6 — `solution_map`: the map the model rebuilds by hand

**Change.** One call that answers "what is this solution made of, and where does a thing live":
per project and per area, the key types — entry points, DI registrations, the catalogues, the
implementations of the interfaces that define an extension point (`ILanguageFrontend`,
`IStyleLanguage`, `IRoslynWorkspace`…), each with `file:line` and, where a page documents it, the
page.

**Where.** The data is already stored: `si_symbol` / `si_usage` written by `reference_index`,
the metacode trees in `.visualMCP/visualmcp.db`, and the documentation index that
`read_project_docs` already builds. Engine side, next to
[src/VisualMCP/Tools/Maintenance/ReferenceIndexTool.cs](../src/VisualMCP/Tools/Maintenance/ReferenceIndexTool.cs).

**Why.** `list_directory` is used as a probe because there is no map: 61 turns list a folder and
then read at least two files, and in the 7 tours sampled by hand every read was outside the folder
just listed. `get_project_info` lists files, `solution_audit` reports health, `reference_index`
returns raw declarations — none of them answers "where does language support live".

**Done when.** The orientation probe (`list_directory` over a code directory) disappears from the
analysis, and `find_implementations`/`reference_index` use rises above zero.

### W7 — `capability_report`: language × area × proof × page

**Change.** One call returning the matrix the 12-call turn was reconstructing:

| Language | Area | State | Proof | Documented in |
|---|---|---|---|---|
| C# | style | 87 ids, 39 with a reason | `StyleRewriteCatalog` | guides/refactoring.md |
| Visual Basic | style | 15 ids, 34 with a reason | `VbStyleRewriteCatalog` | reference/limits.md |
| Razor | markup analysis | 28 rules | `RazorRuleCatalog` | razor.md |
| … | … | … | … | … |

Rows are derived, never typed by hand:

- **languages** — the `LanguageNames` constants and the gates that compare against them (this is
  the grep the turn repeated five times);
- **areas** — the `[McpServerTool]` surface, grouped, plus the catalogues the tools delegate to;
- **counts** — read from the catalogues themselves (`StyleRewriteCatalog`, `VbStyleRewriteCatalog`,
  the inspection packs, the refactoring service list);
- **documented in** — the documentation index, so a capability with no page is visible as such.

**Where.** New tool under `src/VisualMCP/Tools/Analysis/`; engine side beside the catalogues it
reads. A new tool must also be added to `ServerInstructions` in
[src/VisualMCP/Program.cs](../src/VisualMCP/Program.cs) and counted in
[CAPABILITY_MAP.md](../CAPABILITY_MAP.md).

**Done when.** "What does language X support, and is it documented?" is one call, and the answer
carries the symbol that proves each row.

### W8 — Coverage numbers in the docs generated from W7

**Change.** The counts in [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md),
[reference/limits.md](public/reference/limits.md), [gaps.md](gaps.md) and
[CAPABILITY_MAP.md](../CAPABILITY_MAP.md) are generated from W7 instead of typed. The corrections
already made to those pages were made by hand, one line at a time, and nothing keeps them in step
with the catalogues: the next rule added to `StyleRewriteCatalog` re-opens the same drift.

**Done when.** A test compares the numbers in the pages with the numbers the catalogues return, and
fails the build on a mismatch.

### W9 — Routing: the built-in `Read`/`Edit`/`Grep` still win

**Change.** In this project the model used the **built-in** `Edit` 3,131 times and `Read` 1,331,
against `read_file` 710 and no semantic tool anywhere near those numbers. Whatever W1–W8 improve,
more than half of the file operations never reach the product. The `PreToolUse` guard covers shell
probes; it does not cover the built-in Read/Edit/Grep, and it does not cover a shell command issued
through `run_command` (1,202 calls) or `Bash` (572).

**Done when.** The share of file operations that go through VisualMCP is measured and reported, and
the routing rules cover the built-in tools as well as the shell ones.

---

## What is not reducible

Keep these out of the backlog — they are work, not waste:

- `Edit` → `run_command` / `build_project`: edit, build, fix is the loop. 175 repeated `Edit` calls
  in a turn are iteration on code, not duplicated questions.
- `run_command` for builds, tests and git pipelines: it is the escape hatch, and 1,202 calls say it
  is a real one.
- Any read that is genuine code reading. W1–W3 make the *choice* of what to read right; they do not
  remove the reading.

---

## The acceptance gate

Re-run the analysis after each item and compare. A shipped item must move its own row:

```bash
python scripts/analyze-induced-chains.py --project VisualMCP --detail 8
```

| Metric | Today | Target |
|---|---:|---|
| induced round-trips / VisualMCP calls | 244 / 4,176 (5.8%) | < 2% |
| `search_text` → `read_file` | 134 | < 40 |
| `read_file` re-read in the same turn | 46 | < 10 |
| `list_directory` over code directories | 141 | < 20 |
| capability-shaped `search_text` | 40 | 0 (answered by W7) |
| symbol calls / navigation calls | 104 / 1,957 (5%) | > 0.2 |

The scripts are part of the product's test surface: they read the session store, so they measure
what the agent actually did, not what the tool descriptions intend.

---

## How this list is maintained

- One row per measured chain, not per idea. An item without a number in
  [The chains, measured](#the-chains-measured) does not belong on this page.
- When an item ships, move its row to ✅, add the tool to
  [CAPABILITY_MAP.md](../CAPABILITY_MAP.md), and delete the corresponding workaround row in
  [gaps.md § 13](gaps.md#13-what-to-do-today) if it exists.
- Re-measure with a fresh snapshot when a batch closes; the counts drift with the way we work, and
  a stale number is worse than no number.
