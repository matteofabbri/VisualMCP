[Docs home](../README.md) › **Requirements and guarantees**

# Requirements and guarantees

What the product needs, and what a result proves. The boundaries — where a read stops and what a scanner
cannot prove — are written where they can be checked against the inventory rather than repeated here: the
per-language map is [gaps.md §1](../gaps.md#1-language-and-platform-scope), the properties of the analysis
are [gaps.md §11](../gaps.md#11-boundaries-that-are-not-gaps), and the ✅ half is
[resharper-codemaid-coverage.md](../resharper-codemaid-coverage.md).

## Requirements

The **.NET 10 SDK**. The server targets `net10.0` and loads a solution through `MSBuildWorkspace`, and a
solution must evaluate and restore: a missing SDK, a broken `Directory.Build.props` or an unreachable
package feed leaves the semantic model at MSBuild diagnostics, with no silent fallback to text search.

Nothing else is required. The routing guard and the installer's configuration edit run from the published
binary (`VisualMCP --hook pre-tool-use`, `VisualMCP --config …`), so no interpreter is installed or looked
for; the Windows guard is PowerShell, which ships with the operating system. Visual Studio is not
required either: the optional integrations light up only when their tool is present — `tsc`/`eslint` from
`node_modules`, `clang`, `kubectl`, `helm`, `dotnet ef` — or when a tool installs one on demand (Ghidra
and JADX download themselves, `debug_install` fetches netcoredbg, `profile_tools` the diagnostics
toolchain).

## What a result proves

A rewrite is gated on the compiler: a change that would add an error is refused, and the edit is rolled
back. That gate proves *compilation*, never behaviour — the style rules that claim equivalence are checked
operation by operation, and `verify_change` is what measures behaviour, by running the suite on the working
tree and on a revision and diffing the outcome test by test. Code the suite does not cover comes back
`unverified`, not safe.

Everything else is static analysis or process instrumentation, and the answer says what it is: a scanner
reports patterns with a confidence, a sampled stack is statistical, a decompiled body is a reconstruction,
and a lexical symbol table resolves what a token can follow. Every tool names the boundary it ran into in
its own answer — a `not-checked` binding, a `RAW` construct with a diagnostic, an `inconclusive` verdict —
so a green run is read as "no pattern matched", never as a proof.

## Scope

VisualMCP is not a test framework: it runs your tests and compares two runs, it does not invent the
coverage a suite lacks. It is not a proof of correctness or security: `find_security_issues` and the Razor
rules find patterns, not proofs. It is not a remote service: there is no hosted component, and running the
stdio server on another machine would break its paths. It does not guarantee API compatibility after an
upgrade: `update_solution` proves the build, and the tests decide the behaviour.

## Licensing

The source is available under the **PolyForm Noncommercial License 1.0.0**. Noncommercial use is free;
commercial or company use requires a separate paid license. See [LICENSE](../../LICENSE) and
[CONTRIBUTING.md](../../CONTRIBUTING.md).

---

Next: [Help and FAQ](../help.md)
