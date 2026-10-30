# Languages — one layer per language

The engine is language-neutral by construction. Navigation, refactoring and style are written
once against Roslyn syntax (or against a small markup scanner), and a language layer supplies the
matchers and parsers for one language. `capability_report` reports the live layers and the rule
counts; this page is the map of the folders.

| Layer | Folder | Surface |
|---|---|---|
| C# | `CSharp/` | the main surface: navigation, analysis, 50+ refactorings, style, inspections, testing, packages, modernization |
| Visual Basic | `VB/` | style only — `simplify_vb_code`, 15 Microsoft code-style ids; everything else has no VB frontend |
| Razor / Blazor | `Razor/` | page and component inventory, 11 security rules (`RZ1001`–`RZ1011`, CWE-tagged) and 17 markup rules (`RZ2001`–`RZ2017`), verified edits |
| XAML | `Xaml/` | WPF/WinUI/MAUI/Avalonia/UWP inventory, `XAML0001`–`XAML0009`, name/resource/style navigation |
| Style core | `Style/` | `StyleRewrite`, `IStyleRuleSet`, `IStyleLanguage`, `StyleRewriteService`, `.editorconfig` options — the language-neutral half of `simplify_code` |
| Gate | `Gate/` | compilation cache and semantic equivalence, used by every code-editing layer |

## C#

`CSharp/` is the bulk of the engine and is split by concern:

| Folder | What it owns |
|---|---|
| `Navigation/` | find symbol/references/callers, implementations, derived types, type outlines, symbol at a line |
| `Analysis/` | diagnostics, solution audit, complexity (cyclomatic, cognitive, Big-O), dead code, duplicates, security, DI, namespace coupling, async anti-patterns, test quality, nullability |
| `Refactoring/` | the semantic refactorings, each gated on the compiler |
| `Style/` | the `simplify_code` rules for C# |
| `Inspections/` | the analyzer packs and their code fixes behind `inspect_code` / `apply_code_fix` |
| `Testing/` | run/rerun tests, coverage, test impact, generated test scaffolds |
| `Packages/` | NuGet facts, upgrades, version conflicts, vulnerability fixes |
| `Modernization/` | Aspire, target-framework and package upgrades |
| `ContextActions/` | the ReSharper-style context actions (import type, argument guards, wrap in try/using, string re-encoding) |
| `Generation/` | DTOs, builders, members, XML docs |
| `Search/` | structural search (`$holes$`) and declaration-resolving `search_code` |
| `Parsing/` | `CsprojParser` — package names Roslyn's project model does not expose |
| `Documentation/` | XML docs and the undocumented-public-API scan |
| `Benchmarking/` | BenchmarkDotNet runs and snippet benchmarks |
| `Reflection/` | metadata-only assembly inspection |
| `SolutionManagement/` | adding and removing projects in a solution |

A rule is only real when it is both catalogued and applicable: `docs/engine-state.md` lists the
IDE rules that are rewritten and why the others are not, `CAPABILITY_MAP.md` has the
rule-by-rule table, and `docs/gaps.md` is the honest list of what is missing.

## Adding a language

Read [`docs/adding-a-language.md`](../../docs/adding-a-language.md). The shape is always the same:
implement the matchers for the neutral engine (or a scanner of your own), register the service in
`AddSnitch()` / `AddSnitchMetacode()`, expose it through a tool, and add a rule-case test per
rewrite. A language that only has a reader can join the metacode model instead of the style
engine — see [Snitch.Metacode](../Libraries/README.md).
