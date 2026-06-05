using System.Text;

namespace VisualMCP.Implementation.Memory;

/// <summary>
/// Reads/writes the agent's persistent memory files for the current project,
/// living in %USERPROFILE%\.claude\projects\&lt;slug&gt;\memory\ (MEMORY.md index +
/// topic .md files). Lets the plugin own memory instead of the editor's
/// sensitive-file write path.
/// </summary>
internal static class MemoryImpl
{
    internal static object Read(string? file, string? projectPath)
    {
        var dir = ResolveDir(projectPath);

        if (!string.IsNullOrWhiteSpace(file))
        {
            var path = FilePath(dir, file);
            if (path is null) return new { error = "Invalid file name." };
            if (!File.Exists(path)) return new { memoryDir = dir, file = Path.GetFileName(path), exists = false };
            try { return new { memoryDir = dir, file = Path.GetFileName(path), exists = true, content = File.ReadAllText(path) }; }
            catch (Exception ex) { return new { error = $"Failed to read: {ex.Message}" }; }
        }

        // No file: return the index + the list of memory files (with content).
        var files = ListFiles(dir);
        string? index = null;
        var indexPath = Path.Combine(dir, "MEMORY.md");
        if (File.Exists(indexPath)) try { index = File.ReadAllText(indexPath); } catch { }

        return new
        {
            memoryDir = dir,
            exists = Directory.Exists(dir),
            index,
            fileCount = files.Count,
            files = files.Select(f => new
            {
                name = Path.GetFileName(f),
                sizeBytes = SafeLen(f),
                content = SafeRead(f, 20_000),
            }).ToList(),
        };
    }

    internal static object Write(string file, string content, string? projectPath)
    {
        var dir = ResolveDir(projectPath);
        var path = FilePath(dir, file);
        if (path is null) return new { error = "Invalid file name." };
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, content ?? "");
            return new { memoryDir = dir, file = Path.GetFileName(path), written = true, bytes = new FileInfo(path).Length };
        }
        catch (Exception ex) { return new { error = $"Failed to write: {ex.Message}", path }; }
    }

    internal static object Append(string file, string content, string? projectPath)
    {
        var dir = ResolveDir(projectPath);
        var path = FilePath(dir, file);
        if (path is null) return new { error = "Invalid file name." };
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            if (File.Exists(path)) { var existing = File.ReadAllText(path); sb.Append(existing); if (existing.Length > 0 && !existing.EndsWith('\n')) sb.Append('\n'); }
            sb.Append(content ?? "");
            File.WriteAllText(path, sb.ToString());
            return new { memoryDir = dir, file = Path.GetFileName(path), appended = true, bytes = new FileInfo(path).Length };
        }
        catch (Exception ex) { return new { error = $"Failed to append: {ex.Message}", path }; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    internal static string ResolveDir(string? projectPath)
    {
        var root = string.IsNullOrWhiteSpace(projectPath) ? Directory.GetCurrentDirectory() : Path.GetFullPath(projectPath);
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var slug = root.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", slug, "memory");
        return baseDir;
    }

    private static List<string> ListFiles(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static string? FilePath(string dir, string file)
    {
        var name = Path.GetFileName(file.Trim());        // strip any path components (no traversal)
        if (string.IsNullOrEmpty(name)) return null;
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        return Path.Combine(dir, name);
    }

    private static long SafeLen(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }
    private static string? SafeRead(string f, int max) { try { var s = File.ReadAllText(f); return s.Length > max ? s[..max] + "…(truncated)" : s; } catch { return null; } }
}
