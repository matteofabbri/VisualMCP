[Docs home](../README.md) › Guides › **Build, test, packages and modernization**

# Build, test, packages and modernization

The tools that close the loop after an edit: compile, run, test, measure, upgrade.

## Build

```
build_project  projectName=MyApp.Api
```

Runs `dotnet build` and returns **structured** errors and warnings — id, severity, file, line, message —
instead of a log tail to re-read. Defaults worth knowing:

- `restore=true` runs a NuGet restore first.
- `runAnalyzers=true` also runs the configured Roslyn analyzers, so the build doubles as a code-style
  pass.
- `noCopyOutput=true` compiles without copying files to `bin/`, which is what lets it work while the app
  is running.

If the build fails with a "file in use" error, `stop_server` releases the lock held by a VisualMCP
instance, or `build_project` with `noCopyOutput=true` avoids the copy.

Related:

| Tool | Use |
|---|---|
| `get_diagnostics` | The compiler's current view, without a build |
| `run_code_analysis` | Analyzer diagnostics only |
| `extract_build_log_errors` | Pull errors out of a native/MSBuild log file, including UTF-16 logs |
| `get_environment_info` | Which SDKs, runtimes, Visual Studio and `cl.exe` are installed |

## Run

```
run_project  projectName=MyApp.Api timeoutSeconds=15
```

Launches `dotnet run`, captures output and returns when the timeout expires. For a web app the startup
output includes the listening URL, which is exactly what you need before calling `http_invoke` or
`signal_r_connect` against it. If the solution has a single runnable project, omit `projectName`.

`run_command` is the general escape hatch: PowerShell, pwsh, cmd or bash (PowerShell by default on
Windows, bash elsewhere; `cmd` and Windows PowerShell are refused off Windows), with pipes, redirection,
nested shells and scripts of any length. The process tree is killed at the timeout. It runs with the
server's privileges and is not sandboxed — prefer a dedicated tool when one exists.

## Test

```
run_tests
run_tests  projectFilter=MyApp.Tests extraArgs="--configuration Release"
```

Runs `dotnet test`, parses every TRX file produced and returns:

- totals — passed, failed, skipped, duration;
- a per-project breakdown;
- each failure with test name, error message and stack trace.

```
get_test_coverage_map  minCoverage=60
```

Runs the suite with XPlat Code Coverage and returns per-class and per-method **line coverage**. It
requires `coverlet.collector` in the test projects; without it, the tool reports that rather than
inventing numbers.

## Benchmark

| Tool | Use |
|---|---|
| `run_benchmarks` | Build and run an existing BenchmarkDotNet project in Release, return the summary table; filter with `*Name*` |
| `quick_benchmark` | Benchmark an ad-hoc snippet without writing a `[Benchmark]` class; optional project reference and setup code |

`quick_benchmark` is the fast path: it generates a temporary BenchmarkDotNet project, references the
project you name, runs it and returns time and allocations. Use `quick=false` for a full run.

For a running process, use `profile_*` ([Profiling](profiling.md)) rather than a microbenchmark: measured
cost under real load is a different question.

## NuGet packages

| Goal | Tool |
|---|---|
| See what is installed, per target framework, and find version conflicts | `list_nuget_packages` |
| Find available upgrades | `check_nuget_updates` |
| Find known vulnerabilities | `check_nuget_vulnerabilities` |
| Fix vulnerable top-level packages | `fix_nuget_vulnerabilities` |
| Find where a package's DLLs live | `locate_nuget_package` |
| Read a package's public API | `inspect_package` |
| Decompile one type from a package | `decompile_package_type` |
| Add, remove or update one package | `add_nuget_package`, `remove_nuget_package`, `update_nuget_package` |
| Force one version across projects | `unify_nuget_version` |

`list_nuget_packages` reports requested and **resolved** versions and flags the same package pinned to
different versions across projects. Vulnerability results include the advisory id, severity and link,
and include transitive packages by default — most advisories hit indirect dependencies.

`inspect_package` and `decompile_package_type` are one-shot: give a package id and version, and they
locate the cached DLL, then list its API or decompile a type. No solution needed.

## Modernization

Three tools, all preview-first and all with a **build gate that rolls back on failure**.

### `aspire`

```
aspire  action=status
aspire  action=install
aspire  action=add  appHostName=MyApp.AppHost projects=[MyApp.Api,MyApp.Worker]
```

- `status` reports the SDK band, whether the Aspire templates and workload are present, and whether an
  AppHost/ServiceDefaults already exist.
- `install` runs `dotnet new install Aspire.ProjectTemplates` (no elevation); the workload is installed
  on .NET 8 or on request.
- `add` creates the AppHost and ServiceDefaults projects, adds solution entries and project references,
  writes `AddProject<T>("name")` lines and `AddServiceDefaults()`/`MapDefaultEndpoints()`, then builds
  the AppHost. If the wiring does not compile, the `Program.cs` edits are reverted.

Limits: the workload may need elevation on some machines (reported, not retried); `add` supports
top-level `Program.cs` files — one with an explicit `Main` is reported and left alone.

### `update_framework`

```
update_framework  targetFramework=net10.0 dryRun=true
```

Moves the projects to a newer target framework:

- edits TFM values in place, keeping platform suffixes (`-windows`, `-android`, …);
- leaves `netstandard` and `net4x` alone;
- keeps `global.json` in sync;
- preview by default (`dryRun=true` returns the plan: file, property, from, to);
- with `dryRun=false`, builds the whole solution and, on failure, **restores every edited file byte for
  byte**.

A `global.json` without an `sdk.version` is not modified: inserting a pin changes how the repository
resolves SDKs, which is the caller's decision.

### `update_solution`

```
update_solution  targetFramework=net10.0 dryRun=true
```

The framework bump plus every outdated **top-level** NuGet package, in the right order:

1. apply the framework plan (if `targetFramework` is set);
2. list outdated packages with `dotnet list package --outdated`;
3. raise each one — in `Directory.Packages.props` rows when Central Package Management is in use, in the
   matching `PackageReference` otherwise; a version that lives elsewhere is reported in `notes` instead
   of guessed;
4. build; on failure restore framework edits, project files, the props file and `global.json`.

Omit `targetFramework` to update packages only. `dryRun=true` is the default and returns the full plan.

### What the upgrade tools do not claim

- **"Latest" is what NuGet reports.** The build gate proves the combination compiles; it does not prove
  API compatibility. Run `run_tests` afterwards — `update_solution` deliberately does not run tests,
  because a slow suite should not turn an upgrade into a timeout.
- **One TFM band per call.** Mixed solutions are handled per project, and `TargetFrameworks` entries move
  together; a project is not newly multi-targeted.
- **Rollback is in-memory, per call.** If the process dies mid-write, the edit history still lets you
  revert the writes that succeeded, file by file.

## A typical loop

```
build_project        → structured errors
apply_code_fix       → fix one finding
build_project        → verify
run_tests            → verify behaviour
inspect_code         → catch what the compiler does not
quick_benchmark      → check a hot path did not regress
```

For a mechanical fan-out — the same `simplify_code` over ten documents, then a build — use `run_plan`
instead of ten round trips. See [Workflow and integrations](workflow.md#run_plan).

---

Next: [Profiling](profiling.md)
