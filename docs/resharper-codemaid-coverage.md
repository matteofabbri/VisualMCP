# ReSharper & CodeMaid — Feature Coverage

Comparison of VisualMCP tools against ReSharper, Rider and CodeMaid.
Legend: ✅ implemented · ⚠️ partial · ❌ missing

Last updated: 2026-11-02 — **429 tools** in the running build; `capability_report` prints the live count.
This is the ✅ side of the comparison: each row names a capability that exists, the tool that provides it,
and the guarantee — what that tool proves and where it stops. A row that reads "scanned", "masked" or
"candidate" is exactly the boundary it sounds like, and the boundary is stated in that row.

**A limit is a row, never a paragraph.** Whatever is not covered lives in the cell it belongs to — a ⚠️
row in the feature table, a sentence in the "Where it stops" column — so a table that reads ✅ never has
an exception explained below it.

## Contents

[ReSharper](#resharper) · [CodeMaid](#codemaid) · [Other languages and file kinds](#other-languages-and-file-kinds) · [Cross-cutting boundaries](#cross-cutting-boundaries) · [Revision log](#revision-log)

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
| Full-text search + regex | `search_text`, `search_code`, `text_index` | ✅ raw matching lines, the same regex resolved to the declaration that contains each hit, and a persisted FTS5 index |
| **Structural search & replace** | `structural_search` | ✅ AST pattern with `$holes$`, compiler-gated rewrite, C# and Visual Basic |
| Search by semantic query ("methods that throw X") | `find_throw_sites` | ✅ the direct throwers of a type (or of everything derived from it), the callers that reach one with the shortest call path, and the methods that catch it as a boundary |
| Go to file by fuzzy name / Search Everywhere | `find_file` | ✅ fuzzy and CamelHumps file-name search |

### Refactorings

| Feature | Tool | Status |
|---|---|---|
| Rename (preview + apply) | `preview_rename`, `apply_rename`, `rename_namespace` | ✅ symbols across the solution, and a namespace's last segment with every declaration and using updated |
| Extract method / interface / class / superclass | `extract_method`, `extract_interface`, `extract_class`, `extract_superclass` | ✅ `extract_method` and `extract_interface` also run on a `.vb` document: the block becomes a private `Sub`/`Function`, and the interface is written in VB's vocabulary with the `Implements` clause on the type *and* on every member |
| Extract variable / field / parameter / part of string | `introduce_variable`, `introduce_field`, `introduce_parameter`, `introduce_part_of_string` | ✅ |
| Inline method / variable / parameter / class / field | `inline_symbol`, `inline_parameter`, `inline_class`, `inline_field` | ✅ |
| Change signature (reorder/add/remove/transform) | `change_signature`, `add_params`, `remove_params`, `transform_parameters` | ✅ `change_signature` and the `ParamArray` pair run on C# and Visual Basic; `transform_parameters` runs on Visual Basic for optional/required and retype, while wrap-in-a-parameter-object and replace-with-a-property are C# only |
| Move type / namespace / folder / static members / instance method / into another type | `move_type`, `move_to_namespace`, `move_to_folder`, `move_static_members`, `move_instance_method`, `move_type_to_outer_scope`, `move_type_to_nested_scope` | ✅ `move_type` and `move_to_folder` also run on `.vb`, carrying the file's `Imports` and the declared `Namespace` |
| Safe delete, copy type, use base type | `safe_delete`, `safe_delete_file`, `safe_delete_folder`, `copy_type`, `use_base_type` | ✅ symbols, files and folders — each refused while a reference exists |
| Pull members up / push members down | `pull_members_up`, `push_members_down` | ✅ |
| Encapsulate field, property ↔ method, indexer ↔ method | `encapsulate_field`, `property_to_auto`, `property_to_function`, `function_to_property`, `function_to_indexer`, `indexer_to_function` | ✅ `property_to_auto` and `function_to_property` also run on `.vb`, rewriting both call spellings (with and without parentheses) |
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
**Every one of them except three also runs on a `.vb` document**, each under the same compiler gate: the ones whose
operation is a declaration (`apply_rename`, `encapsulate_field`, `add_params`/`remove_params`,
`change_signature`, `change_type`, `use_base_type`, `type_to_partial`, `safe_delete`) and the ones
whose VB spelling is its own operation (`extract_interface`, `extract_method`, `extract_class`,
`extract_superclass`, `move_type`, `move_to_folder`, `move_to_namespace`, `move_instance_method`,
`move_static_members`, `move_type_to_nested_scope`, `move_type_to_outer_scope`, `property_to_auto`,
`function_to_property`, `property_to_function`, `inline_field`, `inline_symbol`, `inline_parameter`,
`inline_class`, `function_to_indexer`, `indexer_to_function`, `introduce_variable`, `introduce_field`,
`introduce_parameter`, `introduce_part_of_string`, `invert_boolean`, `copy_type`, `static_to_extension`,
`extension_to_static`, `convert_anonymous_to_type`, `implement_inpc`, `pull_members_up`,
`push_members_down`, `transform_parameters` (all five operations), `generify` and `global_usings`). What
stays C#-only is only what VB cannot express — a record, a primary constructor, a nullable reference type.
ReSharper's **context actions** (1000+) are covered only in part: the style rewrites are
`simplify_code` (97 IDE ids) and `simplify_vb_code` (41 VB ids), the fixes come from `apply_code_fix`,
and 54 context-action ids are applied directly by `context_action` — the guards, the wrap actions, the
statement and declaration shapes, the string conversions, LINQ in both directions, StringBuilder,
pattern merging and the extension block, named in its table below.

### Code generation / "create" family

| Feature | Tool | Status |
|---|---|---|
| Implement missing interface/abstract members | `implement_members` | ✅ compiler-gated |
| Create class/member from usage | `create_from_usage` | ✅ the six declaration shapes the compiler can ask for — CS1061 member, CS0117 static member, CS1501/CS7036 overload, CS1729 constructor, CS0246 type, CS0103 method — each built from the call site and compiler-gated |
| Context actions outside the style/refactoring families | `context_action` | ⚠️ 54 of the 307 actions in the census: import, eight argument guards, five wrap actions, the statement/declaration family, nullable migration, the string re-encodings, LINQ both ways, StringBuilder, pattern merging and the C# 14 extension block. **Visual Basic** implements the 33 ids that have a VB spelling and refuses the C#-syntax ones with the reason. What remains is the proprietary tail — actions whose condition is a type-system fact rather than a shape — and the IDE-only ones; see [the census](resharper-context-actions.md) |
| Generate XML documentation | `generate_docs` | ✅ summary/param/typeparam/returns |
| Generate constructor / Equals / GetHashCode / ToString | `generate_members` | ✅ |
| Implement INotifyPropertyChanged | `implement_inpc` | ✅ |
| **Live / postfix / file templates** | `templates` | ✅ 18 live, 10 postfix (`.if`, `.null`, `.var`, `.await`, …) and 8 file templates, plus a team store in `.visualMCP/templates.json`; compiler-gated |
| Generate a test class from a type | `generate_tests` | ✅ xUnit, one `[Fact]` per public method, compiler-gated |
| Generate a DTO from JSON / a fluent builder | `generate_dto_from_json`, `generate_builder` | ✅ |
| Create class/member from usage (the long tail) | `create_from_usage` | ✅ every compiler error whose fix is *declaring* something is covered (CS1061, CS0117, CS1501, CS7036, CS1729, CS0246, CS0103); errors whose fix is a different edit are answered with the list, not guessed at |

### Formatting & cleanup

| Feature | Tool | Status |
|---|---|---|
| Reformat code | `format_code` | ✅ document/project/solution, `.editorconfig` aware |
| Code cleanup (all steps in one call) | `code_cleanup` | ✅ style → usings → regions → reorder → format |
| **Cleanup profiles** | `code_cleanup` `profile=` | ✅ built-in `full` / `reformat` / `style` / `usings` / `regions` / `reorder` / `safe`, team profiles in `.visualMCP/cleanup-profiles.json`, `saveProfileAs` writes one |
| Optimize / sort usings | `optimize_usings` | ✅ |
| Reorder members | `reorder_members` | ✅ |
| Remove regions | `remove_regions` | ✅ |
| Formatting settings management, `.editorconfig` generation | `configure_formatting` | ✅ authors `csharp_*`/`dotnet_*`/indent/line-ending/charset/file-header options; severities with `configure_inspection` |

### Local history

| Feature | Tool | Status |
|---|---|---|
| Per-file snapshots independent of commits | `local_history` | ✅ snapshot/history/diff/restore in the solution store |
| Undo of a write this server made | `edit_history`, `revert_edit` | ✅ |

### Version control & collaboration

| Feature | Tool | Status |
|---|---|---|
| Working-tree state, diff and history | `git_status`, `git_diff`, `git_log`, `git_show`, `git_blame` | ✅ read-only; a diff of the working tree, the index or any ref/range, a per-file stat, a commit in full, and blame over a line range |
| Stage, commit, amend, squash | `git_stage`, `git_commit`, `git_amend`, `git_squash` | ✅ a commit can stage everything first, and `git_commit conventional=true` refuses a message that breaks Conventional Commits |
| Branches, tags, remotes, sync | `git_branch`, `git_create_branch`, `git_tag`, `git_remote`, `git_sync` | ✅ fetch/pull/push; a forced push uses `--force-with-lease` behind `confirm=true` |
| Merge, cherry-pick, revert, reset | `git_merge`, `git_cherry_pick`, `git_revert`, `git_reset` | ✅ the strategies of each operation, abort/skip/continue where git has them, and a hard reset behind `confirm=true` |
| **The conflict model** | `git_conflicts` | ✅ the `<<<<<<<` blocks parsed into hunks with all three sides (`ours`, `theirs`, the `\|\|\|\|\|\|\|` base with diff3), resolved per hunk or per file (`ours`/`theirs`/`both`/`base`/`custom`) and staged once no marker remains |
| Rebase, including an interactive plan | `git_rebase` | ✅ start/continue/abort/skip; `action=plan` takes a JSON todo list (`pick`/`reword`/`squash`/`fixup`/`drop` with messages) through `GIT_SEQUENCE_EDITOR` and never opens an editor |
| Shelves: named patches and the anonymous stash | `git_patch`, `git_stash` | ✅ a patch is created from the working tree, the index or any revision/range, listed, shown, applied with `--check`/`--3way`/reverse and deleted; the stash keeps the quick anonymous case |
| Worktree, bisect, submodule | `git_worktree`, `git_bisect`, `git_submodule` | ✅ linked working trees, a binary search that reports the first bad commit, and submodule status/update/init/sync/deinit |
| Commit convention as data | `commit_lint` | ✅ Conventional Commits linted from a message, one revision or a range — the parsed type/scope/description/breaking/footers plus every violation with its rule id, with no Node toolchain; merge and `git revert` messages exempt |
| GitHub | `gh_repo`, `gh_pr`, `gh_issue`, `gh_release`, `gh_actions`, `gh_secret`, `github` | ✅ repositories, pull requests through their whole lifecycle, issues, releases, Actions runs, repository secrets, plus the raw `gh` escape hatch |
| GitHub review with inline comments | `gh_pr action=review`/`review-comments`/`checks` | ✅ comments anchored to `path`/`line`/`side`, approve/request-changes/comment, the inline comments a PR already carries, and the check runs on its head |
| Branch protection and rulesets | `gh_branch_protection` | ✅ protection, required status checks and the repository rulesets that apply |
| GitLab, Azure DevOps, Bitbucket Cloud | `git_host` | ✅ merge/pull requests, comments, merge, `review-pr`, close/reopen/update, issues/work items and pipelines; provider and coordinates parsed from the remote, credentials from `GITLAB_TOKEN`/`AZURE_DEVOPS_EXT_PAT`/`BITBUCKET_*`; GitLab's missing request-changes vote is stated, not faked |
| The review brief for a diff | `code_review` | ✅ changed files by kind, the risk checklist, new TODO/FIXME markers with their line, the worst methods of each changed C# file, and optionally the inspection findings inside them |

### Analysis & inspections

| Feature | Tool | Status |
|---|---|---|
| Compiler errors & warnings | `get_diagnostics` | ✅ |
| Inspections from the shipped packs | `inspect_code` | ✅ ~800 ids from 5 packs |
| Apply an inspection's own code fix | `apply_code_fix` | ✅ dry run, FixAll by scope |
| **Baseline: only the findings a change introduced** | `inspection_baseline` | ✅ |
| ReSharper's inspection catalogue | `resharper_inspection_map` | ⚠️ the catalogue is **read, not ported**: 1,560 ids in Rider 2026.2.1 (910 of them applicable to C#), each with its language group, the analyzer that produces it and its tooltip description, read from `lib/ReSharperHost`. 19 are implemented natively under ReSharper's names in the opt-in `rider` pack (16 of them with a code fix), 26 more map to an existing capability here, 8 have none. The rest are closed-source analyzers over JetBrains' PSI — a reimplementation each, not a registration. **All 1,560 ids, one row each with its coverage, are [resharper_tocover.md](resharper_tocover.md)** — `outputPath` writes that page from the same read |
| Severity configuration / suppressions / `.DotSettings` | `configure_inspection`, `suppress_finding`, `read_dot_settings` | ✅ severity per solution/project/file, a `#pragma` or `SuppressMessage` suppression, and reading the team's `.DotSettings` layers (its ids are reported, not applied to Roslyn) |
| Solution-wide background analysis | `background_analysis`, `analysis_findings` | ✅ a warm loop re-runs the packs when the solution changes; the findings are served from its cache |
| Dead code, unused symbols | `find_dead_code`, `find_unused_symbols` | ✅ and "no reference" is not "dead": the members a framework calls by convention (`frameworkInvoked`) and the symbols a reflective lookup names (`reflectionInvoked`) are reported apart from `unused` |
| Code smells, async and performance anti-patterns | `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns` | ✅ the semantic shapes — blocking calls and `async void`, a concatenation, a LINQ materialiser or a collection allocated inside a loop |
| Complexity (cyclomatic, cognitive, Halstead, Big-O) | `complexity_report`, `get_complexity_hotspots`, `analyze_complexity`, `get_metrics` | ✅ |
| Reduce complexity (applies refactorings) | `reduce_complexity` | ✅ |
| Fix complexity across the solution in one call (rank + reduce + report) | `fix_complexity` | ✅ |
| Duplicate code (clone detection) | `find_duplicate_code` | ✅ |
| Security rules | `find_security_issues` | ✅ SQL injection, command injection, weak crypto, secrets, deserialization, SSRF, XXE, TLS validation, path traversal |
| DI analysis | `analyze_dependency_injection` | ✅ |
| Project/namespace dependencies, cycles, coupling | `analyze_dependencies`, `analyze_circular_namespace_dependencies`, `analyze_namespace_coupling` | ✅ |
| **Layer rules (a dependency pointing up)** | `check_layer_violations` | ✅ |
| **Dependency graph as a diagram** | `export_dependency_graph` | ✅ DOT/Mermaid, at project, namespace and type level |
| **Cohesion (LCOM4)** | `get_cohesion_metrics` | ✅ |
| **Test quality** | `analyze_test_quality` | ✅ |
| **Missing validation (parameters and DTOs)** | `find_missing_validation` | ✅ unguarded public parameters, plus DTO-shaped public classes/records whose properties carry no `System.ComponentModel.DataAnnotations` validation |
| **Nullability posture** | `analyze_nullability` | ✅ |
| **Breaking changes in the public API** | `find_breaking_changes` | ✅ vs a git ref |
| **Quality trend between two revisions** | `get_quality_trend` | ✅ the same syntactic rule set on both sides (average complexity, methods over 10/20, long methods, TODOs), with a verdict and the files that moved most; `to=worktree` measures the uncommitted tree |
| Value tracking / data-flow origin-destination | `track_value` | ✅ definitions (writes, including the declaration) and uses (reads), each with file:line and the member it happens in |
| **Taint: request data reaching a dangerous operation** | `trace_data_flow` | ⚠️ sources (query/form/header/cookie/body/route data, console and environment input, model-bound action parameters) to the SQL, process, file, redirect, XSS and SSRF sinks, with the CWE, the path of assignments and calls, and an explicit source/sink/sanitizer table. It follows locals, parameters and call arguments to a caller-set depth, plus a field keyed by its declaration **and** the receiver it is reached through — with two locals or two parameters, `a.Value` and `b.Value` are different entries, and a receiver that is `this`, another field or a property keeps its identity across methods. A database and a queue are not followed, and the sanitizer list — parsed numbers, GUIDs, `Path.GetFileName`, encoded strings — is what ends a flow. Candidates to check, not proofs |
| Reflection and framework entry points | `find_reflection_usage`, `framework_entry_points` | ✅ the names a reflective lookup asks for — a literal, `nameof`, `typeof` or any compile-time concatenation — and whether they resolve in the solution (by simple or metadata name), and the members a framework calls by convention: what a static call graph cannot see. `find_unused_symbols` applies both before calling anything unused |
| External API surface actually used | `find_external_usages` | ✅ which third-party assemblies the solution uses, with call sites and a per-assembly summary (version, path, distinct members, top types); the path is the handoff `decompile_type` / `assembly_workspace` need to read the internals |
| Churn × complexity | `git_churn_hotspots` | ✅ git history joined with the complexity of each file's worst method |
| Generated sources | `list_generated_sources` | ✅ the generators a project references and the syntax trees they produce (trees that exist in the compilation but not as documents), each tree's declarations searched by name with `symbol=`, and their code on request. `find_symbol` and `read_symbol` fall back to those trees, so a generated declaration is located and read like a source one |
| Whole-solution report | `solution_audit` | ✅ |
| Change plan for a feature | `plan_feature_change` | ✅ |
| Analyze a stack trace | `analyze_stack_trace` | ✅ frames resolved to file:line, grouped by file |

### Testing

| Feature | Tool | Status |
|---|---|---|
| Run tests, structured results | `run_tests` | ✅ `dotnet test` — the whole solution or the test projects whose name a `projectFilter` selects, raw args forwarded, TRX parsed into per-project results with the failing tests and their messages; the failures are recorded |
| Per-class/per-method coverage | `get_test_coverage_map` | ✅ coverlet (`coverlet.collector` in the test projects), line **and** branch rates per class and per method, for the named project or the whole solution |
| Run one test / rerun failed | `run_test`, `rerun_failed` | ✅ TRX result for a name, a class or a raw filter, and exactly the failures the last `run_test` or `run_tests` recorded — per project, cleared by a green run |
| Test discovery, with parameterized data | `list_tests` | ✅ class, method, framework, and the `[InlineData]`/`[TestCase]`/`[DataRow]` rows; `MemberData`/`TestCaseSource`/`DynamicData` named as dynamic |
| Debug a test / continuous testing / covering tests | `debug_test`, `continuous_testing`, `tests_covering_symbol` | ✅ the test host under the DAP debugger, run-on-save, and the test methods that reference a symbol |
| Test scaffolding | `generate_tests` | ✅ xUnit, compiler-gated |
| Mutation testing, flaky detection | `run_mutation_tests`, `find_flaky_tests` | ✅ Stryker score and survivors; outcome changes between runs, apart from the tests that never passed |
| Which tests a change affects | `test_impact_analysis`, `run_affected_tests` | ✅ git diff → the symbols the changed files declare → the test methods that reference them, and the same filter executed in one call |
| Behaviour preserved by a change | `verify_change` | ✅ the suite on the working tree and on the base revision in a detached worktree, diffed test by test: regressions, fixes, new tests, verdict |

### Debugger

A real Debug Adapter Protocol session over netcoredbg (installed on demand), with every request the
adapter implements exposed as a tool, plus two capabilities reached through Roslyn.

| Feature | Tool | Status |
|---|---|---|
| Start under the debugger / attach | `debug_launch`, `debug_attach` | ✅ a built assembly or a `.csproj` (the newest `bin/` assembly), arguments, environment, working directory, stop at entry; attach by process id |
| Line breakpoints | `debug_breakpoints` | ✅ per file, with a C# condition and a hit count (`>5`, `>=5`, `5`, `%3`) counted **by this server** because the adapter has none — an unsatisfied hit is resumed transparently and `hitConditionSkips` reports it; verified vs unverified with the adapter's message |
| Break on a method by name | `debug_function_breakpoints` | ✅ the DAP function-breakpoint forms (`Ns.Type.Method`, `Module.dll!Ns.Type.Method(int)`) with a condition; the whole set is replaced per call, and an unverified name binds when its module loads |
| **Break where a member is written (data breakpoint)** | `debug_data_breakpoints` | ✅ every write site in the solution's source, resolved by Roslyn: assignment, compound assignment, `++`/`--`, a `ref`/`out` argument and an initializer, each listed with its kind. A write from code outside the solution's source does not stop, and a local is refused |
| Step, continue, pause | `debug_pause`, `debug_continue`, `debug_step` | ✅ over/into/out, and every resume waits for the next stop or for the exit |
| Exceptions by type | `debug_exceptions` | ✅ named types evaluated by the server against the **hierarchy** resolved through the solution — a base class catches its derived types, a framework base included, and `!Type` excludes a branch; `all` / `user-unhandled` say when to break when no type is named, and with no setting to change the call returns the stopped exception — id, stage, type, message, details and inner exceptions. A stop on an exception carries that same block |
| Threads, stack, variables | `debug_threads`, `debug_stack`, `debug_variables` | ✅ file:line per frame, and Locals/Arguments/Autos with member expansion |
| Evaluate, and **write**, a value | `debug_evaluate` | ✅ the data tip of a watch, and `newValue` is Set Value: the expression is assigned in the running process and the answer shows what it holds afterwards |
| Console output | `debug_output` | ✅ stdout/stderr as the debuggee wrote them, plus the adapter's stderr when a launch failed |
| Restart with the same breakpoints, optionally rebuilt | `debug_restart` | ✅ the same program, arguments, environment, line/function breakpoints and exception settings again under a new session id; `rebuild=true` runs `dotnet build` on the project that produced the assembly, and a failed build leaves the session untouched |
| Edit-and-continue / hot reload | — | ⚠️ the adapter applies metadata deltas only over GDB/MI (`-apply-deltas`), and its DAP mode has no request for them. Substitutes: `debug_restart rebuild=true` and `debug_evaluate newValue` |

### Decompilation & profiling

| Feature | Tool | Status |
|---|---|---|
| Inspect/decompile an assembly or a type | `inspect_assembly`, `inspect_assemblies`, `decompile_type`, `decompile_types`, `decompile_assembly` | ✅ public API from metadata, or the C# from ILSpy — one type or a whole page of them |
| NuGet package API / type source | `inspect_package`, `inspect_packages`, `decompile_package_type`, `find_assembly_api` | ✅ the package's DLL located in the cache and its API read, without a path |
| Pseudocode view (metacode) | `metacode_translate`, `decompile_to_metacode`, `metacode_compare` | ✅ |
| Native binary → pseudocode | `analyze_native_binary` (Ghidra) | ✅ |
| Navigate inside decompiled code | `assembly_workspace` | ✅ search, source, references and outline over a Roslyn workspace keyed on the MVID; `action=il` returns the same symbol as IL; rename is not possible (there is no source to write), and debugging the decompiled code needs the debugger plus a source-mapping step |
| IL viewer | `disassemble` | ✅ the IL of one type or method, with tokens resolved against the metadata; `assembly_workspace action=il` links the decompiled view and the IL view of the same symbol |
| CPU/memory/I-O diagnosis | `profile_*` (27 tools) | ✅ sampling, allocation sites, GC, heap retention, thread-pool and lock analysis, async and database event analysis, instrumentation, plus the verdict tools (`profile_slowdown`, `profile_threadpool`, `profile_review_change`, `profile_root_cause`) |

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

---

## Other languages and file kinds

The engine's semantic model is Roslyn, over C# and Visual Basic. The other languages a repository
carries get a **scanned** layer — masked source, the rules that need no type information, an inventory
tool — which is the same contract SQL, XAML and Razor already had. Each row states what its layer proves
and, in the last column, exactly where it stops.

| Language / kind | Tool | What it proves | Where it stops |
|---|---|---|---|
| JavaScript, TypeScript, JSX, TSX | `js_files`, `analyze_js`, `js_symbols` | modules imported and exported, functions and classes, JSX presence, the specifiers resolved into a module graph with its import cycles (relative path, `tsconfig.json` `baseUrl`/`paths`, `node_modules`, with `externalImports` counted outside the scan); 18 rules over masked source — `eval`, `new Function`, `innerHTML`, `dangerouslySetInnerHTML`, `document.write`, loose equality, `var`, `console`, `debugger`, empty catch, `@ts-ignore`, `target="_blank"` without `rel`, JSX `<img>` without `alt`, `any` in TypeScript, `setTimeout("…")`, an unresolvable relative import, an import cycle among the scanned modules (JS0018, one per strongly connected group); **a symbol table** — the declarations a file makes (functions, class members, declarators incl. destructuring, parameters, TS interfaces/types/enums, import bindings) with line/column/container/exports, a name resolved to the declaration in the innermost enclosing scope, every reference inside the module and through the imports other modules take from it (the specifier resolved the same way), JSX component tags as references, and the declarations nothing reads | no type model: a member reached through an untyped object, a computed member name and `eval` are not resolved, a lexical `rename` exists (`js_symbols action=rename`, not type-aware) while the other refactorings are absent, JSX/TSX components, props/state and bundler configuration are not analysed, and a runtime alias neither `tsconfig.json` nor `node_modules` declares (a bundler plugin, an import map) or a package `exports` condition map is not followed. `node_typecheck` and `node_lint` run the project's own `tsc`/`eslint` |
| HTML, CSS, SCSS | `markup_files`, `analyze_markup` | tags read by a quote-aware scanner, with balance, duplicate ids, `alt`, `lang`, viewport, label binding, nested forms, deprecated elements, inline handlers, `target="_blank"`, empty href, `button` type, control names, `http://` subresources, an attribute written twice (HTML0016); for CSS `!important`, ID selectors, empty rules, `@import`, duplicate selectors, unbalanced braces, `javascript:` URLs, `expression()`; a selector resolves against the page's element index — compound parts with tag, classes, id, attribute conditions, the structural pseudo-classes and the `:nth-child()`/`:nth-last-child()`/`:nth-of-type()`/`:nth-last-of-type()` formulas, joined by descendant, child, adjacent-sibling and general-sibling combinators — a selector that matches nothing is CSS0009, a declaration overridden on every element it matches is CSS0010, a class used and defined in no sheet is HTML0015, the CSS rules and selectors also run on every `<style>` block, and an inline `style` attribute is checked for `!important`, `javascript:` and `expression()` | a structural scanner, not a renderer: a selector whose meaning only a browser can decide (`:hover`, `:checked`, `:has()`) is indexed but never reported, the nesting is the one the markup writes, CSS0010 resolves specificity and source order for a declaration overridden on every element it matches (equal specificity across different files is unknown), a CDN sheet or CSS built in JavaScript is outside the scan, and CSS isolation and formatting are not analysed |
| YAML | `yaml_files`, `analyze_yaml`, `k8s_validate` | documents and top-level keys, and for a Kubernetes manifest its `kind`/`apiVersion`/`metadata.name`; tabs in indentation, duplicate keys, trailing whitespace, YAML 1.1 booleans, a colon glued to its key, a value that parses as a nested mapping, unbalanced flow collections, an alias with no anchor; then the manifest shape — kind without apiVersion or the reverse, metadata with no name, a container with no image, two containers with the same name, a workflow job with neither runs-on nor uses, a compose service with neither image nor build, a workflow with jobs but no on trigger; and `k8s_validate` checks a manifest offline against a table of the built-in kinds (K8S001–K8S023: required fields, selector/template agreement, a pinned image, a DNS-1123 name, a duplicate resource, a cluster-scoped kind with a namespace, an empty ConfigMap/Secret, a dangling volume reference) | structural checks for a GitHub Actions workflow and a compose file, but no full schema for either; not a YAML parser: `k8s_validate` is a table of the built-in kinds rather than the OpenAPI schema, so a custom resource is reported as having no schema, and an indentation-aware line scan leaves nested-value typing and anchors beyond a missing definition unchecked. `k8s_validate server=true` asks a configured cluster with `kubectl apply --dry-run=server`, which applies the real OpenAPI schema and admission chain |
| F# | `fsharp_files`, `analyze_fsharp`, `fsharp_symbols` | modules, types, top-level functions and opens — the declaration inventory; tabs, `Option.get`, partial list access, `Unchecked.defaultof`/`unbox`, null comparison, `with _ ->`, `let mutable`, `printf`, `failwith`, a duplicated `open`; **a lexical symbol table** — modules, namespaces, types, union cases, record fields, `let` bindings and members with their positions, a name resolved to the declaration of the same name before it in its file and, across files, only through an `open`, with every reference and the declarations nothing reads | no semantic model — F# is not a Roslyn language — so no type checking, refactorings, inspections or complexity (a lexical `rename` exists in `fsharp_symbols`); the table is lexical (shadowing inside a match arm, a module abbreviation and an exported name the assembly compiles are outside it), and `build_project` returns only the compiler's own `FS####` diagnostics |
| SQL | `sql_files`, `analyze_sql` | statements, verbs and tables; the lexical, safety, security and performance rules SQL0001–SQL0012, and the DDL the scanned files carry (`CREATE TABLE`, `ALTER TABLE … ADD`) read into a schema model that checks a statement's table and column names (SQL0011/SQL0012, `schema=off` to disable) | not a dialect parser and never a live database: the schema is the one the scan reads, so a table created elsewhere is reported as undeclared, not as invalid; no execution, no query plan, no formatting, no generated SQL for a single query (use `profile_database` on a running process), no seeding or data operations, and SQL inside a C# string is a `trace_data_flow` sink rather than parsed text |
| Kubernetes and Helm | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs`, `k8s_kustomize`, `k8s_validate`, `k8s_port_forward`, `k8s_exec`, `k8s_apply`, `k8s_delete`, `k8s_rollout`, `k8s_helm` | the cluster as data — client/server versions and the context, resources reduced to name/ready/restarts, `describe`, logs, an offline kustomize render, a manifest validated against the built-in kinds (K8S001–K8S023), a port-forward session that starts, is inspected and stops, one confirmed command inside a pod, and apply/delete/rollout with `confirm=true` (apply defaults to a server-side dry run); helm releases, a chart rendered offline and `helm lint` | the wrapper, not the cluster: `k8s_validate server=true` asks the cluster with `kubectl apply --dry-run=server` (the authority for a CRD's OpenAPI schema and a webhook when a cluster answers), a port-forward lives in the server process and ends with it, and every write needs `confirm=true` |
| XAML (WPF, WinUI 3, MAUI, Avalonia, UWP) | `xaml_pages`, `analyze_xaml`, `xaml_bindings`, `xaml_tree`, `xaml_navigate`, `xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`, `format_xaml` | the project's dialect and target frameworks, the `x:Class` code-behind resolved through Roslyn, XAML0001–XAML0015, every binding with its data type and a `valid`/`missing-member`/`not-checked` status, the element tree, `x:Name`/resource/style/handler navigation, and verified edits | no compiled XAML: a binding whose data type none of `x:DataType`/`DataType`/`DataContext`/`d:DataContext` names is `not-checked`, a key from a referenced assembly's theme dictionary is invisible, and a template's content is checked only against the type the markup declares. The code-behind is fully C# |
| Docker and Compose | `docker_status`, `docker_ps`, `docker_images`, `docker_inspect`, `docker_logs`, `docker_stats`, `docker_run`, `docker_control`, `docker_rm`, `docker_pull`, `docker_image_rm`, `docker_networks`, `docker_volumes`, `docker_prune`, `docker_compose` | the local engine as data — containers, images, networks, volumes, logs, stats, inspect, run/start/stop/restart/pause/unpause/kill/rm, pull, prune, and compose up/down/ps/logs/restart/stop/start/pull/build | the wrapper, not the engine: no Dockerfile parsing or linting, no image layer/size or build-cache analysis, no registry login/tag/push, no image vulnerability scan, and `docker_compose` runs the client rather than modelling the stack |
| gRPC / Protocol Buffers | the generated C# is analysed like any C# | `.proto` files are not parsed: no service/message model, no breaking-change check, no code generation |
| SignalR | hub classes are C#; `signal_r_*` connects to a hub and exercises it | no hub-contract analysis: the method names, argument shapes and the client/server generator are not modelled |

### Project kinds

| Project kind | State |
|---|---|
| ASP.NET Core (MVC, Razor Pages, Minimal APIs, Web API) | ✅ C# at source level; Razor markup by scanner. No runtime request tracing (profiling is process-level) |
| WinForms | ⚠️ C# is analysed; `.Designer.cs` is generated code and several analyses skip it |
| WPF / WinUI 3 / MAUI / Avalonia / UWP | ⚠️ C# analysed; XAML by the markup layer above — no compiled XAML, and a binding type the markup does not name is not checked |
| gRPC | ⚠️ generated C# analysed; `.proto` files are not parsed |
| SignalR | ⚠️ hubs are C#; no hub-contract analysis |
| Unity / Godot / MAUI workloads | ⚠️ whatever MSBuild can restore is loaded; no specialist project support |
| Legacy non-SDK projects | ⚠️ loaded through MSBuild; `update_framework` leaves `net4x`/`netstandard` alone |
| Solution/project creation | ✅ `project_template`: `dotnet new list` as data, and `create` runs a template with a name, folder, framework and language |

## Cross-cutting boundaries

These are not a feature of any one language or tool: they are properties every result shares, plus the
safety facts of a process-level server, and the residual boundary of the families that have their own
section. Each is stated so a green run is read for what it proves; the debugger adapter's own surface is
in [Debugger](#debugger) and the decompiled-code boundary in
[Decompilation & profiling](#decompilation--profiling).

| Boundary | What it means |
|---|---|
| What a rewrite gate proves | The compiler gate proves that a change compiles, never that it is correct. Behaviour is measured by `verify_change`, which compares two runs test by test; code the suite does not cover comes back `unverified` instead of being called safe |
| What a scanner proves | `analyze_complexity`, the Razor rules and security scanning report patterns with their confidence: a green run means "no pattern matched", and `inspection_baseline` is what turns that into a statement about a change rather than about a moment |
| Source-generated code | A generator's tree belongs to the compilation, not to a project document. `list_generated_sources` names the generators and the trees (and with `symbol=` the declarations inside them), and `find_symbol`/`read_symbol` read those trees. Every document-based tool works on documents, so the editors and refactorings, `reference_index`, `inspect_code` and `text_index` do not see them; `search_text`/`search_code` reach an emitted `.g.cs` file on disk when the glob covers it — the file, not its symbols |
| Reflection and framework conventions | `find_unused_symbols` reports `frameworkInvoked` (classified by `framework_entry_points`) and `reflectionInvoked` (the symbols a reflective lookup names — a literal, `nameof`, `typeof` or a compile-time concatenation, resolved by simple or metadata name, the full list in `find_reflection_usage`) apart from `unused`. A name that is not a compile-time constant, such as `Type.GetType(assembledName)`, is not guessed |
| Referenced assemblies | `find_external_usages` reports the surface the solution uses, with call sites and the assembly's version and path; `decompile_type` and `assembly_workspace` read its internals from that path. What the used surface does not state is what the assembly does internally, and no call graph crosses that boundary |
| Razor and Blazor | A heuristic markup scanner (28 rules): markup inside `@{ … }`/`@code { … }` is C# and is not descended. The anti-forgery rule resolves the partials a form renders and searches their text, so a token built by a helper it does not name is a false positive to review and an unresolved partial counts as having no token. Duplicate ids are compared across the rendered composition — the page, the layout it declares or the one the nearest `_ViewStart.cshtml` selects, and the partials either renders. A rule whose answer depends on a runtime value stays silent, files over 2 MB and unreadable folders are reported as skipped, and `.cshtml`/`.razor` are not Roslyn documents: `razor_*` reads them from disk. Compiled Razor is not modelled: `@model` property/type validation, `@using` resolution, tag helpers and their required attributes, component parameters/cascading values/`@bind` compatibility/event callbacks, CSS isolation, JS interop and Razor refactorings are outside the scanner |
| Metacode readers | The pseudocode model is a syntax model, not a language service: no type checking, no symbol navigation, no editing. Python and C++ are read from AST dumps — `translate_python_file`/`translate_cpp_file` produce one when CPython's `ast` or `clang` is on the machine, and a dump you supply is taken as it is; Java and Rust have a source frontend, and Go has a label in the model only. A construct the model has no node for stays `RAW[lang] … // reason` with a diagnostic, so a translation can be incomplete and says where. Ghidra and JADX are external tools with a real runtime cost: downloaded on first use, a JDK, a timeout |
| Profiling | The target must expose a diagnostics IPC endpoint. Sampling is statistical, so short captures are noisy and the verdict tools refuse windows they cannot judge. Allocation sites need stack walks (`profile=gc`/`diagnose`). `file:line` needs PDBs (`profile_symbols` fetches them). ClrMD cannot attach to the server's own process. Inlining and async state machines hide user frames. `profile_async` and `profile_database` need their EventSource presets, and `profile_root_cause` needs PDBs plus a source file. Instrumentation writes a rewritten copy that must run beside the probe DLL. `profile_file_io` uses the Windows kernel ETW provider, so it is Windows-only and privileged. The native monitor answers bounded requests on loopback and is unauthenticated: do not expose its port |
| Automation and safety | `run_command` is not sandboxed and runs with the server's privileges; `run_plan` can call any tool and any shell command, and its validation prevents broken plans, not dangerous ones; `confirm=true` is a guardrail, not authorization. The edit history records what this server wrote, per workspace and per file, not what an IDE or a script wrote. The store is a cache: indexes are snapshots keyed on content hashes, so after large external changes rebuild them or reload the solution |

The families that have a residual boundary are stated here, next to the tools they qualify:

| Family | What is still outside |
|---|---|
| Taint analysis (`trace_data_flow`) | A receiver that is a property or a call is still an over-approximation (the same path is assumed to be the same object); a database and a queue are not followed; the sanitizer list is explicit, so a sanitizer it does not name is a false positive to review; the answer is candidates with their path, not proofs |
| Mutation testing (`run_mutation_tests`) | The runner is an external tool the user installs (Stryker.NET); the product drives it, it does not replace it |
| Database profiling, async/event-source viewers, instrumentation profiling | Process-level only: no request tracing and no distributed trace across services; `profile_root_cause` needs the PDB and the source on disk |
| Churn × complexity (`git_churn_hotspots`) | Static (model + history), not a runtime measurement |
| Context actions and generation | Contract annotations (`[NotNull]`/`[CanBeNull]` are the pre-nullable way of saying what `string?` says today, so generating them would write code the language obsoleted) and language injections (an embedded SQL/HTML/CSS fragment's editor service is a second parser platform, not a missing rewrite) are boundaries |
| Packaging and deployment | MSIX is not generated: it needs the Windows packaging tools, not the SDK. `installer_project` covers WiX and ClickOnce, `deploy_target` covers IIS and Azure Web Apps/Static Web Apps, and every deployment write needs `confirm=true` |
| SQL and EF Core | A query's own SQL is not generated (use `profile_database` on the running process for that); seeding and data operations are not modelled, and the schema is the one the scanned DDL declares, never a live catalog |

---

## Revision log

The batches that changed this page or the count it quotes, newest first. A count is the live inventory at
the end of the batch (`capability_report` prints the current one); the per-feature detail of each batch is
in [CAPABILITY_MAP.md](../CAPABILITY_MAP.md).

| Date | What changed | Tools |
|---|---|---|
| 2026-11-02 | **CI and compose structure**: `analyze_yaml` gains **YAML0013** (a GitHub Actions job with neither `runs-on` nor `uses`), **YAML0014** (a compose service with neither `image` nor `build`) and **YAML0015** (a workflow with jobs but no `on` trigger), read from the same indentation-aware walk; it is a structural check, not a schema | 429 (unchanged) |
| 2026-11-02 | **The gap inventory is retired**: every boundary it listed now lives in this page — the per-language rows, the project kinds, the cross-cutting table, the debugger and decompiler sections — and `docs/gaps.md` is an index that states there is no open gap row. The revision log below is the batch history | 429 (unchanged) |
| 2026-11-02 | **The scanned layers gain JS0018, HTML0016 and the `:nth-*` selectors**: `analyze_js` reports an import cycle as a finding (one per strongly connected group of scanned modules, at the import that reaches into it); `analyze_markup` matches `:nth-child()`/`:nth-last-child()`/`:nth-of-type()`/`:nth-last-of-type()` (`An+B`, `odd`, `even`) against the page index, so CSS0009/CSS0010 see them, and flags an attribute written twice on one element | 429 (unchanged) |
| 2026-11-01 | **A Cross-cutting boundaries section**: the properties every result shares (the gate, scanners, generated code, reflection, referenced assemblies, Razor, metacode readers, profiling, automation/safety) and the residual boundary of taint, mutation, database profiling and churn now live here, next to the features they qualify, instead of on the gap inventory | 420 (unchanged) |
| 2026-11-01 | **The VB refactoring catalogue is complete except the three constructs VB does not have**: `generify`, `global_usings` (a project-level `Import`) and `transform_parameters`' wrap/replace-with-property now run on a `.vb` document | 420 (unchanged) |
| 2026-11-01 | **CSS0010**: the CSS cascade is resolved for the dead-declaration case — every rule carries its declarations, specificity and source order, matched against each page's element index; a declaration overridden on every element it matches is reported | 420 (unchanged) |
| 2026-11-01 | **A lexical rename for the two scanned languages and a server-side manifest check**: `js_symbols action=rename` and `fsharp_symbols action=rename` rename one declaration and every reference the table resolved (for JavaScript the uses in the importing modules and the import binding itself; for F#, the uses before it in its file and in a file that opens the declaring module), previewed by default, with no compiler gate; `k8s_validate server=true` adds `kubectl apply --dry-run=server`, so a CRD's schema and an admission webhook are the cluster's answer rather than the built-in table's | 420 (unchanged) |
| 2026-11-01 | **The Visual Basic refactoring coverage grows**: thirteen paths that used to stop at a C# syntax tree now run on a `.vb` document — the `introduce_*` family, `invert_boolean`, `copy_type`, the `<Extension>` pair, `convert_anonymous_to_type`, `implement_inpc`, `pull_members_up`/`push_members_down`, and `transform_parameters` for make-optional/make-required/change-type. The C#-only remainder is now what VB cannot express (records, primary constructors, nullable reference types) plus `global_usings`, `generify` and the two call-site rewrites | 420 (unchanged) |
| 2026-11-01 | **The JS/TS and F# symbol tables, and Kubernetes closed at its own surface**: `js_symbols` (a tokeniser over the masked source, a scope per brace block, declarations and the declaration a name resolves to, references through the module's imports, JSX tags as references, `outline`/`find`/`refs`/`unused`); `fsharp_symbols` (the same four questions as a name table, a use resolving before it in its file and across files only through an `open`); `k8s_validate` (K8S001–K8S023 offline, with its own YAML path reader) and `k8s_port_forward` (a forward as a session: start/list/status/stop). The scanned rows for JS/TS, YAML and F# now say what the symbol tables prove and where the type model is still absent | 416 → 420 |
| 2026-11-01 | **The debugger, against the adapter's real surface**: per-exception-type filters (`filterOptions`) and the stopped exception's block (`exceptionInfo`), function breakpoints, Set Value through `debug_evaluate newValue`, hit conditions counted by the server, **data breakpoints as the solution's Roslyn-resolved write sites**, and `debug_restart` with an optional rebuild. Edit-and-continue/hot reload is a ⚠️ row: the adapter applies metadata deltas only over GDB/MI | 413 → 416 |
| 2026-10-31 | **The HTML/CSS depth**: `markup_files` indexes every page's tags/classes/ids and every sheet's simple selectors; `analyze_markup` gains CSS0009 (a simple selector that matches no element) and HTML0015 (a class used and defined in no scanned sheet), runs the CSS rules and selectors on every `<style>` block, and checks an inline `style` attribute for `!important`, `javascript:` and `expression()` | 404 (unchanged) |
| 2026-10-31 | **The depth of the scanned layers and the data-flow reach**: `analyze_yaml` gains YAML0009–YAML0012 (kind/apiVersion asymmetry, metadata without a name, a container with no image, duplicate container names), `analyze_js` resolves a relative import and reports JS0017, and `trace_data_flow` follows a value stored in a field of the same type across the method that writes it and the method that reads it. No new tool: the range of three existing ones | 401 (unchanged) |
| 2026-10-31 | **Version control and collaboration** stops being a row in `gaps.md` and becomes a feature section here: the 37 tools — 28 in the Git family (27 `git_*` and `commit_lint`), 8 GitHub and `code_review`. The section adds no tool, it moves ones already counted | 401 (unchanged) |
| 2026-10-31 | **The board becomes the source of truth**: `board_brief` (counts, the card to pick up, staleness) and `board_audit` (each open card cross-checked against the live tool and context-action catalogue); the rows on this page do not move, the inventory does | 399 → 401 |
| 2026-10-31 | **The last point of every open family**: the scanned layers (`js_files`/`analyze_js`, `markup_files`/`analyze_markup`, `yaml_files`/`analyze_yaml`, `fsharp_files`/`analyze_fsharp`), `trace_data_flow`, `find_throw_sites` (387 → 397), five context actions (`merge_patterns`, `concat_to_stringbuilder`, `linq_query_to_method`, `linq_method_to_query`, `convert_to_extension_block`) and CS1729 in `create_from_usage`; then `commit_lint` (→ 398) and `disk_usage` (→ 399) | 387 → 399 |
| 2026-10-31 | **The rows that used to close this page**: the debugger (16 `debug_*` tools over netcoredbg), the test workflow (`run_test`, `continuous_testing`, `tests_covering_symbol`, `generate_tests`, `run_mutation_tests`, `debug_test`), decompiled code as a workspace (`assembly_workspace`), the diagram and exploration tools (`call_graph`, `sequence_diagram`, `control_flow_graph`, `find_todos`, `find_endpoints`, `find_file`, `solution_map`), the smaller refactorings (`transform_parameters`, `make_non_static`, `move_type_to_nested_scope`, `generify`, `remove_params`, `extension_to_static`, `invert_boolean`, `inline_field`, `extract_members_to_partial`), and the last ReSharper/CodeMaid rows: templates (`templates`), cleanup profiles (`code_cleanup profile=`), CodeMaid's layout rules (`layout_code`), fuzzy symbol search (`find_symbol`), ten more context actions (merge/split conditions, a null check joined with its use, `var` and back, split declaration, remove braces, nullable migration, digit separators, `ConfigureAwait(false)`) and the create-from-usage long tail (CS0117, CS1501, CS7036 next to CS1061/CS0246/CS0103) | — |
