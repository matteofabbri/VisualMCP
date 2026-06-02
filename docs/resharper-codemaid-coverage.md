# ReSharper & CodeMaid — Feature Coverage

Comparison of VisualMCP tools against ReSharper and CodeMaid features.
Legend: ✅ implemented · ⚠️ partial · ❌ missing

---

## ReSharper

### Navigation & Search

| Feature | Tool | Status |
|---|---|---|
| Find symbol by name (semantic) | `find_symbol` | ✅ |
| Find all references | `find_references` | ✅ |
| Find implementations of interface | `find_implementations` | ✅ |
| Find derived types / subclasses | `find_derived_types` | ✅ |
| Call hierarchy (find callers) | `find_callers` | ✅ |
| Hover info / resolve symbol at position | `get_symbol_info` | ✅ |
| List type members with signatures | `get_type_members` | ✅ |
| Go to file / type by fuzzy name | — | ❌ |
| Navigate to recent files / edits | — | ❌ |

### Analysis & Inspections

| Feature | Tool | Status |
|---|---|---|
| Compiler errors & warnings | `get_diagnostics` | ✅ |
| Project dependency graph + cycle detection | `analyze_dependencies` | ✅ |
| Dead code / unused symbols | `find_unused_symbols` | ✅ |
| Cyclomatic complexity, LOC, nesting | `get_metrics` | ✅ |
| Code smells (async void, empty catch, long methods…) | `find_code_smells` | ✅ |
| Full inspections (null checks, LINQ, closures, boxing…) | — | ❌ |
| Structural Search & Replace (SSR) | — | ❌ |
| Dependency matrix | — | ❌ |
| Solution-wide background error analysis | — | ❌ (model not applicable) |

### Documentation

| Feature | Tool | Status |
|---|---|---|
| Read XML doc comment for a symbol | `get_xml_docs` | ✅ |
| Find public API missing XML docs | `find_undocumented_public_api` | ✅ |

### Refactoring

| Feature | Tool | Status |
|---|---|---|
| Preview rename | `preview_rename` | ✅ |
| **Apply rename** (write changes to disk) | `apply_rename` | ✅ (added) |
| Extract method candidates | `extract_method_candidates` | ✅ |
| Remove unused `using` directives | `optimize_usings` | ✅ (added) |
| Sort `using` directives | `optimize_usings` | ✅ (added) |
| Reorder members by convention | `reorder_members` | ✅ (added) |
| **Move type to matching file** | `move_type` | ✅ (added) |
| **Extract interface** | `extract_interface` | ✅ (added) |
| **Safe delete** | `safe_delete` | ✅ (added) |
| **Inline method / variable** | `inline_symbol` | ✅ (added) |
| **Encapsulate field** (generate property + update refs) | `encapsulate_field` | ✅ (added) |
| **Change method signature** | `change_signature` | ✅ (added) |
| **Introduce variable** | `introduce_variable` | ✅ (added) |
| **Pull members up** | `pull_members_up` | ✅ (added) |

### Code Generation

| Feature | Tool | Status |
|---|---|---|
| **Generate constructor / Equals / GetHashCode / ToString** | `generate_members` | ✅ (added) |
| **Implement INotifyPropertyChanged** | `implement_inpc` | ✅ (added) |

### Testing

| Feature | Tool | Status |
|---|---|---|
| Run tests, parse results | `run_tests` | ✅ |
| Per-class / per-method coverage | `get_test_coverage_map` | ✅ |

---

## CodeMaid

| Feature | Tool | Status |
|---|---|---|
| Remove unused `using` directives | `optimize_usings` | ✅ (added) |
| Sort `using` directives | `optimize_usings` | ✅ (added) |
| Reorder members (access modifier order) | `reorder_members` | ✅ (added) |
| Code outline / Spade view | — | ❌ |
| **Remove regions** | `remove_regions` | ✅ (added) |
| Format document | — | ❌ |
| Comment formatting / wrapping | — | ❌ |
| Join lines / remove excess blank lines | — | ❌ |
| Collapse to definitions | — | ❌ (IDE-only) |

---

## Remaining gaps

All planned ReSharper/CodeMaid features have been implemented. Lower-priority items that remain:

- **Full ReSharper inspections** — null checks, LINQ inefficiencies, boxing, closure captures (would require running Roslyn analyzers)
- **Structural Search & Replace** — pattern-based find/replace across the solution
- **Dependency matrix** — visual coupling metrics between projects/namespaces
- **Push members down** — inverse of `pull_members_up`
- **Code outline / Spade** — hierarchical code tree (IDE-only concept)
