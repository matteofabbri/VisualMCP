[Docs home](../README.md) › Guides › **Reading and analysing code**

# Reading and analysing code

The first rule of working in a .NET solution with VisualMCP: **ask the compiler, not the text**. This
guide covers how to find, read and understand code, and how to pick the right analysis tool for a
question.

## Start with the project's own docs

`read_project_docs` returns the solution's README and Markdown documentation, indexed automatically when
the solution opens. It is the cheapest way to learn what a repository is for before reading any code.
Combine it with `solution_audit` (below) for a first pass over an unfamiliar solution.

## Find and read

### The lookup table

| You want | Call |
|---|---|
| A type or member by name | `find_symbol` |
| The **source** of that declaration | `read_symbol` |
| Every place a symbol is used | `find_references` |
| Every method that calls this one | `find_callers` |
| Every implementation of an interface | `find_implementations` |
| The subclasses of a class | `find_derived_types` |
| The symbol at a file and line | `get_symbol_info` |
| A type's full member list, with signatures and docs | `get_type_members` |
| The XML docs of a symbol | `get_xml_docs` |
| The project's files, references and packages | `get_project_info` |

### Why not grep

Text search answers a different question: *where does this string appear?* On real C# that is not the
same as *where is this symbol used?*:

- A method name can be an overload; grep cannot tell which one a call resolves to.
- A call through an interface, a base class or a delegate has no textual trace of the concrete method.
- Comments, string literals, documentation and same-named members of unrelated types all match.
- Partial types, generics and nullability make the declaration harder to recognise from text.

`find_references` and `find_callers` resolve the **symbol**, so they return the real uses and skip the
noise. `search_text` still exists for arbitrary text — configs, logs, C/C++ headers, Markdown — and
`text_index` is a full-text index when you want ranked, word-based search across the whole solution.

### A typical walk

```
find_symbol            → OrderService.ChargeAsync (3 matches, in two projects)
read_symbol            → the method body, with line numbers
find_references        → 14 real uses: 9 calls, 3 interface implementations, 2 registrations
find_callers           → the 4 methods that call it
get_type_members       → the rest of OrderService, with signatures and docs
```

Each of those is one call. A text-based agent typically spends several round trips per step and still
misses the interface dispatch.

## Build and diagnostics

| Question | Tool |
|---|---|
| Does the solution compile, and what are the errors? | `build_project` |
| What does the compiler think right now? | `get_diagnostics` |
| Fix the mechanical compiler diagnostics | `fix_diagnostics` |
| What do the configured analyzers say? | `run_code_analysis` |
| What do the built-in inspection packs say? | `inspect_code` |
| Pull errors out of a native/MSBuild log | `extract_build_log_errors` |

`build_project` runs `dotnet build` and returns structured diagnostics rather than a log tail. By
default it runs the configured analyzers too, and it compiles without copying output so a running app
does not lock the DLL. `get_diagnostics` is the Roslyn view of the solution and does not require a
build.

`fix_diagnostics` is the write side of that pair: it rewrites eight mechanical `CS####` shapes — an
unused local (`CS0168`/`CS0219`), an unused private field (`CS0169`), an unused event (`CS0067`), a
duplicated `using` (`CS0105`), a member that hides an inherited one (`CS0108`/`CS0114`) and a `new` that
hides nothing (`CS0109`). Every fix is accepted only when the project compiles no worse, the run is
refused if the fixes together would add an error, and anything outside that set comes back as skipped
with the reason. Preview by default; written edits are in the edit history.

A useful loop after any change:

```
build_project → get_diagnostics → fix_diagnostics → run_code_analysis → run_tests
```

## Choose the right analysis

`solution_audit` is the front door: one call returns inspections grouped by id, compiler
diagnostics, the most complex methods, the largest files, the project graph with cycles, and suggested
first moves. It is cached for an unchanged solution, so a second call is nearly free.

When you need to go deeper:

| Question | Tool | What makes it different |
|---|---|---|
| What is wrong with this solution overall? | `solution_audit` | Aggregates several analyses, with a prioritised shortlist |
| What would an IDE flag? | `inspect_code` | Built-in Rider-style packs (Roslynator, .NET analyzers, AsyncFixer, Meziantou, StyleCop) plus the VisualMCP house rules (`VMC0001`: one type per file) |
| Fix one of those findings | `apply_code_fix` | Runs the analyzer's own `CodeFixProvider`; preview by default, FixAll supported |
| How many paths must a reader hold? | `complexity_report`, `get_complexity_hotspots`, `get_metrics` | McCabe cyclomatic, Sonar cognitive, Halstead and maintainability index, with the lines that own the score |
| How does this scale with *n*? | `analyze_complexity` | Static Big-O estimate: loop nesting, LINQ chains, recursion shape, allocation sites, callee propagation |
| This method is too complex — lower it | `reduce_complexity` | Plans and applies extract-block, name-boolean and guard-clause moves, re-measures, rolls back if a step breaks compilation |
| Make the whole codebase simpler | `fix_complexity` | Ranks the solution, lowers the worst methods with the same moves, and reports before/after in one call |
| Where does a refactoring pay first? | `git_churn_hotspots` | Joins git history (how often a file changes) with the complexity ranking (its worst method); read-only, and a file that only *looks* big is not a hotspot |
| Did this branch make the code better or worse? | `get_quality_trend` | Two revisions (or `to=worktree`) measured with the same syntactic rule set, with a verdict and the files whose complexity moved most; nothing is compiled, so a revision that no longer builds is still comparable |
| Are the inputs actually validated? | `find_missing_validation` | Public parameters never null-checked, and DTO-shaped classes/records whose properties carry no `System.ComponentModel.DataAnnotations` validation |
| Is this code reachable? | `find_dead_code` | Control-flow graph, not heuristics over text |
| Is this symbol used anywhere? | `find_unused_symbols` | Roslyn reference search across the solution |
| Is this code duplicated? | `find_duplicate_code` | Structural clones, normalised over names and literals |
| Is there a security problem? | `find_security_issues` | CWE-tagged: injection, crypto, secrets, deserialization, SSRF, XXE, TLS, path traversal |
| Is the async code wrong? | `find_async_anti_patterns` | `.Result`/`.Wait()`, `async void`, await in loops, `Task.Run` over sync code |
| Is this loop allocating? | `find_performance_anti_patterns` | `string +=` in a loop, a `ToList()`/`ToArray()` inside a loop, a collection allocated per iteration — read from the type model, not from the spelling |
| Is the DI container sound? | `analyze_dependency_injection` | Captive dependencies, duplicate registrations, unresolved services, service locator |
| Are the namespaces tangled? | `analyze_namespace_coupling`, `analyze_circular_namespace_dependencies` | Ca/Ce, instability, abstractness — and real namespace cycles |
| What will this change break? | `plan_feature_change` | Ordered impact plan: callers, implementors, derived types, files, tests |
| Where do I start? | `list_analysis_tools` | The catalogue with when to use each tool |

### The two meanings of "complexity"

They are easy to confuse, and using the wrong one gives a misleading answer:

- **Cyclomatic / cognitive complexity** measures how many paths a *reader* has to hold in their head.
  Use `complexity_report` for one method with the exact lines that own the score, `get_complexity_hotspots`
  for a ranked list, `get_metrics` for a table, and `fix_complexity` to lower the worst ones in one call
  (`reduce_complexity` when you already know the exact method).
- **Big-O** measures how cost grows with input size. Use `analyze_complexity`. It is a static
  upper-bound heuristic; `profile_*` measures what actually happens.

### Confidence

These tools are static. They are precise about what the compiler knows (symbols, overloads, types,
control flow) and honest about what they infer:

- `analyze_complexity` reports a confidence and its evidence (loops with line numbers, recursion shape,
  memory sites). Treat it as a map of suspicious code, not a benchmark.
- `find_security_issues` is a pattern and type-based scan, not a proof. A clean result is not a security
  audit.
- `find_dead_code` follows reachability in the analysed solution; code invoked by reflection or a
  framework convention may look dead.
- `inspect_code` is only as complete as the packs and rules you enable; use `enableAll` and
  `minSeverity=info` for the widest view.

## Worked examples

**"Where is this used and what breaks if I change it?"**

```
find_symbol            OrderService.ChargeAsync
find_references        → every call, implementation and registration
plan_feature_change    → impact graph with files and tests, in order
```

**"Make this solution understandable before I touch it."**

```
read_project_docs      → what the project says about itself
solution_audit         → health report and first moves
find_unused_symbols    → dead weight
analyze_dependencies   → project graph, cycles
```

**"Why is this method unreadable?"**

```
complexity_report      → score and the lines responsible
reduce_complexity      → a preview of the refactorings that lower it, re-measured
```

**"The codebase is too complex — clean it up."**

```
fix_complexity         → ranks the worst methods, previews their diffs, reports before/after
fix_complexity         dryRun=false → writes them, then re-ranks what is left
```

**"Is this safe to ship?"**

```
find_security_issues
find_async_anti_patterns
inspect_code           with packs=all, minSeverity=warning
run_tests
```

## Related pages

- [Refactoring and style](refactoring.md) — how to change the code once you understand it
- [Tool catalogue: analysis](../tools.md#analysis) — every analysis tool at a glance
- [Limits](../reference/limits.md) — what static analysis cannot tell you

---

Next: [Refactoring and style](refactoring.md)
