using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snitch.Languages.CSharp.Inspections;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The real ReSharper catalogue: the ids are read from assembly metadata, so the scan is proven on a synthetic
/// assembly and exercised on the local Rider installation when one is present.
/// </summary>
public sealed class ResharperCatalogueTests
{
    private const string ProbeSource = """
        namespace JetBrains.ReSharper.Feature.Services.Daemon
        {
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class ConfigurableSeverityHighlightingAttribute : Attribute
            {
                public ConfigurableSeverityHighlightingAttribute(string id, string languages) { }

                public string AttributeId { get; set; }

                public string ToolTipFormatStringResourceName { get; set; }

                public Type ToolTipFormatStringResourceType { get; set; }

                public string Languages { get; set; }
            }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class ElementProblemAnalyzerAttribute : Attribute
            {
                public ElementProblemAnalyzerAttribute(params Type[] elementTypes) { }

                public Type[] HighlightingTypes { get; set; }
            }
        }

        namespace Probe
        {
            [JetBrains.ReSharper.Feature.Services.Daemon.ConfigurableSeverityHighlighting(
                "ProbeRule", "CSHARP", AttributeId = "ReSharper Probe", Languages = "CSHARP",
                ToolTipFormatStringResourceName = "ProbeRule_Text")]
            public sealed class ProbeRuleWarning { }

            [JetBrains.ReSharper.Feature.Services.Daemon.ConfigurableSeverityHighlighting(
                "ProbeStyle", "CSHARP", Languages = "CSHARP")]
            public sealed class ProbeStyleInfo { }

            [JetBrains.ReSharper.Feature.Services.Daemon.ElementProblemAnalyzer(
                typeof(object), HighlightingTypes = new[] { typeof(ProbeRuleWarning) })]
            public sealed class ProbeRuleAnalyzer { }
        }
        """;

    [Fact]
    public void The_reader_takes_the_ids_the_groups_and_the_analyzer_links_from_the_metadata()
    {
        var root = CreateProbeInstallation();
        try
        {
            var catalogue = ReSharperCatalogueReader.Read(root);

            Assert.True(catalogue.Found, catalogue.Error);
            Assert.Equal(1, catalogue.AssembliesScanned);
            Assert.Equal(2, catalogue.Ids.Count);

            var rule = catalogue.Ids.Single(id => id.Id == "ProbeRule");
            Assert.Equal("CSHARP", rule.Languages);
            Assert.Equal("ReSharper Probe", rule.Category);
            Assert.Equal("Probe.ProbeRuleWarning", rule.HighlightingType);
            Assert.Equal("Probe.ProbeRuleAnalyzer", rule.AnalyzerType);
            Assert.Equal("Probe rule description", rule.Description);

            var style = catalogue.Ids.Single(id => id.Id == "ProbeStyle");
            Assert.Null(style.AnalyzerType);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_folder_without_the_engine_is_reported_not_guessed()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "catalogue-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalogue = ReSharperCatalogueReader.Read(root);

            Assert.False(catalogue.Found);
            Assert.NotNull(catalogue.Error);
            Assert.Empty(catalogue.Ids);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_local_rider_installation_yields_the_real_catalogue()
    {
        var rider = FindRider();
        if (rider is null) return;

        var catalogue = ReSharperCatalogueReader.Read(rider);

        Assert.True(catalogue.Found, catalogue.Error);
        Assert.True(catalogue.Ids.Count > 300, $"only {catalogue.Ids.Count} ids were read");
        Assert.True(catalogue.Ids.Count(id => id.AnalyzerType is not null) > 300);
        Assert.True(catalogue.Ids.Count(id => id.Description is not null) > 100);

        var invertIf = catalogue.Ids.First(id => id.Id == "InvertIf");
        Assert.Contains("InvertIfProblemAnalyzer", invertIf.AnalyzerType);
        Assert.Equal("CSHARP", invertIf.Languages);
        Assert.False(string.IsNullOrWhiteSpace(invertIf.Description));
    }

    [Fact]
    public async Task The_report_joins_the_catalogue_with_the_curated_coverage()
    {
        var rider = FindRider();
        if (rider is null) return;

        var result = ResultAssert.Json(await new ResharperInspectionMapService()
            .ReportAsync(rider, includeInstall: true, maxUncovered: 5));

        Assert.True(result.GetProperty("catalogueIdCount").GetInt32() > 300);

        var install = result.GetProperty("install");
        Assert.True(install.GetProperty("found").GetBoolean());
        Assert.True(install.GetProperty("idCount").GetInt32() > 300);

        var coverage = result.GetProperty("coverage");
        Assert.True(coverage.GetProperty("mappedInCatalogueCount").GetInt32() > 0);
        Assert.True(coverage.GetProperty("uncoveredFromCatalogueCount").GetInt32() > 0);
        Assert.True(coverage.GetProperty("uncoveredFromCatalogue").GetArrayLength() <= 5);
    }

    private static string CreateProbeInstallation()
    {
        var compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(ProbeSource)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var root = Path.Combine(AppContext.BaseDirectory, "catalogue-probe-" + Guid.NewGuid().ToString("N"));
        var host = Path.Combine(root, "lib", "ReSharperHost");
        Directory.CreateDirectory(host);

        var resources = Path.Combine(root, "Strings.resources");
        using (var writer = new System.Resources.ResourceWriter(resources))
            writer.AddResource("ProbeRule_Text", "Probe rule description");

        using var stream = File.Create(Path.Combine(host, "Probe.dll"));
        var emit = compilation.Emit(
            stream,
            manifestResources: [new ResourceDescription("Probe.Strings.resources", () => File.OpenRead(resources), isPublic: true)]);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Select(d => d.ToString())));
        return root;
    }

    private static string? FindRider()
    {
        var home = Environment.GetEnvironmentVariable("RIDER_HOME");
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(Path.Combine(home, "lib", "ReSharperHost")))
            return home;

        var jetbrains = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains");
        if (!Directory.Exists(jetbrains)) return null;

        return Directory.EnumerateDirectories(jetbrains, "*Rider*")
            .Where(directory => Directory.Exists(Path.Combine(directory, "lib", "ReSharperHost")))
            .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
