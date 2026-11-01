[Docs home](../README.md) › Guides › **Workflow and integrations**

# Workflow and integrations

VisualMCP is also the plumbing around the code: executing a batch of calls, keeping a plan, remembering
project facts, and driving Git, GitHub and Docker without leaving the client.

## `run_plan`

`run_plan` executes a whole dependency graph **inside the server**, with no model in the loop: one round
trip instead of one call per step.

```json
{
  "name": "tidy-and-verify",
  "steps": [
    {
      "id": "simplify",
      "kind": "tool",
      "tool": "simplify_code",
      "arguments": { "scope": "project", "projectName": "MyApp", "rules": "IDE0028,IDE0041", "dryRun": false },
      "paths": ["src/MyApp"]
    },
    {
      "id": "inspect",
      "kind": "tool",
      "tool": "inspect_code",
      "arguments": { "projectName": "MyApp", "minSeverity": "warning" },
      "paths": ["src/MyApp"]
    },
    {
      "id": "build",
      "kind": "command",
      "command": "dotnet build src/MyApp/MyApp.csproj -c Release",
      "shell": "powershell",
      "dependsOn": ["simplify", "inspect"],
      "timeoutSeconds": 300
    }
  ],
  "maxConcurrency": 2,
  "onFailure": "stop",
  "outputChars": 4000
}
```

How it behaves:

- A step is either a **tool** call — any tool this server exposes, by name, with the argument names its
  schema uses — or a **command** (`powershell`, `pwsh`, `cmd`, `bash`).
- **`dependsOn`** orders steps. Independent steps run concurrently, bounded by `maxConcurrency`.
- **`paths`** declares what a step writes. Two steps whose paths overlap never run at the same time,
  which is what makes parallel edits safe.
- **`forEach` + `as`** fans one step out over a list, substituting `${item}`.
- **Validation happens before anything runs.** A duplicate id, an unknown tool, an unknown dependency or
  a cycle rejects the whole plan and nothing executes.
- A failed step skips its dependents. `onFailure: "stop"` (default) also stops starting unrelated steps;
  `"continue"` runs them anyway. `continueOnError` marks a step's failure as tolerable.
- The report aggregates every step's status, timing, exit code, error and output, under a per-step
  `outputChars` budget.

Use it for **mechanical** work: the same operation over many files, or a build that depends on a set of
edits. Do not use it for steps that need judgement between them — that is still the model's job.

## Planning

### Kanban board

The board lives in the solution store, so it survives restarts and is scoped to the solution.

| Tool | Action |
|---|---|
| `board_add` | Create a card: title, description, column, priority, labels, assignee |
| `board_update` | Edit fields; every change is recorded in history |
| `board_move` | Move across `backlog → todo → in_progress → review → done`, optionally at a position |
| `board_comment` | Append a progress note, blocker or decision |
| `board_list` | Filter by column, priority, assignee, label or text |
| `board_show` | One card with all comments and its full history |
| `board_board` | The whole board as five columns, with counts and the done share |
| `board_delete` | Delete a card with its comments and history |

### Checklists

`create_task_checklist` puts a multi-step operation on the board: one card whose description holds the
steps as a checklist, so it shows up in `board_brief`, `board_show` and `board_export` like every other
card. `update_task_checklist` ticks steps off by card id, and `board_add{title, steps: [...]}` does the
same on a card you describe yourself. No file is written — a Markdown checklist next to the solution is
invisible to the next session, a card is not.

### Memory

Memory is the board's second lane. A fact, a decision, a constraint goes to
`board_remember{topic, body, kind}`: a topic has one current version, writing it again supersedes the
previous one (kept as history) instead of duplicating it, and an identical body is a no-op.
`board_recall{topic|query}` reads it back, `board_forget{topic}` retires a topic. Pinned notes ride
along in `board_brief` and in the `board` block of `read_project_docs`, which is what makes writing one
worth the call. The `memory_read` / `memory_write` / `memory_append` tools of older builds are gone.

Notes that still live as files are imported once: Claude Code keeps them under
`~/.claude/projects/<slug>/memory/` (the slug is the project path with `:` and every separator folded to
`-`, this repository's is `D--REPOSITORY-VisualMCP`) and this client under
`~/.zcode/cli/memories/projects/<name>-<id>/memory/`. At startup the server reads both and writes every
note through `board_remember` (topic `imported-<type>-<name>`, the kind mapped from the frontmatter
`type`), keyed on the body so a second start imports nothing; the files stay where they are but stop
being the source. The first `read_project_docs` of a session returns a `memoryImport` block saying what
moved. Set `VISUALMCP_MEMORY_IMPORT=off` to skip the pass.

`docs/gaps.md`, a backlog file and a `MEMORY.md` are historical records; the board is the plan and the
memory. The routing guard (`VisualMCP --hook pre-tool-use` on Linux/macOS, `hooks/pre-tool-use.ps1` on
Windows) enforces it: it denies a
`Write`/`Edit` into a memory store and the creation of a plan-like `.md` at the repository root or in
`docs/`, naming the tool that does the job instead. Editing a document that already exists is never
refused.

For facts the repository documents itself, `read_project_docs` is the right source and is indexed
automatically when the solution opens.

## Git

27 tools, covering the daily flow, the history operations that usually need a terminal, and the hosting services other than GitHub.

| Task | Tools |
|---|---|
| Inspect | `git_status`, `git_diff`, `git_log`, `git_show`, `git_blame` |
| Stage and commit | `git_stage`, `git_commit` |
| Branches | `git_create_branch`, `git_branch` (list/create/delete/rename/switch) |
| Rewrite local history | `git_squash`, `git_amend`, `git_reset` (soft/mixed/hard) |
| Interactive rebase as data | `git_rebase action=plan` — a JSON list of pick/reword/squash/fixup/drop steps, no editor |
| Integrate | `git_merge`, `git_rebase`, `git_cherry_pick`, `git_revert` |
| Resolve a conflict | `git_conflicts` — hunks with ours/theirs/base, resolve per hunk (ours/theirs/both/base/custom), stages the file |
| Park work | `git_stash` (push/list/pop/apply/drop/show/clear), `git_patch` (named patches: create/list/show/apply/reverse/delete) |
| Remotes and tags | `git_remote`, `git_tag`, `git_sync` |
| Anything else | `git_run` — any git command, verbatim, with exit code and output |

Safety details:

- `git_reset` with `mode=hard` requires `confirm=true`; it discards uncommitted work.
- `git_sync` with `force=true` requires `confirm=true` and uses `--force-with-lease`.
- `git_squash` refuses on a dirty working tree, because its changes would be folded into the squashed
  commit.
- `git_merge`, `git_rebase` and `git_cherry_pick` report the conflicted files; `git_conflicts` resolves
  them hunk by hunk, then continue or abort through the same tool.
- A history rewrite as a todo list (`git_rebase action=plan`) never opens an editor: `reword` and
  `squash` take their message from the step, and a conflict stops the plan like any other rebase.

## GitHub

Eight tools over the authenticated [`gh` CLI](https://cli.github.com/). Run `gh auth login` once; the
server does not manage credentials.

| Tool | Actions |
|---|---|
| `gh_repo` | list, view, create, fork, clone, edit, delete |
| `gh_pr` | list, view, create, merge, close, reopen, comment, review (line-anchored comments), review-comments, checks, diff, checkout, ready |
| `gh_issue` | list, view, create, close, reopen, comment |
| `gh_release` | list, view, create, upload assets, delete |
| `gh_actions` | runs list/view/logs, rerun, cancel, workflows, dispatch |
| `gh_secret` | list, set, delete repository Actions secrets |
| `gh_branch_protection` | get, checks, rules — branch protection and rulesets, read-only |
| `github` | Any `gh` command, verbatim |

GitLab, Azure DevOps and Bitbucket Cloud are `git_host`: the provider, host, project and repository are
read from the git remote (`action=detect` reports them), and the write actions require `confirm=true`.
Tokens come from the server's environment (`GITLAB_TOKEN`, `AZURE_DEVOPS_EXT_PAT`, `BITBUCKET_*`).
Before reviewing a change, `code_review` returns the brief the review starts from.

Destructive actions (`merge`, `close`, `delete`, `set`/`delete` secret, `rerun`/`cancel`/`dispatch`) require
`confirm=true` where the tool defines it. Secrets are sent to GitHub and never echoed back.

## Docker

15 tools covering the container lifecycle. The engine must be running.

| Task | Tools |
|---|---|
| Availability and inventory | `docker_status`, `docker_ps`, `docker_images`, `docker_networks`, `docker_volumes` |
| Inspect | `docker_inspect`, `docker_logs`, `docker_stats` |
| Lifecycle | `docker_run`, `docker_control`, `docker_rm`, `docker_pull`, `docker_image_rm` |
| Compose | `docker_compose` (up, down, ps, logs, restart, stop, start, pull, build) |
| Reclaim disk | `docker_prune` — requires `confirm=true` |

`docker_rm` needs `force=true` to remove a running container; `docker_image_rm` needs it when containers
still reference the image. Prune is irreversible.

## Files, search and indexes

| Need | Tool |
|---|---|
| Read one file | `read_file` |
| Read several known files | `read_files` — one budgeted call, per-file errors |
| See a folder tree | `read_tree` — one call, build folders skipped |
| Inventory one folder | `list_directory` |
| Regex search any text | `search_text` |
| Ranked word search across the solution | `text_index` |
| Symbol declarations and typed usages | `reference_index` |
| Move or rename a file | `move_path` |
| Download a URL | `download_file` |

Three search tools, three different jobs:

- **`search_text`** is grep with structure: regex, glob filter, context, `file:line`. Use it for configs,
  logs, C/C++ headers and arbitrary text.
- **`text_index`** is a full-text index in the store. Words are matched inside identifiers (`required`
  finds `GetRequiredService`), results are BM25-ranked, and one call returns the matching line plus the
  files that match most. Use it when you want *where is this named/documented* and do not need a regex.
- **`reference_index`** is the symbol index: declarations, typed usages (`call`, `read`, `write`,
  `instantiate`, `inherit`, `implement`, `override`, `attribute`, `cast`, `typeof`, `nameof`, pattern,
  type argument), callers/callees, hierarchy, outline, metacode per declaration, unused symbols.

Indexes are snapshots keyed on file content: `build` them before querying and rebuild after edits.
`text_index` builds on first search; `reference_index` reports what is stored with `action=status`.

`package_facts` is the NuGet graph from `obj/project.assets.json` — no restore, no network — with
resolved versions per target framework and the dependency edges.

## HTTP and SignalR

| Tool | Use |
|---|---|
| `http_invoke` | Call a REST endpoint of a running app: any method, headers, JSON body, timeout. Self-signed localhost certificates accepted |
| `signal_r_connect` | Connect to any ASP.NET Core SignalR hub and get a session id |
| `signal_r_subscribe` | Buffer messages for one hub method |
| `signal_r_events` | Drain buffered events, optionally waiting |
| `signal_r_invoke` | Call a hub method, with or without a return value |
| `signal_r_logs` | Connection lifecycle entries |
| `signal_r_list_sessions` | Active sessions |
| `signal_r_disconnect` | Close a session |

A typical loop: `run_project` → read the listening URL → `http_invoke` / `signal_r_connect` →
`signal_r_subscribe` → `signal_r_invoke` → `signal_r_events`. This is how an agent tests a live API or a
hub without writing a client.

## Scaffolding and platform

| Tool | What it writes |
|---|---|
| `list_licenses` | The licence catalogue (id, SPDX identifier, copyleft family, OSI approval, patent grant) |
| `add_license` | A LICENSE from a bundled template — 20 licenses, MIT to AGPL and PolyForm |
| `add_contributing` | CONTRIBUTING.md, optionally with a relicensing-friendly CLA |
| `add_ci_workflow` | A GitHub Actions workflow: multi-OS build, or self-contained release binaries on a tag |
| `host_system` | Reports OS, architecture, newline convention, case sensitivity, and the line ending a path resolves to |
| `fix_line_endings` | Normalises line endings at file/directory/project/solution scope; preview by default |

`fix_line_endings` is for a deliberate migration. Every other write already preserves the target file's
own convention, so you rarely need it.

## Audit and undo

- `command_log` — one row per tool call: date, tool, arguments (credential-looking values redacted).
- `edit_history` — one row per file this server wrote, with the tool, rules, before/after hashes and
  whether it is still revertible.
- `revert_edit` — restore the file to its state before one recorded edit, refusing if the file changed
  after the edit.
- `local_history` — whole-file snapshots independent of commits: `snapshot` captures each changed source
  file, `history`/`diff` walk and compare them, `restore` writes one back (the replaced text is
  snapshotted first).

These read the solution store, so they are per solution, not global.

---

Next: [Configuration reference](../reference/configuration.md)
