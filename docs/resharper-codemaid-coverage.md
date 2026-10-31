# ReSharper & CodeMaid — Feature Coverage

Comparison of VisualMCP tools against ReSharper, Rider and CodeMaid.
Legend: ✅ implemented · ⚠️ partial · ❌ missing · 💀 not applicable to a headless MCP server

Last updated: 2026-10-31. This revision closes the last ReSharper/CodeMaid rows: `templates` (live,
postfix and file templates with a team store), `layout_code` (CodeMaid's comment reflow, line joining and
blank-line rules), named cleanup profiles in `code_cleanup`, the fuzzy fallback in `find_symbol`, ten more
`context_action` ids and the create-from-usage long tail. The rows that were stale (call hierarchy, go to
file, safe delete of files, test debugging/scaffolding, IL view, severity/suppressions/background
analysis, value tracking, stack traces, formatting settings) are corrected against the live inventory.

---

## ReSharper

### Navigation & Search

| Feature | Tool | Status |
|---|---|---|
| Find symbol by name (semantic) | `find_symbol` | ✅ exact / prefix / CamelHumps / contains, ranked, with a fuzzy-subsequence fallback when nothing matches |
| Find all references | `find_references` | ✅ with a kind filter |
| Call hierarchy | `find_callers`, `call_hierarchy` | ✅ one-level callers, and the bidirectional tree with a depth |
| Implementations / derived types | `find_implementations`, `find_derived_types` | ✅ |
| Symbol info / type members | `get_symbol_info`, `get_type_members` | ✅ |
| Go to declaration, with the body | `read_symbol` | ✅ |
| Full-text search + regex | `search_text`, `text_index` (FTS5) | ✅ |
| **Structural search & replace** | `structural_search` | ✅ AST pattern with `$holes$`, compiler-gated rewrite |
| Search by semantic query ("methods that throw X") | `structural_search` | ⚠️ shape only, no data-flow predicate |
| Go to file by fuzzy name / Search Everywhere / MRU | `find_file` | ✅ fuzzy and CamelHumps file-name search; the MRU list and the popup are the client's |

### Refactorings

| Feature | Tool | Status |
|---|---|---|
| Rename (preview + apply) | `preview_rename`, `apply_rename` | ✅ |
| Extract method / interface / class / superclass | `extract_method`, `extract_interface`, `extract_class`, `extract_superclass` | ✅ |
| Extract variable / field / parameter / part of string | `introduce_variable`, `introduce_field`, `introduce_parameter`, `introduce_part_of_string` | ✅ |
| Inline method / variable / parameter / class / field | `inline_symbol`, `inline_parameter`, `inline_class`, `inline_field` | ✅ |
| Change signature (reorder/add/remove/transform) | `change_signature`, `add_params`, `remove_params`, `transform_parameters` | ✅ `transform_parameters` wraps parameters in a parameter object, makes them optional or required, retypes them, or replaces one with a property |
| Move type / namespace / folder / static members / instance method / into another type | `move_type`, `move_to_namespace`, `move_to_folder`, `move_static_members`, `move_instance_method`, `move_type_to_outer_scope`, `move_type_to_nested_scope` | ✅ |
| Safe delete, copy type, use base type | `safe_delete`, `safe_delete_file`, `safe_delete_folder`, `copy_type`, `use_base_type` | ✅ symbols, files and folders — each refused while a reference exists |
| Pull members up / push members down | `pull_members_up`, `push_members_down` | ✅ |
| Encapsulate field, property ↔ method, indexer ↔ method | `encapsulate_field`, `property_to_auto`, `property_to_function`, `function_to_property`, `function_to_indexer`, `indexer_to_function` | ✅ |
| Constructor → factory, to primary constructor, to record | `constructor_to_factory`, `convert_to_primary_constructor`, `convert_to_record` | ✅ |
| Anonymous type → named type | `convert_anonymous_to_type` | ✅ |
| Make static / non-static | `make_static`, `make_non_static` | ✅ call sites routed through an instance already in scope; refused when one is not |
| Generify (method or type) | `generify` | ✅ call sites and references updated; constraints passed in |
| Static → extension method / back | `static_to_extension`, `extension_to_static` | ✅ |
| Invert if | `invert_if` | ✅ |
| Invert Boolean | `invert_boolean` | ✅ |
| Global usings / non-global usings / implicit usings | `global_usings`, `convert_to_non_global_using`, `implicit_usings` | ✅ |
| Convert abstract class ↔ interface | `convert_type_kind` | ✅ both directions, implementers rewritten with it, compiler-gated |
| Members to partial | `type_to_partial`, `extract_members_to_partial` | ✅ makes the type partial, or moves the selected members into a new partial file |
| Change type / change nullability / rename tuple component | `change_type`, `change_nullability`, `rename_tuple_component` | ✅ |
| Structural search & replace as a refactoring | `structural_search` | ✅ |

All ~51 refactorings of Rider's "main set" are implemented; the table above is the tool for each one.
ReSharper's **context actions** (1000+) are covered only in part: the style rewrites are
`simplify_code` (97 IDE ids) and `simplify_vb_code` (15 VB ids), the fixes come from `apply_code_fix`,
and 36 context-action ids are applied directly by `context_action` — the guards, the wrap actions, the
statement and declaration shapes and the string conversions named in its table below.

### Code generation / "create" family

| Feature | Tool | Status |
|---|---|---|
| Implement missing interface/abstract members | `implement_members` | ✅ compiler-gated |
| Create member/type from usage | `create_from_usage` | ✅ CS1061, CS0117, CS1501, CS7036, CS0246, CS0103 — the method, static member, overload or type |
| Context actions outside the style/refactoring families | `context_action` | ✅ 36 action ids: import, eight argument guards, five wrap actions, the statement/declaration family, nullable migration and the string re-encodings — see [the 307-action census](resharper-context-actions.md) |
| Generate XML documentation | `generate_docs` | ✅ summary/param/typeparam/returns |
| Generate constructor / Equals / GetHashCode / ToString | `generate_members` | ✅ |
| Implement INotifyPropertyChanged | `implement_inpc` | ✅ |
| **Live / postfix / file templates** | `templates` | ✅ 18 live, 10 postfix (`.if`, `.null`, `.var`, `.await`, …) and 8 file templates, plus a team store in `.visualMCP/templates.json`; compiler-gated |
| Generate a test class from a type | `generate_tests` | ✅ xUnit, one `[Fact]` per public method, compiler-gated |
| Generate a DTO from JSON / a fluent builder | `generate_dto_from_json`, `generate_builder` | ✅ |
| Create class/member from usage (the long tail) | `create_from_usage` | ⚠️ the six compiler-error shapes above; other errors need a different tool |

### Formatting & cleanup

| Feature | Tool | Status |
|---|---|---|
| Reformat code | `format_code` | ✅ document/project/solution, `.editorconfig` aware |
| Code cleanup (all steps in one call) | `code_cleanup` | ✅ style → usings → regions → reorder → format |
| **Cleanup profiles** | `code_cleanup` `profile=` | ✅ built-in `full` / `reformat` / `style` / `usings` / `regions` / `reorder` / `safe`, team profiles in `.visualMCP/cleanup-profiles.json`, `saveProfileAs` writes one |
| Optimize / sort usings | `optimize_usings` | ✅ |
| Reorder members | `reorder_members` | ✅ |
| Remove regions | `remove_regions` | ✅ |
| On-save cleanup | — | 💀 the client triggers `code_cleanup` on its own save event |
| Formatting settings management, `.editorconfig` generation | `configure_formatting` | ✅ authors `csharp_*`/`dotnet_*`/indent/line-ending/charset/file-header options; severities with `configure_inspection` |

### Local history

| Feature | Tool | Status |
|---|---|---|
| Per-file snapshots independent of commits | `local_history` | ✅ snapshot/history/diff/restore in the solution store |
| Undo of a write this server made | `edit_history`, `revert_edit` | ✅ |

### Analysis & inspections

| Feature | Tool | Status |
|---|---|---|
| Compiler errors & warnings | `get_diagnostics` | ✅ |
| Inspections from the shipped packs | `inspect_code` | ✅ ~800 ids from 5 packs |
| Apply an inspection's own code fix | `apply_code_fix` | ✅ dry run, FixAll by scope |
| **Baseline: only the findings a change introduced** | `inspection_baseline` | ✅ |
| ReSharper's proprietary inspection set (~2500) | `resharper_inspection_map` | ⚠️ overlap only: 8 ids are implemented natively under ReSharper's names in the opt-in `rider` pack, ~30 map to an existing capability, and the map says which |
| Severity configuration / suppressions / `.DotSettings` | `configure_inspection`, `suppress_finding`, `read_dot_settings` | ✅ severity per solution/project/file, a `#pragma` or `SuppressMessage` suppression, and reading the team's `.DotSettings` layers (its ids are reported, not applied to Roslyn) |
| Solution-wide background analysis | `background_analysis`, `analysis_findings` | ✅ a warm loop re-runs the packs when the solution changes; the findings are served from its cache |
| Dead code, unused symbols | `find_dead_code`, `find_unused_symbols` | ✅ |
| Code smells, async anti-patterns | `find_code_smells`, `find_async_anti_patterns` | ✅ |
| Complexity (cyclomatic, cognitive, Halstead, Big-O) | `complexity_report`, `get_complexity_hotspots`, `analyze_complexity`, `get_metrics` | ✅ |
| Reduce complexity (applies refactorings) | `reduce_complexity` | ✅ |
| Fix complexity across the solution in one call (rank + reduce + report) | `fix_complexity` | ✅ |
| Duplicate code (clone detection) | `find_duplicate_code` | ✅ |
| Security rules | `find_security_issues` | ⚠️ pattern-based, no taint analysis |
| DI analysis | `analyze_dependency_injection` | ✅ |
| Project/namespace dependencies, cycles, coupling | `analyze_dependencies`, `analyze_circular_namespace_dependencies`, `analyze_namespace_coupling` | ✅ |
| **Layer rules (a dependency pointing up)** | `check_layer_violations` | ✅ |
| **Dependency graph as a diagram** | `export_dependency_graph` | ✅ DOT/Mermaid |
| **Cohesion (LCOM4)** | `get_cohesion_metrics` | ✅ |
| **Test quality** | `analyze_test_quality` | ✅ |
| **Missing validation (parameters and DTOs)** | `find_missing_validation` | ✅ unguarded public parameters, plus DTO-shaped public classes/records whose properties carry no `System.ComponentModel.DataAnnotations` validation |
| **Nullability posture** | `analyze_nullability` | ✅ |
| **Breaking changes in the public API** | `find_breaking_changes` | ✅ vs a git ref |
| **Quality trend between two revisions** | `get_quality_trend` | ✅ the same syntactic rule set on both sides (average complexity, methods over 10/20, long methods, TODOs), with a verdict and the files that moved most; `to=worktree` measures the uncommitted tree |
| Value tracking / data-flow origin-destination | `track_value` | ✅ definitions (writes, including the declaration) and uses (reads), each with file:line and the member it happens in |
| Whole-solution report | `solution_audit` | ✅ |
| Change plan for a feature | `plan_feature_change` | ✅ |
| Analyze a stack trace | `analyze_stack_trace` | ✅ frames resolved to file:line, grouped by file |

### Testing

| Feature | Tool | Status |
|---|---|---|
| Run tests, structured results | `run_tests` | ✅ `dotnet test`, project filter, raw args |
| Per-class/per-method coverage | `get_test_coverage_map` | ✅ coverlet, branch coverage included |
| Run one test / rerun failed | `run_test`, `rerun_failed` | ✅ TRX result, and exactly the failures of the last run |
| Debug a test / continuous testing / covering tests | `debug_test`, `continuous_testing`, `tests_covering_symbol` | ✅ the test host under the DAP debugger, run-on-save, and the test methods that reference a symbol |
| Test scaffolding | `generate_tests` | ✅ xUnit, compiler-gated |
| Mutation testing, flaky detection, affected tests | `run_mutation_tests`, `find_flaky_tests`, `run_affected_tests` | ✅ Stryker score and survivors, outcome changes between runs, and the tests a diff affects |

### Decompilation & profiling

| Feature | Tool | Status |
|---|---|---|
| Inspect/decompile an assembly or a type | `inspect_assembly`, `decompile_type`, `decompile_assembly` | ✅ |
| NuGet package API / type source | `inspect_package`, `decompile_package_type` | ✅ |
| Pseudocode view (metacode) | `metacode_translate`, `decompile_to_metacode`, `metacode_compare` | ✅ |
| Native binary → pseudocode | `analyze_native_binary` (Ghidra) | ✅ |
| Navigate inside decompiled code | `assembly_workspace` | ✅ search, source, references and outline over a Roslyn workspace keyed on the MVID; rename is not possible (there is no source to write) |
| IL viewer | `disassemble` | ✅ the IL of one type or method, with tokens resolved against the metadata |
| CPU/memory/I-O diagnosis | `profile_*` (20+ tools) | ✅ |
| Interactive timeline, object-graph explorer | `profile_*` | 💀 the data is a tool result; the client draws the timeline and the graph |

---

## CodeMaid

| Feature | Tool | Status |
|---|---|---|
| Remove unused / sort usings | `optimize_usings` | ✅ |
| Reorder members | `reorder_members` | ✅ |
| Remove regions | `remove_regions` | ✅ |
| Format document | `format_code` | ✅ |
| Full cleanup pass | `code_cleanup` | ✅ with named profiles |
| Comment wrapping, join lines, blank-line rules | `layout_code` | ✅ reflow full-line `//` comments to a width, join a line range (refused when the tokens would change), collapse runs of blank lines and trim them at the braces |
| Code outline / Spade view | `get_type_members`, `read_symbol` | 💀 the data is a tool result; the tree is the client's |

---

## Remaining gaps

The full inventory — every missing capability with what exists instead, the workaround and the reason
it matters — is [What is missing — the gap inventory](gaps.md). This section is the index:

1. **Language and platform reach** — the semantic engine is Roslyn over **C#** and **Visual Basic**;
   **XAML** has a markup layer with the code-behind analysed by Roslyn, **Razor/Blazor** a markup
   scanner, and the non-.NET toolchains are wrapped rather than reimplemented (the Node toolchain,
   Kubernetes/Helm, Docker, a SQL pattern scanner, `ef_migrations`). What stays outside is a semantic
   model for **F#**, for the **JavaScript/TypeScript source itself**, for HTML/CSS and for YAML: those
   files are findable, not understood.
2. **The rest of the context-action family** — `context_action` implements 36 of the 307 context
   actions in the census (the guards, the wrap actions, the statement and declaration shapes, nullable
   migration, the string conversions), and the template family is a tool of its own. The long tail that
   remains is the proprietary one: the LINQ query↔method translation, StringBuilder rewriting, C# 14
   extension blocks, pattern merging beyond sequential checks, and actions that need a data-flow
   predicate rather than a shape.
3. **Inspection administration** — closed: severity per scope, suppressions, custom rules, the
   `.DotSettings` layers, naming rules and a warm background loop all exist. What remains is
   ReSharper's own ~2500-id catalogue, which is a closed-source reimplementation and not a tool gap.
4. **Data-flow analysis** — `find_security_issues` is pattern-based; taint tracking and a
   "methods that throw X" semantic query need a data-flow engine this product does not carry.

Rows that used to be here and closed on 2026-10-31, each verified against the live tool inventory:
the **debugger** (16 `debug_*` tools over netcoredbg), the **test workflow** (`run_test`,
`continuous_testing`, `tests_covering_symbol`, `generate_tests`, `run_mutation_tests`, `debug_test`),
**decompiled code as a workspace** (`assembly_workspace`), the **diagram and exploration** tools
(`call_graph`, `sequence_diagram`, `control_flow_graph`, `find_todos`, `find_endpoints`, `find_file`,
`solution_map`), the **smaller refactorings** (`transform_parameters`, `make_non_static`,
`move_type_to_nested_scope`, `generify`, `remove_params`, `extension_to_static`, `invert_boolean`,
`inline_field`, `extract_members_to_partial`), and the last ReSharper/CodeMaid rows: **templates**
(`templates`), **cleanup profiles** (`code_cleanup profile=`), **CodeMaid's layout rules**
(`layout_code`), **fuzzy symbol search** (`find_symbol`), ten more **context actions** (merge/split
conditions, a null check joined with its use, `var` and back, split declaration, remove braces, nullable
migration, digit separators, `ConfigureAwait(false)`) and the **create-from-usage long tail**
(CS0117, CS1501, CS7036 next to CS1061/CS0246/CS0103).

The VCS row that used to be here closed on 2026-10-31: `git_conflicts` is the 3-way merge assistant
(the hunks as data, resolved per side and staged), `git_rebase action=plan` is the interactive rebase
without an editor, `git_patch` is shelving as named patches, and `git_host` covers GitLab, Azure DevOps
and Bitbucket next to the GitHub `gh_*` tools. Real-time co-editing remains out of scope by design
(a shared transport, not a tool).

UI-bound features (editor, completion popups, tool windows, on-save cleanup, designers) are deliberately
out of scope for a headless MCP server and are listed as such in
[gaps.md](gaps.md#11-ui-bound-features--not-applicable-by-design).
