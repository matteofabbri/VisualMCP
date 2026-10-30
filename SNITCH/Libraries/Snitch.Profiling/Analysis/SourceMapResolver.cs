using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// The source position of one frame. <see cref="Line"/> is set only when the IL offset was known
/// and a sequence point covers it, which is what makes per-line CPU attribution honest: without an
/// IL→native map the frame still gets a <see cref="Display"/> (the method's first line, useful as
/// "the method lives here") but no <see cref="Line"/>, so it never pretends that every sample of
/// the method ran on its signature line.
/// </summary>
public readonly record struct SourceResolution(string? File, int? Line, string? Display);

/// <summary>
/// Maps a managed method (module + metadata token + IL offset) back to its source file and line.
/// It reads portable PDBs (standalone or embedded in the assembly), which is what the Roslyn
/// compiler emits by default, so a hot frame in a trace becomes <c>C:\...\File.cs:123</c> instead
/// of just a method name. The same resolver is used by the stack snapshot and the trace analyzer.
/// </summary>
public sealed class SourceMapResolver
{
    private readonly ConcurrentDictionary<string, Lazy<AssemblySymbols?>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _symbolCacheDirectory;

    public SourceMapResolver(ProfilingOptions options)
    {
        _symbolCacheDirectory = options.SymbolCacheDirectory;
    }

    public string? Resolve(string? modulePath, int metadataToken, int ilOffset = 0) =>
        ResolveFrame(modulePath, metadataToken, ilOffset).Display;

    /// <summary>
    /// Resolves one frame. <c>IlOffset</c> must come from the trace's IL→native map (the runtime
    /// emits it under the JIT keyword, which every CPU preset enables); <c>-1</c> means the runtime
    /// could not map the sample, and the answer then carries only the method's first line.
    /// </summary>
    public SourceResolution ResolveFrame(string? modulePath, int metadataToken, int ilOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(modulePath) || metadataToken == 0)
            return default;

        var symbols = _cache.GetOrAdd(modulePath, path => new Lazy<AssemblySymbols?>(
            () => AssemblySymbols.TryOpen(path, _symbolCacheDirectory))).Value;
        return symbols?.ResolveFrame(metadataToken, ilOffset) ?? default;
    }

    public bool HasSymbols(string? modulePath) =>
        !string.IsNullOrWhiteSpace(modulePath) &&
        _cache.GetOrAdd(modulePath, path => new Lazy<AssemblySymbols?>(
            () => AssemblySymbols.TryOpen(path, _symbolCacheDirectory))).Value is not null;

    private sealed class AssemblySymbols
    {
        private readonly MetadataReaderProvider _provider;
        private readonly MetadataReader _reader;

        private AssemblySymbols(MetadataReaderProvider provider)
        {
            _provider = provider;
            _reader = provider.GetMetadataReader();
        }

        public static AssemblySymbols? TryOpen(string modulePath, string? symbolCacheDirectory)
        {
            try
            {
                if (!File.Exists(modulePath))
                    return null;

                using var peReader = new PEReader(File.OpenRead(modulePath));
                if (!peReader.HasMetadata)
                    return null;

                // 1. Embedded portable PDB (single-file friendly).
                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                        continue;
                    return new AssemblySymbols(peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry));
                }

                // 2. Standalone .pdb next to the module.
                var pdbPath = FindPdb(modulePath, symbolCacheDirectory);
                if (pdbPath is null)
                    return null;

                var bytes = File.ReadAllBytes(pdbPath);
                return new AssemblySymbols(
                    MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(bytes)));
            }
            catch
            {
                // Missing/corrupt PDB, native module, or a Windows PDB: the caller degrades to
                // "method name only", which is still useful.
                return null;
            }
        }

        private static string? FindPdb(string modulePath, string? symbolCacheDirectory)
        {
            var sibling = Path.ChangeExtension(modulePath, ".pdb");
            if (File.Exists(sibling))
                return sibling;

            if (string.IsNullOrWhiteSpace(symbolCacheDirectory) || !Directory.Exists(symbolCacheDirectory))
                return null;

            var candidate = Path.Combine(symbolCacheDirectory, Path.GetFileName(sibling));
            return File.Exists(candidate) ? candidate : null;
        }

        public SourceResolution ResolveFrame(int metadataToken, int ilOffset)
        {
            try
            {
                var rid = metadataToken & 0x00FFFFFF;
                if (rid == 0)
                    return default;

                var handle = MetadataTokens.MethodDefinitionHandle(rid);
                if (handle.IsNil)
                    return default;

                var debugInformation = _reader.GetMethodDebugInformation(handle);
                if (debugInformation.Equals(default(MethodDebugInformation)))
                    return default;

                DocumentHandle document = default;
                var bestLine = 0;
                var bestOffset = -1;
                var firstLine = 0;
                DocumentHandle firstDocument = default;

                foreach (var point in debugInformation.GetSequencePoints())
                {
                    if (point.IsHidden)
                        continue;

                    if (firstLine == 0)
                    {
                        firstLine = point.StartLine;
                        firstDocument = point.Document;
                    }

                    if (point.Offset <= ilOffset && point.Offset >= bestOffset)
                    {
                        bestOffset = point.Offset;
                        bestLine = point.StartLine;
                        document = point.Document;
                    }
                }

                // A sequence point actually covers the sampled IL offset: (file, line) is the exact
                // position of the instruction, which is what a per-line cost needs.
                if (bestLine > 0)
                {
                    var documentName = ToPath(document);
                    return documentName is null
                        ? default
                        : new SourceResolution(documentName, bestLine, $"{documentName}:{bestLine}");
                }

                // No IL offset (or a prologue before the first point): show where the method starts
                // but expose no line, so the line report cannot invent a cost for it.
                if (firstLine > 0)
                {
                    var documentName = ToPath(firstDocument);
                    return documentName is null
                        ? default
                        : new SourceResolution(documentName, null, $"{documentName}:{firstLine}");
                }

                return default;
            }
            catch
            {
                return default;
            }
        }

        private string? ToPath(DocumentHandle handle)
        {
            if (handle.IsNil)
                return null;
            try
            {
                var document = _reader.GetDocument(handle);
                var name = _reader.GetString(document.Name);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }
    }
}
