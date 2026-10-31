---
name: project-missing-tools
description: The analysis-tool backlog from the 2026-06-03 gap analysis — every item is shipped; this file is the record, not a to-do
metadata:
  type: project
---

**Status 2026-10-31: the backlog is empty.** Nothing in this file is still missing; keep it as the map of
which tool closed which row.

Medium priority:

| Area | Tool | Closed by |
|---|---|---|
| Nullable analysis | `analyze_nullability` | `analyze_nullability` — `!` suppressions with lines, nullable context per project, CS86xx |
| Layer architecture | `check_layer_violations` | `check_layer_violations` — every dependency pointing back up, file:line |
| Breaking changes | `find_breaking_changes` | `find_breaking_changes` — public/protected surface vs a git ref |
| Test quality | `analyze_test_quality` | `analyze_test_quality` — no assert, empty, unreadable name, `async void` |
| Cohesion | `get_cohesion_metrics` | `get_cohesion_metrics` — LCOM4 |
| Missing validation | `find_missing_validation` | `find_missing_validation` — unguarded public parameters **and** DTO-shaped classes/records whose properties carry no `System.ComponentModel.DataAnnotations` attribute (closed 2026-10-31) |

Low priority:

| Area | Tool | Closed by |
|---|---|---|
| Performance | `find_performance_anti_patterns` | `find_performance_anti_patterns` — `string +=` in a loop, a LINQ materialiser inside a loop, a collection/constant-size array per iteration (PF0001–PF0003) |
| Visualization | `export_dependency_graph` | `export_dependency_graph` — DOT/Mermaid at project, namespace or type level |
| Quality trend | `get_quality_trend` | `get_quality_trend` — two revisions (or `to=worktree`) measured with one syntactic rule set, with a verdict and the files that moved (closed 2026-10-31) |

**Why:** identified during a gap analysis session on 2026-06-03.
**How to apply:** when the user asks "what is missing" in analysis, do not read this file as a backlog —
it is closed. The live inventory is `docs/gaps.md` plus `capability_report`.
