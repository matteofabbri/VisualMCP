# VisualMCP

VisualMCP gives a coding agent the IDE's own tools over MCP: Roslyn's semantic model, the real MSBuild
project graph, a debugger, a profiler and a decompiler, in one local process. It exists for a plain
reason: reading and changing C# through grep and file reads spends tokens on guesses, while ReSharper,
CodeMaid and Visual Studio answer the same questions in two clicks. This server puts those answers behind
a tool call.

**429 tools.** Of the codes it fixes automatically: **97 of the 126 Visual Studio code-style ids** (plus
**41 for Visual Basic**), **8 compiler diagnostics**, **19 ReSharper inspections** (16 with a code fix),
**~800 analyzer inspection ids** from five packs (each with its own fix where the analyzer ships one),
**54 context actions** and **60+ refactorings**. Every write is a Roslyn operation, compiler-gated,
recorded in the solution's edit history and revertible.

## Start in two minutes

```powershell
git clone https://github.com/matteofabbri/VisualMCP.git C:\REPOSITORY\VisualMCP
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope all
```

```sh
# Linux / macOS
git clone https://github.com/matteofabbri/VisualMCP.git ~/REPOSITORY/VisualMCP
~/REPOSITORY/VisualMCP/scripts/install.sh --scope all
```

Restart your client, open any folder with a `.sln`, and say:

> **Audit this solution and tell me where to start.**

## What it does

`solution_audit` reads the solution once and answers with the compiler's own diagnostics, the methods
ranked by cyclomatic and cognitive complexity, the project graph and the first move to make. Then the
work continues without a grep or a file read from you:

- `fix_complexity` lowers the worst methods — a nested block extracted into a private method, a tangled
  boolean named, a tail `if` flattened into a guard clause — re-measuring after each step and rolling
  back anything that would not compile. `reduce_complexity` does the same for one method.
- `simplify_code` applies the Visual Studio code-style fixes below to a document, a project or the whole
  solution in one call; `simplify_vb_code` does the same for Visual Basic.
- `fix_diagnostics` rewrites the mechanical compiler errors; `apply_code_fix` runs the analyzer's own fix
  for any inspection finding; `context_action` applies the ReSharper context actions.
- 60+ refactorings — rename, extract method/class/interface/superclass, change signature, move type,
  inline — update every call site and are refused the moment they would add a compiler error.

The answer to "what is the most complex code here, and can you fix it" is one call, not a session of
grep and reading. One call on our own codebase (a snapshot):

```text
Solution: VisualMCP.sln — 6 project(s), 1,099 file(s), 229,639 line(s)

Compiler: 0 error(s), 18 warning(s)
     3x CS8602     warning at StyleRewriteService.cs:492
     3x CS8604     warning at VbStyleRewrites.Parameters.cs:59
     2x CS8625     warning at VbStyleTests.cs:1022
     2x CS8603     warning at UseBaseTypeService.cs:52
     2x CS8601     warning at VbRefactoringTail.Moves.cs:147

Most complex methods (cyclomatic complexity, method length):
   89 complexity  323 lines  AnalyzeText(2 args)  (YamlAnalyzer.cs:68)
   83 complexity  342 lines  Scan(2 args)  (JavaScriptSymbols.cs:112)
   64 complexity  294 lines  AzureAsync(13 args)  (RemoteHostService.cs:369)
   63 complexity  272 lines  MoveInstanceMethod(7 args)  (MoveInstanceMethodService.cs:34)
   63 complexity  265 lines  MoveTypeToNestedScope(6 args)  (MoveTypeToNestedScopeService.cs:36)

Project graph:
  Snitch                           → (leaf)
  Snitch.Metacode                  → Snitch
  Snitch.Profiling                 → Snitch, Snitch.Profiling.Probe
  Snitch.Profiling.Probe           → (leaf)
  VisualMCP                        → Snitch, Snitch.Metacode, Snitch.Profiling, Snitch.Profiling.Probe
  VisualMCP.Tests                  → Snitch, Snitch.Metacode, Snitch.Profiling, Snitch.Profiling.Probe, VisualMCP

Suggested first moves:
  1. Extract from 'AnalyzeText(2 args)' (complexity 89) — extract_method on its worst block.
```

The same audit ranks every method (`complexity_report`), names the decision points that own each score,
and `git_churn_hotspots` joins git history with complexity to say which file a refactoring pays for
first.

## The web interface

Every running server also hosts a web view of itself and of the solution store it writes, at
**http://127.0.0.1:4677** — inside the MCP process, so there is nothing extra to launch. The store pages
are read-only `GET`s over SQLite and the live process. The **board page** is the one that edits: cards
are dragged between columns, and a card's panel changes its fields, comments it or deletes it, through
the same board service the `board_*` tools call — and only from a same-origin JSON POST the page itself
sends. A port that cannot be bound is a warning in the log rather than a failure of the MCP session.

| Page | What you see |
|---|---|
| `/` | process facts (pid, uptime, working set, managed heap, runtime, transport), the loaded solutions, the store's counts, the latest tool calls and edits |
| `/board` | the project's board: five columns, a card dragged to move it, and a detail panel to edit, comment on, move or delete it |
| `/tables` · `/t/{table}` | every table the store contains, discovered from `sqlite_master`: filter across every non-blob column, sort, page, and export CSV or JSON |
| `/schema` | the columns of every table as SQLite reports them |
| `/tools` | the live tool catalogue with every argument, its type and whether it is required |
| `/log` | the tail of `debug.log`, newest first, refreshed every 2.5 s, filtered by text or by kind |

A column fixed on the left of every page lists the most recent tool calls — each a link to that tool's
rows in the command log — with the process facts at its foot. The same facts answer as JSON at
`/api/health`, `/api/overview`, `/api/tables`, `/api/tables/{table}` and `/api/board`.

`VISUALMCP_DASHBOARD=off` turns it off, `VISUALMCP_DASHBOARD_PORT` changes the first port it tries
(4677, then a port kept by an instance that is no longer running, then an OS-assigned one), and
`VISUALMCP_DASHBOARD_HOST` changes the interface — loopback is the default because the dashboard has no
authentication. The full reference —
every route and query parameter, the store's tables one by one, the JSON answers, the response headers
and the limits — is [the web interface reference](docs/dashboard.md).

## The codes it fixes for you

### Visual Studio code style — `simplify_code` (C#, 97 ids)

Every id below is a rewrite with a compiler gate: the file is left untouched if the result would add an
error. `simplify_code rules=all` runs all of them; one id, a list or a family works too.

| Ids | What it rewrites |
|---|---|
| IDE0001, IDE0002, IDE0003 | simplify a name, a member access, or remove `this.` |
| IDE0004, IDE0049 | remove a redundant cast; use the C# keyword for a framework type |
| IDE0007, IDE0008 | `var` / the explicitly written type |
| IDE0010, IDE0072 | complete a `switch` statement / expression over an enum |
| IDE0011 | add braces to an embedded statement |
| IDE0016 | a null check + throw → a throw expression |
| IDE0017 | an object initializer |
| IDE0018 | inline the `out` variable |
| IDE0019, IDE0020, IDE0038 | `as` + null check, `is` + cast, `is` without a variable → pattern |
| IDE0021–IDE0027, IDE0053, IDE0061 | expression body: constructor, method, conversion operator, operator, property, indexer, accessor, lambda, local function |
| IDE0028, IDE0300, IDE0301, IDE0302 | collection expressions: `[]`, array, empty, `stackalloc` |
| IDE0029, IDE0030, IDE0031, IDE0074 | `??`, `??=`, `?.` |
| IDE0033 | an explicitly provided tuple element name |
| IDE0034 | `default(T)` → `default` |
| IDE0035 | unreachable code |
| IDE0036 | modifier order |
| IDE0037 | an inferred member name |
| IDE0039 | a lambda → a local function |
| IDE0040 | the accessibility modifier |
| IDE0041 | `x == null` → `x is null` |
| IDE0042 | a deconstructing declaration |
| IDE0044 | `readonly` on a field |
| IDE0045, IDE0046 | a conditional expression for an assignment / a return |
| IDE0047, IDE0048 | redundant parentheses / parentheses for clarity |
| IDE0051, IDE0052 | an unused private member / an unread private field |
| IDE0054, IDE0056, IDE0057 | compound assignment, index operator, range operator |
| IDE0055 | formatting, as the project's `.editorconfig` asks |
| IDE0058, IDE0059 | an unused expression value / a store overwritten before it is read |
| IDE0060 | an unused parameter and its argument at every call site |
| IDE0062, IDE0320 | a static local function / a static lambda |
| IDE0063 | a `using` block → a `using` declaration |
| IDE0066 | a switch statement → a switch expression |
| IDE0070, IDE0071 | `HashCode.Combine`; a simplified interpolation |
| IDE0073 | the file header from `file_header_template` |
| IDE0075, IDE0100 | `x ? true : false` → `x`; `x == true` → `x` |
| IDE0079 | an unnecessary suppression |
| IDE0080 | an unnecessary null-forgiving `!` |
| IDE0082, IDE0280, IDE0340 | `typeof(X).Name` and `ArgumentNullException("x")` → `nameof(...)`; `nameof(List<>)` |
| IDE0083 | `!(x is T)` → `x is not T` |
| IDE0090 | `new Foo()` → `new()` |
| IDE0120, IDE0121 | `Where(p).Any()` → `Any(p)`; `Where(o => o is T).Cast<T>()` → `OfType<T>()` |
| IDE0150 | `x is object` → `x != null` |
| IDE0160, IDE0161 | block-scoped / file-scoped namespace |
| IDE0170, IDE0260 | an extended property pattern; `(o as T)?.M == v` → `o is T { M: v }` |
| IDE0180 | a tuple swap |
| IDE0200, IDE0220, IDE0230 | an unnecessary lambda → method group; an explicit cast in `foreach`; a UTF-8 string literal |
| IDE0240, IDE0241 | a redundant / unnecessary `#nullable` directive |
| IDE0250, IDE0251 | `readonly struct` / `readonly` on a member |
| IDE0380 | an unnecessary `unsafe` |
| IDE0390, IDE0391 | make a method synchronous |
| IDE2000–IDE2006 | blank-line and layout rules |

Three more ids are answered by a dedicated tool instead of a rewrite: `IDE0005` by `optimize_usings`,
`IDE0032` by `property_to_auto`, `IDE0290` by `convert_to_primary_constructor`. The remaining 26 ids of
the table are not rewrites at all — a refactoring, a project-layout decision, a naming rule, a compiler
diagnostic or a construct VB defines instead — and each is named with its reason in
[the coverage page](docs/resharper-codemaid-coverage.md).

### ReSharper

- **19 inspections implemented natively under ReSharper's ids** (the opt-in `rider` pack in
  `inspect_code`): `ConditionIsAlwaysTrueOrFalse`, `ConvertToCompoundAssignment`,
  `ConvertToConstant.Local`, `EmptyConstructor`, `LoopVariableIsNeverChangedInsideLoop`,
  `MemberCanBeMadeStatic.Global`, `MergeIntoPattern`, `MethodOverloadWithOptionalParameter`,
  `RedundantArgument`, `RedundantBaseConstructorCall`, `RedundantCheckBeforeAssignment`,
  `RedundantIfElseBlock`, `RedundantLambdaSignatureParentheses`, `RedundantPatternParentheses`,
  `RedundantStringInterpolation`, `ReplaceSequenceEqualWithConstantPattern`,
  `ReplaceWithStringIsNullOrEmpty`, `StringLiteralAsInterpolationArgument`, `UseNameofExpression`.
  16 of them ship a code fix for `apply_code_fix`.
- **54 context actions** applied by `context_action`: type import; eight argument guards (null, all
  parameters, string not null/empty, collection, variable, dictionary key, enum value, index range);
  four wrap actions (try/catch, try/finally, if, using) plus `#region`; the statement and declaration
  shapes (braces, foreach↔for, if→switch, split/join/merge checks, remove argument name, `var`/explicit
  type, split declaration, invert loop, extension block in both directions); the string conversions (raw,
  verbatim, regular, concatenation↔interpolation, split/join literals, `''`↔`string.Empty`); digit
  separators; nullable migration; `ConfigureAwait(false)`; `StringBuilder`; pattern merging; LINQ in both
  directions; static member import/qualify/unqualify; a file header; a property over a backing field;
  block↔expression body. Visual Basic implements the 33 ids that have a VB spelling.
- **~800 inspection ids** from five analyzer packs — Roslynator (RCS), the .NET analyzers (CA),
  AsyncFixer (ASYNC), Meziantou (MA) and StyleCop (SA) — reported by `inspect_code`, fixed by
  `apply_code_fix` where the analyzer ships a `CodeFixProvider`, with `inspection_baseline` to show only
  what a change introduced.

### Visual Basic — `simplify_vb_code` (41 ids)

`IDE0001`–`IDE0004`, `IDE0009`, `IDE0010`, `IDE0017`, `IDE0029`–`IDE0033`, `IDE0035`, `IDE0036`,
`IDE0040`, `IDE0041`, `IDE0044`–`IDE0049`, `IDE0051`, `IDE0052`, `IDE0054`, `IDE0059`, `IDE0060`,
`IDE0070`, `IDE0071`, `IDE0073`, `IDE0075`, `IDE0081`, `IDE0082`, `IDE0084`, `IDE0100`, `IDE0120`,
`IDE0121`, `IDE0140`, `IDE0280`, `IDE2000`, `IDE2003`.

The declaration-shape refactorings (`apply_rename`, `change_signature`, `extract_method`,
`extract_interface`, `move_type`, `property_to_auto`, `inline_*`, `introduce_*` and the rest) also run on
a `.vb` document, each under the same compiler gate.

### Compiler errors — `fix_diagnostics`

The mechanical CS diagnostics no analyzer ships a fix for:

| Ids | What it does |
|---|---|
| CS0168, CS0219 | removes a local nothing reads — or keeps the side effect as a discard |
| CS0169 | removes a private field nothing uses |
| CS0067 | removes an event nothing raises |
| CS0105 | removes the duplicated `using` |
| CS0108, CS0114 | marks a hidden member `new` |
| CS0109 | removes a `new` that hides nothing |

## Everything it supports

**429 tools**, grouped by what you are trying to do. Every name is a tool; `docs/tools.md` gives the
one-line description of each, and `docs/reference/tool-arguments.md` its arguments. Scanners: 28 Razor
rules, 15 XAML, 17 JS/TS, 15 HTML + 9 CSS, 12 YAML, 12 SQL, 10 F#, 23 Kubernetes manifest checks.

- **Solution and projects** — `list_solutions`, `load_solution`, `check_solution`, `get_project_info`,
  `add_projects_to_solution`, `remove_projects_from_solution`.
- **Files and text** — `read_file`, `read_files`, `read_tree`, `list_directory`, `move_path`,
  `search_text`, `search_code`, `text_index`, `download_file`.
- **Navigation and search** — `find_symbol`, `find_file`, `read_symbol`, `find_references`,
  `find_callers`, `call_hierarchy`, `find_implementations`, `find_derived_types`, `get_symbol_info`,
  `get_type_members`, `solution_map`, `capability_report`, `find_endpoints`, `find_todos`,
  `analyze_stack_trace`, `structural_search` (C# and VB).
- **Documentation** — `get_xml_docs`, `find_undocumented_public_api`, `read_project_docs`.
- **Analysis** — `get_diagnostics`, `fix_diagnostics`, `run_code_analysis`, `solution_audit`,
  `inspect_code`, `apply_code_fix`, `inspection_baseline`, `configure_inspection`,
  `configure_formatting`, `suppress_finding`, `custom_inspections`, `resharper_inspection_map`,
  `read_dot_settings`, `find_spelling_issues`, `naming_rules`, `background_analysis`,
  `analysis_findings`, `complexity_report`, `get_complexity_hotspots`, `get_metrics`,
  `analyze_complexity`, `find_dead_code`, `find_unused_symbols`, `framework_entry_points`,
  `find_reflection_usage`, `list_generated_sources`, `find_external_usages`, `verify_change`,
  `find_breaking_changes`, `get_quality_trend`, `find_duplicate_code`, `analyze_test_quality`,
  `find_code_smells`, `find_missing_validation`, `analyze_nullability`, `find_security_issues`,
  `trace_data_flow`, `find_throw_sites`, `find_async_anti_patterns`,
  `find_performance_anti_patterns`, `find_regex_issues`, `analyze_dependencies`,
  `check_layer_violations`, `export_dependency_graph`, `call_graph`, `sequence_diagram`,
  `control_flow_graph`, `track_value`, `localization_report`,
  `analyze_circular_namespace_dependencies`, `analyze_dependency_injection`,
  `analyze_namespace_coupling`, `get_cohesion_metrics`, `git_churn_hotspots`, `plan_feature_change`,
  `list_analysis_tools`.
- **Refactoring** — `apply_rename`, `preview_rename`, `rename_namespace`, `rename_tuple_component`,
  `move_type`, `move_type_to_outer_scope`, `move_type_to_nested_scope`, `move_to_folder`,
  `move_to_namespace`, `move_static_members`, `move_instance_method`, `extract_method`,
  `extract_method_candidates`, `extract_class`, `extract_interface`, `extract_superclass`,
  `inline_class`, `inline_symbol`, `inline_parameter`, `inline_field`, `introduce_variable`,
  `introduce_field`, `introduce_parameter`, `introduce_part_of_string`, `change_signature`,
  `transform_parameters`, `change_type`, `change_nullability`, `use_base_type`, `add_params`,
  `remove_params`, `convert_to_primary_constructor`, `convert_to_record`, `convert_type_kind`,
  `convert_anonymous_to_type`, `constructor_to_factory`, `copy_type`, `encapsulate_field`,
  `property_to_auto`, `property_to_function`, `function_to_property`, `function_to_indexer`,
  `indexer_to_function`, `pull_members_up`, `push_members_down`, `make_static`, `make_non_static`,
  `generify`, `static_to_extension`, `extension_to_static`, `type_to_partial`,
  `extract_members_to_partial`, `global_usings`, `implicit_usings`, `convert_to_non_global_using`,
  `optimize_usings`, `reorder_members`, `remove_regions`, `safe_delete`, `safe_delete_file`,
  `safe_delete_folder`, `invert_if`, `invert_boolean`, `context_action`, `templates`,
  `migration_campaign`.
- **Generation** — `implement_members`, `implement_inpc`, `create_from_usage`, `generate_docs`,
  `generate_members`, `generate_builder`, `generate_dto_from_json`, `generate_dto_from_xml`,
  `generate_comparer`.
- **Code style** — `simplify_code`, `simplify_vb_code`, `code_cleanup`, `layout_code`, `format_code`,
  `fix_complexity`, `reduce_complexity`.
- **Build, run and publish** — `build_project`, `analyze_binlog`, `project_properties`, `run_project`,
  `run_config`, `launch_settings`, `run_command`, `dotnet_publish`, `publish_profile`,
  `add_project_reference`, `remove_project_reference`, `extract_build_log_errors`,
  `get_environment_info`, `disk_usage`, `stop_server`, `project_template`, `installer_project`,
  `deploy_target`.
- **Testing** — `run_tests`, `get_test_coverage_map`, `run_test`, `rerun_failed`,
  `tests_covering_symbol`, `run_covering_tests`, `generate_tests`, `test_impact_analysis`,
  `run_affected_tests`, `list_tests`, `debug_test`, `continuous_testing`, `find_flaky_tests`,
  `run_mutation_tests`, `verify_change`.
- **Debugging** — `debug_status`, `debug_install`, `debug_launch`, `debug_attach`,
  `debug_breakpoints`, `debug_function_breakpoints`, `debug_data_breakpoints`, `debug_continue`,
  `debug_step`, `debug_pause`, `debug_exceptions`, `debug_threads`, `debug_stack`, `debug_variables`,
  `debug_evaluate`, `debug_output`, `debug_restart`, `debug_terminate`, `debug_sessions`.
- **Benchmarks and profiling** — `run_benchmarks`, `quick_benchmark`, and the 28 `profile_*` tools:
  `profile_processes`, `profile_tools`, `profile_collect`, `profile_analyze`, `profile_root_cause`,
  `profile_async`, `profile_database`, `profile_events`, `profile_instrumentation`,
  `profile_counters`, `profile_stacks`, `profile_heap`, `profile_retention`, `profile_dump`,
  `profile_symbols`, `profile_monitor`, `profile_allocations`, `profile_calltree`, `profile_hotpaths`,
  `profile_hotlines`, `profile_io`, `profile_file_io`, `profile_compare`, `profile_diagnose`,
  `profile_slowdown`, `profile_memory_growth`, `profile_threadpool`, `profile_review_change`.
- **NuGet, packages and modernization** — `list_nuget_packages`, `check_nuget_updates`,
  `check_nuget_vulnerabilities`, `fix_nuget_vulnerabilities`, `locate_nuget_package`,
  `inspect_package`, `inspect_packages`, `decompile_package_type`, `search_nuget_packages`,
  `nuget_sources`, `add_nuget_package`, `remove_nuget_package`, `update_nuget_package`,
  `unify_nuget_version`, `nuget_pack`, `nuget_push`, `aspire`, `update_framework`,
  `update_solution`, `ef_migrations`, `package_facts`.
- **Razor, XAML, SQL and the scanned languages** — `razor_pages`, `analyze_razor_pages`,
  `find_razor_vulnerabilities`, `razor_edit`; `xaml_pages`, `analyze_xaml`, `xaml_bindings`,
  `xaml_tree`, `xaml_navigate`, `xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`,
  `format_xaml`; `sql_files`, `analyze_sql`; `js_files`, `analyze_js`, `js_symbols`; `markup_files`,
  `analyze_markup`; `yaml_files`, `analyze_yaml`; `fsharp_files`, `analyze_fsharp`, `fsharp_symbols`.
- **Decompilation, IL and metacode** — `inspect_assembly`, `inspect_assemblies`, `find_assembly_api`,
  `decompile_type`, `decompile_types`, `decompile_assembly`, `assembly_workspace`, `disassemble`,
  `metacode_translate`, `metacode_compare`, `metacode_index`, `metacode_generate`,
  `translate_python_file`, `translate_cpp_file`, `decompile_to_metacode`, `analyze_native_binary`,
  `metacoding`.
- **Git, GitHub and other hosts** — `git_status`, `git_diff`, `git_log`, `git_stage`, `git_commit`,
  `git_amend`, `git_squash`, `commit_lint`, `git_branch`, `git_create_branch`, `git_merge`,
  `git_conflicts`, `git_rebase`, `git_cherry_pick`, `git_revert`, `git_reset`, `git_stash`,
  `git_patch`, `git_tag`, `git_remote`, `git_show`, `git_blame`, `git_sync`, `git_worktree`,
  `git_bisect`, `git_submodule`, `git_host`, `git_run`; `gh_repo`, `gh_pr`, `gh_issue`, `gh_release`,
  `gh_actions`, `gh_secret`, `gh_branch_protection`, `github`, `code_review`.
- **Containers, clusters and Node** — `docker_status`, `docker_ps`, `docker_inspect`, `docker_logs`,
  `docker_control`, `docker_rm`, `docker_run`, `docker_images`, `docker_pull`, `docker_image_rm`,
  `docker_networks`, `docker_volumes`, `docker_stats`, `docker_compose`, `docker_prune`;
  `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs`, `k8s_kustomize`,
  `k8s_validate`, `k8s_port_forward`, `k8s_exec`, `k8s_apply`, `k8s_delete`, `k8s_rollout`,
  `k8s_helm`; `node_info`, `node_install`, `node_script`, `node_typecheck`, `node_lint`,
  `node_outdated`, `node_audit`.
- **Planning, automation and protocols** — `board_brief`, `board_audit`, `board_remember`,
  `board_recall`, `board_forget`, `board_add`, `board_update`, `board_move`, `board_comment`,
  `board_list`, `board_show`, `board_board`, `board_import`, `board_export`, `board_delete`,
  `create_task_checklist`, `update_task_checklist`; `run_plan`, `http_invoke`, `run_http_file`,
  `http_file`, `signal_r_connect`, `signal_r_subscribe`, `signal_r_events`, `signal_r_invoke`,
  `signal_r_logs`, `signal_r_list_sessions`, `signal_r_disconnect`.
- **Maintenance, scaffolding and self-management** — `command_log`, `edit_history`, `revert_edit`,
  `local_history`, `reference_index`, `host_system`, `fix_line_endings`, `list_licenses`,
  `add_license`, `add_contributing`, `add_ci_workflow`, `check_for_update`, `self_update`.

## Why it saves tokens

A tool call is not one message. Every call re-sends the conversation to the model and appends its
result, so a chain of five calls pays for the transcript five times. Two things move that number, and
both are structural rather than tuning:

- **One call instead of a chain.** `read_symbol` is what `grep` + `Read` + "no, the other overload"
  used to be. `search_code` returns every hit already resolved to the declaration that contains it.
  `find_references` answers with real uses, so there is nothing to disambiguate with a second read.
  `inspect_code` and `solution_audit` replace a page of greps with one report, `profile_diagnose` one
  probe with a set of correlated ones, and `run_plan` a mechanical fan-out — one `simplify_code` per
  file, then a build — with a single call.
- **A narrower answer.** `read_symbol` sends one declaration, not the file around it. `decompile_type`
  compacts ILSpy's C# — indentation, blank lines and comments dropped, the same tokens in about half
  the characters. `profile_hotlines` sends the lines that burn CPU, not the trace, and
  `profile_root_cause` sends the loop, not the sample dump.

The chains this removes are not hypothetical. Measured on our own sessions (a 2026-10-29 snapshot: 51
sessions of this project, 10,249 recorded tool calls), **244 round trips existed only because the
previous answer taught the model a name it then had to go and look up** — over 4,176 VisualMCP calls in
736 turns ([agent ergonomics](docs/agent-ergonomics.md)). The dominant one is `search_text` →
`read_file`, 134 times: the match line was not the code, so the next call went and read it. Against
1,957 raw navigation calls stood 104 symbol-level ones. Those 244 are a floor, from sessions that
already know this codebase — a first visit to an unfamiliar solution pays more, because every
orientation call is a round trip.

| The detour | Now |
|---|---|
| grep the name, read the files it hit, re-read the one whose hit was a comment | `find_references` / `find_callers` — one call, real uses, every overload |
| open the file to see one method | `read_symbol` — the declaration, with its XML docs |
| grep for a string, then read around each hit | `search_code` — the declaration per hit, one call |
| twelve greps to answer "what does this support" | `capability_report` / `solution_map` — one call |
| collect counters, then a trace, then read both | `profile_diagnose` — one call, the findings correlated |
| one `simplify_code` per file, then a build | `run_plan` — one call for the whole fan-out |

## Safety

Four invariants hold across all 429 tools:

- **Semantic, not textual.** Every write is a Roslyn operation on the real project, never a regex over
  files.
- **Compiler-gated.** An edit that adds a compiler error is refused. The whole run rolls back if the
  fixes together would break the build.
- **Recorded and revertible.** Every file the server writes lands in the solution's edit history with
  its before-and-after hash; `revert_edit` undoes it, and the revert is recorded too.
- **Read-only where it says so.** Analysis and profiling never modify anything; the mutating tools are
  marked ✎ and most default to a dry run.

One server serves **one workspace**: the folder the client opened is fixed for the life of the process,
and `load_solution` cannot leave it. Another repository is another server, with its own store and board.
The model, including what happens with two sessions on the same folder, is
[The workspace, the store and multiple sessions](docs/workspace-and-store.md).

## One model, many languages

Metacode is the canonical pseudocode model behind the analysis: it reads **C#, Visual Basic,
Razor pages, JavaScript/TypeScript, Rust and Java source, Python and C++ AST dumps, decompiled .NET
assemblies and recovered native binaries** — persisted per declaration, diffable, generatable back to
C#. Two spellings of the same algorithm compare line-by-line across languages, so the same vocabulary
describes a Ghidra recovery and a Razor page.

## How it compares

| | grep and file reading | index-based code MCPs | **VisualMCP** |
|---|---|---|---|
| Cost of an answer | 3+ calls, each re-sending the conversation | 1–2 calls, in an index's terms | **one call, in the compiler's terms** |
| Understands C# | finds the *text* of a symbol | indexes identifiers | **compiles the solution** — overloads, generics, dispatch |
| Renames safely | renames comments and strings too | misses cross-file uses | **every real reference, compiler-gated** |
| Fixes the build | shows you the log tail | reports findings | **fixes the mechanical errors itself** |
| Debugs and profiles | — | — | **a real debugger and a real profiler** |
| Edits you can trust | writes whatever the model says | same | **gated, recorded, revertible** |

## The honest bit

C# and Visual Basic get the semantic model; XAML, Razor, SQL, JavaScript/TypeScript, HTML/CSS, YAML
and F# get a scanner (they are not compilers), each with a rule set that states what it proves — and
JavaScript/TypeScript and F# also get a **lexical** symbol table (`js_symbols`, `fsharp_symbols`: names,
the declaration a name resolves to, its references, no type model); the Node, Docker and Kubernetes
toolchains are wrapped rather than reimplemented, with an offline manifest validator (`k8s_validate`).
HTML and CSS are scanned, not rendered, and F# has no semantic model — on purpose, and stated with the
other boundaries in the [gap inventory](docs/gaps.md). Everything is static analysis or process
instrumentation: no tool proves a change preserves behaviour, and none replaces your test suite.
And it runs on your machine, locally — no hosted service, no account, no daemon. Your code leaves the
box only in the conversation your client already sends to its model provider.

## Documentation, by what you need

| If you want to… | Read |
|---|---|
| install, verify, upgrade, uninstall | [Getting started](docs/getting-started.md) |
| understand the process, the store, the dashboard, privacy | [How it works](docs/how-it-works.md) |
| browse the running server's store, tools and log in a browser | [Web interface](docs/dashboard.md) |
| look up a tool | [Tool catalogue](docs/tools.md) · [Tool arguments](docs/reference/tool-arguments.md) |
| read and analyse code | [Reading and analysing code](docs/guides/code.md) |
| refactor safely, apply style fixes | [Refactoring and style](docs/guides/refactoring.md) |
| work on Razor, metacode or decompilation | [Razor, metacode and decompilation](docs/guides/razor-metacode.md) |
| build, test, manage NuGet, modernize | [Build, test, packages and modernization](docs/guides/runtime.md) |
| profile a live process | [Profiling](docs/guides/profiling.md) |
| use `run_plan`, planning, Git, GitHub, Docker | [Workflow and integrations](docs/guides/workflow.md) |
| configure ports, paths and environment variables | [Configuration reference](docs/reference/configuration.md) |
| know what it does **not** do | [The gap inventory](docs/gaps.md) and [requirements/guarantees](docs/reference/limits.md) |
| see the ReSharper/CodeMaid coverage, row by row | [Coverage](docs/resharper-codemaid-coverage.md) |
| troubleshoot | [Help and FAQ](docs/help.md) |
| contribute | [CONTRIBUTING.md](CONTRIBUTING.md) |

The engineering notes behind these pages are indexed in [docs/README.md](docs/README.md). Every folder
has its own README: the [engine](SNITCH/README.md), [language layers](SNITCH/Languages/README.md),
[libraries](SNITCH/Libraries/README.md), [the solution](src/README.md), [the MCP host](src/VisualMCP/README.md),
[the test suite](src/VisualMCP.Tests/README.md), [scripts](scripts/README.md), [hooks](hooks/README.md),
[client skills](skills/README.md), [docs](docs/README.md) and [samples](samples/README.md).

## Development

```powershell
dotnet build VisualMCP.sln                                    # engine + host + tests
dotnet test  src/VisualMCP.Tests/VisualMCP.Tests.csproj       # the suite: 1,578 test methods
powershell -ExecutionPolicy Bypass -File scripts\reload.ps1   # rebuild + republish the server
```

Architecture rules and the one-type-per-file rule are in [CONTRIBUTING.md](CONTRIBUTING.md). After
adding or removing a tool, reconnect the MCP server so the client refreshes its tool list.

## License

VisualMCP is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ **Free for noncommercial use** — individuals, hobby and personal projects, learning, research,
  education, and other noncommercial organizations.
- 💼 **Commercial / company use requires a paid license.** Any use by or for a company (or for
  commercial advantage or monetary compensation) needs a separate commercial license. Contact
  **me@matteofabbri.org**.

Source-available, not OSI "open source": read it, fork it, contribute to it — commercial use is
reserved. See [`LICENSE`](LICENSE) for the exact terms.

*This summary is for convenience only and is not legal advice; the [`LICENSE`](LICENSE) file governs.*
