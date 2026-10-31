using System.Xml.Linq;

namespace Snitch.Languages.Xaml;

/// <summary>Which XAML dialect a project writes. The dialects disagree on syntax (`x:Bind`,
/// `x:Double`, `ControlTemplate.TargetType`), so a rule that needs a dialect says which one it assumed.</summary>
public enum XamlDialect
{
    Unknown,
    Wpf,
    WinUi,
    Maui,
    Avalonia,
    Uwp,
}

/// <summary>
/// What a project is, read from its own build files: the dialect, the target frameworks it declares
/// (MAUI multi-targets android/ios/maccatalyst/windows) and the evidence the dialect was decided on.
/// Every signal is a string a reader can check against the csproj, so a wrong answer is visible
/// instead of mysterious.
/// </summary>
public sealed class XamlProjectFacts
{
    public required string Name { get; init; }
    public string? ProjectFile { get; init; }
    public XamlDialect Dialect { get; init; }

    /// <summary>The signals the dialect was inferred from, e.g. "UseMaui=true" or "file *.axaml".</summary>
    public List<string> Signals { get; } = [];

    /// <summary>
    /// The frameworks the project declares, as written: one entry for TargetFramework, one per entry
    /// of TargetFrameworks. Conditions are not evaluated — what the file says is what is reported.
    /// </summary>
    public List<string> TargetFrameworks { get; } = [];

    public bool MultiTargeted => TargetFrameworks.Count > 1;

    /// <summary>True when the dialect compiles <c>{x:Bind}</c> (WinUI 3 and UWP); false elsewhere.</summary>
    public bool SupportsCompiledBinding => Dialect is XamlDialect.WinUi or XamlDialect.Uwp;

    public string DialectName => Dialect switch
    {
        XamlDialect.Wpf => "WPF",
        XamlDialect.WinUi => "WinUI 3",
        XamlDialect.Maui => ".NET MAUI",
        XamlDialect.Avalonia => "Avalonia",
        XamlDialect.Uwp => "UWP",
        _ => "unknown",
    };

    /// <summary>The platform suffixes a MAUI project multi-targets (android, ios, maccatalyst, windows).</summary>
    public List<string> Platforms => TargetFrameworks
        .SelectMany(framework => framework.Split('-', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        .Where(platform => platform is "android" or "ios" or "maccatalyst" or "windows")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public object ToJson() => new
    {
        name = Name,
        projectFile = ProjectFile,
        dialect = DialectName,
        dialectId = Dialect.ToString().ToLowerInvariant(),
        supportsCompiledBinding = SupportsCompiledBinding,
        targetFrameworks = TargetFrameworks,
        multiTargeted = MultiTargeted,
        platforms = Platforms.Count > 0 ? Platforms : null,
        signals = Signals,
    };
}

/// <summary>
/// Reads the dialect and the target frameworks out of the project file (and the
/// <c>Directory.Build.props</c> files above it, where UseWPF/UseMaui commonly live). It reads the XML
/// the SDK reads, it does not evaluate MSBuild conditions: a framework or a property behind a
/// <c>Condition</c> is reported as written, and the signals say what was found.
/// </summary>
public static class XamlProjects
{
    private const int MaxParents = 6;

    /// <summary>
    /// The facts for a project directory. <paramref name="hasAxaml"/> is the evidence a file gives
    /// when the project file cannot be read (an <c>.axaml</c> file is Avalonia by construction).
    /// </summary>
    public static XamlProjectFacts ForDirectory(string? directory, string name, bool hasAxaml = false)
    {
        var projectFile = directory is null ? null : FindProjectFile(directory);
        var signals = new List<string>();
        var frameworks = new List<string>();

        if (projectFile is null)
        {
            if (hasAxaml)
            {
                signals.Add("file *.axaml");
                return Create(name, null, XamlDialect.Avalonia, signals, frameworks);
            }

            return Create(name, null, XamlDialect.Unknown, signals, frameworks);
        }

        var documents = BuildDocuments(projectFile);
        foreach (var document in documents)
            CollectFrameworks(document, frameworks);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<string>();
        foreach (var document in documents)
        {
            CollectProperties(document, properties);
            CollectPackages(document, packages);
        }

        foreach (var package in packages)
        {
            if (package.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
                package.StartsWith("Microsoft.Maui", StringComparison.OrdinalIgnoreCase) ||
                package.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase))
                signals.Add($"PackageReference {package}");
        }

        if (Flag(properties, "UseMaui")) signals.Add("UseMaui=true");
        if (Flag(properties, "UseWPF")) signals.Add("UseWPF=true");
        if (Flag(properties, "UseWinUI")) signals.Add("UseWinUI=true");
        if (properties.TryGetValue("TargetPlatformIdentifier", out var platformIdentifier))
            signals.Add($"TargetPlatformIdentifier={platformIdentifier}");
        if (hasAxaml) signals.Add("file *.axaml");

        var dialect = Decide(properties, packages, frameworks, hasAxaml, signals);
        return Create(name, projectFile, dialect, signals, frameworks);
    }

    private static XamlProjectFacts Create(string name, string? projectFile, XamlDialect dialect,
        List<string> signals, List<string> frameworks)
    {
        var facts = new XamlProjectFacts { Name = name, ProjectFile = projectFile, Dialect = dialect };
        facts.Signals.AddRange(signals.Where(signal => !facts.Signals.Contains(signal)).Distinct(StringComparer.Ordinal));
        facts.TargetFrameworks.AddRange(frameworks.Distinct(StringComparer.OrdinalIgnoreCase));
        return facts;
    }

    private static XamlDialect Decide(IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<string> packages, IReadOnlyList<string> frameworks, bool hasAxaml, List<string> signals)
    {
        if (Flag(properties, "UseMaui") ||
            packages.Any(package => package.StartsWith("Microsoft.Maui", StringComparison.OrdinalIgnoreCase)))
            return XamlDialect.Maui;

        if (Flag(properties, "UseWinUI") ||
            packages.Any(package => package.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)))
            return XamlDialect.WinUi;

        if (Flag(properties, "UseWPF"))
            return XamlDialect.Wpf;

        if (packages.Any(package => package.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)) || hasAxaml)
            return XamlDialect.Avalonia;

        if ((properties.TryGetValue("TargetPlatformIdentifier", out var identifier) &&
             identifier.Equals("UAP", StringComparison.OrdinalIgnoreCase)) ||
            frameworks.Any(framework => framework.StartsWith("uap", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("UWP target platform");
            return XamlDialect.Uwp;
        }

        return XamlDialect.Unknown;
    }

    /// <summary>The project's own csproj plus the Directory.Build.props chain above it.</summary>
    private static List<XDocument> BuildDocuments(string projectFile)
    {
        var documents = new List<XDocument>();

        var directory = Path.GetDirectoryName(projectFile);
        for (var level = 0; directory is not null && level < MaxParents; level++)
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props)) TryAdd(documents, props);

            var parent = Path.GetDirectoryName(directory);
            if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)) break;
            directory = parent;
        }

        TryAdd(documents, projectFile);
        return documents;
    }

    private static void TryAdd(List<XDocument> documents, string path)
    {
        try { documents.Add(XDocument.Load(path)); }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // A build file that does not parse is the build's problem, not the dialect scan's.
        }
    }

    private static void CollectFrameworks(XDocument document, List<string> frameworks)
    {
        foreach (var element in document.Descendants())
        {
            var name = element.Name.LocalName;
            if (name is not ("TargetFramework" or "TargetFrameworks")) continue;

            foreach (var framework in element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
                    frameworks.Add(framework);
        }
    }

    private static void CollectProperties(XDocument document, Dictionary<string, string> properties)
    {
        foreach (var element in document.Descendants())
        {
            if (element.HasElements) continue;
            if (element.Name.LocalName is "TargetFramework" or "TargetFrameworks") continue;

            // A property set unconditionally is what the dialect check reads; a conditioned one is
            // still recorded, because "UseWPF only in Debug" is not a signal worth losing either.
            properties.TryAdd(element.Name.LocalName, element.Value.Trim());
        }
    }

    private static void CollectPackages(XDocument document, List<string> packages)
    {
        foreach (var reference in document.Descendants().Where(element =>
                     element.Name.LocalName is "PackageReference" or "PackageVersion"))
        {
            var id = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !packages.Contains(id, StringComparer.OrdinalIgnoreCase))
                packages.Add(id.Trim());
        }
    }

    private static bool Flag(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest .csproj at or above a directory.</summary>
    public static string? FindProjectFile(string directory)
    {
        var current = directory;
        for (var level = 0; current is not null && level < MaxParents; level++)
        {
            string? best = null;
            try
            {
                best = Directory.GetFiles(current, "*.csproj")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (best is not null) return best;

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        return null;
    }
}
