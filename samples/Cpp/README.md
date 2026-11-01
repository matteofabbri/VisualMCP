# Cpp — a C++ project used to exercise the C++ metacode reader

This folder is a small, self-contained C++ project inside the VisualMCP repository. It exists so the C++
side of the server — `metacode_translate`, `metacode_compare`, `translate_cpp_file` — can be exercised, and
measured, against real C++ instead of a synthetic snippet. It is **not** part of `VisualMCP.sln`: the .NET
build is unaffected.

What the demo actually proves, tool by tool, with the output it produced, is in [DEMO.md](DEMO.md).

## Layout

| Path | What it is |
|---|---|
| `include/catalog/order.hpp` | The library header: namespace, type aliases, `enum class`, a struct, a class with access specifiers, a virtual method, overloaded `operator==`/`operator[]`, a derived class with `override`, a function template, a class template |
| `src/order.cpp` | The definitions: constructors/destructor, range-based `for`, `throw`, `switch` with `default`, `try`/`catch`, `static_cast`, `operator[]` |
| `src/main.cpp` | A console program that uses the library and checks itself (exit code 0 on success) |
| `compare/Total.cpp` | The same algorithm as `compare/Total.cs`, for `metacode_compare` |
| `compare/Total.cs` | The C# side of that pair — the C++ file has no .NET project, it is read, not compiled by the .NET build |
| `limits/Unmodelled.cpp` | Constructs the pseudocode model deliberately has no node for: each must survive as `RAW[cpp]` + a diagnostic |
| `build.ps1` | Builds `build/CppDemo.exe` with the C++ toolchain Visual Studio ships (`-Run` also runs it) |

## Build and run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File samples\Cpp\build.ps1 -Run
```

The script finds Visual Studio with `vswhere` and calls `cl` through `vcvars64.bat`. The server needs no
extra install for the demos: the clang it uses for the AST dump is the LLVM toolset that ships inside Visual
Studio (`VC\Tools\Llvm\x64\bin\clang.exe`), which the dumper discovers by itself — it is not on `PATH` on a
normal Windows developer machine.

## The demos, in one line each

```text
metacode_translate(path: samples/Cpp/src/order.cpp, filter: "catalog",
                   clangArgs: "-std=c++20 -I..\include")   → the whole library as pseudocode, 0 diagnostics
metacode_compare(leftPath: samples/Cpp/compare/Total.cpp, leftFilter: "SumEven",
                 clangArgs: "-std=c++20", rightPath: samples/Cpp/compare/Total.cs)
                                                          → the same body, line for line; only the ceremony differs
metacode_translate(path: samples/Cpp/limits/Unmodelled.cpp, filter: "unmodelled_demo")
                                                          → RAW[cpp] for goto/sizeof/delete/using, with diagnostics
```

`filter` and `clangArgs` are what make a real translation unit readable: without a filter, a file that
includes `<vector>` and `<string>` dumps **every declaration of the standard library** — 304 MB of JSON for
the three-line program in `DEMO.md` — and without `-std=`/`-I` clang may not parse the project at all.
