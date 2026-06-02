# VsSolutionPlugin

An MCP (Model Context Protocol) server that lets Claude Code read, navigate, and analyse Visual Studio solutions as if it were Visual Studio — using Roslyn's `MSBuildWorkspace` for full semantic understanding.

## Features

| Tool | Description |
|------|-------------|
| `list_solutions` | Scan a directory tree for `.sln` / `.slnx` files |
| `load_solution` | Open a solution with Roslyn MSBuildWorkspace (loads the full semantic model) |
| `get_project_info` | Return documents, project/assembly references, and NuGet packages for a project |
| `find_symbol` | Semantic search for any named symbol across the loaded solution |
| `find_implementations` | Find every type that implements an interface, with per-member mapping |
| `run_tests` | Execute all tests via `dotnet test`, parse TRX output, return structured results |
| `read_file` | Read a source file with line numbers and optional range |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows (MSBuildLocator discovers the SDK installed via `dotnet.exe`)

## Quick start

### 1. Build

```powershell
dotnet build src/VsSolutionServer/VsSolutionServer.csproj
```

### 2. Register with Claude Code

Add to your `.mcp.json` (project-level) or `~/.claude/mcp.json` (global):

```json
{
  "mcpServers": {
    "vs-solution": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\REPOSITORY\\VsSolutionPlugin\\src\\VsSolutionServer\\VsSolutionServer.csproj",
        "--no-build"
      ]
    }
  }
}
```

For a production deployment, publish first and point `command` at the exe:

```powershell
dotnet publish src/VsSolutionServer -c Release -o publish/
```

```json
"command": "C:\\REPOSITORY\\VsSolutionPlugin\\publish\\VsSolutionServer.exe",
"args": []
```

### 3. Use the `/vs-load` skill

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

Loads the solution into memory. **Must be called before any other tool that reads solution data.** Returns project list and workspace diagnostics (MSBuild warnings/errors).

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
dotnet build src/VsSolutionServer

# Watch (auto-rebuild on save)
dotnet watch --project src/VsSolutionServer build
```

The MCP server communicates over stdio — there is no HTTP endpoint to test directly. Use Claude Code with the MCP registration above, or write an integration test that sends JSON-RPC over stdin.
