# VisualMCP.Tests — the test suite

One xUnit project (`net10.0`) covering the engine and the host. It references `VisualMCP`, which
transitively brings in `Snitch`, `Snitch.Metacode` and `Snitch.Profiling`, so a test can exercise a
service directly instead of going through MCP.

```powershell
# Everything
dotnet test src/VisualMCP.Tests/VisualMCP.Tests.csproj

# One class / one test
dotnet test src/VisualMCP.Tests/VisualMCP.Tests.csproj --filter "FullyQualifiedName~SimplifyCodeTests"
dotnet test src/VisualMCP.Tests/VisualMCP.Tests.csproj --filter "FullyQualifiedName~ReadSymbol"

# From an MCP client, the same run as structured TRX:
# run_tests { projectFilter: "VisualMCP.Tests" }
```

## Shared helpers

| File | What it gives a test |
|---|---|
| `FakeWorkspace.cs` | a real `IRoslynWorkspace` over real files in a temp folder (`AdhocWorkspace` + files on disk), so services write through their normal path and the test reads the result from disk; variants add metadata references and an `.editorconfig` |
| `TestBootstrap.cs` | a `[ModuleInitializer]` that initialises SQLite once, removing the native-library extraction race between parallel classes |
| `TestGitRepo.cs` | a throwaway git repository in a temp folder (real `git`, commits, branches, `Run` for commands expected to fail), shared by the git, conflict, rebase-plan, patch and review tests |
| `StyleRewriteDiagnostics.cs` | rule-case helper: runs a `StyleRewrite` matcher over a snippet and fails with the rule id and the source instead of a generic "filesChanged was 0" |

## Layout

The files are grouped by area, not by layer: `Refactoring*`, `SimplifyCode*`, `VbStyleTests`,
`Metacode*`, `Profiling*`, `Razor*`, `XamlTests`, `Navigation*`, `Complexity*`, `Inspection*`,
`GitServiceTests`, `GitConflictTests`, `GitRebasePlanTests`, `GitPatchTests`, `RemoteHostTests`,
`GitHubReviewPayloadTests`, `ChangeReviewTests`, `BoardTests`, `PlanExecutionTests`,
`DashboardTests`, `OneTypePerFileTests`, `ProductSurfaceTests`, and so on. When you add a rule, a
refactoring or a tool with behaviour beyond argument forwarding, add a row to the matching file rather
than a new catch-all file.

## Conventions

- **Temp directories only.** `FakeWorkspace` allocates a fresh folder per instance; do not write
  into the repository from a test.
- **Assert the behaviour, not the spelling.** Style and refactoring tests compile the rewritten
  source and check the observable shape (and often that the project still compiles), because the
  formatter can change whitespace.
- **One rule, one case.** A style rule is proven by a case that fires and a case that must not.
- **No server process.** Tests call services in-process; the MCP transport, the dashboard and
  process profiling have their own integration-style tests.

## Two traps that cost time

**The test host locks `bin`.** If another session (or a previous run) left a `testhost` alive, a
build fails with a file-in-use error. Run the suite with a separate artifacts directory:

```powershell
dotnet test src/VisualMCP.Tests/VisualMCP.Tests.csproj --artifacts-path .artifacts-test
```

**A test that decompiles the running assembly can crash the host.** `decompile_*` tests point at a
copy or a separate assembly, never at the test assembly itself.
