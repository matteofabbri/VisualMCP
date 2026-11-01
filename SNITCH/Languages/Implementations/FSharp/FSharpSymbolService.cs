using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.FSharp;

/// <summary>
/// Symbol navigation over the solution's F# files: the declarations a file makes, the declaration a name
/// resolves to, the references to it (inside its file and in the files that <c>open</c> its module), the
/// declarations nothing reads, and a lexical rename of one declaration with every reference the scan
/// resolved to it. Lexical: F# has no Roslyn model, so a name resolves by position in its file and, across
/// files, only through an <c>open</c> the other file declares.
/// </summary>
public interface IFSharpSymbolService
{
    /// <summary>outline | find | refs | unused | rename.</summary>
    Task<object> SymbolsAsync(string action, string? name, string? file, string? path, bool partial, int maxResults,
        string? newName = null, bool dryRun = true);
}

public sealed class FSharpSymbolService : IFSharpSymbolService
{
    private static readonly string[] ExcludedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build", "out",
        "coverage", "packages", ".paket", ".artifacts",
    ];

    private readonly IRoslynWorkspace _workspace;

    public FSharpSymbolService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> SymbolsAsync(
        string action, string? name, string? file, string? path, bool partial, int maxResults,
        string? newName = null, bool dryRun = true)
    {
        action = string.IsNullOrWhiteSpace(action) ? "find" : action.Trim().ToLowerInvariant();
        if (action is not ("outline" or "find" or "refs" or "unused" or "rename"))
            return new { error = "action must be outline, find, refs, unused or rename." };

        maxResults = Math.Clamp(maxResults, 1, 5_000);
        var root = ResolveRoot(path);
        var (scanRoot, files, truncated) = Discover(root, 2_000);
        var index = await BuildAsync(scanRoot, files, truncated);

        return action switch
        {
            "outline" => Outline(index, file, maxResults),
            "find" => Find(index, name, partial, maxResults),
            "refs" => References(index, name, file, maxResults),
            "rename" => await RenameAsync(index, name, newName, file, dryRun),
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
                error = $"'{file}' is not an F# file of the scan.",
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
                isPublic = declaration.Public,
                uses = uses.TryGetValue(position, out var count) ? count : 0,
            })
            .Take(maxResults)
            .ToList();

        return new
        {
            action = "outline",
            file = Relative(index.Root, symbols.Path),
            lines = symbols.Lines,
            modules = symbols.Modules,
            opens = symbols.Opens,
            declarationCount = symbols.Declarations.Count,
            declarations,
            truncated = symbols.Declarations.Count > declarations.Count,
            note = "A lexical table over the masked source: a declaration is what an indentation-aware line " +
                   "scan can prove, and a name resolves to the declaration of the same name before it in " +
                   "the file. F# has no Roslyn model, so nothing is type-checked.",
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

                matches.Add(new
                {
                    name = declaration.Name,
                    kind = declaration.Kind.ToString().ToLowerInvariant(),
                    file = Relative(index.Root, symbols.Path),
                    line = declaration.Line,
                    column = declaration.Column,
                    container = declaration.Container,
                    isPublic = declaration.Public,
                    internalReferences = uses.TryGetValue(position, out var count) ? count : 0,
                    externalReferences = ExternalReferences(index, symbols, declaration).Count(),
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
                ? "No declaration carries that name in the scanned files."
                : "externalReferences counts the uses in files that `open` the declaring module; a call from " +
                  "an assembly this scan does not read is invisible.",
        };
    }

    private static object References(Index index, string? name, string? file, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new { error = "action=refs needs a name." };

        var declaringFile = file is null ? null : ResolveFile(index.Root, file);
        var candidates = new List<(FSharpFileSymbols Symbols, int Position)>();
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

        var results = new List<object>();
        foreach (var (owner, position) in candidates.Take(10))
        {
            var declaration = owner.Declarations[position];
            var references = new List<object>();

            foreach (var use in owner.Uses.Where(use => use.Declaration == position).Take(maxResults))
            {
                references.Add(new
                {
                    file = Relative(index.Root, owner.Path),
                    line = use.Line,
                    column = use.Column,
                    kind = use.Kind,
                    via = (string?)null,
                });
            }

            foreach (var (opening, use) in ExternalReferences(index, owner, declaration))
            {
                if (references.Count >= maxResults) break;
                references.Add(new
                {
                    file = Relative(index.Root, opening.Path),
                    line = use.Line,
                    column = use.Column,
                    kind = use.Kind,
                    via = $"open {declaration.Container}",
                });
            }

            results.Add(new
            {
                name = declaration.Name,
                kind = declaration.Kind.ToString().ToLowerInvariant(),
                file = Relative(index.Root, owner.Path),
                line = declaration.Line,
                column = declaration.Column,
                container = declaration.Container,
                isPublic = declaration.Public,
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
            note = "A reference is a name occurrence that resolves to this declaration: before it in the same " +
                   "file, or in another file that opens the declaring module and declares no name of its own.",
        };
    }

    private static object Unused(Index index, int maxResults)
    {
        var candidates = new List<object>();
        var allUses = index.Files.Values.SelectMany(symbols => symbols.Uses).ToList();

        foreach (var symbols in index.Files.Values)
        {
            var uses = CountUses(symbols);
            for (var position = 0; position < symbols.Declarations.Count; position++)
            {
                var declaration = symbols.Declarations[position];
                if (declaration.Kind is FSharpSymbolKind.Module or FSharpSymbolKind.Field or FSharpSymbolKind.UnionCase)
                    continue;

                var internalReferences = uses.TryGetValue(position, out var count) ? count : 0;
                if (internalReferences > 0) continue;

                var external = ExternalReferences(index, symbols, declaration).Count();
                if (external > 0) continue;

                // A name that appears anywhere without resolving may still be a use the scan cannot follow,
                // and that is not a dead declaration.
                var unresolved = allUses.Count(use => use.Declaration < 0 &&
                                                     string.Equals(use.Name, declaration.Name, StringComparison.Ordinal));
                if (unresolved > 0) continue;

                candidates.Add(new
                {
                    name = declaration.Name,
                    kind = declaration.Kind.ToString().ToLowerInvariant(),
                    file = Relative(index.Root, symbols.Path),
                    line = declaration.Line,
                    column = declaration.Column,
                    container = declaration.Container,
                    note = declaration.Public
                        ? "Public, and no scanned file reads it."
                        : "Private and never read in its file.",
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
            note = "Candidates, not proof: a member called by the compiled assembly, a name built at run time " +
                   "and a use reached through a module abbreviation are not occurrences the scan can see.",
        };
    }

    // ── rename ───────────────────────────────────────────────────────────────────────
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "assert", "base", "begin", "class", "default", "delegate", "do", "done",
        "downcast", "downto", "elif", "else", "end", "exception", "extern", "false", "finally", "fixed",
        "for", "fun", "function", "global", "if", "in", "inherit", "inline", "interface", "internal",
        "lazy", "let", "match", "member", "module", "mutable", "namespace", "new", "not", "null", "of",
        "open", "or", "override", "private", "public", "rec", "return", "select", "static", "struct",
        "then", "to", "true", "try", "type", "upcast", "use", "val", "void", "when", "while", "with",
        "yield",
    };

    private static async Task<object> RenameAsync(Index index, string? name, string? newName, string? file,
        bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "action=rename needs a name." };
        if (string.IsNullOrWhiteSpace(newName) || !IsIdentifier(newName) || ReservedNames.Contains(newName))
            return new { error = $"'{newName}' is not an F# identifier." };
        if (string.Equals(name, newName, StringComparison.Ordinal))
            return new { error = "The names are identical." };

        var declaringFile = file is null ? null : ResolveFile(index.Root, file);
        var candidates = new List<(FSharpFileSymbols Symbols, int Position)>();
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
            return new { error = $"No declaration named '{name}'{(declaringFile is null ? string.Empty : $" in {file}")}." };

        if (candidates.Count > 1 && declaringFile is null)
            return new
            {
                error = $"{candidates.Count} declarations are named '{name}'; pass file to choose one.",
                files = candidates.Select(candidate => Relative(index.Root, candidate.Symbols.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };

        var (owner, declarationPosition) = candidates[0];
        var declaration = owner.Declarations[declarationPosition];
        var ownerText = await File.ReadAllTextAsync(owner.Path);

        var edits = new List<(string File, int Offset, int Length, string Text)>
        {
            (owner.Path, PositionOf(ownerText, declaration.Line, declaration.Column),
                declaration.Name.Length, newName),
        };

        foreach (var use in owner.Uses.Where(use => use.Declaration == declarationPosition))
            edits.Add((owner.Path, PositionOf(ownerText, use.Line, use.Column), use.Name.Length, newName));

        foreach (var (opening, use) in ExternalReferences(index, owner, declaration))
        {
            var text = await File.ReadAllTextAsync(opening.Path);
            edits.Add((opening.Path, PositionOf(text, use.Line, use.Column), use.Name.Length, newName));
        }

        var written = new List<object>();
        foreach (var group in edits.GroupBy(edit => edit.File, StringComparer.OrdinalIgnoreCase))
        {
            var original = await File.ReadAllTextAsync(group.Key);
            var text = original;
            foreach (var edit in group.OrderByDescending(edit => edit.Offset))
                text = text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];

            if (!dryRun) await File.WriteAllTextAsync(group.Key, text);

            written.Add(new
            {
                file = Relative(index.Root, group.Key),
                changes = group.Count(),
                preview = RefactoringFileWriter.Diff(original, text),
            });
        }

        return new
        {
            action = "rename",
            name,
            newName,
            symbol = new
            {
                kind = declaration.Kind.ToString().ToLowerInvariant(),
                file = Relative(index.Root, owner.Path),
                line = declaration.Line,
                column = declaration.Column,
                container = declaration.Container,
                isPublic = declaration.Public,
            },
            filesChanged = written.Count,
            changes = edits.Count,
            dryRun,
            applied = !dryRun,
            files = written,
            note = "A lexical rename: the declaration and every reference the scan resolved to it — before it " +
                   "in its file, or in a file that opens the declaring module. There is no compiler gate for " +
                   "F#, so a name reached through a module alias or a type the scan does not model is not renamed.",
        };
    }

    /// <summary>Line/column (1-based) to an index in the file's text; the line scan owns both.</summary>
    private static int PositionOf(string text, int line, int column)
    {
        var currentLine = 1;
        var index = 0;
        while (currentLine < line && index < text.Length)
        {
            if (text[index] == '\n') currentLine++;
            index++;
        }

        return Math.Min(text.Length, index + Math.Max(0, column - 1));
    }

    private static bool IsIdentifier(string text) =>
        text.Length > 0 &&
        (char.IsLetter(text[0]) || text[0] == '_') &&
        text.All(character => char.IsLetterOrDigit(character) || character is '_' or '\'');

    // ── the index ────────────────────────────────────────────────────────────────────

    private sealed record Index(string Root, Dictionary<string, FSharpFileSymbols> Files, bool Truncated);

    private async Task<Index> BuildAsync(string root, List<string> files, bool truncated)
    {
        var scanned = new Dictionary<string, FSharpFileSymbols>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2_000_000) continue;
                var text = await File.ReadAllTextAsync(file);
                scanned[Path.GetFullPath(file)] = FSharpSymbols.Scan(text, file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new Index(root, scanned, truncated);
    }

    private static Dictionary<int, int> CountUses(FSharpFileSymbols symbols)
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
    /// The uses in other files that reach this declaration: a file whose <c>open</c> names the declaring
    /// module (or one of its prefixes) and that declares no name of its own for it.
    /// </summary>
    private static IEnumerable<(FSharpFileSymbols File, FSharpSymbolUse Use)> ExternalReferences(
        Index index, FSharpFileSymbols owner, FSharpSymbolDeclaration declaration)
    {
        foreach (var other in index.Files.Values)
        {
            if (string.Equals(other.Path, owner.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (!OpensModule(other, declaration.Container)) continue;
            if (other.Declarations.Any(candidate =>
                    string.Equals(candidate.Name, declaration.Name, StringComparison.Ordinal)))
                continue;

            foreach (var use in other.Uses.Where(use =>
                         string.Equals(use.Name, declaration.Name, StringComparison.Ordinal)))
                yield return (other, use);
        }
    }

    private static bool OpensModule(FSharpFileSymbols symbols, string module) =>
        symbols.Opens.Any(opened =>
            string.Equals(opened, module, StringComparison.Ordinal) ||
            module.StartsWith(opened + ".", StringComparison.Ordinal) ||
            opened.StartsWith(module + ".", StringComparison.Ordinal));

    // ── paths ────────────────────────────────────────────────────────────────────────

    private string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solutionPath = _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return Path.GetDirectoryName(solutionPath)!;

        return Directory.GetCurrentDirectory();
    }

    private static (string Root, List<string> Files, bool Truncated) Discover(string root, int maxFiles)
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
                if (!FSharpAnalyzer.IsFSharp(entry)) continue;
                if (files.Count >= maxFiles) { truncated = true; break; }
                files.Add(entry);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (root, files, truncated);
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string? ResolveFile(string root, string file)
    {
        if (Path.IsPathRooted(file)) return File.Exists(file) ? Path.GetFullPath(file) : null;
        var candidate = Path.GetFullPath(Path.Combine(root, file));
        return File.Exists(candidate) ? candidate : null;
    }
}
