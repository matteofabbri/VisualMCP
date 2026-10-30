using Snitch.Languages.CSharp.Reflection;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Composite operations that chain "locate the package DLL in the NuGet cache"
/// with "inspect" or "decompile" — so callers pass a package id + version
/// instead of first finding the .dll path themselves.
/// </summary>
/// <remarks>
/// It lives in <c>Snitch.Metacode</c> because its decompile half is the ILSpy decompiler: a service in
/// <c>Snitch</c> could not reference it without a project cycle. The registration followed the file into
/// <c>AddSnitchMetacode()</c>.
/// </remarks>
public interface IPackageInspectionService
{
    object InspectPackage(string packageId, string? version, string? targetFramework, string? typeFilter, bool includeMembers, int maxTypes);
    object DecompilePackageType(string packageId, string? version, string typeFullName, string? targetFramework, int maxChars, bool compact = true);
}

public sealed class PackageInspectionService : IPackageInspectionService
{
    private readonly IInspectAssemblyService _inspect;
    private readonly IDecompileService _decompile;

    public PackageInspectionService(IInspectAssemblyService inspect, IDecompileService decompile)
    {
        _inspect = inspect;
        _decompile = decompile;
    }

    public object InspectPackage(string packageId, string? version, string? targetFramework, string? typeFilter, bool includeMembers, int maxTypes)
    {
        var (dll, _, _, error) = ResolveDll(packageId, version, targetFramework);
        if (error is not null) return error;
        return _inspect.Run(dll!, typeFilter, includeMembers, maxTypes);
    }

    public object DecompilePackageType(string packageId, string? version, string typeFullName, string? targetFramework, int maxChars, bool compact = true)
    {
        var (dll, _, _, error) = ResolveDll(packageId, version, targetFramework);
        if (error is not null) return error;
        return _decompile.DecompileType(dll!, typeFullName, maxChars, compact);
    }

    /// <summary>Locate a package's primary lib assembly in the NuGet cache.</summary>
    private static (string? dll, string? version, string? tfm, object? error) ResolveDll(string packageId, string? version, string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return (null, null, null, new { error = "A package id is required." });

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var pkgDir = Path.Combine(nugetRoot, packageId.Trim().ToLowerInvariant());
        if (!Directory.Exists(pkgDir))
            return (null, null, null, new { error = $"Package '{packageId}' is not in the NuGet cache. Add or restore the package first." });

        var versions = Directory.EnumerateDirectories(pkgDir).Select(Path.GetFileName).Where(n => n is not null).ToList()!;
        if (versions.Count == 0)
            return (null, null, null, new { error = $"No cached versions for '{packageId}'." });

        string chosenVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            chosenVersion = version.Trim();
            if (!versions.Contains(chosenVersion, StringComparer.OrdinalIgnoreCase))
                return (null, null, null, new { error = $"Version '{version}' not cached. Available: {string.Join(", ", versions)}" });
        }
        else
        {
            // Pick the highest cached version (best-effort numeric sort).
            chosenVersion = versions.OrderBy(v => System.Version.TryParse(v.Split('-')[0], out var pv) ? pv : new System.Version(0, 0), Comparer<System.Version>.Default)
                                    .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                                    .Last();
        }

        var libDir = Path.Combine(pkgDir, chosenVersion, "lib");
        if (!Directory.Exists(libDir))
            return (null, chosenVersion, null, new { error = $"Package '{packageId}' {chosenVersion} has no 'lib' folder (it may be metadata/analyzer-only)." });

        var tfmDirs = Directory.EnumerateDirectories(libDir).Select(Path.GetFileName).Where(n => n is not null).ToList()!;
        if (tfmDirs.Count == 0)
            return (null, chosenVersion, null, new { error = $"No target-framework folders under lib for '{packageId}' {chosenVersion}." });

        string chosenTfm;
        if (!string.IsNullOrWhiteSpace(targetFramework) && tfmDirs.Contains(targetFramework, StringComparer.OrdinalIgnoreCase))
            chosenTfm = tfmDirs.First(t => t.Equals(targetFramework, StringComparison.OrdinalIgnoreCase));
        else
            chosenTfm = PickBestTfm(tfmDirs);

        var tfmPath = Path.Combine(libDir, chosenTfm);
        var dlls = Directory.GetFiles(tfmPath, "*.dll");
        if (dlls.Length == 0)
            return (null, chosenVersion, chosenTfm, new { error = $"No .dll under lib/{chosenTfm} for '{packageId}' {chosenVersion}." });

        // Prefer the assembly matching the package id, else the first.
        var dll = dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d).Equals(packageId, StringComparison.OrdinalIgnoreCase)) ?? dlls[0];
        return (dll, chosenVersion, chosenTfm, null);
    }

    private static string PickBestTfm(List<string> tfms)
    {
        // Prefer modern net (net8.0/net9.0/net10.0…) > netstandard > netcoreapp > netN (framework).
        int Score(string t)
        {
            var l = t.ToLowerInvariant();
            if (l.StartsWith("net") && l.Length > 3 && char.IsDigit(l[3]) && l.Contains('.')) return 400; // net5.0+
            if (l.StartsWith("netstandard")) return 300;
            if (l.StartsWith("netcoreapp")) return 200;
            if (l.StartsWith("net")) return 100; // net48 etc.
            return 0;
        }
        return tfms.OrderByDescending(Score).ThenByDescending(t => t, StringComparer.OrdinalIgnoreCase).First();
    }
}
