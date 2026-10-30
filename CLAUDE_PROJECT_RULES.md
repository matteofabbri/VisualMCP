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
src/VisualMCP/                ← the MCP host (thin tool layer over Snitch)
  Program.cs                  ← startup; MSBuildLocator.RegisterDefaults() MUST be first
  Tools/**/*.cs               ← one file per MCP tool; forwards to a Snitch service
src/Snitch/                   ← the code-intelligence library (the engine)
  SnitchContext.cs            ← the environment: owns every loaded solution + the active
                                 one; IS the IRoslynWorkspace
  Workspace/                  ← IRoslynWorkspace, SolutionDiscovery, doc indexing
  Languages/CSharp/, Git/, …  ← the I*Service implementations
```

---

## Strict rules

### 1. MSBuildLocator before everything
`MSBuildLocator.RegisterDefaults()` must remain the **first statement** in `Program.cs`, before any method that could cause Roslyn/MSBuild types to be JIT-compiled. The `RunServerAsync` method must keep its `[MethodImpl(NoInlining)]` attribute. Do not move or inline this code.

### 2. One type per file
Every file declares one top-level type — class, struct, interface, record, enum or delegate — and the
file name is that type's name. Nested types stay in their container. A family of related types is a
folder of one-type files, not one aggregate `.cs`.

The rule is enforced, not just written: `inspect_code` runs the in-process **house** pack, whose
`VMC0001` (reported as `WEAK_WARNING`) flags a file that declares more than one type, and
`solution_audit` includes the same pack. New files must comply. The tree still holds file-per-family
aggregates written before the rule; record them once with `inspection_baseline(action='create')` so
`action='check'` fails only on a violation a change introduces, and split an aggregate as you touch it.

### 3. One tool per file
Every `[McpServerToolType]` class lives in its own file under `Tools/`. The file name matches the class name.

### 4. Resolve the solution through `IRoslynWorkspace`
There is no global workspace instance. Code that needs the loaded solution injects
`IRoslynWorkspace` (the active solution of the opened environment — implemented by
`SnitchContext`) and calls `EnsureSolutionLoadedAsync()`, guarding null:
```csharp
var solution = await _workspace.EnsureSolutionLoadedAsync();
if (solution is null)
    return new { error = "No C# solution could be auto-located. Provide a path to load_solution." };
```

### 5. Never catch-all silently
Do not use empty `catch { }` blocks except in cleanup/`finally` paths clearly marked `/* best-effort */`. Surface real errors to the caller.

### 6. `Microsoft.Build.*` packages need `ExcludeAssets="runtime"`
Any direct `PackageReference` to a `Microsoft.Build.*` package must include `ExcludeAssets="runtime"`, or `MSBuildLocator`'s `.targets` check will break the build.

### 7. Return a typed result, not an anonymous object and not a string
A tool returns structured data (the MCP SDK serialises it), never a raw string or a
`ToString()` dump. Structured data means a **named result type the engine declares**, not an
anonymous object: Snitch is a library, so a caller must be able to name what a service
returns and read it through the compiler. A service declares one result record per operation
next to its interface — see `SNITCH/IO/IoResults.cs` for the pattern:

```csharp
public sealed record ReadFileResult : IoResult
{
    public string? Path { get; init; }
    public int? TotalLines { get; init; }
    // …
}

ReadFileResult ReadFile(string path, int? fromLine, int? toLine);
```

A failure travels in `IoResult.Error` (a nullable member, never an exception the tool has to
unwrap), and every member the failure leaves unset is nullable, so the serializer's
`WhenWritingNull` default still reduces a failed call to `{"error":"…"}`. The MCP server
serialises camelCase, which is what the result records' PascalCase members produce.

Older services still return anonymous objects and a tool binds them through
`Formatting/EngineJson.cs`. Do not copy that shape into new code; when you touch one of those
services, give it a result record instead.

The exception is a payload that *is* text the model must read as text — file contents, search
matches, a symbol's source. Serialising those turns every newline into `\n` and every quote into
`\u0022`, so the model reads one long escaped line instead of code, and `search_text` payloads grow
by ~80-120%. Those tools return `CallToolResult` with a single `TextContentBlock` (see
`Formatting/ToolText.cs`: `ToolText.Ok` / `ToolText.Fail`). Keep the tool's *metadata* structured;
never wrap source code in JSON.

### 8. NuGet packages come from `CsprojParser`, not Roslyn
Roslyn's `Project.MetadataReferences` contains assembly paths, not package names. Use `CsprojParser.Parse(project.FilePath)` when the caller needs NuGet package names/versions.

---

## Adding a new tool

1. Create `src/VisualMCP/Tools/MyNewTool.cs`
2. Decorate the class with `[McpServerToolType]` and the method with `[McpServerTool, Description("...")]`
3. Keep the class `static`; inject the Snitch `I*Service` (or `SnitchContext`) you need as a method parameter — the MCP SDK resolves it from DI. Put the logic in the service, not the tool
4. Return the engine's result type (`Task<TResult>` for async) — a record the service declares, never `object` (see rule 6)
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
- **Environment model** — `SnitchContext` opens an *environment*: it discovers and loads **every** solution under the working directory, exposing one **active solution** (switchable via `load_solution`) that tools operate on through `IRoslynWorkspace`. State is process-wide, so parallel MCP sessions would interfere; acceptable for the single-user MCP model.
- **No incremental reload** — changing a file on disk is not automatically reflected; the caller must call `load_solution` again.
- **Test discovery is heuristic** — `RunTestsTool` identifies test projects by checking for `Microsoft.NET.Test.Sdk` in the `.csproj` XML. Projects that add the SDK indirectly (e.g. via `Directory.Build.props`) may not be detected.
