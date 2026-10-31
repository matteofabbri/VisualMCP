<!-- VisualMCP:begin (managed by the VisualMCP installer; changes inside this block are overwritten) -->
## VisualMCP - C# / .NET work

`VisualMCP` is installed on this machine (MCP server, Roslyn-based). It discovers and
loads the solution under the working directory automatically: call its tools directly.
`load_solution` is needed only when a tool reports that no solution was found, or to
target a specific `.sln`/`.slnx` by path.

In a .NET repository, use these tools instead of shell probes or reading files by hand:

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
| `dotnet test` once per project | `run_tests{projectFilter, extraArgs}` |
| rename / extract / move / change signature | `apply_rename`, `extract_interface`, `extract_method_candidates`, `change_signature`, `move_type` |
| `python - <<PY` / `sed -i` / `cat > file <<EOF` rewriting or creating a file | `Edit` / `Write` (never heredoc edits) |
| "what is missing / what's next / what do we implement" | `board_brief` — one compact call: counts, the card to pick up, every open card with its age and a stale flag. `board_audit` says which open cards this build already ships; `board_board` / `board_list` are the full and the filtered view |
| remember a fact, a decision or a constraint for the next session | `board_remember{topic, body, kind}` (pinned ones ride along in `board_brief`), read back with `board_recall{query}` — NOT a `MEMORY.md`, a note file or a chat message |

Rules:

- A `PreToolUse` hook denies `grep`, `rg`, `sed -n`, `cat`, `head`, `tail`, `ls`, `find`,
  `Select-String`, `Get-Content` and `Get-ChildItem` inside .NET repositories, plus every
  shell file write (inline `python`, `sed -i`, `cat >`/`tee`/`echo >`). It replies with the
  tool to use instead. Do not retry the same command: switch to the tool.
- Build, test, git and real pipelines remain available from the shell. If a shell form is
  genuinely required (binary files, counting, complex pipes), call the MCP tool
  `run_command` with the exact same command.
- C# semantics (overloads, overrides, generics, partial types, interface dispatch) are
  invisible to text search: call the semantic tool instead of inferring from grep output.
- Prefer `build_project` / `run_tests` / `git_status` / `git_diff` / `git_log` over piping
  `dotnet build`, `dotnet test` or `git` through `grep`/`tail`: the MCP tools return
  structured errors and results instead of a log tail you have to read again.
- At the start of work on a solution, read `read_project_docs` (README/docs) before code.
- The board is the source of truth for what is missing and what comes next. When the user asks
  what is missing, what comes next or what to implement, call `board_brief` first — it answers in
  one call. `board_audit` cross-checks each open card's tool names against this build and says
  which cards are probably already done (`close: true` records the evidence and closes them).
  Memory notes, `docs/gaps.md` and any backlog file are historical records: never answer that
  question by reading one of them. Keep the board alive as you work: `board_move{status:
  in_progress}` when you start, `board_move{status: done, note: <evidence>}` when you finish,
  `board_add` for work you discover.
- `read_project_docs` answers with a `board` block (counts, the card to pick up) — read that
  instead of the plan documents; the documents describe the project, the board says what to do.
- Persistent memory lives on the board: a fact, a decision or a constraint goes to
  `board_remember{topic, body, kind}` and is read back with `board_recall{query}`. Do not write
  `MEMORY.md`, a `*.md` handoff/plan/backlog file or a chat promise as the place a later session is
  meant to find something — the hook denies those writes, and the next session reads the board. This
  build exposes no `memory_read`, `memory_write` or `memory_append`; never call them. Notes another
  client keeps under `~/.claude/projects/<slug>/memory` (or under `~/.zcode/cli/memories`) are
  imported onto the board at startup, and `read_project_docs` reports what moved.
- For multi-step work, `create_task_checklist{title, steps}` puts ONE card on the board with the
  steps as a checklist (a file is not written), and `update_task_checklist{id, done: [...]}` ticks
  them off; `board_add{title, steps: [...]}` does the same on a card you describe yourself.
<!-- VisualMCP:end -->
