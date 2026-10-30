using System.Reflection.PortableExecutable;

namespace Snitch.Profiling.Symbols;

/// <summary>What a module's PE debug directory says about its symbols.</summary>
public sealed record ModuleSymbolInfo(
    string ModulePath,
    string? PdbPath,
    string? PdbName,
    Guid Guid,
    int Age,
    bool Embedded,
    bool Downloaded,
    string Status);

public interface ISymbolService
{
    IReadOnlyList<ModuleSymbolInfo> Inspect(IEnumerable<string> modulePaths, string? outputDirectory);

    Task<IReadOnlyList<ModuleSymbolInfo>> DownloadAsync(
        IEnumerable<string> modulePaths, string? outputDirectory, string? symbolServer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Native replacement for <c>dotnet-symbol</c> for managed modules: it reads the CodeView record
/// from the PE debug directory and speaks the same symbol-server protocol
/// (<c>pdb/&lt;GUID&gt;&lt;age&gt;/pdb</c>) to fetch and cache the portable PDB. That PDB is what
/// <see cref="Analysis.SourceMapResolver"/> then turns into file:line locations.
/// </summary>
public sealed class SymbolService : ISymbolService
{
    private readonly ProfilingOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public SymbolService(ProfilingOptions options) => _options = options;

    public IReadOnlyList<ModuleSymbolInfo> Inspect(IEnumerable<string> modulePaths, string? outputDirectory)
    {
        var directory = ResolveOutputDirectory(outputDirectory);
        return modulePaths.Select(path => InspectOne(path, directory)).ToList();
    }

    public async Task<IReadOnlyList<ModuleSymbolInfo>> DownloadAsync(
        IEnumerable<string> modulePaths, string? outputDirectory, string? symbolServer,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolveOutputDirectory(outputDirectory);
        Directory.CreateDirectory(directory);
        var server = string.IsNullOrWhiteSpace(symbolServer) ? _options.SymbolServer : symbolServer.TrimEnd('/');
        var results = new List<ModuleSymbolInfo>();

        foreach (var modulePath in modulePaths)
        {
            var info = InspectOne(modulePath, directory);
            if (info.Embedded)
            {
                results.Add(info with { Status = "embedded: nothing to download" });
                continue;
            }

            if (info.PdbName is null || info.Guid == Guid.Empty)
            {
                results.Add(info with { Status = info.Status });
                continue;
            }

            if (info.PdbPath is not null && File.Exists(info.PdbPath))
            {
                results.Add(info with { Status = "already present" });
                continue;
            }

            var target = Path.Combine(directory, info.PdbName);
            var key = $"{info.Guid:N}{info.Age:x}";
            var url = $"{server}/{info.PdbName}/{key}/{info.PdbName}";

            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    results.Add(info with
                    {
                        Status = $"symbol server returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    });
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (!LooksLikePdb(bytes))
                {
                    results.Add(info with { Status = "downloaded content is not a PDB" });
                    continue;
                }

                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                results.Add(info with { PdbPath = target, Downloaded = true, Status = "downloaded" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(info with { Status = $"download failed: {ex.Message}" });
            }
        }

        return results;
    }

    private static ModuleSymbolInfo InspectOne(string modulePath, string outputDirectory)
    {
        try
        {
            if (!File.Exists(modulePath))
                return new ModuleSymbolInfo(modulePath, null, null, Guid.Empty, 0, false, false, "module not found");

            using var peReader = new PEReader(File.OpenRead(modulePath));
            var entries = peReader.ReadDebugDirectory();

            if (entries.Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb))
                return new ModuleSymbolInfo(modulePath, modulePath, Path.GetFileName(modulePath), Guid.Empty, 0, true, false, "embedded portable PDB");

            var codeView = entries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView);
            if (codeView.DataSize == 0)
                return new ModuleSymbolInfo(modulePath, null, null, Guid.Empty, 0, false, false, "no CodeView debug record");

            var data = peReader.ReadCodeViewDebugDirectoryData(codeView);
            var pdbName = Path.GetFileName(data.Path);
            var sibling = Path.ChangeExtension(modulePath, ".pdb");
            var cached = Path.Combine(outputDirectory, pdbName);

            var pdbPath = File.Exists(sibling) ? sibling : File.Exists(cached) ? cached : null;
            var status = pdbPath is null ? "missing" : pdbPath == sibling ? "next to the module" : "in the symbol cache";

            return new ModuleSymbolInfo(
                modulePath, pdbPath, pdbName, data.Guid, data.Age, false, false, status);
        }
        catch (Exception ex)
        {
            return new ModuleSymbolInfo(modulePath, null, null, Guid.Empty, 0, false, false, $"unreadable: {ex.Message}");
        }
    }

    private string ResolveOutputDirectory(string? outputDirectory) =>
        !string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetFullPath(outputDirectory)
            : _options.SymbolCacheDirectory ??
              Path.Combine(Environment.CurrentDirectory, _options.OutputDirectoryName, "symbols");

    private static bool LooksLikePdb(byte[] bytes)
    {
        if (bytes.Length < 8)
            return false;

        // Portable PDBs start with the ECMA-335 metadata signature "BSJB".
        if (bytes[0] == 'B' && bytes[1] == 'S' && bytes[2] == 'J' && bytes[3] == 'B')
            return true;

        // Windows PDBs start with "Microsoft C/C++ MSF 7.00".
        const string msf = "Microsoft C/C++ MSF 7.00";
        return bytes.Length >= msf.Length &&
               System.Text.Encoding.ASCII.GetString(bytes, 0, msf.Length) == msf;
    }
}
