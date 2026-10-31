[Docs home](../README.md) › Guides › **Profiling**

# Profiling

VisualMCP profiles a **running** .NET process with the runtime's own diagnostics channel: EventPipe for
traces and counters, ClrMD for heap snapshots and dumps, and the Windows kernel ETW provider for file I/O.
The 28 profiling tools are native — none of them spawns `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`,
`dotnet-dump`, `dotnet-stack` or `dotnet-monitor`. The external toolchain is optional and exists only for
formats the server does not produce itself (for example the `.gcdump` file format).

## The shape of a session

```
profile_processes   → find the pid
profile_counters    → is it CPU, GC or memory? (cheap, 5–10 s)
profile_collect     → capture a trace (cpu / gc / exceptions / threading / runtime / diagnose /
                      async / database / instrumentation)
profile_analyze     → hotspots, allocation sites, GC, exceptions, contention, JIT, timeline
profile_root_cause  → WHY that code costs: the loop chain, the LINQ per iteration, the allocation
profile_slowdown    → or skip straight to a verdict on why it is slow
```

Use the specific tools to drill in, and the verdict tools when the question is a sentence rather than a
table.

## Measuring

| Tool | What it produces |
|---|---|
| `profile_processes` | .NET processes with a live diagnostics endpoint: pid, name, path, working set, start time |
| `profile_counters` | EventCounter time series — CPU, working set, GC heap, alloc rate, gen0/1/2, time in GC, thread-pool queue and threads, exceptions, contention, LOH — with min/max/mean/last and raw points |
| `profile_collect` | A real `.nettrace` through EventPipe. Presets: `cpu`, `gc`, `exceptions`, `threading`, `runtime`, `diagnose`, `async`, `database`, `instrumentation`, `custom`. Bounded collection or `start`/`stop` sessions |
| `profile_analyze` | CPU hotspots with CPU ms and `file:line`, top stacks, allocation sites and types, GC generations/pauses/LOH, exceptions with throw sites, lock contention, thread-pool backlog, JIT time, per-second timeline, findings and next steps |
| `profile_root_cause` | Each measured site joined with the solution's **metacode AST**: the declaration that contains it, the loop chain with its nesting depth and header, the LINQ materialiser executed per iteration, the allocation or string concatenation inside the loop, the await/lock, `Task.Result`/`.Wait()` — plus a sentence that connects the measurement to the construct |
| `profile_async` | The .NET async story from `System.Threading.Tasks.TplEventSource`: scheduled/started/completed/exceptional tasks, exact await durations (send/stop pairs, so not a sample), named async operations, and the state machines still alive when the trace ended |
| `profile_database` | The database story from the data-access EventSources (EF Core, SqlClient, Npgsql, MySql, SQLite, Oracle, Db2): commands with text and duration, the slowest ones, the normalised shapes that repeat (the N+1 signal), connection opens, the share of the window in data access |
| `profile_events` | The Events Viewer: every provider/event pair with count, rate, payload field names and one sample event. No provider-specific parser needed, so custom providers are readable |
| `profile_instrumentation` | Exact call counts and inclusive/exclusive times, no sampling: `action=instrument` rewrites an assembly's IL with an injected probe (Mono.Cecil, a `.map.json` with the names, the probe DLL beside the output); `action=analyze` reads the enter/exit pairs |
| `profile_stacks` | Every managed thread's stack, plus contended monitors with the holding thread and its top frame |
| `profile_heap` | Heap by type, LOH and pinned bytes, finalizable objects, ten largest objects, duplicated strings; optional JSON snapshot |
| `profile_dump` | Collect a dump (`normal`, `withheap`, `triage`, `full`) or analyse an existing `.dmp` with ClrMD — heap plus every thread's stack and contended monitors |
| `profile_allocations` | Sampled allocation bytes attributed to the allocating method, type and `file:line`, with full allocation stacks |
| `profile_calltree` | CPU call tree with inclusive and exclusive time per node; optional folded-stacks export for FlameGraph/speedscope |
| `profile_hotpaths` | The fix list: ranked user methods (cost summed across every stack through them), root-to-leaf hot paths, and branch points where cost splits; CPU, allocations, exceptions or mixed |
| `profile_io` | Where threads block in I/O — File, Http, Network, Database, Pipe, Console, SyncOverAsync, Lock, Sleep, WaitHandle — with call site, source, blocked ms, share and threads |
| `profile_file_io` | Who reads and writes which file: per-process and per-file bytes and operation counts, read/write MB/s, and findings — write hotspot, small-write storm, handle churn, temp churn. Windows kernel ETW: Windows only and elevated |
| `profile_symbols` | Reads PE debug records and downloads PDBs into the solution cache, so frames resolve to `file:line` |
| `profile_compare` | Diffs two traces or two heap snapshots: per-method CPU, per-type allocations/heap, GC, exceptions, contention, with regression findings |
| `profile_tools` | Status/install/update for the seven external diagnostics tools, and what VisualMCP already covers natively (no profiling tool needs them) |
| `profile_monitor` | Start, probe and stop the native in-process diagnostics sidecar: `/info`, `/processes`, `/metrics` (JSON or Prometheus), `/trace`, `/dump`, `/heap`, `/stacks`, `/fileio` over HTTP on loopback |

Presets set the right providers and a stack walk: `gc` uses Verbose GC events plus allocation ticks with
stacks, `cpu` uses the sample profiler, `diagnose` combines CPU samples, allocation ticks, GC, exceptions
and contention. `async` turns on `System.Threading.Tasks.TplEventSource`, `database` turns on the
data-access EventSources at Verbose, and `instrumentation` turns on the probe the instrumenter injects.
`bufferMb` controls the circular buffer; `durationSeconds` bounds a collection.

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

### "Which code creates the problem?"

```
profile_collect     processName=MyApp.Api profile=cpu durationSeconds=20
profile_hotlines    tracePath=…
profile_root_cause  tracePath=… userCodeRoot=C:\src\MyApp
```

`profile_hotlines` answers *where* the CPU goes (one line). `profile_root_cause` answers *why*: it reads
the same trace, matches each hot site to the declaration that contains it in the solution's **metacode
AST**, and reports the construct — "this line sits inside two nested loops (`for …` → `foreach …`), it
allocates `List<Order>` once per element, and `ToList()` materialises the inner query every iteration".
The files are translated from disk with the same frontends the solution index uses, so it also works
when the persisted index is stale. It covers CPU self time, sampled allocations, blocked time and
thrown exceptions.

### "What is the async code doing?"

```
profile_collect profile=async processName=MyApp.Api durationSeconds=30
profile_async   tracePath=…
```

`profile_async` pairs every `TaskWaitSend` with its `TaskWaitStop`, so the await durations are exact
events, not samples. The most valuable line is usually `IncompleteAsyncMethods`: the state machines
still alive when the trace ended — a fire-and-forget task, a deadlock on a synchronisation context, or
work that outlives the request. A CPU trace has no task events; the answer then says so and repeats the
`profile_collect profile=async` command.

### "Which database queries cost the time?"

```
profile_collect  profile=database processName=MyApp.Api durationSeconds=30
profile_database tracePath=… top=20
```

Commands are grouped by **normalised shape** (literals replaced), so the same query with different
arguments is one row — which is how the N+1 signature appears: many fast executions of one shape. If the
trace contains no database event, the report names the providers it did contain and the exact collection
command; `profile_events` shows what any provider actually exposes.

### "I need exact counts, not samples"

```
profile_instrumentation action=instrument assemblyPath=bin\Release\net10.0\MyApp.Core.dll include=MyApp.Core.Services.*
# run the *instrumented* copy with Snitch.Profiling.Probe.dll beside it
profile_collect          profile=instrumentation processName=MyApp.Api durationSeconds=30
profile_instrumentation  action=analyze tracePath=… mapPath=…map.json
```

Every instrumented method calls a probe on entry and on every exit (exception path included). The report
has exact call counts and inclusive/exclusive times — a method that runs twice is reported with two
calls. `include`/`exclude` wildcards bound what is rewritten; a method whose IL cannot be wrapped safely
is skipped and named in the notes.

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

### "Who is writing where, and how?"

```
# start the server elevated: the kernel file provider is a privileged ETW session
profile_file_io  processName=MyApp.Api durationSeconds=15
profile_file_io  processName=MyApp.Api durationSeconds=15 pathFilter=logs writeReport=true
```

`profile_file_io` is Visual Studio's **File I/O** tool, natively: the Windows kernel file provider reports
every read and write on the machine, and the report keeps the target process — so it answers *who* (the
process), *where* (the file), *how much* (bytes, read/write MB/s) and *how* (operations, open/close counts,
average write size). The findings name the pattern: one file owns the writes (`file-write-hotspot`),
thousands of tiny writes (`small-write-storm`, buffer them), an open/close per operation (`handle-churn`),
temp-file churn, or writes whose file could not be resolved because it was opened before the capture. Pass
`processId`/`processName` under load: an unfiltered capture is system-wide. For where threads *block* in file
I/O use `profile_io`; for the managed code that performs the write use `profile_allocations` or
`profile_root_cause`.

The same report is available over HTTP from the native monitor:

```
profile_monitor  action=start
profile_monitor  action=probe path="/fileio?pid=1234&durationSeconds=10"
```

## Monitoring over HTTP

`profile_monitor` is the long-running sidecar, now in-process: `action=start` opens a loopback listener
(default `http://127.0.0.1:52323`, port 0 picks a free one), and its routes are answered by the same
services the tools use — `/info`, `/processes`, `/metrics?pid=&durationSeconds=&format=json|prometheus`,
`/trace?pid=&profile=&durationSeconds=`, `/dump?pid=&type=`, `/heap?pid=&top=`, `/stacks?pid=&maxFrames=`,
`/fileio?pid=&durationSeconds=`, `/artifact?path=` for the produced files. There is no `dotnet-monitor`
binary, no child process and no settings file. Requests are bounded windows rather than continuous streams;
the listener is loopback-only and unauthenticated, so do not expose the port.

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
- `profile_root_cause` needs both halves: PDBs for the measured side and a readable source file for
  the AST side (C#, Visual Basic or Razor under the solution). Without either it reports the
  measurement and says which half is missing instead of guessing.
- `profile_async` needs the `async` preset and `profile_database` the `database` preset: these are
  EventSource providers, not CLR runtime keywords, so a CPU trace does not contain them.
- Instrumented profiling changes the binary: run the rewritten copy, keep `Snitch.Profiling.Probe.dll`
  beside it, and expect the instrumentation overhead (every call writes two events). A strong-named
  assembly on .NET Framework may refuse the rewritten copy; methods whose IL cannot be wrapped safely
  (a `jmp`, a `ret` inside a finally/fault) are skipped and named.
- Async state machines, inlining and framework frames can hide the user code you are looking for;
  `profile_hotpaths` and `profile_io` surface the user-code frame explicitly, which is why they exist.
- `profile_file_io` needs **Windows and an elevated server** (or the Performance Log Users group): a kernel
  ETW session is privileged. It reports the process that performed each operation, not the managed call
  stack; pair it with `profile_allocations` for the code. On Linux the equivalent would need eBPF/perf.
- `profile_threadpool` and `profile_review_change` refuse inconclusive windows on purpose. A confident
  wrong answer is worse than a refusal.

---

Next: [Workflow and integrations](workflow.md)
