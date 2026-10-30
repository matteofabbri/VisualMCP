# src — the host and its tests

`src/` contains the two projects that make the server runnable. The engine itself lives next door
in [`SNITCH/`](../SNITCH/README.md).

| Project | Kind | Depends on | README |
|---|---|---|---|
| `VisualMCP/` | `net10.0` executable, framework-dependent | `Snitch`, `Snitch.Metacode`, `Snitch.Profiling`, ModelContextProtocol 2.x | [VisualMCP/README.md](VisualMCP/README.md) |
| `VisualMCP.Tests/` | xUnit test project, `net10.0` | `VisualMCP` | [VisualMCP.Tests/README.md](VisualMCP.Tests/README.md) |

The full solution (`../VisualMCP.sln`) also contains the three engine projects
(`Snitch`, `Snitch.Metacode`, `Snitch.Profiling`), which is why it lists five projects in total.

```powershell
# Build the whole solution (engine + host + tests)
dotnet build VisualMCP.sln

# Build only the server
dotnet build src/VisualMCP/VisualMCP.csproj -c Release

# Run the test suite
dotnet test src/VisualMCP.Tests/VisualMCP.Tests.csproj
```

Two folders under `src/` (`Snitch.Metacode/`, `VisualMCP.Profiling/`) contain only `obj/`
leftovers from an earlier layout; they are not projects and are not in the solution. The real
libraries are `../SNITCH/Libraries/Snitch.Metacode` and `../SNITCH/Libraries/Snitch.Profiling`.

## Where to change what

| You want to change… | Go to |
|---|---|
| a tool's arguments, name or description | `VisualMCP/Tools/<Area>/` |
| what a tool actually does | `../SNITCH/<area>/` |
| the model a tool receives in every session | `Program.cs` → `ServerInstructions` |
| startup order, discovery, logging, dashboard | [VisualMCP/README.md](VisualMCP/README.md) |
| a test for a rule or a tool | [VisualMCP.Tests/README.md](VisualMCP.Tests/README.md) |

Architecture rules for contributors are in [`../CONTRIBUTING.md`](../CONTRIBUTING.md): one type per
file, a tool is a thin shim, and code edits are compiler-gated.
