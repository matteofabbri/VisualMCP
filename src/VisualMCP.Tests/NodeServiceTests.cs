using Snitch.Execution;
using Snitch.Node;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// node_*: the project discovery and the three output shapes that are worth interpreting
/// (tsc, eslint, the package managers' outdated/audit JSON) plus the guards, against temp projects.
/// </summary>
public sealed class NodeServiceTests
{
    private readonly NodeService _service = new();

    [Fact]
    public void TheNearestPackageJsonAboveTheDirectoryWins()
    {
        using var project = new TempProject();
        project.Write("package.json", """{ "name": "probe" }""");
        var nested = Path.Combine(project.Root, "src", "features");
        Directory.CreateDirectory(nested);

        Assert.Equal(project.Root, NodeService.FindProjectDirectory(nested));
        Assert.Equal(project.Root, NodeService.FindProjectDirectory(project.Root));
    }

    [Fact]
    public void AFilePathResolvesThroughItsDirectory()
    {
        using var project = new TempProject();
        project.Write("package.json", """{ "name": "probe" }""");
        var file = project.Root + Path.DirectorySeparatorChar + "package.json";

        Assert.Equal(project.Root, NodeService.FindProjectDirectory(file));
    }

    [Theory]
    [InlineData("pnpm@9.1.0", null, null, null, "pnpm")]
    [InlineData(null, "pnpm-lock.yaml", null, null, "pnpm")]
    [InlineData(null, null, "yarn.lock", null, "yarn")]
    [InlineData(null, null, null, "package-lock.json", "npm")]
    [InlineData(null, null, null, null, "npm")]
    [InlineData("npm@10.2.0", "yarn.lock", null, null, "npm")]
    public void ThePackageManagerComesFromTheFieldThenTheLockfile(
        string? field, string? pnpmLock, string? yarnLock, string? npmLock, string expected)
    {
        using var project = new TempProject();
        var json = field is null ? """{ "name": "probe" }""" : $$"""{ "name": "probe", "packageManager": "{{field}}" }""";
        project.Write("package.json", json);
        if (pnpmLock is not null) project.Write(pnpmLock, "");
        if (yarnLock is not null) project.Write(yarnLock, "");
        if (npmLock is not null) project.Write(npmLock, "");

        Assert.Equal(expected, NodeService.DetectPackageManager(project.Root));
    }

    [Fact]
    public void TscDiagnosticsAreParsedWithTheirPositionAndCode()
    {
        const string output = """
            src/app.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.
            src/app.ts(20,1): warning TS9999: A warning.
            Found 2 errors in the same file, starting at: src/app.ts:12
            """;

        var diagnostics = NodeService.ParseTscOutput(output);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("src/app.ts", diagnostics[0].File);
        Assert.Equal(12, diagnostics[0].Line);
        Assert.Equal(5, diagnostics[0].Column);
        Assert.Equal("error", diagnostics[0].Severity);
        Assert.Equal("TS2322", diagnostics[0].Code);
        Assert.Contains("not assignable", diagnostics[0].Message);
        Assert.Equal("warning", diagnostics[1].Severity);
    }

    [Fact]
    public void EslintJsonIsParsedPerMessageAndSeverity()
    {
        const string json = """
            [
              {
                "filePath": "/proj/src/a.ts",
                "messages": [
                  { "ruleId": "no-unused-vars", "severity": 2, "message": "'x' is assigned a value but never used.", "line": 3, "column": 7 },
                  { "ruleId": null, "severity": 1, "message": "Definition for rule 'x' was not found.", "line": 1, "column": 1 }
                ]
              }
            ]
            """;

        var findings = NodeService.ParseEslintJson(json);

        Assert.Equal(2, findings.Count);
        Assert.EndsWith("a.ts", findings[0].File);
        Assert.Equal("no-unused-vars", findings[0].RuleId);
        Assert.Equal("error", findings[0].Severity);
        Assert.Equal(3, findings[0].Line);
        Assert.Null(findings[1].RuleId);
        Assert.Equal("warning", findings[1].Severity);
    }

    [Fact]
    public void NpmsOutdatedMapIsNormalised()
    {
        var packages = NodeService.ParseOutdatedJson(
            """{ "lodash": { "current": "4.17.20", "wanted": "4.17.21", "latest": "4.17.21", "dependent": "my-app" } }""",
            "npm");

        var package = Assert.Single(packages);
        Assert.Equal("lodash", package.Name);
        Assert.Equal("4.17.20", package.Current);
        Assert.Equal("4.17.21", package.Wanted);
        Assert.Equal("4.17.21", package.Latest);
    }

    [Fact]
    public void PnpsOutdatedArrayIsNormalised()
    {
        var packages = NodeService.ParseOutdatedJson(
            """[ { "packageName": "react", "current": "17.0.0", "wanted": "18.2.0", "latest": "18.2.0" } ]""",
            "pnpm");

        var package = Assert.Single(packages);
        Assert.Equal("react", package.Name);
        Assert.Equal("18.2.0", package.Wanted);
    }

    [Fact]
    public void YarnsOutdatedTableIsReadThroughItsHead()
    {
        const string ndjson = """
            {"type":"warning","data":"package.json: No license field"}
            {"type":"table","data":{"head":["Package","Current","Wanted","Latest","Package Type","URL"],"body":[["lodash","4.17.20","4.17.21","4.17.21","dependencies","x"],["react","17.0.0","17.0.0","18.2.0","devDependencies","y"]]}}
            """;

        var packages = NodeService.ParseOutdatedJson(ndjson, "yarn");

        Assert.Equal(2, packages.Count);
        Assert.Equal("lodash", packages[0].Name);
        Assert.Equal("dependencies", packages[0].Type);
        Assert.Equal("react", packages[1].Name);
    }

    [Fact]
    public void NpmsAuditIsCountedAndExplained()
    {
        const string json = """
            {
              "metadata": { "vulnerabilities": { "info": 0, "low": 1, "moderate": 0, "high": 2, "critical": 0, "total": 3 } },
              "vulnerabilities": {
                "lodash": {
                  "severity": "high",
                  "via": [ { "title": "Prototype pollution" } ],
                  "range": "<4.17.21",
                  "fixAvailable": true
                },
                "minimist": {
                  "severity": "low",
                  "via": [ { "title": "Prototype pollution" } ],
                  "range": "<1.2.6",
                  "fixAvailable": { "name": "minimist", "version": "1.2.6", "isSemVerMajor": false }
                }
              }
            }
            """;

        var audit = NodeService.ParseAuditJson(json, "npm");

        Assert.Equal(2, audit.Counts.High);
        Assert.Equal(1, audit.Counts.Low);
        Assert.Equal(3, audit.Counts.Total);
        Assert.Equal(2, audit.Packages.Count);
        Assert.Equal("lodash", audit.Packages[0].Name);
        Assert.Equal("yes", audit.Packages[0].Fix);
        Assert.Equal("Prototype pollution", audit.Packages[0].Title);
        Assert.Equal("1.2.6", audit.Packages[1].Fix);
    }

    [Fact]
    public async Task InfoReadsTheProjectFile()
    {
        using var project = new TempProject();
        project.Write("package.json", """
            {
              "name": "web",
              "version": "1.2.3",
              "packageManager": "pnpm@9.1.0",
              "scripts": { "build": "vite build", "test": "vitest" },
              "dependencies": { "react": "^18.0.0" },
              "devDependencies": { "typescript": "^5.0.0", "vite": "^5.0.0" }
            }
            """);

        var result = ResultAssert.Wire(await _service.InfoAsync(project.Root));

        Assert.Equal("web", result.GetProperty("name").GetString());
        Assert.Equal("pnpm", result.GetProperty("packageManager").GetString());
        Assert.Equal("pnpm@9.1.0", result.GetProperty("declaredPackageManager").GetString());
        Assert.Equal(1, result.GetProperty("dependencyCount").GetInt32());
        Assert.Equal(2, result.GetProperty("devDependencyCount").GetInt32());
        Assert.Equal("vite build", result.GetProperty("scripts").GetProperty("build").GetString());
    }

    [Fact]
    public async Task AnUnknownScriptListsTheOnesPackageJsonHas()
    {
        using var project = new TempProject();
        project.Write("package.json", """{ "name": "probe", "scripts": { "build": "vite build", "test": "vitest" } }""");

        var error = ResultAssert.Error(await _service.RunScriptAsync("deploy", project.Root, null, 30));

        Assert.Contains("no script named 'deploy'", error);
        var result = ResultAssert.Wire(await _service.RunScriptAsync("deploy", project.Root, null, 30));
        Assert.Contains("build", result.GetProperty("scripts").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task TypecheckSaysTypeScriptIsMissingInsteadOfDownloadingIt()
    {
        using var project = new TempProject();
        project.Write("package.json", """{ "name": "probe" }""");

        var error = ResultAssert.Error(await _service.TypecheckAsync(project.Root, null, emit: false, 30));

        Assert.Contains("not installed", error);
    }

    [Fact]
    public async Task LintSaysEslintIsMissingInsteadOfDownloadingIt()
    {
        using var project = new TempProject();
        project.Write("package.json", """{ "name": "probe" }""");

        var error = ResultAssert.Error(await _service.LintAsync(project.Root, null, fix: false, 30));

        Assert.Contains("not installed", error);
    }

    [Fact]
    public async Task AScriptRunsThroughThePackageManagerShim()
    {
        // npm is a .cmd on Windows: the run goes through ExternalTool's cmd.exe path. The test is
        // skipped (not failed) on a machine without Node, which is not this product's dependency.
        if (!ExternalTool.Exists("npm")) return;

        using var project = new TempProject();
        project.Write("package.json", """
            {
              "name": "probe",
              "scripts": { "hello": "node -e \"console.log('hello from npm')\"" }
            }
            """);

        var result = ResultAssert.Wire(await _service.RunScriptAsync("hello", project.Root, null, 120));

        Assert.True(result.GetProperty("succeeded").GetBoolean(), result.GetRawText());
        Assert.Contains("hello from npm", result.GetProperty("stdout").GetString() ?? string.Empty);
    }

    /// <summary>A throwaway folder that is removed with the test.</summary>
    private sealed class TempProject : IDisposable
    {
        public TempProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-node-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
