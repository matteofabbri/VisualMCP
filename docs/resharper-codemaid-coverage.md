# ReSharper & CodeMaid — Feature Coverage

Comparison of VisualMCP tools against ReSharper, Rider and CodeMaid.
Legend: ✅ implemented · ⚠️ partial · ❌ missing · 💀 not applicable to a headless MCP server

Last updated: 2026-10-30 (after structural search, cleanup/format, the create family, the inspection
baseline and the seven architecture/quality analyses).

---

## ReSharper

### Navigation & Search

| Feature | Tool | Status |
|---|---|---|
| Find symbol by name (semantic) | `find_symbol` | ✅ exact/contains — no fuzzy ranking |
| Find all references | `find_references` | ✅ with a kind filter |
| Call hierarchy | `find_callers` | ⚠️ direct callers, not a bidirectional tree |
| Implementations / derived types | `find_implementations`, `find_derived_types` | ✅ |
| Symbol info / type members | `get_symbol_info`, `get_type_members` | ✅ |
| Go to declaration, with the body | `read_symbol` | ✅ |
| Full-text search + regex | `search_text`, `text_index` (FTS5) | ✅ |
| **Structural search & replace** | `structural_search` | ✅ AST pattern with `$holes$`, compiler-gated rewrite |
| Search by semantic query ("methods that throw X") | `structural_search` | ⚠️ shape only, no data-flow predicate |
| Go to file by fuzzy name / Search Everywhere / MRU | — | 💀 |

### Refactorings

| Feature | Tool | Status |
|---|---|---|
| Rename (preview + apply) | `preview_rename`, `apply_rename` | ✅ |
| Extract method / interface / class / superclass | `extract_method`, `extract_interface`, `extract_class`, `extract_superclass` | ✅ |
| Extract variable / field / parameter / part of string | `introduce_variable`, `introduce_field`, `introduce_parameter`, `introduce_part_of_string` | ✅ |
| Inline method / variable / parameter / class | `inline_symbol`, `inline_parameter`, `inline_class` | ⚠️ no field inline |
| Change signature (reorder/add/remove) | `change_signature`, `add_params` | ⚠️ no `params` removal, no Transform Parameters |
| Move type / namespace / folder / static members / instance method | `move_type`, `move_to_namespace`, `move_to_folder`, `move_static_members`, `move_instance_method`, `move_type_to_outer_scope` | ✅ |
| Safe delete, copy type, use base type | `safe_delete`, `copy_type`, `use_base_type` | ⚠️ symbols only, not files/folders |
| Pull members up / push members down | `pull_members_up`, `push_members_down` | ✅ |
| Encapsulate field, property ↔ method, indexer ↔ method | `encapsulate_field`, `property_to_auto`, `property_to_function`, `function_to_property`, `function_to_indexer`, `indexer_to_function` | ✅ |
| Constructor → factory, to primary constructor, to record | `constructor_to_factory`, `convert_to_primary_constructor`, `convert_to_record` | ✅ |
| Anonymous type → named type | `convert_anonymous_to_type` | ✅ |
| Make static | `make_static` | ⚠️ no "make non-static" |
| Static → extension method | `static_to_extension` | ⚠️ no reverse |
| Invert if | `invert_if` | ✅ |
| Invert Boolean | — | ❌ |
| Global usings / non-global usings / implicit usings | `global_usings`, `convert_to_non_global_using`, `implicit_usings` | ✅ |
| Convert abstract class ↔ interface | — | ❌ |
| Members to partial | `type_to_partial` | ⚠️ makes the type partial, does not move members |
| Change type / change nullability / rename tuple component | `change_type`, `change_nullability`, `rename_tuple_component` | ✅ |
| Generify | — | ❌ |
| Structural search & replace as a refactoring | `structural_search` | ✅ |

Of Rider's "main set" (~46 refactorings) about 38 are implemented; the missing ones are listed above.
ReSharper's **context actions** (1000+) are covered only in part: the style rewrites are
`simplify_code` (97 IDE ids) and `simplify_vb_code` (15 VB ids), the fixes come from `apply_code_fix`.

### Code generation / "create" family

| Feature | Tool | Status |
|---|---|---|
| Implement missing interface/abstract members | `implement_members` | ✅ compiler-gated |
| Create member/type from usage | `create_from_usage` | ✅ CS1061/CS0246/CS0103 |
| Context actions outside the style/refactoring families | `context_action` | ✅ 13 action ids (14 ReSharper rows): `import_type`, five argument guards, four wrap actions, three string re-encodings — see [the 307-action census](resharper-context-actions.md) |
| Generate XML documentation | `generate_docs` | ✅ summary/param/typeparam/returns |
| Generate constructor / Equals / GetHashCode / ToString | `generate_members` | ✅ |
| Implement INotifyPropertyChanged | `implement_inpc` | ✅ |
| Live / postfix / file templates | — | ❌ |
| Generate a test class from a type | — | ❌ |
| Create class/member from usage (the long tail) | `create_from_usage` | ⚠️ |

### Formatting & cleanup

| Feature | Tool | Status |
|---|---|---|
| Reformat code | `format_code` | ✅ document/project/solution, `.editorconfig` aware |
| Code cleanup (all steps in one call) | `code_cleanup` | ✅ style → usings → regions → reorder → format |
| Optimize / sort usings | `optimize_usings` | ✅ |
| Reorder members | `reorder_members` | ✅ |
| Remove regions | `remove_regions` | ✅ |
| Cleanup profiles / on-save | — | ❌ (cleanup takes a step list instead) |
| Formatting settings management, `.editorconfig` generation | — | ❌ |

### Analysis & inspections

| Feature | Tool | Status |
|---|---|---|
| Compiler errors & warnings | `get_diagnostics` | ✅ |
| Inspections from the shipped packs | `inspect_code` | ✅ ~800 ids from 5 packs |
| Apply an inspection's own code fix | `apply_code_fix` | ✅ dry run, FixAll by scope |
| **Baseline: only the findings a change introduced** | `inspection_baseline` | ✅ |
| ReSharper's proprietary inspection set (~2500) | — | ⚠️ overlap only |
| Severity configuration / suppressions / `.DotSettings` | — | ❌ |
| Solution-wide background analysis | — | ❌ (on demand, no daemon) |
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
| **Missing argument validation** | `find_missing_validation` | ✅ |
| **Nullability posture** | `analyze_nullability` | ✅ |
| **Breaking changes in the public API** | `find_breaking_changes` | ✅ vs a git ref |
| Value tracking / data-flow origin-destination | — | ❌ |
| Whole-solution report | `solution_audit` | ✅ |
| Change plan for a feature | `plan_feature_change` | ✅ |
| Analyze a stack trace | — | ❌ |

### Testing

| Feature | Tool | Status |
|---|---|---|
| Run tests, structured results | `run_tests` | ✅ `dotnet test`, project filter, raw args |
| Per-class/per-method coverage | `get_test_coverage_map` | ✅ coverlet |
| Run one test / rerun failed | `run_tests` with `--filter` via extraArgs | ⚠️ |
| Debug a test, continuous testing, covering tests | — | ❌ no debugger |
| Test scaffolding | — | ❌ |

### Decompilation & profiling

| Feature | Tool | Status |
|---|---|---|
| Inspect/decompile an assembly or a type | `inspect_assembly`, `decompile_type`, `decompile_assembly` | ✅ |
| NuGet package API / type source | `inspect_package`, `decompile_package_type` | ✅ |
| Pseudocode view (metacode) | `metacode_translate`, `decompile_to_metacode`, `metacode_compare` | ✅ |
| Native binary → pseudocode | `analyze_native_binary` (Ghidra) | ✅ |
| Navigate/rename inside decompiled code | — | ❌ text only |
| IL viewer | — | ❌ |
| CPU/memory/I-O diagnosis | `profile_*` (20+ tools) | ✅ |
| Interactive timeline, object-graph explorer | — | ❌ (tool output, not a UI) |

---

## CodeMaid

| Feature | Tool | Status |
|---|---|---|
| Remove unused / sort usings | `optimize_usings` | ✅ |
| Reorder members | `reorder_members` | ✅ |
| Remove regions | `remove_regions` | ✅ |
| Format document | `format_code` | ✅ |
| Full cleanup pass | `code_cleanup` | ✅ |
| Comment wrapping, join lines, blank-line rules | `format_code` | ⚠️ what the Roslyn formatter does |
| Code outline / Spade view | `get_type_members`, `read_symbol` | 💀 |

---

## Remaining gaps

The full inventory — every missing capability with what exists instead, the workaround and the reason
it matters — is [What is missing — the gap inventory](gaps.md). This section is the index:

1. **Debugger** — no breakpoints, stepping, watch or evaluate, and no test debugging. `profile_dump`,
   `profile_stacks` and `profile_diagnose` cover the post-mortem and snapshot side only.
2. **Language and platform reach** — the semantic engine is C#. **Visual Basic** has only
   `simplify_vb_code` (15 ids) plus the language-neutral build/test/diagnostic/text tools: no
   refactorings, no built-in inspections, no formatter, no symbol index, no metacode frontend. **F#** is
   text and build only. **XAML/WPF/WinUI/MAUI/Avalonia**, **JS/TS/HTML/CSS**, **SQL and EF Core
   migrations**, and **Kubernetes/Helm** are at most full-text indexed; nothing parses them.
   **Razor/Blazor** has a markup scanner, not a Razor compiler.
3. **The rest of the context-action family** — `context_action` covers import, the common argument
   guards, four wrap actions and string re-encoding (14 actions out of the 307 context actions in the
   census). The long tail — if→switch, pattern merging, LINQ shape, loop rewrites, interpolation
   conversions, nullable migration — and live/postfix/file templates are not there.
4. **Inspection administration** — severity per scope, suppressions, `.DotSettings` layers, custom
   inspections and a background daemon are absent; `inspection_baseline` is the CI substitute.
5. **Test workflow** — single-test debug, continuous testing, covering tests for a symbol, test
   scaffolding and mutation testing.
6. **Decompiled code as a workspace** — today it is text: no go-to-definition, rename or find usages
   inside a decompiled assembly, and no IL view.
7. **Diagrams and exploration** — `export_dependency_graph` emits DOT/Mermaid; there is no interactive
   type/project graph, sequence diagram, structure view, code vision or TODO explorer.
8. **VCS extras** — local history, a 3-way merge assistant, GitLab/Azure DevOps.
9. **Smaller refactorings** — Extract Members to Partial, Inline Field, Transform Parameters,
   Abstract Class ↔ Interface, Extension → static, Invert Boolean, Make non-static, Move Type into
   Another Type, Generify, `params` removal.

UI-bound features (editor, completion popups, tool windows, on-save cleanup, designers) are deliberately
out of scope for a headless MCP server and are listed as such in
[gaps.md](gaps.md#11-ui-bound-features--not-applicable-by-design).
