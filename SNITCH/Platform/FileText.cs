using System.Text;

namespace Snitch.Platform;

/// <summary>
/// The one place that writes a text file: it normalises the line endings to the file's own convention
/// (or, for a new file, the repository's) and keeps the byte-order mark the file already had. Every
/// service that writes source, markup or configuration goes through here, so a tool can never turn a
/// CRLF file into an LF one by accident — the complaint that started this.
/// </summary>
public static class FileText
{
    /// <summary>Writes the file with normalised newlines and the existing BOM convention.</summary>
    public static async Task WriteAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeFor(path, text);
        await File.WriteAllTextAsync(path, normalized, EncodingFor(path), cancellationToken);
    }

    /// <summary>
    /// Writes with an explicit style, ignoring what the file already uses — the migration path. Without
    /// this overload the writer would put the text back to the file's own convention.
    /// </summary>
    public static async Task WriteAsync(string path, string text, NewLineStyle style, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTo(text, style);
        await File.WriteAllTextAsync(path, normalized, EncodingFor(path), cancellationToken);
    }

    public static void Write(string path, string text) =>
        File.WriteAllText(path, NormalizeFor(path, text), EncodingFor(path));

    /// <summary>Normalises the text for the file it will be written to.</summary>
    public static string NormalizeFor(string path, string text) =>
        LineEndings.Normalize(text, LineEndings.ForFile(path));

    /// <summary>Normalises to an explicit style, ignoring the file (used by the migration tool).</summary>
    public static string NormalizeTo(string text, NewLineStyle style) => LineEndings.Normalize(text, style);

    /// <summary>True when the file on disk starts with a UTF-8 BOM.</summary>
    public static bool HasBom(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
            var head = new byte[3];
            var read = stream.Read(head, 0, 3);
            return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Encoding EncodingFor(string path) => new UTF8Encoding(HasBom(path));
}
