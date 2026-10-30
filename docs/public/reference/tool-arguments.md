# Tool arguments — the ones worth spelling out

The live schema of every tool — name, argument types, required flags and description — is served
by the server itself at **`http://127.0.0.1:4677/tools`** while it runs, and is what the MCP client
shows. This page records the arguments that are easy to get wrong, and the behaviour behind them,
for the tools where a one-line catalogue entry is not enough.

The working-directory solution is discovered and loaded on demand, so no tool below needs a
`load_solution` first unless it says so.

## Solution and project

```
list_solutions
  rootPath   – directory to scan (e.g. C:\REPOSITORY)
  maxDepth   – recursion depth (default: 3)

load_solution
  path       – absolute path to the .sln or .slnx file

get_project_info
  nameOrPath – project name (as returned by load_solution) or absolute .csproj path
```

`load_solution` is **usually unnecessary**: the first tool call discovers and loads the
working-directory solution. Call it to target a specific `.sln`/`.slnx` by path, or when a tool
reports that none could be located. It returns the project list and the workspace diagnostics.
`get_project_info` returns assembly name, language, output type, source files, project and
assembly references, and NuGet packages.

## Navigation

```
find_symbol
  symbolName   – name to search for
  partialMatch – match any symbol whose name contains symbolName (default: false)

read_symbol
  symbolName     – type or member name
  containingType – optional containing type, to disambiguate overloads or nested types
  partialMatch   – contains-match instead of exact (default: false)
  maxLines       – max lines of source per declaration, 20-1000 (default: 300)

find_implementations
  interfaceName – full or partial interface name (e.g. "IMyService" or "MyApp.IMyService")
```

`find_symbol` uses `SymbolFinder.FindSourceDeclarationsAsync`, so it is semantic, not regex.
`read_symbol` returns the declaration's **source** — the whole type body, method body, property
or field, with line numbers, and every partial declaration for a partial type. It is the single
call that replaces `search_text` + `read_file` when the goal is "show me the code of this name":
resolution is semantic (correct overloads, nested and partial types, no false positives) and the
result is plain text, not a JSON string with escaped newlines. `find_implementations` returns every
implementing type and, for each, which member satisfies each interface contract, explicit
implementations included.

## Reading files

```
read_file
  path     – absolute or relative file path
  fromLine – first line to read (1-based, optional)
  toLine   – last line to read (1-based, optional)

read_files
  paths    – file paths to read, in order (duplicates dropped)
  maxBytes – total output budget in characters (2000-190000, default 60000)

read_tree
  path                – root directory
  depth               – levels of contents to expand (1-8, default 3)
  pattern             – glob for file names, e.g. *.cs (default *)
  includeBuildFolders – include bin, obj, .git, .vs, .idea, node_modules (default false)
  maxEntries          – cap on rendered entries (50-2000, default 600)
```

`read_file` prints plain numbered source (`path (lines X-Y of N)`, then the lines). `read_files`
prints the same shape once per file; a file that fails does not abort the batch (its error is
reported in place), and when the budget runs out the remaining paths are named at the end so a
second call can ask for exactly those. `read_tree` prints an indented tree with sizes and
per-folder counts; folders deeper than `depth` are named but not expanded, and folders with more
than 30 matching files are summarised.

## Testing

```
run_tests
  projectFilter – run only projects whose name contains this string (optional)
  extraArgs     – forwarded verbatim to dotnet test (e.g. "--no-build", "--configuration Release")
```

`run_tests` runs `dotnet test`, parses every `.trx` produced, and returns the summary
(total/passed/failed/skipped/duration), a per-project breakdown, and the failed test names with
their error message and stack trace.

## Profiling

The profiling tools operate on a **running** .NET process, so they take `processId` or
`processName` instead of a solution path. They are native: none of them spawns dotnet-trace,
dotnet-counters, dotnet-gcdump, dotnet-dump or dotnet-stack. `profile_tools` reports what is still
delegated (for example the `.gcdump` file format) and can install the external tools when you need
them. The full guide is [Profiling](../guides/profiling.md).

```
profile_processes(nameFilter?)                       → pids, names, paths, working sets

profile_tools(tool?, action=status|install|update,
              scope=auto|global|local, version?, checkLatest?)

profile_collect(processId? | processName?, profile=cpu|gc|exceptions|threading|runtime|diagnose|custom,
                durationSeconds=10, providers?, bufferMb=256, requestStackwalk=true,
                action=collect|start|stop|sessions, sessionId?, outputPath?)

profile_analyze(tracePath, top=25, resolveSource=true, deep=true, writeReport=true)
                → CPU hotspots + stacks, allocation sites + types, GC, exceptions
                  with throw sites, contention, thread pool, JIT, timeline,
                  findings, next steps, Markdown report path

profile_allocations(tracePath, top=25, focusType?, resolveSource=true)
                → allocation sites (method + type + file:line), top stacks, findings

profile_calltree(tracePath, top=40, maxDepth=12, resolveSource=true,
                 writeFolded=false, outputPath?)
                → inclusive/exclusive call tree, roots, folded-stacks path

profile_hotpaths(tracePath, metric=cpu|allocations|exceptions|mixed,
                 top=10, minPercent=1, resolveSource=true, userCodeOnly=true)
                → ranked user-method fix list, root-to-leaf hot paths,
                  branch points, findings

profile_io(tracePath, top=15, minPercent=1, resolveSource=true)
                → I/O blocking sites by category/call site with blocked ms,
                  share, threads, stack and recommendation

profile_compare(leftPath, rightPath, kind=auto|trace|heap, top=25)
                → metric/per-method/per-type deltas + regression findings

profile_counters(processId? | processName?, countersFilter?, providers?,
                 durationSeconds=10, intervalSeconds=1)

profile_stacks(processId? | processName?, maxFrames=40,
               includeRuntimeFrames=false, writeReport=false)

profile_heap(processId? | processName?, top=25, focusType?,
             includeDuplicateStrings=true, writeJson=false, writeReport=false)

profile_dump(action=collect|analyze, processId? | processName?, outputPath?,
             dumpType=withheap, dumpPath?, top=25, focusType?,
             includeStacks=true, maxFrames=40)

profile_diagnose(processId? | processName?, durationSeconds=10,
                 includeHeap=true, includeStacks=true, top=15)
                → counters + trace + heap + stacks, correlated findings,
                  one Markdown diagnosis report

profile_slowdown(tracePath? | processId? | processName?, durationSeconds=10,
                 includeStacks=true, top=15, resolveSource=true, userCodeRoot?)
                → ONE verdict (cpu | gc | io-wait | sync-over-async | lock-wait |
                  blocked-native | not-slow): the cause, the evidence ranked by the
                  share of the window it explains, the runners-up and why they lost

profile_memory_growth(baselinePath?, currentPath?, processId? | processName?,
                      tracePath?, top=25, resolveSource=true)
                → growth | shrink | stable | single-snapshot, the type the heap grew
                  through, and the allocation site (file:line) when a gc trace is given

profile_threadpool(processId? | processName?, durationSeconds=10,
                   top=15, resolveSource=true, userCodeRoot?)
                → starved | at-risk | burst | healthy | inconclusive, queue/worker
                  numbers, the blocking sites

profile_review_change(baselinePath, currentPath, kind=auto|trace|heap, top=25)
                → regression | improvement | unchanged | inconclusive on per-second
                  normalized rates, the driver, and a refusal when the windows are
                  too short to compare

profile_symbols(action=list|download, modulePaths[], outputDirectory?, symbolServer?)

profile_monitor(action=status|start|stop|probe, url?, configPath?,
                path=/info|/processes|/metrics|/dump?…)
```

Artifacts land in `<solution>/.visualMCP/profiling/` (traces, dumps, reports, symbols); that
folder is git-ignored.

## Metacode

The metacode tools need no loaded solution and no running process: they read a file or a text and
print pseudocode. Python and C++ are read from their **AST JSON dumps**, not from source.

```
metacode_translate(path? | text+language=cs|py|cpp|razor|ghidra|pseudo,
                   showOrigin=false, includeComments=true,
                   normalizeControlFlow=true, flattenNestedBlocks=true)
                → the canonical pseudocode, then `// diagnostics (n)` with one line
                  per RAW construct (MC0001–MC0008)

metacode_compare(leftPath? | leftText+leftLanguage?,
                 rightPath? | rightText+rightLanguage?,
                 showOrigin=false, includeComments=true,
                 normalizeControlFlow=true, flattenNestedBlocks=true)
                → header per side, `-`/`+`/` ` diff of the canonical text with
                  long unchanged runs collapsed, per-side diagnostics after it

decompile_to_metacode(assemblyPath, typeFullName?, nameFilter?, namespaceFilter?,
                      skip=0, maxTypes=0, showOrigin=false)
                → ILSpy → C# → the metacode model, printed as pseudocode; one type
                  or a page of types, with hidden/error counts and diagnostics

analyze_native_binary(binaryPath, function?, maxFunctions=0,
                      ghidraHome?, headlessPath?, javaHome?,
                      timeoutSeconds=300, analysisTimeoutSeconds=240,
                      keepProject=false, showOrigin=false)
                → a real Ghidra headless run, then the recovered functions as
                  pseudocode with prototype + entry address; snapshot and log paths
                  in the header. Re-read the snapshot with metacode_translate
```

`analyze_native_binary` is the only metacode tool that starts an external process (Ghidra,
installed on demand); the snapshot it leaves behind is an ordinary metacode input afterwards.
Canonical pseudocode is itself an input (`language=pseudo`, or a `.mc` file), so a language
without a frontend joins by emitting it. `language` is required for text and, with a path,
overrides the extension — an unknown extension alone has no frontend. Visual Basic has no
frontend: the tool answers with that reason. Grammar and limits:
[`../../metacode.md`](../../metacode.md).

## Modernization

`aspire` installs and scaffolds Aspire on demand; `update_framework` and `update_solution` move
the solution forward and put every edited file back when the build fails. The full guide is
[Build, test, packages and modernization](../guides/runtime.md); the engineering detail is
[`../../modernization.md`](../../modernization.md).

```
aspire(action=status|install|add, appHostName?, projects[]?,
       serviceDefaults=true, installIfMissing=true, verify=true,
       includeWorkload=false, force=false, timeoutSeconds=300)
                → status: SDK band, templates/workload, AppHost/ServiceDefaults/
                  consumer projects, raw probes
                → install: dotnet new install Aspire.ProjectTemplates (+ the
                  workload on .NET 8 or on request)
                → add: AppHost + ServiceDefaults projects, sln entries, project
                  references, AddProject<T>("name") lines, AddServiceDefaults()/
                  MapDefaultEndpoints(), then a build of the AppHost; the
                  Program.cs wiring is reverted when it does not compile

update_framework(targetFramework=auto|net10.0, projectNames[]?, dryRun=true,
                 buildGate=true, updateGlobalJson=true,
                 createGlobalJson=false, timeoutSeconds=900)
                → the plan (file, property, from, to) or the applied bump; a
                  failed build restores every edited file

update_solution(targetFramework?, projectNames[]?, includePrerelease=false,
                dryRun=true, buildGate=true, updateGlobalJson=true,
                createGlobalJson=false, timeoutSeconds=900)
                → framework bump (when asked) + every outdated top-level package
                  (project files or Directory.Packages.props), then one build
                  gate with a full rollback
```
