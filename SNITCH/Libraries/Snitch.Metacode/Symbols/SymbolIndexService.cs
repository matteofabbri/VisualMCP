using System.Diagnostics;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The symbol index as a set of questions: build it, search it, and ask what Visual Studio and ILSpy
/// answer from it — where a member is declared, every place it is used and how, who uses it, what it
/// uses, what extends or implements it, and the outline of a file or a type.
/// </summary>
public interface ISymbolIndexService
{
    /// <summary>Indexes the documents whose content changed since the last build.</summary>
    Task<object> BuildAsync(string? projectName = null, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>What the index holds and how fresh it is.</summary>
    object Status();

    /// <summary>Declarations whose name matches, with how many places use each of them.</summary>
    object Find(string query, string? kind = null, bool partial = false, int limit = 100);

    /// <summary>Everything the index knows about one symbol: its declarations, its uses by kind, its callers.</summary>
    object Describe(string symbol, int limit = 100);

    /// <summary>Every use of a symbol, with the kind of use and the member it happens inside.</summary>
    object References(string symbol, string? kind = null, int limit = 200);

    /// <summary>The members that use a symbol: the "find all references" list grouped by caller.</summary>
    object Callers(string symbol, int limit = 100);

    /// <summary>The symbols a member uses: the other half of the call hierarchy.</summary>
    object Callees(string symbol, int limit = 100);

    /// <summary>What a type extends and implements, and what extends and implements it.</summary>
    object Hierarchy(string symbol, int limit = 100);

    /// <summary>The members of a type, or every declaration in a file, in source order.</summary>
    object Outline(string? symbol = null, string? path = null, int limit = 500);

    /// <summary>
    /// The metacode tree of a declaration — or every tree of a file — read from the index: the same
    /// pseudocode the translator prints, stored per symbol when the document was indexed.
    /// </summary>
    object Metacode(string? symbol = null, string? path = null, int limit = 50);

    /// <summary>Non-public declarations nothing uses.</summary>
    object Unused(int limit = 200, string? kind = null);
}

public sealed class SymbolIndexService : ISymbolIndexService, IDisposable
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore _store;
    private readonly SymbolIndexBuilder _builder;
    private readonly SymbolIndexStore _index = new();

    public SymbolIndexService(IRoslynWorkspace workspace, IVisualMcpStore store)
    {
        _workspace = workspace;
        _store = store;
        _builder = new SymbolIndexBuilder();
    }

    public async Task<object> BuildAsync(
        string? projectName = null, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!TryOpen(out var error)) return error!;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"No project '{projectName}'.", projects = solution.Projects.Select(project => project.Name).ToList() };

        var documents = projects
            .SelectMany(project => project.Documents
                .Where(document => document.FilePath is not null &&
                                   document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(document => (Project: project.Name, Document: document)))
            .ToList();

        var hashes = _index.Hashes();
        var stopwatch = Stopwatch.StartNew();

        int scanned = 0, indexed = 0, skipped = 0, unsupported = 0, declarations = 0, usages = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await Parallel.ForEachAsync(
            documents,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                Interlocked.Increment(ref scanned);
                var document = item.Document;
                var path = document.FilePath!;

                try
                {
                    var hash = await _builder.HashAsync(document, token);
                    if (!force && hashes.TryGetValue(path, out var stored) && string.Equals(stored, hash, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var facts = await _builder.BuildAsync(document, item.Project, hash, token);
                    if (facts is null)
                    {
                        // A language with no index frontend (VB today): counted, never silently dropped.
                        Interlocked.Increment(ref unsupported);
                        return;
                    }

                    await _index.ReplaceAsync(facts, token);
                    Interlocked.Increment(ref indexed);
                    Interlocked.Add(ref declarations, facts.Declarations.Count);
                    Interlocked.Add(ref usages, facts.Usages.Count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Enqueue($"{Path.GetFileName(path)}: {exception.Message}");
                }
            });

        var pruned = projectName is null
            ? await _index.PruneAsync(documents.Select(item => item.Document.FilePath!).ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken)
            : 0;

        var status = _index.Status();
        return new
        {
            scanned,
            indexed,
            skipped,
            unsupported,
            pruned,
            failed = failures.Count,
            failures = failures.Take(5).ToList(),
            declarations,
            usages,
            documents = status.Documents,
            milliseconds = stopwatch.ElapsedMilliseconds,
            database = _index.Path,
            note = skipped > 0
                ? "Unchanged documents were skipped: their stored hash already matches the file."
                : "Every C# document was indexed.",
        };
    }

    public object Status()
    {
        if (!TryOpen(out var error)) return error!;

        var status = _index.Status();
        return new
        {
            database = _index.Path,
            documents = status.Documents,
            symbols = status.Symbols,
            declarations = status.Declarations,
            usages = status.Usages,
            declarationKinds = Rows(status.DeclarationKinds),
            usageKinds = Rows(status.UsageKinds),
            newest = status.Newest,
            note = "Content hashes decide what to rebuild: a document whose hash matches its file is not re-indexed. " +
                   "C# only: a document in another language has no frontend yet.",
        };
    }

    public object Find(string query, string? kind = null, bool partial = false, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "A name to search for is required." };
        if (!TryOpen(out var error)) return error!;

        var declarations = _index.DeclarationsByName(query.Trim(), partial, kind, limit);
        var totals = _index.UsageTotals(declarations.Select(declaration => declaration.Key).Distinct().ToList());

        return new
        {
            query,
            partial,
            kind,
            count = declarations.Count,
            matches = declarations.Select(declaration => new
            {
                key = declaration.Key,
                name = declaration.Name,
                display = declaration.Display,
                kind = declaration.Kind,
                signature = declaration.Signature,
                accessibility = declaration.Accessibility,
                modifiers = declaration.Modifiers,
                summary = declaration.Summary,
                container = declaration.ContainerName,
                project = declaration.Project,
                file = declaration.Path,
                line = declaration.Line,
                column = declaration.Column,
                uses = totals.GetValueOrDefault(declaration.Key),
            }).ToList(),
            note = partial
                ? "Substring search on the declaration name. Uses count every recorded use except the override and implement relationships."
                : "Exact declaration-name search. Use partial=true for a substring search, or action=describe for one symbol in full.",
        };
    }

    public object Describe(string symbol, int limit = 100)
    {
        if (!TryOpen(out var error)) return error!;
        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var symbols = new List<object>();
        foreach (var group in declarations.GroupBy(declaration => declaration.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            symbols.Add(new
            {
                key = group.Key,
                name = first.Name,
                display = first.Display,
                kind = first.Kind,
                signature = first.Signature,
                accessibility = first.Accessibility,
                modifiers = first.Modifiers,
                summary = first.Summary,
                container = first.ContainerName,
                @namespace = first.Namespace,
                project = first.Project,
                isStatic = first.IsStatic,
                isAbstract = first.IsAbstract,
                isVirtual = first.IsVirtual,
                isOverride = first.IsOverride,
                isExtension = first.IsExtension,
                declarations = group.Select(Location).ToList(),
                uses = Rows(_index.UsageCounts(group.Key)),
                callers = CallerRows(group.Key, Math.Min(limit, 25)),
            });
        }

        return new
        {
            query = symbol,
            count = symbols.Count,
            symbols,
            note = "uses counts every recorded use by kind, including the override and implement relationships.",
        };
    }

    public object References(string symbol, string? kind = null, int limit = 200)
    {
        if (!TryOpen(out var error)) return error!;
        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var keys = declarations.Select(declaration => declaration.Key).Distinct(StringComparer.Ordinal).ToList();
        var usages = keys
            .SelectMany(key => _index.UsagesOf(key, kind, limit))
            .OrderBy(usage => usage.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(usage => usage.Line)
            .ThenBy(usage => usage.Column)
            .ToList();

        var byKind = keys
            .SelectMany(key => _index.UsageCounts(key))
            .Where(count => kind is null || string.Equals(count.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .GroupBy(count => count.Kind, StringComparer.Ordinal)
            .Select(group => new { kind = group.Key, count = group.Sum(count => count.Count) })
            .OrderByDescending(row => row.count)
            .ToList();

        var lines = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        return new
        {
            query = symbol,
            keys,
            count = usages.Count,
            byKind,
            references = usages.Select(usage => new
            {
                kind = usage.Kind,
                symbol = SymbolKeys.Display(usage.SymbolKey),
                key = usage.SymbolKey,
                container = Lookup(usage.ContainerKey).Name,
                containerKey = usage.ContainerKey,
                file = usage.Path,
                line = usage.Line,
                column = usage.Column,
                start = usage.Start,
                length = usage.Length,
                snippet = Snippet(lines, usage),
            }).ToList(),
            note = "container is the member the use happens inside. snippet is the source line, or null when the file is gone.",
        };
    }

    public object Callers(string symbol, int limit = 100)
    {
        if (!TryOpen(out var error)) return error!;
        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var merged = new Dictionary<string, (int Uses, SortedSet<string> Kinds)>(StringComparer.Ordinal);
        foreach (var key in declarations.Select(declaration => declaration.Key).Distinct(StringComparer.Ordinal))
        {
            foreach (var container in _index.ContainersOf(key, limit))
            {
                if (!merged.TryGetValue(container.ContainerKey, out var entry))
                    entry = (0, new SortedSet<string>(StringComparer.Ordinal));

                entry.Uses += container.Count;
                foreach (var kind in container.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    entry.Kinds.Add(kind);

                merged[container.ContainerKey] = entry;
            }
        }

        var callers = merged
            .OrderByDescending(pair => pair.Value.Uses)
            .Take(limit)
            .Select(pair =>
            {
                var location = Lookup(pair.Key);
                return new
                {
                    container = location.Name,
                    key = pair.Key,
                    kind = location.Kind,
                    declared = location.File is not null,
                    file = location.File,
                    line = location.Line,
                    column = location.Column,
                    uses = pair.Value.Uses,
                    kinds = pair.Value.Kinds.ToList(),
                };
            })
            .ToList();

        return new
        {
            query = symbol,
            count = callers.Count,
            callers,
            note = "One row per member that uses the symbol, with how it uses it. Relationships (override, implement) are not calls and are listed by action=hierarchy.",
        };
    }

    public object Callees(string symbol, int limit = 100)
    {
        if (!TryOpen(out var error)) return error!;
        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var merged = new Dictionary<string, (string Name, int Uses, SortedSet<string> Kinds)>(StringComparer.Ordinal);
        foreach (var key in declarations.Select(declaration => declaration.Key).Distinct(StringComparer.Ordinal))
        {
            foreach (var target in _index.TargetsOf(key, limit))
            {
                if (!merged.TryGetValue(target.SymbolKey, out var entry))
                    entry = (target.Name, 0, new SortedSet<string>(StringComparer.Ordinal));

                entry.Uses += target.Count;
                foreach (var kind in target.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    entry.Kinds.Add(kind);

                merged[target.SymbolKey] = entry;
            }
        }

        var callees = merged
            .OrderByDescending(pair => pair.Value.Uses)
            .Take(limit)
            .Select(pair =>
            {
                var location = Lookup(pair.Key);
                return new
                {
                    symbol = location.Name,
                    name = pair.Value.Name,
                    key = pair.Key,
                    kind = location.Kind,
                    declared = location.File is not null,
                    file = location.File,
                    line = location.Line,
                    uses = pair.Value.Uses,
                    kinds = pair.Value.Kinds.ToList(),
                };
            })
            .ToList();

        return new
        {
            query = symbol,
            count = callees.Count,
            callees,
            note = "declared=false means the symbol is outside the solution (a framework type): the index knows the use, not its declaration.",
        };
    }

    public object Hierarchy(string symbol, int limit = 100)
    {
        if (!TryOpen(out var error)) return error!;
        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var symbols = new List<object>();
        foreach (var group in declarations.GroupBy(declaration => declaration.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            symbols.Add(new
            {
                key = group.Key,
                name = first.Name,
                kind = first.Kind,
                display = first.Display,
                file = first.Path,
                line = first.Line,
                extends = Edges(group.Key, SymbolUsageKind.Inherit, incoming: false, limit),
                implements = Edges(group.Key, SymbolUsageKind.Implement, incoming: false, limit),
                extendedBy = Edges(group.Key, SymbolUsageKind.Inherit, incoming: true, limit),
                implementedBy = Edges(group.Key, SymbolUsageKind.Implement, incoming: true, limit),
                overrides = Edges(group.Key, SymbolUsageKind.Override, incoming: false, limit),
                overriddenBy = Edges(group.Key, SymbolUsageKind.Override, incoming: true, limit),
            });
        }

        return new
        {
            query = symbol,
            count = symbols.Count,
            symbols,
            note = "implementedBy on an interface member lists the members that implement it; extendedBy on a class lists its derived types.",
        };
    }

    public object Outline(string? symbol = null, string? path = null, int limit = 500)
    {
        if (!TryOpen(out var error)) return error!;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var declarations = _index.DeclarationsByPath(path.Trim(), limit);
            return new
            {
                path,
                count = declarations.Count,
                members = declarations.Select(Member).ToList(),
                note = "Every declaration the index holds for that file, in source order.",
            };
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return new { error = "Pass a type or member name, or a file path." };

        var resolved = Resolve(symbol, out error);
        if (error is not null) return error;

        var members = new List<object>();
        foreach (var key in resolved.Select(declaration => declaration.Key).Distinct(StringComparer.Ordinal))
        {
            members.AddRange(_index.DeclarationsByKey(key, limit).Select(Member));
            members.AddRange(_index.DeclarationsInContainer(key, limit).Select(Member));
        }

        return new
        {
            symbol,
            count = members.Count,
            members,
            note = "The declaration itself (once per partial part) followed by its members, in source order.",
        };
    }

    public object Metacode(string? symbol = null, string? path = null, int limit = 50)
    {
        if (!TryOpen(out var error)) return error!;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var trees = _index.MetacodeInPath(path.Trim(), limit);
            return new
            {
                path,
                count = trees.Count,
                trees = trees.Select(Tree).ToList(),
                note = "One tree per declaration the index holds for that file, in source order: a type's tree nests its members.",
            };
        }

        if (string.IsNullOrWhiteSpace(symbol))
            return new { error = "Pass a type or member name, or a file path." };

        var declarations = Resolve(symbol, out error);
        if (error is not null) return error;

        var found = declarations
            .Select(declaration => declaration.Key)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(key => _index.MetacodeOf(key, 1))
            .ToList();

        return new
        {
            query = symbol,
            count = found.Count,
            trees = found.Select(Tree).ToList(),
            note = "The canonical pseudocode of that declaration, generated when the document was indexed: " +
                   "a type carries its members, a member prints only itself. Rebuild the index after edits.",
        };
    }

    public object Unused(int limit = 200, string? kind = null)
    {
        if (!TryOpen(out var error)) return error!;

        var declarations = _index.Unused(limit, kind);
        return new
        {
            count = declarations.Count,
            declarations = declarations.Select(declaration => new
            {
                name = declaration.Name,
                kind = declaration.Kind,
                signature = declaration.Signature,
                accessibility = declaration.Accessibility,
                container = declaration.ContainerName,
                file = declaration.Path,
                line = declaration.Line,
            }).ToList(),
            note = "Only non-public declarations are listed, and only uses the index knows about: reflection, " +
                   "serializers, generated code and anything outside the solution are invisible to it.",
        };
    }

    public void Dispose() => _index.Dispose();

    // ── resolution and shaping ──────────────────────────────────────────────

    private bool TryOpen(out object? error)
    {
        error = null;

        var database = _store.EnsureDatabase();
        if (database is null)
        {
            error = new { error = "No solution is loaded, so there is no index database. Call load_solution first." };
            return false;
        }

        if (_index.Open(database)) return true;

        error = new { error = "The index database could not be opened (is the solution folder writable?).", database };
        return false;
    }

    /// <summary>
    /// Turns what a caller typed into the declarations it names: the full key first, then the exact
    /// name, then — for a qualified name like <c>C.Add</c> — the name plus the qualifier, so a symbol
    /// whose name appears in several types can still be named unambiguously.
    /// </summary>
    private IReadOnlyList<SymbolDeclaration> Resolve(string query, out object? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(query))
        {
            error = new { error = "A symbol name is required." };
            return [];
        }

        var text = query.Trim();
        var declarations = _index.DeclarationsByKey(text, 50);
        if (declarations.Count == 0) declarations = _index.DeclarationsByName(text, partial: false, kind: null, 50);

        if (declarations.Count == 0 && text.Contains('.', StringComparison.Ordinal))
        {
            var separator = text.LastIndexOf('.');
            var name = text[(separator + 1)..];
            var qualifier = text[..separator];

            var named = _index.DeclarationsByName(name, partial: false, kind: null, 50);
            var qualified = named.Where(declaration => Matches(declaration.ContainerName, qualifier)).ToList();

            declarations = qualified.Count > 0 ? qualified : named;
        }

        if (declarations.Count > 0) return declarations;

        error = new
        {
            query,
            error = "The index has no declaration with that name. Build it (action=build) or search with action=find.",
        };
        return [];
    }

    /// <summary>Whether a declaration's container is the qualifier: <c>Square</c>, <c>Ns.C</c> or <c>global::Ns.C</c>.</summary>
    private static bool Matches(string containerName, string qualifier)
    {
        var name = containerName.StartsWith("global::", StringComparison.Ordinal)
            ? containerName["global::".Length..]
            : containerName;

        return name.Equals(qualifier, StringComparison.Ordinal) ||
               name.EndsWith("." + qualifier, StringComparison.Ordinal);
    }

    private KeyInfo Lookup(string key)
    {
        if (string.IsNullOrEmpty(key)) return new KeyInfo(key, null, null, null, null, null);

        var declaration = _index.DeclarationsByKey(key, 1).FirstOrDefault();
        return declaration is null
            ? new KeyInfo(key, SymbolKeys.Display(key), null, null, null, null)
            : new KeyInfo(key, declaration.Display, declaration.Kind, declaration.Path, declaration.Line, declaration.Column);
    }

    private List<object> CallerRows(string key, int limit)
    {
        var rows = new List<object>();

        foreach (var container in _index.ContainersOf(key, limit))
        {
            var location = Lookup(container.ContainerKey);
            rows.Add(new
            {
                container = location.Name,
                key = container.ContainerKey,
                kind = location.Kind,
                file = location.File,
                line = location.Line,
                uses = container.Count,
                kinds = container.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return rows;
    }

    private List<object> Edges(string key, string kind, bool incoming, int limit)
    {
        var edges = new List<object>();

        foreach (var (edgeKey, edgeName) in _index.Relationship(key, kind, incoming, limit))
        {
            var location = Lookup(edgeKey);
            edges.Add(new
            {
                key = edgeKey,
                name = edgeName.Length > 0 ? edgeName : location.Name,
                kind = location.Kind,
                declared = location.File is not null,
                file = location.File,
                line = location.Line,
            });
        }

        return edges;
    }

    private static object Location(SymbolDeclaration declaration) => new
    {
        file = declaration.Path,
        line = declaration.Line,
        column = declaration.Column,
        start = declaration.Start,
        length = declaration.Length,
        endLine = declaration.EndLine,
        endColumn = declaration.EndColumn,
        project = declaration.Project,
    };

    private static object Member(SymbolDeclaration declaration) => new
    {
        name = declaration.Name,
        kind = declaration.Kind,
        signature = declaration.Signature,
        accessibility = declaration.Accessibility,
        modifiers = declaration.Modifiers,
        summary = declaration.Summary,
        line = declaration.Line,
        column = declaration.Column,
    };

    private static object Tree(SymbolMetacode tree) => new
    {
        key = tree.SymbolKey,
        name = tree.Name,
        kind = tree.Kind,
        file = tree.Path,
        line = tree.Line,
        text = tree.Text,
    };

    private static List<object> Rows(IReadOnlyList<SymbolKindCount> counts) =>
        counts.Select(count => (object)new { kind = count.Kind, count = count.Count }).ToList();

    /// <summary>The source line a use sits on, read once per file and never more than 200 files per call.</summary>
    private static string? Snippet(Dictionary<string, string[]?> cache, SymbolUsage usage)
    {
        const int CacheLimit = 200;

        if (!cache.TryGetValue(usage.Path, out var lines))
        {
            try
            {
                lines = File.ReadAllLines(usage.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lines = null;
            }

            if (cache.Count < CacheLimit) cache[usage.Path] = lines;
        }

        if (lines is null || usage.Line < 1 || usage.Line > lines.Length) return null;

        var text = lines[usage.Line - 1].Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    /// <summary>Where a key is declared, when the index knows: a container outside the solution has no file.</summary>
    private sealed record KeyInfo(string Key, string? Name, string? Kind, string? File, int? Line, int? Column);
}
