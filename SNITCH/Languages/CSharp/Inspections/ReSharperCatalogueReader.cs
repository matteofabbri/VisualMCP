using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Reads the ReSharper inspection catalogue out of a Rider installation. Every rule's id is a
/// <c>ConfigurableSeverityHighlighting</c> attribute argument and every rule's implementation is the
/// <c>ElementProblemAnalyzer</c> that names those highlightings: both are assembly metadata, so the catalogue
/// is read rather than guessed. The scan runs once per installation per process.
/// </summary>
public static class ReSharperCatalogueReader
{
    private const string ConfigurableSeverityAttribute = "ConfigurableSeverityHighlightingAttribute";
    private const string AnalyzerAttribute = "ElementProblemAnalyzerAttribute";

    /// <summary>The ids, keyed on the installation path: the engine does not change under a running server.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<ReSharperCatalogue>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads (and caches) the catalogue of the Rider installation at <paramref name="riderPath"/>.</summary>
    public static ReSharperCatalogue Read(string riderPath)
    {
        var path = System.IO.Path.GetFullPath(riderPath);
        return Cache.GetOrAdd(
            path,
            key => new Lazy<ReSharperCatalogue>(() => Scan(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ReSharperCatalogue Scan(string riderPath)
    {
        var host = System.IO.Path.Combine(riderPath, "lib", "ReSharperHost");
        if (!System.IO.Directory.Exists(host))
            return ReSharperCatalogue.NotFound(riderPath, "lib/ReSharperHost was not found under this installation.");

        var entries = new List<RawId>();
        var analyzerByHighlighting = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var assemblies = 0;
        var failures = 0;
        var failureSample = new List<string>();
        var provider = new TypeNameProvider(host);

        foreach (var file in System.IO.Directory.EnumerateFiles(host, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;

                var reader = pe.GetMetadataReader();
                if (!Mentions(reader, ConfigurableSeverityAttribute) && !Mentions(reader, AnalyzerAttribute))
                    continue;

                assemblies++;
                var assembly = reader.IsAssembly
                    ? reader.GetString(reader.GetAssemblyDefinition().Name)
                    : System.IO.Path.GetFileNameWithoutExtension(file);

                foreach (var handle in reader.CustomAttributes)
                {
                    var attribute = reader.GetCustomAttribute(handle);
                    if (attribute.Parent.Kind != HandleKind.TypeDefinition) continue;

                    var constructor = ConstructorTypeName(reader, attribute);
                    if (constructor is not (AnalyzerAttribute or ConfigurableSeverityAttribute)) continue;

                    CustomAttributeValue<TypeName> value;
                    try
                    {
                        value = attribute.DecodeValue(provider);
                    }
                    catch (Exception)
                    {
                        failures++;
                        if (failureSample.Count < 10) failureSample.Add(constructor + "@" + assembly);
                        continue;
                    }

                    if (constructor == AnalyzerAttribute)
                    {
                        var analyzer = FullName(reader, (TypeDefinitionHandle)attribute.Parent);
                        foreach (var highlighting in TypeArray(value, "HighlightingTypes"))
                        {
                            if (!analyzerByHighlighting.TryGetValue(highlighting, out var analyzers))
                            {
                                analyzers = [];
                                analyzerByHighlighting[highlighting] = analyzers;
                            }

                            analyzers.Add(analyzer);
                        }
                    }
                    else if (constructor == ConfigurableSeverityAttribute)
                    {
                        if (value.FixedArguments.Length == 0 || value.FixedArguments[0].Value is not string id || id.Length == 0)
                            continue;

                        var languages = value.FixedArguments.Length > 1 && value.FixedArguments[1].Value is string language
                            ? language
                            : "";
                        entries.Add(new RawId(
                            id,
                            languages,
                            StringArgument(value, "AttributeId"),
                            StringArgument(value, "ToolTipFormatStringResourceName"),
                            FullName(reader, (TypeDefinitionHandle)attribute.Parent),
                            assembly,
                            file));
                    }
                }
            }
            catch (Exception)
            {
                // A native or unreadable DLL in the folder is not a failure of the scan.
            }
        }

        var resources = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var resourceErrors = new List<string>();
        var ids = new List<ReSharperInspectionId>(entries.Count);
        foreach (var entry in entries)
        {
            ids.Add(new ReSharperInspectionId(
                entry.Id,
                entry.Languages,
                entry.Category,
                entry.HighlightingType,
                ResolveAnalyzer(analyzerByHighlighting, entry.HighlightingType),
                Description(entry, resources, resourceErrors),
                entry.Assembly));
        }

        // The same id can be declared once per assembly (a base highlighting plus a fork): keep the entry
        // that actually links to an analyzer, then the first one.
        var deduplicated = ids
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(entry => entry.AnalyzerType is not null) ?? group.First())
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        return new ReSharperCatalogue(true, riderPath, null, assemblies, failures, deduplicated, failureSample, resourceErrors);
    }

    /// <summary>
    /// Several analyzers can name the same highlighting; the rule's own analyzer is the one whose name matches
    /// the highlighting (<c>InvertIfWarning</c> → <c>InvertIfProblemAnalyzer</c>), not merely the first one read.
    /// </summary>
    private static string? ResolveAnalyzer(Dictionary<string, List<string>> map, string highlighting)
    {
        if (!map.TryGetValue(highlighting, out var analyzers) || analyzers.Count == 0) return null;

        var simple = highlighting[(highlighting.LastIndexOf('+') + 1)..];
        simple = simple[(simple.LastIndexOf('.') + 1)..];
        foreach (var suffix in new[] { "Warning", "Info", "Suggestion", "Error", "Highlighting" })
        {
            if (!simple.EndsWith(suffix, StringComparison.Ordinal)) continue;
            simple = simple[..^suffix.Length];
            break;
        }

        foreach (var analyzer in analyzers)
        {
            var name = analyzer[(analyzer.LastIndexOf('+') + 1)..];
            name = name[(name.LastIndexOf('.') + 1)..];
            if (name.StartsWith(simple, StringComparison.Ordinal)) return analyzer;
        }

        return analyzers[0];
    }

    private static string? Description(
        RawId entry,
        Dictionary<string, Dictionary<string, string>> resources,
        List<string> resourceErrors)
    {
        if (entry.ResourceName is not { Length: > 0 } name) return null;

        if (!resources.TryGetValue(entry.File, out var map))
        {
            map = ReadResourceStrings(entry.File, out var error) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            resources[entry.File] = map;
            if (error is not null && resourceErrors.Count < 10)
                resourceErrors.Add(entry.Assembly + ": " + error);
        }

        // A tooltip format string is a sentence, not a chapter: anything longer is not the description.
        return map.TryGetValue(name, out var text) && text.Length is > 0 and < 400 ? text : null;
    }

    /// <summary>Whether the assembly references or declares one of the two attributes, so the rest is skipped.</summary>
    private static bool Mentions(MetadataReader reader, string simpleName)
    {
        foreach (var handle in reader.TypeReferences)
            if (reader.StringComparer.Equals(reader.GetTypeReference(handle).Name, simpleName))
                return true;
        foreach (var handle in reader.TypeDefinitions)
            if (reader.StringComparer.Equals(reader.GetTypeDefinition(handle).Name, simpleName))
                return true;
        return false;
    }

    private static string? ConstructorTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return member.Parent.Kind == HandleKind.TypeReference
                    ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                    : null;

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);

            default:
                return null;
        }
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, declaring) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string? StringArgument(CustomAttributeValue<TypeName> value, string name)
    {
        foreach (var argument in value.NamedArguments)
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) && argument.Value is string text)
                return text;
        return null;
    }

    private static IEnumerable<string> TypeArray(CustomAttributeValue<TypeName> value, string name)
    {
        foreach (var argument in value.NamedArguments)
        {
            if (!string.Equals(argument.Name, name, StringComparison.Ordinal)) continue;

            if (argument.Value is ImmutableArray<CustomAttributeTypedArgument<TypeName>> items)
            {
                foreach (var item in items)
                    if (item.Value is TypeName type && type.Name is { Length: > 0 } text)
                        yield return text;
            }
            else if (argument.Value is TypeName single && single.Name is { Length: > 0 } one)
            {
                yield return one;
            }
        }
    }

    /// <summary>
    /// Reads the embedded <c>.resources</c> streams of one assembly. Each stream starts at its manifest
    /// offset inside the resource directory and ends where the next resource starts.
    /// </summary>
    private static Dictionary<string, string>? ReadResourceStrings(string file, out string? error)
    {
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(file);
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                error = "the file has no metadata";
                return null;
            }

            var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory;
            if (directory is not { } header || header.RelativeVirtualAddress == 0 || header.Size == 0)
            {
                error = "the image has no resource directory";
                return null;
            }

            var reader = pe.GetMetadataReader();
            var resources = reader.ManifestResources
                .Select(handle => reader.GetManifestResource(handle))
                .Where(resource => resource.Implementation.IsNil)
                .Select(resource => (Name: reader.GetString(resource.Name), resource.Offset))
                .Where(resource => resource.Name.EndsWith(".resources", StringComparison.Ordinal))
                .OrderBy(resource => resource.Offset)
                .ToList();
            if (resources.Count == 0)
            {
                error = "no embedded .resources streams";
                return null;
            }

            var baseOffset = RvaToFileOffset(pe.PEHeaders, header.RelativeVirtualAddress);
            if (baseOffset < 0)
            {
                error = "the resource directory RVA does not map to a file offset";
                return null;
            }

            var end = Math.Min(bytes.Length, baseOffset + header.Size);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < resources.Count; index++)
            {
                var start = baseOffset + resources[index].Offset;
                var stop = index + 1 < resources.Count ? baseOffset + resources[index + 1].Offset : end;
                if (start < 0 || stop > bytes.Length || stop <= start) continue;

                var aligned = AlignToResourceMagic(bytes, start, stop);
                try
                {
                    using var section = new MemoryStream(bytes, (int)aligned, (int)(stop - aligned), writable: false);
                    using var resourceReader = new ResourceReader(section);
                    foreach (DictionaryEntry entry in resourceReader)
                        if (entry.Key is string key && entry.Value is string text)
                            map.TryAdd(key, text);
                }
                catch (Exception exception)
                {
                    error ??= $"{exception.GetType().Name}: {exception.Message} at {start}";
                }
            }

            if (map.Count == 0)
            {
                error ??= "the .resources streams held no strings";
                return null;
            }

            return map;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return null;
        }
    }

    /// <summary>
    /// A manifest resource's offset does not always land on the .resources magic (Roslyn leaves four bytes
    /// before it): probe the first bytes of the slice for the signature instead of trusting the alignment.
    /// </summary>
    private static long AlignToResourceMagic(byte[] bytes, long start, long stop)
    {
        for (var offset = start; offset + 4 <= stop && offset < start + 16; offset++)
        {
            if (bytes[offset] == 0xCE && bytes[offset + 1] == 0xCA &&
                bytes[offset + 2] == 0xEF && bytes[offset + 3] == 0xBE)
                return offset;
        }

        return start;
    }

    private static long RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + size) continue;
            return section.PointerToRawData + (long)(rva - section.VirtualAddress);
        }

        return -1;
    }

    /// <summary>One attribute as read, before the analyzer link and the description are resolved.</summary>
    private sealed record RawId(
        string Id,
        string Languages,
        string? Category,
        string? ResourceName,
        string HighlightingType,
        string Assembly,
        string File);

    /// <summary>A type name as the attribute decoder carries it.</summary>
    private readonly struct TypeName : IEquatable<TypeName>
    {
        public TypeName(string name) => Name = name;

        public string Name { get; }

        public bool Equals(TypeName other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is TypeName other && Equals(other);

        public override int GetHashCode() => Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name);

        public override string ToString() => Name ?? "";
    }

    private sealed class TypeNameProvider(string host) : ICustomAttributeTypeProvider<TypeName>
    {
        public TypeName GetPrimitiveType(PrimitiveTypeCode typeCode) => new(typeCode.ToString());

        public TypeName GetSystemType() => new("System.Type");

        public TypeName GetSZArrayType(TypeName elementType) => new(elementType.Name + "[]");

        public TypeName GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => new(FullName(reader, handle));

        public TypeName GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => new(TypeReferenceName(reader, handle));

        public TypeName GetTypeFromSerializedName(string name)
        {
            var comma = name.IndexOf(',', StringComparison.Ordinal);
            return new(comma < 0 ? name : name[..comma]);
        }

        /// <summary>
        /// An enum argument's width decides how the rest of the blob is read: JetBrains' enums are not all
        /// Int32-backed, so the real underlying type is resolved from the type's <c>value__</c> field.
        /// </summary>
        public PrimitiveTypeCode GetUnderlyingEnumType(TypeName type) => ResolveEnumUnderlying(type.Name, host);

        public bool IsSystemType(TypeName type) => string.Equals(type.Name, "System.Type", StringComparison.Ordinal);
    }

    /// <summary>The underlying primitive of each enum argument type, resolved once per process.</summary>
    private static readonly ConcurrentDictionary<string, PrimitiveTypeCode> EnumUnderlying =
        new(StringComparer.Ordinal);

    private static PrimitiveTypeCode ResolveEnumUnderlying(string fullName, string host)
    {
        if (EnumUnderlying.TryGetValue(fullName, out var known)) return known;

        var resolved = PrimitiveTypeCode.Int32;
        var simple = fullName.Contains('+', StringComparison.Ordinal)
            ? fullName[(fullName.LastIndexOf('+') + 1)..]
            : fullName[(fullName.LastIndexOf('.') + 1)..];

        foreach (var file in Directory.EnumerateFiles(host, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;

                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.TypeDefinitions)
                {
                    if (!reader.StringComparer.Equals(reader.GetTypeDefinition(handle).Name, simple)) continue;
                    if (!string.Equals(FullName(reader, handle), fullName, StringComparison.Ordinal)) continue;

                    resolved = UnderlyingOf(reader, reader.GetTypeDefinition(handle));
                    EnumUnderlying[fullName] = resolved;
                    return resolved;
                }
            }
            catch (Exception)
            {
                // Keep looking: the enum may be defined in another assembly.
            }
        }

        EnumUnderlying[fullName] = resolved;
        return resolved;
    }

    private static PrimitiveTypeCode UnderlyingOf(MetadataReader reader, TypeDefinition type)
    {
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            if (!reader.StringComparer.Equals(field.Name, "value__")) continue;

            var blob = reader.GetBlobReader(field.Signature);
            blob.ReadSignatureHeader();
            return (PrimitiveTypeCode)blob.ReadByte();
        }

        return PrimitiveTypeCode.Int32;
    }
}
