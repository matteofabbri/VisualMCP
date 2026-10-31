using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// One shipped set of Roslyn analyzers that stands in for a family of Rider/ReSharper inspections.
/// The assemblies travel next to the server (see the csproj), so any solution gets inspected even
/// when it references no analyzer itself.
/// </summary>
public sealed record InspectionPack(
    string Id,
    string PackageId,
    string[] FilePrefixes,
    string Description);

/// <summary>A pack whose assemblies were found, plus the analyzers and code fixes they expose for C#.</summary>
internal sealed record LoadedInspectionPack(
    InspectionPack Pack,
    ImmutableArray<DiagnosticAnalyzer> Analyzers,
    ImmutableArray<CodeFixProvider> Fixers);

/// <summary>What the loader managed to load, and why anything else failed.</summary>
internal sealed record InspectionPackLibrary(
    IReadOnlyList<LoadedInspectionPack> Packs,
    IReadOnlyList<string> Warnings)
{
    public static readonly InspectionPackLibrary Empty =
        new(Array.Empty<LoadedInspectionPack>(), Array.Empty<string>());
}

internal static class InspectionPackCatalog
{
    /// <summary>Packs used when the caller does not name any.</summary>
    public const string DefaultPacks = "house,roslynator,netanalyzers,asyncfixer";

    /// <summary>
    /// The inspection families shipped with the server. Roslynator is the closest open analogue of
    /// ReSharper's C# inspections; the .NET analyzers add the CA design/reliability rules; the
    /// others cover async pitfalls, correctness and style.
    /// </summary>
    public static readonly IReadOnlyList<InspectionPack> Definitions = new[]
    {
        new InspectionPack("house", "VisualMCP", Array.Empty<string>(),
            "VisualMCP's own house rules (VMC): one type per file."),
        new InspectionPack("rider", "VisualMCP", Array.Empty<string>(),
            "ReSharper-shaped inspections implemented natively under ReSharper's ids: convert to const, loop " +
            "variable changed inside the loop, member can be static, redundant argument, empty constructor, " +
            "redundant if/else, always-true/false condition, redundant base() call."),
        new InspectionPack("roslynator", "Roslynator.Analyzers", new[] { "Roslynator." },
            "ReSharper-style inspections: redundant code, simplifications, language usage (RCS)."),
        new InspectionPack("netanalyzers", "Microsoft.CodeAnalysis.NetAnalyzers",
            new[] { "Microsoft.CodeAnalysis.NetAnalyzers", "Microsoft.CodeAnalysis.CSharp.NetAnalyzers" },
            "Microsoft CA rules: design, globalization, performance, reliability, security, usage."),
        new InspectionPack("asyncfixer", "AsyncFixer", new[] { "AsyncFixer" },
            "Async/await pitfalls: sync-over-async, fire-and-forget, async void (ASYNC)."),
        new InspectionPack("meziantou", "Meziantou.Analyzer", new[] { "Meziantou.Analyzer" },
            "Correctness, performance and security rules (MA)."),
        new InspectionPack("stylecop", "StyleCop.Analyzers", new[] { "StyleCop.Analyzers" },
            "Style and formatting rules (SA); off by default in the SDK, so they need enableAll."),
    };

    private static readonly Lazy<InspectionPackLibrary> Cache =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every pack that could be loaded in this process (loaded once, on first use).</summary>
    public static InspectionPackLibrary Library => Cache.Value;

    public static InspectionPack? Find(string id) =>
        Definitions.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Instantiates every concrete <see cref="CodeFixProvider"/> in one analyzer assembly. Reflection is
    /// required because Roslyn exposes fixers neither on <see cref="AnalyzerFileReference"/> nor through
    /// a package manifest; an assembly without fixers simply yields none.
    /// </summary>
    private static IEnumerable<CodeFixProvider> LoadFixers(
        string assemblyPath, IAnalyzerAssemblyLoader loader, string packId, List<string> warnings)
    {
        System.Reflection.Assembly assembly;
        try
        {
            assembly = loader.LoadFromPath(assemblyPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"{packId}: {Path.GetFileName(assemblyPath)} could not be loaded for code fixes " +
                         $"({ex.GetType().Name}: {ex.Message}).");
            yield break;
        }

        System.Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(type => type is not null).ToArray()!;
        }
        catch (Exception ex)
        {
            warnings.Add($"{packId}: types in {Path.GetFileName(assemblyPath)} could not be inspected " +
                         $"({ex.GetType().Name}: {ex.Message}).");
            yield break;
        }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || !type.IsPublic) continue;
            if (!typeof(CodeFixProvider).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(System.Type.EmptyTypes) is null) continue;

            CodeFixProvider? fixer = null;
            try
            {
                fixer = (CodeFixProvider?)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                warnings.Add($"{packId}: fixer {type.Name} could not be created ({ex.GetType().Name}).");
            }

            if (fixer is not null) yield return fixer;
        }
    }

    private static InspectionPackLibrary Load()
    {
        var warnings = new List<string>();
        var packs = new List<LoadedInspectionPack>();
        var shipped = Path.Combine(AppContext.BaseDirectory, "analyzers");

        foreach (var definition in Definitions)
        {
            // The house rules and the ReSharper-shaped inspections are compiled into this assembly;
            // every other pack is an external analyzer package discovered on disk or in the NuGet cache.
            if (definition.Id is "house" or "rider")
            {
                var own = definition.Id == "house"
                    ? ImmutableArray.Create<DiagnosticAnalyzer>(new OneTypePerFileAnalyzer())
                    : ImmutableArray.Create<DiagnosticAnalyzer>(
                        new Rider.ConvertToConstantLocalAnalyzer(),
                        new Rider.LoopVariableChangedAnalyzer(),
                        new Rider.MemberCanBeMadeStaticAnalyzer(),
                        new Rider.RedundantArgumentAnalyzer(),
                        new Rider.EmptyConstructorAnalyzer(),
                        new Rider.RedundantIfElseAnalyzer(),
                        new Rider.ConstantConditionAnalyzer(),
                        new Rider.RedundantBaseConstructorCallAnalyzer());

                packs.Add(new LoadedInspectionPack(definition, own, ImmutableArray<CodeFixProvider>.Empty));
                continue;
            }

            var assemblies = Discover(definition, shipped);
            if (assemblies.Count == 0)
            {
                warnings.Add($"{definition.Id}: assemblies not found (looked in {shipped} and in the NuGet cache " +
                             $"for '{definition.PackageId}').");
                continue;
            }

            var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
            var fixers = ImmutableArray.CreateBuilder<CodeFixProvider>();
            var loader = new AnalyzerAssemblyLoader();

            foreach (var assembly in assemblies)
            {
                try
                {
                    var reference = new AnalyzerFileReference(assembly, loader);
                    analyzers.AddRange(reference.GetAnalyzers(LanguageNames.CSharp));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{definition.Id}: {Path.GetFileName(assembly)} could not be loaded " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
                }

                // Fixers ship in separate assemblies inside the same package (…CodeFixes.dll /
                // …CodeFixers.dll). Roslyn 5.x exposes no fixer enumeration on AnalyzerFileReference,
                // so the concrete CodeFixProvider types are created reflectively.
                fixers.AddRange(LoadFixers(assembly, loader, definition.Id, warnings));
            }

            if (analyzers.Count == 0)
            {
                warnings.Add($"{definition.Id}: assemblies loaded but exposed no C# analyzer.");
                continue;
            }

            packs.Add(new LoadedInspectionPack(definition, analyzers.ToImmutable(), fixers.ToImmutable()));
        }

        return new InspectionPackLibrary(packs, warnings);
    }

    /// <summary>
    /// Ships-first: a user (or a release layout) can drop DLLs into <c>&lt;app&gt;/analyzers</c>.
    /// Otherwise the package is read from the NuGet global folder, where PackageDownload put it.
    /// </summary>
    private static List<string> Discover(InspectionPack pack, string shippedDirectory)
    {
        if (Directory.Exists(shippedDirectory))
        {
            var shipped = Directory.EnumerateFiles(shippedDirectory, "*.dll")
                .Where(file => pack.FilePrefixes.Any(prefix =>
                    Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (shipped.Count > 0) return shipped;
        }

        var package = NewestPackageVersion(pack.PackageId);
        if (package is null) return new List<string>();

        var folder = BestAnalyzerFolder(Path.Combine(package, "analyzers", "dotnet"));
        return folder is null ? new List<string>() : Directory.EnumerateFiles(folder, "*.dll").ToList();
    }

    /// <summary>
    /// Packages ship either <c>analyzers/dotnet/cs</c> or one folder per supported Roslyn version
    /// (<c>roslyn4.7</c>, <c>roslyn5.9</c>, …). Take the newest variant the host Roslyn can run.
    /// </summary>
    private static string? BestAnalyzerFolder(string analyzersRoot)
    {
        if (!Directory.Exists(analyzersRoot)) return null;

        var plain = Path.Combine(analyzersRoot, "cs");
        if (Directory.Exists(plain)) return plain;

        var host = typeof(Compilation).Assembly.GetName().Version ?? new Version(4, 0);
        var candidates = Directory.EnumerateDirectories(analyzersRoot)
            .Select(dir => (Dir: dir, Version: ParseRoslynVersion(Path.GetFileName(dir))))
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .ToList();

        if (candidates.Count == 0) return null;

        var compatible = candidates
            .Where(candidate => candidate.Version! <= host)
            .Select(candidate => Path.Combine(candidate.Dir, "cs"))
            .FirstOrDefault(PathExists);

        return compatible ?? Path.Combine(candidates[0].Dir, "cs");
    }

    private static bool PathExists(string path) => Directory.Exists(path);

    private static Version? ParseRoslynVersion(string folderName) =>
        folderName.StartsWith("roslyn", StringComparison.OrdinalIgnoreCase) &&
        Version.TryParse(folderName["roslyn".Length..], out var version)
            ? version
            : null;

    private static string? NewestPackageVersion(string packageId)
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var packageFolder = Path.Combine(root, packageId.ToLowerInvariant());
        if (!Directory.Exists(packageFolder)) return null;

        return Directory.EnumerateDirectories(packageFolder)
            .Select(dir => (Dir: dir, Version: ParseVersion(Path.GetFileName(dir))))
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Dir)
            .FirstOrDefault();
    }

    private static Version? ParseVersion(string folderName)
    {
        var core = folderName.Split('-')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    /// <summary>
    /// Roslyn's own loading hook. Analyzers are plain assemblies that reference the Roslyn API the
    /// host already has loaded, so <see cref="Assembly.LoadFrom"/> binds them to that copy.
    /// </summary>
    private sealed class AnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath)
        {
            // Single-assembly packs: dependencies resolve from the same folder via LoadFromPath.
        }

        public Assembly LoadFromPath(string fullPath)
        {
            lock (_loaded)
            {
                if (_loaded.TryGetValue(fullPath, out var loaded)) return loaded;

                var assembly = Assembly.LoadFrom(fullPath);
                _loaded[fullPath] = assembly;
                return assembly;
            }
        }
    }
}
