using System.Collections.Concurrent;
using System.Text.Json;

namespace Snitch.Languages.JavaScript;

/// <summary>
/// The path rules every JavaScript tool shares: which extensions a script is written with, where a
/// specifier points, and which folders are generated rather than source.
/// </summary>
internal static class JavaScriptPaths
{
    private static readonly string[] ExcludedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build", "out",
        "coverage", ".next", ".nuxt", ".svelte-kit", "target", "vendor",
    ];

    /// <summary>The source extensions a relative import may omit, in the order TypeScript resolves them.</summary>
    private static readonly string[] ResolutionSuffixes =
        [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    /// <summary>The <c>package.json</c> fields that name an entry point, most specific first.</summary>
    private static readonly string[] ManifestFields = ["types", "typings", "module", "main"];

    /// <summary>File text keyed on path and write time, so two imports of one package read it once.</summary>
    private static readonly ConcurrentDictionary<string, string?> TextCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TypeScript allows comments and trailing commas in its JSON, so the reader does too.</summary>
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The file a specifier points at, whatever its kind: <c>./x</c> against the importing file,
    /// <c>@/x</c> through the nearest <c>tsconfig.json</c> <c>baseUrl</c>/<c>paths</c>, and <c>pkg/x</c>
    /// through <c>node_modules</c>. Null when nothing on disk answers it: a runtime alias the scanner
    /// cannot see (a bundler plugin, an import map) is not guessed.
    /// </summary>
    public static string? Resolve(string fromFile, string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return null;
        if (JavaScriptAnalyzer.IsRelativeSpecifier(specifier)) return ResolveRelative(fromFile, specifier);
        if (specifier.StartsWith("node:", StringComparison.Ordinal) ||
            specifier.StartsWith("http:", StringComparison.Ordinal) ||
            specifier.StartsWith("https:", StringComparison.Ordinal) ||
            specifier.StartsWith("data:", StringComparison.Ordinal))
            return null;

        return ResolveMapped(fromFile, specifier) ?? ResolvePackage(fromFile, specifier);
    }

    /// <summary>
    /// The file a relative specifier points at: the path itself, the path with a source extension, a
    /// directory index, or the TypeScript source behind a compiled <c>.js</c> specifier. Null when none exists.
    /// </summary>
    public static string? ResolveRelative(string fromFile, string specifier)
    {
        var directory = Path.GetDirectoryName(fromFile);
        if (directory is null) return null;

        var target = Path.GetFullPath(Path.Combine(directory, specifier.Replace('/', Path.DirectorySeparatorChar)));
        return ResolveCandidate(target);
    }

    /// <summary>
    /// The aliases the nearest <c>tsconfig.json</c> declares: <c>compilerOptions.baseUrl</c> and
    /// <c>compilerOptions.paths</c>, with the one wildcard TypeScript allows in a pattern and a target.
    /// A mapped target is tried in declaration order and the first that exists wins, as in TypeScript.
    /// </summary>
    private static string? ResolveMapped(string fromFile, string specifier)
    {
        var config = FindUp(Path.GetDirectoryName(fromFile), "tsconfig.json");
        if (config is null) return null;

        var text = ReadCached(config);
        if (text is null) return null;

        try
        {
            using var document = JsonDocument.Parse(text, Lenient);
            if (!document.RootElement.TryGetProperty("compilerOptions", out var options) ||
                options.ValueKind != JsonValueKind.Object)
                return null;

            if (!options.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                return null;

            var configDirectory = Path.GetDirectoryName(config)!;
            var baseUrl = options.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;
            var baseDirectory = string.IsNullOrWhiteSpace(baseUrl)
                ? configDirectory
                : Path.GetFullPath(Path.Combine(configDirectory, baseUrl!.Replace('/', Path.DirectorySeparatorChar)));

            foreach (var pattern in paths.EnumerateObject())
            {
                var capture = MatchPattern(pattern.Name, specifier);
                if (capture is null || pattern.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var target in pattern.Value.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.String) continue;
                    var mapped = target.GetString()!.Replace("*", capture);
                    var candidate = Path.GetFullPath(
                        Path.Combine(baseDirectory, mapped.Replace('/', Path.DirectorySeparatorChar)));
                    var resolved = ResolveCandidate(candidate);
                    if (resolved is not null) return resolved;
                }
            }
        }
        catch (JsonException) { }

        return null;
    }

    /// <summary>The wildcard capture when a path pattern matches, or null. The empty string is a match.</summary>
    private static string? MatchPattern(string pattern, string specifier)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return string.Equals(pattern, specifier, StringComparison.Ordinal) ? string.Empty : null;

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        if (specifier.Length < prefix.Length + suffix.Length) return null;
        if (!specifier.StartsWith(prefix, StringComparison.Ordinal) ||
            !specifier.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        return specifier[prefix.Length..(specifier.Length - suffix.Length)];
    }

    /// <summary>
    /// A package specifier through the <c>node_modules</c> folders above the importing file:
    /// <c>pkg/sub/path</c> is a file in the package, and <c>pkg</c> is its <c>types</c>/<c>typings</c>/
    /// <c>module</c>/<c>main</c> entry, its directory <c>index</c>, or its directory. Null when nothing exists.
    /// </summary>
    private static string? ResolvePackage(string fromFile, string specifier)
    {
        var parts = specifier.Split('/');
        string packageName;
        string[] rest;
        if (specifier.StartsWith('@'))
        {
            if (parts.Length < 2) return null;
            packageName = parts[0] + "/" + parts[1];
            rest = parts[2..];
        }
        else
        {
            packageName = parts[0];
            rest = parts[1..];
        }

        if (packageName.Length == 0) return null;

        for (var directory = Path.GetDirectoryName(fromFile);
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            var packageDirectory = Path.Combine(
                directory, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(packageDirectory)) continue;

            if (rest.Length > 0)
            {
                var subpath = Path.Combine(packageDirectory, string.Join('/', rest).Replace('/', Path.DirectorySeparatorChar));
                var resolved = ResolveCandidate(subpath);
                if (resolved is not null) return resolved;
                continue;
            }

            var manifest = ReadCached(Path.Combine(packageDirectory, "package.json"));
            if (manifest is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(manifest, Lenient);
                    foreach (var field in ManifestFields)
                    {
                        if (!document.RootElement.TryGetProperty(field, out var entry) ||
                            entry.ValueKind != JsonValueKind.String)
                            continue;

                        var candidate = Path.GetFullPath(Path.Combine(
                            packageDirectory, entry.GetString()!.Replace('/', Path.DirectorySeparatorChar)));
                        var resolved = ResolveCandidate(candidate);
                        if (resolved is not null) return resolved;
                    }
                }
                catch (JsonException) { }
            }

            var index = ResolveCandidate(packageDirectory);
            if (index is not null) return index;
        }

        return null;
    }

    /// <summary>
    /// A path with the extension rules applied: the file itself, the TypeScript source behind a compiled
    /// <c>.js</c> specifier, a source extension, or a directory <c>index</c>.
    /// </summary>
    private static string? ResolveCandidate(string target)
    {
        if (File.Exists(target)) return target;

        // A TypeScript project imports './x.js' although the source on disk is './x.ts'.
        if (target.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            target.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.ChangeExtension(target, null);
            foreach (var suffix in ResolutionSuffixes)
                if (File.Exists(stem + suffix))
                    return stem + suffix;
        }

        foreach (var suffix in ResolutionSuffixes)
            if (File.Exists(target + suffix))
                return target + suffix;

        foreach (var suffix in ResolutionSuffixes)
        {
            var index = Path.Combine(target, "index" + suffix);
            if (File.Exists(index)) return index;
        }

        return null;
    }

    /// <summary>The nearest ancestor directory that holds <paramref name="name"/>, starting at <paramref name="directory"/>.</summary>
    private static string? FindUp(string? directory, string name)
    {
        for (var depth = 0; directory is not null && depth < 64; depth++)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static string? ReadCached(string path)
    {
        string key;
        try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return TextCache.GetOrAdd(key, _ =>
        {
            try { return File.ReadAllText(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    /// <summary>The path relative to the scan root, with forward slashes, which is how answers are keyed.</summary>
    public static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    public static string SnippetAt(string text, int line)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 1 || line > lines.Length) return string.Empty;
        var snippet = lines[line - 1].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }

    /// <summary>Every script under the root, in a stable order, with the generated folders pruned.</summary>
    public static (string Root, List<string> Files, bool Truncated) Discover(string root, int maxFiles)
    {
        var files = new List<string>();
        var truncated = false;

        if (File.Exists(root))
        {
            files.Add(root);
            return (Path.GetDirectoryName(root) ?? root, files, false);
        }

        if (!Directory.Exists(root)) return (root, files, false);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".artifacts", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            string[] entries;
            try { entries = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (!JavaScriptAnalyzer.IsScript(entry)) continue;
                if (files.Count >= maxFiles) { truncated = true; break; }
                files.Add(entry);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (root, files, truncated);
    }
}
