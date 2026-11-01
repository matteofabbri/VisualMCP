using System.IO;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Both installers (Ghidra, JADX) unpack a release into the system temp folder and then move it into the
/// tools folder. <see cref="Directory.Move"/> refuses to cross volumes, and the temp folder and the tools
/// folder are on different drives more often than not (a machine that redirects its temp, or one that
/// keeps its repos and its tools on another disk): this is the move that always works.
/// </summary>
internal static class ToolInstall
{
    public static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException)
        {
            // "Source and destination path must have identical roots": copy instead. Any other IO
            // failure surfaces from Copy, and the caller reports it as the install failing.
        }

        Copy(source, target);
        Directory.Delete(source, recursive: true);
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
