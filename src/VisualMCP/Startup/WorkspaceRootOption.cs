namespace VisualMCP.Startup;

/// <summary>
/// The workspace this process serves: <c>--root &lt;dir&gt;</c>, then <c>VISUALMCP_ROOT</c>, then the
/// directory the client started the server in. ZCode and Claude Code launch a stdio server per
/// session with the project as its working directory, so the fallback is already right for them;
/// the explicit root is for a host that has no project directory (Claude Desktop, an IDE plugin).
/// </summary>
public static class WorkspaceRootOption
{
    /// <summary>The environment variable a host can use instead of <c>--root</c>.</summary>
    public const string EnvironmentVariable = "VISUALMCP_ROOT";

    /// <summary>The configured root, absolute, or null when the working directory is the answer.</summary>
    /// <exception cref="ArgumentException">The argument is present without a value.</exception>
    /// <exception cref="DirectoryNotFoundException">The path is not an existing directory.</exception>
    public static string? Resolve(string[] args)
    {
        string? configured = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--root", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--root needs a directory: --root <path>.");

                configured = args[i + 1];
                i++;
            }
            else if (args[i].StartsWith("--root=", StringComparison.Ordinal))
            {
                configured = args[i]["--root=".Length..];
            }
        }

        configured ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var full = Path.GetFullPath(configured);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"The workspace root '{full}' does not exist.");

        return full;
    }
}
