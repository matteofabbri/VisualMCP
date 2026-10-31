using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class SearchNuGetPackagesTool
{
    [McpServerTool(Name = "search_nuget_packages"), Description(
        "Search the NuGet gallery for packages by id fragment or keywords (the nuget.org search index, not " +
        "the local cache): each hit returns id, latest stable version, description, download count, authors, " +
        "tags and the number of published versions. Use this to find the right package id BEFORE " +
        "add_nuget_package, which needs the exact id.")]
    public static Task<object> SearchNuGetPackages(
        ISearchNuGetPackagesService search,
        [Description("The query: a package id fragment or keywords, e.g. 'csv' or 'image sharp'.")] string query,
        [Description("How many hits to return (default 20, max 100).")] int take = 20,
        [Description("Include pre-release packages in the results (default false).")] bool prerelease = false,
        [Description("Request timeout in seconds (default 30).")] int timeoutSeconds = 30)
        => search.SearchAsync(query, take, prerelease, timeoutSeconds);
}
