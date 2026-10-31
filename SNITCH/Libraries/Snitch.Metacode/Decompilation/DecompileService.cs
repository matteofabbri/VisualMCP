using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Snitch.Execution;
using Snitch.Metacode.Rendering;
using Snitch.Search;
using Snitch.Storage;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// ILSpy's decompiler, exposed twice: as text (the original three calls) and as metacode, where the C#
/// ILSpy produces is read by <see cref="Frontends.CSharpFrontend"/> and lowered into the shared model.
/// <para>
/// The text half prints what ILSpy prints, optionally compacted (see <see cref="CSharpCompactor"/>):
/// indentation and line breaks are a third of the characters and none of them carries meaning, so the
/// inline answer is compacted while a file written to disk keeps the readable formatting.
/// </para>
/// </summary>
public sealed class DecompileService : IDecompileService, IMetacodeDecompileService
{
    /// <summary>Cap on stacked error messages in a result, so a broken assembly cannot flood the note.</summary>
    private const int MaxReportedErrors = 5;

    private readonly IVisualMcpStore? _store;
    private readonly IFullTextIndexService? _fullText;

    /// <summary>For a host that has no solution store: decompiling needs no database.</summary>
    public DecompileService()
    {
    }

    public DecompileService(IVisualMcpStore? store = null, IFullTextIndexService? fullText = null)
    {
        _store = store;
        _fullText = fullText;
    }

    private static CSharpDecompiler Create(string assemblyPath) =>
        new(Path.GetFullPath(assemblyPath), new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });

    /// <summary>The error a caller gets for a path that is not a file, or null when it is one.</summary>
    private static object? Missing(string assemblyPath) =>
        string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)
            ? new { error = $"Assembly not found: {assemblyPath}" }
            : null;

    /// <summary>
    /// Machine code handed to ILSpy fails deep inside it with a metadata error; saying what the file is
    /// (before opening it as .NET) turns that into an instruction.
    /// </summary>
    private static object? NotManaged(string assemblyPath) =>
        ManagedAssemblyDetector.IsManaged(assemblyPath)
            ? null
            : new
            {
                error = NotManagedText(assemblyPath),
                hint = "For a native binary use analyze_native_binary (Ghidra, printed as C and pseudocode) or " +
                       "metacoding action=decompile, which routes it to Ghidra and stores the functions in the " +
                       "solution database's native_artifact / native_function / native_call tables.",
            };

    private static string NotManagedText(string assemblyPath) =>
        $"{Path.GetFileName(assemblyPath)} is not a managed assembly (no CLR metadata), so there is no C# to decompile.";

    /// <summary>ILSpy's formatting, or the same tokens without the whitespace that carries no meaning.</summary>
    private static string Format(string code, bool compact) => compact ? CSharpCompactor.Compact(code) : code;

    public object DecompileType(string assemblyPath, string typeFullName, int maxChars, bool compact = true)
    {
        if (Missing(assemblyPath) is { } missing) return missing;
        if (NotManaged(assemblyPath) is { } native) return native;
        if (string.IsNullOrWhiteSpace(typeFullName))
            return new { error = "A fully-qualified type name is required (e.g. 'Namespace.TypeName')." };

        try
        {
            var decompiler = Create(assemblyPath);
            var raw = decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
            var code = Format(raw, compact);
            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                type = typeFullName,
                language = "C#",
                compacted = compact,
                truncated = code.Length > maxChars,
                totalChars = code.Length,
                code = ProcessRunner.Truncate(code, maxChars),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to decompile type '{typeFullName}': {ex.Message}", assemblyPath };
        }
    }

    public object DecompileAssembly(string assemblyPath, string? outputPath, int maxChars, bool compact = true)
    {
        if (Missing(assemblyPath) is { } missing) return missing;
        if (NotManaged(assemblyPath) is { } native) return native;

        try
        {
            var decompiler = Create(assemblyPath);
            var code = decompiler.DecompileWholeModuleAsString();

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                // A file is read and compiled by something else: it keeps ILSpy's formatting.
                var full = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                Platform.FileText.Write(full, code);
                _fullText?.IndexText(full, "decompiled", code);
                return new { assembly = Path.GetFileName(assemblyPath), outputPath = full, bytes = code.Length, note = "Whole module decompiled and written to file." };
            }

            var preview = Format(code, compact);
            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                language = "C#",
                compacted = compact,
                truncated = preview.Length > maxChars,
                totalChars = preview.Length,
                note = preview.Length > maxChars ? "Large output truncated — pass outputPath to write the full C# to a file, or use decompile_type for one type." : null,
                code = ProcessRunner.Truncate(preview, maxChars),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to decompile assembly: {ex.Message}", assemblyPath };
        }
    }

    public object DecompileTypes(
        string assemblyPath,
        string? nameFilter,
        string? namespaceFilter,
        int skip,
        int maxTypes,
        bool listOnly,
        string? outputPath,
        int maxChars,
        bool persist = false,
        bool compact = true)
    {
        if (Missing(assemblyPath) is { } missing) return missing;
        if (NotManaged(assemblyPath) is { } native) return native;

        skip = Math.Max(0, skip);
        maxTypes = Math.Max(0, maxTypes);
        maxChars = Math.Clamp(maxChars, 2_000, 190_000);

        // The module version id identifies the binary: same MVID, same source. A persisted export is
        // returned as-is instead of running the decompiler again.
        var identity = persist ? TryReadMvid(assemblyPath) : null;
        var artifactDirectory = identity is null ? null : ArtifactDirectory(assemblyPath, identity);

        if (persist && !listOnly && identity is not null && _store?.FindArtifact(identity) is { } known &&
            Directory.Exists(known.Directory))
        {
            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                mvid = identity,
                outputDirectory = known.Directory,
                decompiled = known.Types,
                files = Directory.GetFiles(known.Directory, "*.cs").Take(10).Select(Path.GetFileName).ToList(),
                reused = true,
                note = "Decompiled earlier from a binary with the same module version id: the decompiler was not run again.",
            };
        }

        try
        {
            var decompiler = Create(assemblyPath);
            var module = decompiler.TypeSystem.MainModule;

            // Handles first: decompiling needs TypeDefinitionHandle, filtering needs the definition.
            var definitions = new Dictionary<TypeDefinitionHandle, ITypeDefinition>();
            foreach (var handle in module.MetadataFile.Metadata.TypeDefinitions)
            {
                var definition = module.GetDefinition(handle);
                if (definition is null || definition.Name == "<Module>") continue;
                definitions[handle] = definition;
            }

            var matches = definitions
                .Where(pair => Matches(pair.Value, nameFilter, namespaceFilter))
                .Select(pair => (pair.Key, pair.Value.FullName, pair.Value.Name))
                .OrderBy(pair => pair.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (listOnly)
            {
                var listed = matches.Skip(skip);
                if (maxTypes > 0) listed = listed.Take(maxTypes);
                var names = listed.Select(pair => pair.FullName).ToList();
                return new
                {
                    assembly = Path.GetFileName(assemblyPath),
                    totalTypes = definitions.Count,
                    matched = matches.Count,
                    returned = names.Count,
                    skipped = Math.Min(skip, matches.Count),
                    types = names,
                    note = names.Count < matches.Count - Math.Min(skip, matches.Count)
                        ? "More types match: continue with skip/maxTypes."
                        : null,
                };
            }

            var slice = matches.Skip(skip);
            if (maxTypes > 0) slice = slice.Take(maxTypes);
            var page = slice.ToList();

            var errors = new List<string>();
            var blocks = new List<(string Name, string ShortName, string Code)>(page.Count);
            var hidden = 0;
            foreach (var (handle, name, shortName) in page)
            {
                try
                {
                    var code = decompiler.DecompileTypesAsString(new[] { handle });

                    // Compiler-generated state machines and other hidden members decompile to nothing:
                    // writing an empty file would only look like a bug later.
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        hidden++;
                        continue;
                    }

                    blocks.Add((name, shortName, code));
                }
                catch (Exception ex)
                {
                    if (errors.Count < MaxReportedErrors) errors.Add($"{name}: {ex.Message}");
                }
            }

            var remaining = matches.Count - Math.Min(skip, matches.Count) - page.Count;
            var note = new StringBuilder();
            if (remaining > 0) note.Append($"{remaining} more type(s) match after this page: continue with skip={skip + page.Count}. ");
            if (hidden > 0) note.Append($"{hidden} type(s) produced no source (compiler-generated/hidden) and were skipped. ");
            if (errors.Count > 0) note.Append($"{errors.Count} type(s) failed: {string.Join(" | ", errors)}");
            var noteText = note.ToString().Trim();
            if (noteText.Length == 0) noteText = string.Empty;

            var directory = IsDirectoryTarget(outputPath)
                ? Path.GetFullPath(outputPath!)
                : artifactDirectory;

            if (directory is not null)
            {
                Directory.CreateDirectory(directory);

                var index = new StringBuilder();
                index.AppendLine($"# {Path.GetFileName(assemblyPath)} — {blocks.Count} type(s), ILSpy");
                index.AppendLine("# type<TAB>file");
                var files = new List<string>(blocks.Count);
                for (var i = 0; i < blocks.Count; i++)
                {
                    var fileName = $"{skip + i:D4}_{SafeFileName(blocks[i].ShortName)}.cs";
                    var file = Path.Combine(directory, fileName);
                    Platform.FileText.Write(file, blocks[i].Code);
                    _fullText?.IndexText(file, "decompiled", blocks[i].Code);
                    files.Add(fileName);
                    index.AppendLine($"{blocks[i].Name}\t{fileName}");
                }

                var indexPath = Path.Combine(directory, "_index.txt");
                Platform.FileText.Write(indexPath, index.ToString());

                if (identity is not null)
                {
                    _store?.RecordArtifact(new DecompiledArtifact(
                        identity, assemblyPath, Path.GetFileName(assemblyPath), directory, blocks.Count, DateTimeOffset.UtcNow));
                }

                return new
                {
                    assembly = Path.GetFileName(assemblyPath),
                    mvid = identity,
                    totalTypes = definitions.Count,
                    matched = matches.Count,
                    decompiled = blocks.Count,
                    hidden,
                    outputDirectory = directory,
                    indexFile = indexPath,
                    files = files.Take(10).ToList(),
                    persisted = identity is not null,
                    note = noteText.Length > 0 ? noteText : null,
                };
            }

            // Two renderings of the same page: one for a file that something else will read, one for the
            // answer, where the whitespace is only tokens.
            var readable = new StringBuilder();
            var compactedText = new StringBuilder();
            foreach (var block in blocks)
            {
                var header = "// ===== " + block.Name + " =====" + Environment.NewLine;
                readable.Append(header).AppendLine(block.Code).AppendLine();
                compactedText.Append(header).AppendLine(Format(block.Code, compact: true)).AppendLine();
            }

            var output = compact ? compactedText.ToString() : readable.ToString();
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                // A file stays readable: only the inline answer is compacted.
                var full = Path.GetFullPath(outputPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var text = readable.ToString();
                Platform.FileText.Write(full, text);
                _fullText?.IndexText(full, "decompiled", text);
                return new
                {
                    assembly = Path.GetFileName(assemblyPath),
                    totalTypes = definitions.Count,
                    matched = matches.Count,
                    decompiled = blocks.Count,
                    hidden,
                    outputPath = full,
                    bytes = text.Length,
                    note = noteText.Length > 0 ? noteText : null,
                };
            }

            return new
            {
                assembly = Path.GetFileName(assemblyPath),
                totalTypes = definitions.Count,
                matched = matches.Count,
                decompiled = blocks.Count,
                hidden,
                compacted = compact,
                truncated = output.Length > maxChars,
                totalChars = output.Length,
                note = noteText.Length > 0 ? noteText : null,
                code = ProcessRunner.Truncate(output, maxChars),
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to decompile assembly: {ex.Message}", assemblyPath };
        }
    }

    // ---------------------------------------------------------------- metacode

    public MetacodeDecompilation DecompileTypeToMetacode(string assemblyPath, string typeFullName)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), $"Assembly not found: {assemblyPath}");
        if (!ManagedAssemblyDetector.IsManaged(assemblyPath))
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), NotManagedText(assemblyPath));
        if (string.IsNullOrWhiteSpace(typeFullName))
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), "A fully-qualified type name is required (e.g. 'Namespace.TypeName').");

        try
        {
            var decompiler = Create(assemblyPath);
            var code = decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
            if (string.IsNullOrWhiteSpace(code))
                return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), $"Type '{typeFullName}' produced no C# (compiler-generated or hidden).");

            var translated = Translate(code, assemblyPath, typeFullName);
            return new MetacodeDecompilation(
                // The PATH, not the file name: this value becomes the source_path of the stored rows, and
                // two binaries called the same thing in two folders are not the same source.
                assemblyPath,
                new[] { translated },
                translated.Document,
                1,
                1,
                0,
                Array.Empty<string>(),
                TryReadMvid(assemblyPath));
        }
        catch (Exception ex)
        {
            return MetacodeDecompilation.Failed(
                Path.GetFileName(assemblyPath), $"Failed to decompile type '{typeFullName}': {ex.Message}");
        }
    }

    public MetacodeDecompilation DecompileAssemblyToMetacode(
        string assemblyPath,
        string? nameFilter = null,
        string? namespaceFilter = null,
        int skip = 0,
        int maxTypes = 0)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), $"Assembly not found: {assemblyPath}");

        if (!ManagedAssemblyDetector.IsManaged(assemblyPath))
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), NotManagedText(assemblyPath));

        skip = Math.Max(0, skip);
        maxTypes = Math.Max(0, maxTypes);

        try
        {
            var decompiler = Create(assemblyPath);
            var module = decompiler.TypeSystem.MainModule;

            var definitions = new List<ITypeDefinition>();
            foreach (var handle in module.MetadataFile.Metadata.TypeDefinitions)
            {
                var definition = module.GetDefinition(handle);
                if (definition is null || definition.Name == "<Module>") continue;

                // Compiler-generated types (`<>c`, `<PrivateImplementationDetails>`) decompile to
                // nothing and would make `matched` say more than the caller can get.
                if (definition.Name.StartsWith('<')) continue;
                definitions.Add(definition);
            }

            var matches = definitions
                .Where(type => Matches(type, nameFilter, namespaceFilter))
                .OrderBy(type => type.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var page = matches.Skip(skip);
            if (maxTypes > 0) page = page.Take(maxTypes);

            var types = new List<DecompiledTypeMetacode>();
            var errors = new List<string>();
            var hidden = 0;
            var failed = 0;
            foreach (var type in page)
            {
                try
                {
                    var code = decompiler.DecompileTypeAsString(new FullTypeName(type.ReflectionName));
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        hidden++;
                        continue;
                    }

                    types.Add(Translate(code, assemblyPath, type.FullName));
                }
                catch (Exception ex)
                {
                    // The message list is capped; the count is not, because paging has to know how many
                    // types of this page it must step over.
                    failed++;
                    if (errors.Count < MaxReportedErrors) errors.Add($"{type.FullName}: {ex.Message}");
                }
            }

            var document = new MetacodeDocument(
                Path.GetFileName(assemblyPath),
                MetacodeLanguage.CSharp,
                MergeModules(types.Select(t => t.Document)),
                types.SelectMany(t => t.Document.Diagnostics).ToList());

            return new MetacodeDecompilation(
                // The PATH: it is written as the source_path of every stored row (see the type overload).
                assemblyPath, types, document, definitions.Count, matches.Count, hidden, errors,
                TryReadMvid(assemblyPath), FailedTypes: failed);
        }
        catch (Exception ex)
        {
            return MetacodeDecompilation.Failed(Path.GetFileName(assemblyPath), $"Failed to decompile assembly: {ex.Message}");
        }
    }

    /// <summary>
    /// ILSpy's C# → the C# frontend → a document. The syntax tree is parsed with a file name made of
    /// the assembly and the type, so every node's origin says which ILSpy output line it came from.
    /// </summary>
    private static DecompiledTypeMetacode Translate(string code, string assemblyPath, string typeFullName)
    {
        var label = Path.GetFileName(assemblyPath) + ":" + typeFullName;
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(code, label + ".cs"));

        var members = 0;
        var functions = 0;
        foreach (var type in document.Modules.SelectMany(m => m.Declarations).OfType<MetacodeTypeDeclaration>())
            Count(type, ref members, ref functions);

        return new DecompiledTypeMetacode(
            typeFullName,
            new PseudocodeRenderer().Render(document),
            document,
            members,
            functions);
    }

    private static void Count(MetacodeTypeDeclaration type, ref int members, ref int functions)
    {
        foreach (var member in type.Members)
        {
            members++;
            if (member is MetacodeFunction) functions++;
            if (member is MetacodeTypeDeclaration nested) Count(nested, ref members, ref functions);
        }
    }

    /// <summary>Modules with the same name (a namespace two types share) are folded into the first one.</summary>
    private static IReadOnlyList<MetacodeModule> MergeModules(IEnumerable<MetacodeDocument> documents)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, (MetacodeModule Module, List<MetacodeDeclaration> Declarations)>(StringComparer.Ordinal);

        foreach (var module in documents.SelectMany(d => d.Modules))
        {
            if (!byName.TryGetValue(module.Name, out var existing))
            {
                order.Add(module.Name);
                byName[module.Name] = (module, module.Declarations.ToList());
                continue;
            }

            existing.Declarations.AddRange(module.Declarations);
        }

        return order.Select(name => byName[name].Module with { Declarations = byName[name].Declarations }).ToList();
    }

    private static bool Matches(ITypeDefinition type, string? nameFilter, string? namespaceFilter)
    {
        if (!string.IsNullOrWhiteSpace(namespaceFilter) &&
            !type.Namespace.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(nameFilter) ||
               type.FullName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A path written as a folder (existing directory or trailing separator) means one file per type.</summary>
    private static bool IsDirectoryTarget(string? outputPath) =>
        !string.IsNullOrWhiteSpace(outputPath) &&
        (Directory.Exists(outputPath) ||
         outputPath.EndsWith(Path.DirectorySeparatorChar) ||
         outputPath.EndsWith(Path.AltDirectorySeparatorChar));

    /// <summary>The module version id of an assembly, or null when it cannot be read.</summary>
    private static string? TryReadMvid(string assemblyPath) => AssemblyMvid.TryRead(assemblyPath);

    /// <summary>Where a persisted export lives: inside the solution store, in a folder named after the MVID.</summary>
    private string? ArtifactDirectory(string assemblyPath, string mvid)
    {
        var database = _store?.EnsureDatabase();
        if (database is null) return null;

        var root = Path.Combine(Path.GetDirectoryName(database)!, "decompiled");
        return Path.Combine(root, $"{SafeFileName(Path.GetFileNameWithoutExtension(assemblyPath))}-{mvid[..8]}");
    }

    private static string SafeFileName(string typeName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(typeName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return name.Length <= 120 ? name : name[..120];
    }
}
