# State of the engine and of the solution store

Last updated: 2026-10-30. This document says, without beating around the bush, **what already exists**,
**what is missing** and **why it is missing** — the four structural limitations of the rewrite engine, the
coverage of Microsoft's IDE table, and the state of the solution database
(`<solution>/.visualMCP/visualmcp.db`).

Legend: ✅ done · 🟡 partial · ⬜ missing · ❌ out of scope by choice.

---

## 1. What exists today

| Component | Where | State |
|---|---|---|
| IDE rule engine (`simplify_code`) | **neutral core** `SNITCH\Languages\Style\` (`StyleCore.cs`, `StyleOptions.cs`, `StyleRewriteService.cs`) + **C# layer** `SNITCH\Languages\CSharp\Style\` (`StyleRewrites*.cs`, `CSharpStyleLanguage.cs`, `CSharpStyleRuleSet.cs`) | ✅ **87 ids** rewritten, **39** listed with their reason, 126 in total (count over `SNITCH\Languages\CSharp\Style\`); the VB layer extends them ([docs/adding-a-language.md](adding-a-language.md)) |
| **Visual Basic layer** (`simplify_vb_code`) | `SNITCH\Languages\VB\Style\` (`VbStyleLanguage.cs`, `VbBlocks.cs`, `VbStyleRewrites.cs`, `VbStyleRewriteCatalog.cs`, `VbSimplifyCodeService.cs`) + `VbServiceCollectionExtensions.AddSnitchVisualBasic()` | ✅ **15 VB ids** (14 rewrites: IDE0001, IDE0002, IDE0003, IDE0004, IDE0049, IDE0041, IDE0029/IDE0030, IDE0031, IDE0047, IDE0054, IDE0100, IDE0075, IDE0084, IDE0081) and **34** listed with their reason; same `StyleRewriteService`, so same gate and same edit history |
| MCP tool | `simplify_code(scope, filePath, projectName, rules, dryRun)` for C# · `simplify_vb_code(scope, filePath, projectName, rules, dryRun)` for VB | ✅ two distinct tools: the container holds one style service per language (`ISimplifyCodeService` / `IVbSimplifyCodeService`), so neither can shadow the other |
| **Context action** (`context_action`) | `SNITCH\Languages\CSharp\ContextActions\` (`ContextActionService.cs` + the four partials: `Usings`, `NullChecks`, `Wrapping`, `Strings`) | ✅ **13 actions** in four families that neither the IDE rewrites nor the refactorings reached: type import, argument guards, surround-with on a line range, string literal conversions. Every action resolves its target from a line, previews, and goes through the compiler gate; the inventory of ReSharper's 307 C# context actions is in [resharper-context-actions.md](resharper-context-actions.md) |
| **Decompilation → metacode** | `SNITCH\Libraries\Snitch.Metacode\Decompilation\` (`DecompileService.cs` with `IDecompileService` + `IMetacodeDecompileService`, `MetacodeDecompilation.cs`) + `SnitchMetacodeServiceCollectionExtensions.AddSnitchMetacode()` | ✅ ILSpy's C# goes through `CSharpFrontend` and yields a `MetacodeDocument` + pseudocode per type; tool `decompile_to_metacode`. This extension is the only one that registers `IDecompileService` and `IPackageInspectionService` (removed from `AddSnitch`) |
| **Native binary → metacode** | `SNITCH\Libraries\Snitch.Metacode\Decompilation\` (`GhidraService.cs`, `GhidraFrontend.cs`, `GhidraC.cs`, `GhidraSnapshot.cs`) | ✅ Ghidra headless (temporary project, `ExportNativeGraph.java`, timeout that kills the process tree, `JAVA_HOME_OVERRIDE` with one retry) + C normalization and re-read through `PseudocodeFrontend`; tool `analyze_native_binary`, snapshot re-readable by `metacode_translate` |
| Compiler gate | `StyleRewriteService.ErrorsAsync` (`StyleRewriteService.cs:608`): per project, all documents together and then one by one to isolate the culprit; the refused file is left untouched | ✅ per document, **not** per single change |
| Output formatting | `Formatter.FormatAsync(..., Formatter.Annotation)`: reformats **only** the nodes the engine produced, not the file | ✅ |
| Options channel | `StyleOptions` reads **five** C# keys from `AnalyzerConfigOptions` (`CSharpStyleLanguage.cs:49-73`): `csharp_prefer_braces`, `csharp_preferred_modifier_order`, `csharp_style_namespace_declarations`, `dotnet_style_prefer_foreach_explicit_cast_in_source`, `dotnet_style_require_accessibility_modifiers`; the VB layer reads three of its own (`visual_basic_preferred_modifier_order`, `dotnet_style_require_accessibility_modifiers`, `visual_basic_style_prefer_isnot_expression`) | 🟡 five C# keys and three VB ones out of all the IDE options |
| **Symbol index** | `SNITCH\Libraries\Snitch.Metacode\Symbols\` (`SymbolIndexBuilder`, `CSharpSymbolIndexFrontend`, `SymbolIndexStore`, `SymbolIndexService`) + tables `si_symbol`, `si_usage`, `si_metacode`, `si_document` in the same `visualmcp.db`; tool `reference_index` (`build`/`status`/`find`/`describe`/`refs`/`callers`/`callees`/`hierarchy`/`outline`/`metacode`/`unused`) | ✅ incremental by content hash: where a member is declared, every use **with how it is used** (`call`, `read`, `write`, `instantiate`, `inherit`, `implement`, `override`, `attribute`, …), the member that contains it, and the **metacode tree** of every declaration ([metacode.md](metacode.md#the-symbol-index)) |
| **Sources in the database** | tables `source_document` + `mc_document`/`mc_tree`/`mc_node` in `visualmcp.db`, written by the metacode index (the same pass that reads the file) | ✅ every **text file** of the solution — Roslyn documents in any language, Razor read from disk, and `README`/`.csproj`/`.json` — with text, language, hash and timestamp; a file with no frontend still lands here **and in the AST** as a `File` node, and every AST row carries `source_path` (file or binary) and the `decompiled` flag, so source code and decompiled code coexist without being confused |
| Package facts | tables `package_fact` and `package_dependency` + tool `package_facts` | ✅ from `obj/project.assets.json`, with no restore and no network |
| Decompiled artifacts | table `decompiled_artifact` + `decompile_assembly(persist=true)` | ✅ keyed on MVID, reused |
| Edit history | table `applied_edit` + tool `edit_history`, with the previous text compressed | ✅ and revertible |
| Undo | tool `revert_edit` on the history id, checking that the file has not changed since | ✅ |
| Call log | table `command_log` + tool `command_log` | ✅ |
| Inspection report cache | table `context`, keyed on document path + mtime | ✅ |
| Solution kanban | tables `board_item`, `board_comment`, `board_history` | ✅ |
| Tests | **574** `[Fact]`/`[Theory]` methods in `src/VisualMCP.Tests` (count over the source), which `dotnet test` runs as **742** cases; of which **17** on the `context_action` family (`ContextActionTests`) and **13** on the store (`SolutionStoreTests` 8 + `StoreTests` 5) | ✅ |

---

## 2. The four structural limitations

These are the limitations that explain why most of the remaining IDE rules are not implementable "given how
the engine is built". Three have been removed; only #3 remains partial, by choice (the `var` and naming
preferences are not rewrites).

| # | Limitation | State | What removing it would take | What already exists as a piece |
|---|---|---|---|---|
| 1 | **A single contiguous region, in a single document.** `StyleMatch` is `(Node … Last) → Replacement` | ✅ | — | `StyleMatch.SpanEdits` plus the three-phase planning described below: a multi-file suggestion is resolved against **the whole solution**, every project it touches goes through the gate, and the suggestion is applied **all or nothing** |
| 2 | **The gate only proves that it compiles, not that it is equivalent.** A semantic error that compiles gets through | ✅ | — | the semantic gate described below: `IOperation` comparison for rules that declare equivalence |
| 3 | **No channel for the repository's options** | 🟡 | read `AnalyzerConfigOptions` — **done** for five C# keys and three VB ones; missing are the `csharp_style_var_*` preferences (which contradict each other: IDE0007 vs IDE0008) and the naming options (which need a rename, not a rewrite) | `StyleContext` + `StyleOptions` + `RequiredOption` + `IsOptIn` |
| 4 | **There is no "delete" verb.** A rule replaces a node, it does not remove it | ✅ | — | `StyleMatch.Removes`: IDE0035 (unreachable code), IDE0051 (private member never referenced), IDE0052 (field written and never read) |

### 1. Workspace edit: how it works now

Planning happens in three phases (`StyleRewriteService.SimplifyAsync`), because a multi-file suggestion does
not belong to one project:

1. **Syntactic rewrites** for every document, collected per project.
2. **Multi-document suggestions** (`StyleMatch.SpanEdits`) resolved against the whole solution: every named
   file is looked up in *any* project (`FindDocument`), and the resulting documents join the plan of the
   project that owns them.
3. **Compiler gate** for every project touched, using `CompilationCache`. A multi-file suggestion then
   survives **only if every one of its documents was accepted**: if one is refused, the others are withdrawn
   from the plan before any write.

A file already rewritten as a syntax tree is a block: another suggestion's offsets would point at text
about to disappear, so the second one is refused with the reason.

### 2. Semantic gate: what it proves and what it does not

`StyleRule.ClaimsEquivalence` marks the rules that must leave the meaning intact at the operation level
(today IDE0001, IDE0002, IDE0003, IDE0047, IDE0049). For those, `StyleRewriteService` takes the rewritten
region, substitutes it into a throwaway tree, and compares the `IOperation` tree before and after
(`SemanticEquivalence`):

- implicit operations and the receiver of a qualified access are ignored (`this._x` and `_x` are the same
  thing);
- it compares operation type, operator, constant value, referenced symbol and number of children;
- a difference **refuses the file** with rule, line and the first differing paths; the result reports it in
  `semantics { compared, verified, failures }`.

A node with no operation of its own (a type name in a signature, an expression in parentheses) **cannot be
compared**: it is skipped and counts neither as verified nor as failed, instead of producing a fake proof.
Rules that are not marked stay with the compiler gate alone: the `semantics` field says how many were
compared, not how many exist.

### What each one would unlock

- **(1) workspace edit** → the half of the ReSharper catalogue that is intrinsically multi-file (rename,
  change signature, moving a member and updating its callers).
- **(2) semantic gate + cache** → rules whose error *compiles* are compared at the `IOperation` level when
  they declare equivalence; per-change isolation is cheap because the "before" comes from the cache and the
  "after" is a reparse.
- **(3) options** → already used by IDE0011, IDE0036, IDE0040, IDE0055, IDE0073, IDE0160/IDE0161.
- **(4) deletion** → IDE0035, IDE0051, IDE0052; IDE0110 stays out with its reason.
- **(5) composition** → IDE0005, IDE0032 and IDE0290 are reached by `simplify_code` through delegation.

---

## 3. Coverage of the IDE table

**87 rewritten + 39 with a written reason = 126 ids**, meaning the official table is fully accounted for.
The line-by-line detail is in [CAPABILITY_MAP.md](../CAPABILITY_MAP.md); here only the summary.
**The Visual Basic layer counts separately** — it is the VB column of the table (shared `dotnet_style_*` +
`visual_basic_style_*`): 15 ids rewritten and 34 with a reason in `VbStyleRewriteCatalog`, exposed by
`simplify_vb_code`; ids that VB does not define at all (pattern matching, braces, expression bodies) stay in
the C# catalogue.

| Group | Count | Examples | **What the engine is missing** (not "it is out by choice") |
|---|---|---|---|
| Delegated to other tools | 3 | IDE0005, IDE0032, IDE0290 | ✅ `simplify_code` accepts them and hands them to `optimize_usings` / `property_to_auto` / `convert_to_primary_constructor` |
| Contradicting pairs | 3 | IDE0007 vs IDE0008, IDE0048 vs IDE0047 | **an option**: these are team choices, not defects |
| Change the contract | 3 | IDE0050, IDE0064, IDE0110 | an **API decision** (a record becomes a tuple, a field becomes writable, a discard may be intentional) |
| Layout and policy | 13 | IDE0065, IDE0130, IDE0210/0211, IDE0221, IDE0360, IDE2000–2006 | **project decisions**: where the usings go, namespace vs folder, the shape of blank lines |
| Documented target lists | 2 | IDE0076, IDE0077 | the **allowed targets** Microsoft accepts for `SuppressMessage` |
| Feature-specific | 8 | IDE0078, IDE0303/0304/0305, IDE0330, IDE0350, IDE0410, IDE1005 | builder contract, async analysis, lock patterns, lambda conversion |
| Not applicable to C# | 3 | IDE0081/0084/0140 (VB) | these are Visual Basic: two of the three **exist** in the VB layer (IDE0081, IDE0084) |
| Naming, compiler, Copilot | 3 | IDE1006, IDE1007, IDE3000 | a **rename**, a compiler diagnostic, a non-deterministic suggestion |
| Covered by another id | 1 | IDE0370 | ✅ Microsoft split it into IDE0240/IDE0241, already rewritten |

**No id is left in the "implementable without new features" group:** the last one (IDE0220) is done, and so
are IDE0010, IDE0072, IDE0120 and IDE0121, which had been classified as "analysis" out of laziness rather
than a technical limit.

**A correction of framing:** "out by choice" was a convenient and wrong classification. Apart from the 5
non-applicable ones (Visual Basic, compiler diagnostic, Copilot), every group is stuck on **a missing
feature**, not on a decision. The table below names it, and what it unlocks.

### The missing features, in order of payoff

| Feature | State | What it unlocks |
|---|---|---|
| **Removal of a node** (`StyleMatch.Removes`) | ✅ done | IDE0035, IDE0051, IDE0052 |
| **The solution in the context** (`StyleContext.Solution` + `IsPristine`) | ✅ done | rules whose answer lies outside the document |
| **Switch completion over enums** (semantics, no new feature) | ✅ done | IDE0010 and IDE0072 |
| **LINQ chains** (expression rewrites) | ✅ done | IDE0120, IDE0121, IDE0220 |
| **Nullability flow** (`Nullability.FlowState`) | ✅ done | IDE0080 |
| **Trivia edits** (`#nullable` directives) | ✅ done | IDE0240, IDE0241 (and IDE0370, which Microsoft split into these two) |
| **Accessibility option** (`dotnet_style_require_accessibility_modifiers`) | ✅ done | IDE0040 |
| Persistent reference index | ✅ done | makes those answers fast and repeatable |
| **Incremental compilation cache** | ✅ done | the gate's "before" is free and every candidate is judged by reparsing only the changed document (`CompilationCache`) |
| **Semantic gate** (`IOperation`) | ✅ done for rules that declare equivalence | makes safe the rules whose error compiles; for the others the compiler alone remains |
| **Multi-document workspace edit** | ✅ done | any rule can touch several files of the solution, atomically |
| **Roslyn dataflow** (`AnalyzeDataFlow`) — dead assignments | ⬜ | no longer blocks any id: IDE0059 was done another way |
| **Read/write classification** on references | ✅ done | IDE0052 |
| **Trivia edits** for suppressions | ✅ done | IDE0079: only the `[SuppressMessage]` form and only compiler ids (`CS####`), judged with `WithReportSuppressedDiagnostics(true)` — the compiler honours the attribute, so the normal list is already filtered |
| **`file_header_template`** | ✅ done | IDE0073 — inserts the header and never replaces it, because the existing one is repository text |
| **Composition of the three tools** | ✅ done | IDE0005, IDE0032, IDE0290: the two per-symbol services are reached by an enumeration of candidates inside `SimplifyCodeService`; they are not part of `rules=all` |

**The cheapest way to gain coverage** was to compose the three existing services inside `simplify_code`
(IDE0005, IDE0032, IDE0290): done, and they are counted in the 87. The next step is not another IDE id — it
is the `context_action` family (`docs/resharper-context-actions.md`): the actions that neither a style
rewrite nor a refactoring reaches, with the inventory of ReSharper's 307 C# context actions and the 13
implemented.

---

## 4. The solution store

The database is `<solution>/.visualMCP/visualmcp.db`, SQLite, scoped per solution. Project rule: **only
what is derived and rebuildable goes in, stamped with the hash of its inputs**; the code on disk remains the
only truth. A datum without a freshness hash produces stale analysis, and stale analysis is worse than
slowness because it is silent.

The **schema is an EF Core model**, not six `CREATE TABLE` strings: `SNITCH/Storage/Schema/`
(`VisualMcpDbContext.cs` declares tables, keys, indexes and columns; `VisualMcpSchema.cs` generates the
script from the model and creates only what is missing). The six stores — `VisualMcpStore`, board, text
index, metacode, symbols, native — still write SQL by hand on their own connections, because different
paths (bulk index insert, transactions, compressed blobs) would only be made opaque by a query provider;
but none of them owns the DDL any more. The two exceptions to the model are declared: `ti_line` is an FTS5
virtual table and stays SQL in `VisualMcpSchema`, and `applied_edit.before_text` is added in place on a
database created before the column existed.

| Datum | State | Key | What it is for |
|---|---|---|---|
| Inspection report | ✅ | document path + mtime | don't recompute `inspect_code` on every call |
| Call log | ✅ | increasing id | audit, "what was done and when" |
| Edit history + previous text | ✅ | file + timestamp | audit of the rewrites **and revert** (`revert_edit`), only if the file has not changed since |
| Document fingerprints | ✅ | path, with the content hash | says which files changed: it is the freshness key of the index, the gate and future caches |
| Reference index (symbol → documents and spans) | ✅ | file hash, rebuilt only for changed files | `who` (who uses a symbol), `unused` (non-public members never referenced), impact of a change |
| **Metacode AST** | ✅ | path + declaration_key + node_id | every node of every declaration (`parent_id`, `slot`, `ordinal`, `depth`, span, JSON payload): a structural question is a query, and `metacode_index action=ast` returns it nested. A type nests its members, so a member's nodes appear twice — the price of per-declaration completeness |
| NuGet package facts (resolved version, TFM, edges) | ✅ | id + version + TFM, from `project.assets.json` | "this API exists in the version we target", who pulls a package in |
| Decompiled artifacts | ✅ | assembly MVID | the decompiled output is not lost and ILSpy is not run twice |
| **Incremental compilation cache** | ✅ | references already closed + per-file hash (`SNITCH/Languages/Gate/CompilationCache.cs`) | makes per-change isolation in the gate cheap. Note: Roslyn trees are not serializable, so a cross-process "parse cache" does not exist — what can be persisted is the fingerprint, and that is what is there |
| **Public surface per package version** | ⬜ | id + version | requires reading the package assemblies: it builds on the decompiled artifacts, so it comes later |
| **Synthetic solution from the decompiled output** | ❌ | — | the native (Ghidra) pipeline that built it is no longer in this repo: the store is ready (artifacts per MVID) if it comes back |

Operational note: **`visualmcp.db` is no longer tracked by git** (it left the index): a binary file that
changed on every call dirtied every diff, and the folder is recreated when needed.

---

## 5. Recommended order

| Step | State | Cost | Unlocks |
|---|---|---|---|
| 1. Document fingerprints + reference index | ✅ done | medium | `who`, `unused`, impact of a change; and it is the key to an incremental gate |
| 2. Decompiled artifacts per MVID | ✅ done | small | the decompiled output is reused instead of rerunning ILSpy |
| 3. Package facts + graph | ✅ done | small | resolved version per TFM, who pulls a package in |
| 4. Edit history with the text + `revert_edit` | ✅ done | small | every write is undoable (if the file has not changed since) |
| 5. Incremental compilation cache for the gate | ✅ done (`SNITCH/Languages/Gate/CompilationCache.cs`) | medium | per-change isolation: a wrong rule stops refusing the whole file |
| 6. Compose the three existing tools inside `simplify_code` | ✅ done (`SimplifyCodeService` delegates and the style service accepts the ids) | small, but per-symbol | IDE0005, IDE0032, IDE0290 |
| 7. Low-cost remaining rules | ✅ done: IDE0073, IDE0079, IDE0039 | small | +3 ids |
| 8. Workspace edit (multi-document, atomic) | ✅ done | medium | the multi-file family of refactorings |
| 9. Semantic gate (`IOperation` diff) | ✅ done for rules that declare equivalence | large | rules whose error compiles |

---

## 6. How to verify the state yourself

- Active rules and their reasons: `simplify_code` with an unsupported id returns `notRewritten` with the
  reason; `StyleRewriteCatalog.Supported` / `.NotRewritten` are the two lists in the engine.
- Visual Basic rules: the same two lists are `VbSimplifyCodeService.Rules` / `.NotRewritten`
  (`VbStyleRewriteCatalog`); `simplify_vb_code` with an id VB does not rewrite returns `notRewritten` with
  the reason, and with `scope=solution` it touches only the `.vb` files of the loaded solution.
- Options read: the result of `simplify_code` includes `styleOptions` when a rule driven by `.editorconfig`
  is requested (for VB it also reports `preferIsNot`).
- What is in the index: `reference_index(action=status)` reports documents, symbols, declarations, uses and
  the per-kind counts (declarations and uses).
- Where a member is declared, what its signature is called, who uses it and **how**: `reference_index` with
  `action=find|describe|refs`; `action=callers|callees` for the call hierarchy, `action=hierarchy` for
  bases/interfaces/derived/override, `action=outline` for the shape of a type or a file, `action=unused`
  for what nobody uses.
- Packages and dependencies: `package_facts(action=list|dependencies)` after an `action=refresh`.
- History and undo: `edit_history` (every row says whether it is `revertible`) and `revert_edit(id)`.
- Decompilation to pseudocode: `decompile_to_metacode(assemblyPath, typeFullName=…)` returns the header with
  `type(s)`, `hidden` and `error(s)`; `analyze_native_binary` returns the recovered functions, the snapshot
  and log paths and the `RAW`/`MC0008` diagnostics.
- Where every number in this document comes from: `StyleRewriteCatalog.cs`, `VisualMcpStore*.cs` and the
  tests in `src/VisualMCP.Tests/` (`SimplifyCode*.cs`, `SolutionStoreTests.cs`).
