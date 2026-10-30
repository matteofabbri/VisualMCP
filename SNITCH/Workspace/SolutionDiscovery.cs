namespace Snitch.Workspace;

/// <summary>
/// Locates solution files (<c>.sln</c>/<c>.slnx</c>) on disk. Shared by
/// <see cref="Snitch.SnitchContext"/> and the IO tools so discovery behaves identically
/// everywhere. Built on the common <see cref="FileDiscovery"/> walker.
/// </summary>
public static class SolutionDiscovery
{
    /// <summary>
    /// Enumerates every solution file beneath <paramref name="root"/> as absolute paths,
    /// pruning build-output/VCS noise directories and de-duplicating across the
    /// <c>.sln</c>/<c>.slnx</c> patterns. Inaccessible folders are skipped, never thrown.
    /// </summary>
    public static IEnumerable<string> Enumerate(string root, int maxDepth = 24) =>
        FileDiscovery.Walk(root, "*.sln", maxDepth)
            .Concat(FileDiscovery.Walk(root, "*.slnx", maxDepth))
            // On Windows "*.sln" also matches ".slnx" (the 3-char-extension wildcard rule),
            // so keep only exact extensions and drop the resulting duplicates.
            .Where(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
