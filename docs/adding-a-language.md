# Adding a language to the style engine

The IDE-rule engine is split into two layers: a **neutral core** that knows nothing about syntax, and a
**language layer** that supplies only what differs. Adding Visual Basic means writing the second layer; the
first is touched only when a language preference has no neutral field to live in (that happened for
`visual_basic_style_prefer_isnot_expression`, see below).

## What is already shared (`Snitch/Languages/Style/`)

| Type | Role |
|---|---|
| `StyleRewrite` | the base class of a rule: `Rule`, `Rules`, `RequiredOption`, `IsOptIn`, `TryMatch(node, model)` / `TryMatch(node, context)`, plus the neutral helpers (`Match`, `Removal`, `SameSymbol`, `IsUserDefinedOperator`) |
| `StyleRule`, `StyleMatch` | a rule's id/title/description; the match with node, replacement, final span and the `Removes` flag |
| `StyleOptions` + the five enums | the preferences already mapped away from key names: braces, modifier order, namespaces, foreach cast, accessibility, `IsNot` |
| `StyleContext` | semantic model, options, workspace, solution, `IsPristine` |
| `IStyleRuleSet` | `All`, `Supported`, `NotRewritten`, `SupportedIds` |
| `IStyleLanguage` | the four points of contact with the syntax (below) |
| `StyleRewriteService` | **the whole pipeline**: document/project/solution scope, up to 3 passes per document, compiler gate with per-refusal isolation, report, edit history, file writing |

## What a language must provide

1. **Its own rule base class** (`CSharpStyleRewrite` as the model): the neutral class plus the helpers that
   need the language's syntax factory — parsing an expression or a statement from text, the speculative
   binding used by the qualification checks.
2. **`IStyleLanguage`**, four members plus the extension:
   ```csharp
   string Name { get; }                 // LanguageNames.VisualBasic
   string Extension { get; }            // ".vb"
   bool IsStatement(SyntaxNode node);   // tells a statement from an expression
   bool IsShapeWrapper(SyntaxNode replacement);      // a parsed block that contains statements
   IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent);
   SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements);
   StyleOptions ReadOptions(Document document, SyntaxTree? tree);
   ```
   `StatementsOf` + `WithStatements` are all the service needs to replace or remove a range of statements:
   the rest of the splice (trivia, annotations, ordering) is shared.
3. **`IStyleRuleSet`**: the language's rule catalogue and the `NotRewritten` table with the reason for
   every id that is not rewritten.
4. **The options parser**: maps the language's keys (`visual_basic_*`, `dotnet_style_*`) onto the fields of
   `StyleOptions`.
5. **The concrete service**: a three-line class that inherits `StyleRewriteService`, passing the rule set,
   the language and the refactoring name — like `SimplifyCodeService` for C#.

## What necessarily stays with the language

The rules themselves: every matcher works on its language's node types and `SyntaxKind`s, and the text it
emits is that language's syntax. There is nothing to abstract there, and it must not be forced.

## Current state

- **C#**: 87 ids, 39 not rewritten with a reason, `Snitch/Languages/CSharp/Style/`.
- **Visual Basic**: 15 ids, `Snitch/Languages/VB/Style/` (`VbStyleLanguage`, `VbStyleRuleSet`,
  `VbStyleRewrites.cs`, `VbSimplifyCodeService`). The tests are `src/VisualMCP.Tests/VbStyleTests.cs`, over
  an `AdhocWorkspace` with a real VB project: for every supported id, one case that is rewritten and one
  that must stay untouched. The service is registered by `AddSnitchVisualBasic()`
  (`Snitch/Languages/VB/VbServiceCollectionExtensions.cs`) and is not wired into `AddSnitch`: a host that
  wants VB asks for it, so the two services never compete for the same `ISimplifyCodeService`.
- A VB rule may declare the same IDE id as a C# one: the ids belong to Microsoft's table, not to the
  language, and the table has separate columns for C# and VB. That is why
  `VbStyleRewriteCatalog.NotRewritten` covers the **VB column** (the shared `dotnet_style_*` rules plus the
  `visual_basic_style_*` ones): an id VB does not define at all — pattern matching, braces, expression
  bodies — is not a VB rule and does not appear there, while everything VB defines and we do not rewrite
  has a reason, one per line.

### What required a generalization of the neutral core

- `StyleOptions` gained one field, `PreferIsNot`
  (`IsNotPreference.None | PreferIsNot | PreferNot`): `visual_basic_style_prefer_isnot_expression` is a VB
  option, but the place where an already-mapped preference lives is the neutral record — like `Braces` for
  C# — and the rule does not read the key name.
- `StyleRewriteService` explained the ids held back by an option with a sentence that listed
  IDE0011/IDE0036/IDE0160/IDE0161, the four C# ones. The note is now built from the ids selected in that
  run, and `preferIsNot` appears next to `braces`/`namespaces`/`modifierOrder`. For C# the list and the
  `braces` field (which a test asserts on) stay identical.
- Nothing else changed: `StyleRewrite`, the pipeline, the compiler gate, the statement splice and the edit
  history were already neutral, and VB uses them as they are.

## Traps already paid for that apply to VB too

- From the second pass on, the tree is no longer the solution's: `StyleContext.IsPristine` is true only on
  the first pass, and a rule that consults the solution must refuse the rest.
- `ReplaceNodes` rebuilds **the whole** tree: statement matches found before it must be recomputed
  afterwards (the service already does this).
- A replacement on the root (directives) must not be annotated with `Formatter.Annotation`, or the whole
  file gets reformatted.

## VB traps, paid for while writing `Languages/VB/`

- **There is no public `BlockSyntax` with `Statements`.** C# has a single type that owns a list of
  statements; VB has sixteen (`MethodBlockBaseSyntax`, `MultiLineIfBlockSyntax`, `CaseBlockSyntax`,
  `TryBlockSyntax`, `DoLoopBlockSyntax`, …) and no common base. `VbBlocks` is the only place that knows
  them: a type missing there is not a compilation error, it is a statement rule that silently does
  nothing. When a rule that splices statements is added, its test case must live in a block whose type is
  in the list.
- **The factory says `ParseExecutableStatement`, not `ParseStatement`.** `SyntaxFactory.ParseStatement`
  exists in C# and not in VB: the compiler flags it, but the "obvious" typo is exactly that one.
- **`TypeSyntax` derives from `ExpressionSyntax`.** A VB type name is also an expression, so
  `ParseTypeName(...)` fits the APIs that take an `ExpressionSyntax` (speculative binding included) and no
  second path is needed.
- **The null test is `Is Nothing`, and `= Nothing` is not equivalent everywhere.** For a `String` VB
  compares values (`"" = Nothing` is true); for a value type `= Nothing` is the default value. IDE0041
  fires only on a reference type other than `String`, or it would change the meaning.
- **Coalescing is `If(a, b)` and the ternary condition is `If(c, a, b)`**: two different node types
  (`BinaryConditionalExpressionSyntax` and `TernaryConditionalExpressionSyntax`), and IDE0029 must rewrite
  the ternary into the binary one, not the other way round.
- **IDE0084 has a direction, not a shape.** `Not (o Is other)` and `o IsNot other` are both correct: which
  one to write is decided by `visual_basic_style_prefer_isnot_expression`, so the rule reads
  `StyleContext.Options.PreferIsNot` and declares `RequiredOption`; without the option it touches nothing.
  The form without parentheses (`Not o Is C`) is the same thing: `Is` binds tighter than `Not`.
- **`Formatter.FormatAsync` works for VB only if the `Microsoft.CodeAnalysis.VisualBasic.Workspaces`
  assembly is in the MEF composition.** `MefHostServices.DefaultAssemblies` loads it by name, so it is the
  `PackageReference` in the `.csproj` that makes it available: without that package the VB document parses
  but formatting cannot find the service.
- **None of the C# "syntax" rules come along.** No braces, no expression bodies, no property patterns, no
  `??=`: the corresponding ids are not "unimplemented", they are ids VB does not have. The VB table is
  deliberately short (15 ids) and every id that is not rewritten has its reason.
