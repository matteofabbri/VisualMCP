---
name: visualmcp
description: Use VisualMCP for C#/.NET repository work — semantic code search and reads (find_symbol, read_symbol, search_code), compiler diagnostics, builds, tests, git, refactoring, decompilation, Razor/XAML analysis, profiling, and the planning board. Installing this skill supplies instructions only; it does not register the VisualMCP MCP server or add its tools to a running client — when the tools are absent, connect them first. Skip it outside .NET repositories (no .sln/.slnx in the working tree).
metadata:
  version: "1"
---

# VisualMCP

VisualMCP is a Roslyn-based MCP server. It discovers and loads the solution under the
working directory automatically and answers code questions semantically: symbol search and
reads, compiler diagnostics, builds and tests, git, mechanical refactoring, decompilation,
Razor and XAML analysis, profiling, plus the planning board that holds the project's plan.
When its tools are connected, route all C# work through them instead of shell probes and
hand-read files: C# semantics (overloads, overrides, generics, partial types, interface
dispatch) are invisible to text search, and the tools return structured results instead of
a log tail to read again.

## Connect only when needed

Installing this skill supplies instructions; it does not register the VisualMCP MCP server,
publish the build, or add tools to an already-running client. The exception is a plugin
(marketplace) install: `.claude-plugin/plugin.json` bundles this skill and registers its own
`VisualMCP` server through the plugin launcher, outside what the script installers manage.

If `mcp__VisualMCP__*` tools are available and registration is not known to be stale, go
straight to the work. Do not run setup, doctor or registration checks before every task.
Use the connected server's actual tool list and input schemas; a skill installed from
repository main may describe tools an older installed build does not have yet.

If the tools are absent, connect the server once, then restart the client:

- Windows, from a checkout of the repository:
  `powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode`
- Linux / macOS, from a checkout:
  `./scripts/install.sh --scope zcode`
- Other scopes (`all`, `user`, `project`, `local`) and what each writes are in
  [connection and recovery](references/connection-and-recovery.md).
- No checkout and no installed build: manual registration is the fallback — point the
  client's MCP configuration at the published `VisualMCP.exe` (or `dotnet <path to
  VisualMCP.dll>`). Never use `dotnet run`; its build output corrupts the stdio protocol.
- Plugin install: if the launcher reports the runtime missing, run the script installer,
  then disable and re-enable the plugin so it relaunches the server.

Then RESTART the client. MCP configuration and hooks are read at session start: the tools
do not appear mid-session no matter how often the call is retried. Tell the user to
restart instead of repeating a call. Verify with `check_solution` in a folder that holds a
solution — it reports the loaded solution, the project count, and the workspace root.

Connecting never installs the .NET SDK, Node.js, a JDK, Ghidra or any other engine:
VisualMCP needs a .NET 10 SDK to build and run, and everything else (Docker, the GitHub
CLI, a JDK for native analysis) lights up only when already present. If a prerequisite is
missing, say so and stop; do not install unrelated software to repair registration.

For every observed blocker — a call the hook denies, a tool that reports no solution, a
server outside its workspace boundary, a call that times out — read
[connection and recovery](references/connection-and-recovery.md) before a second repair
attempt, and keep doing useful work that does not depend on the broken call.

## Route the work through the tools

In a .NET repository, use these instead of shell probes or reading files by hand:

| Instead of | Call |
|---|---|
| `grep -rn ClassName` | `find_symbol` |
| `grep -rn` "who uses / who calls this" | `find_references` / `find_callers` |
| `grep -rn` "who implements this interface" | `find_implementations` / `find_derived_types` |
| "show me the body of this type/method" (you just saw its name) | `read_symbol{symbolName}` — one call instead of `search_text` + `read_file` |
| `sed -n '10,40p' file.cs`, `cat`, `head`, `tail` | `read_file{path, fromLine, toLine}` |
| reading several files whose paths you already know | `read_files{paths[]}` — one round-trip, budgeted, per-file errors |
| `grep` for arbitrary text in any file | `search_text{pattern, path, glob}` |
| `grep` and then reading the file to see the code | `search_code{pattern, path}` — every hit comes back as the whole declaration, one call instead of `search_text` + `read_file` |
| several DLLs, NuGet packages or decompiled types | `inspect_assemblies`, `inspect_packages`, `decompile_types` — one call each instead of a plural loop |
| keeping a decompilation, or writing code through the pseudocode model | `metacoding{action: decompile / rewrite / insert}` (mechanical style fixes stay in `simplify_code`) |
| `ls`, `dir`, `find` | `list_directory{path}` |
| `ls -R`, `dir /s`, "walk down this tree to see the shape" | `read_tree{path, depth, pattern}` — one call instead of one `list_directory` per level |
| opening a type to see its members | `get_type_members` / `get_symbol_info` |
| guessing whether the code compiles | `get_diagnostics` / `build_project` |
| run only the tests a change reaches | `run_affected_tests{baseRef}` — git diff → the tests that reference the changed symbols, run in one call (`test_impact_analysis` lists them without running) |
| re-run exactly what failed in the last run | `rerun_failed` |
| one test, one class, or a raw `dotnet test --filter` | `run_test{testName?, className?, filter?}` |
| the whole suite (the pre-merge gate, slow) | `run_tests{projectFilter?, extraArgs?, timeoutSeconds?}` |
| rename / extract / move / change signature | `apply_rename`, `extract_interface`, `extract_method_candidates`, `change_signature`, `move_type` |
| stage and commit (everything, or just some files) | `git_commit{stageAll: true}` / `git_commit{paths: [...]}` — staging is part of the call, not a separate `git_stage` |
| `python - <<PY` / `sed -i` / `cat > file <<EOF` rewriting or creating a file | `Edit` / `Write` (never heredoc edits) |
| "what is missing / what's next / what do we implement" | `board_brief` — one compact call: counts, the card to pick up, every open card with its age and a stale flag. `board_audit` says which open cards this build already ships; `board_board` / `board_list` are the full and the filtered view |
| move or close several cards | `board_move{ids: [...], status, note}` — one call, one transaction, the note on every card |
| remember a fact, a decision or a constraint for the next session | `board_remember{topic, body, kind}` (pinned ones ride along in `board_brief`), read back with `board_recall{query}` — NOT a `MEMORY.md`, a note file or a chat message |

Non-C# files (JSON, YAML, logs, scripts) keep normal tools. Build, test, git and real
pipelines remain available from the shell; for a shell form the hook denies and that is
genuinely required (binary files, counting, complex pipes), call the MCP tool
`run_command` with the same command.

## Rules

- A `PreToolUse` hook (installed by the script installers) denies `grep`, `rg`, `sed -n`,
  `cat`, `head`, `tail`, `ls`, `find`, `Select-String`, `Get-Content` and `Get-ChildItem`
  inside .NET repositories, plus every shell file write (inline `python`, `sed -i`,
  `cat >`/`tee`/`echo >`). It replies with the tool to use instead. Do not retry the same
  command: switch to the tool. The literal token `vmcp-allow` keeps a command deliberately.
- Prefer `build_project` / `run_tests` / `git_status` / `git_diff` / `git_log` over piping
  `dotnet build`, `dotnet test` or `git` through `grep`/`tail`: the MCP tools return
  structured errors and results instead of a log tail you have to read again.
- After changing code, run `run_affected_tests` first: it derives the tests from the diff, so
  the suite you run is the one the change reaches. Use `rerun_failed` to repeat a failure,
  `run_test` for one test or class, and `run_tests` for the whole suite — that is the pre-merge
  gate, not the loop you run after every edit.
- At the start of work on a solution, read `read_project_docs` (README/docs) before code.
- The board is the source of truth for what is missing and what comes next. When the user
  asks what is missing, what comes next or what to implement, call `board_brief` first —
  it answers in one call. Memory notes, `docs/gaps.md` and any backlog file are historical
  records: never answer that question by reading one of them. Keep the board alive as you
  work: `board_move{status: in_progress}` when you start, `board_move{status: done, note:
  <evidence>}` when you finish, `board_add` for work you discover.
- Persistent memory lives on the board: a fact, a decision or a constraint goes to
  `board_remember{topic, body, kind}` and is read back with `board_recall{query}`. Do not
  write `MEMORY.md`, a `*.md` handoff/plan/backlog file or a chat promise as the place a
  later session is meant to find something. Notes another client keeps under
  `~/.claude/projects/<slug>/memory` (or `~/.zcode/cli/memories`) are imported onto the
  board at startup, and `read_project_docs` reports what moved.
- For multi-step work, `create_task_checklist{title, steps}` puts ONE card on the board
  with the steps as a checklist (a file is not written), and `update_task_checklist{id,
  done: [...]}` ticks them off; `board_add{title, steps: [...]}` does the same on a card
  you describe yourself.
- Never create a `.md` at the repository root unless it is one of the published documents
  (README, CONTRIBUTING, LICENSE, CHANGELOG, ...): a checklist, a plan or a working note
  named after the task it describes is a card, and the hook refuses the write.
  Documentation goes under `docs/`.

## What is installed on this machine

The script installers (`scripts/install.ps1` / `scripts/install.sh`) manage four
independent pieces; any of them can be present without the others:

| Piece | Where | What it does |
|---|---|---|
| the server | the client's MCP configuration, launching the published build | the tools |
| the routing hook | the client's hooks configuration | denies the shell probes above at the moment they happen |
| the AGENTS.md block | `~/.zcode/AGENTS.md`, managed `VisualMCP:begin/end` block | the same routing, stated up front in every ZCode session |
| this skill | `~/.zcode/skills/visualmcp/` (and `~/.claude/skills/visualmcp/`) | this document |

A skill-only install (copying this folder by hand) is legitimate: it carries the routing
and the connection instructions, and the agent connects the server itself on first use —
exactly what the "Connect only when needed" section describes.
