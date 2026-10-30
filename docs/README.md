# docs — documentation

Two separate sets, on purpose. **`public/`** is the user guide: it explains the product to
someone who installs it and never reads the engine. The files directly in this folder are
engineering notes: they describe how the engine is built, what it covers and what it does not,
and they are allowed to name internal types.

## User guide — [`public/README.md`](public/README.md)

The entry point for users. Install, concepts, the complete tool catalogue, task guides, limits.

| Page | What it answers |
|---|---|
| [Getting started](public/getting-started.md) | requirements, install scopes, verification, first session, upgrade, uninstall |
| [How it works](public/how-it-works.md) | the process model, Roslyn loading, the solution store, the dashboard, privacy |
| [Tool catalogue](public/tools.md) | every tool, grouped by area, one line each |
| [Tool arguments](public/reference/tool-arguments.md) | the arguments and behaviour that are not obvious from a one-line entry |
| [Reading and analysing code](public/guides/code.md) | navigation, diagnostics, audits, complexity, security, dead code, dependency analysis |
| [Refactoring and style](public/guides/refactoring.md) | the refactoring catalogue, `simplify_code`, inspections and code fixes, what "safe" means |
| [Razor, metacode and decompilation](public/guides/razor-metacode.md) | page analysis and edits, canonical pseudocode, ILSpy and Ghidra |
| [Build, test, packages and modernization](public/guides/runtime.md) | build/run/test, coverage, benchmarks, NuGet, Aspire, upgrades |
| [Profiling](public/guides/profiling.md) | native CPU/GC/heap/I/O profiling, the verdict tools, artifacts, limits |
| [Workflow and integrations](public/guides/workflow.md) | `run_plan`, planning, Git, GitHub, Docker, files, HTTP, SignalR |
| [Configuration](public/reference/configuration.md) | environment variables, paths, ports, store tables, dashboard API |
| [Limits](public/reference/limits.md) | what the product does not do, and why |
| [Help and FAQ](public/help.md) | troubleshooting and common questions |
| [Contributing](public/contributing.md) | build, test, architecture rules, CLA |

## Engineering notes

| Page | What it answers |
|---|---|
| [engine-state.md](engine-state.md) | which IDE rules are rewritten, which are not and why, the structural limits of the rewrite engine, the state of the solution store |
| [gaps.md](gaps.md) | **everything the product does not do**: language and platform gaps, test debugging, mutation testing, template and inspection-administration gaps |
| [metacode.md](metacode.md) | the canonical pseudocode model: grammar, frontends, decompilation, indexing, generation, limits |
| [profiling.md](profiling.md) | every `profile_*` tool, what it reads, the artifacts, the known limits and the comparison with Visual Studio |
| [razor.md](razor.md) | the Razor/Blazor analysis layer and its 28 rules |
| [dashboard.md](dashboard.md) | the web dashboard: pages, query parameters, JSON API, safety rules |
| [modernization.md](modernization.md) | `aspire`, `update_framework`, `update_solution` |
| [adding-a-language.md](adding-a-language.md) | how a new language joins the style engine or the metacode model |
| [agent-ergonomics.md](agent-ergonomics.md) | how many calls an agent needs to answer one question, measured on the session store, with the chains that are still mechanical |
| [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md) | the rule-by-rule comparison against ReSharper / Rider / CodeMaid |
| [resharper-context-actions.md](resharper-context-actions.md) | the context-action catalogue and its behaviour |

The rule-by-rule table for the style engine is [`../CAPABILITY_MAP.md`](../CAPABILITY_MAP.md).

## House rules

- **Counts are live facts.** A tool count, "87 rule ids", "28 Razor rules" — all of them age badly:
  take the number from `capability_report` when you write it, and say where it came from.
- **`gaps.md` is the honest inventory.** A feature is not documented as present until it is in the
  tool catalogue and the engine applies it; anything else belongs in `gaps.md`.
- **Public pages do not name internal types.** If a sentence needs `SnitchContext` or
  `CompilationCache` to make sense, it belongs here, not in `public/`.
- **Links are relative and checked.** Both sets are read on GitHub, so a renamed page must be
  renamed in every index at once.
