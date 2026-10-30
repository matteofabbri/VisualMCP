[Docs home](../README.md) › Guides › **Refactoring and style**

# Refactoring and style

VisualMCP can change your code, not just read it. This guide explains what makes those changes
different from a find/replace, which tools preview before writing, and how to use the style engine
without surprises.

## What "semantic refactoring" means here

Every refactoring in the catalogue:

1. **Resolves the symbol** through Roslyn — the exact declaration, overload or override you named.
2. **Rewrites the affected references**, in the same file and across the solution — call sites,
   interface implementations, `using` directives, constructor calls.
3. **Refuses rather than guesses** when the operation would change meaning or cannot be expressed
   (a method group in `nameof`, an object initializer, a base class that uses the member, a
   non-self-contained selection).
4. **Records the write** in the solution's edit history, with the previous text, so it can be audited
   and reverted.

A rename through `apply_rename` updates the declaration and every real reference. A find/replace renames
the same text in comments, strings and unrelated types, and usually misses implicit interface
implementations.

## The safety ladder

| Stage | What it gives you |
|---|---|
| **Read-only previews** | `preview_rename`, `extract_method_candidates`, `inspect_code` — see the problem or the shape before touching anything |
| **Dry run** | Most write tools accept `dryRun`; where the default is `true`, the result is a before/after pair and a line diff per edit, and nothing is written |
| **Compiler gate** | Risky operations compile the project after the change and refuse it if it would add errors; the file is left untouched |
| **Targeted guard** | `razor_edit` takes `expect` (the text that must currently be there) and re-scans the page before writing |
| **Edit history** | `edit_history` lists every write; `revert_edit(id)` restores the file exactly, and refuses if the file changed after the edit |
| **Your own safety net** | A branch and a clean `git status` are still the cheapest rollback |

**Which tools write by default?** The schema is the source of truth, but the split is worth knowing:

- **Preview-first (`dryRun` defaults to `true`):** `simplify_code`, `simplify_vb_code`, `apply_code_fix`,
  `razor_edit`, and most refactorings — `extract_method`, `extract_class`, `extract_superclass`,
  `change_type`, `change_nullability`, `use_base_type`, `copy_type`, `constructor_to_factory`,
  `convert_to_primary_constructor`, `convert_to_record`, `add_params`, `invert_if`, `make_static`,
  `property_to_auto`, `property_to_function`, `static_to_extension`, `type_to_partial`,
  `introduce_field`, `introduce_parameter`, `push_members_down`, `move_to_folder`, and others.
- **Write-through by default:** `apply_rename`, `change_signature`, `safe_delete`, `generate_members`,
  `optimize_usings`, `reorder_members`, `remove_regions`, `move_type`, `pull_members_up`,
  `inline_symbol`, `introduce_variable`, `encapsulate_field`, `implement_inpc`, `extract_interface`.

When in doubt, use the read-only counterpart first (`preview_rename`, `extract_method_candidates`,
`inspect_code`) or pass `dryRun=true` explicitly.

## The catalogue by task

The full list is in the [tool catalogue](../tools.md#refactoring). Grouped by intent:

### Rename and move

`apply_rename` / `preview_rename`, `rename_namespace`, `rename_tuple_component`, `move_type`,
`move_type_to_outer_scope`, `move_to_folder`, `move_to_namespace`, `move_static_members`,
`move_instance_method`.

Renames are semantic: parameter names, backing fields, `nameof`, XML docs and interface members follow.
`move_instance_method` converts the method to a static one on the target type, with the old receiver as
its first parameter, and rewrites `a.M(x)` to `Target.M(a, x)`.

### Extract and inline

`extract_method`, `extract_class`, `extract_superclass`, `extract_interface`, `extract_method_candidates`,
`inline_class`, `inline_symbol`, `inline_parameter`, `introduce_variable`, `introduce_field`,
`introduce_parameter`, `introduce_part_of_string`.

`extract_method` handles a contiguous statement block. A block that `await`s becomes an `async Task`
method; a single trailing `return expr;` becomes the new method's return value. It refuses blocks it
cannot prove equivalent — `yield`, `break`/`continue`/`goto`, a return that is not last, a write to a
pre-existing local.

### Signatures and types

`change_signature`, `change_type`, `change_nullability`, `use_base_type`, `add_params`,
`convert_to_primary_constructor`, `convert_to_record`, `convert_anonymous_to_type`,
`constructor_to_factory`, `copy_type`, `function_to_indexer`, `indexer_to_function`,
`encapsulate_field`, `property_to_auto`, `property_to_function`, `function_to_property`.

`change_signature` is the one to reach for when a parameter is added, removed or reordered: it returns
every call site as a before/after pair, refuses method-group uses, and is gated on the compiler.
`change_nullability` is gated on the *nullability* diagnostics specifically: it is refused when adding
`?` would introduce new CS86xx/CS87xx warnings.

### Inheritance and membership

`pull_members_up`, `push_members_down`, `extract_superclass`, `implement_inpc`, `generate_members`,
`make_static`, `static_to_extension`, `type_to_partial`.

`push_members_down` only moves a member into its single derived type, and refuses when the base type
uses it. `extract_superclass` refuses a member selection that reads fields staying behind, naming the
offending member instead of producing code that does not compile.

### Usings, files and cleanup

`global_usings`, `implicit_usings`, `convert_to_non_global_using`, `optimize_usings`, `reorder_members`,
`remove_regions`, `safe_delete`, `invert_if`.

`safe_delete` verifies with Roslyn that the symbol has zero references. If it has any, it lists them and
does nothing. `implicit_usings` toggles `<ImplicitUsings>` and, when disabling, writes the explicit
`using` directives the files actually relied on — verified by compiling with the generated global-usings
document removed.

### Complexity

`reduce_complexity` is the bridge between analysis and refactoring: it plans the moves that remove
decisions from a method (extract a nested block, name a tangled boolean, flatten a tail `if` into a
guard clause), applies them one at a time, re-measures, and stops at the target. Every step is verified
against the compiler, and the run rolls back to the last state that compiled.

```
complexity_report  filePath=Services/OrderService.cs line=84
reduce_complexity  filePath=Services/OrderService.cs methodName=ProcessOrder target=10
```

`fix_complexity` is the same reducer without having to name a method: it ranks the whole solution
(or one project/file), lowers the worst methods up to `maxMethods`, and returns one aggregated
before/after report with the per-method diffs. Use it when the request is "make this codebase
simpler"; use `reduce_complexity` when you already know the exact method.

```
fix_complexity  projectName=Orders maxMethods=5 target=10     # preview
fix_complexity  projectName=Orders maxMethods=5 dryRun=false  # write
```

## The style engine

`simplify_code` applies the IDE's mechanical suggestions — the quick-fix popup equivalents — without an
IDE. It is a rewrite engine over Roslyn syntax trees, not a formatter.

### Coverage

- **87 of the 126 rule ids** in Microsoft's code-style table are rewritten.
- The other **39 answer with the reason**, per id, in the result's `notRewritten` list — never silently.
- Three of the 39 are answered by another tool: `IDE0005`, `IDE0032` and `IDE0290` are accepted by
  `simplify_code` and delegated to `optimize_usings`, `property_to_auto` and
  `convert_to_primary_constructor`, one report per id under `delegated`.
- Visual Basic is a separate layer with **15 rewritten ids** and 34 with a reason, exposed by
  `simplify_vb_code`.

Representative rules: collection expressions (`IDE0028`, `IDE0300`, `IDE0301`), null-coalescing and
null-propagation (`IDE0029`, `IDE0030`, `IDE0031`, `IDE0074`), pattern matching (`IDE0019`, `IDE0020`,
`IDE0038`, `IDE0083`), expression-bodied members (`IDE0021`–`IDE0027`, `IDE0053`, `IDE0061`), conditional
expressions (`IDE0045`, `IDE0046`), switch expressions (`IDE0066`), exhaustive switch completion
(`IDE0010`, `IDE0072`), LINQ chains (`IDE0120`, `IDE0121`), `HashCode.Combine` (`IDE0070`), dead code and
unused members (`IDE0035`, `IDE0051`, `IDE0052`, `IDE0059`), unused parameters across every call site
(`IDE0060`), `^` and ranges (`IDE0056`, `IDE0057`), `nameof` (`IDE0082`, `IDE0280`, `IDE0340`),
nullability-directive cleanup (`IDE0240`, `IDE0241`), and formatting (`IDE0055`, opt-in).

### How a run behaves

```
simplify_code scope=project projectName=MyApp rules=IDE0028,IDE0041
```

- **`scope`** is `document` (needs `filePath`), `project` (needs `projectName`) or `solution`.
- **`rules`** is a comma-separated list; omit it for every supported rule. `IDE0055` is not part of
  `rules=all` because formatting an entire project is a deliberate act.
- **`dryRun=true`** (default) returns the before/after pair per edit and a line diff. Nothing is written.
- On write, each changed document is **compiled**; a document whose change would add errors is reported
  under `refused` and left untouched.
- Rewritten nodes are **formatted by Roslyn's formatter**, so indentation follows the file, not the
  engine.
- The result includes `styleOptions` when a rule is driven by `.editorconfig`.

The `.editorconfig` keys currently read are `csharp_prefer_braces`,
`csharp_preferred_modifier_order`, `csharp_style_namespace_declarations`,
`dotnet_style_prefer_foreach_explicit_cast_in_source` and
`dotnet_style_require_accessibility_modifiers` for C#; and
`visual_basic_preferred_modifier_order`, `dotnet_style_require_accessibility_modifiers` and
`visual_basic_style_prefer_isnot_expression` for Visual Basic. Option-driven rules do nothing while the
option is unset.

### Visual Basic

`simplify_vb_code` has the same shape — scope, rules, `dryRun`, compiler gate, edit history — and 15
rule ids: simplify name/member access, remove `Me.`, unnecessary cast, predefined type, `Is Nothing`,
`If(a, b)`, `?.`, parentheses, compound assignment, boolean equality, conditional simplification,
`IsNot` (driven by `visual_basic_style_prefer_isnot_expression`) and `ByVal` removal. It is a separate
tool because the engine registers one style service per language; `simplify_code` rewrites C# only.

## Inspections and code fixes

Two tools, two directions:

```
inspect_code    packs=all minSeverity=warning ids=RCS*,CA18*
apply_code_fix  diagnosticId=RCS1060 filePath=Services/OrderService.cs line=42
```

`inspect_code` runs six built-in packs — the VisualMCP **house** rules (`VMC0001`: one type per file),
**Roslynator**, **.NET analyzers**, **AsyncFixer**, **Meziantou** and **StyleCop** — over every project.
No analyzer package needs to be referenced by your solution. Defaults are
`house,roslynator,netanalyzers,asyncfixer` at `minSeverity=info` with dormant rules
enabled (they arrive as `WEAK_WARNING`). Filter with `projectName`, `filePath`, `ids`, `excludeIds`,
`maxResults` and `timeoutSeconds`.

`apply_code_fix` re-runs the same packs and executes the analyzer's **own** `CodeFixProvider`.
`dryRun=true` is the default; `fixAllScope=document|project|solution` repairs every occurrence of that
id, and a change touching more than `maxChangedFiles` (default 5) files is refused. Rules that ship no
fix are refused with that message rather than a guessed edit — most CA diagnostics, and every compiler
diagnostic outside the eight mechanical `CS####` shapes `fix_diagnostics` rewrites (unused
local/field/event, duplicated using, hidden member, redundant `new`).

## What "safe" does not mean

- **The compiler gate proves compilation, not behaviour.** A rewrite can compile and still change
  meaning. `simplify_code` is a mechanical rewriter, not a semantic equivalence prover.
- **The style gate is per document.** One bad rewrite refuses the whole document's changes rather than
  isolating the single edit. Other documents in the same run still apply.
- **Each style rule edits one contiguous region in one document.** Rules that need multi-file edits are
  handled by dedicated refactorings (`change_signature`, `global_usings`, `IDE0060`), not by the generic
  rewriter.
- **Refactorings refuse instead of approximating.** A refusal names what blocked it; the correct next
  step is usually to prepare the code (remove the object initializer, move the member, split the
  overload), then retry.

## A safe working loop

1. `git_status` — know what is already modified.
2. Understand the target: `find_symbol`, `read_symbol`, `find_references`.
3. Preview: `preview_rename`, `simplify_code` (`dryRun=true`), `extract_method_candidates`.
4. Write one operation at a time.
5. `build_project` and `run_tests`.
6. `inspect_code` to catch what the compiler does not.
7. If something is wrong: `edit_history`, then `revert_edit(id)` — or `git_diff` and revert.

---

Next: [Razor, metacode and decompilation](razor-metacode.md)
