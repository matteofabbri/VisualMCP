using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Symbols;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileSymbolsTool
{
    [McpServerTool(Name = "profile_symbols"), Description(
        "Inspect or download the symbols of compiled modules — the native equivalent of 'dotnet-symbol' for managed " +
        "assemblies, no external tool needed. It reads the PE CodeView debug record (PDB name, GUID, age), reports " +
        "whether the PDB is embedded, next to the module or missing, and with action='download' fetches it from a " +
        "symbol server (default https://msdl.microsoft.com/download/symbols) into the solution's symbol cache using " +
        "the standard '<pdb>/<GUID><age>/<pdb>' protocol. Those PDBs are what profile_analyze and profile_stacks use " +
        "to turn a frame into file:line, so downloading symbols first makes the rest of the profiling output " +
        "actionable. Pass the module paths reported by a trace or a running process (e.g. the app's .dll).")]
    public static async Task<object> Symbols(
        ISymbolService symbols,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("'list' (default) or 'download'.")] string action = "list",
        [Description("Module paths (.dll/.exe) whose PDB should be inspected or downloaded.")] string[]? modulePaths = null,
        [Description("Directory for downloaded PDBs. Default: <solution>/.visualMCP/profiling/symbols.")] string? outputDirectory = null,
        [Description("Symbol server base URL for action='download'.")] string? symbolServer = null)
    {
        try
        {
            if (modulePaths is null || modulePaths.Length == 0)
                return new { error = "Pass modulePaths, e.g. ['C:\\app\\bin\\MyApp.dll']." };

            var directory = string.IsNullOrWhiteSpace(outputDirectory)
                ? options.ResolveSymbolCache(ProfilingToolHelpers.SolutionDirectory(workspace))
                : outputDirectory;

            if (string.Equals(action, "download", StringComparison.OrdinalIgnoreCase))
            {
                var downloaded = await symbols.DownloadAsync(modulePaths, directory, symbolServer);
                return new { action = "download", directory, modules = downloaded };
            }

            var inspected = symbols.Inspect(modulePaths, directory);
            return new { action = "list", directory, modules = inspected };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
