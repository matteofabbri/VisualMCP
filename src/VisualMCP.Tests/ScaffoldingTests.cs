using System.Text.Json;
using Snitch.Scaffolding;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The scaffold documents are embedded resources now, not string literals: these tests pin that every
/// catalogue row resolves to an embedded license, that no placeholder survives rendering, and that the
/// files land on disk in the shape the tools advertise. The web serializer options are used so the
/// assertions read the same JSON the MCP client receives.
/// </summary>
public sealed class ScaffoldingTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vmcp-scaffold-" + Guid.NewGuid().ToString("N"));
    private readonly ScaffoldService _service = new();

    public ScaffoldingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── catalogue ─────────────────────────────────────────────────────────────
    [Fact]
    public void Catalogue_ListsLicensesWithSpdxMetadata()
    {
        var json = Result(_service.ListLicenses());
        var licenses = json.GetProperty("licenses").EnumerateArray().ToList();

        Assert.True(licenses.Count >= 20, $"only {licenses.Count} licenses are in the catalogue");
        Assert.Equal(licenses.Count, json.GetProperty("count").GetInt32());
        Assert.Equal(licenses.Count, licenses.Select(l => l.GetProperty("id").GetString()!).Distinct().Count());
        Assert.All(licenses, license =>
        {
            Assert.False(string.IsNullOrWhiteSpace(license.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(license.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(license.GetProperty("spdx").GetString()));
            Assert.Contains(license.GetProperty("family").GetString(),
                new[] { "permissive", "weak-copyleft", "strong-copyleft", "network-copyleft", "public-domain", "source-available" });
        });

        var ids = licenses.Select(l => l.GetProperty("id").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("mit", ids);
        Assert.Contains("apache-2.0", ids);
        Assert.Contains("gpl-3.0-only", ids);
        Assert.Contains("agpl-3.0-only", ids);
        Assert.Contains("lgpl-2.1-only", ids);
        Assert.Contains("mpl-2.0", ids);
        Assert.Contains("unlicense", ids);
        Assert.Contains("polyform-noncommercial-1.0.0", ids);
    }

    // ── add_license ───────────────────────────────────────────────────────────
    [Fact]
    public void EveryCatalogueLicenseHasAnEmbeddedTemplateThatRenders()
    {
        foreach (var id in CatalogueIds())
        {
            var target = Path.Combine(_directory, id);
            Directory.CreateDirectory(target);

            var result = Result(_service.AddLicense(id, target, "ACME Corp", 2031, overwrite: false));
            Assert.Equal(id, result.GetProperty("license").GetString());

            var text = File.ReadAllText(Path.Combine(target, "LICENSE"));
            Assert.True(text.Length > 300, $"the LICENSE written for '{id}' is only {text.Length} chars");
            Assert.Contains("ACME Corp", text);
            Assert.Contains("2031", text);
            Assert.DoesNotContain("{{", text);
        }
    }

    [Fact]
    public void LicenseIdsAreCaseInsensitiveAndAnExistingFileNeedsOverwrite()
    {
        Result(_service.AddLicense("MIT", _directory, "First Holder", 2030, overwrite: false));
        var path = Path.Combine(_directory, "LICENSE");
        Assert.Contains("MIT License", File.ReadAllText(path));

        Assert.Contains("overwrite=true", Error(_service.AddLicense("mit", _directory, null, null, overwrite: false)));

        Result(_service.AddLicense("MIT", _directory, "Second Holder", 2030, overwrite: true));
        Assert.Contains("Second Holder", File.ReadAllText(path));
    }

    [Fact]
    public void UnknownLicenseNamesTheWholeCatalogue()
    {
        var error = Error(_service.AddLicense("gpl-4.0", _directory, null, null, overwrite: false));

        Assert.Contains("Unknown license 'gpl-4.0'", error);
        Assert.Contains("apache-2.0", error);
        Assert.Contains("polyform-small-business-1.0.0", error);
    }

    // ── add_contributing ──────────────────────────────────────────────────────
    [Fact]
    public void ContributingIncludesTheClaOnlyWhenAsked()
    {
        Result(_service.AddContributing(_directory, "Maintainer Inc", "the MIT License", includeCla: true, overwrite: false));
        var withCla = File.ReadAllText(Path.Combine(_directory, "CONTRIBUTING.md"));

        Assert.Contains("the MIT License", withCla);
        Assert.Contains("Maintainer Inc", withCla);
        Assert.Contains("Contributor License Agreement (CLA)", withCla);
        Assert.DoesNotContain("{{", withCla);

        var plainDir = Path.Combine(_directory, "no-cla");
        Directory.CreateDirectory(plainDir);
        Result(_service.AddContributing(plainDir, null, null, includeCla: false, overwrite: false));
        var plain = File.ReadAllText(Path.Combine(plainDir, "CONTRIBUTING.md"));

        Assert.DoesNotContain("Contributor License Agreement", plain);
        Assert.DoesNotContain("{{", plain);
        Assert.Contains("the project's LICENSE", plain);
    }

    // ── add_ci_workflow ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("dotnet-multi-os", "build")]
    [InlineData("dotnet-release", "release")]
    public void CiWorkflowRendersTheTemplateAndKeepsGitHubExpressions(string type, string fileName)
    {
        var result = Result(_service.AddCiWorkflow(_directory, @"src\App\App.csproj", "", type, overwrite: false));
        var path = result.GetProperty("path").GetString()!.Replace('\\', '/');

        Assert.EndsWith($"/.github/workflows/{fileName}.yml", path);
        var yml = File.ReadAllText(result.GetProperty("path").GetString()!);
        Assert.Contains($"name: {fileName}", yml);
        Assert.Contains("src/App/App.csproj", yml);
        Assert.Contains("${{ matrix.rid }}", yml);
        Assert.DoesNotContain("{{NAME}}", yml);
        Assert.DoesNotContain("{{PROJECT}}", yml);
        Assert.DoesNotContain("{{APPNAME}}", yml);
        Assert.Contains("${{ matrix.os }}", yml);

        if (type == "dotnet-release")
            Assert.Contains("App-${{ matrix.rid }}.zip", yml);   // {APPNAME} is filled in
    }

    [Fact]
    public void UnknownWorkflowTypeIsRefused()
    {
        var error = Error(_service.AddCiWorkflow(_directory, "App.csproj", "build", "jenkins", overwrite: false));
        Assert.Contains("dotnet-multi-os", error);
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private List<string> CatalogueIds()
    {
        var json = JsonSerializer.SerializeToElement(_service.ListLicenses(), Web);
        return json.GetProperty("licenses").EnumerateArray()
            .Select(license => license.GetProperty("id").GetString()!)
            .ToList();
    }

    private static JsonElement Result(object result)
    {
        var json = JsonSerializer.SerializeToElement(result, Web);
        if (json.TryGetProperty("error", out var error))
            throw new Xunit.Sdk.XunitException("scaffold returned an error: " + error.GetString() + Environment.NewLine + json.GetRawText());

        return json;
    }

    private static string Error(object result)
    {
        var json = JsonSerializer.SerializeToElement(result, Web);
        if (!json.TryGetProperty("error", out var error) || error.GetString() is not { } text)
            throw new Xunit.Sdk.XunitException("expected a refusal, got: " + json.GetRawText());

        return text;
    }
}
