using Snitch.Languages.CSharp.Packages;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// search_nuget_packages: the azuresearch response shape is parsed without a network, so
/// the search endpoint contract is pinned by a test instead of by an ad-hoc read.
/// </summary>
public sealed class SearchNuGetPackagesTests
{
    [Fact]
    public void Parse_ReadsTheAzureSearchShape()
    {
        const string json = """
        {
            "totalHits": 2,
            "data": [
                {
                    "id": "CsvHelper",
                    "version": "33.0.1",
                    "description": "Library to help reading and writing CSV files.",
                    "authors": ["Josh Close"],
                    "tags": ["csv", "excel"],
                    "totalDownloads": 200000000,
                    "projectUrl": "https://joshclose.github.io/CsvHelper/",
                    "versions": [{ "version": "33.0.1" }, { "version": "32.0.3" }]
                },
                {
                    "id": "Csv",
                    "version": "2.0.93",
                    "description": "",
                    "authors": [],
                    "tags": [],
                    "totalDownloads": 0,
                    "projectUrl": "",
                    "versions": []
                }
            ]
        }
        """;

        var (totalHits, packages) = SearchNuGetPackagesService.Parse(json);

        Assert.Equal(2, totalHits);
        Assert.Equal(2, packages.Count);

        var first = packages[0];
        Assert.Equal("CsvHelper", first.Id);
        Assert.Equal("33.0.1", first.Version);
        Assert.Contains("CSV", first.Description);
        Assert.Equal(200000000, first.TotalDownloads);
        Assert.Equal(["Josh Close"], first.Authors);
        Assert.Equal(["csv", "excel"], first.Tags);
        Assert.Equal(2, first.VersionCount);
        Assert.Contains("CsvHelper", first.ProjectUrl);

        var second = packages[1];
        Assert.Equal("Csv", second.Id);
        Assert.Empty(second.Authors);
        Assert.Equal(0, second.VersionCount);
    }

    [Fact]
    public void Parse_ToleratesMissingFields()
    {
        const string json = """{ "data": [ { "id": "OnlyId" } ] }""";

        var (totalHits, packages) = SearchNuGetPackagesService.Parse(json);

        Assert.Equal(0, totalHits);
        var only = Assert.Single(packages);
        Assert.Equal("OnlyId", only.Id);
        Assert.Equal("", only.Version);
        Assert.Equal("", only.Description);
        Assert.Equal(0, only.TotalDownloads);
        Assert.Empty(only.Authors);
        Assert.Empty(only.Tags);
        Assert.Equal("", only.ProjectUrl);
    }
}
