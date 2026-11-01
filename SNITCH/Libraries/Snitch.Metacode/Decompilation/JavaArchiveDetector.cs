using System.IO.Compression;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Tells a Java binary — a compiled class, a DEX, or a ZIP container full of them (.jar, .war, .apk,
/// .aar, .aab, …) — from everything else, so decompilation can hand it to JADX instead of to ILSpy
/// (which reads .NET metadata) or Ghidra (which reads machine code).
/// </summary>
public static class JavaArchiveDetector
{
    /// <summary>ZIP containers JADX accepts as a Java program by their extension alone.</summary>
    private static readonly HashSet<string> JavaContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".war", ".ear", ".aar", ".apk", ".aab", ".xapk", ".apkm", ".apks",
    };

    /// <summary>How many ZIP entries are opened while deciding whether a nameless archive holds classes.</summary>
    private const int SniffEntries = 200;

    /// <summary>
    /// True when the file is Java bytecode: the <c>CAFEBABE</c> magic of a class, the <c>dex\n</c> magic
    /// of a Dalvik executable, or a ZIP that carries <c>.class</c> entries / a <c>classes*.dex</c>. A
    /// plain .zip is only accepted when its content says so, because a zip is also how a NuGet package
    /// or an archive of sources travels.
    /// </summary>
    public static bool IsJavaBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[8];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (read < 4) return false;

            // 0xCAFEBABE is both a .class file and the first four bytes of a multi-release JAR, so the
            // magic alone is enough: whatever it is, JADX is the decompiler that reads it.
            if (head[0] == 0xCA && head[1] == 0xFE && head[2] == 0xBA && head[3] == 0xBE) return true;

            if (head[0] == (byte)'d' && head[1] == (byte)'e' && head[2] == (byte)'x' && head[3] == (byte)'\n') return true;

            var zip = head[0] == (byte)'P' && head[1] == (byte)'K'
                      && head[2] is 3 or 5 or 7 && head[3] is 4 or 6 or 8;
            if (!zip) return false;

            var extension = Path.GetExtension(path);
            if (JavaContainers.Contains(extension)) return true;

            stream.Position = 0;
            return CarriesClasses(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Whether a ZIP holds bytecode rather than documents, images or a single serialized payload.</summary>
    private static bool CarriesClasses(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var examined = 0;
        foreach (var entry in archive.Entries)
        {
            if (++examined > SniffEntries) break;

            var name = entry.FullName;
            if (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) return true;
            if (Path.GetFileName(name).StartsWith("classes", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".dex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
