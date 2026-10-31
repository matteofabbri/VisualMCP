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
  fuzzy        – when nothing matched, fall back to a subsequence match (default: true)

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

profile_collect(processId? | processName?,
                profile=cpu|gc|exceptions|threading|runtime|diagnose|async|database|instrumentation|custom,
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

profile_root_cause(tracePath, top=15, minShare=0.5, userCodeRoot?, resolveSource=true)
                → each measured site (cpu | allocations | blocked | exceptions) joined
                  with the metacode AST of the declaration that contains it: the loop
                  chain and nesting depth, the LINQ materialiser, the allocation or
                  string concatenation inside the loop, the await/lock, the
                  Task.Result/.Wait(); plus per-site explanations and findings

profile_async(tracePath)
                → scheduled/started/completed/exceptional task counts, exact await
                  durations (TaskWaitSend/TaskWaitStop pairs) with the longest first,
                  named async operations, and the state machines still alive at the
                  end of the trace (IncompleteAsyncMethod)

profile_database(tracePath, top=15)
                → commands with text and duration, the slowest ones, the normalised
                  shapes that cost the most (the N+1 signal), connection opens,
                  the share of the window in data access; no-event answers name the
                  providers the trace did contain

profile_events(tracePath, provider?, event?, top=50)
                → every provider/event pair with count, rate, payload field names
                  and one sample event; works on custom providers (Events Viewer)

profile_instrumentation(action=instrument|analyze, assemblyPath?, outputPath?,
                        include?, exclude?, tracePath?, mapPath?)
                → instrument: rewrite the assembly's IL (Mono.Cecil, injected probe,
                  .map.json, probe DLL copied beside the output), with wildcard
                  include/exclude on the full method name
                → analyze: exact call counts and inclusive/exclusive times from the
                  enter/exit pairs, ranked by cost and by call count

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
overrides the extension — an unknown extension alone has no frontend. Visual Basic is a source
language (`language=vb`, or a `.vb` file, which `metacode_translate`, `metacode_compare`,
`metacode_index` and `metacode_generate` read through Roslyn). Grammar and limits:
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

## Templates, style and layout

```
templates(action=list|expand|create|save|delete, template?, filePath?, line=1, values?,
          name?, projectName?, namespaceName?, directory?, kind?, body?, description?,
          dryRun=true)
                → values are the $holes$ of the template, "key=value;key2=value2";
                  expand replaces the line when its trimmed text is the live shortcut,
                  otherwise inserts after it; a postfix template needs the line to end
                  with '.name' (order.if, value.null, GetOrders().var). create writes a
                  file template (class, interface, record, struct, enum, static-class,
                  abstract-class, xunit-test) and adds the document to the project;
                  save/delete keep the team's .visualMCP/templates.json

code_cleanup(scope=document|project|solution, filePath?, projectName?, steps?, profile?,
             saveProfileAs?, dryRun=true)
                → steps or profile, never both; the built-in profiles are full,
                  reformat, style, usings, regions, reorder and safe, and the team's
                  own live in .visualMCP/cleanup-profiles.json
                  ({"profiles":{"team":{"steps":["style","usings","format"]}}}).
                  saveProfileAs records the effective step list there.

layout_code(scope=document|project|solution, filePath?, projectName?,
            rules=comments,blank_lines, maxCommentWidth=120, fromLine?, toLine?,
            dryRun=true)
                → join needs fromLine and toLine; an edit that would change the token
                  stream is refused (a multi-line string, a comment); comments and
                  blank lines apply to C# and Visual Basic
```

## Version control and collaboration

```
git_conflicts(action=list|show|resolve, path?, strategy=ours|theirs|both|base|custom,
              blockIndex?, content?, stage=true, repoPath?)
                → list: the operation in progress (merge/rebase/cherry-pick/revert)
                  and every conflicted file with its hunks (labels, line counts,
                  first lines)
                → show: one file's hunks with all three sides and the diff3 text
                → resolve: rewrite the chosen side over one hunk or the whole file;
                  stage it when no marker remains

git_rebase(action=start|continue|abort|skip|plan, onto?, plan?, repoPath?)
                → plan: onto is the commit BEFORE the first step and plan is
                  [{"commit":"abc1234","action":"pick|reword|squash|fixup|drop",
                    "message":"…"}] — reword and squash take their message from the
                  step (squash without one keeps the previous message); applied
                  through GIT_SEQUENCE_EDITOR, no editor is opened

git_patch(action=create|list|show|apply|delete, name?, target?, path?,
          reverse=false, check=false, confirm=false, directory?, repoPath?)
                → create: target = working (default) | staged | a revision/range;
                  patches live in <git-dir>/visualmcp-patches unless directory is set
                → apply: check=true only validates, reverse=true undoes it, and a
                  plain apply that fails is retried with --3way

git_host(action=detect|list-prs|view-pr|create-pr|comment-pr|merge-pr|
                list-issues|create-issue|pipelines,
         provider=auto|gitlab|azuredevops|bitbucket, hostUrl?, project?,
         repository?, number?, title?, body?, sourceBranch?, targetBranch?,
         state?, limit=20, confirm=false, repoPath?, timeoutSeconds=60)
                → detect: provider/host/project/repository parsed from the remote
                  and whether a credential is in the environment
                → project = GitLab group/project path, Azure DevOps project,
                  Bitbucket workspace; Azure self-hosted form:
                  hostUrl=https://dev.azure.com/{organization}
                → credentials: GITLAB_TOKEN; AZURE_DEVOPS_EXT_PAT (or
                  AZURE_DEVOPS_TOKEN); BITBUCKET_TOKEN, or BITBUCKET_USERNAME +
                  BITBUCKET_APP_PASSWORD

gh_pr(action=…|review|review-comments|checks, comments?, reviewEvent?)
                → review: comments is a JSON array
                  [{"path":"src/A.cs","line":42,"side":"RIGHT","body":"…"}];
                  reviewEvent = comment (default) | approve | request-changes;
                  line is a line of the diff, side RIGHT = the new file

gh_branch_protection(action=get|checks|rules, branch?, repo?)
                → get: protected flag plus the rules when the token has admin;
                  checks: strict mode and the required contexts/checks;
                  rules: the repository rulesets that apply (public API)

code_review(baseRef=HEAD, path?, maxFiles=100, includeInspections=false,
            repoPath?, timeoutSeconds=120)
                → changed files by kind, additions/deletions, the risk checklist,
                  new TODO/FIXME markers with their new-file line, the worst
                  methods of each changed C# file and, with includeInspections,
                  the inspection findings inside the changed files

get_quality_trend(from=HEAD~1, to=HEAD, repoPath?, path?, top=10)
                → both sides measured with ONE syntactic rule set (a revision
                  through `git archive`, the working tree from disk): average
                  complexity per method, methods over 10 and 20, long methods
                  (over 40 lines), TODO markers — files/lines/types/methods and
                  the complexity total are context and never change the verdict
                → returns improved|regressed|unchanged, every signal with its
                  direction and percent delta, the files whose complexity moved
                  most in each direction, and the added/removed files
                → to=worktree measures the uncommitted tree; a path narrows the
                  measurement to one folder or .cs file. Nothing is compiled, so
                  a revision that no longer builds is still comparable
```

`code_review` is the brief, not the review: `run_affected_tests` runs the tests the change reaches,
`find_breaking_changes` compares the public surface, and `gh_pr action=review` posts the review with
its line comments. GitHub is served by the `gh` CLI; `git_host` covers GitLab, Azure DevOps and
Bitbucket over their HTTP APIs, and both are read-only until `confirm=true`.

## Kubernetes, Node and SQL

```
k8s_status(timeoutSeconds=30)

k8s_context(action=current|list|use, name?, timeoutSeconds=30)

k8s_get(kind, name?, namespaceName?, allNamespaces=false, selector?,
        output?, timeoutSeconds=60)
                → output omitted = summary rows parsed from `get -o json`
                  (name, namespace, phase or "N/M ready", restarts, age);
                  output=yaml|json|wide|name|jsonpath=… returns the raw view

k8s_describe(kind, name?, namespaceName?, allNamespaces=false, timeoutSeconds=60)

k8s_logs(pod, container?, namespaceName?, tail=200, since?, previous=false,
         timeoutSeconds=45)

k8s_apply(file?, content?, namespaceName?, dryRun=true, confirm=false,
          timeoutSeconds=120)
                → exactly one of file/content; dryRun=true is the SERVER-side
                  validation (--dry-run=server) and changes nothing; applying
                  needs dryRun=false AND confirm=true

k8s_delete(kind, name, namespaceName?, ignoreNotFound=false, confirm=false,
           timeoutSeconds=120)

k8s_rollout(action=status|restart|undo, kind=deployment, name?, namespaceName?,
            toRevision?, confirm=false, timeoutSeconds=180)

k8s_helm(action=list|status|history|get-values|template|repo-list, release?,
         chart?, namespaceName?, valuesFile?, setValues[]?, timeoutSeconds=120)
                → template renders offline with -f/--set; list and history ask
                  for -o json; a missing helm is reported, not thrown

node_info(directory?)
                → nearest package.json above directory (default: cwd), the
                  package manager (packageManager field → lockfile → npm),
                  declaredPackageManager, node/manager versions, scripts,
                  dependency counts, typescriptInstalled/eslintInstalled

node_install(directory?, packages[]?, dev=false, remove=false,
             frozenLockfile=false, timeoutSeconds=300)
                → no packages = the lockfile install (npm ci when frozenLockfile);
                  dev=true adds --save-dev; remove=true uninstalls

node_script(script, directory?, args[]?, timeoutSeconds=600)
                → <manager> run <script> [-- args…]; an unknown script is refused
                  with the list of the ones package.json declares

node_typecheck(directory?, project?, emit=false, timeoutSeconds=300)
                → node_modules/.bin/tsc --noEmit --pretty false, parsed into
                  TSxxxx diagnostics; missing TypeScript is reported with the
                  install command, never downloaded

node_lint(directory?, pattern?, fix=false, timeoutSeconds=300)
                → node_modules/.bin/eslint --format json, parsed into findings
                  with rule, severity and position; topRules summarises

node_outdated(directory?, timeoutSeconds=180)
node_audit(directory?, timeoutSeconds=180)

sql_files(path?, maxFiles=500)
                → per file: lines, statements, verbs, tables, finding count;
                  bin/obj/node_modules/.git are skipped

analyze_sql(path?, minSeverity=info, rule?, maxFiles=500, maxFindings=200)
                → SQL0001-SQL0010, each with rule, severity, file:line:column,
                  the source line and a fix; unknown rule ids are refused with
                  the catalogue. A pattern scan, not a dialect parser.
```
