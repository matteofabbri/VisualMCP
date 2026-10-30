using System.Text.RegularExpressions;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The constructs a first pass left as <c>RAW[linguaggio]</c>: C# locks, yields, events, deconstructed
/// and awaited foreach loops, Python comprehensions, imports and decorators, C++ templates, range-for,
/// lambdas and type aliases. Each one has a node now, and each one must print in the canonical text and
/// read back from it character for character — the reader is the other half of the format.
/// </summary>
public sealed class MetacodeDepthTests
{
    /// <summary>The C# fixture: every construct this pass added to the model, in one class.</summary>
    private const string CSharpDepth = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Demo;

        public sealed class Worker
        {
            public event EventHandler Changed;

            private readonly object _gate = new object();

            public IEnumerable<int> Count(int n)
            {
                lock (_gate)
                {
                    for (var i = 0; i < n; i++)
                    {
                        yield return i;
                    }
                }

                yield break;
            }

            public void Pairs(IEnumerable<KeyValuePair<string, int>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    Log(key);
                }
            }

            public async Task RunAsync(IAsyncEnumerable<int> source)
            {
                await foreach (var item in source)
                {
                    Log(item);
                }
            }

            public void Apply(List<int> xs)
            {
                xs.ForEach(x =>
                {
                    if (x > 0)
                    {
                        Log(x);
                    }
                });
            }

            private void Log(object value)
            {
            }
        }
        """;

    /// <summary>
    /// A real CPython dump (the dumper in docs/metacode.md) for imports, a decorated method, the four
    /// comprehension spellings and a decorated local function.
    /// </summary>
    private const string PythonDepth = """
        {
         "_type": "Module",
         "body": [
          {
           "_type": "Import",
           "names": [
            {
             "_type": "alias",
             "name": "os",
             "asname": null,
             "lineno": 1,
             "col_offset": 7,
             "end_lineno": 1,
             "end_col_offset": 9
            }
           ],
           "lineno": 1,
           "col_offset": 0,
           "end_lineno": 1,
           "end_col_offset": 9
          },
          {
           "_type": "ImportFrom",
           "module": "typing",
           "names": [
            {
             "_type": "alias",
             "name": "List",
             "asname": null,
             "lineno": 2,
             "col_offset": 19,
             "end_lineno": 2,
             "end_col_offset": 23
            }
           ],
           "level": 0,
           "lineno": 2,
           "col_offset": 0,
           "end_lineno": 2,
           "end_col_offset": 23
          },
          {
           "_type": "ClassDef",
           "name": "Store",
           "bases": [],
           "keywords": [],
           "body": [
            {
             "_type": "FunctionDef",
             "name": "build",
             "args": {
              "_type": "arguments",
              "posonlyargs": [],
              "args": [],
              "vararg": null,
              "kwonlyargs": [],
              "kw_defaults": [],
              "kwarg": null,
              "defaults": []
             },
             "body": [
              {
               "_type": "Return",
               "value": {
                "_type": "Call",
                "func": {
                 "_type": "Name",
                 "id": "Store",
                 "ctx": { "_type": "Load" },
                 "lineno": 8,
                 "col_offset": 15,
                 "end_lineno": 8,
                 "end_col_offset": 20
                },
                "args": [],
                "keywords": [],
                "lineno": 8,
                "col_offset": 15,
                "end_lineno": 8,
                "end_col_offset": 22
               },
               "lineno": 8,
               "col_offset": 8,
               "end_lineno": 8,
               "end_col_offset": 22
              }
             ],
             "decorator_list": [
              {
               "_type": "Name",
               "id": "staticmethod",
               "ctx": { "_type": "Load" },
               "lineno": 6,
               "col_offset": 5,
               "end_lineno": 6,
               "end_col_offset": 17
              }
             ],
             "returns": {
              "_type": "Name",
              "id": "Store",
              "ctx": { "_type": "Load" },
              "lineno": 7,
              "col_offset": 16,
              "end_lineno": 7,
              "end_col_offset": 21
             },
             "type_comment": null,
             "type_params": [],
             "lineno": 7,
             "col_offset": 4,
             "end_lineno": 8,
             "end_col_offset": 22
            }
           ],
           "decorator_list": [],
           "type_params": [],
           "lineno": 5,
           "col_offset": 0,
           "end_lineno": 8,
           "end_col_offset": 22
          },
          {
           "_type": "FunctionDef",
           "name": "collect",
           "args": {
            "_type": "arguments",
            "posonlyargs": [],
            "args": [
             {
              "_type": "arg",
              "arg": "xs",
              "annotation": null,
              "type_comment": null,
              "lineno": 11,
              "col_offset": 12,
              "end_lineno": 11,
              "end_col_offset": 14
             }
            ],
            "vararg": null,
            "kwonlyargs": [],
            "kw_defaults": [],
            "kwarg": null,
            "defaults": []
           },
           "body": [
            {
             "_type": "Assign",
             "targets": [
              {
               "_type": "Name",
               "id": "doubled",
               "ctx": { "_type": "Store" },
               "lineno": 12,
               "col_offset": 4,
               "end_lineno": 12,
               "end_col_offset": 11
              }
             ],
             "value": {
              "_type": "ListComp",
              "elt": {
               "_type": "BinOp",
               "left": {
                "_type": "Name",
                "id": "x",
                "ctx": { "_type": "Load" },
                "lineno": 12,
                "col_offset": 15,
                "end_lineno": 12,
                "end_col_offset": 16
               },
               "op": { "_type": "Mult" },
               "right": {
                "_type": "Constant",
                "value": 2,
                "kind": null,
                "lineno": 12,
                "col_offset": 19,
                "end_lineno": 12,
                "end_col_offset": 20
               },
               "lineno": 12,
               "col_offset": 15,
               "end_lineno": 12,
               "end_col_offset": 20
              },
              "generators": [
               {
                "_type": "comprehension",
                "target": {
                 "_type": "Name",
                 "id": "x",
                 "ctx": { "_type": "Store" },
                 "lineno": 12,
                 "col_offset": 25,
                 "end_lineno": 12,
                 "end_col_offset": 26
                },
                "iter": {
                 "_type": "Name",
                 "id": "xs",
                 "ctx": { "_type": "Load" },
                 "lineno": 12,
                 "col_offset": 30,
                 "end_lineno": 12,
                 "end_col_offset": 32
                },
                "ifs": [
                 {
                  "_type": "Compare",
                  "left": {
                   "_type": "Name",
                   "id": "x",
                   "ctx": { "_type": "Load" },
                   "lineno": 12,
                   "col_offset": 36,
                   "end_lineno": 12,
                   "end_col_offset": 37
                  },
                  "ops": [ { "_type": "Gt" } ],
                  "comparators": [
                   {
                    "_type": "Constant",
                    "value": 0,
                    "kind": null,
                    "lineno": 12,
                    "col_offset": 40,
                    "end_lineno": 12,
                    "end_col_offset": 41
                   }
                  ],
                  "lineno": 12,
                  "col_offset": 36,
                  "end_lineno": 12,
                  "end_col_offset": 41
                 }
                ],
                "is_async": 0
               }
              ],
              "lineno": 12,
              "col_offset": 14,
              "end_lineno": 12,
              "end_col_offset": 42
             },
             "type_comment": null,
             "lineno": 12,
             "col_offset": 4,
             "end_lineno": 12,
             "end_col_offset": 42
            },
            {
             "_type": "Assign",
             "targets": [
              {
               "_type": "Name",
               "id": "lookup",
               "ctx": { "_type": "Store" },
               "lineno": 13,
               "col_offset": 4,
               "end_lineno": 13,
               "end_col_offset": 10
              }
             ],
             "value": {
              "_type": "DictComp",
              "key": {
               "_type": "Name",
               "id": "k",
               "ctx": { "_type": "Load" },
               "lineno": 13,
               "col_offset": 14,
               "end_lineno": 13,
               "end_col_offset": 15
              },
              "value": {
               "_type": "Name",
               "id": "v",
               "ctx": { "_type": "Load" },
               "lineno": 13,
               "col_offset": 17,
               "end_lineno": 13,
               "end_col_offset": 18
              },
              "generators": [
               {
                "_type": "comprehension",
                "target": {
                 "_type": "Tuple",
                 "elts": [
                  {
                   "_type": "Name",
                   "id": "k",
                   "ctx": { "_type": "Store" },
                   "lineno": 13,
                   "col_offset": 23,
                   "end_lineno": 13,
                   "end_col_offset": 24
                  },
                  {
                   "_type": "Name",
                   "id": "v",
                   "ctx": { "_type": "Store" },
                   "lineno": 13,
                   "col_offset": 26,
                   "end_lineno": 13,
                   "end_col_offset": 27
                  }
                 ],
                 "ctx": { "_type": "Store" },
                 "lineno": 13,
                 "col_offset": 23,
                 "end_lineno": 13,
                 "end_col_offset": 27
                },
                "iter": {
                 "_type": "Name",
                 "id": "xs",
                 "ctx": { "_type": "Load" },
                 "lineno": 13,
                 "col_offset": 31,
                 "end_lineno": 13,
                 "end_col_offset": 33
                },
                "ifs": [],
                "is_async": 0
               }
              ],
              "lineno": 13,
              "col_offset": 13,
              "end_lineno": 13,
              "end_col_offset": 34
             },
             "type_comment": null,
             "lineno": 13,
             "col_offset": 4,
             "end_lineno": 13,
             "end_col_offset": 34
            },
            {
             "_type": "Assign",
             "targets": [
              {
               "_type": "Name",
               "id": "unique",
               "ctx": { "_type": "Store" },
               "lineno": 14,
               "col_offset": 4,
               "end_lineno": 14,
               "end_col_offset": 10
              }
             ],
             "value": {
              "_type": "SetComp",
              "elt": {
               "_type": "Name",
               "id": "n",
               "ctx": { "_type": "Load" },
               "lineno": 14,
               "col_offset": 14,
               "end_lineno": 14,
               "end_col_offset": 15
              },
              "generators": [
               {
                "_type": "comprehension",
                "target": {
                 "_type": "Name",
                 "id": "n",
                 "ctx": { "_type": "Store" },
                 "lineno": 14,
                 "col_offset": 20,
                 "end_lineno": 14,
                 "end_col_offset": 21
                },
                "iter": {
                 "_type": "Name",
                 "id": "xs",
                 "ctx": { "_type": "Load" },
                 "lineno": 14,
                 "col_offset": 25,
                 "end_lineno": 14,
                 "end_col_offset": 27
                },
                "ifs": [],
                "is_async": 0
               }
              ],
              "lineno": 14,
              "col_offset": 13,
              "end_lineno": 14,
              "end_col_offset": 28
             },
             "type_comment": null,
             "lineno": 14,
             "col_offset": 4,
             "end_lineno": 14,
             "end_col_offset": 28
            },
            {
             "_type": "Assign",
             "targets": [
              {
               "_type": "Name",
               "id": "stream",
               "ctx": { "_type": "Store" },
               "lineno": 15,
               "col_offset": 4,
               "end_lineno": 15,
               "end_col_offset": 10
              }
             ],
             "value": {
              "_type": "GeneratorExp",
              "elt": {
               "_type": "Name",
               "id": "n",
               "ctx": { "_type": "Load" },
               "lineno": 15,
               "col_offset": 14,
               "end_lineno": 15,
               "end_col_offset": 15
              },
              "generators": [
               {
                "_type": "comprehension",
                "target": {
                 "_type": "Name",
                 "id": "n",
                 "ctx": { "_type": "Store" },
                 "lineno": 15,
                 "col_offset": 20,
                 "end_lineno": 15,
                 "end_col_offset": 21
                },
                "iter": {
                 "_type": "Name",
                 "id": "xs",
                 "ctx": { "_type": "Load" },
                 "lineno": 15,
                 "col_offset": 25,
                 "end_lineno": 15,
                 "end_col_offset": 27
                },
                "ifs": [],
                "is_async": 0
               }
              ],
              "lineno": 15,
              "col_offset": 13,
              "end_lineno": 15,
              "end_col_offset": 28
             },
             "type_comment": null,
             "lineno": 15,
             "col_offset": 4,
             "end_lineno": 15,
             "end_col_offset": 28
            },
            {
             "_type": "Return",
             "value": {
              "_type": "Name",
              "id": "doubled",
              "ctx": { "_type": "Load" },
              "lineno": 16,
              "col_offset": 11,
              "end_lineno": 16,
              "end_col_offset": 18
             },
             "lineno": 16,
             "col_offset": 4,
             "end_lineno": 16,
             "end_col_offset": 18
            }
           ],
           "decorator_list": [],
           "returns": null,
           "type_comment": null,
           "type_params": [],
           "lineno": 11,
           "col_offset": 0,
           "end_lineno": 16,
           "end_col_offset": 18
          },
          {
           "_type": "FunctionDef",
           "name": "outer",
           "args": {
            "_type": "arguments",
            "posonlyargs": [],
            "args": [],
            "vararg": null,
            "kwonlyargs": [],
            "kw_defaults": [],
            "kwarg": null,
            "defaults": []
           },
           "body": [
            {
             "_type": "FunctionDef",
             "name": "inner",
             "args": {
              "_type": "arguments",
              "posonlyargs": [],
              "args": [],
              "vararg": null,
              "kwonlyargs": [],
              "kw_defaults": [],
              "kwarg": null,
              "defaults": []
             },
             "body": [
              {
               "_type": "Return",
               "value": {
                "_type": "Constant",
                "value": 1,
                "kind": null,
                "lineno": 22,
                "col_offset": 15,
                "end_lineno": 22,
                "end_col_offset": 16
               },
               "lineno": 22,
               "col_offset": 8,
               "end_lineno": 22,
               "end_col_offset": 16
              }
             ],
             "decorator_list": [
              {
               "_type": "Name",
               "id": "staticmethod",
               "ctx": { "_type": "Load" },
               "lineno": 20,
               "col_offset": 5,
               "end_lineno": 20,
               "end_col_offset": 17
              }
             ],
             "returns": null,
             "type_comment": null,
             "type_params": [],
             "lineno": 21,
             "col_offset": 4,
             "end_lineno": 22,
             "end_col_offset": 16
            },
            {
             "_type": "Return",
             "value": {
              "_type": "Name",
              "id": "inner",
              "ctx": { "_type": "Load" },
              "lineno": 23,
              "col_offset": 11,
              "end_lineno": 23,
              "end_col_offset": 16
             },
             "lineno": 23,
             "col_offset": 4,
             "end_lineno": 23,
             "end_col_offset": 16
            }
           ],
           "decorator_list": [],
           "returns": null,
           "type_comment": null,
           "type_params": [],
           "lineno": 19,
           "col_offset": 0,
           "end_lineno": 23,
           "end_col_offset": 16
          }
         ],
         "type_ignores": []
        }
        """;

    /// <summary>
    /// A clang <c>-ast-dump=json</c> document: a function template, a class template, a type alias, a
    /// range-based for and a lambda. Written from the documented shape of the dump — clang is not on
    /// this machine, so this fixture is not a captured dump like the Python one.
    /// </summary>
    private const string CppDepth = """
        {
         "kind": "TranslationUnitDecl",
         "inner": [
          {
           "kind": "FunctionTemplateDecl",
           "name": "max",
           "inner": [
            { "kind": "TemplateTypeParmDecl", "name": "T" },
            {
             "kind": "FunctionDecl",
             "name": "max",
             "type": { "qualType": "T (T, T)" },
             "inner": [
              { "kind": "ParmVarDecl", "name": "a", "type": { "qualType": "T" } },
              { "kind": "ParmVarDecl", "name": "b", "type": { "qualType": "T" } },
              {
               "kind": "CompoundStmt",
               "inner": [
                {
                 "kind": "ReturnStmt",
                 "inner": [
                  {
                   "kind": "ConditionalOperator",
                   "inner": [
                    {
                     "kind": "BinaryOperator",
                     "opcode": ">",
                     "inner": [
                      { "kind": "DeclRefExpr", "name": "a" },
                      { "kind": "DeclRefExpr", "name": "b" }
                     ]
                    },
                    { "kind": "DeclRefExpr", "name": "a" },
                    { "kind": "DeclRefExpr", "name": "b" }
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
          {
           "kind": "ClassTemplateDecl",
           "name": "Box",
           "inner": [
            { "kind": "TemplateTypeParmDecl", "name": "T" },
            {
             "kind": "CXXRecordDecl",
             "name": "Box",
             "tagUsed": "class",
             "completeDefinition": true,
             "inner": [
              { "kind": "AccessSpecDecl", "access": "public" },
              { "kind": "FieldDecl", "name": "value", "type": { "qualType": "T" } }
             ]
            }
           ]
          },
          {
           "kind": "TypeAliasDecl",
           "name": "Count",
           "type": { "qualType": "int" }
          },
          {
           "kind": "FunctionDecl",
           "name": "walk",
           "type": { "qualType": "void (int *)" },
           "inner": [
            { "kind": "ParmVarDecl", "name": "values", "type": { "qualType": "int *" } },
            {
             "kind": "CompoundStmt",
             "inner": [
              {
               "kind": "CXXForRangeStmt",
               "inner": [
                {
                 "kind": "DeclStmt",
                 "inner": [
                  {
                   "kind": "VarDecl",
                   "name": "__range",
                   "type": { "qualType": "int *" },
                   "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                  }
                 ]
                },
                {
                 "kind": "CXXMemberCallExpr",
                 "type": { "qualType": "int *" },
                 "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                },
                {
                 "kind": "CXXMemberCallExpr",
                 "type": { "qualType": "int *" },
                 "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                },
                {
                 "kind": "BinaryOperator",
                 "opcode": "!=",
                 "inner": [
                  { "kind": "DeclRefExpr", "name": "__begin" },
                  { "kind": "DeclRefExpr", "name": "__end" }
                 ]
                },
                {
                 "kind": "UnaryOperator",
                 "opcode": "++",
                 "inner": [ { "kind": "DeclRefExpr", "name": "__begin" } ]
                },
                {
                 "kind": "DeclStmt",
                 "inner": [ { "kind": "VarDecl", "name": "item", "type": { "qualType": "int" } } ]
                },
                {
                 "kind": "CompoundStmt",
                 "inner": [
                  {
                   "kind": "CallExpr",
                   "inner": [
                    { "kind": "DeclRefExpr", "name": "Use" },
                    { "kind": "DeclRefExpr", "name": "item" }
                   ]
                  }
                 ]
                }
               ]
              },
              {
               "kind": "DeclStmt",
               "inner": [
                {
                 "kind": "VarDecl",
                 "name": "twice",
                 "type": { "qualType": "auto" },
                 "inner": [
                  {
                   "kind": "LambdaExpr",
                   "inner": [
                    {
                     "kind": "CXXRecordDecl",
                     "name": "",
                     "inner": [
                      {
                       "kind": "CXXMethodDecl",
                       "name": "operator()",
                       "type": { "qualType": "int (int) const" },
                       "inner": [
                        { "kind": "ParmVarDecl", "name": "x", "type": { "qualType": "int" } },
                        {
                         "kind": "CompoundStmt",
                         "inner": [
                          {
                           "kind": "ReturnStmt",
                           "inner": [
                            {
                             "kind": "BinaryOperator",
                             "opcode": "*",
                             "inner": [
                              { "kind": "DeclRefExpr", "name": "x" },
                              { "kind": "IntegerLiteral", "value": "2" }
                             ]
                            }
                           ]
                          }
                         ]
                        }
                       ]
                      }
                     ]
                    }
                   ]
                  }
                 ]
                }
               ]
              }
             ]
            }
           ]
          }
         ]
        }
        """;

    /// <summary>A clang dump for the one function the three-language equivalence test compares.</summary>
    private const string CppWalk = """
        {
         "kind": "TranslationUnitDecl",
         "inner": [
          {
           "kind": "FunctionDecl",
           "name": "walk",
           "type": { "qualType": "void (int *)" },
           "inner": [
            { "kind": "ParmVarDecl", "name": "values", "type": { "qualType": "int *" } },
            {
             "kind": "CompoundStmt",
             "inner": [
              {
               "kind": "CXXForRangeStmt",
               "inner": [
                {
                 "kind": "DeclStmt",
                 "inner": [
                  {
                   "kind": "VarDecl",
                   "name": "__range",
                   "type": { "qualType": "int *" },
                   "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                  }
                 ]
                },
                {
                 "kind": "CXXMemberCallExpr",
                 "type": { "qualType": "int *" },
                 "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                },
                {
                 "kind": "CXXMemberCallExpr",
                 "type": { "qualType": "int *" },
                 "inner": [ { "kind": "DeclRefExpr", "name": "values" } ]
                },
                {
                 "kind": "BinaryOperator",
                 "opcode": "!=",
                 "inner": [
                  { "kind": "DeclRefExpr", "name": "__begin" },
                  { "kind": "DeclRefExpr", "name": "__end" }
                 ]
                },
                {
                 "kind": "UnaryOperator",
                 "opcode": "++",
                 "inner": [ { "kind": "DeclRefExpr", "name": "__begin" } ]
                },
                {
                 "kind": "DeclStmt",
                 "inner": [ { "kind": "VarDecl", "name": "item", "type": { "qualType": "int" } } ]
                },
                {
                 "kind": "CompoundStmt",
                 "inner": [
                  {
                   "kind": "CallExpr",
                   "inner": [
                    { "kind": "DeclRefExpr", "name": "Use" },
                    { "kind": "DeclRefExpr", "name": "item" }
                   ]
                  }
                 ]
                }
               ]
              }
             ]
            }
           ]
          }
         ]
        }
        """;

    /// <summary>The same loop in Python, as CPython's <c>ast</c> module dumps it.</summary>
    private const string PythonWalk = """
        {
         "_type": "Module",
         "body": [
          {
           "_type": "FunctionDef",
           "name": "walk",
           "args": {
            "_type": "arguments",
            "posonlyargs": [],
            "args": [
             {
              "_type": "arg",
              "arg": "values",
              "annotation": null,
              "type_comment": null,
              "lineno": 1,
              "col_offset": 9,
              "end_lineno": 1,
              "end_col_offset": 15
             }
            ],
            "vararg": null,
            "kwonlyargs": [],
            "kw_defaults": [],
            "kwarg": null,
            "defaults": []
           },
           "body": [
            {
             "_type": "For",
             "target": {
              "_type": "Name",
              "id": "item",
              "ctx": { "_type": "Store" },
              "lineno": 2,
              "col_offset": 8,
              "end_lineno": 2,
              "end_col_offset": 12
             },
             "iter": {
              "_type": "Name",
              "id": "values",
              "ctx": { "_type": "Load" },
              "lineno": 2,
              "col_offset": 16,
              "end_lineno": 2,
              "end_col_offset": 22
             },
             "body": [
              {
               "_type": "Expr",
               "value": {
                "_type": "Call",
                "func": {
                 "_type": "Name",
                 "id": "Use",
                 "ctx": { "_type": "Load" },
                 "lineno": 3,
                 "col_offset": 8,
                 "end_lineno": 3,
                 "end_col_offset": 11
                },
                "args": [
                 {
                  "_type": "Name",
                  "id": "item",
                  "ctx": { "_type": "Load" },
                  "lineno": 3,
                  "col_offset": 12,
                  "end_lineno": 3,
                  "end_col_offset": 16
                 }
                ],
                "keywords": [],
                "lineno": 3,
                "col_offset": 8,
                "end_lineno": 3,
                "end_col_offset": 17
               },
               "lineno": 3,
               "col_offset": 8,
               "end_lineno": 3,
               "end_col_offset": 17
              }
             ],
             "orelse": [],
             "type_comment": null,
             "lineno": 2,
             "col_offset": 4,
             "end_lineno": 3,
             "end_col_offset": 17
            }
           ],
           "decorator_list": [],
           "returns": null,
           "type_comment": null,
           "type_params": [],
           "lineno": 1,
           "col_offset": 0,
           "end_lineno": 3,
           "end_col_offset": 17
          }
         ],
         "type_ignores": []
        }
        """;

    /// <summary>The same loop in C#: <c>foreach</c> over an array.</summary>
    private const string CSharpWalk = """
        public class C
        {
            public void Walk(int[] values)
            {
                foreach (var item in values)
                {
                    Use(item);
                }
            }
        }
        """;

    private static string RenderCSharp(string source, MetacodeOptions? options = null) =>
        MetacodeTranslator.Default.Render(MetacodeSource.FromCSharp(source, "sample.cs"), options);

    /// <summary>
    /// The opt-in readers for an AST dump. Python and C++ are not in <see cref="MetacodeTranslator.Default"/>:
    /// nothing in this product produces their dump, so a caller who has one asks for that reader explicitly.
    /// </summary>
    private static readonly MetacodeTranslator DumpTranslator = new(new ILanguageFrontend[]
    {
        new PythonFrontend(),
        new CppFrontend(),
    });

    private static string RenderPython(string json) =>
        DumpTranslator.Render(MetacodeSource.FromPythonAst(json, "sample.py"));

    private static string RenderCpp(string json) =>
        DumpTranslator.Render(MetacodeSource.FromCppAst(json, "sample.cpp"));

    /// <summary>A translation that reported nothing it could not place.</summary>
    private static string RenderClean(string source)
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(source, "sample.cs"));
        Assert.Empty(document.Diagnostics);
        return new PseudocodeRenderer().Render(document);
    }

    /// <summary>Render, read the text back, render again: the two texts must be the same string.</summary>
    private static string RoundTrip(string pseudocode)
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(pseudocode, "sample.mc"));
        Assert.False(document.HasErrors, string.Join("\n", document.Diagnostics));
        return new PseudocodeRenderer().Render(document);
    }

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
            .Select(line => Regex.Replace(line, "^FOREACH (\\S+) : [^ ]+ IN ", "FOREACH $1 IN "))
            .ToArray();
    }

    [Fact]
    public void A_lock_is_a_scoped_block()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                private readonly object _gate = new object();

                public void Run()
                {
                    lock (_gate)
                    {
                        Use();
                    }
                }
            }
            """);

        Assert.Contains("LOCK _gate DO", pseudocode);
        Assert.Contains("END LOCK", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_yield_says_whether_it_returns_or_breaks()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public IEnumerable<int> Count(int n)
                {
                    for (var i = 0; i < n; i++)
                    {
                        yield return i;
                    }

                    yield break;
                }
            }
            """);

        Assert.Contains("YIELD RETURN i", pseudocode);
        Assert.Contains("YIELD BREAK", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void An_event_is_a_member_with_a_type()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public event EventHandler Changed;
            }
            """);

        Assert.Contains("PUBLIC EVENT Changed : EventHandler", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_deconstructing_foreach_keeps_both_names()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public void Pairs(IEnumerable<KeyValuePair<string, int>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        Log(key);
                    }
                }
            }
            """);

        Assert.Contains("FOREACH (key, value) IN pairs DO", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void An_awaited_foreach_says_so()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public async Task RunAsync(IAsyncEnumerable<int> source)
                {
                    await foreach (var item in source)
                    {
                        Log(item);
                    }
                }
            }
            """);

        Assert.Contains("AWAIT FOREACH item IN source DO", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_block_bodied_lambda_is_read_back_with_its_statements()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public void Apply(List<int> xs)
                {
                    xs.ForEach(x =>
                    {
                        if (x > 0)
                        {
                            return;
                        }

                        Use(x);
                    });
                }
            }
            """);

        // The body is a block of statements inside an expression: the reader has to keep them apart.
        Assert.Contains("=> {", pseudocode);
        Assert.Contains("IF x > 0 THEN", pseudocode);
        Assert.Contains("Use(x)", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_nested_block_lambda_keeps_its_own_block()
    {
        var pseudocode = RenderClean("""
            public class C
            {
                public void Apply(List<int> xs)
                {
                    xs.Select(x =>
                    {
                        var doubled = x * 2;
                        return doubled;
                    });
                }
            }
            """);

        Assert.Contains("DECLARE doubled", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void Every_new_csharp_construct_reads_back_to_the_same_text()
    {
        var pseudocode = RenderClean(CSharpDepth);

        Assert.Contains("LOCK _gate DO", pseudocode);
        Assert.Contains("YIELD RETURN i", pseudocode);
        Assert.Contains("YIELD BREAK", pseudocode);
        Assert.Contains("PUBLIC EVENT Changed : EventHandler", pseudocode);
        Assert.Contains("FOREACH (key, value) IN pairs DO", pseudocode);
        Assert.Contains("AWAIT FOREACH item IN source DO", pseudocode);
        Assert.Contains("=> {", pseudocode);

        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void Python_comprehensions_print_their_clauses()
    {
        var pseudocode = RenderPython(PythonDepth);

        Assert.Contains("DECLARE doubled = [x * 2 FOR x IN xs IF x > 0]", pseudocode);
        Assert.Contains("DECLARE lookup = {k : v FOR k, v IN xs}", pseudocode);
        Assert.Contains("DECLARE unique = {n FOR n IN xs}", pseudocode);
        Assert.Contains("DECLARE stream = (n FOR n IN xs)", pseudocode);
    }

    [Fact]
    public void Python_imports_and_decorators_are_modelled()
    {
        var pseudocode = RenderPython(PythonDepth);

        Assert.Contains("IMPORT os", pseudocode);
        Assert.Contains("FROM typing IMPORT List", pseudocode);
        Assert.Contains("@staticmethod", pseudocode);
        Assert.Contains("METHOD build() : Store", pseudocode);
    }

    [Fact]
    public void The_python_document_reads_back_to_the_same_text()
    {
        var document = DumpTranslator.Translate(MetacodeSource.FromPythonAst(PythonDepth, "sample.py"));
        Assert.Empty(document.Diagnostics);

        var pseudocode = new PseudocodeRenderer().Render(document);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void Cpp_templates_range_for_lambdas_and_aliases_are_modelled()
    {
        var document = DumpTranslator.Translate(MetacodeSource.FromCppAst(CppDepth, "sample.cpp"));
        Assert.Empty(document.Diagnostics);

        var pseudocode = new PseudocodeRenderer().Render(document);

        Assert.Contains("FUNCTION max<T>(a : T, b : T) : T", pseudocode);
        Assert.Contains("CLASS Box<T>", pseudocode);
        Assert.Contains("PUBLIC FIELD value : T", pseudocode);
        Assert.Contains("ALIAS Count = int", pseudocode);
        Assert.Contains("FOREACH item : int IN values DO", pseudocode);
        Assert.Contains("DECLARE twice : auto = (x : int) => {", pseudocode);
        Assert.Contains("RETURN x * 2", pseudocode);
    }

    [Fact]
    public void The_cpp_document_reads_back_to_the_same_text()
    {
        var pseudocode = RenderCpp(CppDepth);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void The_same_foreach_prints_the_same_body_from_csharp_python_and_cpp()
    {
        var csharp = Body(RenderCSharp(CSharpWalk));
        var python = Body(RenderPython(PythonWalk));
        var cpp = Body(RenderCpp(CppWalk));

        Assert.Equal(csharp, python);
        Assert.Equal(csharp, cpp);

        Assert.Equal(new[]
        {
            "FOREACH item IN values DO",
            "Use(item)",
            "END FOREACH",
        }, csharp);
    }

    [Fact]
    public void A_construct_outside_the_model_is_still_reported()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp("""
            public class C
            {
                public unsafe void Pin(int value)
                {
                    fixed (int* pointer = &value)
                    {
                        Use(pointer);
                    }
                }
            }
            """, "sample.cs"));

        var diagnostic = Assert.Single(document.Diagnostics, d => d.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
        Assert.Contains("FixedStatement", diagnostic.Message);

        var pseudocode = new PseudocodeRenderer().Render(document);
        Assert.Contains("RAW[cs]", pseudocode);
        Assert.Contains("FixedStatement has no pseudocode node", pseudocode);
    }
}
