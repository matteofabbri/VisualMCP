# hooks — the routing guard

The server cannot ask a client to prefer its own tools: ZCode does not forward an MCP server's
`instructions` to the model. A model that has never been told otherwise does C# work with
`grep -rn`, `sed -n`, `cat` and `ls`. These files close that gap, and the installers add them
(`scripts\install.ps1` / `scripts/install.sh`, skip with `-NoHooks` / `--no-hooks`) or refresh them
on their own:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope zcode
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope all
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Uninstall
```

```sh
./scripts/install.sh --hooks-only --scope zcode
./scripts/install.sh --hooks-only --scope all
./scripts/install.sh --hooks-only --uninstall --scope all
```

| File | What it is | Where it is registered |
|---|---|---|
| `pre-tool-use.ps1` | Windows: a `PreToolUse` hook on the `Bash` tool. It reads the payload from stdin and answers with a deny decision naming the MCP tool that does the job | ZCode: `~/.zcode/cli/config.json` → `hooks.events.PreToolUse` + `hooks.enabled: true`. Claude Code: `~/.claude/settings.json` → `hooks.PreToolUse` |
| `pre-tool-use.py` | Linux/macOS: the same hook in Python 3 (the payload is JSON), registered as `python3 "<path>"` and only when an interpreter is found | the same two places |
| `agents-block.md` | The same routing rules stated up front, as a managed block for an instruction file | `~/.zcode/AGENTS.md` (user scope, loaded in every workspace). Claude Code does not need it: it receives the server's `instructions` |

## What is denied, and when

The hook acts only when a `.sln`/`.slnx` is visible within three parent levels of the working
directory, so a non-.NET repository keeps its shell. It then denies:

- search: `grep`, `egrep`, `fgrep`, `rg`, `ag`, `ack`, `Select-String`
- line-range reads: `sed -n`
- file reads: `cat`, `head`, `tail`, `more`, `less`, `bat`, `nl`, `tac`, `Get-Content`
- listings: `ls`, `dir`, `find`, `tree`, `Get-ChildItem`
- shell file rewrites: inline `python` with a write call, `sed -i`, `cat >` / `tee` / `echo >`

The deny reason names the replacement (`find_symbol`, `find_references`, `search_text`,
`read_file`, `read_symbol`, `list_directory`, `Edit` / `Write`, …), and says the solution is
already loaded. The block in `agents-block.md` states the same routing up front, plus why C#
semantics (overloads, overrides, generics, partial types) need Roslyn rather than text matching.

## What always passes

Anything that mentions a build or tooling command (`dotnet`, `msbuild`, `nuget`, `git`, `gh`,
`npm`, `node`, `python`, `docker`, `kubectl`, `cargo`, `go`, `make`, `mvn`, `curl`, …), a write
redirection to a file, or a command with the literal token **`vmcp-allow`**. The alternative for a
genuine shell case is the MCP tool `run_command`, which runs the same command and keeps it
visible in the plugin log.

## Design rules (do not break them)

- **Never fail a session.** Any error or unparsable payload exits 0 (pass).
- **Emit exactly the three keys** of the `PreToolUse` schema
  (`hookSpecificOutput.hookEventName`, `permissionDecision`, `permissionDecisionReason`); an extra
  key discards the whole answer.
- **Segments decide, not substrings.** `rm old && grep Foo` is still caught; `dotnet test | tail -3`
  keeps its build segment as the deciding one.
- **Hooks are read at session start.** Restart ZCode / Claude Code after installing or editing one.

The block in `agents-block.md` is delimited by `<!-- VisualMCP:begin/end -->`, so re-running the
installer replaces it in place instead of appending a second copy.
