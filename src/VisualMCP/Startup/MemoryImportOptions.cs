namespace VisualMCP.Startup;

/// <summary>
/// Where the memory of this project can come from, where it is carried to, and the size limits.
/// </summary>
/// <remarks>
/// Persistent memory belongs to the client, and every client keeps it in its own place: Claude Code under
/// <c>~/.claude/projects/&lt;slug&gt;/memory</c> (the slug being the project path with ':' and every separator
/// folded to '-'), ZCode under <c>~/.zcode/cli/memories/projects/&lt;name&gt;-&lt;id&gt;/memory</c>. Earlier VisualMCP
/// builds wrote into the Claude Code store, so notes can exist there while the client reading them now is a
/// different one — this import carries the notes across so they are not stranded.
/// <c>VISUALMCP_MEMORY_IMPORT=off</c> disables the pass.
/// </remarks>
public sealed record MemoryImportOptions
{
    /// <summary>False when <c>VISUALMCP_MEMORY_IMPORT</c> is off/0/false/no.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The user profile root holding the <c>.claude</c> and <c>.zcode</c> folders.</summary>
    public string HomeDirectory { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The project the memory belongs to; its folder name identifies the client's memory store.</summary>
    public string ProjectRoot { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Upper bound on the notes copied by one pass.</summary>
    public int MaxFiles { get; init; } = 200;

    /// <summary>A note larger than this is reported instead of copied.</summary>
    public long MaxFileBytes { get; init; } = 512 * 1024;

    /// <summary>Reads the environment overrides for <paramref name="projectRoot"/>.</summary>
    public static MemoryImportOptions FromEnvironment(string projectRoot)
    {
        var flag = Environment.GetEnvironmentVariable("VISUALMCP_MEMORY_IMPORT");
        var enabled = !(string.Equals(flag, "off", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(flag, "0", StringComparison.Ordinal)
                     || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(flag, "no", StringComparison.OrdinalIgnoreCase));

        return new MemoryImportOptions { Enabled = enabled, ProjectRoot = projectRoot };
    }

    /// <summary>
    /// The directory name Claude Code gives a project path: every ':' and separator becomes '-', so
    /// <c>D:\REPOSITORY\VisualMCP</c> is <c>D--REPOSITORY-VisualMCP</c>.
    /// </summary>
    public static string ClaudeSlug(string projectRoot)
    {
        var root = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return root.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
    }
}
