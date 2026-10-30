[Docs home](README.md) › **Help and FAQ**

# Help and FAQ

## Troubleshooting

### The server does not appear in my client

1. **Restart the client.** MCP configuration and the tool list are read at session start.
2. Check the file for your client:
   - ZCode: `%USERPROFILE%\.zcode\cli\config.json` → `mcp.servers.VisualMCP`
   - Claude Code: `%USERPROFILE%\.claude.json`, or `.mcp.json` in the project
3. ZCode validates its schema strictly: an unknown key drops the server, and a **BOM** at the start of
   the file can make the whole config unreadable. Re-run the installer rather than hand-editing.
4. Confirm the executable exists in `%USERPROFILE%\.zcode\mcp-servers\VisualMCP\app\` (or the `.claude`
   equivalent). Re-run `scripts\install.ps1` if it does not.
5. In ZCode, look at **Settings → MCP** for the connection state and the error.

### Tools are missing after an update

The client caches the tool list at initialization. Restart the client. If a new build added or renamed
tools, the client that launched the old process still has the old list.

### "No solution could be located"

- The server auto-discovers solutions under the **working directory** the client launched it in. If that
  is not the repository root, discovery can fail.
- Call `list_solutions` to see what is visible, then `load_solution` with an absolute `.sln`/`.slnx` path.
- `check_solution` reports what is loaded right now.

### The first call is slow

The server opens the solution in the background after the client's first message. A large solution with a
cold NuGet cache can take a while; the first tool call waits for the load. Subsequent calls reuse it.
`build_project` may also run a restore, which is usually the slowest part of a cold start.

### "File is in use" when building

A running app or a previous server instance holds a DLL. Use `stop_server` to stop other VisualMCP
instances, or rely on `build_project`'s default `noCopyOutput=true`, which compiles without copying into
`bin/`.

### A refactoring was refused

The result names the reason. Common ones:

- a method group or `nameof()` use that would change meaning;
- an object initializer that a constructor-to-factory rewrite cannot express;
- a member selection that is not self-contained;
- the change would add compiler errors (the compiler gate refused it).

Prepare the code — remove the object initializer, move the member, split the overload — then retry. Use
`preview_rename` or a `dryRun` tool to see the proposal first.

### `revert_edit` refused

The file changed after the edit it would revert. Reverting would overwrite newer work, so the tool
refuses. Use `git_diff` to inspect the current state, or restore from version control.

### The index is stale

`reference_index` and `text_index` are snapshots keyed on file content. After substantial external edits:

```
reference_index  action=build
text_index       action=build
```

`reference_index(action=status)` says what is stored. Reloading the solution (`load_solution`) also
refreshes the semantic model.

### Ghidra is not found

`analyze_native_binary` (and `metacoding action=decompile` on a native binary) installs Ghidra on
demand: the latest public release is downloaded from GitHub and extracted under the per-user tools
folder (`%LOCALAPPDATA%\VisualMCP\tools` on Windows, the equivalent local data folder elsewhere). If
the download is blocked, set `GHIDRA_INSTALL_DIR` or pass `ghidraHome`/`headlessPath` and nothing is
downloaded. Ghidra still needs a JDK 21+: set `JAVA_HOME`/`JAVA_HOME_OVERRIDE` or pass `javaHome`. The
failure text names the log, and `save=false` keeps a run out of the solution database.

### GitHub tools fail

The `gh_*` tools shell out to the GitHub CLI. Install it and run `gh auth login` once. `github` is a raw
passthrough, useful for checking the error the CLI returned.

### Docker tools fail

The Docker engine must be running and reachable by the current user. `docker_status` is the diagnostic
call: it reports the engine version and the containers it can see.

### `profile_processes` does not list my app

The process must expose a .NET diagnostics IPC endpoint, run under a compatible runtime, and be visible
to the current user. A process started with diagnostics disabled will not appear.

### The agent still uses grep

MCP servers cannot force a model to use them. Claude Code receives the server's `instructions`; ZCode
does not. Install the routing guard (`scripts\install-hooks.ps1 -Scope zcode` or `all`), or copy
`hooks\agents-block.md` into your client's instruction file, then restart the client.

### A shell command is blocked by the hook

The routing guard denies read-only probes in .NET repositories. Two escape hatches:

- call `run_command` with the same command (it is the sanctioned shell path, and it is logged);
- include the literal token `vmcp-allow` in the command.

To remove the guard entirely: `scripts\install-hooks.ps1 -Uninstall`.

### The dashboard is unreachable

The URL is in the log (`Web dashboard on http://127.0.0.1:<port>`) and in `GET /api/health`. If 4677 is
taken, the server falls back to the last port it used, then to an OS-assigned one. Set
`VISUALMCP_DASHBOARD_PORT` to prefer another port, or `VISUALMCP_DASHBOARD=off` to disable it.

### Coverage is empty

`get_test_coverage_map` requires `coverlet.collector` in the test projects. Add it, restore, and run the
tool again.

### Uninstall leaves things behind

The uninstaller removes the server registration. It does not delete:

- `%USERPROFILE%\.zcode\mcp-servers\VisualMCP\` and `%USERPROFILE%\.claude\mcp-servers\VisualMCP\`
  (the published binaries);
- `<solution>\.visualMCP\` (stores, indexes, board, edit history);
- `%USERPROFILE%\.claude\projects\<slug>\memory\` (project memory).

Delete those folders explicitly if you want a clean removal.

## FAQ

**Is my source code sent anywhere?**

The server itself does not upload anything and has no remote endpoint. Your client sends the
conversation — including tool results — to its model provider. If the model calls `read_file` or
`read_symbol`, that text is part of the conversation. See [How it works](how-it-works.md#privacy).

**Does it work offline?**

Mostly. Loading, analysis, refactoring, profiling and Git are local. NuGet restore/build and upgrades, the
GitHub CLI, `download_file`, symbol downloads and the update check need the network. A solution whose
packages are already restored can be analysed offline.

**Can it change files without asking?**

Yes, for the write-through tools (`apply_rename`, `change_signature`, `safe_delete`, `generate_members`,
package tools, `git_commit`, …). The preview-first family (`simplify_code`, `apply_code_fix`,
`razor_edit`, most refactorings) defaults to a dry run. Every write is recorded in `edit_history` and can
be reverted while the file is unchanged. Use a branch and a clean working tree for anything important.

**Which languages are supported?**

C# is the main surface. Visual Basic has a style layer (`simplify_vb_code`, 15 ids) plus the
language-neutral build/test/diagnostic tools — no refactorings, inspections or formatter. Razor and
Blazor markup have a dedicated scanner. XAML/WPF/WinUI/MAUI/Avalonia has a markup layer
(`xaml_pages`, `analyze_xaml`, `xaml_navigate`) with the code-behind resolved through Roslyn, and EF
Core migrations run through `ef_migrations`. F# has no semantic support (text and build/test only).
JS/TS/HTML/CSS, SQL as a language, and Kubernetes manifests are at most full-text indexed: no parser,
no semantic analysis. The full table is
[Limits → Language and platform coverage](reference/limits.md#language-and-platform-coverage). C#,
Razor, decompiled assemblies and Ghidra snapshots are all inputs to metacode.

**Do I need Visual Studio?**

No. You need the .NET 10 SDK. Visual Studio is only relevant to `get_environment_info` (which reports it
if present) and to the post-build install hook in the project file.

**Can I use it on Linux or macOS?**

CI produces self-contained builds for Linux x64 and macOS arm64, and the server is not fundamentally
Windows-bound. Windows is the documented and fully supported platform; the installer and the routing
guard are PowerShell. Treat Linux/macOS as a build target, not a supported installation path, unless you
set up the client registration yourself.

**How many tools are there?**

312 in this revision. The live list is `capability_report` (with per-area counts), `http://127.0.0.1:4677/tools`
(everything, with schemas), or the [tool catalogue](tools.md).

**How do I update?**

`check_for_update` then `self_update` from inside the client, or re-run the install script after pulling.
Restart the client if it does not reconnect by itself.

**Can I use it in CI?**

The server is a stdio MCP server, so it needs an MCP client to drive it. The underlying operations
(`build_project`, `run_tests`, `inspect_code`, `simplify_code`, …) are ordinary local processes and can
be used in a pipeline if you have a client harness. Nothing requires an account or a hosted service.

**Is it free?**

Source-available under the PolyForm Noncommercial License 1.0.0: free for noncommercial use. Commercial
or company use requires a separate paid license. See [LICENSE](../../LICENSE).

**How do I report a bug?**

Open an issue at <https://github.com/matteofabbri/VisualMCP/issues>. Include:

- the tool name and the arguments you passed;
- the exact error or an excerpt of the result;
- the relevant tail of `%USERPROFILE%\.claude\mcp-servers\VisualMCP\debug.log` (redact secrets and paths
  you do not want public);
- the solution type (SDK-style, legacy, multi-targeted) and the .NET SDK version.

"The refactoring refused" is usually correct behaviour; include the refusal message, because it names the
blocker.

---

Next: [Contributing](contributing.md)
