---
name: project-missing-tools
description: MCP tools identified as missing from VisualMCP for deep code analysis (priority medium/low backlog)
metadata:
  type: project
---

Tools already decided and being implemented (priority high): FindDuplicateCode, FindSecurityIssues, AnalyzeDependencyInjection.

Remaining backlog — medium priority:

| Area | Tool | Why |
|---|---|---|
| Nullable analysis | `AnalyzeNullability` | Trova `!` abusati, nullable disabilitate, flussi null non gestiti |
| Layer architecture | `CheckLayerViolations` | Verifica dipendenze invertite tra layer (Domain→App→Infra) |
| Breaking changes | `FindBreakingChanges` | Confronta API pubblica tra branch/commit |
| Test quality | `AnalyzeTestQuality` | Test senza assert, nomi non descrittivi, test di implementation details |
| Cohesion | `GetCohesionMetrics` | LCOM — classi che andrebbero spezzate |
| Missing validation | `FindMissingValidation` | Parametri pubblici senza guard clause, DTO senza DataAnnotation |

Low priority:

| Area | Tool | Why |
|---|---|---|
| Performance | `FindPerformanceAntiPatterns` | LINQ su hot path, boxing, string+ in loop, ToList() inutili |
| Visualization | `ExportDependencyGraph` | Esporta grafo in DOT/Mermaid |
| Quality trend | `GetQualityTrend` | Confronta metriche tra due commit |

**Why:** identified during a gap analysis session on 2026-06-03.
**How to apply:** when the user asks to implement more analysis tools, start from the medium-priority list above.
