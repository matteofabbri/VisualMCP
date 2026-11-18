# skills

# skills

The `visualmcp` skill is the product's front door — the REA-style packaging in which the
skill carries the instructions and connects its own MCP server:

| Skill | Installed by | What it does |
|---|---|---|
| `visualmcp` | `scripts\install.ps1` / `scripts/install.sh` (or copy the folder into `~/.zcode/skills/` / `~/.claude/skills/`) | Triggers on C#/.NET repository work. Carries the routing table (MCP tool instead of grep/cat/ls), the board and memory rules, and the connection ladder: installing the skill does not register the server, so when the `mcp__VisualMCP__*` tools are absent the session runs the installer itself, restarts the client, then proceeds. See its [SKILL.md](visualmcp/SKILL.md) and [connection and recovery](visualmcp/references/connection-and-recovery.md) |

Optional skills that are not installed by the installers:

| Skill | Invocation | What it does |
|---|---|---|
| `predictive-coding` | model-invoked when the `Predicting` solution is in scope | Holds the six design invariants of the PCN (free-energy minimization, local Hebbian only, temporal continuity, energy gating, lazy FASTER persistence, SIMD via `System.Numerics.Tensors`); four of them are enforced by the `pcn` inspection pack (PCN0001–PCN0004) |
| `vs-init` | `/vs-init [solution path]` | Writes or appends a `CLAUDE.md` section that instructs the agent to prefer the MCP tools over Grep/Bash for C#, and records the default solution path. Idempotent: it does not append twice |
| `vs-load` | `/vs-load <solution \| directory>` | Calls `list_solutions` when given a directory, then `load_solution`, then `get_project_info` for each project, and summarises frameworks, source counts, packages and project references |

The `vs-init` template predates the routing guard: the guard blocks the wrong calls at the
moment they happen, while the skill writes a static instruction block. `visualmcp` supersedes
both — with it installed, the routing is already in every session and the server connects
itself on first use. The plugin packaging (`.claude-plugin/plugin.json`) ships `visualmcp`
together with its MCP server registration.

| Skill | Invocation | What it does |
|---|---|---|
| `vs-init` | `/vs-init [solution path]` | Writes or appends a `CLAUDE.md` section that instructs the agent to prefer the MCP tools over Grep/Bash for C#, and records the default solution path. Idempotent: it does not append twice |
| `vs-load` | `/vs-load <solution \| directory>` | Calls `list_solutions` when given a directory, then `load_solution`, then `get_project_info` for each project, and summarises frameworks, source counts, packages and project references |
| `predictive-coding` | model-invoked when the `Predicting` solution is in scope | Holds the six design invariants of the PCN (free-energy minimization, local Hebbian only, temporal continuity, energy gating, lazy FASTER persistence, SIMD via `System.Numerics.Tensors`); four of them are enforced by the `pcn` inspection pack (PCN0001–PCN0004) |

The `vs-init` template predates the routing guard: the guard blocks the wrong calls at the
moment they happen, while the skill writes a static instruction block. `visualmcp` supersedes
both — with it installed, the routing is already in every session and the server connects
itself on first use. The plugin packaging (`.claude-plugin/plugin.json`) ships `visualmcp`
together with its MCP server registration.
