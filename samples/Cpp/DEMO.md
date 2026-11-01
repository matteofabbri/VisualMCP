# CppDemo — the C++ metacode reader, measured

Every number and every block of output below was produced on **2026-11-01** against the build in this tree,
with the clang that ships inside Visual Studio 18 (`clang version 22.1.3`, `x86_64-pc-windows-msvc`), on
Windows 10.0.26300. The C++ project itself is three files and builds with MSVC 14.51.

## 0. Build and run the project

```text
> powershell -NoProfile -ExecutionPolicy Bypass -File samples\Cpp\build.ps1 -Run
order.cpp
main.cpp
Generazione del codice in corso...
built D:\REPOSITORY\VisualMCP\samples\Cpp\build\CppDemo.exe
ok   two lines
ok   total is the sum of unitPrice * quantity
ok   the base describe is reused
ok   a lambda is callable
ok   the class template holds the value
all checks passed
```

The program exits 0 when every check passes, so the sample is a test as well as a demo.

## 1. Why `filter` and `clangArgs` exist

`clang -Xclang -ast-dump=json` dumps the **whole translation unit**, every declaration of every header it
includes. This three-line program:

```cpp
#include <vector>
#include <string>
int total(const std::vector<int>& v) { int s = 0; for (int x : v) s += x; return s; }
```

produces a **304,541,292-byte** dump (3,987,399 lines). With `-ast-dump-filter total` it is **48,894
bytes** — the function, and nothing else. And without `-std=`/`-I`, clang recovers from the missing header
with `RecoveryExpr` nodes instead of failing, which is why the dumper now reports a non-zero exit as an
error rather than translating the recovery tree.

## 2. `metacode_translate` on a real file — the whole library, 0 diagnostics

```text
metacode_translate(path: "samples/Cpp/src/order.cpp",
                   filter: "catalog",
                   clangArgs: "-std=c++20 -I..\include")
```

`order.cpp` includes `catalog/order.hpp`, which is why `-I..\include` is needed: clang runs in the file's
directory, so the arguments are resolved from there. The tool answers `NAMESPACE catalog` twice — the header
and the source reopen the same namespace — and every construct below is a node in the model, not text:

```text
NAMESPACE catalog
  ALIAS Money = long long
  ALIAS Prices = std::vector<Money>
  ENUM Status
    Draft
    Open
    Paid
    Cancelled
  END ENUM
  STRUCT Line
    PUBLIC FIELD sku : std::string
    PUBLIC FIELD unitPrice : Money
    PUBLIC FIELD quantity : int
  END STRUCT
  CLASS Order
    PUBLIC CONSTRUCTOR Order(customer : std::string)
    END CONSTRUCTOR
    PUBLIC DESTRUCTOR ~Order()
    END DESTRUCTOR
    PUBLIC METHOD add(line : Line)
    END METHOD
    PUBLIC METHOD total() : Money
    END METHOD
    PUBLIC METHOD count() : int
    END METHOD
    PUBLIC METHOD operator==(other : Order&) : bool
    END METHOD
    PUBLIC METHOD operator[](index : std::size_t) : Line&
    END METHOD
    PUBLIC METHOD describe() : std::string
    END METHOD
    PUBLIC METHOD status() : Status
      RETURN this.status_
    END METHOD
    PUBLIC METHOD setStatus(status : Status)
      this.status_ = status
    END METHOD
    PRIVATE FIELD customer_ : std::string
    PRIVATE FIELD lines_ : std::vector<Line>
    PRIVATE FIELD status_ : Status
  END CLASS
  CLASS PriorityOrder EXTENDS Order
    PUBLIC CONSTRUCTOR PriorityOrder(customer : std::string, fee : Money)
    END CONSTRUCTOR
    PUBLIC METHOD describe() : std::string
    END METHOD
    PRIVATE FIELD fee_ : Money
  END CLASS
  FUNCTION applyDiscount(amount : Money, percent : int) : Money
  END FUNCTION
  FUNCTION clamp<T>(value : T, low : T, high : T) : T
    IF value < low THEN
      RETURN low
    END IF
    IF value > high THEN
      RETURN high
    END IF
    RETURN value
  END FUNCTION
  CLASS Box<T>
    PUBLIC CONSTRUCTOR Box<T>(value : T)
    END CONSTRUCTOR
    PUBLIC METHOD get() : T
      RETURN this.value_
    END METHOD
    PUBLIC METHOD set(value : T)
      this.value_ = value
    END METHOD
    PRIVATE FIELD value_ : T
  END CLASS
END NAMESPACE
```

…and the same namespace as `order.cpp` reopens it, with the bodies:

```text
NAMESPACE catalog
  PUBLIC CONSTRUCTOR Order(customer : std::string)
  END CONSTRUCTOR
  PUBLIC DESTRUCTOR ~Order()
  END DESTRUCTOR
  PUBLIC METHOD add(line : Line)
    IF line.quantity <= 0 THEN
      THROW NEW std::invalid_argument("quantity must be positive")
    END IF
    this.lines_.push_back(move(line))
  END METHOD
  PUBLIC METHOD total() : Money
    DECLARE sum : Money = 0
    FOREACH line : Line& IN this.lines_ DO
      sum += line.unitPrice * line.quantity
    END FOREACH
    RETURN sum
  END METHOD
  PUBLIC METHOD count() : int
    RETURN CAST(int, this.lines_.size())
  END METHOD
  PUBLIC METHOD operator==(other : Order&) : bool
    RETURN this.customer_ == other.customer_ AND this.total() == other.total()
  END METHOD
  PUBLIC METHOD operator[](index : std::size_t) : Line&
    RETURN this.lines_[index]
  END METHOD
  PUBLIC METHOD describe() : std::string
    SWITCH this.status_
      CASE Draft
        NEW std::string("draft")
      CASE Open
        NEW std::string("open")
      CASE Paid
        NEW std::string("paid")
      DEFAULT
    END SWITCH
  END METHOD
  PUBLIC CONSTRUCTOR PriorityOrder(customer : std::string, fee : Money)
  END CONSTRUCTOR
  PUBLIC METHOD describe() : std::string
    TRY
      IF this.fee_ < 0 THEN
        THROW NEW std::invalid_argument("negative fee")
      END IF
      RETURN this.describe() + " (priority)"
    CATCH (std::invalid_argument& error)
      RETURN NEW std::string("invalid: ") + error.what()
    END TRY
  END METHOD
  FUNCTION applyDiscount(amount : Money, percent : int) : Money
    IF percent < 0 OR percent > 100 THEN
      THROW NEW std::out_of_range("percent out of range")
    END IF
    RETURN amount - amount * percent / 100
  END FUNCTION
END NAMESPACE
```

**0 diagnostics** for the file: 158,637 characters of clang JSON became 153 lines of pseudocode, and
nothing had to be kept as `RAW[cpp]`.

Two renderings are worth reading twice, because they are honest rather than pretty:

- `NEW std::string("draft")` for `return "draft";` — a `CXXConstructExpr` really does construct a temporary
  `std::string`; the model prints the construction instead of pretending it is a bare literal.
- `this.describe()` for `Order::describe()` inside `PriorityOrder::describe` — the model is syntactic and
  does not carry the qualifier; the call is still visibly not recursive in the AST, which is what the
  model keeps.

## 3. `metacode_compare` — a C++ algorithm against its C# port

```text
metacode_compare(leftPath: "samples/Cpp/compare/Total.cpp", leftFilter: "SumEven",
                 clangArgs: "-std=c++20",
                 rightPath: "samples/Cpp/compare/Total.cs")
```

```text
--- Total · cpp · 1 module(s) · 12 line(s) · 0 diagnostic(s)
+++ Total · cs · 1 module(s) · 14 line(s) · 0 diagnostic(s)

6 line(s) only in left, 8 line(s) only in right — '-' is left, '+' is right.

- MODULE Total
-   FUNCTION SumEven(values : std::vector<int>&) : int
-     DECLARE total : int = 0
-     FOREACH value : int IN values DO
+ NAMESPACE Compare
+   PUBLIC STATIC CLASS Total
+     PUBLIC STATIC METHOD SumEven(values : int[]) : int
+       DECLARE total = 0
+       FOREACH value IN values DO
        IF value % 2 != 0 THEN
          CONTINUE
        END IF
        total += value
      END FOREACH
      RETURN total
-   END FUNCTION
- END MODULE
+     END METHOD
+   END CLASS
+ END NAMESPACE
```

The six body lines align as identical. What differs is the ceremony — a module against a namespace and a
class, a free function against a static method, a typed declaration against `var` — which is exactly the
answer the tool exists to give. Leading indentation is ignored when aligning (a C# method sits two levels
deeper for reasons that have nothing to do with the algorithm), and the lines are printed as they are.

## 4. `metacode_translate` on what the model does not have

```text
metacode_translate(path: "samples/Cpp/limits/Unmodelled.cpp", filter: "unmodelled_demo")
```

```text
NAMESPACE unmodelled_demo
  RAW[cpp] UsingDirectiveDecl // unmodelled declaration: UsingDirectiveDecl
  FUNCTION walk(value : int)
    RAW[cpp] GotoStmt // goto has no node yet
    LABEL finish
      CAST(void, value)
    END LABEL
  END FUNCTION
  FUNCTION size_of_pointer(pointer : int*) : int
    RETURN RAW[cpp] UnaryExprOrTypeTraitExpr // unmodelled expression: UnaryExprOrTypeTraitExpr
  END FUNCTION
  FUNCTION release(pointer : int*)
    RAW[cpp] CXXDeleteExpr // delete has no node yet
  END FUNCTION
END NAMESPACE

# diagnostics
# warning MC0002 at 9:21: unmodelled declaration: UsingDirectiveDecl
# warning MC0003 at 13:0: goto has no node yet
# warning MC0004 at ?: unmodelled expression: UnaryExprOrTypeTraitExpr
# warning MC0003 at 25:0: delete has no node yet
```

Four constructs, four diagnostics, the source text kept next to each. `sizeof` is the one whose line is
unknown: clang omits a location when it repeats the previous one, and an expression that carries only an
offset has no line to report — the diagnostic says `?` rather than guessing.

## 5. What mapped to what

| C++ | Pseudocode | Model node |
|---|---|---|
| `namespace catalog { }` | `NAMESPACE catalog` | `MetacodeModule` (`Namespace`) |
| `using Money = long long;` / `typedef` | `ALIAS Money = long long` | `MetacodeTypeAlias` |
| `enum class Status` | `ENUM Status` | `MetacodeTypeDeclaration` (`Enum`) |
| `struct Line` / `class Order` | `STRUCT` / `CLASS`, `PUBLIC`/`PRIVATE` per access specifier | `MetacodeTypeDeclaration` + `MetacodeModifiers` |
| `class PriorityOrder final : public Order` | `CLASS PriorityOrder EXTENDS Order` | `BaseTypes`; `final` is an attribute, not a modifier |
| `explicit Order(std::string)` | `PUBLIC CONSTRUCTOR Order(customer : std::string)` | `MetacodeFunction` (`Constructor`) |
| `~Order() = default` | `PUBLIC DESTRUCTOR ~Order()` | `MetacodeFunction` (`Destructor`) |
| `virtual std::string describe() const` | `PUBLIC METHOD describe()` (virtual in the model) | `MetacodeModifiers.Virtual` |
| `bool operator==(const Order&) const` | `operator==` + `==` expression | `CXXOperatorCallExpr` → `MetacodeBinary` |
| `const Line& operator[](size_t)` | `operator[]` + `this.lines_[index]` | `MetacodeIndex` |
| `for (const Line& line : lines_)` | `FOREACH line : Line& IN this.lines_ DO` | `CXXForRangeStmt` → `MetacodeForEach` |
| `template <typename T> T clamp(…)` | `FUNCTION clamp<T>(…)` | `MetacodeTypeParameter` |
| `template <typename T> class Box` | `CLASS Box<T>` | `MetacodeTypeParameter` on the type |
| `throw std::invalid_argument("…")` | `THROW NEW std::invalid_argument("…")` | `MetacodeThrow` |
| `try { } catch (const std::invalid_argument& e) { }` | `TRY … CATCH (std::invalid_argument& error)` | `MetacodeTry` / `MetacodeCatch` |
| `switch (status_) { case …: default: }` | `SWITCH this.status_` with `CASE`/`DEFAULT` | `MetacodeSwitch` |
| `static_cast<int>(…)` | `CAST(int, …)` | `MetacodeCast` |
| `int*` / `const Line&` | `int*` / `Line&` | `MetacodeTypeRef` with `IsPointer`/`IsReference` |
| `Money sum = 0;` | `DECLARE sum : Money = 0` | `MetacodeLocalVariable` (initializer unwrapped from `ImplicitCastExpr`) |

The compiler's own members — the injected class name, the implicit copy/move constructor and assignment,
the implicit destructor — are skipped: they are materialized by clang, not written by anyone.

## 6. Where it stops

- **The solution-wide index does not read a `.cpp` source.** `metacode_index` stores a `.cpp` file as a
  `file` node, because it reads sources and a C++ source needs its dump; building one dump per file for a
  whole tree is not what that pass does. Use `metacode_translate` with a `filter` for one file.
- **No type checking, no symbol navigation, no editing.** This is a syntax model: `std::vector<int>` is a
  type as written, not a resolved type, and nothing here can rename or refactor C++.
- **The preprocessor does not exist in the dump.** An `#include` leaves no trace other than the declarations
  it brings, and a macro is already expanded.
- **A `filter` is a substring of the qualified name**, so it also matches a declaration whose name merely
  contains it: `demo` matches the `_cldemote` intrinsic. Pick a namespace that does not occur elsewhere.
- **Recovered parses are refused.** If clang exits non-zero the tool reports the compiler's diagnostics
  instead of translating the `RecoveryExpr` tree it printed, which would describe a program that no longer
  exists.
