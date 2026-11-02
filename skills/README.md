# skills — optional client skills

Three Claude Code skill definitions. They are not installed by `scripts\install.ps1`: copy the
folder you want into your client's skills directory (or point the client at this repository) if
you use them. With the routing guard installed they are mostly a convenience — the guard and
`AGENTS.md` already tell the model to use the MCP tools.

| Skill | Invocation | What it does |
|---|---|---|
| `vs-init` | `/vs-init [solution path]` | Writes or appends a `CLAUDE.md` section that instructs the agent to prefer the MCP tools over Grep/Bash for C#, and records the default solution path. Idempotent: it does not append twice |
| `vs-load` | `/vs-load <solution \| directory>` | Calls `list_solutions` when given a directory, then `load_solution`, then `get_project_info` for each project, and summarises frameworks, source counts, packages and project references |
| `predictive-coding` | model-invoked when the `Predicting` solution is in scope | Holds the six design invariants of the PCN (free-energy minimization, local Hebbian only, temporal continuity, energy gating, lazy FASTER persistence, SIMD via `System.Numerics.Tensors`); four of them are enforced by the `pcn` inspection pack (PCN0001–PCN0004) |

The `vs-init` template is older than the current routing guard: the guard blocks
the wrong calls at the moment they happen, while the skill writes a static instruction block. If
you use both, expect `agents-block.md` (or your own `AGENTS.md`) to be the source of truth — see
[`../hooks/README.md`](../hooks/README.md).
