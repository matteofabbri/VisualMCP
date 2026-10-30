using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// The module version id of a managed assembly: the identity a decompilation is cached under. Two files
/// with the same MVID have the same source, which is what lets both the persisted export and the metacode
/// index recognise "this binary was already decompiled" without running ILSpy again.
/// </summary>
public static class AssemblyMvid
{
    /// <summary>The MVID in the store's canonical (dash-less) form, or null when the file is not a readable PE.</summary>
    public static string? TryRead(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var metadata = peReader.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N");
        }
        catch (Exception)
        {
            return null;
        }
    }
}
