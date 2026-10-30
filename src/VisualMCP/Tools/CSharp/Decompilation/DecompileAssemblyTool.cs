using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DecompileAssemblyTool
{
    [McpServerTool(Name = "decompile_assembly"), Description(
        "Decompile a .NET assembly (.dll) to C# with the ILSpy engine. Three ways to use it: " +
        "(1) WHOLE MODULE — no filters and outputPath as a .cs file writes everything to one file, otherwise you get a " +
        "truncated preview; (2) SELECTED TYPES — nameFilter / namespaceFilter (substring, case-insensitive) decompile just " +
        "those, and listOnly=true returns only their names (fast: use it first to discover what an assembly contains, " +
        "including internal types that an exported-API listing never shows); (3) ONE FILE PER TYPE — pass a DIRECTORY as " +
        "outputPath (an existing folder, or a path ending in a separator) and each selected type is written to " +
        "0001_Namespace.Type.cs with an _index.txt mapping names to files, so you can then open them with read_file. " +
        "The C# in the ANSWER is compacted by default (ILSpy's indentation, line breaks and comments carry no meaning " +
        "and cost about half the tokens); files written to outputPath always keep the readable formatting, and after " +
        "they are written they are searchable with text_index. Large assemblies: full exports take minutes and can " +
        "exceed the client timeout — filter first, and page with skip/maxTypes. For one known type use decompile_type, " +
        "which is cheaper.")]
    public static object DecompileAssembly(
        IDecompileService decompile,
        [Description("Path to the .dll to decompile.")] string assemblyPath,
        [Description("A .cs file for one concatenated output, or a DIRECTORY (existing, or ending with a separator) for one file per type.")] string? outputPath = null,
        [Description("Only types whose full name contains this text (case-insensitive). Empty = all.")] string? nameFilter = null,
        [Description("Only types in this namespace or a sub-namespace (prefix match, case-insensitive).")] string? namespaceFilter = null,
        [Description("Skip this many matching types (paging). Default 0.")] int skip = 0,
        [Description("Maximum types to decompile or list. 0 = all (default 0; use a bound on large assemblies).")] int maxTypes = 0,
        [Description("Return only the matching type names, without source (fast discovery).")] bool listOnly = false,
        [Description("Keep the one-file-per-type export in the solution store (.visualMCP/decompiled/<name>-<mvid>/) and reuse " +
                     "it when a binary with the same module version id is decompiled again. Needs a loaded solution.")] bool persist = false,
        [Description("Maximum characters of C# returned when no outputPath is given (default: 60000).")] int maxChars = 60_000,
        [Description("Strip indentation, line breaks and comments from the C# returned in the answer (default: true). " +
                     "Files written to outputPath are never compacted. Pass false for the readable form inline too.")] bool compact = true)
    {
        var directoryTarget = !string.IsNullOrWhiteSpace(outputPath) &&
            (Directory.Exists(outputPath) ||
             outputPath.EndsWith(Path.DirectorySeparatorChar) ||
             outputPath.EndsWith(Path.AltDirectorySeparatorChar));

        var wholeModule = string.IsNullOrWhiteSpace(nameFilter) &&
                          string.IsNullOrWhiteSpace(namespaceFilter) &&
                          !listOnly &&
                          !persist &&
                          !directoryTarget &&
                          skip == 0;

        return wholeModule
            ? decompile.DecompileAssembly(assemblyPath, outputPath, maxChars, compact)
            : decompile.DecompileTypes(
                assemblyPath, nameFilter, namespaceFilter, skip, maxTypes, listOnly, outputPath, maxChars, persist, compact);
    }
}
