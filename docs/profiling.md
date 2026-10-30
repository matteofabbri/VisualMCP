# Profiling

`Snitch.Profiling` is the profiling library of the server: it observes a **running** .NET
process, turns the observation into numbers, compares those numbers against a rule set, and tells
you what to look at next. It is host-agnostic (explicit process ids and paths, no workspace
dependency) and the MCP tools in `src/VisualMCP/Tools/Profiling/` are a thin layer over it.

The goal is not to replace PerfView, dotTrace or dotMemory. It is to make the common questions
answerable **inside the agent loop**, in one or two calls, without installing a toolchain and
without leaving the conversation:

- Which method is burning CPU, and at which line?
- What is the application allocating, and what survives to gen2?
- Why is the process stuck — which thread holds the lock everyone wants?
- Why is memory high — which type owns the heap?
- Is the thread pool starving, and are exceptions being used for control flow?

## The seven tools, native vs delegated

| Tool | VisualMCP does itself | Still delegated |
|---|---|---|
| **dotnet-trace** | `profile_collect` starts/stops the EventPipe session (presets, custom providers, circular buffer, stackwalk) and writes a real `.nettrace`; `profile_analyze` reads it. | `convert`/`report`/`list-profiles` verbs, interactive UI. |
| **dotnet-counters** | `profile_counters` runs the EventPipe/EventCounter session and returns the time series (min/max/mean/last + raw points). | Interactive `monitor` console UI, `collect` file export. |
| **dotnet-gcdump** | `profile_heap` attaches with ClrMD and computes counts/sizes per type, LOH, pinned, free, finalizable, largest objects and duplicated strings. | The `.gcdump` file format (for Visual Studio / `dotnet-gcdump report`). |
| **dotnet-dump** | `profile_dump` collects through `DiagnosticsClient.WriteDump` and analyses an existing dump with ClrMD (heap + stacks + sync blocks). | Ad-hoc SOS commands (`clrstack`, `dumpheap -stat`, `syncblk`, …). |
| **dotnet-stack** | `profile_stacks` captures every managed stack with ClrMD, resolves frames to source, and reports contended monitors with the holding thread. | The `report` graphviz output. |
| **dotnet-monitor** | `profile_monitor` starts a loopback instance (`--no-auth`), generates its settings file, probes HTTP endpoints, stops it. | The service itself is not reimplemented: one-shot profiling stays native. |
| **dotnet-symbol** | `profile_symbols` reads the PE CodeView record and downloads/caches the PDB with the symbol-server protocol. | Bulk rescue of every module inside a dump. |

`profile_tools` reports this table per tool, including whether the command is installed and which
version would actually run (local tool manifest > global tool > `PATH`). It can install or update a
tool when you decide you need the external artifact.

## Workflows

### A CPU problem

```text
profile_processes()                               → find the pid
profile_collect(processName="MyApp", profile="cpu", durationSeconds=15)
profile_analyze(tracePath="…/trace-MyApp-1234-….nettrace")
```

`profile_analyze` returns `CpuHotspots` (method, samples, share, estimated CPU ms, source line),
`CpuStacks` (the call paths that produced the samples), a per-second `Timeline`, and findings such
as `cpu-hotspot` when one method owns ≥ 40 % of the CPU samples. If the process was idle, the
warnings say so instead of inventing a hotspot.

For the shape of the time rather than a flat list, use `profile_calltree`: it returns inclusive and
exclusive CPU ms per node (a cheap method that owns time through a callee is visible only there),
the caller chain (`Path`) and, with `writeFolded=true`, a folded-stacks file for FlameGraph or
speedscope.

### Hot paths: several fixes at once

Visual Studio's Hot Path view walks the call tree and shows the single most expensive path. You fix
that path, recompile, rerun, and the view shows the next one. `profile_hotpaths` is built to break
that loop:

```text
profile_hotpaths(tracePath="…/trace.nettrace", metric="cpu", top=10)
```

It returns three views of the same stack tree:

- **HotMethods** — the fix list. One entry per user method/line, with the cost summed across every
  stack that goes through it, the number of distinct paths (`Variants`), and a recommendation that
  says whether the cost is self time (optimise the body) or is spread through callees (reduce
  calls / batch / cache). Several entries can be planned at once.
- **TopPaths** — the concrete root-to-leaf paths, with the deepest user frame and its `file:line`.
- **Branches** — frames where the cost splits between several hot children. That is the decision
  point: optimise the caller if the children share work, otherwise start with the largest child.

Paths that differ only by generic instantiations or wrapper frames are merged into one entry
(`Variants`), so the list does not fill with near-duplicates. The metric can be `cpu` (ms),
`allocations` (sampled bytes) or `exceptions` (throw paths); `mixed` returns CPU and allocation
entries side by side and adds a finding when both point at the same area. `minPercent` (default 1 %)
filters noise, and `userCodeOnly` (default true, with the solution directory as the user-code root)
keeps framework frames out of the fix list while still showing them in the paths.

This is the intended first call for "where do I start": it answers with a ranked set of changes
instead of one path that becomes stale after the first fix.

### Where are threads blocked in I/O?

```text
profile_collect(processName="MyApp", profile="runtime", durationSeconds=15)
profile_io(tracePath="…/trace.nettrace")
```

`profile_io` reads the intervals in which a thread was sampled **outside managed code**
(`ClrThreadSampleType.External`) and attributes each wait to the managed stack that caused it. The
result is not a snapshot: consecutive samples bound the wait, so it reports **how long** each site
blocked, not just that a thread looked busy.

Sites are grouped by category and user call site:

| Category | Typical blocking frame |
|---|---|
| `File` | `System.IO.FileStream.Read`, `File.ReadAllText`, `OSFileStreamStrategy..ctor` |
| `Http` | `System.Net.Http.HttpClient.Send` |
| `Network` | `Socket.Receive`, `NetworkStream.Read`, `TcpClient` |
| `Database` | `SqlCommand.ExecuteReader`, `Npgsql*`, `DbCommand` |
| `Pipe` | `NamedPipeServerStream.WaitForConnection`, `PipeStream.Read` |
| `Console` | `Console.Read*` |
| `SyncOverAsync` | `Task.Wait`, `Task<T>.Result`, `GetAwaiter().GetResult()` |
| `Lock` | `Monitor.Wait/Enter`, `SemaphoreSlim.Wait`, mutex/lock classes |
| `Sleep` | `Thread.Sleep` |
| `WaitHandle` | `WaitHandle.WaitOne` |
| `Other` | anything else that blocks in native code |

Each site carries the user method/line, the blocking framework method, blocked ms, share, thread
count, samples and the stack. `Idle` intervals — a thread-pool worker parked on its completion port
(`GetQueuedCompletionStatus`, `LowLevelLifoSemaphore.WaitForSignal`) — are **excluded** from the
totals and reported separately (`IdleMs`), because they are not application blocking. A
`Task.Wait` anywhere in the stack wins over a leaf `WaitHandle.WaitOne`, so the synchronous-over-
asynchronous pattern is classified as `SyncOverAsync`, not as a generic wait.

Findings: `io-block` (a dominant I/O site), `sync-over-async` (the thread waits on work that is
already asynchronous, and it is usually a pool thread → `thread-pool-blocking`),
`thread-blocking` (sleep/lock wait dominant), `io-category` (one I/O family dominates) and
`io-pressure` (threads spend a large part of the trace waiting). Use `profile_stacks` when you want
to see the blocked threads of a live process right now.

### Where does this allocation come from?

```text
profile_collect(processName="MyApp", profile="gc", durationSeconds=15)
profile_allocations(tracePath="…/trace-MyApp-1234-….nettrace", top=20)
```

`profile_allocations` returns the allocating method (leaf frame of the allocation tick), its
`file:line`, the allocated type, sampled MB, sample count, share, small/large kind and the full
allocation stack. A type-level list tells you *what*; this tells you *where*. The trace must contain
allocation stacks: the `gc` preset is Verbose with `requestStackwalk` for exactly this reason.

### Did this change make it worse?

```text
profile_heap(processName="MyApp", writeJson=true)     → heap-before.json
profile_heap(processName="MyApp", writeJson=true)     → heap-after.json
profile_compare(leftPath="…/heap-before.json", rightPath="…/heap-after.json")

profile_collect(processName="MyApp", profile="cpu", durationSeconds=15)   → trace-before.nettrace
# change the code, redeploy, collect trace-after.nettrace
profile_compare(leftPath="…/trace-before.nettrace", rightPath="…/trace-after.nettrace")
```

Trace comparison returns total CPU, per-method CPU deltas, per-type allocation deltas, GC,
exceptions, contention and pool backlog, plus findings (`cpu-regression`, `allocation-regression`,
`gc-regression`, …). Heap comparison returns total/LOH/finalizable and per-type byte/instance
deltas (`heap-growth`, `heap-type-growth`, …).

### One call when the process is unhealthy and you do not know why

```text
profile_diagnose(processName="MyApp", durationSeconds=10)
```

It samples counters, collects a `runtime` trace, captures the stacks and (by default) a heap
snapshot, runs every rule set and then **correlates** the signals: "cpu-usage peaked at 96 % while
method X owned 71 % of CPU samples", "thread-pool queue grew to 4,000 with 9 threads blocked in
synchronous waits", "time-in-gc is 28 % and System.Byte[] owns most sampled allocations". It writes
one Markdown report and returns the structured report. Use the specific tools to drill into a
single signal afterwards.

### A memory problem

```text
profile_counters(processName="MyApp", countersFilter="gc-heap-size,alloc-rate,gen-2-gc-count,loh-size", durationSeconds=20)
profile_heap(processName="MyApp", top=40, focusType="System.Byte[]")
```

Then read the findings: `heap-type-dominance`, `heap-loh`, `finalizer-queue`, `duplicate-strings`.
For something you can analyse repeatedly, `profile_dump(action="collect")` and then
`profile_dump(action="analyze", dumpPath=…)` on the frozen file.

### A hang or a slowdown with no CPU

```text
profile_stacks(processName="MyApp", maxFrames=60)
```

Every thread's managed stack is returned with `file:line` where the PDB is available, plus
`ContendedMonitors`: for each lock with waiting threads, the holding thread, the object type and
the holder's top frame. The `monitor-contention` and `blocked-threads` findings point at the
holder; that is the thread to read.

### Counters before a trace

`profile_counters` is the cheap first look: `cpu-usage`, `working-set`, `gc-heap-size`,
`alloc-rate`, `gen-0/1/2-gc-count`, `time-in-gc`, `threadpool-queue-length`,
`threadpool-thread-count`, `exception-count`, `monitor-lock-contention-count`, `loh-size`, plus
ASP.NET Core, Kestrel, `System.Net.Http`/`Sockets` and gRPC when the process hosts them. Unknown
providers are ignored by the runtime, so the default list is safe for a console app too. The
counter series also feed the advisor directly (`cpu-saturated`, `gc-time-high`,
`allocation-rate-high`, `threadpool-queue`, `lock-contention-count`, …), which is what
`profile_diagnose` uses to correlate them with the trace.

## The answer layer: one verdict per question

The tools above return measurements. The four services below take measurements that already exist
— the trace analysis, the I/O report, the counters, the stacks, the heap snapshots and the
comparison — and answer **one question with one sentence**, a ranked list of evidence, and the next
call to make. None of them re-implements EventPipe or ClrMD: each one orchestrates the services
described above.

They are registered by `AddVisualMcpProfiling` and are meant to be wrapped one-to-one by an MCP
tool. Every report contains the same envelope, `AnswerReport`:

| Field | Meaning |
|---|---|
| `Question` | the question the report answers |
| `Verdict` | a stable machine id (`cpu`, `io-wait`, `growth`, `starved`, `regression`, …) |
| `Answer` | one sentence with the numbers, the place and the reason |
| `Confidence` | `high` (two independent sources agree), `medium` (one measurement), `low` (weak or incomplete) |
| `Evidence` | ordered items: `Rank`, `Weight`, `Id`, `Severity`, `Statement`, `Evidence`, `Recommendation`, `NextStep`, `Source` |
| `NextSteps` | the concrete calls and paths to open, most specific first |
| `Measurements` | the facts the answer was built from |
| `Warnings` | what was missing, was truncated, or should not be trusted |

`Weight` always means "how much of the measured window (or of the heap) this item can explain", so
`Rank` is an importance order rather than a severity guess. A finding that fires below the
threshold is still evidence — it just ranks last. Findings keep their documented ids, so the table
under [Findings](#findings) applies to this layer too.

### Why is it slow? — `ISlowdownService`

```text
AnalyzeProcessAsync(processId, name, durationSeconds, includeStacks, outputDirectory)
AnalyzeTraceAsync(tracePath, counters?, stacks?)
```

The live overload samples counters, collects a `diagnose` trace (CPU + Verbose allocation ticks +
GC + exceptions + contention) and the stacks, then produces one verdict. The offline overload takes
a trace that already exists, with counters and stacks as optional corroboration.

Every candidate cause is scored in one unit — **how many seconds of the wall-clock window it can
explain** — which is what makes them comparable:

| Cause id | Weight | Evidence it comes from |
|---|---|---|
| `cpu` | core-seconds of CPU per wall second | `TraceAnalysis` CPU samples, or `cpu-usage` |
| `gc` | GC pause share of the window | `GcSummary.PauseShare`, or `time-in-gc` |
| `io-wait` | seconds of thread-wait in File/Http/Network/Database/Pipe/Console | `IoBlockReport` categories |
| `sync-over-async` | seconds parked in `Task.Wait`/`.Result`/`GetAwaiter().GetResult()` | `IoBlockReport` `SyncOverAsync` |
| `lock-wait` | seconds waiting on locks | `IoBlockReport` `Lock` **or** `ContentionSummary` — the larger, never the sum |
| `blocked-native` | seconds in `Thread.Sleep`/`WaitHandle`/other native waits | `IoBlockReport` |

The largest weight wins; ties break in the table's order. Below **10 % of the window** nothing
dominates and the verdict is `not-slow` with the numbers that prove it — a process that used 30 ms
of CPU in a one-second window is not slow, and saying so is more useful than reporting the least
small number. With only stacks (no trace) the weight becomes the share of threads parked in a wait,
and the detail string says so; confidence drops to `low`.

`Causes` keeps the full ranking with each cause's weight and detail, so the runners-up are visible
with the reason they lost. Next steps start with the site of the winning cause (a `file:line` when
the PDB was found), then fall back to the advisor's suggestions.

### Why is memory growing? — `IMemoryGrowthService`

```text
CompareAsync(baselineJsonPath, currentJsonPath, tracePath?)
CaptureAsync(processId, name, baselineJsonPath?, tracePath?, outputDirectory)
```

Heap snapshots are written with `profile_heap writeJson=true`; `CaptureAsync` adds the same file
for the snapshot it just took, so the current run can be the next baseline. The service compares
the two snapshots (the existing heap comparison), optionally enriches the answer with a gc trace
(GC numbers and allocation sites) and then commits to a verdict:

| Verdict | When |
|---|---|
| `growth` | heap `+10 MB` **and** `+20 %` between the snapshots |
| `shrink` | heap `−10 MB` **and** `−20 %` |
| `stable` | anything smaller — the deltas are below the floor this analysis uses |
| `single-snapshot` | no baseline: the current owner is named, growth is explicitly not concluded |

The correlation is what a diff alone cannot give: when the type that grew is also the type the
allocation-site analysis attributes the most sampled bytes to, the answer says the growth is
*produced* at that `file:line` (finding `memory-growth-driver`, evidence ranked first) rather than
held by an old cache. When it cannot correlate, it says so and asks for a gc trace. A truncated
heap walk is reported in `Warnings` and drops the confidence, because the totals are then a lower
bound.

### Is the thread pool starved? — `IThreadPoolStarvationService`

```text
AnalyzeProcessAsync(processId, name, durationSeconds, outputDirectory)
AnalyzeAsync(counters?, stacks?, trace?, io?)
```

A high queue is not starvation by itself — a burst that drains while the pool grows its worker
count is healthy, and a queue of zero with twelve threads parked on `Task.Result` is starving. The
verdict requires the two halves of the signature: **work waiting** (queue peak or trace backlog)
and **threads that cannot take it** (blocked threads, sync-over-async sites). States:

| State | Meaning |
|---|---|
| `starved` | queue/backlog ≥ 1 000 and not drained, with blocked threads or sync-over-async sites |
| `burst` | queue ≥ 1 000 but drained to ≤ 25 % of its peak — the pool kept up |
| `at-risk` | some back-pressure exists (blocked threads, sync-over-async, a queue ≥ 200) but not the full signature |
| `healthy` | a queue signal exists and nothing above fired |
| `inconclusive` | no queue signal (neither counters nor trace backlog) |

The live overload collects the thread-pool counters, the stacks and a `runtime` trace — not the
`threading` preset, because blocked intervals come from the sample profiler and the `threading`
preset does not enable it. `BlockingSites` lists the sync-over-async/thread-pool call sites, so
"make it async" arrives with a file and a line.

### What changed since the last measurement? — `IChangeReviewService`

```text
ReviewAsync(baselinePath, currentPath, kind="auto")
```

The comparison service produces deltas; this layer adds the three things a delta table cannot give:

1. **A unit that survives different windows.** CPU is restated as ms per second of trace,
   allocations as MB/s, exceptions as per second, contention and GC pause as ms/s. Comparing the
   raw CPU total of a 3 s trace with a 30 s trace says nothing; the normalized rate does.
2. **A significance floor per metric** (CPU 50 ms/s, allocations 5 MB/s, exceptions 5/s, GC pause
   20 ms/s, contention 50 ms/s, gen2 1/s, backlog 100 items), so a 2 % wobble is reported as
   `unchanged` instead of as a regression.
3. **A refusal.** Below 500 ms of trace on either side — or below 100 CPU samples — the answer is
   `inconclusive` with the reason, not a number.

Verdicts: `regression`, `improvement`, `unchanged`, `inconclusive`. The driver is named when the
comparison knows it: the method whose CPU grew (`cpu-regression`) or the type whose allocations
grew. Heap snapshots go through the same service (`kind="auto"` picks `heap` for two `.json`
files) with the 10 MB / 20 % floor and the growing type as the driver.

## Presets and stack walks

| Preset | Level | Sample profiler | Stack walks | Use for |
|---|---|---|---|---|
| `cpu` | Informational | yes | yes | CPU hotspots and call trees |
| `runtime` | Informational | yes | yes | general diagnosis; CPU + GC + exceptions + contention |
| `diagnose` | **Verbose** | yes | yes | everything `profile_diagnose` needs: CPU samples + allocation ticks with stacks + GC/exceptions/contention |
| `gc` | **Verbose** | no | yes | allocation ticks **with call stacks**, GC pauses, allocation sites |
| `exceptions` | Informational | no | yes | exception throw sites |
| `threading` | Informational | no | yes | contention + thread pool |
| `custom` | from the provider string | per provider | `requestStackwalk` param (default true) | anything else |

Allocation ticks are emitted only at Verbose, and call stacks are attached to allocation/exception
events only when the session requests stack walks (`EventPipeSessionConfiguration.RequestStackwalk`,
exposed as `profile_collect requestStackwalk=true`). That combination is what makes
`profile_allocations` possible; without it you still get the type totals from `profile_analyze`,
but not the site.

## How the analysis works

`profile_analyze` does two passes over the trace:

1. **Event pass** (`EventPipeEventSource`): GC start/stop and heap stats, allocation ticks with
   type names, exceptions, contention stops with duration, thread-pool worker/enqueue/dequeue
   events, JIT start/load. This produces `GcSummary`, `Allocations`, `Exceptions`,
   `ContentionSummary`, `ThreadPoolSummary`, `JitSummary`.
2. **CPU pass, only if sample-profiler events exist**: the file is converted to ETLX in a temp
   file (deleted afterwards) because in a `.nettrace` the stacks are attached to the event record,
   not emitted as events. CPU samples come from the sample profiler's **Managed ThreadSample**
   events, each carrying the sampled thread's stack. Consecutive samples of the same thread bound a
   CPU interval, External/Error samples (thread not in managed code) are not CPU, and an interval
   far beyond the thread's sampling rate means the thread was not running and is dropped. Each leaf
   frame is mapped to its `TraceCodeAddress` (module path, metadata token, IL offset) and then to
   `file:line` by the portable-PDB resolver.

   We deliberately do not use `SampleProfilerThreadTimeComputer`: it also treats allocation ticks
   and allocation samples as sampling events, so in a Verbose trace the "CPU" was attributed to the
   allocating lambda, and a thread sampled only at the start and end produced one sample carrying
   the whole trace duration (a 3 s trace reported 6 s of CPU). Both were caught by comparing the
   reader with a live process.
3. **Deep pass** (`TraceStackReader`): the same ETLX conversion is reused to read allocation ticks
   and exception events *with their call stacks*. The reader caches the model per file (path +
   mtime + size, two entries), so `profile_analyze`, `profile_allocations`, `profile_calltree`,
   `profile_hotpaths`, `profile_compare` and `profile_diagnose` do not each pay for the conversion.
   CPU samples, allocation sites, exception throw sites and the timeline all come from this one
   model; the timeline buckets CPU ms, sampled allocation bytes, GC generations/pauses, exceptions
   and contention per second.

The sample metric is milliseconds of CPU time, so `CpuHotspot.EstimatedCpuMs` is a measurement,
not a sample count. `Share` is relative to the total CPU time in the trace.

### Source resolution

The same `SourceMapResolver` is used by `profile_stacks`, `profile_analyze` and the heap's object
samples:

1. embedded portable PDB in the assembly;
2. a `.pdb` next to the module;
3. a PDB in the solution's symbol cache (`<solution>/.visualMCP/profiling/symbols`).

Run `profile_symbols(action="download", modulePaths=[…])` first when a module was shipped without
its PDB; it speaks the public symbol-server protocol (`<pdb>/<GUID><age>/<pdb>`) and caches the
result.

## Findings

| Id | Fires when | Usual fix |
|---|---|---|
| `cpu-hotspot` | top method ≥ 40 % of CPU samples (≥ 50 samples) | optimise/cache that method first |
| `cpu-concentration` | top 3 methods ≥ 80 % | inspect the shared caller in `CpuStacks` |
| `gc-pause` | GC pause ≥ 10 % of the trace (critical ≥ 25 %) | allocate less, check Server GC |
| `gen2-pressure` | ≥ 5 gen2 collections | find survivors in a heap snapshot |
| `loh-pressure` | LOH ≥ 25 % of the heap | pool large buffers (`ArrayPool<T>`) |
| `allocation-hotspot` | one type ≥ 40 % of sampled bytes | reuse/pool/struct-ify |
| `allocation-rate` | ≥ 100 MB/s sampled | follow the allocation hotspot |
| `finalizer-pressure` | ≥ 10 000 finalized objects | `IDisposable`, suppress finalizer |
| `exception-storm` | ≥ 100 exceptions (critical ≥ 1 000) | `Try*`/result types on the hot path |
| `lock-contention` | ≥ 500 ms total or ≥ 100 ms longest wait | shrink the critical section, no I/O under lock |
| `threadpool-starvation` | queue backlog ≥ 1 000 | remove blocking calls (`Task.Wait`, `.Result`, `Sleep`) |
| `jit-time` | ≥ 500 ms JIT | ReadyToRun, warm the hot paths |
| `heap-type-dominance` | one type ≥ 30 % of the heap | check for a cache without eviction |
| `heap-loh` | LOH ≥ 25 % of the heap snapshot | as `loh-pressure` |
| `finalizer-queue` | ≥ 50 000 finalizable objects | deterministic disposal |
| `duplicate-strings` | ≥ 50 MB wasted on identical strings | interning/shared lookup |
| `monitor-contention` | a monitor has waiting threads | read the holder's stack |
| `blocked-threads` | ≥ 8 threads blocked in sync waits | make the blocking call async |
| `exception-in-flight` | a thread carries an exception | the reason a hung thread is unwinding |
| `allocation-site` | one allocating method ≥ 40 % of sampled bytes | fix the allocation at that line |
| `allocation-type` | one type ≥ 40 % of sampled bytes | pool/reuse/struct-ify the type |
| `loh-allocation` | ≥ 25 % of allocated bytes are large objects | pool large buffers |
| `exception-site` | ≥ 100 exceptions and a throw site is known | replace control flow at that line |
| `cpu-spike` / `allocation-spike` | one timeline bucket ≥ 2×/3× the mean | profile the request/batch in that window |
| `cpu-saturated` | `cpu-usage` max ≥ 80 % | follow the trace's top method |
| `gc-time-high` | `time-in-gc` mean ≥ 10 % | reduce allocation volume/lifetime |
| `allocation-rate-high` | `alloc-rate` ≥ 100 MB/s | find the allocation site |
| `threadpool-queue` | `threadpool-queue-length` max ≥ 1,000 | remove blocking calls |
| `lock-contention-count` | ≥ 100 contention events in counters | profile_stacks on the holder |
| `cpu-regression` / `allocation-regression` / `gc-regression` | compare: CPU +100 ms & +20 %, type +10 MB & +25 %, gen2 +3 | inspect the delta row |
| `heap-growth` / `heap-type-growth` | compare: heap +20 % & +10 MB, one type +10 MB & +25 % | bound the cache / take a dump |
| `cpu-bound` / `gc-bound` / `pool-starvation` | diagnose: counter + trace + stacks agree | the correlated statement names the method |

Findings carry the number that produced them (`Evidence`), a recommendation, an optional concrete
next call (`SuggestedAction`) and the `Source` when known. They are leads, not verdicts: thresholds
are deliberately conservative.

## Artifacts and naming

Everything is written under `<solution>/.visualMCP/profiling/` (already in `.gitignore`):

```
trace-<process>-<pid>-<yyyyMMdd-HHmmss>.nettrace
dump-<process>-<pid>-<yyyyMMdd-HHmmss>.dmp
<trace-stem>-analysis.md          ← written by profile_analyze when writeReport=true
heap-<pid>-<timestamp>.md         ← profile_heap writeReport=true
stacks-<pid>-<timestamp>.md       ← profile_stacks writeReport=true
symbols/                          ← downloaded PDBs
monitor-settings.json             ← generated dotnet-monitor configuration
```

`profile_collect action="start"` returns a session id; `action="stop"` ends it and reports the file.
Sessions are capped by `ProfilingOptions.MaxDurationSeconds` (300 s) for the bounded form.

## Limits and troubleshooting

- **The process must be .NET and have diagnostics enabled.** A process started with
  `DOTNET_EnableDiagnostics=0` publishes no IPC endpoint and cannot be profiled.
- **Windows CPU samples need load.** The sample profiler samples *running* threads; an idle process
  produces sample events but no CPU samples, and the analyzer says so in `Warnings` instead of
  reporting a fake hotspot.
- **Dump analysis needs the matching runtime.** ClrMD loads the DAC (`mscordaccore.dll`) for the
  dump's runtime version; analysing a dump from a runtime not installed on this machine fails with a
  clear message. Install the runtime (or use `dotnet-dump` with the matching runtime) and retry.
- **Stack capture suspends the target** for the duration of the walk (`profile_stacks`) or the
  memory copy (`profile_heap`). That is a few milliseconds to a few hundred for a large heap, not a
  process pause for the whole analysis.
- **Linux needs ptrace permission** for live attach; containers usually need `--cap-add=SYS_PTRACE`.
- **dotnet-monitor listens on loopback with `--no-auth`.** Do not expose that URL; set a real
  configuration (`configPath`) when you need authentication.
- **The `.gcdump` artifact is not produced.** `profile_heap` returns the numbers a gcdump is read
  for; use dotnet-gcdump when a tool (Visual Studio, `dotnet-gcdump report`) needs the file.
- **The gc preset is Verbose and writes more data.** Allocation ticks with stacks are numerous
  (a hot allocator can produce hundreds of thousands of events per second). Prefer a short window
  (5–15 s) and `focusType` when the allocation list is large; the reader caps the sample list and
  says so in `Truncated`/warnings.
- **A `.nettrace` without stack walks still gives type totals, not sites.** If
  `profile_allocations` says every site is `(no allocation stack)`, recollect with `profile='gc'`
  or `requestStackwalk=true`.

## Tests

`src/VisualMCP.Tests/ProfilingTests.cs` covers the catalogue, the provider presets (including the
Verbose gc preset and the stackwalk flags), the rule engine on synthetic measurements and on
counter series, the report renderer, the portable-PDB source map, the PE CodeView reader
(`profile_symbols`) and the byte-level heap/stack models. The advanced layer is covered by unit
tests with a fake trace model (call-tree inclusive/exclusive math and folded output, allocation-site
aggregation, trace comparison and heap comparison) and by end-to-end tests against the test process:
a 3-second counter session must produce real EventCounters, a 2-second CPU trace collected while a
background thread burns CPU must produce real `CPU_TIME` hotspots plus a call tree and folded file,
and a 1-second `gc` trace of an allocating thread must produce allocation sites attributed to
`ProfilingTests.cs`.

`src/VisualMCP.Tests/ProfilingAdvancedTests.cs` covers the answer layer with synthetic measurements:
the four verdicts of the slowdown analysis (CPU, I/O, sync-over-async, not-slow) and its live
orchestration through fakes, memory growth with and without a correlated allocation site plus the
single-snapshot and stable verdicts, the five thread-pool states including a burst that drains, and
the change review (a CPU regression across different trace durations, the refusal on tiny windows,
and heap growth as a regression). None of these tests need a live process; they assert on the
verdict, the rank-1 evidence and the fields a tool reads.

