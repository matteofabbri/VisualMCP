using System.Text.RegularExpressions;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The metacode translator: one pseudocode shape for trees that come from different languages, and a
/// reader for the text it prints.
/// </summary>
public sealed class MetacodeTests
{
    private const string CSharpSum = """
        public static class Math
        {
            public static int Sum(int n)
            {
                var total = 0;
                for (var i = 0; i < n; i++)
                {
                    if (i % 2 == 0)
                    {
                        total += i;
                    }
                }

                return total;
            }
        }
        """;

    private const string RustSum = """
        fn sum(n: i32) -> i32 {
            let mut total = 0;
            for i in 0..n {
                if i % 2 == 0 {
                    total += i;
                }
            }
            total
        }
        """;

    private const string PythonSum = """
        {
          "_type": "Module",
          "body": [
            {
              "_type": "FunctionDef",
              "name": "sum",
              "args": {
                "_type": "arguments",
                "posonlyargs": [],
                "args": [{ "_type": "arg", "arg": "n" }],
                "vararg": null,
                "kwonlyargs": [],
                "kw_defaults": [],
                "kwarg": null,
                "defaults": []
              },
              "body": [
                {
                  "_type": "Assign",
                  "targets": [{ "_type": "Name", "id": "total" }],
                  "value": { "_type": "Constant", "value": 0 }
                },
                {
                  "_type": "For",
                  "target": { "_type": "Name", "id": "i" },
                  "iter": {
                    "_type": "Call",
                    "func": { "_type": "Name", "id": "range" },
                    "args": [{ "_type": "Name", "id": "n" }],
                    "keywords": []
                  },
                  "body": [
                    {
                      "_type": "If",
                      "test": {
                        "_type": "Compare",
                        "left": {
                          "_type": "BinOp",
                          "left": { "_type": "Name", "id": "i" },
                          "op": { "_type": "Mod" },
                          "right": { "_type": "Constant", "value": 2 }
                        },
                        "ops": [{ "_type": "Eq" }],
                        "comparators": [{ "_type": "Constant", "value": 0 }]
                      },
                      "body": [
                        {
                          "_type": "AugAssign",
                          "target": { "_type": "Name", "id": "total" },
                          "op": { "_type": "Add" },
                          "value": { "_type": "Name", "id": "i" }
                        }
                      ],
                      "orelse": []
                    }
                  ],
                  "orelse": []
                },
                { "_type": "Return", "value": { "_type": "Name", "id": "total" } }
              ],
              "decorator_list": [],
              "returns": null
            }
          ]
        }
        """;

    private const string CppSum = """
        {
          "kind": "TranslationUnitDecl",
          "inner": [
            {
              "kind": "FunctionDecl",
              "name": "sum",
              "type": { "qualType": "int (int)" },
              "inner": [
                { "kind": "ParmVarDecl", "name": "n", "type": { "qualType": "int" } },
                {
                  "kind": "CompoundStmt",
                  "inner": [
                    {
                      "kind": "DeclStmt",
                      "inner": [
                        {
                          "kind": "VarDecl",
                          "name": "total",
                          "type": { "qualType": "int" },
                          "inner": [{ "kind": "IntegerLiteral", "value": "0" }]
                        }
                      ]
                    },
                    {
                      "kind": "ForStmt",
                      "inner": [
                        {
                          "kind": "DeclStmt",
                          "inner": [
                            {
                              "kind": "VarDecl",
                              "name": "i",
                              "type": { "qualType": "int" },
                              "inner": [{ "kind": "IntegerLiteral", "value": "0" }]
                            }
                          ]
                        },
                        {
                          "kind": "BinaryOperator",
                          "opcode": "<",
                          "inner": [
                            { "kind": "ImplicitCastExpr", "inner": [{ "kind": "DeclRefExpr", "name": "i" }] },
                            { "kind": "ImplicitCastExpr", "inner": [{ "kind": "DeclRefExpr", "name": "n" }] }
                          ]
                        },
                        {
                          "kind": "UnaryOperator",
                          "opcode": "++",
                          "isPostfix": true,
                          "inner": [{ "kind": "DeclRefExpr", "name": "i" }]
                        },
                        {
                          "kind": "CompoundStmt",
                          "inner": [
                            {
                              "kind": "IfStmt",
                              "inner": [
                                {
                                  "kind": "BinaryOperator",
                                  "opcode": "==",
                                  "inner": [
                                    {
                                      "kind": "BinaryOperator",
                                      "opcode": "%",
                                      "inner": [
                                        { "kind": "DeclRefExpr", "name": "i" },
                                        { "kind": "IntegerLiteral", "value": "2" }
                                      ]
                                    },
                                    { "kind": "IntegerLiteral", "value": "0" }
                                  ]
                                },
                                {
                                  "kind": "CompoundStmt",
                                  "inner": [
                                    {
                                      "kind": "CompoundAssignOperator",
                                      "opcode": "+=",
                                      "inner": [
                                        { "kind": "DeclRefExpr", "name": "total" },
                                        { "kind": "DeclRefExpr", "name": "i" }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    },
                    { "kind": "ReturnStmt", "inner": [{ "kind": "DeclRefExpr", "name": "total" }] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string RenderCSharp(string source, MetacodeOptions? options = null) =>
        MetacodeTranslator.Default.Render(MetacodeSource.FromCSharp(source, "sample.cs"), options);

    /// <summary>
    /// The two dump readers are part of <see cref="MetacodeTranslator.Default"/>: what they need is an AST
    /// dump, and something in this product produces one (translate_python_file / translate_cpp_file, or
    /// metacode_translate on a .py/.cpp path).
    /// </summary>
    private static readonly MetacodeTranslator DumpTranslator = MetacodeTranslator.Default;

    private static string RenderPython(string json) =>
        DumpTranslator.Render(MetacodeSource.FromPythonAst(json, "sample.py"));

    private static string RenderCpp(string json) =>
        DumpTranslator.Render(MetacodeSource.FromCppAst(json, "sample.cpp"));

    /// <summary>
    /// The body of the function a fixture declares, with type annotations removed: those are exactly
    /// where the three languages differ, and the point is that nothing else does.
    /// </summary>
    private static string[] Body(string pseudocode)
    {
        var lines = pseudocode.Replace("\r", string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var signature = lines.FindIndex(line =>
            line.Contains("METHOD ", StringComparison.Ordinal) || line.Contains("FUNCTION ", StringComparison.Ordinal));

        return lines.Skip(signature + 1)
            .TakeWhile(line => !line.StartsWith("END FUNCTION", StringComparison.Ordinal)
                               && !line.StartsWith("END METHOD", StringComparison.Ordinal))
            .Select(line => Regex.Replace(line, " : [^=]+ = ", " = "))
            .ToArray();
    }

    [Fact]
    public void A_csharp_class_prints_one_shape()
    {
        var pseudocode = RenderCSharp("""
            namespace Demo;

            public sealed class Counter
            {
                private int _value;

                public int Increment(int by)
                {
                    for (var i = 0; i < by; i++)
                    {
                        _value = _value + 1;
                    }

                    return _value;
                }
            }
            """);

        Assert.Equal("""
            NAMESPACE Demo
              PUBLIC SEALED CLASS Counter
                PRIVATE FIELD _value : int
                PUBLIC METHOD Increment(by : int) : int
                  DECLARE i = 0
                  WHILE i < by DO
                    _value = _value + 1
                    i += 1
                  END WHILE
                  RETURN _value
                END METHOD
              END CLASS
            END NAMESPACE

            """, pseudocode);
    }

    [Fact]
    public void The_same_algorithm_prints_the_same_body_from_csharp_python_cpp_and_rust()
    {
        var csharp = Body(RenderCSharp(CSharpSum));
        var python = Body(RenderPython(PythonSum));
        var cpp = Body(RenderCpp(CppSum));
        // Rust is in the default pipeline: its reader parses the source, so no dump step is involved.
        var rust = Body(MetacodeTranslator.Default.Render(MetacodeSource.FromRust(RustSum, "sample.rs")));

        Assert.Equal(csharp, python);
        Assert.Equal(csharp, cpp);
        Assert.Equal(csharp, rust);

        Assert.Equal(new[]
        {
            "DECLARE total = 0",
            "DECLARE i = 0",
            "WHILE i < n DO",
            "IF i % 2 == 0 THEN",
            "total += i",
            "END IF",
            "i += 1",
            "END WHILE",
            "RETURN total",
        }, csharp);
    }

    [Fact]
    public void The_rendered_pseudocode_reads_back_to_the_same_text()
    {
        var source = """
            namespace Demo;

            public sealed class Counter
            {
                private int _value;

                public int Increment(int by)
                {
                    for (var i = 0; i < by; i++)
                    {
                        if (i > 0)
                        {
                            _value = _value + 1;
                        }
                    }

                    return _value;
                }
            }
            """;

        var first = RenderCSharp(source);
        var second = MetacodeTranslator.Default.Render(MetacodeSource.FromPseudocode(first, "sample.mc"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_richer_document_also_reads_back_to_the_same_text()
    {
        var source = """
            using System;
            using System.Collections.Generic;

            namespace Shop;

            public sealed class Order
            {
                private readonly List<string> _lines = new();

                public Order(string customer) => Customer = customer;

                public string Customer { get; private set; }

                public int Count => _lines.Count;

                public void Add(string item, int quantity = 1)
                {
                    for (var i = 0; i < quantity; i++)
                    {
                        _lines.Add(item);
                    }

                    if (_lines.Count > 10)
                    {
                        throw new InvalidOperationException($"too many {item}");
                    }
                }

                public string Describe(bool verbose)
                {
                    var head = _lines.Count == 0 ? "empty" : _lines[0];
                    var text = verbose ? $"{Customer}: {head}" : Customer ?? "?";
                    switch (_lines.Count)
                    {
                        case 0:
                            return "none";
                        default:
                            break;
                    }

                    try
                    {
                        return text.Trim();
                    }
                    catch (InvalidOperationException error) when (verbose)
                    {
                        return error.Message;
                    }
                    finally
                    {
                        Console.WriteLine("done");
                    }
                }
            }
            """;

        var first = RenderCSharp(source);
        var reloaded = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(first, "sample.mc"));
        var second = new PseudocodeRenderer().Render(reloaded);

        Assert.Equal(first, second);
        Assert.False(reloaded.HasErrors, string.Join("\n", reloaded.Diagnostics));
        Assert.Contains("NEW()", first);
        Assert.Contains("PROPERTY Customer : string", first);
        Assert.Contains("CATCH (InvalidOperationException error) WHEN verbose", first);
    }

    [Fact]
    public void Control_flow_normalization_is_a_choice()
    {
        var pseudocode = RenderCSharp(CSharpSum, MetacodeOptions.Default with { NormalizeControlFlow = false });

        Assert.Contains("FOR DECLARE i = 0; i < n; i += 1 DO", pseudocode);
        Assert.DoesNotContain("WHILE", pseudocode);
    }

    [Fact]
    public void A_continue_keeps_the_loop_it_belongs_to()
    {
        // Rewriting this `for` into a while would move the increment past the continue and change the
        // program, so the pass has to leave it alone.
        var pseudocode = RenderCSharp("""
            public class C
            {
                public void Walk(int n)
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (i == 3)
                        {
                            continue;
                        }

                        Use(i);
                    }
                }
            }
            """);

        Assert.Contains("FOR DECLARE i = 0; i < n; i += 1 DO", pseudocode);
    }

    [Fact]
    public void An_unsupported_statement_is_reported_and_kept()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp("""
            public class C
            {
                public void F()
                {
                    unsafe
                    {
                        Use();
                    }
                }
            }
            """, "sample.cs"));

        var diagnostic = Assert.Single(document.Diagnostics, d => d.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
        Assert.True(diagnostic.Origin.StartLine > 0);
        Assert.Contains("UnsafeStatement", diagnostic.Message);

        var pseudocode = new PseudocodeRenderer().Render(document);
        Assert.Contains("RAW[cs]", pseudocode);
    }

    [Fact]
    public void Python_source_text_is_refused_with_a_readable_reason()
    {
        var document = DumpTranslator.Translate(MetacodeSource.FromPythonAst("def f():\n    pass\n", "x.py"));

        Assert.True(document.HasErrors);
        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(MetacodeDiagnosticCodes.ParseError, diagnostic.Code);
        Assert.Contains("AST dumper", diagnostic.Message);
    }

    [Fact]
    public void Comments_survive_when_asked_for()
    {
        var source = """
            public class C
            {
                public void F()
                {
                    // the important part
                    Run();
                }
            }
            """;

        Assert.Contains("// the important part", RenderCSharp(source));
        Assert.DoesNotContain("// the important part", RenderCSharp(source, MetacodeOptions.Default with { IncludeComments = false }));
    }

    [Fact]
    public void A_python_or_cpp_source_is_not_a_dump_and_the_pipeline_says_so()
    {
        var directory = Path.Combine(Path.GetTempPath(), "metacode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var python = Path.Combine(directory, "sample.py");
            var cpp = Path.Combine(directory, "sample.cpp");
            File.WriteAllText(python, "def f():\n    pass\n");
            File.WriteAllText(cpp, "int f() { return 0; }\n");

            // The readers take an AST dump, not the source, so a .py/.cpp file is not handed to one (it
            // would be parsed as a malformed dump). The diagnostic names what produces the dump instead:
            // the two translate_*_file tools, or metacode_translate/metacode_compare on that path.
            var cppDiagnostic = Assert.Single(MetacodeTranslator.Default.Translate(MetacodeSource.FromFile(cpp)).Diagnostics);
            Assert.Equal(MetacodeDiagnosticCodes.NoFrontend, cppDiagnostic.Code);
            Assert.Contains("translate_cpp_file", cppDiagnostic.Message);

            Assert.Equal(MetacodeDiagnosticCodes.NoFrontend,
                Assert.Single(MetacodeTranslator.Default.Translate(MetacodeSource.FromFile(python)).Diagnostics).Code);

            Assert.False(new CppFrontend().CanHandle(MetacodeSource.FromFile(cpp)));
            Assert.False(new PythonFrontend().CanHandle(MetacodeSource.FromFile(python)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_source_no_frontend_reads_is_its_own_diagnostic()
    {
        // Go has no reader in this pipeline; Rust, which used to be this test's example, has one now.
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromText("package main", MetacodeLanguage.Go, "main.go"));

        Assert.Equal(MetacodeDiagnosticCodes.NoFrontend, Assert.Single(document.Diagnostics).Code);
        Assert.Empty(document.Modules);
    }
}
