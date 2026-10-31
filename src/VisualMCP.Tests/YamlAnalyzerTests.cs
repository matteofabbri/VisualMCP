using Snitch.Languages.Yaml;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// analyze_yaml: the indentation-aware line scan. One case that fires and one that must not, per rule,
/// plus the manifest identity the inventory reports.
/// </summary>
public sealed class YamlAnalyzerTests
{
    private static YamlAnalysis Analyze(string source) => YamlAnalyzer.AnalyzeText(source, "probe.yaml");

    private static string[] Rules(YamlAnalysis analysis) =>
        analysis.Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    [Fact]
    public void ACleanManifestHasNoFindingsAndItsIdentityIsRead()
    {
        var analysis = Analyze("""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: api
              labels:
                app: api
            spec:
              replicas: 2
              template:
                spec:
                  containers:
                    - name: api
                      image: example/api:1.0
            """);

        Assert.Empty(analysis.Findings);
        Assert.Equal("Deployment", analysis.Kind);
        Assert.Equal("apps/v1", analysis.ApiVersion);
        Assert.Equal("api", analysis.Name);
        Assert.Contains("spec", analysis.TopLevelKeys);
        Assert.Equal(1, analysis.Documents);
    }

    [Fact]
    public void ATabInTheIndentationIsAnError()
    {
        Assert.Contains("YAML0001", Rules(Analyze("key:\n\tchild: 1")));
        Assert.DoesNotContain("YAML0001", Rules(Analyze("key:\n  child: 1")));
    }

    [Fact]
    public void ADuplicateKeyIsFlagged()
    {
        Assert.Contains("YAML0002", Rules(Analyze("a: 1\na: 2")));
        Assert.DoesNotContain("YAML0002", Rules(Analyze("a: 1\nb: 2")));
        Assert.DoesNotContain("YAML0002", Rules(Analyze("root:\n  a: 1\nother:\n  a: 2")));
    }

    [Fact]
    public void AColonGluedToItsKeyIsFlagged()
    {
        Assert.Contains("YAML0005", Rules(Analyze("name:value: 1")));
        Assert.DoesNotContain("YAML0005", Rules(Analyze("name: value")));
    }

    [Fact]
    public void Yaml11BooleansAreFlagged()
    {
        Assert.Contains("YAML0004", Rules(Analyze("enabled: yes")));
        Assert.Contains("YAML0004", Rules(Analyze("enabled: off")));
        Assert.DoesNotContain("YAML0004", Rules(Analyze("enabled: true")));
        Assert.DoesNotContain("YAML0004", Rules(Analyze("enabled: \"yes\"")));
    }

    [Fact]
    public void AValueThatParsesAsANestedMappingIsFlagged()
    {
        Assert.Contains("YAML0006", Rules(Analyze("name: a: b")));
        Assert.DoesNotContain("YAML0006", Rules(Analyze("name: \"a: b\"")));
    }

    [Fact]
    public void AnUnbalancedFlowCollectionIsFlagged()
    {
        Assert.Contains("YAML0007", Rules(Analyze("list: [a, b")));
        Assert.DoesNotContain("YAML0007", Rules(Analyze("list: [a, b]")));
        Assert.DoesNotContain("YAML0007", Rules(Analyze("map: { a: 1 }")));
    }

    [Fact]
    public void AnAliasWithNoAnchorIsFlagged()
    {
        Assert.Contains("YAML0008", Rules(Analyze("copy: *defaults")));
        Assert.DoesNotContain("YAML0008", Rules(Analyze("base: &defaults 1\ncopy: *defaults")));
    }

    [Fact]
    public void ABlockScalarIsNotReadAsKeys()
    {
        var analysis = Analyze("""
            script: |
              a: 1
              a: 2
            next: 1
            """);

        Assert.DoesNotContain("YAML0002", Rules(analysis));
        Assert.Contains("script", analysis.TopLevelKeys);
        Assert.Contains("next", analysis.TopLevelKeys);
        Assert.DoesNotContain("a", analysis.TopLevelKeys);
    }

    [Fact]
    public void ACommentIsNotAKey()
    {
        Assert.DoesNotContain("YAML0002", Rules(Analyze("# a: 1\na: 2")));
        Assert.DoesNotContain("YAML0006", Rules(Analyze("a: 1 # note: not a key")));
    }

    [Fact]
    public void TrailingWhitespaceIsInfo()
    {
        Assert.Contains("YAML0003", Rules(Analyze("a: 1 \nb: 2")));
        Assert.DoesNotContain("YAML0003", Rules(Analyze("a: 1\nb: 2")));
    }

    [Fact]
    public async Task TheServiceScansTheTreeSkipsBuildOutputAndFiltersByRule()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new YamlService(workspace);

        var root = workspace.RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "deploy"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(root, "deploy", "api.yaml"), "kind: Deployment\nmetadata:\n  name: api");
        await File.WriteAllTextAsync(Path.Combine(root, "deploy", "bad.yaml"), "a: 1\na: 2");
        await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "ignored.yaml"), "a: 1\na: 2");

        var inventory = ResultAssert.Json(await service.InventoryAsync(root, 100));
        Assert.Equal(2, inventory.GetProperty("fileCount").GetInt32());

        var analysis = ResultAssert.Json(await service.AnalyzeAsync(root, "info", "YAML0002", 100, 100));
        Assert.Equal(1, analysis.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, analysis.GetProperty("filesAnalyzed").GetInt32());

        var unknown = ResultAssert.Error(await service.AnalyzeAsync(root, "info", "YAML9999", 100, 100));
        Assert.Contains("Unknown rule", unknown);
    }
}
