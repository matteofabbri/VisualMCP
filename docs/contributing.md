[Docs home](README.md) › **Contributing**

# Contributing

VisualMCP is **source-available** under the [PolyForm Noncommercial License 1.0.0](../LICENSE): free for
noncommercial use, with commercial use requiring a separate paid license. Contributions are welcome
under the terms in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Before you start

- Open an issue for anything non-trivial, so the approach can be agreed before code is written.
- Fork, branch, implement, build, test.
- Keep the architecture described below.

## Build and test

```powershell
dotnet build src/VisualMCP/VisualMCP.csproj -c Release
dotnet test  src/VisualMCP.Tests/VisualMCP.Tests.csproj -c Release
```

CI builds and tests on Windows, Linux and macOS with the .NET 10 SDK, and publishes a Release artifact per
platform.

## Repository layout

| Path | What it is |
|---|---|
| `src/VisualMCP` | The MCP host: tool declarations, dashboard, startup, client glue |
| `src/VisualMCP/Tools/<Area>` | Thin `[McpServerTool]` shims, grouped by area |
| `src/VisualMCP/Implementation`, `Analysis`, `Execution`, … | MCP-side services that are not part of the reusable engine |
| `SNITCH/` | The code-intelligence engine: workspace, languages, style, search, storage, git/docker/github |
| `SNITCH/Libraries/Snitch.Metacode` | The metacode model, frontends, renderers, decompilation |
| `SNITCH/Libraries/Snitch.Profiling` | EventPipe/ClrMD profiling |
| `src/VisualMCP.Tests` | xunit suite (300+ test methods) |
| `docs/` | This documentation: the user guide (`guides/`, `reference/`) and the engineering notes |

## Architecture rules

1. **A tool is a thin shim.** The `[McpServerTool]` method parses arguments, delegates to a service, and
   formats the answer. Business logic does not live in `Tools/`.
2. **Language-specific tools live under `Tools/CSharp` and `Implementation/CSharp`**; language-agnostic
   tools stay at the top level.
3. **The engine is language-neutral** where the operation is generic. The style engine is a shared core
   (`SNITCH/Languages/Style`) plus a per-language layer; adding Visual Basic did not require a second
   engine. Follow that split when adding a language.
4. **The solution store holds only derived, reconstructible data**, keyed on a freshness token (content
   hash, mtime, MVID). If a value cannot be rebuilt, it does not belong there.
5. **Writes go through the shared file writer**, which preserves BOM and line endings and records the
   previous text for `edit_history`/`revert_edit`.
6. **Prefer `System.Collections.Concurrent` and `Interlocked` to `lock`** in server code. The server is
   concurrent by design.
7. **Tests are part of the change.** A rule or refactoring gets a case that must fire and a case that must
   not.

## Adding a tool

1. Create `Tools/<Area>/MyTool.cs` with `[McpServerToolType]` on the class and `[McpServerTool(Name =
   "my_tool")]` on the method. Tools are registered by `WithToolsFromAssembly` — no list to update.
2. Write the description for the **model**, not for a changelog: what question it answers, what it
   returns, and what it refuses to do.
3. Give every parameter a `[Description]`. The schema is the API.
4. Delegate to a service; keep the shim small.
5. Add a test. If the tool writes files, test the dry run and the edit history.

Use snake_case names. Avoid a new tool when an existing one can take an argument: the catalogue is
already large, and a clear verb with a mode is easier to route to than two near-synonyms.

## Adding a style rule

1. Implement the rewrite (`StyleRewrite`) and register it in the language's rule set.
2. Remove the id from the `NotRewritten` list, or update its reason.
3. Add a rule case to the shared test table: one input that must change and one that must not.
4. Update [Limits](reference/limits.md) if the coverage numbers change.

The rewrite must be verified by the compiler gate and recorded in the edit history. Rules that cannot be
expressed as a single contiguous region in one document do not belong in `simplify_code`; write a
dedicated refactoring instead.

## Pull requests

- Describe the change and **why**; link the issue.
- Keep unrelated formatting out of the diff.
- Note the user-visible behaviour: a new tool, a changed default, a new limit.
- Update the public documentation when a tool, a default or a limit changes.

## Contributor License Agreement

Because the project is dual-licensed (noncommercial and commercial), contributions must grant the
maintainer the rights needed to keep doing that. By submitting a pull request, patch or other content,
you agree that:

1. you have the right to contribute it;
2. you grant **Matteo Fabbri** a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license,
   with the right to sublicense and relicense, to use, reproduce, modify, distribute and license your
   contribution under any terms, including the PolyForm Noncommercial license and separate commercial
   terms;
3. you retain copyright;
4. the contribution is provided "as is", without warranty.

If you do not agree, do not submit a contribution. The full text is in
[CONTRIBUTING.md](../CONTRIBUTING.md); it is provided for convenience and is not legal advice.

---

[Docs home](README.md) · [Getting started](getting-started.md) · [Limits](reference/limits.md)
