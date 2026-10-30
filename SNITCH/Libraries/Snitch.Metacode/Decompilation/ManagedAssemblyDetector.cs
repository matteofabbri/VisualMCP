namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Tells a managed assembly (a PE with a CLI header) from a native image, so decompilation can refuse
/// machine code with an explanation instead of handing it to ILSpy.
/// </summary>
public static class ManagedAssemblyDetector
{
    /// <summary>
    /// True when the PE has a non-empty COM-descriptor data directory (the CLI header) — i.e.
    /// it carries .NET metadata. Non-PE files and native images return false.
    /// </summary>
    public static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40) return false;
            if (reader.ReadUInt16() != 0x5A4D) return false;             // 'MZ'

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 0x18 + 0x70 > stream.Length) return false;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return false;         // 'PE\0\0'

            stream.Position = peOffset + 0x18;
            var magic = reader.ReadUInt16();                              // 0x20b = PE32+
            var directories = peOffset + 0x18 + (magic == 0x20b ? 0x70 : 0x60);
            if (directories + 15L * 8 > stream.Length) return false;

            stream.Position = directories + 14 * 8;                       // IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR
            return reader.ReadUInt32() != 0;
        }
        catch
        {
            return false;
        }
    }
}
