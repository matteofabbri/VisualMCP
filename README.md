# VisualMCP

An MCP (Model Context Protocol) server that lets Claude Code read, navigate, and analyse Visual Studio solutions as if it were Visual Studio — using Roslyn's `MSBuildWorkspace` for full semantic understanding.

## How it works — nothing leaves your machine

When you install this plugin, you are installing a **local executable**. Claude Code launches it as a child process on your own PC and communicates with it over stdin/stdout. There is no remote server, no upload, no network connection involved.

```
Your machine
┌─────────────────────────────────────────────────────┐
│                                                     │
│   Claude Code ──stdin/stdout──▶ VisualMCP   │
│                                       │             │
│                                  reads your         │
│                                  local files        │
│                                       │             │
│                               C:\REPOSITORY\...     │
└─────────────────────────────────────────────────────┘
                        │
                        │  only this reaches Anthropic
                        ▼
              the text of the conversation
              (never your source files)
```

The only thing that reaches Anthropic's servers is the **text of the chat** — what you type and what Claude replies. Your source files stay on your local disk. Claude sees only the structured JSON results the server produces (symbol names, line numbers, diagnostics) — not the raw file contents, unless you explicitly call `read_file`.

This is the same model as a Language Server Protocol (LSP) server: when Visual Studio analyses your code it runs a local process (`roslyn.exe`, `omnisharp`, etc.) that reads files and replies to the IDE. Nothing is sent anywhere.

> **Running it remotely would break it** — the server opens paths like `C:\REPOSITORY\...` directly on the filesystem. If the process ran on a different machine those paths would not exist. The stdio transport is intentionally local-only.

## Features

| Tool | Description |
|------|-------------|
| `list_solutions` | Scan a directory tree for `.sln` / `.slnx` files |
| `load_solution` | Open a solution with Roslyn MSBuildWorkspace (loads the full semantic model) |
| `get_project_info` | Documents, project/assembly references, and NuGet packages for a project |
| `read_file` | Read a source file with line numbers and optional range |
| **Navigation** | |
| `find_symbol` | Semantic search for any named symbol across the loaded solution |
| `find_references` | Every location where a symbol is used (not just declared) |
| `find_implementations` | All types that implement an interface, with per-member mapping |
| `find_derived_types` | Downward inheritance tree for a class or interface |
| `find_callers` | Call hierarchy — every method that calls a given method |
| `get_symbol_info` | Resolve the symbol at a specific file + line (like hovering in VS) |
| `get_type_members` | All members of a type with full signatures and XML docs |
| **Analysis** | |
| `get_diagnostics` | Compiler errors and warnings (equivalent to VS Error List) |
| `analyze_dependencies` | Project dependency graph, cycle detection, root projects |
| `find_unused_symbols` | Public/internal symbols with zero references (dead code) |
| `get_metrics` | Cyclomatic complexity, lines of code, nesting depth per method |
| `find_code_smells` | Static detection: `async void`, empty catch, await-in-lock, long methods, etc. |
| **Documentation** | |
| `get_xml_docs` | XML documentation comment for a symbol, parsed into structured fields |
| `find_undocumented_public_api` | Public/protected symbols missing XML docs |
| **Refactoring** | |
| `preview_rename` | Preview a symbol rename across the solution without applying it |
| `extract_method_candidates` | Identify code blocks in long methods suitable for extraction |
| **Testing** | |
| `run_tests` | Execute tests via `dotnet test`, parse TRX output, return structured results |
| `get_test_coverage_map` | Per-class and per-method line coverage via XPlat Code Coverage |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows (MSBuildLocator discovers the SDK installed via `dotnet.exe`)

## Installation

The install script builds a stable single-file Release executable, places it in
`%USERPROFILE%\.claude\mcp-servers\VisualMCP\app\`, and registers it with Claude
Code at the **scope you choose**. After any install/uninstall, **restart Claude
Code** so it reloads its configuration.

### Choose a scope

| Scope | Availability | Stored in | Use when |
|-------|--------------|-----------|----------|
| `user` *(default)* | Every folder on this machine — global | `%USERPROFILE%\.claude.json` | You want it everywhere |
| `local` | Only the chosen project folder, private to you | `%USERPROFILE%\.claude.json` (keyed to that folder) | You want it for one project without affecting others or your team |
| `project` | The chosen project, **shared** via a committed file | `.mcp.json` in the project | You want teammates to get it too |

```powershell
# Global (default) — available in every project
powershell -ExecutionPolicy Bypass -File scripts\install.ps1

# Just this one project, private to you (run from the project, or pass -ProjectDir)
cd C:\REPOSITORY\MyApp
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope local

# Shared with the team via a committed .mcp.json
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp
```

`scripts\install-global.ps1` is a shortcut for `install.ps1 -Scope user`.

To remove it (match the scope you installed with):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope user  -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope local -Uninstall -ProjectDir C:\REPOSITORY\MyApp
```

> **`project` scope and teams:** the generated `.mcp.json` records this machine's
> absolute path to the published `.exe`. Teammates on other machines must run the
> script themselves (the path won't exist for them). For a fully portable team
> setup, commit a `dotnet run`-based `.mcp.json` instead (see *Development* below).

---

**Why the install script is better than `dotnet run`**

| | `dotnet run --project ...` | installed exe |
|---|---|---|
| Startup | slow — compiles on every launch | fast — pre-built Release binary |
| Output | build text can corrupt the stdio protocol | clean |
| After a code change | picks it up automatically | requires re-running the install script |

The `.mcp.json` at the root of this repo points at the built **Debug** DLL for
development convenience. The install script produces a stable Release build that
is better suited for daily use.

---

### Manual quick start

If you want to run it without the script, build it:

```powershell
dotnet build src/VisualMCP/VisualMCP.csproj
```

Then add to your project's `.mcp.json` (point at the built DLL — running the
assembly directly avoids `dotnet run`'s build output corrupting the protocol):

```json
{
  "mcpServers": {
    "VisualMCP": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "C:\\REPOSITORY\\VisualMCP\\src\\VisualMCP\\bin\\Debug\\net10.0\\VisualMCP.dll"
      ]
    }
  }
}
```

The solution in the working directory is **auto-discovered and loaded on demand**,
so you can call any tool right away — no explicit `load_solution` step needed.

### Use the `/vs-load` skill

```
/vs-load C:\REPOSITORY\MyApp\MyApp.sln
```

Or scan a directory:

```
/vs-load C:\REPOSITORY\MyApp
```

## Tool reference

### `list_solutions`

```
rootPath   – directory to scan (e.g. C:\REPOSITORY)
maxDepth   – recursion depth (default: 3)
```

### `load_solution`

```
path       – absolute path to the .sln or .slnx file
```

Loads the solution into memory. **Usually unnecessary** — tools auto-discover and load the working-directory solution on first use. Call this only to target a specific `.sln`/`.slnx` by path, or if a tool reports none could be located. Returns project list and workspace diagnostics (MSBuild warnings/errors).

### `get_project_info`

```
nameOrPath – project name (as returned by load_solution) or absolute .csproj path
```

Returns: assembly name, language, output type, source file list, project references, assembly references, NuGet packages.

### `find_symbol`

```
symbolName   – name to search for
partialMatch – if true, matches any symbol whose name *contains* symbolName (default: false)
```

Uses `SymbolFinder.FindSourceDeclarationsAsync` — semantically correct, not regex.

### `find_implementations`

```
interfaceName – full or partial interface name (e.g. "IMyService" or "MyApp.IMyService")
```

Returns every implementing type and, for each, which member satisfies each interface contract (including explicit implementations).

### `run_tests`

```
projectFilter – optional: run only projects whose name contains this string
extraArgs     – optional: forwarded verbatim to dotnet test
                (e.g. "--no-build", "--configuration Release")
```

Runs `dotnet test`, parses all `.trx` files produced, and returns:
- Summary: total / passed / failed / skipped / duration
- Per-project breakdown
- Failed test details: test name, error message, stack trace

### `read_file`

```
path     – absolute or relative file path
fromLine – first line to read (1-based, optional)
toLine   – last line to read (1-based, optional)
```

## Architecture

```
Program.cs
  └── MSBuildLocator.RegisterDefaults()   ← must run before any Roslyn JIT
  └── MCP host (stdio transport)

Workspace/
  └── RoslynWorkspaceService              ← singleton; holds MSBuildWorkspace + Solution

Tools/
  ├── ListSolutionsTool                   ← filesystem scan only, no Roslyn
  ├── LoadSolutionTool                    ← calls RoslynWorkspaceService.LoadSolutionAsync
  ├── GetProjectInfoTool                  ← reads from CurrentSolution
  ├── FindSymbolTool                      ← SymbolFinder.FindSourceDeclarationsAsync
  ├── FindImplementationsTool             ← SymbolFinder.FindImplementationsAsync
  ├── RunTestsTool                        ← dotnet test + TRX parse
  └── ReadFileTool                        ← plain file read, no Roslyn

Parsing/                                  ← legacy XML parsers (kept for NuGet package
  ├── SlnParser.cs                          data not exposed by Roslyn API)
  ├── SlnxParser.cs
  └── CsprojParser.cs
```

### Key design decisions

- **MSBuildLocator must be called before any Roslyn type is JIT-compiled.** `Program.cs` calls it at the top of `Main`, then delegates to a `[MethodImpl(NoInlining)]` method to prevent the JIT from eagerly compiling Roslyn references.
- **`RoslynWorkspaceService` is a process-lifetime singleton.** Loading a new solution disposes the previous workspace. This matches the single-session MCP model.
- **`Microsoft.Build.*` references must carry `ExcludeAssets="runtime"`** when used alongside `MSBuildLocator`, otherwise the locator refuses to start (it enforces this via a `.targets` check).
- **NuGet packages are read from the `.csproj` XML** (via `CsprojParser`) because Roslyn's `Project` model exposes assembly/metadata references but not the original package names.

## Development

```powershell
# Build
dotnet build src/VisualMCP

# Watch (auto-rebuild on save)
dotnet watch --project src/VisualMCP build
```

The MCP server communicates over stdio — there is no HTTP endpoint to test directly. Use Claude Code with the MCP registration above, or write an integration test that sends JSON-RPC over stdin.
