using System.Text.RegularExpressions;

namespace Snitch.Languages.CSharp.Modernization;

/// <summary>
/// Text-level edits for the files that pin a .NET version: a project file, a
/// <c>Directory.Packages.props</c> and <c>global.json</c>.
///
/// The elements are edited in place — only the value between the tags changes — because rewriting a
/// project file through an XML DOM reformats every attribute and turns a one-line version bump into a
/// whole-file diff. The same reason the refactoring writers keep trivia: a version bump should look
/// like a version bump.
/// </summary>
public static class ProjectFiles
{
    /// <summary>One changed property/value, as the tool reports it.</summary>
    public sealed record TfmEdit(string Property, string From, string To);

    private static readonly Regex TfmElement = new(
        "<(?<tag>TargetFrameworks?)>(?<value>[^<]*)</\\k<tag>>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DotNetTfm = new(
        @"^net(?<major>\d+)\.(?<minor>\d+)(?<suffix>-[a-z][a-z0-9.]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ── target frameworks ─────────────────────────────────────────────────────

    /// <summary>Every value of <c>TargetFramework</c>/<c>TargetFrameworks</c> in a project file.</summary>
    public static IReadOnlyList<string> ReadTargetFrameworks(string projectFileText)
    {
        var values = new List<string>();
        foreach (Match match in TfmElement.Matches(projectFileText))
            foreach (var part in match.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!values.Contains(part, StringComparer.OrdinalIgnoreCase))
                    values.Add(part);

        return values;
    }

    /// <summary>
    /// Bumps every <c>netX.Y</c> target to <paramref name="targetFramework"/>. Targets that are not a
    /// modern .NET TFM are left alone on purpose: <c>netstandard2.0</c> and <c>net48</c> exist to keep a
    /// library usable from older consumers, and rewriting them would change who can reference it.
    /// </summary>
    public static bool TryBumpTargetFrameworks(
        string projectFileText, string targetFramework, out string updated, out IReadOnlyList<TfmEdit> edits)
    {
        var changes = new List<TfmEdit>();
        var result = TfmElement.Replace(projectFileText, match =>
        {
            var property = match.Groups["tag"].Value;
            var original = match.Groups["value"].Value;
            var parts = original.Split(';');
            var rewritten = new List<string>(parts.Length);
            var propertyChanges = new List<TfmEdit>();

            foreach (var part in parts)
            {
                var value = part.Trim();
                var bumped = BumpTargetFramework(value, targetFramework);
                if (bumped is null)
                {
                    rewritten.Add(part);
                    continue;
                }

                propertyChanges.Add(new TfmEdit(property, value, bumped));
                rewritten.Add(bumped);
            }

            if (propertyChanges.Count == 0) return match.Value;

            changes.AddRange(propertyChanges);
            return match.Value.Replace(original, string.Join(";", rewritten));
        });

        edits = changes;
        updated = result;
        return changes.Count > 0;
    }

    /// <summary>
    /// The new spelling of one TFM, or <c>null</c> when it should stay as it is. A platform suffix is
    /// kept (<c>net8.0-windows</c> → <c>net10.0-windows</c>); a suffix on the target overrides it, so
    /// "move everything to net10.0-windows" is expressible.
    /// </summary>
    public static string? BumpTargetFramework(string tfm, string targetFramework)
    {
        var current = ParseDotNetTfm(tfm);
        var target = ParseDotNetTfm(targetFramework);
        if (current is null || target is null) return null;
        if ((current.Value.Major, current.Value.Minor).CompareTo((target.Value.Major, target.Value.Minor)) >= 0) return null;

        var suffix = target.Value.Suffix ?? current.Value.Suffix ?? "";
        return $"net{target.Value.Major}.{target.Value.Minor}{suffix}";
    }

    public static (int Major, int Minor, string? Suffix)? ParseDotNetTfm(string tfm)
    {
        var match = DotNetTfm.Match((tfm ?? "").Trim());
        if (!match.Success) return null;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);

        // Modern .NET starts at 5; `net48`/`net4.8` are .NET Framework spellings and `net48.0` is not
        // a TFM at all. Bounding the major keeps those out of the "bump" path instead of treating 48
        // as a newer target than 10.
        if (major is < 5 or > 39) return null;

        return (major, minor,
                match.Groups["suffix"].Success ? match.Groups["suffix"].Value.ToLowerInvariant() : null);
    }

    /// <summary>
    /// Accepts what a caller naturally types: <c>net10.0</c>, <c>net10</c>, <c>10.0</c>, and the
    /// platform-specific forms. Normalised to <c>net&lt;major&gt;.&lt;minor&gt;[-suffix]</c>.
    /// </summary>
    public static bool TryNormalizeTargetFramework(string? input, out string normalized, out string? error)
    {
        var text = (input ?? "").Trim().ToLowerInvariant();
        error = null;
        normalized = "";

        if (text.Length == 0)
        {
            error = "A target framework is required, e.g. 'net10.0' or 'auto'.";
            return false;
        }

        if (Regex.IsMatch(text, @"^\d+\.\d+(-[a-z][a-z0-9.]*)?$")) text = "net" + text;
        else if (Regex.IsMatch(text, @"^net\d+$")) text += ".0";

        var parsed = ParseDotNetTfm(text);
        if (parsed is null)
        {
            error = $"'{input}' is not a modern .NET target framework. Use a form like 'net10.0' or 'net10.0-windows'.";
            return false;
        }

        normalized = $"net{parsed.Value.Major}.{parsed.Value.Minor}{parsed.Value.Suffix}";
        return true;
    }

    /// <summary>The TFM an installed SDK band targets: <c>10.0.101</c> → <c>net10.0</c>.</summary>
    public static string? SdkTargetFromVersion(string sdkVersion)
    {
        var core = (sdkVersion ?? "").Split('-')[0];
        var parts = core.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        return major >= 5 ? $"net{major}.{minor}" : null;
    }

    /// <summary>The newest SDK in <c>dotnet --list-sdks</c> output, as a version string.</summary>
    public static string? NewestSdkVersion(string listSdksOutput)
    {
        Version? best = null;
        string? bestText = null;

        foreach (Match match in Regex.Matches(listSdksOutput, @"(?m)^\s*(?<version>\d+\.\d+\.\d+[^\s\[\]]*)\s+\[", RegexOptions.CultureInvariant).Cast<Match>())
        {
            var text = match.Groups["version"].Value;
            var core = text.Split('-')[0];
            if (!Version.TryParse(core, out var version)) continue;
            if (best is null || version > best) { best = version; bestText = text; }
        }

        return bestText;
    }

    // ── global.json ──────────────────────────────────────────────────────────

    /// <summary>The <c>sdk.version</c> pinned by a global.json, when there is one.</summary>
    public static string? ReadGlobalJsonSdkVersion(string globalJson)
    {
        var sdk = Regex.Match(globalJson, "\"sdk\"\\s*:\\s*\\{(?<body>[^}]*)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!sdk.Success) return null;
        var version = Regex.Match(sdk.Groups["body"].Value, "\"version\"\\s*:\\s*\"(?<v>[^\"]*)\"", RegexOptions.CultureInvariant);
        return version.Success ? version.Groups["v"].Value : null;
    }

    /// <summary>
    /// Rewrites the pinned SDK version, leaving every other key (notably <c>rollForward</c>) untouched.
    /// Returns false when the file has no sdk block or no version: inserting one would change how the
    /// repository resolves the SDK, which is the caller's decision, not this helper's.
    /// </summary>
    public static bool TryUpdateGlobalJsonSdkVersion(string globalJson, string sdkVersion, out string updated)
    {
        updated = globalJson;
        var sdk = Regex.Match(globalJson, "\"sdk\"\\s*:\\s*\\{(?<body>[^}]*)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!sdk.Success) return false;

        var body = sdk.Groups["body"];
        var version = Regex.Match(body.Value, "\"version\"\\s*:\\s*\"(?<v>[^\"]*)\"", RegexOptions.CultureInvariant);
        if (!version.Success) return false;

        var newBody = string.Concat(
            body.Value.AsSpan(0, version.Groups["v"].Index),
            sdkVersion,
            body.Value.AsSpan(version.Groups["v"].Index + version.Groups["v"].Length));

        updated = string.Concat(globalJson.AsSpan(0, body.Index), newBody, globalJson.AsSpan(body.Index + body.Length));
        return true;
    }

    public static string GlobalJsonTemplate(string sdkVersion) =>
        "{\n" +
        "  \"sdk\": {\n" +
        $"    \"version\": \"{sdkVersion}\",\n" +
        "    \"rollForward\": \"latestFeature\"\n" +
        "  }\n" +
        "}\n";

    // ── package versions ─────────────────────────────────────────────────────

    /// <summary>
    /// Sets the version of one package reference or package version, whichever of the three shapes the
    /// file uses: a <c>Version</c> attribute, a child <c>&lt;Version&gt;</c> element, or the
    /// <c>PackageVersion</c> row of a <c>Directory.Packages.props</c>. Returns false when the element is
    /// there but its version is not, which in Central Package Management is the normal case for a
    /// project file — the caller then edits the props file instead.
    /// </summary>
    public static bool TrySetPackageVersion(
        string text, string packageId, string newVersion, out string updated, string elementName = "PackageReference")
    {
        updated = text;
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(newVersion)) return false;

        var element = Regex.Escape(elementName);
        var openTags = Regex.Matches(text, $"<{element}\\b(?<attrs>[^>]*)>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match tag in openTags.Cast<Match>())
        {
            var attrs = tag.Groups["attrs"];
            var include = AttributeValue(attrs.Value, "Include") ?? AttributeValue(attrs.Value, "Update");
            if (!string.Equals(include, packageId, StringComparison.OrdinalIgnoreCase)) continue;

            var versionAttribute = Regex.Match(attrs.Value, "\\bVersion\\s*=\\s*\"(?<v>[^\"]*)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (versionAttribute.Success)
            {
                var value = versionAttribute.Groups["v"];
                var newAttrs = string.Concat(
                    attrs.Value.AsSpan(0, value.Index), newVersion,
                    attrs.Value.AsSpan(value.Index + value.Length));
                updated = string.Concat(text.AsSpan(0, attrs.Index), newAttrs, text.AsSpan(attrs.Index + attrs.Length));
                return true;
            }

            // A self-closing <PackageReference ... /> has no child element to look into.
            if (attrs.Value.TrimEnd().EndsWith('/')) return false;

            var closeAt = text.IndexOf($"</{elementName}>", tag.Index + tag.Length, StringComparison.OrdinalIgnoreCase);
            if (closeAt < 0) return false;

            var innerStart = tag.Index + tag.Length;
            var inner = text[innerStart..closeAt];
            var child = Regex.Match(inner, "<Version>(?<v>[^<]*)</Version>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!child.Success) return false;

            var newInner = string.Concat(
                inner.AsSpan(0, child.Groups["v"].Index), newVersion,
                inner.AsSpan(child.Groups["v"].Index + child.Groups["v"].Length));
            updated = string.Concat(text.AsSpan(0, innerStart), newInner, text.AsSpan(closeAt));
            return true;
        }

        return false;
    }

    private static string? AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(attributes, $"\\b{Regex.Escape(name)}\\s*=\\s*\"(?<v>[^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>Picks the higher of two NuGet versions, falling back to a string comparison when unparsable.</summary>
    public static string HigherVersion(string left, string right)
    {
        static Version? Core(string value) =>
            Version.TryParse(value.Split('-', '+')[0], out var version) ? version : null;

        var l = Core(left);
        var r = Core(right);
        if (l is not null && r is not null) return r >= l ? right : left;
        return string.CompareOrdinal(right, left) >= 0 ? right : left;
    }

    // ── project shape ────────────────────────────────────────────────────────

    public static bool IsTestProject(string projectFileText) =>
        Regex.IsMatch(projectFileText, @"Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A project that produces a process an AppHost can start.</summary>
    public static bool IsRunnableProject(string projectFileText) =>
        Regex.IsMatch(projectFileText, @"<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        Regex.IsMatch(projectFileText, @"Sdk\s*=\s*""Microsoft\.NET\.Sdk\.(Web|Worker|Razor|BlazorWebAssembly)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Aspire's resource name: lower-case, non-alphanumerics collapsed to a dash.</summary>
    public static string ResourceName(string projectName) =>
        Regex.Replace((projectName ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>The generated <c>Projects</c> member for a referenced project: <c>My.App.Api</c> → <c>Projects.My_App_Api</c>.</summary>
    public static string ProjectsMember(string projectName) =>
        "Projects." + new string((projectName ?? "").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>The nearest file walking up from a directory, or null.</summary>
    public static string? FindUpwards(string startDirectory, string fileName, int maxDepth = 8)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; directory is not null && depth < maxDepth; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
