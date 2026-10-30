namespace Snitch.Profiling;

/// <summary>
/// Configuration for the profiling library. Every path is resolved relative to the
/// solution directory the caller passes in, so the library itself has no notion of a
/// workspace (it can be used from a test or a console host as well as from the MCP server).
/// </summary>
public sealed class ProfilingOptions
{
    /// <summary>Directory created under the solution for traces, dumps, reports and symbols.</summary>
    public string OutputDirectoryName { get; set; } = Path.Combine(".visualMCP", "profiling");

    /// <summary>Dotnet host used to list/install the diagnostic tools and to launch dotnet-monitor.</summary>
    public string DotnetExecutable { get; set; } = "dotnet";

    public int DefaultDurationSeconds { get; set; } = 10;

    public int MaxDurationSeconds { get; set; } = 300;

    /// <summary>Base URL of the NuGet flat-container feed used to look up the latest tool version.</summary>
    public string NuGetServiceIndex { get; set; } = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>Symbol server used by the dotnet-symbol reimplementation.</summary>
    public string SymbolServer { get; set; } = "https://msdl.microsoft.com/download/symbols";

    /// <summary>Where downloaded PDBs are cached. Defaults to the output directory / symbols.</summary>
    public string? SymbolCacheDirectory { get; set; }

    public string ResolveOutputDirectory(string solutionDirectory) =>
        Path.Combine(solutionDirectory, OutputDirectoryName);

    public string ResolveSymbolCache(string solutionDirectory) =>
        SymbolCacheDirectory ?? Path.Combine(ResolveOutputDirectory(solutionDirectory), "symbols");
}
