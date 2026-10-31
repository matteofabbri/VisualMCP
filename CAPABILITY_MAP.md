# VisualMCP — Capability Map vs The Competition

> Goal: become the number one MCP tool for .NET developers.  
> Main competitors: **ReSharper**, **Rider**, **VS IntelliCode**, **GitHub Copilot Workspace**, **SonarQube**, **NDepend**, **NCrunch**, **dotTrace/dotMemory**, **CodeScene**.

---

## Engine (2026-10-28)

VisualMCP runs on **Snitch** (namespace `Snitch.*`), which lives **inside this repository** in `SNITCH\` and
which `src\VisualMCP\VisualMCP.csproj` references as `..\..\SNITCH\Snitch.csproj` (lines 35-37). The old
`Snitch.2o` (`D:\REPOSITORY\SNITCH2`) has been retired and its code no longer exists on disk.

`AddSnitch` (`SnitchServiceCollectionExtensions`) registers `SnitchContext` — the environment that discovers
and opens the solutions under the working directory — and the `I*Service` services the tools inject.
`IRoslynWorkspace` is not a separate adapter: `SnitchContext` itself implements it
(`public sealed class SnitchContext : IAsyncDisposable, IRoslynWorkspace`, `SnitchContext.cs:19`) and is
registered as such in `SnitchServiceCollectionExtensions.cs:28`; the old `SnitchContextLegacy` has been
absorbed and no longer exists (`SnitchContext.cs:46-49`, and `find_symbol` does not find it).

Many engine services return **anonymous objects**, not nameable types: the tools read their fields through
`VisualMCP.Formatting.EngineJson`, which serializes the result and reads it back onto a record with
case-insensitive binding. The exceptions are the profiling layer and metacode, which return named records
(`SlowdownReport`, `MetacodeDecompilation`, …) that the tools relay as they are, and `read_symbol`, which
uses `IRoslynWorkspace` + Roslyn directly because the engine no longer exposes the extraction of a
declaration's source.

Lost with Snitch 2 and not rebuilt: `rider_inspect`, `read_vs_state` and the native pipeline
(`inspect_native_binary`). The orphan files were removed on 2026-10-28 and remain recoverable from git
history.

---

## Snitch.Metacode (2026-10-29)

`SNITCH/Libraries/Snitch.Metacode` is a separate library that brings syntax trees of different languages
**into a single pseudocode model**, exposed by **four MCP tools**: `metacode_translate` (one source →
canonical pseudocode, with the diagnostics at the end), `metacode_compare` (two sources → pseudocode and a
line-by-line diff, which is the way to say "is it the same algorithm?"), `decompile_to_metacode` (a .NET
assembly → ILSpy → C# → the same model) and `analyze_native_binary` (a native binary → Ghidra headless → the
recovered functions in the same model). The default pipeline reads the **three source languages of the
product** — **C#** and **Visual Basic** (Roslyn, syntax only: no compilation, no loaded solution) and
**Razor** (`.cshtml`/`.razor`) — plus the library's own formats: **Ghidra** (the JSON snapshot the service
exports) and the canonical text the library prints (`PseudocodeFrontend`), so any other language comes in
simply by producing that text. **Python** (JSON dump of the `ast` module) and **C++** (`clang -ast-dump=json`)
have a reader but are **opt-in**: no tool produces those dumps, so they are not in
`MetacodeTranslator.Default` and `metacode_translate` does not offer the languages. Visual Basic has its own
frontend (`VisualBasicFrontend`): `metacode_translate`, `metacode_compare`, `metacode_index` and
`metacode_generate` read `.vb` files, and what the model does not have stays `RAW[vb]` with a diagnostic.

The same algorithm written in C#, Python and C++ comes out with the **same body**, line by line: that is the
test `The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`, which for Python and C++ builds
the translator with the corresponding reader. Language differences disappear in two passes (`for` → `while`,
nested blocks flattened) and every node keeps its origin line. A construct with no node becomes
`RAW[language] text // reason` **plus a diagnostic**: an incomplete translation is visible.

| Aspect | State | Notes |
|---|:---:|---|
| Single model (declarations, statements, expressions) | ✅ | `MetacodeDocument` + `Origin` on every node |
| C# frontend from a `SyntaxTree` | ✅ | classes, structs, interfaces, enums, records, methods, properties, fields, `using`, switch, try/catch, lambda |
| Python frontend from an `ast` JSON dump | ✅ | `range()` → `FOR` node like C#; `self` → `this`; first assignment → `DECLARE` |
| C++ frontend from a `clang` JSON dump | ✅ | classes/structs/enums, access specifiers, loops, switch, try/catch |
| Pseudocode frontend (read back) | ✅ | identical round trip, character for character, in the tests |
| Normalization passes | ✅ | `NormalizeControlFlowPass` (with a guard on `continue`), `FlattenSingleStatementBlocksPass` |
| Diagnostics instead of silent losses | ✅ | `MC0001`–`MC0008` |
| C++ templates, range-for, block-bodied lambda in the parser | ✅ | `MetacodeTypeParameter` reused for templates (`FUNCTION max<T>(…) : T`, `CLASS Box<T>`), `FOREACH` for range-for, block body extracted by `PseudocodeExpression.Parse` |
| Constructs that used to fall into `RAW` | ✅ | C#: `lock`, `yield`, `event`, `foreach` with deconstruction, `await foreach`; Python: `import`/`from … import`, decorators, comprehensions (list/set/dict/generator); C++: lambdas, type aliases (`using`/`typedef`) |
| Decompilation → metacode | ✅ | `DecompileService` implements `IDecompileService` **and** `IMetacodeDecompileService`: ILSpy's C# goes through `CSharpFrontend` and yields a `MetacodeDocument` + pseudocode per type; `decompile_to_metacode` takes one type or a page of types, with `members`/`functions`, `hidden`, `errors` |
| Native binary → metacode | ✅ | `IGhidraService` runs Ghidra headless (temporary project, `ExportNativeGraph` script, timeout that kills the process tree, `JAVA_HOME_OVERRIDE` with one retry) and `GhidraFrontend` normalizes Ghidra's C and reads it back with `PseudocodeFrontend`; `analyze_native_binary` returns the functions with prototype and address, and the snapshot remains re-readable by `metacode_translate` |
| MCP tools | ✅ | `metacode_translate` (path or text + language, `showOrigin`, diagnostics at the end), `metacode_compare` (two sources, LCS line-by-line diff with compressed context), `decompile_to_metacode` (assembly → pseudocode) and `analyze_native_binary` (native → pseudocode); none of the four requires a loaded solution |

Documentation: [docs/metacode.md](docs/metacode.md). Tests: `src/VisualMCP.Tests/MetacodeTests.cs` (11 cases),
`src/VisualMCP.Tests/MetacodeDepthTests.cs` (15 cases on the added constructs and on the full round trips) and
`src/VisualMCP.Tests/DecompilationMetacodeTests.cs` (14 cases on decompilation and Ghidra). In this work batch
they were not run — build and tests are forbidden to subagents, compilation is serialized by the workflow — so
the first execution is the gate's.

---

## Web dashboard (2026-10-29)

The server hosts a **read-only** web view of itself and of the solution store, inside the same MCP process (no
process to start), on **http://127.0.0.1:4677**. The tool is NOT an MCP tool: it is a second, minimal ASP.NET
Core instance next to the stdio transport, with the log providers zeroed because stdout belongs to the
protocol.

| Aspect | State | Notes |
|---|:---:|---|
| Port | ✅ | 4677, otherwise the one remembered in `context` (`dashboard.port`), otherwise one assigned by the system; the chosen one is saved back, so two clients on the same solution do not collide |
| A page per table | ✅ | Tables are read from `sqlite_master`, not from a fixed list: `command_log`, `applied_edit`, `context`, `board_*`, `si_*`, `ti_*`, `package_*`, `decompiled_artifact` and any future table; the FTS5 shadow tables are hidden |
| Filter, sort, pages | ✅ | Substring on every non-blob column, sortable headers, page size 25–1000, `CSV`/`JSON` |
| Overview | ✅ | Process (pid, uptime, memory, runtime, transport), loaded solutions with projects/documents, counts, latest calls and latest changes |
| Tool catalogue | ✅ | The tools the process advertises, with description and arguments (type, required, description) and a filter |
| Live log | ✅ | Tail of `debug.log` from the most recent, refresh every 2.5 s, text filter and filter by type (tool/requests/errors/warnings) |
| Schema | ✅ | Columns of every table as SQLite reports them (type, not null, pk) |
| JSON API | ✅ | `/api/health`, `/api/overview`, `/api/tables`, `/api/tables/{table}` |
| Security | ✅ | Loopback only, all GET, restricted CSP, values always encoded, table names validated against `sqlite_master` |
| A discovery probe does not start it | ✅ | Same decision as the eager load: a probe does not pay for opening the store |
| Port busy | ✅ | Warning in the log, the MCP session is unaffected |

Documentation: [docs/dashboard.md](docs/dashboard.md). Tests: `src/VisualMCP.Tests/DashboardTests.cs` (7 cases:
table discovery, filter/paging, injection refusal, blob exclusion, port policy, remembered port, and a real
listener serving every page).

---

## Legend

| Symbol | Meaning |
|---------|-------------|
| ✅ | Implemented |
| ❌ | Not implemented |
| 🔧 | Partially implemented / proof-of-concept |
| 🎯 | High priority — to implement |
| 💀 | Not applicable (out of scope for an MCP server) |

---

## 1. Navigation & Code Search

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Find a symbol by name (class, method, field) | ✅ | `find_symbol` |
| Find all references to a symbol | ✅ | `find_references` |
| Find all callers of a method | ✅ | `find_callers` |
| Find implementations of an interface | ✅ | `find_implementations` |
| Find types derived from a base class | ✅ | `find_derived_types` |
| Detailed info on a symbol (signature, doc, inheritance) | ✅ | `get_symbol_info` |
| List the members of a type | ✅ | `get_type_members` |
| Go-to-definition with the body (shows the declaration's source) | ✅ | `read_symbol` — semantically resolved, replaces `search_text` + `read_file` |
| Go-to-line | ❌ | 💀 for a pure MCP server |
| Full-text + regex search across all files | ✅ | `search_text` (regex, reads from disk) |
| **Repository full-text index (FTS5)** | ✅ | `text_index` — one row per line of every file (source, documentation, decompiled) in `.visualMCP/visualmcp.db`, identifiers split (`required` finds `GetRequiredService`), BM25 ranking, answer `file:line` + the line + context lines + the files that match most; incremental build by (length, mtime) and pruning of vanished files |
| Search by AST structure (structural search) | ❌ | 🎯 ReSharper SSR — to implement |
| Search by semantic pattern (e.g. "all methods that throw X") | ❌ | 🎯 |
| File structure / outline of a type | 🔧 | Covered by `get_type_members` |
| Recent search / MRU | ❌ | 💀 for a pure MCP server |

---

## 2. Refactoring

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Rename (with preview) | ✅ | `apply_rename` + `preview_rename` |
| Extract Method | ✅ | `extract_method` — contiguous block; inferred parameters, one trailing `return`; blocks with `await` → `async Task[<T>]` + `await`; refuses jumps/yield and any change that adds compilation errors |
| Extract Method (suggestions) | ✅ | `extract_method_candidates` |
| Extract Interface | ✅ | `extract_interface` |
| Extract Variable | ✅ | `introduce_variable` |
| Inline Method / Variable | ✅ | `inline_symbol` |
| Change Signature | ✅ | `change_signature` — reorder/add/remove; rewrites call sites node by node, per-call-site preview, refuses method groups, gate: compiler |
| Encapsulate Field | ✅ | `encapsulate_field` |
| Pull Members Up | ✅ | `pull_members_up` |
| Push Members Down | ✅ | `push_members_down` — base with a single derived type and the member used only there |
| Move Type to file/namespace | ✅ | `move_type` (file) + `move_to_namespace` (namespace, with the `using`s of the referencing files, gate: compiler) |
| Rename namespace | ✅ | `rename_namespace` — renames the last segment through Roslyn's `Renamer`: declarations, `using`s and qualified references (gate: compiler) |
| Convert to auto-property | ✅ | `property_to_auto` (refuses if the field is used elsewhere) |
| Make partial | ✅ | `type_to_partial` (marks every declaration of the type) |
| Make static (`ChangeStaticness`) | ✅ | `make_static` — rewrites the call sites, refuses if it touches instance state |
| Method → property (`Function2Property`) | ✅ | `function_to_property` — refuses if used as a method group or in `nameof` |
| Property → method (`Property2Function`) | ✅ | `property_to_function` — `GetX()`/`SetX(value)`; rewrites reads, writes, `+=` and `++`/`--`; refuses `nameof`, ref/out and initializers |
| Inline parameter (`InlineParameter`) | ✅ | `inline_parameter` — only if every call site passes the same expression, independent of the call site and with the same binding inside the method (speculative binding); gate: compiler |
| Static → extension (`Static2Extension`) | ✅ | `static_to_extension` — rotates the call sites, adds the `using` if needed |
| Introduce field | ✅ | `introduce_field` — context-free expressions only (no locals/parameters/instance) |
| Introduce parameter | ✅ | `introduce_parameter` — any expression fixed at compile time (literals, constants, `nameof`, `typeof`, `default(T)`); the call sites receive the same one |
| Invert if (`InvertBool`) | ✅ | `invert_if` — requires an `else` |
| Add params modifier (`ParamsModifier`) | ✅ | `add_params` — safe: existing call sites remain valid |
| Change nullability (`ChangeNullability`) | ✅ | `change_nullability` — gate: refuses if it introduces new CS86xx/CS87xx diagnostics |
| Rename tuple component (`RenameTupleComponent`) | ✅ | `rename_tuple_component` — semantic match: does not touch `.Count` of other types |
| Global usings (`GlobalUsings`) | ✅ | `global_usings` — gate: refuses if the global import introduces errors (ambiguity) |
| Implicit usings (`ToggleImplicitUsings`) | ✅ | `implicit_usings` — writes `<ImplicitUsings>enable\|disable</ImplicitUsings>`; when disabling, adds the explicit `using`s inferred from the semantic model and verifies the project with the `*.GlobalUsings.g.cs` document removed |
| Global → local using (`ConvertToNonGlobalUsing`) | ✅ | `convert_to_non_global_using` — removes a `global using X;` and adds `using X;` only to the files that stop compiling (compiler-driven) |
| Change type (`ChangeType`) | ✅ | `change_type` — gate: writes only if the project still compiles |
| Move type to outer scope | ✅ | `move_type_to_outer_scope` — extracts the nested type, rewrites `Outer.Inner` |
| Copy type | ✅ | `copy_type` — duplicates the type under a new name (constructors renamed) |
| Constructor → factory (`Constructor2FactoryMethod`) | ✅ | `constructor_to_factory` — private constructor + `Create`, rewrites the `new`s |
| Convert to primary constructor | ✅ | `convert_to_primary_constructor` — only when the constructor assigns parameters to fields |
| Use base type | ✅ | `use_base_type` — changes the type to a base/interface, if it compiles |
| Push members down | ✅ | `push_members_down` — base with a single derived type and the member used only there |
| Extract class | ✅ | `extract_class` — members that read only themselves → a new class, the original keeps a field + forwarding members (gate: compiler) |
| Extract superclass (`ExtractSuperclass`) | ✅ | `extract_superclass` — the selected members go into a new base class, the type inherits; the selection must be self-contained (gate: compiler) |
| Convert anonymous type to declared type | ✅ | `convert_anonymous_to_type` — `new { ... }` → class with constructor and readonly properties, creation rewritten (gate: compiler) |
| Move static members (`MoveStaticMembers`) | ✅ | `move_static_members` — static methods/properties/fields into another type; rewrites `Source.M(...)` and the bare form `M(...)`, imports the namespace (gate: compiler) |
| Move instance method (`MoveInstanceMethod`) | ✅ | `move_instance_method` — becomes static on the target with the receiver as its first parameter, `this`/implicit members re-pointed, call sites rewritten (gate: compiler) |
| Inline class (`InlineClass`) | ✅ | `inline_class` — a helper class used only as one field of its owner is absorbed: members moved in, `_helper.X` → `X`, field and type removed; any other use is a refusal |
| Move to folder (`MoveToFolder`) | ✅ | `move_to_folder` — puts the file in its namespace's folder (or an explicit one) and updates the model; new `IRoslynWorkspace.TryRemoveDocument` |
| Function ↔ indexer | ✅ | `function_to_indexer` / `indexer_to_function` — `T Get(int i)` ↔ `T this[int i]`, call sites rewritten; the call sites must be in the same file |
| Introduce part of string | ✅ | `introduce_part_of_string` — a substring of a literal becomes a `private const` and a concatenation remains (gate: compiler) |
| Style simplifications | ✅ | `simplify_code` — 87 IDE ids rewritten and 39 listed with their reason (table below), document/project/solution scope, preview, gate: compiler |
| Visual Basic style simplifications | ✅ | `simplify_vb_code` — 15 ids of the VB column (14 rewrites: IDE0001, IDE0002, IDE0003, IDE0004, IDE0049, IDE0041, IDE0029/IDE0030, IDE0031, IDE0047, IDE0054, IDE0100, IDE0075, IDE0084, IDE0081), 34 VB ids not rewritten with their reason; document/project/solution scope, preview, gate: compiler, id `IDE0084` driven by `visual_basic_style_prefer_isnot_expression` |
| Convert class to record | ✅ | `convert_to_record` — keyword change, or the positional form (`record Point(int X, int Y)`) when a constructor assigns every parameter to an auto-property of the same name (gate: compiler) |
| Convert abstract class ↔ interface | ✅ | `convert_type_kind` — both directions in one tool: the declaration and every derived type/implementer are rewritten together (`override` removed or added); instance state, structs, records and default interface methods are refused with the reason (gate: compiler over every project touched) |
| Flatten conditional / Remove nesting | ❌ | 🎯 |
| Use pattern matching | ❌ | 🎯 |
| Safe Delete | ✅ | `safe_delete` |
| Reorder Members | ✅ | `reorder_members` |
| Remove Regions | ✅ | `remove_regions` |
| Optimize Usings | ✅ | `optimize_usings` |
| Generate constructor / ToString / Equals / GetHashCode | ✅ | `generate_members` |
| Implement INotifyPropertyChanged | ✅ | `implement_inpc` |

### IDE rules rewritten by `simplify_code`

Of Microsoft's official table (code-style rules), these are the rules with a mechanical rewrite verified by the
compiler. For the engine's four structural limitations, what has been removed and what remains to do, see
[docs/engine-state.md](docs/engine-state.md).

| IDE | Rewrite |
|-----|-------------|
| IDE0001 | `System.Collections.Generic.List<int>` → `List<int>` (simple name already in scope) |
| IDE0002 | `Type.Member` → `Member` when the unqualified name binds to the same symbol |
| IDE0003 | `this.Member` → `Member` |
| IDE0004 | `(T)x` → `x` when `x` already has that type |
| IDE0010 | adds the enum members no `case` covers (only when `default` is missing) |
| IDE0011 | adds braces to an embedded statement when `csharp_prefer_braces` asks for it |
| IDE0016 | `if (x == null) throw …; target = x;` → `target = x ?? throw …;` |
| IDE0017 | `var x = new C(); x.A = 1; x.B = 2;` → `var x = new C { A = 1, B = 2 };` |
| IDE0018 | `int v; M(out v)` → `M(out int v)` when `v` is not read afterwards |
| IDE0019 | `var t = x as T; if (t != null) …` → `if (x is T t) …` |
| IDE0020 / IDE0038 | `if (x is T) { … (T)x … }` → `if (x is T t) { … t … }` |
| IDE0021 | constructor whose body is a single statement → `C(int a) => Age = a;` |
| IDE0022 | method whose body is a single `return`/expression → `int F() => 1;` |
| IDE0023 | conversion operator → expression body |
| IDE0024 | arithmetic operator → expression body |
| IDE0025 | getter-only property → `public int P => _p;` |
| IDE0026 | getter-only indexer → expression body |
| IDE0027 | `get { return _p; }` accessor → `get => _p;` |
| IDE0028 | `new List<T>()` → `[]`, `new List<T> { a, b }` → `[a, b]` |
| IDE0029 / IDE0030 | `x != null ? x : y` → `x ?? y` |
| IDE0031 | `x != null ? x.M() : null` → `x?.M()` |
| IDE0033 | `tuple.Item1` → `tuple.Name` when the tuple declares the name |
| IDE0034 | `default(T)` → `default` |
| IDE0035 | removes unreachable statements after an unconditional jump |
| IDE0036 | orders the modifiers according to `csharp_preferred_modifier_order` |
| IDE0037 | `new { name = name }` → `new { name }` |
| IDE0040 | makes the accessibility modifier explicit (or removes it if it is the default) according to `dotnet_style_require_accessibility_modifiers` |
| IDE0041 | `x == null` → `x is null` (the default operator only) |
| IDE0042 | `var t = Get(); var a = t.Item1; var b = t.Item2;` → `var (a, b) = Get();` |
| IDE0044 | adds `readonly` to a private field written only in its type's constructors |
| IDE0045 | `if (c) x = a; else x = b;` → `x = c ? a : b;` |
| IDE0046 | `if (c) return a; else return b;` → `return c ? a : b;` |
| IDE0047 | parentheses around a primary expression: removed |
| IDE0049 | `System.Int32` / `Int32` → `int` |
| IDE0051 | removes a private member no file of the solution references |
| IDE0052 | removes a private field written and never read, together with every assignment that writes it |
| IDE0053 | lambda whose body is a single statement → `x => x + 1` |
| IDE0054 | `x = x + y` → `x += y` |
| IDE0055 | reformats the file with Roslyn's `Formatter` according to `.editorconfig` (on request only: not part of `rules=all`) |
| IDE0058 | `M();` where the value is not read → `_ = M();` |
| IDE0059 | removes an assignment whose value is overwritten before being read (Roslyn dataflow) |
| IDE0060 | removes a parameter nobody reads, together with the argument at every call site — a suggestion that spans several files |
| IDE0056 | `a[a.Length - 1]` → `a[^1]` |
| IDE0057 | `s.Substring(a)` → `s[a..]`, `s.Substring(0, n)` → `s[..n]` |
| IDE0061 | local function whose body is a single statement → expression body |
| IDE0062 | local function that captures nothing → `static` |
| IDE0063 | `using (var x = …) { … }` → `using var x = …;` (bodies with no jumps only) |
| IDE0066 | `switch` where every branch produces a value → `x switch { … }` |
| IDE0070 | hand-written `GetHashCode` → `System.HashCode.Combine(…)` |
| IDE0071 | `$"{x.ToString()}"` → `$"{x}"` |
| IDE0072 | adds the missing members' arms to the switch expression, copying the discard expression |
| IDE0074 | `if (x == null) x = y;` → `x ??= y;` |
| IDE0075 | `x ? true : false` → `x` |
| IDE0080 | removes `!` (null-forgiving) where the flow state already says "not null" |
| IDE0082 | `typeof(X).Name` → `nameof(X)` (keyword types need qualified names: `nameof(string)` does not compile) |
| IDE0083 | `!(x is T)` → `x is not T` |
| IDE0090 | `new Foo(args)` → `new(args)` when the type is already fixed by the context |
| IDE0100 | `x == true` → `x`, `x == false` → `!x` |
| IDE0120 | `xs.Where(p).Any()` → `xs.Any(p)` (and `First`, `Count`, `Single`, …) |
| IDE0121 | `xs.Where(o => o is T).Cast<T>()` → `xs.OfType<T>()` |
| IDE0150 | `x is object` → `x != null` |
| IDE0160 / IDE0161 | block-scoped or file-scoped namespace according to `csharp_style_namespace_declarations` |
| IDE0170 | `{ A: { B: 1 } }` → `{ A.B: 1 }` |
| IDE0180 | `var t = a; a = b; b = t;` → `(a, b) = (b, a);` |
| IDE0200 | `x => M(x)` → `M` |
| IDE0220 | `foreach (T x in collection)` with a hidden conversion → `System.Linq.Enumerable.Cast<T>(collection)`, according to `dotnet_style_prefer_foreach_explicit_cast_in_source` |
| IDE0230 | `Encoding.UTF8.GetBytes("x")` → `"x"u8` where a span is needed |
| IDE0240 / IDE0241 | removes a `#nullable` directive that changes nothing relative to the state in force (first trivia-level rewrite) |
| IDE0250 / IDE0251 | adds `readonly` to a struct or one of its members, if the compiler allows it |
| IDE0260 | `(o as T)?.M == value` → `o is T { M: value }` (properties/fields and constant values only) |
| IDE0280 | `new ArgumentNullException("x")` → `nameof(x)` |
| IDE0300 / IDE0301 | `new T[] { a, b }` → `[a, b]`, `new T[0]` → `[]` |
| IDE0302 | `stackalloc int[] { 1, 2 }` → `[1, 2]` |
| IDE0320 | lambda that captures nothing → `static` |
| IDE0340 | `nameof(List<int>)` → `nameof(List<>)` |
| IDE0380 | removes the `unsafe` modifier when there is nothing unsafe in the member |
| IDE0390 / IDE0391 | removes `async` from a method that does not `await` and wraps the `return`s in `Task.FromResult` / `Task.CompletedTask` (IDE0391 for overrides and interface implementations) |

That is **87 ids** out of the table's 126. Three families deserve a note:

- **Option-driven** (`IDE0011`, `IDE0036`, `IDE0040`, `IDE0055`, `IDE0160`/`IDE0161`, `IDE0220`): the engine
  reads the project's `.editorconfig` (`csharp_prefer_braces`, `csharp_preferred_modifier_order`,
  `dotnet_style_require_accessibility_modifiers`, `csharp_style_namespace_declarations`,
  `dotnet_style_prefer_foreach_explicit_cast_in_source`) and acts only if the option is configured. The result
  reports in `styleOptions` what it found, so an id that did nothing explains why instead of looking broken.
  `IDE0055` is **opt-in**: it reformats the whole file, so it is not part of `rules=all`.
- **Expression body** (`IDE0021`–`IDE0027`, `IDE0053`, `IDE0061`): they fire only when the body is a single
  statement and contains no comments or directives — the rewrite removes the braces, and a comment inside would
  disappear with them.
- **`IDE0044` / `IDE0250` / `IDE0251`**: the final verdict remains with the compiler, but `IDE0044` now
  computes the set of writes to the field's symbol first. Before, it guessed and left the gate to refuse the
  *file*, which also made all the other rewrites of that file disappear.
- **Capture and unsafe** (`IDE0062`, `IDE0320`, `IDE0380`): before adding `static` or removing `unsafe` the
  engine resolves every symbol in the body. A local function or lambda that reads an outer local or parameter,
  or an instance member without a receiver, is not touched; `unsafe` stays if the member contains a pointer, a
  function pointer, `stackalloc`, `fixed` or an `&`.

The statement-level rewrites (`IDE0011`, `IDE0016`, `IDE0017`, `IDE0018`, `IDE0019`, `IDE0020`, `IDE0038`,
`IDE0042`, `IDE0045`, `IDE0046`, `IDE0063`, `IDE0066`, `IDE0180`) replace several statements with one; the
resulting nodes are reformatted by Roslyn's `Formatter`, so the indentation stays the file's.

Every actual write is recorded in the solution's history (`edit_history`, the `applied_edit` table of
`visualmcp.db`): tool, rules applied and content hashes before/after, so a rewrite is verifiable and not only
claimed.

All the other rules of the table (`IDE0040` accessibility modifiers, `IDE1006` naming, `IDE0073` file header,
…) are listed with their reason in `StyleRewriteCatalog.NotRewritten` and the tool answers with that reason
instead of ignoring the id: they are layout, team choices or semantic changes, not mechanical rewrites.

---

## 3. Code Analysis & Diagnostics

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Compiler errors and warnings (Roslyn) | ✅ | `get_diagnostics` |
| Code smells (async void, empty catch, overly long methods, etc.) | ✅ | `find_code_smells` |
| Dead code (unused types/methods/fields) | ✅ | `find_dead_code` |
| Unused symbols | ✅ | `find_unused_symbols` |
| Async anti-patterns | ✅ | `find_async_anti_patterns` |
| **Allocation anti-patterns** | ✅ | `find_performance_anti_patterns` — `string +=` in a loop, a LINQ materialiser inside a loop, a collection or fixed-size array allocated per iteration; checked against the type model, not the text |
| Static security issues | ✅ | `find_security_issues` |
| Inventory of Razor pages and Blazor components | ✅ | `razor_pages` — kind, `@page`, `@model`, layout, sections, `@inject`, handlers, code-behind |
| Markup/accessibility problems in Razor pages | ✅ | `analyze_razor_pages` — `RZ2xxx` rules with severity, `file:line` and a fix |
| Vulnerabilities in Razor pages (XSS, CSRF, secrets, CSP) | ✅ | `find_razor_vulnerabilities` — findings with CWE, severity and remediation ([docs/razor.md](docs/razor.md)) |
| Editing a Razor page | ✅ | `razor_edit` — attribute/directive/line range, `expect` as a guard, re-scan before writing, revertible history |
| Duplicate code detection | ✅ | `find_duplicate_code` |
| Complexity hotspots (cyclomatic complexity) | ✅ | `get_complexity_hotspots` |
| **Computational and spatial complexity (Big-O)** | ✅ | `analyze_complexity` — per-method estimate of time and memory: loop nesting and bounds, LINQ chains, sorting, linear lookups inside loops, recursion shape, allocation sites and methods called within the solution; evidence, confidence and optimization suggestions |
| Code metrics (LOC, maintainability index, etc.) | ✅ | `get_metrics` |
| Run Roslyn analyzers (StyleCop, Roslynator, etc.) | ✅ | `run_code_analysis` |
| ReSharper-style inspections with Rider ids/severities | ✅ | `inspect_code` (packs: roslynator, netanalyzers, asyncfixer, meziantou, stylecop) |
| **Whole-solution audit in one call** | ✅ | `solution_audit` — inspections grouped by id, compiler errors/warnings, most complex methods, largest files, project graph + cycles, first moves |
| **Apply an inspection's fix** | ✅ | `apply_code_fix` — runs the pack's CodeFixProvider (dry run by default) |
| Namespace coupling analysis | ✅ | `analyze_namespace_coupling` |
| Circular dependency detection | ✅ | `analyze_circular_namespace_dependencies` |
| DI container analysis | ✅ | `analyze_dependency_injection` |
| Project dependencies | ✅ | `analyze_dependencies` |
| Architecture rules / layer enforcement | ✅ | `check_layer_violations` |
| LINQ anti-patterns | 🔧 | `find_performance_anti_patterns` (materialisation inside a loop) and the IDE0120/IDE0121 rules of `simplify_code`; no complete rule set |
| Nullable reference analysis | ✅ | `analyze_nullability` — misused `!`, disabled nullable, unhandled flows |
| Test suite quality | ✅ | `analyze_test_quality` — tests without asserts, empty tests, non-descriptive names, `async void` |
| Input validation (parameters and DTOs) | ✅ | `find_missing_validation` — public parameters without a guard clause and DTOs (public class/record without behaviour, ≥2 settable properties) with properties lacking `System.ComponentModel.DataAnnotations` attributes |
| Cohesion (LCOM4) | ✅ | `get_cohesion_metrics` — how many independent field clusters a class has |
| Data flow analysis | ❌ | 🎯 (dataflow exists only inside `simplify_code` for IDE0059) |
| Taint analysis (security) | ❌ | 🎯 SonarQube-style |
| Code churn / historical hotspot (git-aware) | ✅ | `git_churn_hotspots` — git log (how often a file changes) × Roslyn complexity of its worst method, bounded window and read-only |
| Cognitive complexity | ✅ | `complexity_report` and `get_complexity_hotspots` — McCabe and Sonar cognitive, with the lines that produce the score |
| API surface diff (breaking change detection) | ✅ | `find_breaking_changes` (compared against a git ref) |
| **Quality trend between two revisions** | ✅ | `get_quality_trend` — two revisions (or `to=worktree`) measured with ONE set of syntactic rules: average complexity per method, methods over 10/20, long methods, TODOs, with size as context; verdict improved/regressed/unchanged, signals with direction and the files that moved most. It does not compile: a revision that does not build is still comparable |

---

## 4. NuGet & Dependencies

| Feature | Implemented | Notes |
|---------|:---:|-------|
| List installed packages | ✅ | `list_nuget_packages` |
| Add package | ✅ | `add_nuget_package` |
| Remove package | ✅ | `remove_nuget_package` |
| Update package | ✅ | `update_nuget_package` |
| Check available updates | ✅ | `check_nuget_updates` |
| Check CVE vulnerabilities | ✅ | `check_nuget_vulnerabilities` |
| Automatic vulnerability fix | ✅ | `fix_nuget_vulnerabilities` |
| Unify conflicting versions across projects | ✅ | `unify_nuget_version` |
| Locate a DLL in the local NuGet cache | ✅ | `locate_nuget_package` |
| Inspect a package's public API | ✅ | `inspect_package` |
| Central Package Management (CPM) handling | 🟡 | `update_solution` updates the `PackageVersion` rows of `Directory.Packages.props`; the file is neither created nor migrated |
| License compliance check | ❌ | 🎯 |
| Visual dependency graph | ❌ | 🎯 |
| Transitive oversharing analysis | ❌ | 🎯 |

---

## 5. Testing

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Run all tests (`dotnet test`) | ✅ | `run_tests` |
| Parse TRX results (pass/fail/skip) | ✅ | `run_tests` |
| Test coverage map | 🔧 | `get_test_coverage_map` — depends on external tools |
| Generate unit tests | ❌ | 🎯 |
| Flaky test analysis | ❌ | 🎯 |
| Mutation testing | ❌ | 🎯 Stryker integration |
| Run a specific test by name/filter | 🔧 | through `extraArgs` in `run_tests` |
| Live test feedback (NCrunch-style) | ❌ | 💀 for a pure MCP server |
| Snapshot testing helpers | ❌ | ❌ |

---

## 6. Build & Execution

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Build project/solution | ✅ | `build_project` |
| Run project | ✅ | `run_project` |
| Extract errors from a build log | ✅ | `extract_build_log_errors` |
| Run an arbitrary command | ✅ | `run_command` |
| Stop the server (releases the DLL lock) | ✅ | `stop_server` |
| Environment info (.NET SDKs, VS, OS) | ✅ | `get_environment_info` |
| Deterministic workflow (DAG of steps, in parallel) | ✅ | `run_plan` — `tool` steps (any tool of the server, by name, with the same argument names as its schema) and `command` steps (shell); `dependsOn`, `paths` (two steps with a common path never run together), `forEach`/`as`, `continueOnError`, `onFailure`; the plan is validated **before** starting (duplicate id, unknown tool or dependency, cycle → nothing runs) and the report aggregates status, duration, exit code, error and output per step |
| Fan-out over files/projects without subagents | ✅ | `run_plan` with `forEach`: one concrete step per item, `${item}` substituted in command, tool, `workingDirectory`, `paths` and string arguments; one answer instead of N calls |
| Isolation of parallel work | 🔧 | `run_plan` accepts a per-step `workingDirectory`: pointing at separate git worktrees isolates the work; creating the worktrees remains `git_run` |
| Hot reload / watch mode | ❌ | 🎯 `dotnet watch` integration |
| Multi-target build | ❌ | 🎯 |
| Publish (dotnet publish) | ❌ | 🎯 |

---

## 7. Decompilation & Reflection

All decompilation lives in **`SNITCH/Libraries/Snitch.Metacode/Decompilation/`**, namespace
`Snitch.Metacode.Decompilation`: ILSpy (`DecompileService`, `IDecompileService`, `IMetacodeDecompileService`),
the package composite (`PackageInspectionService`) and Ghidra (`GhidraService`). `Snitch.csproj` no longer
references `ICSharpCode.Decompiler`: the package belongs only to the project that decompiles.
`ManagedAssemblyDetector` (PE with a CLR header) is there to refuse a native binary with an explanation
instead of letting ILSpy fail inside the metadata.

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Decompile a type from an assembly | ✅ | `decompile_type` — **compacted** C# by default: no indentation, line breaks or comments, same tokens, about half the characters (`compact=false` for the readable form) |
| Decompile a whole assembly | ✅ | `decompile_assembly` (single file, preview, or **a folder → one file per type** with `_index.txt`); the preview is compacted, the files written stay readable and land in `text_index` immediately |
| List/select an assembly's types (internal ones too) | ✅ | `decompile_assembly` with `listOnly`, `nameFilter`, `namespaceFilter`, paged with `skip`/`maxTypes` |
| Decompile a type from a NuGet package | ✅ | `decompile_package_type` |
| Inspect an assembly's API without decompiling | ✅ | `inspect_assembly` |
| Decompile to pseudocode (metacode) | ✅ | `decompile_to_metacode` — ILSpy → C# → the metacode model: one type or a page of types, with `hidden`/`errors` per type and the (`RAW`) diagnostics at the end |
| Analyse a native binary (Ghidra headless) | ✅ | `analyze_native_binary` — Ghidra headless recovers the functions and returns them in pseudocode with prototype and address; a single `function` or `maxFunctions`; the snapshot (`functions.json`) can be re-read with `metacode_translate` without Ghidra |
| View IL (Intermediate Language) | ❌ | 🎯 |
| Decompiled diff between two versions | ❌ | 🎯 |

---

## 8. Documentation

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Read XML doc comments | ✅ | `get_xml_docs` |
| Find undocumented public APIs | ✅ | `find_undocumented_public_api` |
| Read project documentation (README, etc.) | ✅ | `read_project_docs` |
| Generate XML doc comments automatically | ❌ | 🎯 |
| Generate Markdown/HTML documentation | ❌ | 🎯 (DocFX, Sandcastle-style) |
| Changelog generation from git history | ❌ | 🎯 |

---

## 9. Git & GitHub

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Git status | ✅ | `git_status` |
| Git log | ✅ | `git_log` |
| Git diff | ✅ | `git_diff` |
| Git stage | ✅ | `git_stage` |
| Git commit | ✅ | `git_commit` |
| Create branch | ✅ | `git_branch` (list/create/delete/rename/switch) |
| Squash commit | ✅ | `git_squash` — commit-tree + reset --soft: it also works when squashing the whole branch |
| Rename a commit | ✅ | `git_amend` (HEAD) |
| Merge | ✅ | `git_merge` — default/no-ff/ff-only/squash, abort, conflicts reported |
| Three-way merge assistance | ✅ | `git_conflicts` — hunks with ours/theirs/base, resolve per hunk (ours/theirs/both/base/custom), automatic staging |
| Rebase | ✅ | `git_rebase` — start/continue/abort/skip |
| Interactive rebase | ✅ | `git_rebase action=plan` — JSON todo list (pick/reword/squash/fixup/drop with messages) applied through `GIT_SEQUENCE_EDITOR`, no editor |
| Cherry-pick / revert | ✅ | `git_cherry_pick`, `git_revert` |
| Reset | ✅ | `git_reset` — soft/mixed/hard; hard requires `confirm=true` |
| Git stash | ✅ | `git_stash` — push/list/pop/apply/drop/show/clear |
| Named patches / shelves | ✅ | `git_patch` — create (working/staged/range), list, show, apply (`--check`, `--3way`, reverse), delete |
| Worktree | ✅ | `git_worktree` — list/add/remove/prune |
| Bisect | ✅ | `git_bisect` — start/good/bad/skip/log/view/reset |
| Submodule | ✅ | `git_submodule` — status/update/init/sync/deinit |
| Tag | ✅ | `git_tag` — list/create/delete, annotated with a message |
| Remote | ✅ | `git_remote` — list/add/remove/set-url |
| Fetch / pull / push | ✅ | `git_sync` — force with `--force-with-lease` behind `confirm=true` |
| Git show | ✅ | `git_show` |
| Git blame | ✅ | `git_blame` (with a line range) |
| Arbitrary git command | ✅ | `git_run` — covers reflog and filter-repo |
| GitHub repo | ✅ | `gh_repo` — list/view/create/fork/clone/edit/delete |
| GitHub PR | ✅ | `gh_pr` — list/view/create/merge/close/reopen/comment/review/review-comments/checks/diff/checkout/ready |
| GitHub inline review | ✅ | `gh_pr action=review` — comments anchored to `path`/`line`/`side`, event comment/approve/request-changes |
| GitHub check runs | ✅ | `gh_pr action=checks` — status, bucket, link of every check on the PR |
| GitHub branch protection | ✅ | `gh_branch_protection` — get/checks/rules (public rulesets, detail with an admin token) |
| GitHub issue | ✅ | `gh_issue` — list/view/create/close/reopen/comment |
| GitHub release | ✅ | `gh_release` — list/view/create/upload/delete |
| GitHub Actions | ✅ | `gh_actions` — list/view/logs/rerun/cancel/workflows/dispatch |
| GitHub secrets | ✅ | `gh_secret` — list/set/delete |
| GitHub CLI passthrough | ✅ | `github` (gh command) |
| GitLab / Azure DevOps / Bitbucket | ✅ | `git_host` — PR/MR, comments, merge, issues/work items, pipelines; provider and coordinates from the remote, token from the environment |
| Review brief for a diff (Roslyn-aware) | ✅ | `code_review` — files by kind, risk checklist, new TODOs with their line, worst methods of the changed files, optional findings |
| Conventional commits enforcement | ❌ | 🎯 |

---

## 10. Benchmarking & Performance

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Run BenchmarkDotNet | ✅ | `run_benchmarks` |
| Quick benchmark of an inline snippet | ✅ | `quick_benchmark` |
| CPU profiling (dotTrace-style) | ✅ | `profile_collect` (EventPipe, cpu preset) + `profile_analyze` — per-method hotspots with CPU ms, share, stack and `file:line`; native, no external tool |
| Memory profiling (dotMemory-style) | ✅ | `profile_heap` (types, LOH, pinned, finalizable, largest objects, duplicate strings) + `profile_dump` (frozen snapshot, heap + stacks) |
| Allocation analysis (heap) | ✅ | `profile_analyze` — allocation ticks per type, sampled bytes, share; **`profile_allocations`** attributes the bytes to the method that allocates, with type, `file:line` and full stack (requires the `gc` preset: Verbose + stackwalk); rules `allocation-hotspot` / `allocation-site` / `loh-allocation` |
| Performance timeline | ✅ | `profile_analyze` — 1 s buckets with CPU ms, sampled allocations, GC (gen0/1/2 and pauses), exceptions and contention; rules `cpu-spike` / `allocation-spike` |
| Inclusive/exclusive call tree | ✅ | `profile_calltree` — tree with inclusive/exclusive time per node, caller chain, folded-stacks export for FlameGraph/speedscope |
| Multiple hot paths (VS-style, several fixes) | ✅ | `profile_hotpaths` — list of methods/lines to fix aggregated over every stack (`Variants`), root-to-leaf paths with user frames, split points (`Branches`) where the cost divides; metrics cpu / allocations / exceptions / mixed |
| I/O blocks by site | ✅ | `profile_io` — `External` intervals attributed to the blocking stack: categories File/Http/Network/Database/Pipe/Console/SyncOverAsync/Lock/Sleep/WaitHandle, call site with `file:line`, blocked ms, share, threads, stack; thread-pool idle excluded; rules `io-block`, `sync-over-async`, `thread-pool-blocking`, `thread-blocking`, `io-pressure` |
| Comparison and regressions | ✅ | `profile_compare` — two traces (CPU per method, allocations per type, GC, exceptions, contention, backlog) or two heap JSONs; rules `cpu-regression`, `allocation-regression`, `heap-growth`; `profile_heap writeJson=true` |
| Multi-signal diagnosis | ✅ | `profile_diagnose` — counters + trace + stacks + heap in one call, correlated findings (`cpu-bound`, `gc-bound`, `pool-starvation`) and a single report |
| "Why is it slow" verdict | ✅ | `profile_slowdown` — cpu/gc/io-wait/sync-over-async/lock-wait/blocked-native weighted in the same unit (seconds of the window explained), one verdict, ordered evidence and the candidates that lost with their reason; from a live process (counters + `diagnose` trace + stacks) or from an existing trace |
| "Why is memory growing" verdict | ✅ | `profile_memory_growth` — growth/shrink/stable/single-snapshot; comparison of two heap JSONs (`profile_heap writeJson=true`) or a snapshot of a live process that writes the JSON for next time, with the grown type correlated to the allocation site (`file:line`) when a `gc` trace is present |
| "Is the thread pool starved" verdict | ✅ | `profile_threadpool` — starved/at-risk/burst/healthy/inconclusive; requires both a full queue **and** blocked threads, names the blocking site with `file:line`; counters + `runtime` trace + stacks |
| "Is this change a regression" verdict | ✅ | `profile_review_change` — regression/improvement/unchanged/inconclusive over rates normalised per second (CPU ms/s, allocations MB/s, exceptions/s, …), significance thresholds per metric; it is `inconclusive` only when both windows are under 500 ms, while with fewer than 100 CPU samples the CPU metric does not enter the judgement (the verdict can still come from the others) |
| Cause in the code (join with the metacode AST) | ✅ | `profile_root_cause` — every measured site (CPU, allocations, blocked time, exceptions) is attached to the declaration that contains it in the **metacode AST**: loop chain with depth and header, LINQ materialiser per iteration, allocation/concatenation inside a loop, `await`/`lock` in the loop, `Task.Result`/`.Wait()`; the declaration is translated from the file on disk (C#/VB/Razor frontend), cached by timestamp |
| .NET async analysis | ✅ | `profile_async` — `System.Threading.Tasks.TplEventSource`: tasks scheduled/started/completed/exceptional, exact `await` durations (TaskWaitSend/Stop pairs), named async operations, **state machines never completed** (`IncompleteAsyncMethod`); `async` preset |
| Database analysis | ✅ | `profile_database` — EventSource of EF Core/SqlClient/Npgsql/MySql/SQLite/Oracle/Db2: commands with text and duration, slow commands, repeated normalised shapes (N+1 signal), connection opens, share of the window in data access; `database` preset; with no events it names the providers present and how to recapture |
| Events Viewer | ✅ | `profile_events` — inventory of ANY provider/event: count, rate, payload field names and a sample event; no provider-specific parser needed, so it also works on custom providers |
| Instrumented (exact) profiling | ✅ | `profile_instrumentation` — `action=instrument` rewrites the IL (Mono.Cecil + injected EventSource probe, `.map.json`, probe DLL next to the output, include/exclude wildcards), `action=analyze` reads the enter/exit pairs: **exact call counts and inclusive/exclusive times**, no sampling; `instrumentation` preset |
| Live EventCounters (dotnet-counters) | ✅ | `profile_counters` — native time series with min/max/mean/last |
| Managed stacks + contended locks (dotnet-stack) | ✅ | `profile_stacks` — every thread, monitors with their owning thread, blocking rules |
| Dump collect/analyze (dotnet-dump) | ✅ | `profile_dump` — `WriteDump` + ClrMD analysis (heap, stacks, sync blocks) |
| Diagnostics toolchain installer | ✅ | `profile_tools` — status/install/update of the 7 tools, with native vs delegated coverage |
| Symbols (dotnet-symbol) | ✅ | `profile_symbols` — CodeView, download from the symbol server, cache for `file:line` resolution |
| dotnet-monitor (HTTP sidecar) | ✅ | `profile_monitor` — start/stop/probe on loopback (`--no-auth`) |

> Profiling was introduced on 2026-10-29 in the separate library `SNITCH/Libraries/Snitch.Profiling`
> (EventPipe + ClrMD + TraceEvent); documentation in `docs/profiling.md`. On the same day the advanced layer
> was added: attribution of allocations to site/line, call tree, timeline, comparison/regression and
> correlated diagnosis. The answer layer (`Verdicts/`: `ISlowdownService`, `IMemoryGrowthService`,
> `IThreadPoolStarvationService`, `IChangeReviewService`, with the shared `AnswerReport` envelope) is exposed
> by the four verdicts above: the existing `profile_*` tools remain the way to measure a single signal.

---

## 10bis. Platform & maintenance

| Feature | Implemented | Notes |
|---------|:---:|-------|
| System info | ✅ | `host_system` — OS family (windows/linux/macos), architecture, default newline, path case-sensitivity |
| A file's line-ending style | ✅ | `host_system path=…` — existing file → `.editorconfig` → `.gitattributes` → neighbouring files → OS |
| Line-ending normalization | ✅ | `fix_line_endings` — file/directory/project/solution scope, `auto`/`lf`/`crlf`, preview |
| Writing with the right style | ✅ | every writer goes through `FileText`: a refactoring no longer changes the file's convention, and new files follow the repository's |
| Command log | ✅ | `command_log` table in the SQLite database (date, time, tool, arguments) written on every call; read with `command_log` |

---

## 11. Runtime Communication & Infrastructure

| Feature | Implemented | Notes |
|---------|:---:|-------|
| HTTP invocation (REST client) | ✅ | `http_invoke` |
| SignalR connect / disconnect | ✅ | `signal_r_connect` / `signal_r_disconnect` |
| SignalR subscribe to hub events | ✅ | `signal_r_subscribe` |
| SignalR invoke hub method | ✅ | `signal_r_invoke` |
| SignalR read buffered events | ✅ | `signal_r_events` |
| Docker status + container list | ✅ | `docker_status`, `docker_ps` |
| Docker container: config and logs | ✅ | `docker_inspect` (full JSON), `docker_logs` (tail, timestamps, since) |
| Docker lifecycle | ✅ | `docker_control` (start/stop/restart/pause/unpause/kill), `docker_rm` (force, volumes) |
| Docker run | ✅ | `docker_run` — name, ports, env, volumes, network, command, detach |
| Docker images | ✅ | `docker_images` (list/inspect), `docker_pull`, `docker_image_rm` |
| Docker networks / volumes / stats | ✅ | `docker_networks`, `docker_volumes`, `docker_stats` |
| Docker compose up/down/ps/logs | ✅ | `docker_compose` |
| Docker prune | ✅ | `docker_prune` — requires `confirm=true` |
| Kubernetes integration | ❌ | ❌ (out of scope) |
| gRPC testing | ❌ | 🎯 |
| WebSocket testing | ❌ | 🎯 |

---

## 12. File System & I/O

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Load a solution (.sln/.slnx) | ✅ | `load_solution` |
| List solutions in a directory | ✅ | `list_solutions` |
| List a directory (with glob) | ✅ | `list_directory` |
| Directory tree to depth N (prunes bin/obj/.git) | ✅ | `read_tree` |
| Read a file (with offset and line limit) | ✅ | `read_file` |
| Read several files in one call (output budget) | ✅ | `read_files` |
| Move / rename a path | ✅ | `move_path` |
| Project info (framework, output, packages) | ✅ | `get_project_info` |
| Validate a solution's structure | ✅ | `check_solution` |
| Create a file from a template | 🔧 | through `run_command` |
| Delete a file/folder | ❌ | 🎯 |
| File system watch (live changes) | ❌ | 💀 for a pure MCP server |

---

## 13. Scaffolding & Project Setup

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Add a LICENSE | ✅ | `add_license` — 20 licenses (permissive, copyleft, public domain, source-available) from embedded templates; `list_licenses` for the catalogue with SPDX id, family and patent grant |
| Add CONTRIBUTING.md | ✅ | `add_contributing` |
| Add a GitHub Actions CI workflow | ✅ | `add_ci_workflow` |
| Add/remove projects to/from a solution | ✅ | `add_projects_to_solution` / `remove_projects_from_solution` |
| Create a new project (`dotnet new`) | 🟡 | `aspire(action='add')` creates AppHost and ServiceDefaults from the Aspire templates; the other templates remain `run_command`'s job |
| CRUD scaffold from a model | ❌ | 🎯 |
| EF Core migration scaffold | ❌ | 🎯 |
| `.editorconfig` / `global.json` template | ❌ | 🎯 |
| `.gitignore` template | ❌ | 🎯 |

---

## 14. Planning

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Create a task checklist | ✅ | `create_task_checklist` |
| Update a task checklist | ✅ | `update_task_checklist` |
| Kanban board (SQLite) | ✅ | `board_add`/`board_update`/`board_move`/`board_comment`/`board_list`/`board_show`/`board_board`/`board_delete` — 5 columns, priority, labels, assignee, comments, change history |
| Plan a feature change (impact analysis) | ✅ | `plan_feature_change` |

---

## 15. Self-Management

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Check for an available update | ✅ | `check_for_update` |
| Self-update from GitHub Releases | ✅ | `self_update` |
| Download a file from a URL | ✅ | `download_file` |
| List the available analysis tools | ✅ | `list_analysis_tools` |

---

## 16. Solution modernization (2026-10-30)

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Install Aspire on demand (templates/workload) | ✅ | `aspire` — `dotnet new list aspire` and `dotnet workload list` for diagnosis; `dotnet new install Aspire.ProjectTemplates` without elevation; the workload only on SDK 8 or on request |
| Scaffold Aspire in the solution | ✅ | `aspire(action='add')` — AppHost + ServiceDefaults, `dotnet sln add`, references from the executable projects, `AddProject<T>` and `AddServiceDefaults()`/`MapDefaultEndpoints()`, then a build of the AppHost with rollback of the `Program.cs` files only |
| Update the target framework | ✅ | `update_framework` — changes only the value between the tags (no XML rewrite), keeps the platform suffixes, does not touch `netstandard`/`net4x`, syncs `global.json`, preview by default |
| Update the solution's packages | ✅ | `update_solution` — `dotnet list package --outdated`, versions written into the csproj or into the `PackageVersion` rows of `Directory.Packages.props`, a single build gate |
| Compiler gate with full rollback | ✅ | `update_framework`/`update_solution` rebuild the solution and, if the build fails, rewrite every modified file as it was |
| TFM choice from the installed SDKs | ✅ | `auto` → `dotnet --list-sdks` → the newest band (`net10.0`) |

---

## 17. Non-.NET toolchain: Kubernetes, Node and SQL (2026-10-31)

Closes section 13 of [docs/gaps.md](docs/gaps.md): the "workaround" rows became tools, or a written boundary
with the reason. 355 → **373 tools**.

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Cluster state, kubeconfig contexts | ✅ | `k8s_status` (client/server/context/reachability + helm version), `k8s_context` (`current`/`list`/`use`) |
| Cluster resources as data | ✅ | `k8s_get` — rows (name, namespace, phase or `N/M ready`, restarts, age) from `get -o json`; `output=yaml\|json\|wide\|name\|jsonpath=…` for the raw view; multiple kinds comma-separated |
| Pod diagnosis | ✅ | `k8s_describe` (events, conditions, containers, scheduler), `k8s_logs` (`tail`, `since`, `container`, `previous`) |
| Writing to the cluster with confirmation | ✅ | `k8s_apply` (server-side dry run by default; applies only with `dryRun=false` + `confirm=true`), `k8s_delete`, `k8s_rollout` (`status`/`restart`/`undo`) |
| Helm releases | ✅ | `k8s_helm` — `list`, `status`, `history`, `get-values`, `template` (offline render with `-f`/`--set`), `repo-list`; a missing helm is an answer, not an exception |
| JS/TS project and package manager detection | ✅ | `node_info` — the nearest `package.json`, `packageManager` → lockfile → npm, node and manager versions, scripts, dependencies, installed tsc/eslint |
| npm/pnpm/yarn install and scripts | ✅ | `node_install` (lockfile, `ci`/`--frozen-lockfile`, add with `dev`, `remove`), `node_script` (arguments after `--`, a missing script refused with the list) |
| TypeScript diagnostics | ✅ | `node_typecheck` — `tsc --noEmit --pretty false` parsed into `TSxxxx` diagnostics with `file(line,col)`, counts and raw output when nothing parses; a missing TypeScript is reported with the command that installs it, never downloaded |
| ESLint diagnostics | ✅ | `node_lint` — `eslint --format json` into findings (file, rule, severity, position), counts and most frequent rules; `fix=true` runs `--fix` |
| JS dependency health | ✅ | `node_outdated` and `node_audit` normalise the npm, pnpm and yarn 1 shapes (severity, vulnerable range, available fix) |
| SQL inventory | ✅ | `sql_files` — lines, statements, verbs, tables, findings per file; `bin`/`obj`/`node_modules`/VCS excluded |
| SQL diagnostics | ✅ | `analyze_sql` — SQL0001–SQL0010 (lexical, correctness, security, performance) with `file:line:column`, source line and fix; comments and literals masked before matching |
| Dialectal SQL parser / schema | ❌ | There is none: the scanner checks the shape, not that a database accepts the statement (§1.6 of gaps.md) |

---

## 18. ReSharper & CodeMaid: templates, cleanup profiles, layout (2026-10-31)

Closes the last ❌/⚠️ rows of [docs/resharper-codemaid-coverage.md](docs/resharper-codemaid-coverage.md):
**373 → 375 tools**.

| Feature | Implemented | Notes |
|---------|:---:|-------|
| Live / postfix / file templates | ✅ | `templates` — 18 live, 10 postfix (`.if`, `.null`, `.var`, `.await`, …) and 8 file (class, interface, record, struct, enum, static class, abstract class, xUnit test); `expand` replaces the shortcut line or inserts after the anchor, `create` generates the file, `save`/`delete` maintain the team store `.visualMCP/templates.json`; compiler gate |
| Named cleanup profiles | ✅ | `code_cleanup profile=` — `full`/`reformat`/`style`/`usings`/`regions`/`reorder`/`safe` plus the team profiles in `.visualMCP/cleanup-profiles.json`; `saveProfileAs` writes one |
| CodeMaid layout rules | ✅ | `layout_code` — reflows `//` comments to a width, joins a line range (refused if the tokens would change), normalises blank lines; compiler gate |
| Fuzzy symbol search | ✅ | `find_symbol` — beyond exact/prefix/CamelHumps/contains, a subsequence fallback (`custord` → `CustomerOrder`) when nothing matches |
| Context action: the second tranche | ✅ | `context_action` — 36 ids: merge/split conditions, null-check merged with the use, `use_var`/`use_explicit_type`, split declaration, remove braces, `#nullable enable`, digit separators, `ConfigureAwait(false)` |
| Create from usage: shapes beyond CS1061 | ✅ | `create_from_usage` — CS0117 (static member), CS1501/CS7036 (overload from the call site) in addition to CS1061/CS0246/CS0103 |

---


## Summary by Category

Counting: one ✅ row and one ❌ row per section table; the 🔧 (partial) and 💀 (out of scope) rows do not enter
the total, and every row below is the count of its section.

| Category | Implemented | Missing | % Coverage |
|-----------|:---:|:---:|:---:|
| Navigation & Search | 9 | 4 | 69% |
| Refactoring | 38 | 4 | 90% |
| Code Analysis | 22 | 7 | 76% |
| NuGet & Dependencies | 10 | 3 | 77% |
| Testing | 2 | 5 | 29% |
| Build & Execution | 8 | 3 | 73% |
| Decompilation | 7 | 2 | 78% |
| Documentation | 3 | 3 | 50% |
| Git & GitHub | 36 | 1 | 97% |
| Benchmarking | 21 | 0 | 100% |
| Platform & maintenance | 5 | 0 | 100% |
| Runtime & Infrastructure | 13 | 3 | 81% |
| File System & I/O | 9 | 2 | 82% |
| Scaffolding | 4 | 4 | 50% |
| Planning | 4 | 0 | 100% |
| Self-Management | 4 | 0 | 100% |
| Metacode (pseudocode) | 12 | 0 | 100% |
| Modernization & upgrade | 6 | 0 | 100% |
| **TOTAL** | **212** | **41** | **84%** |

The section tables are frozen at 2026-10-30 and do not contain the **14 tools added that day** (SSR, cleanup
and formatting, the "create" family, the inspection baseline, seven analyses), the **18 closed on 2026-10-31**
(9 Kubernetes/Helm, 7 Node/JS-TS, 2 SQL), the **2 of section 18** (`templates`, `layout_code`) nor
`get_quality_trend` (2026-10-31, the quality trend between two revisions: section 3 carries its row). The live
count is `capability_report`: **381 tools**.

---

## Priority gaps vs ReSharper

Updated on 2026-10-31. The four entries that were here have been closed: `structural_search` (SSR),
`check_layer_violations` (layer enforcement), `generate_docs` (XML doc) and `convert_to_record` (already
implemented, it had been listed by mistake). In the same session `code_cleanup`, `format_code`,
`implement_members`, `create_from_usage`, `inspection_baseline`, `export_dependency_graph`,
`get_cohesion_metrics`, `analyze_test_quality`, `find_missing_validation`, `analyze_nullability` and
`find_breaking_changes` also arrived.

What remains, in order of impact:

| ReSharper / Rider feature | Priority | State |
|---------------------------|:---:|-------|
| Interactive debugger (breakpoints, stepping, watch, evaluate, debugging a test) | 🔴 High | ✅ closed on 2026-10-30: 16 `debug_*` tools on netcoredbg (DAP), `debug_test` included; edit-and-continue and breakpoints by exception type remain outside |
| Language/platform scope: **XAML/WPF/WinUI/MAUI/Avalonia**, **JS/TS/HTML/CSS**, **SQL/EF Core migrations**, **Kubernetes/Helm**, **F#** | 🟡 Medium | ⚠️ closed on 2026-10-31 for XAML (inventory + XAML0001–0009 + navigation), Razor, SQL (SQL0001–SQL0010 scanner), EF migrations (`ef_migrations`), Kubernetes/Helm (`k8s_*`) and the Node toolchain (npm/pnpm/yarn, tsc, eslint). **JS/TS** (toolchain only) and **F#** (build/test only: it is not a Roslyn language) remain without a semantic model |
| Test flow: debugging a test, continuous testing, "which tests cover this symbol", scaffolding, mutation testing | 🟡 Medium | ✅ closed: `run_test`, `rerun_failed`, `debug_test`, `continuous_testing`, `tests_covering_symbol`, `generate_tests`, `run_mutation_tests`, `run_affected_tests`, `find_flaky_tests` |
| Decompiled code as a workspace (go-to-definition, find usages) and IL view | 🟡 Medium | ✅ closed for navigation: `assembly_workspace` (search/source/references/outline over a Roslyn workspace per MVID) + `disassemble` for IL; rename and debugging in decompiled code remain impossible (there is no source to write) |
| Diagrams and exploration: interactive type/project graph, sequence diagram, structure view, code vision, TODO explorer | 🟢 Low | ⚠️ `export_dependency_graph` (projects/namespaces/types), `call_graph`, `sequence_diagram`, `control_flow_graph`, `track_value`, `find_todos`, `find_endpoints`, `solution_map` and `find_file` exist; rendering is the client's job |
| Rest of the "create/generate" family and context actions: long tail and live/postfix/file templates | 🟡 Medium | ⚠️ `context_action` covers 26 ids; `implement_members`, `create_from_usage`, `generate_docs`, `generate_dto_from_json`, `generate_builder` cover the frequent cases; templates and the long tail remain |
| Inspection administration (per-scope severity, suppressions, `.DotSettings`, daemon with warm compilation) | 🟡 Medium | ✅ closed: `configure_inspection`, `suppress_finding`, `read_dot_settings`, `custom_inspections`, `naming_rules`, `find_spelling_issues`, `background_analysis`/`analysis_findings`; only ReSharper's proprietary catalogue remains outside |
| Local history, three-way merge assistance, GitLab/Azure DevOps | 🟢 Low | ✅ closed on 2026-10-31: `local_history`, `git_conflicts` (conflict model), `git_rebase action=plan`, `git_patch`, `git_host` (GitLab/Azure DevOps/Bitbucket), inline review and `code_review` |
| Minor refactorings: Extract Members to Partial, Inline Field, Transform Parameters, Extension → static, Invert Boolean, Make non-static, Move Type into Another Type, Generify | 🟢 Low | ✅ closed on 2026-10-31 (`extract_members_to_partial`, `inline_field`, `transform_parameters`, `extension_to_static`, `invert_boolean`, `make_non_static`, `move_type_to_nested_scope`, `generify`) |
| Taint analysis and quality trend between commits | 🟡 Medium | ❌ not closable with an isolated tool: it needs a data-flow engine, and respectively the findings saved per revision (see §12 of gaps.md) |
| Real-time collaborative editing | 💀 | Out of scope: shared transport and editor surface, not a local stdio tool |

The complete inventory — language by language and area by area, with what exists today, what exactly is
missing and where the boundary ends — is in **[docs/gaps.md](docs/gaps.md)**. The ✅ picture category by
category remains in [docs/resharper-codemaid-coverage.md](docs/resharper-codemaid-coverage.md).

## Priority gaps vs SonarQube/CodeScene

| Feature | Priority | Notes |
|---------|:---:|-------|
| Taint analysis | 🟡 Medium | SQL injection / XSS tracked over the data flow; `find_security_issues` is pattern-based |
| Code churn hotspot (git-aware) | 🟡 Medium | CodeScene killer feature |
| Mutation testing (Stryker) | 🟡 Medium | A real quality gate |

---

*Last updated: 2026-10-30*
