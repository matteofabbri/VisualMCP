# Snitch.Metacode — one pseudocode for trees in different languages

`SNITCH/Libraries/Snitch.Metacode` turns a syntax tree into **a single model** and prints it as canonical
pseudocode. The product pipeline reads **C#** and **Visual Basic** (Roslyn) and **Razor**
(`.cshtml`/`.razor`), plus the library's own two formats: the canonical text and the Ghidra snapshot. A type
decompiled with ILSpy and a native binary come in through the same door.

The model is neutral by construction, and two **opt-in** readers prove it: Python (JSON dump of the `ast`
module) and C++ (`clang -ast-dump=json`). They are not in `MetacodeTranslator.Default` because that
translator reads a source, not a dump: the `translate_python_file` / `translate_cpp_file` tools produce the
dump and build a translator with the reader that understands it. The same algorithm written in C#, Python
and C++ still comes out with the same body, line for line — that is the test
`The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`, run with those readers.

The library does not use the semantic model: it reads syntax. A C# file costs a parse, not a compilation,
and no loaded solution is needed. The one exception is the [symbol index](#the-symbol-index): "who uses this
member, and how" cannot be answered by reading syntax, and the index is a capability of its own that lives
here next to the translator.

## The architecture in one line

```
tree (Roslyn | pseudocode text | Ghidra snapshot)
    -> frontend          (one per language, produces only model nodes)
    -> IMetacodePass     (normalization: language differences removed here)
    -> MetacodeDocument  (the single model, with origin and diagnostics)
    -> PseudocodeRenderer (canonical text)  <->  PseudocodeFrontend (reads it back)
```

The opt-in readers (`PythonFrontend`, `CppFrontend`) enter at exactly the same point: they are
`ILanguageFrontend` like the others, simply outside the default list.

A binary enters through the same door: ILSpy produces C# and Ghidra produces C, but the model that comes out
is the usual one (see [Decompilation](#decompilation-from-a-binary-to-metacode)).

Every layer talks only to the next one. Adding a language means adding an `ILanguageFrontend` to
`MetacodeTranslator.Frontends`: passes, renderer and parser are not touched.

## The model

The vocabulary is deliberately smaller than any language (a construct that does not fit is *desugared* or
declared opaque, not invented):

| Group | Types |
|---|---|
| Containers | `MetacodeDocument`, `MetacodeModule`, `MetacodeTypeDeclaration`, `MetacodeFunction` |
| Members | `MetacodeField`, `MetacodeProperty`/`MetacodeAccessor`, `MetacodeEnumMember`, `MetacodeEvent`, `MetacodeTypeAlias`, `MetacodeParameter` |
| Statements | `MetacodeBlock`, `MetacodeLocalVariable`, `MetacodeAssignment`, `MetacodeIf`, `MetacodeWhile`, `MetacodeDoWhile`, `MetacodeFor`, `MetacodeForEach`, `MetacodeSwitch`, `MetacodeTry`, `MetacodeUsing`, `MetacodeLock`, `MetacodeYield`, `MetacodeImport`, `MetacodeReturn`, `MetacodeThrow`, `MetacodeBreak`/`Continue`/`Goto`/`Label`, `MetacodeLocalFunction`, `MetacodeComment` |
| Expressions | `MetacodeLiteral`, `MetacodeIdentifier`, `MetacodeMemberAccess`, `MetacodeInvocation`, `MetacodeBinary`, `MetacodeUnary`, `MetacodeConditional`, `MetacodeIndex`, `MetacodeObjectCreation`, `MetacodeCast`, `MetacodeTypeCheck`, `MetacodeCollection`, `MetacodeDictionary`, `MetacodeComprehension`, `MetacodeLambda`, `MetacodeAwait`, `MetacodeRange`, `MetacodeSwitchExpression`, `MetacodeInterpolatedString` |
| Honesty | `MetacodeOpaqueStatement`, `MetacodeOpaqueExpression`, `MetacodeOpaqueDeclaration`, `MetacodeDiagnostic` |

Every node carries `Origin` (language, file, line, column): the pseudocode stays traceable down to the
original line.

**An unsupported construct never disappears.** It becomes a `RAW[cs] … // reason` node and a `MC0002`–`MC0004`
diagnostic: an incomplete translation is visible, instead of looking complete.

### The constructs that are no longer opaque

The vocabulary grew where a real language asked for it, and every addition brings its test
(`MetacodeDepthTests`):

| Construct | Node | Where from |
|---|---|---|
| `lock (gate) { … }` | `MetacodeLock` | C#. It is not a `USING`: it protects against the absence of other threads, not a resource |
| `yield return x` / `yield break` | `MetacodeYield` (value `null` = break) | C# |
| `event` | `MetacodeEvent` | C#; signature only, the accessors are the language's convention |
| `foreach (var (k, v) in xs)` | `MetacodeForEach` with `VariableName = "(k, v)"` | C# |
| `await foreach` | `MetacodeForEach.IsAwait` | C#; without the flag it was a silent loss |
| `import os` / `from typing import List` | `MetacodeImport` (+ `MetacodeImportName`) | Python |
| `@decorator` | `MetacodeFunction.Decorators` / `MetacodeTypeDeclaration.Decorators` | Python; the decorator text, in source order |
| `[x for x in xs if p]`, `{…}`, `{k: v …}`, `(… )` | `MetacodeComprehension` (+ `MetacodeComprehensionClause`) | Python |
| template parameters | `MetacodeTypeParameter` on function and type | C++ (`FunctionTemplateDecl`, `ClassTemplateDecl`) |
| `for (int x : xs)` | `MetacodeForEach` | C++ (`CXXForRangeStmt`, read by role, not by position) |
| lambda | `MetacodeLambda` | C++ (`LambdaExpr`, signature from the closure's `operator()`) |
| `using Count = int;` / `typedef` | `MetacodeTypeAlias` | C++ (the compiler's implicit aliases are skipped) |

A template's parameter types are the same `MetacodeTypeParameter` the pseudocode parser already read back
in `<T>`: no new node was needed, it needed filling in.

## Example: the same body in three languages

```csharp
int Sum(int n)
{
    var total = 0;
    for (var i = 0; i < n; i++)
        if (i % 2 == 0) total += i;
    return total;
}
```
```python
def sum(n):
    total = 0
    for i in range(n):
        if i % 2 == 0:
            total += i
    return total
```
```cpp
int sum(int n) {
    int total = 0;
    for (int i = 0; i < n; i++)
        if (i % 2 == 0) total += i;
    return total;
}
```

All three become:

```
METHOD Sum(n : int) : int        (C#)
FUNCTION sum(n)                  (Python)
FUNCTION sum(n : int) : int      (C++)

  DECLARE total = 0
  DECLARE i = 0
  WHILE i < n DO
    IF i % 2 == 0 THEN
      total += i
    END IF
    i += 1
  END WHILE
  RETURN total
```

The `for` loop counts and differs from `while` only because `NormalizeControlFlowPass` rewrites it — and it
does not rewrite it when the body contains a `continue`, because the increment would be skipped. Python's
`for i in range(n)` becomes the **same** `MetacodeFor` node as the C# `for`, so the result is identical with
no special cases in the renderer.

## Using it

```csharp
using Snitch.Metacode;

// From a Roslyn tree already in memory (no compilation).
MetacodeDocument document = MetacodeTranslator.Default.Translate(
    MetacodeSource.FromSyntaxTree(tree));

// From C# source.
string pseudocode = MetacodeTranslator.Default.Render(
    MetacodeSource.FromCSharp(source, "Sample.cs"));

// From a CPython JSON dump: the reader is not in the default pipeline, so it is named.
var python = new MetacodeTranslator(new ILanguageFrontend[] { new PythonFrontend() })
    .Render(MetacodeSource.FromPythonAst(json, "sample.py"));

// From clang -ast-dump=json, same story.
var cpp = new MetacodeTranslator(new ILanguageFrontend[] { new CppFrontend() })
    .Render(MetacodeSource.FromCppAst(json, "sample.cpp"));

// From a Razor page or a Blazor component.
var razor = MetacodeTranslator.Default.Render(
    MetacodeSource.FromRazor(File.ReadAllText("Index.cshtml"), "Index.cshtml"));

// From pseudocode already written (or produced by another tool).
var parsed = MetacodeSource.FromPseudocode(text);
```

Options (all with a sensible default):

```csharp
var options = MetacodeOptions.Default with
{
    NormalizeControlFlow = true,   // for -> while
    FlattenNestedBlocks = true,    // block inside block -> block
    IncludeComments = true,        // comments seen by the frontend
    Passes = new IMetacodePass[] { new MyPass() },
};
```

## The opt-in readers: Python and C++

These two readers are not in `MetacodeTranslator.Default`, which remains the pipeline of the sources the
translator reads directly: `metacode_translate` does not offer the languages (`language` is `cs`, `razor`,
`ghidra` or `pseudo`) and `MetacodeSource.FromFile` does not infer them from the extension — a `.json` does
not say whether it holds a CPython `ast`, a clang dump or something else. The dump is produced by the two
dedicated tools, which then build the translator with the right reader: `translate_python_file` runs the
dumper below, `translate_cpp_file` runs `clang -ast-dump=json`; both also accept a ready-made dump
(`dumpJson`). The Python dumper, if you want to run it by hand — one file, no dependencies:

```python
import ast, json, sys

def dump(node):
    if isinstance(node, ast.AST):
        out = {"_type": type(node).__name__}
        for field in node._fields:
            out[field] = dump(getattr(node, field, None))
        if hasattr(node, "lineno"):
            out["lineno"], out["col_offset"] = node.lineno, node.col_offset
            out["end_lineno"], out["end_col_offset"] = node.end_lineno, node.end_col_offset
        return out
    if isinstance(node, list):
        return [dump(item) for item in node]
    return node

source = open(sys.argv[1], encoding="utf-8").read()
print(json.dumps(dump(ast.parse(source, filename=sys.argv[1])), indent=2))
```

C++:

```bat
clang++ -Xclang -ast-dump=json -fsyntax-only sample.cpp > sample.json
```

`MetacodeSource.FromFile` chooses the reader by extension (`.cs`, `.cshtml`/`.razor`, `.mc`/`.pseudo`) and
for a `.json` recognises only a Ghidra snapshot (`imageBase` + `functions`). A Python or clang dump is passed
through `FromPythonAst`/`FromCppAst` to a `MetacodeTranslator` built with `PythonFrontend`/`CppFrontend`.

## The canonical text

Keywords are uppercase and one statement sits on one line:

```
MODULE name
NAMESPACE name
CLASS | STRUCT | INTERFACE | ENUM | RECORD | UNION | TRAIT [<parameters>] [EXTENDS …]
FUNCTION | METHOD | CONSTRUCTOR | DESTRUCTOR | OPERATOR | LOCAL FUNCTION [<parameters>]
PROPERTY, GET/SET (AUTO or block), FIELD, CONST, EVENT, ALIAS
@decorator                  (one line per decorator, above the declaration)
DECLARE x : type = value, CONST x : type = value
IF … THEN / ELSE IF … THEN / ELSE / END IF
WHILE … DO / END WHILE       REPEAT / UNTIL …
FOR init; condition; increment DO / END FOR
FOREACH x [: type] IN expression DO / END FOREACH
AWAIT FOREACH x [: type] IN expression DO / END FOREACH
SWITCH / CASE a, b / DEFAULT / END SWITCH
TRY / CATCH [(Type name)] [WHEN condition] / ELSE / FINALLY / END TRY
USING x : type = expr DO / END USING
LOCK expression DO / END LOCK
YIELD RETURN expression, YIELD BREAK
IMPORT a, b AS c            FROM module IMPORT a, b AS c
RETURN expr, THROW expr, BREAK, CONTINUE, GOTO label, LABEL name
RAW[cs] text // reason
// comment
```

Comprehensions sit inside the parentheses of the container they build, with the clauses in source order —
that is the order that gives meaning to a nested comprehension:

```
[x * 2 FOR x IN xs IF x > 0]
{k : v FOR k, v IN pairs}
{n FOR n IN xs}
(n FOR n IN xs)
```

Operators, from weakest: `? :`, `??`, `OR`, `AND`, `|`, `^`, `&`, `== != IN NOT IN`, `< <= > >= IS IS NOT AS`,
`<< >>`, `+ -`, `* / % DIV`, `**`, prefixes `NOT - + ~ AWAIT NEW CAST`, postfixes `. ?. () []`.

`PseudocodeFrontend` reads this text back; the test `The_rendered_pseudocode_reads_back_to_the_same_text`
verifies that the round trip does not change a single character, and `MetacodeDepthTests` verifies it for
every added construct — including the block-bodied lambda, which the renderer prints over several lines and
the parser reassembles: when a line leaves a parenthesis or a brace open, the following lines belong to the
same statement, and the body between braces is read back by the statement reader (passed to the expression
parser) instead of by the expression grammar, where it would have no place. It is the door for any language
that has no frontend: just produce this text.

## Adding a language

1. An `ILanguageFrontend` with `CanHandle` and `Translate` (see `CSharpFrontend` as the reference: it is the
   shortest because Roslyn does the parsing).
2. Map every construct onto a model node; what has no node becomes `MetacodeOpaque*` + `MetacodeDiagnostic`,
   with the original text.
3. Add it to `MetacodeTranslator.Default` **only if the translator can read the source directly**: that is
   the rule that puts C#, Visual Basic, Java and Razor there and Python/C++ not. A reader that depends on a
   dump produced elsewhere goes through the constructor (opt-in), as `PythonFrontend` and `CppFrontend` do; if
   the product knows how to produce that dump (like `translate_python_file` / `translate_cpp_file`), the tool
   builds the translator with the right reader and it stays opt-in. Java is the other case: the source is
   Java text and JADX produces it, so `JavaFrontend` reads it directly and belongs in the default pipeline.
4. Add a fixture to the test `The_same_algorithm_prints_the_same_body_from_csharp_python_and_cpp`: it is the
   operational definition of "same architecture".

## Known limits (declared, not silent)

- **C++** (opt-in reader): `goto` stays opaque, as do `using`/`using namespace`, `friend`, `static_assert`,
  partial template specializations, `delete`, `sizeof` and the expressions the model does not have
  (`StmtExpr`, `noexcept`, …): all `RAW[cpp]` with a diagnostic. A lambda's captures are not modelled: the
  body is, the captured state is not. The preprocessor does not exist in the dump, and an `#include` leaves
  no trace other than the declarations it brings.
- **Python** (opt-in reader): `match`, `assert`, `global`/`nonlocal`, `del`, `yield` and the unpacked
  arguments (`*xs`, `**kw`) stay opaque; decorators are read as text, not as calls. `from . import x` keeps
  the dots in the module name.
- **C#**: `fixed`, `unsafe`, `checked`, `delegate`s, user-defined conversions and `goto case`/`goto default`
  are opaque; complex `switch` patterns (`case int n when n > 0`) stay as text inside `CASE`.
- **Visual Basic**: `With … End With`, `ReDim`, `Erase`, `On Error` (GoTo/Resume Next), `RaiseEvent`,
  `AddHandler`/`RemoveHandler`, `Exit Try` and the `Like` operator are `RAW[vb]` with a diagnostic; `MyClass`
  lowers to `this` with a `LossyLowering` (it suppresses virtual dispatch). The direction of a `For` with a
  non-constant `Step` is assumed positive, and the diagnostic says so.
- **Razor**: `@switch`, `@try`/`catch`/`finally` and `@do`/`while` stay **a single** `RAW[razor]` with a
  diagnostic — their `case`/`catch`/`while` are not `@` blocks, so the scanner does not see them as
  branches; an `@if`/`@foreach` body without braces is markup, not a block. The markup between two
  constructs is `RAW[razor]` (one segment per line, with the newlines as `\n`) and the directives are
  `MetacodeOpaqueDeclaration`: a page is not an algorithm, and pretending it is would be worse than saying
  so.
- **Pseudocode parser**: a block-bodied lambda is read back, and the lines following an open parenthesis or
  brace belong to the same statement. Two things stay out: a `RAW[…]` line with unbalanced parentheses is
  not reassembled (a `RAW` line is always a single line), and a comment with unbalanced parentheses inside a
  lambda body is not seen as a comment during reassembly.
- **Comments**: C# captures them from trivia; the Python and clang dumps do not contain them.
- **.NET decompilation**: ILSpy's C# contains constructs the C# frontend does not model — attributes
  (dropped: they are not in the model) and `x with { … }` (which stays `RAW[cs]` with a diagnostic).
  Compiler-generated types are excluded from the selection.
- **Ghidra**: C's comma operator stays `RAW[cpp]`, multi-level pointers collapse to one, calling conventions
  and the decompiler's comments are lost, and the canonical text does not carry the origin of the single
  statement (the function does).
- **Java**: an anonymous class body, a labeled `break`/`continue`, a local class, `assert`, a switch
  expression and a method reference are `RAW[java]` with a diagnostic; annotations are decorators (their
  arguments stay text); a record's components become fields plus the canonical constructor; a JADX comment is
  dropped.

## Decompilation: from a binary to metacode

Decompilation entered the library as **another way of producing a `MetacodeDocument`**, not as a separate
capability: the C# ILSpy reconstructs is read by the `CSharpFrontend` that already existed, so a decompiled
type has the same shape as a type read from source — namespace, types, members, bodies, `Origin` on every
node. `Snitch` cannot reference `Snitch.Metacode` (it is the other way round), so the implementation lives
here; the public namespace `Snitch.Languages.CSharp.Decompilation` is unchanged, so existing callers compile
without changes.

```
assembly .dll ──ILSpy──▶ C# ──CSharpFrontend──▶ MetacodeDocument ──renderer──▶ pseudocode
java archive ──JADX──▶ .java sources ──JavaFrontend──▶ MetacodeDocument ──renderer──▶ pseudocode
native binary ──Ghidra headless──▶ decompiled C ──normalization──▶ canonical text ──PseudocodeFrontend──▶ MetacodeDocument
```

### Two contracts, one implementation

`DecompileService` implements both halves, and `AddSnitchMetacode()` registers one next to the other:

| Contract | What it returns |
|---|---|
| `IDecompileService` | the same C# text as before, with the usual three calls (`DecompileType`, `DecompileAssembly`, `DecompileTypes`) |
| `IMetacodeDecompileService` | `MetacodeDecompilation`: decompiled types with their pseudocode, the merged `MetacodeDocument`, the diagnostics, the hidden types and the errors |

```csharp
using Snitch.Languages.CSharp.Decompilation;
using Microsoft.Extensions.DependencyInjection;

services.AddSnitchMetacode();   // after AddSnitch()

var result = service.DecompileTypeToMetacode(assemblyPath, "Newtonsoft.Json.JsonConvert");
result.Pseudocode;              // the type in canonical pseudocode
result.Document;                // the same tree, with the origin of every node
result.Types[0].Members;        // how many members the model saw
```

Every type is decompiled **on its own** and translated on its own: a type the model cannot read costs that
type and not the assembly. Compiler-generated types (`<>c`, `<PrivateImplementationDetails>`) do not enter
the selection; a type ILSpy cannot print ends up in `Hidden`, one that fails in `Errors` — never an
exception to the caller.

**The origin of a decompiled node** points at the line of the C# ILSpy produced, labelled
`<assembly>:<type>.cs`: it is the trace that actually exists, because the original source does not. If the
`.cs` file on disk is also wanted, `IDecompileService.DecompileTypes` still writes it.

## Ghidra: a native binary, the same model

Ghidra headless decompiles an executable to C; that C is **normalized into the canonical text** and read
back by `PseudocodeFrontend`. It is the "normalize first" choice, and it is not a fallback:

- `CppFrontend` reads the JSON of `clang -ast-dump`, not C text, and Ghidra's C is not a translation unit a
  compiler would accept (`undefined8`, `FUN_140001008`, `LAB_…` are Ghidra names);
- normalizing instead reuses the documented reader for "any language that has no frontend"
  (`PseudocodeFrontend`), so **one format and one reader** remain;
- the normalizer is a real C reader, not a text substitution: a cast stays a `CAST`, a dereference stays a
  dereference, `p->f` becomes `p.f` — not a `p->f` that accidentally became something else.

### The service

```csharp
using Snitch.Metacode.Decompilation;

IGhidraService ghidra = provider.GetRequiredService<IGhidraService>();
var analysis = await ghidra.AnalyzeAsync(@"C:\Windows\System32\version.dll");

analysis.Functions;   // how many functions it recovered
analysis.Error;       // the exact reason if it did not start (never an exception)
analysis.LogPath;     // the full log of the process
analysis.ToMetacode();// the functions as a MetacodeDocument
```

- **Where Ghidra lives**: `GhidraOptions.HeadlessPath`, then `GhidraHome`, then `GHIDRA_INSTALL_DIR`/
  `GHIDRA_HOME`, finally the installer folder (`%LOCALAPPDATA%\VisualMCP\tools\ghidra_*`, or
  `GhidraOptions.ToolsDirectory`). The service does not assume it is on the `PATH`.
- **Temporary project**: one per analysis, under `GhidraOptions.OutputDirectory` (default
  `%TEMP%\vmcp-ghidra\<sha256 of the binary>`). Ghidra deletes it at the end of the run (`-deleteProject`)
  and the service removes the leftover folder; with `KeepProject = true` the option is **not** passed and the
  project stays, together with the `ExportNativeGraph.java` script the service writes there: it does not
  depend on an external file.
- **Timeout**: `TimeoutSeconds` is a cap for the whole run; when it expires the process **and its tree** are
  killed and the result says it timed out, with the log.
- **JDK**: `JAVA_HOME_OVERRIDE` is set for the child process when `GhidraOptions.JavaHome` is known, or — as
  the previous native pipeline did — after a first attempt that failed with an error naming Java: in that
  case the service looks for a JDK (`JAVA_HOME` and the standard folders) and retries once. On this machine
  `JAVA_HOME` points at a JDK 17 and Ghidra 12.2 starts anyway: the override path exists for the machines
  where it does not.

### Installation: Ghidra when it is missing

`GhidraOptions.AutoInstall` (default `true`) means `AnalyzeAsync` does not merely look for Ghidra: if it does
not find it, `GhidraInstaller` downloads the latest release from the project's GitHub feed
(`ghidra_<version>_PUBLIC_<date>.zip`, the only archive Ghidra publishes — it is platform-independent) and
extracts it under the user's tools folder. `HostSystem` is the only answer to "which platform is this": it
decides the launcher name (`analyzeHeadless.bat` on Windows, `analyzeHeadless` elsewhere) and, on
Linux/macOS, restores the execute bit that a zip extracted on Windows does not preserve
(`RestoreUnixModes`). `GhidraHome`/`GHIDRA_INSTALL_DIR` always win and download nothing; `AutoInstall = false`
is the choice of whoever manages Ghidra themselves.

`GhidraInstallation` returns `HeadlessPath`, `Home`, `Version`, `Source` (`explicit`, `ghidraHome`,
`environment`, `tools`) and the error with the manual instruction when the download is not possible. A
failure is not an exception: `analyze_native_binary` and `metacoding` report it with the log.

### Where the result goes

A binary that is not a managed assembly has no C# to decompile: `ManagedAssemblyDetector` reads the PE header
(COM descriptor directory) and `metacoding action=decompile` routes it to Ghidra instead of failing inside
ILSpy. `analyze_native_binary` does the same for the read-only path.

The result is **persisted twice**, and the second is the one that matters for the AST: the recovered
functions become **a metacode document** under `native://<file>` — the functions as declarations, the full
node-by-node AST, the complexity, all in the same tables (`mc_document`/`mc_tree`/`mc_node`) as the
solution's code, with `source_path` = the binary and `decompiled` = 1 — and they also remain in the dedicated
native tables:

| Table | What it holds |
|---|---|
| `native_artifact` | one analysed binary: `sha256` (its identity), file, path, `image`, processor language, entry point, image base, function count, size, Ghidra version, launcher, date |
| `native_function` | one row per recovered function: name, entry address, prototype, size, Ghidra's **decompiled C**, the model's **canonical pseudocode**, cyclomatic/cognitive/max nesting/statement/branch |
| `native_call` | one edge per call seen by Ghidra: `caller` and `callee` by entry address |

The key is the file's SHA-256: the same binary (even copied elsewhere) is not decompiled twice, and
`force=true` replaces the rows instead of adding a second copy. The model is translated once: every function
gets its entry in `native_function`, with the same pseudocode and the same complexity a tree of the solution
would have. The write is transactional and degrades without failing the analysis when the database is not
available.

### The binary's folder: reading and decompiling its neighbours

A binary does not live alone: next to it are its documentation, its `.config` files, and the other binaries
of the same program. After a decompilation (`metacoding action=decompile`, `siblings` on by default) the
**same folder** is walked and what is inside enters the same AST:

- **text** files are read as in a build (timestamp, then hash): a `.md`, a `.json` or a `.cs` becomes a
  document, with the frontend if the language has one and a `File` node if not;
- the other **managed assemblies** are decompiled with ILSpy (one by one, with the same MVID key: those
  already in the store are skipped);
- **native** binaries are handed to Ghidra, but at most two per sweep: one Ghidra run costs tens of seconds
  and a folder of executables must not turn a call into a wait. The others are listed as skipped, with the
  instruction to decompress them on their own.

The cap is `maxSiblings` (default 25 files examined): beyond it, the sweep stops and says so. Files larger
than 64 MB are skipped, already-decompiled binaries (SHA-256 in `native_artifact`) are not re-analysed, and
every file goes through the same freshness check as a build — so a second call on the same folder rewrites
nothing. The result reports a `sweep` with the counts (texts read, assemblies decompiled, native binaries,
skipped, failed) and the reasons for the skipped ones.

### Normalization, construct by construct

| Ghidra's C | Pseudocode | Note |
|---|---|---|
| `void FUN_x(longlong a, byte * b)` | `FUNCTION FUN_x(a : longlong, b : byte*) : void` | the calling convention (`__fastcall`) is not in the model and is dropped |
| `undefined8 x;` | `DECLARE x : undefined8` | `type name [32]` keeps the size in the type name |
| `*p = 0;` | `(*p) = 0` | a write through a pointer is an assignment, not a declaration |
| `(uint)*p << 0x18` | `CAST(uint, *p) << 0x18` | the cast stays a cast, the hex stays a number |
| `p->f` | `p.f` | |
| `&DAT_…`, `*puVar7` | `&DAT_…`, `*puVar7` | two new unary operators of the model, `AddressOf` and `Dereference` |
| `L"text"`, `L'\0'` | `"text"`, `'\0'` | the wide prefix is the encoding, not the value |
| `if/else`, `while`, `do…while`, `for`, `switch` with `case`/`default`, `goto`/label | `IF/ELSE`, `WHILE`, `REPEAT…UNTIL`, `FOR`, `SWITCH`, `GOTO`/`LABEL` | the `for` then becomes `WHILE` through the normalization pass, as in every other language |
| `/* WARNING: … */` | — | Ghidra's comments talk about the decompiler, not the program |

**What does not fit** is declared, not guessed: a C comma expression (`while (p = f(p), *p)`) has no node —
it is a sequence, and the model has no sequence expressions — and it becomes `RAW[cpp] …` with an `MC0008`
diagnostic that says why. A type with more than one pointer level (`char **`) collapses to the level
`MetacodeTypeRef.IsPointer` can represent.

**The origin of a native function** is what is needed to find it again: the document is the binary, the line
is the position in the snapshot, the `Snippet` is Ghidra's signature. The canonical text, by contrast, does
not carry origins (the pseudocode reader does not follow them), which is why the function — not the single
statement — is the traceable unit of a native tree.

### How it is tested

Ghidra is not invoked in the tests: it is slow and machine-dependent. `DecompilationMetacodeTests` registers a
**real snapshot** (two functions of `C:\Windows\System32\version.dll`, captured with a headless run on
29 October 2026) and verifies on it the format parser, the produced metacode, the origin, the full
renderer↔parser round trip and the `RAW` case of the comma operator. `NativeCodeStoreTests` (7 cases) uses
the same snapshot for the tables: the `native_artifact`/`native_function`/`native_call` rows, the SHA-256
key, reuse and forced replacement, lookup by name or address, routing of a non-managed file through
`metacoding`, the installer recognising an existing copy and the asset choice from the release feed. The
"Ghidra really starts" verification is done by hand, once, and its outcome goes into the report of whoever
runs it.

## Java: a class file, the same model

A `.class`, a `.dex`, or a zip of classes (`.jar`, `.war`, `.aar`, `.apk`, `.aab`) is **Java bytecode**, and
JADX is the decompiler that reads it. Unlike Ghidra's C, what JADX produces is real Java source, so the
frontend reads it directly instead of going through the canonical text:

```
Hello.jar ──JADX──▶ sources/com/example/Hello.java ──JavaFrontend──▶ MetacodeDocument ──renderer──▶ pseudocode
```

`JavaFrontend` is a recursive-descent reader over the Java grammar (`JavaLexer` + `JavaParser` in the same
file): packages and imports, classes/interfaces/enums/records and annotation types, fields, constructors and
methods, generics with wildcards, annotations as decorators, `if`/`while`/`do`/`for` (counted and enhanced),
`switch` with fall-through, `try`/multi-catch/finally/try-with-resources, `synchronized`, lambdas, casts,
array creation and initializers, `instanceof` patterns. `<` and `>` are lexed as single characters — `>>` in
`Map<K, List<V>>` closes two generic lists and not a shift — and the parser merges the run when it is an
operator.

### The service and the installer

```csharp
using Snitch.Metacode.Decompilation;

IJadxService jadx = provider.GetRequiredService<IJadxService>();
var analysis = await jadx.DecompileAsync(@"app.jar");

analysis.Files;       // how many .java files JADX wrote
analysis.Sources;     // each one: where it is, its package path, its type name
analysis.Error;       // the exact reason if it did not run (never an exception)
analysis.LogPath;     // the full log of the process
```

`JadxInstaller` mirrors the Ghidra one. The asset it wants is the cross-platform CLI bundle
`jadx-<version>.zip` (the GUI bundles are filtered out by name): it unpacks `bin/jadx` and `bin/jadx.bat`
next to `lib/`, so `HostSystem` decides which launcher this process executes, `RestoreUnixModes` restores
the execute bit a zip extracted on Windows does not preserve, and `JADX_HOME` / `JADX_CLI` /
`JadxOptions.JadxHome` always win and download nothing. JADX is itself a Java program: with no `JAVA_HOME`
and no JDK under the usual install roots the run fails, and the error says so (`JavaRuntimeLocator` is the
same JDK search Ghidra uses, and a run that complains about Java is retried once with it).

### Where the result goes

`metacoding action=decompile` asks `JavaArchiveDetector` before anything else — `CAFEBABE`, `dex\n`, or a
zip that actually carries `.class` entries — so a class file is not mistaken for a Mach-O fat binary and a
jar is not dismissed as "not an executable image". The routing is:

| The file is | Goes to | Stored as |
|---|---|---|
| .NET metadata | ILSpy | `assembly://<file>/<type>` |
| Java bytecode | JADX | `java://<archive>/<package/Type.java>` |
| machine code | Ghidra | `native://<file>` (plus the native tables) |

Every decompiled `.java` file becomes one document in the same `mc_document`/`mc_tree`/`mc_node` tables as
the solution's own code, with `source_path` = the archive and `decompiled` = 1. The Java text itself is kept
in `source_document`, because unlike an ILSpy type it is a file a reader can look at. The identity is the
archive's SHA-256: the same jar is not decompiled twice unless `force=true`, a second run reports "already
stored", and a class that disappeared from the jar is removed with the rest of its archive.

### What stays opaque

The model's pressure valve, as in every other frontend: an anonymous class body (`new Runnable() { … }` — the
creation is kept as `RAW[java]` with a diagnostic and the chain around it still lowers), a labeled
`break`/`continue`, a local class, `assert`, a switch expression and a method reference. One such statement
costs that statement: the parser recovers at the next `;` or block and the rest of the method is a tree.

### How it is tested

`JavaFrontendTests` reads a file shaped like JADX's output — package, fields, constructor, a method per
control-flow construct, a multi-catch, an enhanced `for`, an anonymous class — and asserts the model (not the
text): the class, the members, no `ParseError` anywhere, the anonymous class reported and kept, and recovery
continuing after a statement the reader cannot place. `JadxTests` covers the release asset choice, the
installer resolving an existing copy and a tools-folder install, the bytecode detector (`class`, `dex`, jar,
plain zip), and a full `metacoding` run with JADX replaced by a recorded result: the rows under
`java://fake.jar/…`, the `java` kind in `metacoding`'s own listing, and the second call answering "already
stored".

## The model grew a little more

| Construct | Node | Where from |
|---|---|---|
| `*p`, `&x` | `MetacodeUnary` with `Dereference` / `AddressOf` | C, C++ and whatever Ghidra decompiles; printed as in C and read back by the parser |
| `x with { A = y }` | `MetacodeWithExpression`, printed `WITH { … }` | record/value update; the emitter rewrites `with` |
| `new { A = x, y }` | `MetacodeAnonymousObject`, printed `ANON { … }` | object without a declared type; the shorthand keeps the expression name |
| `x is not ("a" or "b")`, `x is { }`, `case > 0:` | `MetacodePatternMatch` / `MetacodePatternText`, printed `MATCHES (…)` and `PATTERN (…)` | the pattern stays text (the same choice as `MetacodeSwitchArm`), the operand is a real node |
| `[.. xs, 1]` | `MetacodeSpread`, printed `SPREAD xs` | spread of a collection expression |

In this pass the canonical-text reader closed four holes that the decompiled C revealed: hex literals
(`0x100` no longer splits into `0x` and `100`), character literals (`'\0'`), a `FOR` header containing `<`
(which no longer opens an angle bracket, so `FOR i = 0; i < n; i += 1 DO` reads back), and a `RAW[…]` line
read back as a **warning** instead of an error — `RAW` is valid canonical text, not a parser failure.

With the five nodes above, the model translates **all of the library's own code**: the 48 files of
`SNITCH/Libraries/Snitch.Metacode` go from 560 diagnostics to zero. `MetacodeLibraryCoverageTests` is the
guardian: it translates every file and fails if a new construct is left without a node, so the model cannot
regress. `MetacodeExpressionTests` covers the five constructs one by one (canonical text, renderer↔parser
round trip, re-readable C# emission), and the "with, anonymous objects, patterns and spreads" case is in the
theory of `MetacodeGenerationTests` that verifies the full emitter→C#→model round trip.

## Razor: a page is a class that renders

A `.cshtml`/`.razor` is not a tree of declarations: it is markup with C# inside. `RazorFrontend` models it
for what it is — a class with a page to draw:

- `@model` becomes the `Model` property, every `@inject` a property with the same name (or the type name);
  the other directives (`@page`, `@using`, `@layout`, `@attribute`, the tag helpers) are
  `MetacodeOpaqueDeclaration` with their text, because they configure the page, they do not declare it.
- the members of `@code`/`@functions` go through `CSharpFrontend`: the class already contains them, so the
  frontend reads them for real (methods, properties, fields) with the **origin on the page's line**.
- the markup body is the body of a `Render` method: `@if`/`else if`/`else`, `@foreach`, `@for`, `@while`,
  `@lock` and `@using (…)` are the model's control nodes (the header is C#, so the C# frontend lowers it
  onto an empty shape, and the body is the markup), and the HTML between one construct and another stays a
  `RAW[razor]`.

```razor
@model ProductListViewModel
<h1>Products</h1>
@if (Model.Any) { <ul>@foreach (var p in Model.Products) { <li>@p.Name</li> }</ul> }
```
```
MODULE Index
  PROPERTY Model : ProductListViewModel
    GET AUTO
  END PROPERTY
  METHOD Render
    RAW[razor] <h1>Products</h1> // HTML markup
    IF Model.Any THEN
      RAW[razor] <ul> // HTML markup
      FOREACH p IN Model.Products DO
        RAW[razor] <li>@p.Name</li> // HTML markup
      END FOREACH
      RAW[razor] </ul> // HTML markup
    END IF
  END METHOD
END MODULE
```

`else` is the reason the frontend re-reads the text instead of trusting the list of blocks: in Razor
`else`/`else if` have no `@`, so the scanner does not report them, and the chain has to be rebuilt from the
braces. For the same reason `@switch`, `@try` and `@do` stay opaque: their `case`/`catch`/`while` are markup,
not branches the model could represent without inventing.

## Visual Basic: a module is a class

`VisualBasicFrontend` reads a `.vb` with Roslyn's syntax layer, like C#: one parse, no compilation. A
`Module` becomes a `CLASS` with the `STATIC` modifier; `Structure`, `Interface` and `Enum` have their own
kinds; `Inherits` and `Implements` merge into the `EXTENDS` list. A property with explicit accessors becomes a
`PROPERTY` with the two accessors, an automatic one two empty accessors (`AUTO`).

The three conditional forms stay distinct and become what they say: `If(c, a, b)` a `MetacodeConditional`,
`If(a, b)` a `??`, `If … Then` an `IF`. `For i = 0 To n` lowers to `MetacodeFor` with the comparison in the
direction of the `Step` when it is a constant, `Select Case` becomes `SWITCH` with `To` and `Is >` as textual
patterns, `Do … Loop Until` a `REPEAT … UNTIL`, and `d!Name` an index on a string. A `Dim x = 1` carries no
type, like C#'s `var`: the model prints them the same way.

What the model does not have stays `RAW[vb]` with a diagnostic: `With … End With` (an implicit receiver
cannot be lowered without duplicating its evaluation), `ReDim`, `Erase`, `On Error`, `RaiseEvent`,
`AddHandler`/`RemoveHandler`, `Exit Try` and `Like`. The full list is in the known limits.

## The symbol index

`Symbols/` answers, from the database instead of from the solution, the questions that in Visual Studio are
*Go To Definition*, *Find All References* and *Call Hierarchy*, and in ILSpy are the *Analyzer*: **where a
member is declared, with which signature, where it is used, how it is used, who uses it and what it uses**.
The cost of answering the first time is paid once: afterwards they are indexed SQLite queries, and a file
that has not changed is not reopened.

```
C# document ──Roslyn (syntax + semantic model)──► SymbolIndexBuilder
                                                   │  declarations (signature, span, container)
                                                   │  uses (how, inside which member)
                                                   │  relations the syntax does not write (override, implement)
                                                   │  metacode trees, one per declaration
                                                   ▼
                        .visualMCP/visualmcp.db  (si_symbol, si_usage, si_metacode, si_document)
                                                       ▼
                SymbolIndexService ──► reference_index(action=find|describe|refs|callers|metacode|…)
```

### What a declaration records

The row carries the **signature as the source writes it** (`public static int Add(int a, int b)`), the symbol
kind, accessibility, modifiers (`static`, `abstract`, `virtual`, `override`, `async`, `const`, `readonly`,
`required`, `extension`…), the namespace, the **container** (type or namespace), the XML `summary` when the
project analyses it, and the full span — line, column, start, end — so a caller can read the body instead of
searching for it.

A symbol's **key** is `Kind:` + its fully qualified signature (`Method:global::Ns.C.Add(int)`). Two
declarations share a key only if they are the same symbol: a partial class has one key and several rows, two
overloads have different keys. Constructed generics and reduced extension methods collapse onto the
definition, so `List<int>` and `List<string>` are one entry and `x.Ext()` falls back on the `Ext(this X, …)`
declaration.

> `SymbolDisplayFormat.FullyQualifiedFormat` is **not enough** in this version of Roslyn: it omits the
> member's options, so every method formats with the bare name and two overloads would end up on the same
> key. The format used is built by hand in `SymbolKeys` and includes container, parameters and `ref`/`out`.

### How it is used: the vocabulary

The "how" is not given by Roslyn: it has to be read from the syntax around the name, from the nearest node
upwards, and the symbol decides only where needed (an interface is *implemented*, a base class is *extended*).
The values are stable — they are saved in the database and returned by the tools — and a value is added, never
renamed:

| Group | Values |
|---|---|
| Members | `call`, `method-group`, `instantiate`, `read`, `write`, `read-write`, `initializer-write`, `subscribe`, `unsubscribe`, `ref-argument`, `out-argument`, `in-argument`, `lock` |
| Types | `inherit`, `implement`, `type-argument`, `parameter-type`, `return-type`, `field-type`, `property-type`, `local-type`, `event-type`, `attribute`, `constraint`, `catch`, `cast`, `as`, `pattern`, `typeof`, `default`, `sizeof`, `array`, `nameof`, `deconstruction`, `using`, `type` |
| Relations | `override`, `implement` — the only two rows the source does not write (below) |

`x.y.Z` is **a single slot**: the name and every member access above it answer the same question ("is it a
call, a write, a read?"), so `x.M()` is `call` while `x.M` alone is `method-group`, `x.P = 1` is `write` and
`x.P += 1` is `read-write`.

### The relations the syntax does not state

`override` does not name the base member, and an implicit interface implementation is just a matching
signature. Both are reconstructed (`FindImplementationForInterfaceMember`, `OverriddenMethod`) and recorded
**as a use of the base member, at the position of the member that overrides or implements it**: the row's
container is the derived member. From there:

- `implementedBy` of `IFoo.M` are the members that implement it (the override in a derived class included,
  because the override chain is walked);
- `implementedBy` of `IFoo` are the types that implement it — recorded once on the type, so it does not depend
  on which file declares the member.

### The same tree, in metacode

The index does not merely read the source: it also **lowers it into metacode**. While the document is in hand
(`MetacodeSource.FromSyntaxTree`), the translator produces the `MetacodeDocument`, and every declaration of
the model is paired with the symbol the index knows — the node's origin (line and column, 1-based) is the
same position as the Roslyn declaration, with the name under the same container as a fallback for the nodes
a pass has moved. So `action=metacode` returns the **canonical pseudocode of what you found**, read from the
database:

```
reference_index(action=metacode, symbol="Counter")        → CLASS Counter … END CLASS   (with its members)
reference_index(action=metacode, symbol="Counter.Add")    → PUBLIC METHOD Add(delta : int) … END METHOD
reference_index(action=metacode, path="…/Counter.cs")     → all the forests of the file, in order
```

A type carries its members inside, and every member also has **its own** row: asking for a method does not
mean regenerating the type. The text is the renderer's — so the passes' normalization is included, and the
`for` is already a `WHILE` — and it is the same text that `PseudocodeFrontend` reads back. It is gzipped in
the database (a type repeats its members) and rewritten with the document when the hash changes.

For this, `PseudocodeRenderer` has two public overloads — `Render(MetacodeDeclaration)` and
`Render(IReadOnlyList<MetacodeDeclaration>)` — that print one declaration or a list with the whole document's
rules.

### Using it

```csharp
using Snitch.Metacode.Symbols;
using Snitch.Storage;
using Snitch.Workspace;

var index = new SymbolIndexService(workspace, store);   // or ISymbolIndexService from the container

await index.BuildAsync();                    // incremental: content hash per document
index.Find("Twice", partial: true);          // declarations, with how many places use them
index.Describe("Counter");                   // signature, documentation, span, uses by kind, callers
index.References("_value", kind: "write");   // every write, with the member that contains it
index.Callers("Twice");                      // who uses it, grouped by caller
index.Callees("Use");                        // what that member uses (declared=false ⇒ outside the solution)
index.Hierarchy("IShape");                   // bases, interfaces, derived, implementers, overrides
index.Outline(symbol: "Counter");            // the type's members, in source order
index.Outline(path: "…/Counter.cs");         // the file's declarations
index.Metacode(symbol: "Counter.Add");       // the metacode tree of that declaration
index.Metacode(path: "…/Counter.cs");        // the file's trees, in source order
index.Unused();                              // non-public declarations nobody uses
```

A document has one frontend per language (`ISymbolIndexFrontend`: which nodes declare, which names use a
symbol, how a use is read, which signature is printed); the builder, the store and the service are neutral.
C# is the only implementation, so `build` counts documents of other languages as `unsupported` instead of
skipping them silently.

Declared limits: locals, parameters and type parameters are not indexed (the question is "who uses this
*member*"); a method group with several overloads and no invocation does not bind to a symbol and is skipped;
the index is a **snapshot**, it must be rebuilt after changes (the queries do not read the files, except the
line of code shown as `snippet`).

## The solution's metacode, persisted

`reference_index build` produces the trees as a side effect of the symbol index. But the solution's metacode is
an artifact of its own, with its own lifecycle:

- **when the solution is first opened** it is generated for **every file of the solution** (the background
  task in `Program.cs`, after `OpenEnvironmentAsync`): the Roslyn documents of all projects, in whatever
  language the workspace can load, plus **every text file under the solution folder** read from disk — the
  `.cshtml`/`.razor` pages are content items and the workspace does not keep them as documents, and a
  `README`, a `.csproj` or a `.json` are not code but are part of the solution. The selection for documents is
  by language, not by `.cs` extension: the frontend decides; for files it is the content (a NUL in the first
  8 KB says "binary", and over 1 MB is not code).
- **when a file is saved** it is updated incrementally: one round every 2 seconds reads the timestamp first,
  and only if it changed the content, so it translates **only that document**. It also applies to a save made
  in another editor, because the text is read from the file on disk, not from the workspace snapshot;
- **when the solution is built** `build_project` does an explicit refresh after the build (always best-effort:
  an index error does not turn a successful build into a failed tool).

The result lives in `.visualMCP/visualmcp.db`, and every source is there **twice**: on one side as its
language writes it, on the other as the model reads it.

| Table | Key | What it holds |
|---|---|---|
| `source_document` | `path` | the file **in its native language**: text, language tag (`cs`, `vb`, `razor`, `md`, `csproj`…), content hash, timestamp, characters and lines. It exists for **every** file of the solution |
| `mc_document` | `path` | content hash, timestamp, project, language, declaration count, AST nodes, diagnostics — and the provenance: `source_path` and the `decompiled` flag |
| `mc_tree` | `declaration_key` + `path` + line + name | the declaration's canonical pseudocode (gzip) and the structural facts: cyclomatic, cognitive, max nesting, statements, branches, expressions — with `source_path` and `decompiled` |
| `mc_node` | `path` + `declaration_key` + `node_id` | **the complete AST** of every declaration: one row per node, with `parent_id`, `slot` (the parent property that holds it), `ordinal`, `depth`, line/column, the node's payload in JSON — and `source_path`/`decompiled` |

**Provenance is a column, not a convention.** `source_path` is the file the content comes from — for a
document read from disk it is the file itself, for decompiled code it is the **binary** — and `decompiled`
says which of the two it is (0 = read, 1 = decompiled). The three AST tables carry both columns on **every**
row, not only on the document: a query on `mc_node` always knows where a node comes from and whether it is
decompiled code, without having to interpret the `assembly://` prefix or walk back to the document. Rows
written before the columns existed receive them on the first `Ensure`: the schema adds in place the columns
the model declares and the file does not have (`ALTER TABLE … ADD COLUMN`, with zero as the default), without
recreating or losing anything.

`mc_node` is the tree, `mc_tree.text` is its rendering: the canonical pseudocode is for reading, comparing and
regenerating, `mc_node` holds **every** node of the model — the same vocabulary as `MetacodeNode` (`If`,
`Block`, `Invocation`, `Binary`, …) — with the parent relationships, so a structural question ("where are the
`Try`s deeper than 4", "which nodes have no origin") is a SQL query and not a parse. The rows are in
pre-order: `node_id` always grows after its parent, and `ORDER BY node_id` reconstructs the tree with no other
input. The walk is driven by each record's primary constructor, not by a `switch` on the types: a new model
node enters the AST the day it exists, with no list to update. `metacode_index action=ast` returns it nested,
with `parent_id`/`slot`/`ordinal` on every node and the payload already interpreted; a type nests its members,
exactly like its text.

The native side is the text, not a serialized tree: a Roslyn `SyntaxTree` is not serializable, and reading it
back from text costs a parse. What the database keeps is therefore the exact source (plus its hash), from
which the Roslyn model is reconstructed identically — and that is what lets the workspace use all of Roslyn's
features on the document, while the metacode remains the neutral view of the same file.

A file no frontend can read — a `README.md`, a `.csproj`, a `.json` — **does not disappear**: `source_document`
keeps the text, and the AST has **a `File` node** (kind `file`, payload with `path`, `name`, `language`,
`lines`, `chars`) that says what it is. So the AST contains everything the solution contains, instead of
containing only what is code; `metacode_index action=status` counts them in `files` and the build report in
`unsupported`. A language no frontend reads (for example F#, Java or Rust) follows the same path. Visual Basic
was on this list: since the VB frontend, the next pass translates the `.vb` files already read (the hash is
the right one) without touching `source_document`.

The "native" semantic side — declarations, uses, the override and implementation relation — is the symbol index
in the `si_*` tables, written by the same solution open and then on request of `reference_index action=build`
(it costs a compilation, the metacode does not).

The key is synthetic but stable — namespace, containing types, name and arity
(`method:Demo.Counter.Sum(1)`, `indexer:Demo.Store.this[1]`, ``type:Demo.Store`1``) — so a lookup does not
require the semantic model. A type carries its members inside and every member also has its own row, as in the
symbol index. The facts are **columns**, not prose: a report is a SQL query.

### Cyclomatic complexity from the model

`MetacodeComplexityAnalyzer` (`Analysis/`) computes McCabe and Sonar's cognitive complexity over
`MetacodeStatement`/`MetacodeExpression`, with the same rules as the repository's C# analyzer: one point for
`if`, loop, `catch`, ternary, `&&`/`||`, `??`, `switch` branch, `goto` and recursive self-call; `else if`
costs one point with no nesting penalty, a plain `else` zero paths and one cognitive point. Lambdas and local
functions are units of their own, as the compiler sees them.

The test `The_metacode_complexity_matches_the_CSharp_analyzer` compares the two numbers, method by method, on
six sources covering the decision constructs — with the semantic model passed to the C# analyzer, because
recursion is only visible there. It is the proof that the metric on the model is not a second, approximate
implementation: it is the same metric, applicable to any language that enters the model.

`complexity_report` remains the full C# view (Halstead, maintainability index, per-line contributions);
`metacode_index action=complexity` is the persisted, neutral view, read from the database.

### Generating code: metacode → C#

`CSharpEmitter` (`Generation/`) is the inverse of `CSharpFrontend`: same tree walk, same precedence table,
different target syntax. The acceptance criterion is a **fixed point**: translating the source, emitting C#,
reading the emitted C# back and translating it again must produce the same canonical pseudocode — that is what
the ten cases of `The_emitter_round_trips_a_file` do.

What the model cannot express in C# is lowered to the closest form that compiles, with a comment in the code
and a diagnostic in the output: `**` becomes `Math.Pow`, `in` becomes `Enumerable.Contains`, a set/dictionary
literal becomes `HashSet<object>`/`Dictionary<object, object>`, a comprehension with a single `for` becomes a
LINQ chain, a module of free functions becomes a `static partial class`. A comprehension with several `for`
clauses is not lowered: emitting code that does not compile would be worse than declaring it was not done. A
C# declaration the model does not have (a `delegate`, a user-defined conversion) is rewritten **verbatim**,
because it is valid C# by construction; a construct from another language becomes `// TODO(metacode): …`.

### Using it

```csharp
using Snitch.Metacode.Indexing;

var index = new MetacodeIndexService(workspace, store);   // or IMetacodeIndexService from the container

await index.BuildAsync();                       // incremental: timestamp, then content hash
await index.RefreshAsync();                     // the cheap round of the background loop
index.Status();                                 // documents, declarations, functions, types, complexity range
index.Trees(symbol: "Counter.Sum");             // the stored tree, with its numbers
index.Trees(path: "…/Counter.cs");              // all the declarations of the file
index.Complexity(minCyclomatic: 10);            // the ones worth reducing, worst first
index.Generate(symbol: "Counter.Sum");          // stored metacode → C#
index.Generate(text: pseudocode, language: "pseudo");  // new code from pseudocode → C#
```

The two corresponding MCP tools are `metacode_index` (`status|build|complexity|trees`) and
`metacode_generate` (metacode → C#, with the diagnostics of both directions).

Declared limits: C# only (Razor and VB have no emitter, and `.cshtml`/`.razor` documents are not Roslyn
documents); local functions and lambdas have no row of their own (their complexity is measured as part of the
method that contains them); a type has no complexity of its own — its members do.

## Where the files are

| File | Role |
|---|---|
| `MetacodeModel.cs`, `MetacodeStatements.cs`, `MetacodeExpressions.cs` | the model |
| `MetacodeDiagnostics.cs` | diagnostics, `MC0001`–`MC0008` codes, options, `IMetacodePass` |
| `MetacodeRewriter.cs` | the base of the passes (replacement with block splice) |
| `MetacodeSource.cs`, `ILanguageFrontend` | input and the frontend contract |
| `MetacodeTranslator.cs` | the façade: right frontend, passes, document |
| `Frontends/CSharpFrontend.cs`, `CSharpLowering.Expressions.cs` | Roslyn |
| `Frontends/VisualBasicFrontend.cs`, `VisualBasicLowering.Expressions.cs` | Roslyn VB: modules, types, members, statements and expressions of a `.vb` |
| `Frontends/PythonFrontend.cs` | `ast` dump — opt-in reader, not in the default pipeline |
| `Frontends/CppFrontend.cs` | `clang` dump — opt-in reader, not in the default pipeline |
| `Frontends/RazorFrontend.cs` | `.cshtml` pages and `.razor` components, on the scanner model of `SNITCH/Languages/Razor` |
| `Frontends/PseudocodeFrontend.cs`, `PseudocodeExpression.cs` | reading the canonical text |
| `Passes/MetacodePasses.cs` | `NormalizeControlFlowPass`, `FlattenSingleStatementBlocksPass` |
| `Rendering/PseudocodeRenderer.cs` | printing |
| `Decompilation/DecompileService.cs`, `IDecompileService.cs`, `MetacodeDecompilation.cs` | ILSpy → C# → model (public namespace unchanged) |
| `Decompilation/GhidraService.cs` | Ghidra headless: process, timeout, temporary project, `JAVA_HOME_OVERRIDE`, export script |
| `Decompilation/GhidraInstaller.cs` | finding and installing Ghidra: release feed, extraction, per-platform launcher (`HostSystem`), execute bit on Unix |
| `Decompilation/NativeCodeStore.cs` | the native tables of the solution database: `native_artifact`, `native_function`, `native_call` |
| `Decompilation/GhidraC.cs` | the reader of Ghidra's C that emits canonical text |
| `Decompilation/GhidraFrontend.cs`, `GhidraSnapshot.cs` | snapshot → functions → model, and the format parser |
| `SnitchMetacodeServiceCollectionExtensions.cs` | `AddSnitchMetacode()`: translator, decompiler (two contracts), Ghidra, symbol index, metacode index |
| `Analysis/MetacodeComplexityAnalyzer.cs` | cyclomatic (McCabe) and cognitive (Sonar) over `MetacodeStatement`/`MetacodeExpression` |
| `Generation/CSharpEmitter.cs` | metacode → C#: the inverse of the C# frontend, with a diagnostic for every approximation |
| `Indexing/MetacodeIndexModel.cs`, `MetacodeIndexStore.cs`, `MetacodeIndexService.cs` | the solution's metacode: `mc_document`/`mc_tree` tables, incremental build/refresh, complexity, generation |
| `Symbols/SymbolIndexModel.cs` | symbol keys, declaration and usage rows, metacode trees, the `SymbolUsageKind` vocabulary |
| `Symbols/CSharpSymbolIndexFrontend.cs` | the C# layer of the index: nodes that declare, names that use, use classification, signature |
| `Symbols/SymbolIndexBuilder.cs` | the per-document walk: declarations, uses, `override`/`implement` relations, and the metacode tree of every declaration |
| `Symbols/SymbolIndexStore.cs` | `si_symbol`, `si_usage`, `si_metacode`, `si_document` tables and the indexed queries |
| `Symbols/SymbolIndexService.cs` | the questions: `build`, `find`, `describe`, `refs`, `callers`, `callees`, `hierarchy`, `outline`, `metacode`, `unused` |
| `src/VisualMCP.Tests/MetacodeTests.cs` | 11 tests: exact shape, equivalence between languages, round trip, diagnostics |
| `src/VisualMCP.Tests/MetacodeDepthTests.cs` | 15 tests: the added constructs, one by one, plus the round trip of every document |
| `src/VisualMCP.Tests/DecompilationMetacodeTests.cs` | 14 tests: ILSpy→metacode, Ghidra from a registered snapshot, pointers, DI wiring |
| `src/VisualMCP.Tests/NativeCodeStoreTests.cs` | 7 tests: native tables, SHA-256 key, reuse/replacement, routing of a non-managed binary, installer and asset choice |
| `src/VisualMCP.Tests/SymbolIndexTests.cs` | 11 tests: signature and position, the use kinds, callers/callees, hierarchy, outline, incremental build |
| `src/VisualMCP.Tests/MetacodeExpressionTests.cs` | 6 tests: `with`, anonymous object, pattern match, case pattern, spread, C# emission of the five nodes |
| `src/VisualMCP.Tests/MetacodeLibraryCoverageTests.cs` | 1 test: every file of `Snitch.Metacode` translates without diagnostics (the model's guardian) |
| `src/VisualMCP.Tests/MetacodeGenerationTests.cs` | 24 tests: fixed-point round trip on twelve sources, model complexity against the C# analyzer, declared approximations |
| `src/VisualMCP.Tests/MetacodeIndexTests.cs` | 6 tests: incremental build/refresh, per-declaration rows, complexity from the store, generation from the store and from pseudocode |
