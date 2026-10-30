using Microsoft.Extensions.DependencyInjection;
using Snitch.Metacode;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Decompilation as a producer of metacode, and Ghidra as a producer of the same model.
/// <para>
/// The Ghidra half never runs Ghidra: the fixture below is real recorded output — two functions of
/// <c>C:\Windows\System32\version.dll</c>, captured by running Ghidra 12.2 headless once — and the
/// tests replay it through <see cref="GhidraSnapshot"/> and <see cref="GhidraFrontend"/>. Running the
/// decompiler inside a test would be slow and would make the result depend on the machine.
/// </para>
/// </summary>
public sealed class DecompilationMetacodeTests
{
    /// <summary>
    /// A recorded Ghidra snapshot, verbatim except that it keeps only two functions. The C still has
    /// what real Ghidra output has: <c>undefined8</c>, <c>FUN_...</c>, casts, a store through a
    /// pointer and hex literals.
    /// </summary>
    private const string GhidraSnapshotJson = """
        {
         "image": "version.dll",
         "language": "x86:LE:64:default",
         "entry": "1800010f0",
         "imageBase": "180000000",
         "functions": [
          {
           "entry": "180001124",
           "name": "FUN_180001124",
           "signature": "undefined8 __fastcall FUN_180001124(undefined8 param_1, int param_2)",
           "size": 124,
           "decompiled": "\r\nundefined8 FUN_180001124(undefined8 param_1,int param_2)\r\n\r\n{\r\n  if (param_2 == 0) {\r\n    if (DAT_180007090 != 0xffffffff) {\r\n      TlsFree(DAT_180007090);\r\n    }\r\n    if (DAT_180007628 != (HMODULE)0x0) {\r\n      FreeLibrary(DAT_180007628);\r\n    }\r\n  }\r\n  else {\r\n    if (param_2 == 1) {\r\n      DAT_180007090 = TlsAlloc();\r\n    }\r\n    else if (param_2 != 2) {\r\n      return 1;\r\n    }\r\n    if (DAT_180007090 != 0xffffffff) {\r\n      TlsSetValue(DAT_180007090,(LPVOID)0x0);\r\n    }\r\n  }\r\n  return 1;\r\n}\r\n\r\n",
           "calls": [ "EXTERNAL:00000029", "EXTERNAL:00000028", "EXTERNAL:00000021", "EXTERNAL:00000023" ]
          },
          {
           "entry": "1800011b0",
           "name": "FUN_1800011b0",
           "signature": "bool __fastcall FUN_1800011b0(void)",
           "size": 52,
           "decompiled": "\r\nbool FUN_1800011b0(void)\r\n\r\n{\r\n  bool bVar1;\r\n  \r\n  DAT_180007658 = malloc(0x100);\r\n  bVar1 = DAT_180007658 != (undefined8 *)0x0;\r\n  DAT_180007660 = DAT_180007658;\r\n  if (bVar1) {\r\n    *DAT_180007658 = 0;\r\n  }\r\n  return !bVar1;\r\n}\r\n\r\n",
           "calls": [ "EXTERNAL:00000006" ]
          }
         ]
        }
        """;

    private const string GhidraBinary = @"C:\Windows\System32\version.dll";

    private static MetacodeDocument GhidraDocument() =>
        new GhidraFrontend().Translate(MetacodeSource.FromGhidra(GhidraSnapshotJson, "version.json"));

    private static MetacodeFunction GhidraFunctionNamed(MetacodeDocument document, string name) =>
        document.Modules.SelectMany(m => m.Declarations).OfType<MetacodeFunction>().Single(f => f.Name == name);

    private static string Render(MetacodeDocument document) => new PseudocodeRenderer().Render(document);

    // ------------------------------------------------------------------ ILSpy → metacode

    [Fact]
    public void A_decompiled_type_becomes_metacode_with_its_members()
    {
        var assembly = typeof(MetacodeTranslator).Assembly.Location;
        var result = new DecompileService().DecompileTypeToMetacode(assembly, "Snitch.Metacode.MetacodeTranslator");

        Assert.Null(result.Error);
        var type = Assert.Single(result.Types);
        Assert.Equal("Snitch.Metacode.MetacodeTranslator", type.FullName);
        Assert.Equal(6, type.Members);
        Assert.Equal(4, type.Functions);

        // The shape the model gives every language: a namespace, a type, its members, its bodies.
        Assert.Contains("NAMESPACE Snitch.Metacode", type.Pseudocode);
        Assert.Contains("CLASS MetacodeTranslator", type.Pseudocode);
        Assert.Contains("PROPERTY Default : MetacodeTranslator", type.Pseudocode);
        Assert.Contains("METHOD Translate(source : MetacodeSource", type.Pseudocode);
        Assert.Contains("IF languageFrontend == null THEN", type.Pseudocode);
        Assert.Contains("RETURN metacodeDocument", type.Pseudocode);
    }

    [Fact]
    public void Every_node_of_a_decompiled_type_can_be_traced_to_the_csharp_ilspy_printed()
    {
        var assembly = typeof(MetacodeTranslator).Assembly.Location;
        var document = new DecompileService().DecompileTypeToMetacode(assembly, "Snitch.Metacode.MetacodeTranslator").Document;

        Assert.False(document.HasErrors, string.Join("\n", document.Diagnostics));

        var type = Assert.Single(document.Modules.SelectMany(m => m.Declarations).OfType<MetacodeTypeDeclaration>());
        var method = Assert.Single(type.Members.OfType<MetacodeFunction>(), f => f.Name == "Translate");

        Assert.NotNull(method.Origin.Document);
        Assert.Contains("Snitch.Metacode.MetacodeTranslator", method.Origin.Document);
        Assert.EndsWith(".cs", method.Origin.Document);
        Assert.True(method.Origin.StartLine > 0);
        Assert.NotEmpty(method.ParameterList);
    }

    [Fact]
    public void A_whole_assembly_is_decompiled_type_by_type_and_paged()
    {
        var assembly = typeof(MetacodeTranslator).Assembly.Location;
        var page = new DecompileService().DecompileAssemblyToMetacode(
            assembly, nameFilter: "Snitch.Metacode.Decompilation", maxTypes: 3);

        Assert.Null(page.Error);
        Assert.Equal(3, page.Types.Count);
        Assert.True(page.Matched >= page.Types.Count, "the filter matches more types than the page shows");
        Assert.Empty(page.Errors);
        Assert.All(page.Types, type => Assert.StartsWith("Snitch.Metacode.Decompilation", type.FullName));

        // Each page entry is a real declaration with its own body — classes, records, whatever it is.
        Assert.All(page.Types, type => Assert.Contains(type.FullName[(type.FullName.LastIndexOf('.') + 1)..], type.Pseudocode));

        // A positional record carries its state in the parameter list, which the model does not list as
        // members yet; everything else on the page is a normal type and has to show its members.
        Assert.All(page.Types, type => Assert.True(
            type.Members > 0 || type.Pseudocode.Contains("RECORD", StringComparison.Ordinal),
            type.FullName + " has no members"));
        Assert.NotEmpty(page.Document.Modules);
    }

    [Fact]
    public void A_missing_assembly_is_reported_in_the_result_instead_of_thrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vmcp-missing-" + Guid.NewGuid().ToString("N") + ".dll");

        var result = new DecompileService().DecompileTypeToMetacode(missing, "Nope.Type");

        Assert.True(result.IsError);
        Assert.Contains("Assembly not found", result.Error);
        Assert.Empty(result.Types);
    }

    // ------------------------------------------------------------------ Ghidra → metacode

    [Fact]
    public void The_recorded_ghidra_snapshot_parses_into_its_functions()
    {
        var snapshot = GhidraSnapshot.Parse(GhidraSnapshotJson);

        Assert.Equal("version.dll", snapshot.Image);
        Assert.Equal("x86:LE:64:default", snapshot.Language);
        Assert.Equal("1800010f0", snapshot.Entry);
        Assert.Equal("180000000", snapshot.ImageBase);
        Assert.Equal(2, snapshot.Functions.Count);
        Assert.Equal("FUN_180001124", snapshot.Functions[0].Name);
        Assert.Equal(124, snapshot.Functions[0].Size);
        Assert.Equal(4, snapshot.Functions[0].Calls.Count);
        Assert.True(snapshot.Functions[1].HasBody);
    }

    [Fact]
    public void A_ghidra_function_becomes_metacode_with_its_control_flow()
    {
        var pseudocode = Render(GhidraDocument());

        // `undefined8`, `FUN_...` and the raw addresses survive as names; the structure is the model's.
        Assert.Contains("FUNCTION FUN_180001124(param_1 : undefined8, param_2 : int) : undefined8", pseudocode);
        Assert.Contains("IF param_2 == 0 THEN", pseudocode);
        Assert.Contains("IF DAT_180007628 != CAST(HMODULE, 0x0) THEN", pseudocode);
        Assert.Contains("TlsSetValue(DAT_180007090, CAST(LPVOID, 0x0))", pseudocode);
        Assert.Contains("RETURN 1", pseudocode);
        Assert.Contains("ELSE", pseudocode);
    }

    [Fact]
    public void A_ghidra_store_through_a_pointer_is_a_dereference_not_a_declaration()
    {
        var pseudocode = Render(GhidraDocument());
        var function = GhidraFunctionNamed(GhidraDocument(), "FUN_1800011b0");

        Assert.Contains("FUNCTION FUN_1800011b0() : bool", pseudocode);
        Assert.Contains("DECLARE bVar1 : bool", pseudocode);
        Assert.Contains("DAT_180007658 = malloc(0x100)", pseudocode);

        // `*DAT_180007658 = 0;` is an assignment whose target is a dereference.
        Assert.Contains("(*DAT_180007658) = 0", pseudocode);
        Assert.DoesNotContain("DECLARE DAT_180007658", pseudocode);
        Assert.Contains("RETURN NOT bVar1", pseudocode);

        var assignment = function.Body!.Statements
            .OfType<MetacodeIf>()
            .Single()
            .Then.Statements
            .OfType<MetacodeAssignment>()
            .Single();
        var dereference = Assert.IsType<MetacodeUnary>(assignment.Target);
        Assert.Equal(MetacodeUnaryOperator.Dereference, dereference.Operator);
    }

    [Fact]
    public void A_ghidra_function_keeps_the_binary_and_the_address_as_its_origin()
    {
        var snapshot = GhidraSnapshot.Parse(GhidraSnapshotJson) with { BinaryPath = GhidraBinary };
        var document = new GhidraFrontend().Translate(snapshot);

        var first = GhidraFunctionNamed(document, "FUN_180001124");
        Assert.Equal(MetacodeLanguage.Cpp, first.Origin.Language);
        Assert.Equal(GhidraBinary, first.Origin.Document);
        Assert.Equal(1, first.Origin.StartLine);
        Assert.Contains("FUN_180001124", first.Origin.Snippet);
        Assert.Equal(2, GhidraFunctionNamed(document, "FUN_1800011b0").Origin.StartLine);
    }

    [Fact]
    public void The_ghidra_document_reads_back_to_the_same_text()
    {
        var document = GhidraDocument();
        var first = Render(document);

        var reloaded = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(first, "ghidra.mc"));

        Assert.False(reloaded.HasErrors, string.Join("\n", reloaded.Diagnostics));
        Assert.Equal(first, Render(reloaded));
    }

    [Fact]
    public void Ghidra_c_with_a_comma_expression_is_kept_as_raw_with_a_warning()
    {
        // `while (p = f(p), *p)` has no node: C's comma operator is a sequence, and the model has no
        // sequence expression. The line must survive as RAW and say so, not be silently mangled.
        const string json = """
            {
             "image": "demo.dll", "language": "x86:LE:64:default", "entry": "1000", "imageBase": "10000000",
             "functions": [
              {
               "entry": "1000", "name": "FUN_1000", "signature": "void __fastcall FUN_1000(void)", "size": 8,
               "decompiled": "void FUN_1000(void)\n\n{\n  while (pVar = pVar + -1, *pVar) {\n    Use(*pVar);\n  }\n}\n",
               "calls": []
              }
             ]
            }
            """;

        var document = new GhidraFrontend().Translate(MetacodeSource.FromGhidra(json, "demo.json"));
        var pseudocode = Render(document);

        Assert.Contains("WHILE RAW[pseudo]", pseudocode);
        Assert.False(document.HasErrors, string.Join("\n", document.Diagnostics));
        Assert.Contains(document.Diagnostics, d => d.Code == MetacodeDiagnosticCodes.LossyLowering && d.Message.Contains("could not place"));
        Assert.Equal(pseudocode, Render(MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(pseudocode, "demo.mc"))));
    }

    [Fact]
    public void Ghidra_is_found_by_name_or_by_address()
    {
        var snapshot = GhidraSnapshot.Parse(GhidraSnapshotJson);

        Assert.Equal("FUN_1800011b0", snapshot.FindFunction("FUN_1800011b0")?.Name);
        Assert.Equal("FUN_1800011b0", snapshot.FindFunction("fun_1800011b0")?.Name);
        Assert.Equal("FUN_1800011b0", snapshot.FindFunction("0x1800011b0")?.Name);
        Assert.Equal("FUN_180001124", snapshot.FindFunction("180001124")?.Name);
        Assert.Null(snapshot.FindFunction("no-such-function"));
    }

    [Fact]
    public void A_json_that_is_not_a_ghidra_snapshot_is_its_own_diagnostic()
    {
        var document = new GhidraFrontend().Translate(MetacodeSource.FromGhidra("{ \"kind\": \"TranslationUnitDecl\" }", "x.json"));

        Assert.True(document.HasErrors);
        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(MetacodeDiagnosticCodes.ParseError, diagnostic.Code);
        Assert.Contains("not a Ghidra snapshot", diagnostic.Message);
    }

    // ------------------------------------------------------------------ the model addition

    [Fact]
    public void A_dereference_and_an_address_of_print_and_read_back()
    {
        const string source = """
            MODULE m
              FUNCTION f() : void
                *p = 1
                q = &value
                RETURN *(*p)
              END FUNCTION
            END MODULE
            """;

        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(source, "pointers.mc"));
        Assert.False(document.HasErrors, string.Join("\n", document.Diagnostics));

        var text = Render(document);
        Assert.Contains("(*p) = 1", text);
        Assert.Contains("q = &value", text);
        Assert.Contains("RETURN *(*p)", text);
        Assert.Equal(text, Render(MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(text, "pointers.mc"))));
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void AddSnitchMetacode_registers_both_decompiler_contracts()
    {
        var services = new ServiceCollection();
        services.AddSnitchMetacode();

        using var provider = services.BuildServiceProvider();
        var text = provider.GetRequiredService<IDecompileService>();
        var metacode = provider.GetRequiredService<IMetacodeDecompileService>();

        Assert.Same(text, metacode);
        Assert.IsType<DecompileService>(text);
        Assert.IsType<GhidraService>(provider.GetRequiredService<IGhidraService>());
    }
}
