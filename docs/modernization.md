# Modernization: Aspire, the target framework, the packages

Three tools keep a solution current without leaving the conversation:

| Tool | What it answers |
|---|---|
| `aspire` | "Add Aspire to this solution" — installs the templates if they are missing, creates the AppHost and ServiceDefaults projects, wires them, and compiles the result. |
| `update_framework` | "Move this solution to .NET 10" — bumps `TargetFramework`/`TargetFrameworks` in place, keeps an existing `global.json` in sync, and reverts everything when the solution stops compiling. |
| `update_solution` | "Bring the solution up to date" — the framework bump and every outdated top-level NuGet package, in one gated pass (Central Package Management aware). |

They live in `SNITCH/Languages/CSharp/Modernization/`; the MCP tools in
`src/VisualMCP/Tools/Modernization/` are a thin layer over those services. Tests are in
`src/VisualMCP.Tests/ModernizationTests.cs`.

The design rule behind all three: **plan first, edit text in place, gate on the compiler, restore
on failure.** A project file is not rewritten through an XML DOM — only the value between the tags
changes — so a version bump looks like a version bump in the diff.

---

## `aspire`

### `action='status'`

Reports, in one call:

- the SDK in use and the TFM band it belongs to (`10.0.101` → `net10.0`);
- whether `Aspire.ProjectTemplates` is installed (`dotnet new list aspire`);
- whether the `aspire` workload is installed (`dotnet workload list`);
- which projects in the loaded solution are an **AppHost** (`Aspire.AppHost.Sdk`), a
  **ServiceDefaults** project (`Microsoft.Extensions.ServiceDiscovery` / `…Http.Resilience`), or an
  **Aspire consumer** (any `Aspire.*` package);
- the raw probe output, so a disagreement between the tool and the shell is visible.

### `action='install'`

Runs `dotnet new install Aspire.ProjectTemplates` in the solution directory. No elevation.

The `aspire` **workload** is a .NET 8-era requirement — from .NET 9 the AppHost SDK arrives through
NuGet — so it is installed automatically only when the SDK is 8.x, or when `includeWorkload=true`.
A workload failure does not fail the install: it is reported, and the templates remain usable.

`force=true` reinstalls over an existing installation; without it an already-installed template pack
is reported as such and nothing runs.

### `action='add'`

Idempotent scaffolding into the loaded solution:

1. if any project already is an AppHost → stop with `alreadyPresent` (nothing is created);
2. install the templates when missing, if `installIfMissing=true` (the default);
3. `dotnet new aspire-servicedefaults` → `<solution>.ServiceDefaults` (`serviceDefaults=true`, default);
4. `dotnet new aspire-apphost` → `<solution>.AppHost`, or `appHostName`;
5. `dotnet sln <sln> add` both projects;
6. `dotnet add <appHost> reference <project>` for every **runnable** project (Exe/WinExe, Web, Worker,
   Razor, BlazorWebAssembly — tests and libraries are skipped), or only for the names passed in
   `projects`;
7. `builder.AddProject<Projects.My_App_Api>("my-app-api");` before `builder.Build()` in the AppHost's
   `Program.cs`;
8. with ServiceDefaults: a project reference from each wired project, `builder.AddServiceDefaults();`
   after the builder, and `app.MapDefaultEndpoints();` before `app.Run()`;
9. `dotnet build` of the AppHost (`verify=true`, default). On failure the **Program.cs edits are
   reverted** and the build output is returned; the projects and references are kept, since they are
   valid on their own.

The `Projects.*` member name is the project name with every non-alphanumeric character turned into
`_` (`My.App.Api` → `Projects.My_App_Api`). The resource name is the kebab-case form
(`My.App.Api` → `my-app-api`).

A `Program.cs` that does not match the expected shape (no `CreateBuilder` at the start of a line, a
multi-line builder statement, no `builder.Build()` in the AppHost) is **left untouched** and reported;
the tool never guesses where a statement ends.

---

## `update_framework`

### Which projects, which values

- Every project of the solution, or the ones named in `projectNames`.
- `TargetFramework` and `TargetFrameworks` are edited in place; `Directory.Build.props` is included
  when it holds the TFM for the projects (found walking up from the solution directory).
- Only **modern .NET** TFMs are bumped:

  | From | To (`net10.0`) | Why |
  |---|---|---|
  | `net8.0` | `net10.0` | the normal case |
  | `net8.0-windows10.0.19041.0` | `net10.0-windows10.0.19041.0` | the platform suffix is kept |
  | `net9.0-android` | `net10.0-android` | same |
  | `net8.0` with target `net10.0-windows` | `net10.0-windows` | a suffix on the target overrides the project's |
  | `netstandard2.0`, `net48` | *unchanged* | they exist to keep older consumers working |
  | `net10.0`, `net11.0` | *unchanged* | no downgrade, no rewriting what is already newer |

- `targetFramework` accepts `net10.0`, `10.0`, `net10`, and `auto`/omitted → the newest installed SDK
  band from `dotnet --list-sdks`.
- `global.json`: an **existing** file has its `sdk.version` updated to the newest installed SDK of the
  requested band (`updateGlobalJson=true`, default); `rollForward` and every other key are untouched.
  A missing `global.json` is never created unless `createGlobalJson=true`.

### The gate and the rollback

`dryRun=true` (default) returns the plan: every file, the property, the old and the new value. Nothing
is written, and no `dotnet` process is started when `targetFramework` is explicit and no SDK pin is
involved — a preview is free.

With `dryRun=false`:

1. the edits are written (through the same writer as every other tool, so line endings and BOM are
   preserved and the write is recorded in the solution's edit history);
2. `buildGate=true` (default) builds the whole solution (`runAnalyzers` off — this is a compile gate);
3. on failure **every edited file is restored byte for byte** and the compiler errors are returned with
   `reverted=true`.

That is the difference between an upgrade and a gamble: a failed bump never leaves a half-upgraded
tree behind.

---

## `update_solution`

One call that does both halves, in the right order:

1. with `targetFramework` set (`'auto'` or a TFM), the framework plan from `update_framework` is
   applied first;
2. `dotnet list <sln> package --outdated` (`--include-prerelease` on request) lists the outdated
   **top-level** packages;
3. each package version is raised to its latest:

   - **Central Package Management** (`Directory.Packages.props` found walking up from the solution):
     the `<PackageVersion Include="X" Version="Y" />` rows are updated — one edit for the whole
     solution;
   - otherwise the `Version` attribute (or the child `<Version>` element) of the matching
     `PackageReference` is updated;
   - a reference whose version lives elsewhere (an MSBuild property, an inherited row) is reported in
     `notes` instead of being guessed at;
4. the solution is built; on failure **everything** — framework edits, project files, props file,
   `global.json` — is restored.

Omit `targetFramework` to update packages only. `includePrerelease` widens the candidate set.
`dryRun=true` (default) returns the full plan plus the package table.

---

## Limits, stated plainly

- **The workload needs elevation on some machines.** `aspire` installs templates without it; a
  workload failure is reported, not retried, and the templates are enough from .NET 9 on.
- **`aspire add` supports top-level `Program.cs` files.** A `Program.cs` with an explicit `Main`
  (or a builder statement split over several lines) is reported and left alone — wire it by hand.
- **One TFM band per call.** Mixed solutions are handled per project (each keeps its platform suffix),
  but a project is never multi-targeted anew; `TargetFrameworks` entries are all bumped together.
- **"Latest" is what NuGet reports.** The build gate proves the combination compiles; it does not
  prove the packages are API-compatible. Run `run_tests` afterwards — `update_solution` deliberately
  does not run tests, because a slow suite should not turn an upgrade into a timeout.
- **No rollback if the process dies mid-write.** The rollback is in-memory, per call. The edit history
  (`edit_history`, `revert_edit`) covers the edits that succeeded, file by file.
- **A `global.json` without an `sdk.version` is not modified**: inserting a pin changes how the
  repository resolves SDKs, which is the caller's decision.
