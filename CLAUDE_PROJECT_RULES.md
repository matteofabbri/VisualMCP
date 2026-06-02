# CLAUDE_PROJECT_RULES

Rules and conventions for Claude Code when working on **VsSolutionPlugin**.

## What this project is

An MCP server written in C# (.NET 10) that exposes Visual Studio solution analysis to Claude via the Model Context Protocol. It uses Roslyn's `MSBuildWorkspace` to load solutions semantically, exactly as Visual Studio does.

---

## Build & test

```powershell
dotnet build src/VisualMCP/VisualMCP.csproj
```

Always build before committing. The MCP server process locks the output `.exe` when running — kill it first:

```powershell
Stop-Process -Name VisualMCP -Force -ErrorAction SilentlyContinue
```

There are no automated tests in this repo yet. Verify changes manually via Claude Code with the MCP server registered.

---

## Project layout

```
src/VisualMCP/
  Program.cs                  ← startup; MSBuildLocator.RegisterDefaults() MUST be first
  Workspace/
    RoslynWorkspaceService.cs ← the singleton that owns the loaded Solution
  Tools/
    *.cs                      ← one file per MCP tool
  Parsing/
    *.cs                      ← legacy XML parsers (kept for NuGet package names)
```

---

## Strict rules

### 1. MSBuildLocator before everything
`MSBuildLocator.RegisterDefaults()` must remain the **first statement** in `Program.cs`, before any method that could cause Roslyn/MSBuild types to be JIT-compiled. The `RunServerAsync` method must keep its `[MethodImpl(NoInlining)]` attribute. Do not move or inline this code.

### 2. One tool per file
Every `[McpServerToolType]` class lives in its own file under `Tools/`. The file name matches the class name.

### 3. Tools always guard `CurrentSolution`
Every tool that depends on a loaded solution must begin with:
```csharp
var solution = RoslynWorkspaceService.Instance.CurrentSolution;
if (solution is null)
    return new { error = "No solution loaded. Call load_solution first." };
```

### 4. Never catch-all silently
Do not use empty `catch { }` blocks except in cleanup/`finally` paths clearly marked `/* best-effort */`. Surface real errors to the caller.

### 5. `Microsoft.Build.*` packages need `ExcludeAssets="runtime"`
Any direct `PackageReference` to a `Microsoft.Build.*` package must include `ExcludeAssets="runtime"`, or `MSBuildLocator`'s `.targets` check will break the build.

### 6. Return anonymous objects, not strings
Tool methods return `object` (serialised to JSON by the MCP SDK). Always return structured anonymous objects, never raw strings or `ToString()` dumps.

### 7. NuGet packages come from `CsprojParser`, not Roslyn
Roslyn's `Project.MetadataReferences` contains assembly paths, not package names. Use `CsprojParser.Parse(project.FilePath)` when the caller needs NuGet package names/versions.

---

## Adding a new tool

1. Create `src/VisualMCP/Tools/MyNewTool.cs`
2. Decorate the class with `[McpServerToolType]` and the method with `[McpServerTool, Description("...")]`
3. Make the class `static` (no DI needed — use `RoslynWorkspaceService.Instance` directly)
4. Return `object` (or `Task<object>` for async)
5. Build and verify — the MCP SDK discovers tools via assembly scanning, no registration needed
6. Document the new tool in `README.md` (tool reference table + parameter list)

---

## Dependency rules

| Package | Reason | Notes |
|---------|--------|-------|
| `Microsoft.Build.Locator` | Finds the .NET SDK MSBuild at runtime | Must call `RegisterDefaults()` before any Roslyn type loads |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | `MSBuildWorkspace.OpenSolutionAsync` | Core Roslyn workspace API |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | C#-specific syntax/semantic APIs | |
| `ModelContextProtocol` | MCP server SDK | `WithToolsFromAssembly()` scans for `[McpServerToolType]` |
| `Microsoft.Extensions.Hosting` | Generic host / DI / logging | |

Do not add NuGet packages without a concrete reason. Prefer .NET BCL APIs over third-party libraries for parsing (XML, JSON, regex).

---

## Commit conventions

- Imperative subject line: `Add ...`, `Fix ...`, `Refactor ...`
- Body explains *why*, not just *what*
- Always include `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>` when Claude authored the commit
- Kill the running MCP server process before building so the `.exe` is not locked

---

## Known constraints

- **Everything runs locally — no upload.** The server is a child process launched by Claude Code on the user's machine via stdin/stdout (stdio transport). Source files are read directly from the local filesystem and never sent anywhere. The only data that reaches Anthropic is the text of the conversation. This also means the server cannot be deployed as a remote service without a filesystem mount, because it opens absolute paths like `C:\REPOSITORY\...` directly.
- **Windows only** — `MSBuildLocator.RegisterDefaults()` discovers the SDK via `dotnet.exe` on the PATH. Linux/macOS is untested.
- **Single active solution** — `RoslynWorkspaceService` holds one `Solution` at a time. Parallel MCP sessions would interfere; this is acceptable for the current single-user MCP model.
- **No incremental reload** — changing a file on disk is not automatically reflected; the caller must call `load_solution` again.
- **Test discovery is heuristic** — `RunTestsTool` identifies test projects by checking for `Microsoft.NET.Test.Sdk` in the `.csproj` XML. Projects that add the SDK indirectly (e.g. via `Directory.Build.props`) may not be detected.
