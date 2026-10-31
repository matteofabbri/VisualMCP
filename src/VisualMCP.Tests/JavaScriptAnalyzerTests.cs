using Snitch.Languages.JavaScript;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// analyze_js: one rule, one case that fires and one that must not. The scanner masks comments and
/// literals first, which is what keeps a keyword inside them from firing a rule.
/// </summary>
public sealed class JavaScriptAnalyzerTests
{
    private static JsAnalysis Analyze(string source, string file = "probe.ts") =>
        JavaScriptAnalyzer.AnalyzeText(source, file);

    private static string[] Rules(JsAnalysis analysis) =>
        analysis.Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    [Fact]
    public void ACleanFileHasNoFindingsAndItsModulesAreRead()
    {
        var analysis = Analyze("""
            import { useState } from "react";
            import "./styles.css";

            export class Cart {
                total(items) {
                    return items.reduce((sum, item) => sum + item.price, 0);
                }
            }

            export function format(value) {
                return String(value);
            }
            """);

        Assert.Empty(analysis.Findings);
        Assert.Contains("react", analysis.Imports);
        Assert.Contains("./styles.css", analysis.Imports);
        Assert.Contains("Cart", analysis.Exports);
        Assert.Contains("format", analysis.Exports);
        Assert.Equal(1, analysis.Classes);
        Assert.True(analysis.TypeScript);
    }

    [Fact]
    public void DynamicCodeIsFlagged()
    {
        Assert.Contains("JS0002", Rules(Analyze("""eval("2 + 2");""")));
        Assert.Contains("JS0003", Rules(Analyze("""const f = new Function("return 1");""")));
        Assert.Contains("JS0016", Rules(Analyze("""setTimeout("run()", 10);""")));
        Assert.DoesNotContain("JS0016", Rules(Analyze("setTimeout(() => run(), 10);")));
    }

    [Fact]
    public void InnerHtmlIsFlaggedOnlyWhenTheValueIsNotALiteral()
    {
        Assert.Contains("JS0004", Rules(Analyze("element.innerHTML = value;")));
        Assert.DoesNotContain("JS0004", Rules(Analyze("""element.innerHTML = "<b>fixed</b>";""")));
        Assert.Contains("JS0005", Rules(Analyze("return <div dangerouslySetInnerHTML={{ __html: value }} />;", "probe.tsx")));
        Assert.Contains("JS0006", Rules(Analyze("""document.write("<h1>hi</h1>");""")));
    }

    [Fact]
    public void LooseEqualityIsFlaggedButStrictIsNot()
    {
        Assert.Contains("JS0007", Rules(Analyze("if (a == b) { run(); }")));
        Assert.Contains("JS0007", Rules(Analyze("if (a != b) { run(); }")));
        Assert.DoesNotContain("JS0007", Rules(Analyze("if (a === b) { run(); }")));
        Assert.DoesNotContain("JS0007", Rules(Analyze("if (a !== b) { run(); }")));
    }

    [Fact]
    public void LeftoversAreFlagged()
    {
        Assert.Contains("JS0008", Rules(Analyze("var count = 0;")));
        Assert.Contains("JS0009", Rules(Analyze("""console.log("debug");""")));
        Assert.Contains("JS0010", Rules(Analyze("debugger;")));
        Assert.Contains("JS0011", Rules(Analyze("try { run(); } catch (e) { }")));
        Assert.Contains("JS0012", Rules(Analyze("// @ts-ignore\nrun();")));
    }

    [Fact]
    public void AKeywordInsideAStringOrACommentDoesNotFire()
    {
        Assert.DoesNotContain("JS0002", Rules(Analyze("""const text = "eval(document.cookie)";""")));
        Assert.DoesNotContain("JS0002", Rules(Analyze("""// eval(userInput)""")));
        Assert.DoesNotContain("JS0007", Rules(Analyze("const text = 'a == b';")));
    }

    [Fact]
    public void ANewTabLinkWithoutNoopenerIsFlagged()
    {
        Assert.Contains("JS0013", Rules(Analyze("""const link = <a href="https://x" target="_blank">x</a>;""", "probe.tsx")));
        Assert.DoesNotContain("JS0013", Rules(Analyze(
            """const link = <a href="https://x" target="_blank" rel="noopener noreferrer">x</a>;""", "probe.tsx")));
        Assert.DoesNotContain("JS0013", Rules(Analyze("""const link = <a href="https://x" target="_self">x</a>;""", "probe.tsx")));
    }

    [Fact]
    public void AnImageWithoutAltIsFlagged()
    {
        Assert.Contains("JS0014", Rules(Analyze("""const image = <img src="a.png" />;""", "probe.tsx")));
        Assert.DoesNotContain("JS0014", Rules(Analyze("""const image = <img src="a.png" alt="A" />;""", "probe.tsx")));
    }

    [Fact]
    public void AnyIsOnlyFlaggedInTypeScript()
    {
        Assert.Contains("JS0015", Rules(Analyze("function f(value: any) { return value; }", "probe.ts")));
        Assert.DoesNotContain("JS0015", Rules(Analyze("function f(value) { return value; }", "probe.js")));
    }

    [Fact]
    public void JsxIsDetectedFromAnElementNotFromAGeneric()
    {
        Assert.True(Analyze("""const view = <Panel title="x" />;""", "probe.tsx").Jsx);
        Assert.False(Analyze("const map = new Map<string, number>();", "probe.ts").Jsx);
    }

    [Fact]
    public async Task TheServiceScansTheTreeSkipsNodeModulesAndFiltersByRule()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new JavaScriptService(workspace);

        var root = workspace.RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "a.ts"), "var x = 1;");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "b.ts"), "eval(input);");
        await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "ignored.js"), "eval(input);");

        var inventory = ResultAssert.Json(await service.InventoryAsync(root, 100));
        Assert.Equal(2, inventory.GetProperty("fileCount").GetInt32());

        var analysis = ResultAssert.Json(await service.AnalyzeAsync(root, "info", "JS0002", 100, 100));
        Assert.Equal(1, analysis.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, analysis.GetProperty("filesAnalyzed").GetInt32());

        var unknown = ResultAssert.Error(await service.AnalyzeAsync(root, "info", "JS9999", 100, 100));
        Assert.Contains("Unknown rule", unknown);
    }
}
