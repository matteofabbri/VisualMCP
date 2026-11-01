using Snitch.Workspace;

namespace Snitch.Languages.JavaScript;

/// <summary>
/// Symbol navigation over the solution's JavaScript and TypeScript: the declarations a file makes, the
/// declaration a name resolves to, the references to it (inside its module and through the imports other
/// modules take from it), and the declarations nothing reads.
/// </summary>
public interface IJavaScriptSymbolService
{
    /// <summary>outline | find | refs | unused.</summary>
    Task<object> SymbolsAsync(string action, string? name, string? file, string? path, bool partial, int maxResults);
}

public sealed class JavaScriptSymbolService : IJavaScriptSymbolService
{
    private readonly IRoslynWorkspace _workspace;

    public JavaScriptSymbolService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> SymbolsAsync(
        string action, string? name, string? file, string? path, bool partial, int maxResults)
    {
        action = string.IsNullOrWhiteSpace(action) ? "find" : action.Trim().ToLowerInvariant();
        if (action is not ("outline" or "find" or "refs" or "unused"))
            return new { error = "action must be outline, find, refs or unused." };

        maxResults = Math.Clamp(maxResults, 1, 5_000);
        var root = ResolveRoot(path);
        var (scanRoot, files, truncated) = JavaScriptPaths.Discover(root, 2_000);
        var index = await BuildAsync(scanRoot, files, truncated);

        return action switch
        {
            "outline" => Outline(index, file, maxResults),
            "find" => Find(index, name, partial, maxResults),
            "refs" => References(index, name, file, maxResults),
            _ => Unused(index, maxResults),
        };
    }

    // ── the four questions ───────────────────────────────────────────────────────────

    private static object Outline(Index index, string? file, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(file))
            return new { error = "action=outline needs a file (absolute, or relative to the scan root)." };

        var path = ResolveFile(index.Root, file);
        if (path is null || !index.Files.TryGetValue(path, out var symbols))
            return new
            {
                error = $"'{file}' is not a script of the scan.",
                scanned = index.Files.Count,
                hint = "Pass a path under the solution, or a `path` to scan from another folder.",
            };

        var uses = CountUses(symbols);
        var declarations = symbols.Declarations
            .Select((declaration, position) => new
            {
                name = declaration.Name,
                kind = declaration.Kind.ToString().ToLowerInvariant(),
                line = declaration.Line,
                column = declaration.Column,
                container = declaration.Container,
                exportedAs = declaration.ExportedAs,
                uses = uses.TryGetValue(position, out var count) ? count : 0,
            })
            .Take(maxResults)
            .ToList();

        return new
        {
            action = "outline",
            file = JavaScriptPaths.Relative(index.Root, symbols.Path),
            lines = symbols.Lines,
            typeScript = symbols.TypeScript,
            jsx = symbols.Jsx,
            declarationCount = symbols.Declarations.Count,
            declarations,
            truncated = symbols.Declarations.Count > declarations.Count,
            imports = symbols.Imports.Select(binding => new
            {
                local = binding.Local,
                imported = binding.Imported,
                module = binding.Module,
                line = binding.Line,
                resolved = ResolveModule(symbols.Path, binding.Module) is not null,
            }).ToList(),
            exports = symbols.Exports,
            note = "A lexical table: a name resolves to the declaration in the innermost enclosing scope, " +
                   "and an imported name resolves through its binding. A member reached through an object " +
                   "the scan did not type is not resolved.",
        };
    }

    private static object Find(Index index, string? name, bool partial, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new { error = "action=find needs a name." };

        var matches = new List<object>();
        var total = 0;
        foreach (var symbols in index.Files.Values)
        {
            var uses = CountUses(symbols);
            for (var position = 0; position < symbols.Declarations.Count; position++)
            {
                var declaration = symbols.Declarations[position];
                var hit = partial
                    ? declaration.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(declaration.Name, name, StringComparison.Ordinal);
                if (!hit) continue;

                total++;
                if (matches.Count >= maxResults) continue;

                var external = ExternalReferences(index, symbols.Path, declaration).Count();
                matches.Add(new
                {
                    name = declaration.Name,
                    kind = declaration.Kind.ToString().ToLowerInvariant(),
                    file = JavaScriptPaths.Relative(index.Root, symbols.Path),
                    line = declaration.Line,
                    column = declaration.Column,
                    container = declaration.Container,
                    exportedAs = declaration.ExportedAs,
                    internalReferences = uses.TryGetValue(position, out var count) ? count : 0,
                    externalReferences = external,
                });
            }
        }

        return new
        {
            action = "find",
            query = name,
            partial,
            matchCount = total,
            matches,
            truncated = total > matches.Count,
            note = total == 0
                ? "No declaration carries that name in the scanned files. A property reached through an " +
                  "untyped object is not a declaration the scan can see."
                : "internalReferences counts the uses in the declaring module; externalReferences the uses " +
                  "in the modules that import the name.",
        };
    }

    private static object References(Index index, string? name, string? file, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new { error = "action=refs needs a name." };

        var declaringFile = file is null ? null : ResolveFile(index.Root, file);
        var candidates = new List<(JsFileSymbols Symbols, int Position)>();
        foreach (var symbols in index.Files.Values)
        {
            if (declaringFile is not null &&
                !string.Equals(symbols.Path, declaringFile, StringComparison.OrdinalIgnoreCase))
                continue;

            for (var position = 0; position < symbols.Declarations.Count; position++)
                if (string.Equals(symbols.Declarations[position].Name, name, StringComparison.Ordinal))
                    candidates.Add((symbols, position));
        }

        if (candidates.Count == 0)
            return new
            {
                error = $"No declaration named '{name}'{(declaringFile is null ? string.Empty : $" in {file}")}.",
                hint = "Use action=find with partial=true to check the spelling.",
            };

        // The export is the symbol a reader means; the importing module's own binding is the same name
        // seen from the other side, so it is only reported when nothing else declares it.
        if (declaringFile is null && candidates.Any(candidate =>
                candidate.Symbols.Declarations[candidate.Position].Kind != JsSymbolKind.Import))
            candidates.RemoveAll(candidate =>
                candidate.Symbols.Declarations[candidate.Position].Kind == JsSymbolKind.Import);

        var results = new List<object>();
        foreach (var (owner, position) in candidates.Take(10))
        {
            var declaration = owner.Declarations[position];
            var references = new List<object>();

            foreach (var use in owner.Uses.Where(use => use.Declaration == position).Take(maxResults))
            {
                references.Add(new
                {
                    file = JavaScriptPaths.Relative(index.Root, owner.Path),
                    line = use.Line,
                    column = use.Column,
                    kind = use.Kind,
                    via = (string?)null,
                });
            }
            foreach (var (importing, use, binding) in ExternalReferences(index, owner.Path, declaration))
            {
                if (references.Count >= maxResults) break;
                references.Add(new
                {
                    file = JavaScriptPaths.Relative(index.Root, importing.Path),
                    line = use.Line,
                    column = use.Column,
                    kind = use.Kind,
                    via = string.Equals(binding.Imported, binding.Local, StringComparison.Ordinal)
                        ? binding.Module
                        : $"{binding.Module} ({binding.Imported} as {binding.Local})",
                });
            }

            results.Add(new
            {
                name = declaration.Name,
                kind = declaration.Kind.ToString().ToLowerInvariant(),
                file = JavaScriptPaths.Relative(index.Root, owner.Path),
                declaration.Line,
                declaration.Column,
                declaration.Container,
                exportedAs = declaration.ExportedAs,
                referenceCount = references.Count,
                references,
            });
        }

        return new
        {
            action = "refs",
            query = name,
            symbolCount = candidates.Count,
            symbols = results,
            truncated = candidates.Count > results.Count,
            note = "A reference is a name occurrence that resolves to this declaration — through the module's " +
                   "own scopes, or through an import that the other module's specifier resolves to. A write " +
                   "is an occurrence in an assignment position; a call is one followed by '('.",
        };
    }

    private static object Unused(Index index, int maxResults)
    {
        var candidates = new List<object>();

        foreach (var symbols in index.Files.Values)
        {
            var uses = CountUses(symbols);
            for (var position = 0; position < symbols.Declarations.Count; position++)
            {
                var declaration = symbols.Declarations[position];
                if (declaration.Kind is JsSymbolKind.Import or JsSymbolKind.Parameter) continue;

                var internalReferences = uses.TryGetValue(position, out var count) ? count : 0;
                var exported = declaration.ExportedAs.Count > 0;
                var external = exported ? ExternalReferences(index, symbols.Path, declaration).Count() : 0;

                if (internalReferences > 0 || external > 0) continue;

                candidates.Add(new
                {
                    name = declaration.Name,
                    kind = declaration.Kind.ToString().ToLowerInvariant(),
                    file = JavaScriptPaths.Relative(index.Root, symbols.Path),
                    line = declaration.Line,
                    column = declaration.Column,
                    container = declaration.Container,
                    exported,
                    note = exported
                        ? "Exported but no scanned module imports it and nothing in its module reads it."
                        : "Not exported and never read in its module.",
                });
            }
        }

        var shown = candidates.Take(maxResults).ToList();
        return new
        {
            action = "unused",
            filesScanned = index.Files.Count,
            truncatedScan = index.Truncated,
            candidateCount = candidates.Count,
            candidates = shown,
            truncated = candidates.Count > shown.Count,
            note = "Candidates, not proof: a name reached through an untyped object, a string, a reflection " +
                   "call or a framework convention is not a reference the scan can see, and an entry point " +
                   "is read by the runtime rather than by a module.",
        };
    }

    // ── the index ────────────────────────────────────────────────────────────────────

    private sealed record Index(string Root, Dictionary<string, JsFileSymbols> Files, bool Truncated);

    private async Task<Index> BuildAsync(string root, List<string> files, bool truncated)
    {
        var scanned = new Dictionary<string, JsFileSymbols>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2_000_000) continue;
                var text = await File.ReadAllTextAsync(file);
                scanned[Path.GetFullPath(file)] = JavaScriptSymbols.Scan(text, file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new Index(root, scanned, truncated);
    }

    private static Dictionary<int, int> CountUses(JsFileSymbols symbols)
    {
        var counts = new Dictionary<int, int>();
        foreach (var use in symbols.Uses)
        {
            if (use.Declaration < 0) continue;
            counts[use.Declaration] = counts.TryGetValue(use.Declaration, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// The uses in other modules that reach this declaration through an import: the module specifier is
    /// resolved to the declaring file, the imported name to the declaration's exported name, and the local
    /// binding to the uses that resolve to it.
    /// </summary>
    private static IEnumerable<(JsFileSymbols File, JsSymbolUse Use, JsImportBinding Binding)> ExternalReferences(
        Index index, string declaringFile, JsSymbolDeclaration declaration)
    {
        if (declaration.ExportedAs.Count == 0) yield break;

        foreach (var importing in index.Files.Values)
        {
            if (string.Equals(importing.Path, declaringFile, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var binding in importing.Imports)
            {
                if (!declaration.ExportedAs.Contains(binding.Imported, StringComparer.Ordinal)) continue;

                var resolved = ResolveModule(importing.Path, binding.Module);
                if (resolved is null ||
                    !string.Equals(resolved, declaringFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                var local = Bindings(importing, binding.Local);
                foreach (var use in importing.Uses.Where(use => use.Declaration == local))
                    yield return (importing, use, binding);
            }
        }
    }

    private static int Bindings(JsFileSymbols symbols, string local)
    {
        for (var position = 0; position < symbols.Declarations.Count; position++)
            if (symbols.Declarations[position].Kind == JsSymbolKind.Import &&
                string.Equals(symbols.Declarations[position].Name, local, StringComparison.Ordinal))
                return position;
        return -1;
    }

    private static string? ResolveModule(string fromFile, string module) =>
        JavaScriptAnalyzer.IsRelativeSpecifier(module)
            ? JavaScriptPaths.ResolveRelative(fromFile, module) is { } resolved ? Path.GetFullPath(resolved) : null
            : null;

    // ── paths ────────────────────────────────────────────────────────────────────────

    private string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solutionPath = _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return Path.GetDirectoryName(solutionPath)!;

        return Directory.GetCurrentDirectory();
    }

    private static string? ResolveFile(string root, string file)
    {
        if (Path.IsPathRooted(file)) return File.Exists(file) ? Path.GetFullPath(file) : null;
        var candidate = Path.GetFullPath(Path.Combine(root, file));
        return File.Exists(candidate) ? candidate : null;
    }
}
