[Docs home](../README.md) › Guides › **Profiling**

# Profiling

VisualMCP profiles a **running** .NET process with the runtime's own diagnostics channel: EventPipe for
traces and counters, ClrMD for heap snapshots and dumps. The 20 profiling tools are native — none of them
spawns `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `dotnet-dump` or `dotnet-stack`. The external
toolchain is optional and exists only for formats the server does not produce itself (for example the
`.gcdump` file format).

## The shape of a session

```
profile_processes   → find the pid
profile_counters    → is it CPU, GC or memory? (cheap, 5–10 s)
profile_collect     → capture a trace (cpu / gc / exceptions / threading / runtime / diagnose)
profile_analyze     → hotspots, allocation sites, GC, exceptions, contention, JIT, timeline
profile_slowdown    → or skip straight to a verdict on why it is slow
```

Use the specific tools to drill in, and the verdict tools when the question is a sentence rather than a
table.

## Measuring

| Tool | What it produces |
|---|---|
| `profile_processes` | .NET processes with a live diagnostics endpoint: pid, name, path, working set, start time |
| `profile_counters` | EventCounter time series — CPU, working set, GC heap, alloc rate, gen0/1/2, time in GC, thread-pool queue and threads, exceptions, contention, LOH — with min/max/mean/last and raw points |
| `profile_collect` | A real `.nettrace` through EventPipe. Presets: `cpu`, `gc`, `exceptions`, `threading`, `runtime`, `diagnose`, `custom`. Bounded collection or `start`/`stop` sessions |
| `profile_analyze` | CPU hotspots with CPU ms and `file:line`, top stacks, allocation sites and types, GC generations/pauses/LOH, exceptions with throw sites, lock contention, thread-pool backlog, JIT time, per-second timeline, findings and next steps |
| `profile_stacks` | Every managed thread's stack, plus contended monitors with the holding thread and its top frame |
| `profile_heap` | Heap by type, LOH and pinned bytes, finalizable objects, ten largest objects, duplicated strings; optional JSON snapshot |
| `profile_dump` | Collect a dump (`normal`, `withheap`, `triage`, `full`) or analyse an existing `.dmp` with ClrMD — heap plus every thread's stack and contended monitors |
| `profile_allocations` | Sampled allocation bytes attributed to the allocating method, type and `file:line`, with full allocation stacks |
| `profile_calltree` | CPU call tree with inclusive and exclusive time per node; optional folded-stacks export for FlameGraph/speedscope |
| `profile_hotpaths` | The fix list: ranked user methods (cost summed across every stack through them), root-to-leaf hot paths, and branch points where cost splits; CPU, allocations, exceptions or mixed |
| `profile_io` | Where threads block in I/O — File, Http, Network, Database, Pipe, Console, SyncOverAsync, Lock, Sleep, WaitHandle — with call site, source, blocked ms, share and threads |
| `profile_symbols` | Reads PE debug records and downloads PDBs into the solution cache, so frames resolve to `file:line` |
| `profile_compare` | Diffs two traces or two heap snapshots: per-method CPU, per-type allocations/heap, GC, exceptions, contention, with regression findings |
| `profile_tools` | Status/install/update for the seven external diagnostics tools, and what VisualMCP already covers natively |
| `profile_monitor` | Start, probe and stop a loopback `dotnet-monitor` (`--no-auth`, localhost only) |

Presets set the right providers and a stack walk: `gc` uses Verbose GC events plus allocation ticks with
stacks, `cpu` uses the sample profiler, `diagnose` combines CPU samples, allocation ticks, GC, exceptions
and contention. `bufferMb` controls the circular buffer; `durationSeconds` bounds a collection.

## Answering

These return **one verdict**, not a data dump. Each names its evidence, the share of the measured window
it can explain, and the runners-up that lost.

| Question | Tool | Possible answers |
|---|---|---|
| Why is this process slow? | `profile_slowdown` | `cpu`, `gc`, `io-wait`, `sync-over-async`, `lock-wait`, `blocked-native`, `not-slow` |
| Why is memory growing? | `profile_memory_growth` | `growth`, `shrink`, `stable`, `single-snapshot` |
| Is the thread pool starved? | `profile_threadpool` | `starved`, `at-risk`, `burst`, `healthy`, `inconclusive` |
| Is this change a regression? | `profile_review_change` | `regression`, `improvement`, `unchanged`, `inconclusive` |
| The process is unhealthy and I do not know why | `profile_diagnose` | Counters + trace + stacks + heap in one call, correlated findings, one Markdown report |

`profile_slowdown` works live or from an existing trace. `profile_threadpool` deliberately requires a
full queue **and** threads that cannot take the work; a long queue alone is a burst, not starvation.
`profile_review_change` compares per-second normalised rates with a significance floor per metric and
refuses to judge windows under 500 ms — a refusal is a feature, not an error.

## Recipes

### "It is slow"

```
profile_slowdown  processName=MyApp.Api durationSeconds=15
```

If the verdict is `cpu`, follow with `profile_hotpaths` or `profile_calltree` to find the method. If it
is `io-wait` or `sync-over-async`, follow with `profile_io` to get the blocking call site. If it is `gc`,
follow with `profile_allocations`.

### "Which method burns the CPU?"

```
profile_collect  processName=MyApp.Api profile=cpu durationSeconds=20
profile_analyze  tracePath=…top=30
profile_hotpaths tracePath=… metric=cpu top=15
```

`profile_analyze` gives the flat ranked list; `profile_hotpaths` gives the fix list, the hot paths and
the branch points where optimising a caller is the better move; `profile_calltree` gives inclusive and
exclusive time.

### "Where does this allocation come from?"

```
profile_collect      processName=MyApp.Api profile=gc durationSeconds=30
profile_allocations  tracePath=… focusType=MyApp.Order
```

Allocation stacks need the `gc` preset (or `diagnose`): it requests stack walks. `profile_heap` answers
what is *on* the heap now; `profile_allocations` answers who *allocated* it.

### "Memory keeps growing"

```
profile_heap           processName=MyApp.Api writeJson=true
# … exercise the workload …
profile_heap           processName=MyApp.Api writeJson=true
profile_memory_growth  baselinePath=first.json currentPath=second.json tracePath=gc.nettrace
```

Without two snapshots the tool answers `single-snapshot` and lists the biggest types rather than
pretending to see a trend.

### "It hangs, with no CPU"

```
profile_stacks  processName=MyApp.Api
profile_dump    action=collect processName=MyApp.Api dumpType=withheap
```

`profile_stacks` names contended monitors and the thread holding each lock. A dump freezes the state so
you can analyse it repeatedly.

### "Did my change make it worse?"

```
profile_review_change  baselinePath=before.nettrace currentPath=after.nettrace
```

For raw deltas rather than a verdict, use `profile_compare`; for two heap snapshots, `kind=heap`.

## Artifacts and symbols

Traces, dumps, reports and downloaded symbols land in:

```
<solution>/.visualMCP/profiling/
```

The folder is covered by `.gitignore`. Reports are Markdown, so they can be read in the editor or
attached to a review.

Method names resolve to `file:line` when a portable PDB is next to the module or in the symbol cache.
`profile_symbols` can download PDBs from a symbol server first:

```
profile_symbols  action=download modulePaths=[bin/Debug/net10.0/MyApp.Api.dll]
```

## Limits

- The target process must expose a diagnostics IPC endpoint. A process started with diagnostics disabled,
  or a different runtime, cannot be profiled.
- Sampling is statistical: short captures and very hot, very short methods are noisy. Use seconds, not
  milliseconds, and compare two windows rather than trusting one.
- Allocation attribution requires stack walks (`profile=gc` or `diagnose`); without them, `profile_analyze`
  can report totals but not sites.
- ClrMD cannot attach to the server's own process; profile your application, not VisualMCP.
- `file:line` needs PDBs. Without them you get method names and modules, not source lines.
- Async state machines, inlining and framework frames can hide the user code you are looking for;
  `profile_hotpaths` and `profile_io` surface the user-code frame explicitly, which is why they exist.
- `profile_threadpool` and `profile_review_change` refuse inconclusive windows on purpose. A confident
  wrong answer is worse than a refusal.

---

Next: [Workflow and integrations](workflow.md)
