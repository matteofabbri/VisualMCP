using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Inspection administration: custom rules declared in the repository, the .DotSettings a team shares,
/// the spelling inspection, the .editorconfig naming policy, and the warm background analysis.
/// </summary>
public sealed class InspectionAdministrationTests
{
    // ── custom inspections ───────────────────────────────────────────────────
    [Fact]
    public void Custom_rules_parse_and_every_invalid_rule_is_explained()
    {
        const string json = """
            {
              "rules": [
                { "id": "VMC1001", "kind": "bannedSymbol", "value": "System.Console", "severity": "warning", "message": "Use ILogger." },
                { "id": "VMC1002", "kind": "identifierPattern", "value": "/^Class[0-9]+$/", "severity": "info" },

                { "id": "BAD1", "kind": "bannedSymbol", "value": "x" },
                { "id": "VMC1003", "kind": "notAKind", "value": "x" },
                { "id": "VMC1004", "kind": "bannedSymbol", "value": "x", "severity": "fatal" }
              ]
            }
            """;

        var rules = CustomInspectionService.Parse(json, out var errors);

        Assert.Equal(2, rules.Count);
        Assert.Equal("warning", rules[0].Severity);
        Assert.Equal("info", rules[1].Severity);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task Custom_rules_report_symbols_namespaces_identifiers_comments()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text;

            namespace Probe;

            internal class Class1
            {
                // TODO: replace this
                public void Run()
                {
                    System.Console.WriteLine(new StringBuilder());
                }
            }
            """));

        var path = Path.Combine(workspace.RootDirectory, ".visualMCP");
        Directory.CreateDirectory(path);
        var ruleFile = Path.Combine(path, "inspections.json");
        await File.WriteAllTextAsync(ruleFile, """
            { "rules": [
              { "id": "VMC1001", "kind": "bannedSymbol", "value": "System.Console", "severity": "warning", "message": "Use ILogger." },
              { "id": "VMC1002", "kind": "bannedNamespace", "value": "System.Text", "severity": "info", "message": "No text." },
              { "id": "VMC1003", "kind": "identifierPattern", "value": "/^Class[0-9]+$/", "severity": "info", "message": "Name the class." },
              { "id": "VMC1004", "kind": "commentPattern", "value": "TODO", "severity": "info", "message": "No TODO." }
            ] }
            """);

        var result = ResultAssert.Json(await new CustomInspectionService(workspace).RunAsync(null, null, ruleFile, "info", 100));

        Assert.Equal(4, result.GetProperty("ruleCount").GetInt32());
        var rules = result.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("rule").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("VMC1001", rules);
        Assert.Contains("VMC1002", rules);
        Assert.Contains("VMC1003", rules);
        Assert.Contains("VMC1004", rules);
    }

    // ── .DotSettings ────────────────────────────────────────────────────────
    [Fact]
    public void DotSettings_severities_disabled_ids_and_words_are_read()
    {
        var file = Path.Combine(Path.GetTempPath(), "vmcp-" + Guid.NewGuid().ToString("N") + ".DotSettings");
        File.WriteAllText(file, """
            <wpf:ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                    xmlns:s="clr-namespace:System;assembly=mscorlib"
                                    xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=InconsistentNaming/@EntryIndexedValue">WARNING</s:String>
              <s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=RedundantUsingDirective/@EntryIndexedValue">NONE</s:String>
              <s:String x:Key="/Default/Environment/Spelling/CustomDictionaryWords/=Probe/@EntryIndexedValue">Probe</s:String>
            </wpf:ResourceDictionary>
            """);

        try
        {
            var content = DotSettingsService.Parse([file]);

            Assert.Equal("WARNING", content.InspectionSeverities["InconsistentNaming"]);
            Assert.Contains("RedundantUsingDirective", content.DisabledInspections);
            Assert.Equal(["Probe"], content.DictionaryWords);
            Assert.Empty(content.Errors);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── spelling ────────────────────────────────────────────────────────────
    [Fact]
    public void One_edit_apart_covers_substitution_insertion_deletion_and_transposition()
    {
        Assert.True(SpellingService.IsOneEditApart("recieve", "receive"));
        Assert.True(SpellingService.IsOneEditApart("order", "orders"));
        Assert.True(SpellingService.IsOneEditApart("orders", "order"));
        Assert.True(SpellingService.IsOneEditApart("count", "counr"));
        Assert.False(SpellingService.IsOneEditApart("count", "count"));
        Assert.False(SpellingService.IsOneEditApart("count", "counter"));
        Assert.False(SpellingService.IsOneEditApart("alpha", "omega"));
    }

    [Fact]
    public void Identifiers_split_on_case_and_separators()
    {
        Assert.Equal(["Get", "Order", "Items"], SpellingService.SplitIdentifier("GetOrderItems"));
        Assert.Equal(["order", "items"], SpellingService.SplitIdentifier("order_items"));
        Assert.Equal(["HTTP", "Client"], SpellingService.SplitIdentifier("HTTPClient"));
    }

    [Fact]
    public async Task Spelling_flags_a_word_one_edit_from_the_solution_or_the_dictionary()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Calculator
            {
                public int Calculate(int value) => value;

                // recieve the result
                public int Calcualte(int value) => Calculate(value) + value;
            }
            """));

        var dictionary = Path.Combine(Path.GetTempPath(), "vmcp-" + Guid.NewGuid().ToString("N") + ".dic");
        await File.WriteAllTextAsync(dictionary, "receive\n");

        try
        {
            var result = ResultAssert.Json(
                await new SpellingService(workspace).FindAsync(null, null, dictionary, 50));

            var findings = result.GetProperty("findings").EnumerateArray().ToList();
            Assert.Contains(findings, finding =>
                finding.GetProperty("word").GetString()!.Equals("Calcualte", StringComparison.OrdinalIgnoreCase) &&
                finding.GetProperty("suggestion").GetString()!.Equals("Calculate", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(findings, finding =>
                finding.GetProperty("word").GetString()!.Equals("recieve", StringComparison.OrdinalIgnoreCase) &&
                finding.GetProperty("suggestion").GetString()!.Equals("receive", StringComparison.OrdinalIgnoreCase) &&
                finding.GetProperty("suggestionSource").GetString() == "dictionary");
        }
        finally
        {
            File.Delete(dictionary);
        }
    }

    // ── naming rules ────────────────────────────────────────────────────────
    [Fact]
    public void Naming_rules_build_from_editorconfig_keys_and_compute_the_expected_name()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet_naming_rule.private_fields.severity"] = "suggestion",
            ["dotnet_naming_rule.private_fields.symbols"] = "private_fields",
            ["dotnet_naming_rule.private_fields.style"] = "underscore_camel",
            ["dotnet_naming_symbols.private_fields.applicable_kinds"] = "field",
            ["dotnet_naming_symbols.private_fields.applicable_accessibilities"] = "private",
            ["dotnet_naming_style.underscore_camel.capitalization"] = "camel_case",
            ["dotnet_naming_style.underscore_camel.required_prefix"] = "_",
        };

        var rule = Assert.Single(NamingRulesService.BuildRules(settings));
        Assert.Equal(["_orderCount"], new[] { NamingRulesService.ExpectedName(rule, "OrderCount") });
        Assert.Equal("_value", NamingRulesService.ExpectedName(rule, "Value"));
        Assert.Equal(["Order", "Count"], NamingRulesService.Words("OrderCount"));
    }

    [Fact]
    public async Task Naming_violations_are_listed_and_can_be_renamed()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig("""
            [*.cs]
            dotnet_naming_rule.private_fields.severity = warning
            dotnet_naming_rule.private_fields.symbols = private_fields
            dotnet_naming_rule.private_fields.style = underscore_camel
            dotnet_naming_symbols.private_fields.applicable_kinds = field
            dotnet_naming_symbols.private_fields.applicable_accessibilities = private
            dotnet_naming_style.underscore_camel.capitalization = camel_case
            dotnet_naming_style.underscore_camel.required_prefix = _
            """, ("C.cs", """
            namespace Probe;

            internal class C
            {
                private int Count;

                public int Read() => Count;
            }
            """));

        var service = new NamingRulesService(workspace, new ApplyRenameService(workspace));

        var listed = ResultAssert.Json(await service.RunAsync());
        Assert.Equal(1, listed.GetProperty("violationCount").GetInt32());
        var violation = listed.GetProperty("violations")[0];
        Assert.Equal("Count", violation.GetProperty("current").GetString());
        Assert.Equal("_count", violation.GetProperty("expected").GetString());

        var applied = ResultAssert.Json(await service.RunAsync(apply: true));
        Assert.Equal(1, applied.GetProperty("applied").GetInt32());

        var text = workspace.Read("C.cs");
        Assert.Contains("private int _count;", text);
        Assert.Contains("=> _count;", text);
    }

    // ── background analysis ─────────────────────────────────────────────────
    [Fact]
    public async Task A_warm_run_caches_findings_and_analysis_findings_serves_them()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        var inspections = new StubInspections();
        var service = new BackgroundAnalysisService(workspace, inspections);

        var refresh = ResultAssert.Json(await service.RefreshAsync());
        Assert.True(refresh.GetProperty("ran").GetBoolean());
        Assert.Equal(2, refresh.GetProperty("findings").GetInt32());

        var status = ResultAssert.Json(service.Status());
        Assert.False(status.GetProperty("running").GetBoolean());
        Assert.Equal(2, status.GetProperty("findings").GetInt32());
        Assert.NotNull(status.GetProperty("lastRunUtc").GetString());

        var all = ResultAssert.Json(service.Findings(null, null, "info", 10));
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var errorsOnly = ResultAssert.Json(service.Findings(null, null, "error", 10));
        Assert.Equal(1, errorsOnly.GetProperty("total").GetInt32());
    }

    private sealed class StubInspections : IInspectCodeService
    {
        public Task<InspectionReport> RunAsync(
            string? projectName, string? filePath, string packs, string minSeverity, string? ids,
            string? excludeIds, bool enableAll, int maxResults, int timeoutSeconds)
        {
            var report = new InspectionReport(
                "ok", "Probe.sln", 5, 2, 2, false,
                [
                    new InspectionFinding("RCS1001", "WARNING", "Add braces", "C.cs", 3, 1, "Probe", "roslynator", "Readability"),
                    new InspectionFinding("CA2007", "ERROR", "Await the task", "C.cs", 7, 1, "Probe", "netanalyzers", "Reliability"),
                ],
                new Dictionary<string, int> { ["WARNING"] = 1, ["ERROR"] = 1 },
                new Dictionary<string, int> { ["RCS1001"] = 1, ["CA2007"] = 1 },
                new Dictionary<string, int> { ["roslynator"] = 1, ["netanalyzers"] = 1 },
                ["house", "roslynator", "netanalyzers", "asyncfixer"],
                []);

            return Task.FromResult(report);
        }
    }
}
