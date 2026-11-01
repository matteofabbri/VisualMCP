using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Languages.CSharp.Inspections.Rider;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The ReSharper-shaped pack: each native analyzer under ReSharper's own id, and the curated coverage map
/// that says which ids map where — the honest half of "the proprietary catalogue".
/// </summary>
public sealed class RiderInspectionTests
{
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source, DiagnosticAnalyzer analyzer)
    {
        using var workspace = FakeWorkspace.Create(("C.cs", source));
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var compilation = await solution!.Projects.Single().GetCompilationAsync();

        var analyzers = compilation!.WithAnalyzers(ImmutableArray.Create(analyzer));
        return await analyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>The text after the pack's own code fix has been applied to the single diagnostic.</summary>
    private static async Task<string> ApplyFixAsync(string source, DiagnosticAnalyzer analyzer)
    {
        using var workspace = FakeWorkspace.Create(("C.cs", source));
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var document = solution!.Projects.Single().Documents.Single();

        var compilation = await document.Project.GetCompilationAsync();
        var diagnostic = (await compilation!.WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync()).Single();

        var actions = new List<CodeAction>();
        await new RiderCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
            document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var applied = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        return (await applied.GetDocument(document.Id)!.GetTextAsync()).ToString();
    }

    [Fact]
    public async Task ConvertToConstant_reports_a_local_assignedOnceWithAConstant()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    int limit = 10;
                    return limit;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ConvertToConstantLocalAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ConvertToConstant_Local", diagnostic.Id);
    }

    [Fact]
    public async Task ConvertToConstant_ignores_a_local_that_is_written_later()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    int limit = 10;
                    limit++;
                    return limit;
                }
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new ConvertToConstantLocalAnalyzer()));
    }

    [Fact]
    public async Task LoopVariableChanged_reports_a_write_inside_the_body()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    var total = 0;
                    for (var i = 0; i < 10; i++)
                    {
                        i = i + 2;
                        total += i;
                    }

                    return total;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new LoopVariableChangedAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "LoopVariableIsNeverChangedInsideLoop");
    }

    [Fact]
    public async Task MemberCanBeMadeStatic_reports_a_method_without_instance_state()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                private int _value;

                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int WithState()
                {
                    return _value;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new MemberCanBeMadeStaticAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MemberCanBeMadeStatic_Global", diagnostic.Id);
        Assert.Contains("Add", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RedundantArgument_reports_the_value_equal_to_the_default()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M() => Add(1, 5);

                private int Add(int value, int step = 5) => value + step;
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantArgumentAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantArgument");
    }

    [Fact]
    public async Task EmptyConstructor_reports_the_only_public_parameterless_one()
    {
        const string source = """
            namespace Probe;

            public class C
            {
                public C()
                {
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new EmptyConstructorAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "EmptyConstructor");
    }

    [Fact]
    public async Task RedundantIfElse_reports_opposite_boolean_returns()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool IsPositive(int value)
                {
                    if (value > 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantIfElseAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantIfElseBlock");
    }

    [Fact]
    public async Task ConstantCondition_reports_a_computed_constant_but_not_a_literal()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public void M()
                {
                    if (1 == 1) { }

                    while (true) { break; }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ConstantConditionAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ConditionIsAlwaysTrueOrFalse", diagnostic.Id);
    }

    [Fact]
    public async Task RedundantBaseConstructorCall_reports_an_explicit_parameterless_base()
    {
        const string source = """
            namespace Probe;

            internal class B
            {
            }

            internal class C : B
            {
                public C() : base()
                {
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantBaseConstructorCallAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantBaseConstructorCall");
    }

    [Fact]
    public async Task RedundantStringInterpolation_reports_an_interpolated_string_with_no_holes()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public string M() => $"hello";
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantStringInterpolationAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RedundantStringInterpolation", diagnostic.Id);
    }

    [Fact]
    public async Task RedundantStringInterpolation_ignores_a_string_with_an_interpolation()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public string M(string name) => $"hello {name}";
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new RedundantStringInterpolationAnalyzer()));
    }

    [Fact]
    public async Task MethodOverloadWithOptionalParameter_reports_the_parameter_the_shorter_overload_hides()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public void M(int value) { }

                public void M(int value, int step = 5) { }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new MethodOverloadWithOptionalParameterAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MethodOverloadWithOptionalParameter", diagnostic.Id);
        Assert.Contains("step", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReplaceSequenceEqualWithConstantPattern_reports_a_char_span_compared_to_a_literal()
    {
        const string source = """
            using System;

            namespace Probe;

            internal class C
            {
                public bool M(ReadOnlySpan<char> value) => value.SequenceEqual("abc");
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ReplaceSequenceEqualWithConstantPatternAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ReplaceSequenceEqualWithConstantPattern", diagnostic.Id);
    }

    [Fact]
    public async Task RedundantPatternParentheses_reports_parentheses_precedence_does_not_need()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(object value) => value is (int and > 5);
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantPatternParenthesesAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RedundantPatternParentheses", diagnostic.Id);
    }

    [Fact]
    public async Task RedundantPatternParentheses_keeps_the_parentheses_an_and_needs_around_an_or()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(object value) => value is (int or string) and not null;
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new RedundantPatternParenthesesAnalyzer()));
    }

    [Fact]
    public async Task MergeIntoPattern_reports_two_checks_on_the_same_value()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(int value) => value is > 0 && value is < 10;
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new MergeIntoPatternAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MergeIntoPattern", diagnostic.Id);
    }

    [Fact]
    public async Task MergeIntoPattern_ignores_checks_on_different_values()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(int value, int other) => value is > 0 && other is < 10;
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new MergeIntoPatternAnalyzer()));
    }

    [Fact]
    public async Task ReplaceWithStringIsNullOrEmpty_reports_both_directions()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool Empty(string value) => value == null || value.Length == 0;

                public bool Filled(string value) => value != null && value.Length > 0;
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ReplaceWithStringIsNullOrEmptyAnalyzer());

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ReplaceWithStringIsNullOrEmpty", diagnostic.Id));
    }

    [Fact]
    public async Task ReplaceWithStringIsNullOrEmpty_ignores_a_non_string_operand()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool Empty(int[] values) => values == null || values.Length == 0;
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new ReplaceWithStringIsNullOrEmptyAnalyzer()));
    }

    [Fact]
    public async Task MergeIntoPattern_fix_writes_the_and_pattern()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(int value) => value is > 0 && value is < 10;
            }
            """;

        var text = await ApplyFixAsync(source, new MergeIntoPatternAnalyzer());

        Assert.Contains("value is > 0 and < 10", text);
        Assert.DoesNotContain("&&", text);
    }

    [Fact]
    public async Task ReplaceWithStringIsNullOrEmpty_fix_writes_the_call_and_its_negation()
    {
        const string positive = """
            namespace Probe;

            internal class C
            {
                public bool M(string value) => value == null || value.Length == 0;
            }
            """;

        var text = await ApplyFixAsync(positive, new ReplaceWithStringIsNullOrEmptyAnalyzer());
        Assert.Contains("string.IsNullOrEmpty(value)", text);

        const string negative = """
            namespace Probe;

            internal class C
            {
                public bool M(string value) => value != null && value.Length > 0;
            }
            """;

        var negated = await ApplyFixAsync(negative, new ReplaceWithStringIsNullOrEmptyAnalyzer());
        Assert.Contains("!string.IsNullOrEmpty(value)", negated);
    }

    [Fact]
    public async Task RedundantStringInterpolation_fix_writes_a_plain_literal()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public string M() => $"hello";
            }
            """;

        var text = await ApplyFixAsync(source, new RedundantStringInterpolationAnalyzer());

        Assert.Contains("=> \"hello\";", text);
        Assert.DoesNotContain("$\"hello\"", text);
    }

    [Fact]
    public async Task ReplaceSequenceEqualWithConstantPattern_fix_writes_the_pattern()
    {
        const string source = """
            using System;

            namespace Probe;

            internal class C
            {
                public bool M(ReadOnlySpan<char> value) => value.SequenceEqual("abc");
            }
            """;

        var text = await ApplyFixAsync(source, new ReplaceSequenceEqualWithConstantPatternAnalyzer());

        Assert.Contains("value is \"abc\"", text);
        Assert.DoesNotContain("SequenceEqual", text);
    }

    // ── coverage map ────────────────────────────────────────────────────────
    [Fact]
    public async Task The_rider_pack_reports_the_ported_ids_through_inspect_code()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System;

            namespace Probe;

            public class C
            {
                public string Interpolation() => $"hello";

                public void Overload(int value)
                {
                }

                public void Overload(int value, int step = 5)
                {
                }

                public bool Compare(ReadOnlySpan<char> value) => value.SequenceEqual("abc");

                public bool Pattern(object value) => value is (int and > 5);

                public bool Merged(int value) => value is > 0 && value is < 10;

                public bool Empty(string value) => value == null || value.Length == 0;
            }
            """));

        var report = await new InspectCodeService(workspace)
            .RunAsync(null, null, "rider", "info", null, null, enableAll: true, maxResults: 200, timeoutSeconds: 60);

        var ids = report.Findings.Select(finding => finding.InspectionId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("RedundantStringInterpolation", ids);
        Assert.Contains("MethodOverloadWithOptionalParameter", ids);
        Assert.Contains("ReplaceSequenceEqualWithConstantPattern", ids);
        Assert.Contains("RedundantPatternParentheses", ids);
        Assert.Contains("MergeIntoPattern", ids);
        Assert.Contains("ReplaceWithStringIsNullOrEmpty", ids);
    }

    [Fact]
    public async Task The_rider_pack_is_selectable_through_inspect_code()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            public class C
            {
                public C()
                {
                }
            }
            """));

        var report = await new InspectCodeService(workspace)
            .RunAsync(null, null, "rider", "info", null, null, enableAll: true, maxResults: 100, timeoutSeconds: 60);

        Assert.Contains(report.Findings, finding => finding.InspectionId == "EmptyConstructor");
        Assert.Equal("rider", Assert.Single(report.Packs));
    }

    [Fact]
    public async Task The_map_maps_the_native_ids_and_names_the_absent_ones()
    {
        var result = ResultAssert.Json(await new ResharperInspectionMapService().ReportAsync(includeInstall: false));

        Assert.True(result.GetProperty("curatedCount").GetInt32() >= 40);
        Assert.Equal(19, result.GetProperty("byKind").GetProperty("rider").GetInt32());
        Assert.True(result.GetProperty("byKind").GetProperty("none").GetInt32() > 0);

        var uncovered = result.GetProperty("uncovered").EnumerateArray().Select(entry => entry.GetString()).ToList();
        Assert.DoesNotContain("UseNameofExpression", uncovered);
        Assert.Contains("MemberCanBePrivate.Global", uncovered);

        var riderEntries = result.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "rider")
            .Select(entry => entry.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("ConvertToConstant.Local", riderEntries);
        Assert.Contains("LoopVariableIsNeverChangedInsideLoop", riderEntries);
        Assert.Contains("UseNameofExpression", riderEntries);
        Assert.Contains("ConvertToCompoundAssignment", riderEntries);
        Assert.Contains("RedundantLambdaSignatureParentheses", riderEntries);
        Assert.Contains("StringLiteralAsInterpolationArgument", riderEntries);
        Assert.Contains("RedundantCheckBeforeAssignment", riderEntries);
        Assert.Contains("RedundantStringInterpolation", riderEntries);
        Assert.Contains("MethodOverloadWithOptionalParameter", riderEntries);
        Assert.Contains("ReplaceSequenceEqualWithConstantPattern", riderEntries);
        Assert.Contains("RedundantPatternParentheses", riderEntries);
        Assert.Contains("MergeIntoPattern", riderEntries);
        Assert.Contains("ReplaceWithStringIsNullOrEmpty", riderEntries);
    }

    [Fact]
    public async Task A_missing_riderInstallation_is_reported_not_guessed()
    {
        var result = ResultAssert.Json(await new ResharperInspectionMapService().ReportAsync(
            riderPath: Path.Combine(Path.GetTempPath(), "vmcp-no-rider-" + Guid.NewGuid().ToString("N")),
            includeInstall: true));

        Assert.False(result.GetProperty("install").GetProperty("found").GetBoolean());
    }
}
