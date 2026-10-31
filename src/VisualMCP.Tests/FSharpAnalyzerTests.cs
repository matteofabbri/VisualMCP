using Snitch.Languages.FSharp;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// analyze_fsharp: the scanner over an indentation-sensitive language. One case that fires and one that
/// must not per rule, plus the declaration inventory.
/// </summary>
public sealed class FSharpAnalyzerTests
{
    private static FSharpAnalysis Analyze(string source) => FSharpAnalyzer.AnalyzeText(source, "probe.fs");

    private static string[] Rules(FSharpAnalysis analysis) =>
        analysis.Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    [Fact]
    public void ACleanFileHasNoFindingsAndItsDeclarationsAreRead()
    {
        var analysis = Analyze("""
            module Probe.Domain

            open System

            type Order = { Id: int; Total: decimal }

            let total order = order.Total

            let describe order =
                match order.Total with
                | value when value > 0m -> "positive"
                | _ -> "zero"
            """);

        Assert.Empty(analysis.Findings);
        Assert.Contains("Probe.Domain", analysis.Modules);
        Assert.Contains("Order", analysis.Types);
        Assert.Contains("total", analysis.Functions);
        Assert.Contains("describe", analysis.Functions);
        Assert.Contains("System", analysis.Opens);
    }

    [Fact]
    public void ATabInTheIndentationIsAnError()
    {
        Assert.Contains("FS0001", Rules(Analyze("module M\n\tlet x = 1")));
        Assert.DoesNotContain("FS0001", Rules(Analyze("module M\n    let x = 1")));
    }

    [Fact]
    public void PartialOptionAndListAccessesAreFlagged()
    {
        Assert.Contains("FS0002", Rules(Analyze("let v = Option.get maybe")));
        Assert.Contains("FS0002", Rules(Analyze("let v = Option.Value maybe")));
        Assert.DoesNotContain("FS0002", Rules(Analyze("let v = Option.defaultValue 0 maybe")));

        Assert.Contains("FS0003", Rules(Analyze("let first = List.head items")));
        Assert.Contains("FS0003", Rules(Analyze("let first = Seq.head items")));
        Assert.DoesNotContain("FS0003", Rules(Analyze("let first = List.tryHead items")));
    }

    [Fact]
    public void UncheckedCastsAreFlagged()
    {
        Assert.Contains("FS0004", Rules(Analyze("let x = Unchecked.defaultof<int>")));
        Assert.Contains("FS0004", Rules(Analyze("let x = unbox<float> value")));
        Assert.DoesNotContain("FS0004", Rules(Analyze("let x = box 1")));
    }

    [Fact]
    public void NullComparisonIsFlagged()
    {
        Assert.Contains("FS0005", Rules(Analyze("if value = null then 0 else 1")));
        Assert.Contains("FS0005", Rules(Analyze("if value <> null then 0 else 1")));
        Assert.DoesNotContain("FS0005", Rules(Analyze("if isNull value then 0 else 1")));
    }

    [Fact]
    public void ACatchAllAndMutableAndPrintsAreFlagged()
    {
        Assert.Contains("FS0006", Rules(Analyze("""
            let run () =
                try
                    work ()
                with _ -> ()
            """)));

        Assert.Contains("FS0007", Rules(Analyze("let mutable count = 0")));
        Assert.Contains("FS0008", Rules(Analyze("""printfn "done" """)));
        Assert.Contains("FS0009", Rules(Analyze("""failwith "boom" """)));
    }

    [Fact]
    public void ADuplicateOpenIsFlagged()
    {
        Assert.Contains("FS0010", Rules(Analyze("open System\nopen System")));
        Assert.DoesNotContain("FS0010", Rules(Analyze("open System\nopen System.IO")));
    }

    [Fact]
    public void AStringOrACommentDoesNotFire()
    {
        Assert.DoesNotContain("FS0002", Rules(Analyze("""let text = "Option.get maybe" """)));
        Assert.DoesNotContain("FS0007", Rules(Analyze("// let mutable count = 0")));
        Assert.DoesNotContain("FS0009", Rules(Analyze("""let text = "failwith" """)));
    }

    [Fact]
    public void ABlockCommentIsMaskedAcrossLines()
    {
        var analysis = Analyze("""
            (*
            let mutable count = 0
            *)
            let x = 1
            """);

        Assert.DoesNotContain("FS0007", Rules(analysis));
        Assert.DoesNotContain("count", analysis.Functions);
        Assert.Contains("x", analysis.Functions);
    }

    [Fact]
    public async Task TheServiceScansTheTreeSkipsBuildOutputAndFiltersByRule()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new FSharpService(workspace);

        var root = workspace.RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "a.fs"), "module A\nlet x = 1");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "b.fs"), "module B\nlet y = Option.get maybe");
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "ignored.fs"), "let z = Option.get never");

        var inventory = ResultAssert.Json(await service.InventoryAsync(root, 100));
        Assert.Equal(2, inventory.GetProperty("fileCount").GetInt32());

        var analysis = ResultAssert.Json(await service.AnalyzeAsync(root, "info", "FS0002", 100, 100));
        Assert.Equal(1, analysis.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, analysis.GetProperty("filesAnalyzed").GetInt32());

        var unknown = ResultAssert.Error(await service.AnalyzeAsync(root, "info", "FS9999", 100, 100));
        Assert.Contains("Unknown rule", unknown);
    }
}
