# Costo CPU per riga dal sampling (profile_hotlines)

_Created 2026-10-29 20:35. Update the Done column with `update_task_checklist`._

| Task | Done |
| ---- | ---- |
| SourceMapResolver: risoluzione (file, riga) esatta + display di fallback | ☑ |
| TraceFrameInfo: portare File/Line sul frame e valorizzarli in TraceStackReader | ☑ |
| Modelli: CpuLineHotspot, LineCostReport, HotLines su TraceAnalysis | ☑ |
| TraceAnalysisService: aggregare il self time per riga | ☑ |
| Nuovo LineCostService (scope globale o per metodo, testo sorgente, findings) | ☑ |
| ProfilingAdvisor + report Markdown: sezione e regola cpu-hot-line | ☑ |
| Tool MCP profile_hotlines + registrazione DI + ServerInstructions | ☑ |
| Test: unit (fake reader) ed end-to-end su traccia reale con BurnCpu | ☑ |
| Build + run_tests + verifica empirica che ILOffset produca righe distinte | ☑ |
