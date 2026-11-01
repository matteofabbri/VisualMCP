using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// An assembly decompiled INTO a Roslyn workspace: one document per type, a project with the
/// platform references and the assembly's own folder as metadata references. That is what turns
/// decompiled code from text into a place where a symbol can be found, its source read, its uses
/// listed and its members outlined — the navigation a library without sources normally lacks.
/// <para>
/// The workspace is keyed on the module version id: loading the same binary again reuses it.
/// </para>
/// </summary>
public interface IAssemblyWorkspaceService
{
    /// <summary>Decompile the assembly into the workspace (or reuse the one already loaded).</summary>
    Task<object> LoadAsync(string assemblyPath, int maxTypes, bool force);

    /// <summary>What is loaded, with MVID, type count and load time.</summary>
    object List();

    /// <summary>Types and members whose name matches, ranked exact &gt; prefix &gt; CamelHumps &gt; contains.</summary>
    Task<object> SearchAsync(string assemblyPath, string query, int maxResults);

    /// <summary>The decompiled source of one type or member, with its document location.</summary>
    Task<object> SourceAsync(string assemblyPath, string symbol, int maxChars);

    /// <summary>The IL of a type or one of its methods, decoded from the same binary's method bodies.</summary>
    Task<object> IlAsync(string assemblyPath, string symbol, int maxMethods);

    /// <summary>Every use of a type or member inside the decompiled set (Roslyn's symbol finder).</summary>
    Task<object> ReferencesAsync(string assemblyPath, string symbol, int maxResults);

    /// <summary>Types (or one type's members) with their line numbers.</summary>
    Task<object> OutlineAsync(string assemblyPath, string? typeName, int maxMembers);

    /// <summary>Drop the workspace of one assembly (or every one, with null).</summary>
    object Unload(string? assemblyPath);
}

public sealed class AssemblyWorkspaceService : IAssemblyWorkspaceService
{
    private readonly ConcurrentDictionary<string, AssemblyWorkspace> _workspaces =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<object> LoadAsync(string assemblyPath, int maxTypes = 0, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return Task.FromResult<object>(new { error = $"Assembly not found: {assemblyPath}" });
        if (!ManagedAssemblyDetector.IsManaged(assemblyPath))
            return Task.FromResult<object>(new { error = $"{Path.GetFileName(assemblyPath)} is not a managed assembly." });

        var full = Path.GetFullPath(assemblyPath);
        var identity = AssemblyMvid.TryRead(full);

        if (!force && _workspaces.TryGetValue(full, out var known) && known.Mvid == identity)
            return Task.FromResult(Describe(known, reused: true));

        maxTypes = Math.Clamp(maxTypes, 0, 5000);

        try
        {
            var decompiler = new CSharpDecompiler(full, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
            var module = decompiler.TypeSystem.MainModule;

            var definitions = new List<(TypeDefinitionHandle Handle, string FullName)>();
            foreach (var handle in module.MetadataFile.Metadata.TypeDefinitions)
            {
                var definition = module.GetDefinition(handle);
                if (definition is null || definition.Name == "<Module>") continue;
                if (definition.Name.StartsWith('<')) continue; // compiler-generated
                definitions.Add((handle, definition.FullName));
            }

            definitions = definitions
                .OrderBy(pair => pair.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var truncated = maxTypes > 0 && definitions.Count > maxTypes;
            if (truncated) definitions = definitions.Take(maxTypes).ToList();

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var assemblyName = Path.GetFileNameWithoutExtension(full);

            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                assemblyName,
                assemblyName,
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular),
                metadataReferences: References(full));

            var solution = workspace.CurrentSolution.AddProject(projectInfo);

            var documents = new List<DecompiledDocument>();
            var errors = new List<string>();

            foreach (var (handle, typeName) in definitions)
            {
                string code;
                try
                {
                    code = decompiler.DecompileTypesAsString([handle]);
                }
                catch (Exception ex)
                {
                    if (errors.Count < 5) errors.Add($"{typeName}: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(code)) continue;

                var logicalPath = Path.Combine(
                    Path.GetDirectoryName(full)!, assemblyName + ".decompiled",
                    SafeFileName(typeName) + ".cs");

                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(logicalPath),
                    SourceText.From(code, Encoding.UTF8),
                    filePath: logicalPath);

                documents.Add(new DecompiledDocument(typeName, logicalPath, code));
            }

            if (!workspace.TryApplyChanges(solution))
                return Task.FromResult<object>(new
                {
                    error = "Roslyn refused the decompiled workspace (TryApplyChanges returned false).",
                });

            var loaded = new AssemblyWorkspace(full, assemblyName, identity, workspace, projectId, documents, truncated, errors);
            _workspaces[full] = loaded;
            return Task.FromResult(Describe(loaded, reused: false));
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new
            {
                error = $"The assembly could not be loaded as a workspace: {ex.Message}",
                assemblyPath = full,
            });
        }
    }

    public object List() => new
    {
        count = _workspaces.Count,
        workspaces = _workspaces.Values
            .OrderBy(workspace => workspace.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .Select(workspace => new
            {
                assembly = workspace.AssemblyPath,
                name = workspace.AssemblyName,
                mvid = workspace.Mvid,
                types = workspace.Documents.Count,
                truncated = workspace.Truncated,
                loadedAt = workspace.LoadedAt,
            })
            .ToList<object>(),
        note = "A loaded workspace is a real Roslyn solution: search, source, references and outline " +
               "answer from it without decompiling again.",
    };

    public Task<object> SearchAsync(string assemblyPath, string query, int maxResults = 50)
    {
        if (Find(assemblyPath) is not { } workspace) return Task.FromResult<object>(NotLoaded(assemblyPath));
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<object>(new { error = "A query is required." });

        maxResults = Math.Clamp(maxResults, 1, 500);
        var matches = new List<(int Rank, string SortKey, object Entry)>();

        foreach (var document in workspace.Documents)
        {
            foreach (var type in document.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (MatchRank(query, type.Identifier.Text) is > 0 and var typeRank)
                    matches.Add((typeRank, type.Identifier.Text,
                        new { kind = KindOf(type), name = type.Identifier.Text, file = document.FilePath, line = LineOf(type) }));

                foreach (var member in MembersOf(type))
                {
                    var name = MemberName(member);
                    if (name is null) continue;
                    if (MatchRank(query, name) is not (> 0 and var rank)) continue;

                    matches.Add((rank, name, new
                    {
                        kind = MemberKind(member),
                        name,
                        owner = type.Identifier.Text,
                        file = document.FilePath,
                        line = LineOf(member),
                    }));
                }
            }
        }

        var ordered = matches
            .OrderByDescending(match => match.Rank)
            .ThenBy(match => match.SortKey, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(match => match.Entry)
            .ToList();

        return Task.FromResult<object>(new
        {
            assembly = workspace.AssemblyName,
            query,
            totalMatches = matches.Count,
            returned = ordered.Count,
            matches = ordered,
            note = "Ranking: exact name, then prefix, then CamelHumps (e.g. 'CPS' finds CheckParameterSafety), then contains.",
        });
    }

    public Task<object> SourceAsync(string assemblyPath, string symbol, int maxChars = 20000)
    {
        if (Find(assemblyPath) is not { } workspace) return Task.FromResult<object>(NotLoaded(assemblyPath));
        if (string.IsNullOrWhiteSpace(symbol))
            return Task.FromResult<object>(new { error = "A symbol is required (Type or Type.Member)." });

        var found = workspace.FindSyntax(symbol);
        if (found is null)
            return Task.FromResult<object>(new
            {
                error = $"'{symbol}' is not a type or member of {workspace.AssemblyName}.",
                hint = "Call action=search to see the names the workspace holds.",
            });

        var (document, node, kind) = found.Value;
        var text = node.ToFullString();
        var span = node.SyntaxTree.GetLineSpan(node.Span);

        return Task.FromResult<object>(new
        {
            assembly = workspace.AssemblyName,
            symbol,
            kind,
            file = document,
            line = span.StartLinePosition.Line + 1,
            endLine = span.EndLinePosition.Line + 1,
            totalChars = text.Length,
            truncated = text.Length > maxChars,
            code = text.Length > maxChars ? text[..maxChars] + "…(truncated)" : text,
        });
    }

    /// <summary>
    /// The other half of the C#↔IL toggle: the same member, read as the instructions the JIT actually
    /// consumes. The symbol is resolved in the decompiled workspace (so a name action=search found works
    /// here too) and the binary's method body is decoded by <see cref="DisassemblyService"/>.
    /// </summary>
    public async Task<object> IlAsync(string assemblyPath, string symbol, int maxMethods = 50)
    {
        if (Find(assemblyPath) is not { } workspace) return NotLoaded(assemblyPath);
        if (string.IsNullOrWhiteSpace(symbol))
            return new { error = "A symbol is required (Type or Type.Member)." };

        var compilation = await workspace.Workspace.CurrentSolution
            .GetProject(workspace.ProjectId)!.GetCompilationAsync();
        var target = ResolveSymbol(compilation, symbol);
        if (target is null)
            return new
            {
                error = $"'{symbol}' does not resolve inside {workspace.AssemblyName}.",
                hint = "Call action=search to see the names the workspace holds.",
            };

        var typeName = target.ContainingType?.Name ?? (target as INamedTypeSymbol)?.Name;
        var methodName = target is IMethodSymbol method ? method.Name : null;
        var il = new DisassemblyService().Disassemble(workspace.AssemblyPath, typeName, methodName,
            Math.Clamp(maxMethods, 1, 500));

        return new
        {
            assembly = workspace.AssemblyName,
            symbol = target.ToDisplayString(),
            type = typeName,
            method = methodName,
            view = "il",
            il,
            note = "The C# view is action=source of the same symbol; this is the same body as the instructions " +
                   "the JIT consumes, with token operands resolved to names.",
        };
    }

    public async Task<object> ReferencesAsync(string assemblyPath, string symbol, int maxResults = 100)
    {
        if (Find(assemblyPath) is not { } workspace) return NotLoaded(assemblyPath);
        if (string.IsNullOrWhiteSpace(symbol)) return new { error = "A symbol is required (Type or Type.Member)." };

        maxResults = Math.Clamp(maxResults, 1, 1000);

        try
        {
            var compilation = await workspace.Workspace.CurrentSolution
                .GetProject(workspace.ProjectId)!.GetCompilationAsync();

            var target = ResolveSymbol(compilation, symbol);
            if (target is null)
                return new
                {
                    error = $"'{symbol}' does not resolve inside {workspace.AssemblyName}.",
                    hint = "The decompiled set binds its own types and the platform references; an external symbol " +
                           "only resolves when its assembly sits in the same folder.",
                };

            var references = await SymbolFinder.FindReferencesAsync(target, workspace.Workspace.CurrentSolution);
            var locations = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    if (location.IsImplicit) continue;

                    var span = location.Location.GetLineSpan();
                    if (!seen.Add($"{span.Path}:{span.StartLinePosition.Line}")) continue;

                    locations.Add(new
                    {
                        file = span.Path,
                        line = span.StartLinePosition.Line + 1,
                        column = span.StartLinePosition.Character + 1,
                    });

                    if (locations.Count >= maxResults) break;
                }

                if (locations.Count >= maxResults) break;
            }

            return new
            {
                assembly = workspace.AssemblyName,
                symbol = target.ToDisplayString(),
                referenceCount = locations.Count,
                truncated = locations.Count >= maxResults,
                locations,
                note = "Uses inside the decompiled set: a use by a consumer outside the assembly is not visible here.",
            };
        }
        catch (Exception ex)
        {
            return new { error = $"The reference search failed: {ex.Message}", assembly = workspace.AssemblyName };
        }
    }

    public Task<object> OutlineAsync(string assemblyPath, string? typeName, int maxMembers = 200)
    {
        if (Find(assemblyPath) is not { } workspace) return Task.FromResult<object>(NotLoaded(assemblyPath));
        maxMembers = Math.Clamp(maxMembers, 1, 2000);

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var found = workspace.FindSyntax(typeName);
            if (found is not { } value || value.Node is not BaseTypeDeclarationSyntax type)
                return Task.FromResult<object>(new { error = $"Type '{typeName}' is not in {workspace.AssemblyName}." });

            var members = MembersOf(type)
                .Take(maxMembers)
                .Select(member => new
                {
                    kind = MemberKind(member),
                    name = MemberName(member),
                    line = LineOf(member),
                })
                .ToList<object>();

            return Task.FromResult<object>(new
            {
                assembly = workspace.AssemblyName,
                type = type.Identifier.Text,
                file = value.Document,
                line = LineOf(type),
                memberCount = MembersOf(type).Count(),
                members,
            });
        }

        var types = workspace.Documents
            .SelectMany(document => document.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Select(type => new
                {
                    kind = KindOf(type),
                    name = type.Identifier.Text,
                    file = document.FilePath,
                    line = LineOf(type),
                    members = MembersOf(type).Count(),
                }))
            .Take(maxMembers)
            .ToList<object>();

        return Task.FromResult<object>(new
        {
            assembly = workspace.AssemblyName,
            typeCount = workspace.Documents.Count,
            truncated = workspace.Truncated,
            types,
        });
    }

    public object Unload(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            var count = _workspaces.Count;
            foreach (var key in _workspaces.Keys) Release(key);
            return new { unloaded = count };
        }

        var full = Path.GetFullPath(assemblyPath);
        return _workspaces.ContainsKey(full)
            ? Release(full)
            : new { unloaded = 0, note = $"'{Path.GetFileName(full)}' was not loaded." };
    }

    /// <summary>The uppercase letters of a name: 'CheckParameterSafety' → 'CPS'.</summary>
    public static string CamelHumps(string name) => new(name.Where(char.IsUpper).ToArray());

    // ── internals ────────────────────────────────────────────────────────────
    private object Release(string key)
    {
        if (!_workspaces.TryRemove(key, out var workspace)) return new { unloaded = 0 };
        workspace.Workspace.Dispose();
        return new { unloaded = 1, assembly = workspace.AssemblyPath };
    }

    private AssemblyWorkspace? Find(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
        return _workspaces.TryGetValue(Path.GetFullPath(assemblyPath), out var workspace) ? workspace : null;
    }

    private static object NotLoaded(string assemblyPath) => new
    {
        error = $"{Path.GetFileName(assemblyPath)} is not loaded as a workspace.",
        hint = "Call assembly_workspace action=load first.",
    };

    private static object Describe(AssemblyWorkspace workspace, bool reused) => new
    {
        assembly = workspace.AssemblyPath,
        name = workspace.AssemblyName,
        mvid = workspace.Mvid,
        types = workspace.Documents.Count,
        truncated = workspace.Truncated,
        reused,
        failedTypes = workspace.Errors.Count > 0 ? workspace.Errors : null,
        note = reused
            ? "The workspace for this module version was already loaded (the decompiler was not run again)."
            : "Loaded into a Roslyn workspace: action=search, source, references and outline answer from it.",
    };

    private static Microsoft.CodeAnalysis.ISymbol? ResolveSymbol(Compilation? compilation, string symbol)
    {
        if (compilation is null) return null;

        var dot = symbol.LastIndexOf('.');
        if (dot > 0)
        {
            var type = FindType(compilation, symbol[..dot]);
            if (type is not null)
                foreach (var member in type.GetMembers(symbol[(dot + 1)..]))
                    return member;
        }

        var direct = FindType(compilation, symbol);
        if (direct is not null) return direct;

        foreach (var candidate in AllTypes(compilation.GlobalNamespace))
            foreach (var member in candidate.GetMembers(symbol))
                return member;

        return null;
    }

    private static INamedTypeSymbol? FindType(Compilation compilation, string name)
    {
        var direct = compilation.GetTypeByMetadataName(name);
        if (direct is not null) return direct;

        foreach (var candidate in AllTypes(compilation.GlobalNamespace))
            if (candidate.Name == name ||
                candidate.ToDisplayString().EndsWith("." + name, StringComparison.Ordinal))
                return candidate;

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    foreach (var nested in AllTypes(child)) yield return nested;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers()) yield return nested;
                    break;
            }
        }
    }

    private static int MatchRank(string query, string name)
    {
        if (name.Equals(query, StringComparison.Ordinal)) return 4;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (CamelHumps(name).Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static string? MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventDeclarationSyntax @event => @event.Identifier.Text,
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
        BaseTypeDeclarationSyntax nested => nested.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        _ => null,
    };

    private static IEnumerable<MemberDeclarationSyntax> MembersOf(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax declaration => declaration.Members,
        EnumDeclarationSyntax enumeration => enumeration.Members.Cast<MemberDeclarationSyntax>(),
        _ => [],
    };

    private static string MemberKind(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => "method",
        PropertyDeclarationSyntax => "property",
        FieldDeclarationSyntax => "field",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        ConstructorDeclarationSyntax => "constructor",
        IndexerDeclarationSyntax => "indexer",
        BaseTypeDeclarationSyntax nested => KindOf(nested),
        _ => member.Kind().ToString(),
    };

    private static string KindOf(BaseTypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax => "record",
        EnumDeclarationSyntax => "enum",
        _ => "class",
    };

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string SafeFileName(string typeName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(typeName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return name.Length <= 120 ? name : name[..120];
    }

    /// <summary>The assembly's own folder as metadata references, plus the platform: dependencies resolve.</summary>
    private static List<MetadataReference> References(string assemblyPath)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(path)) continue;
            try { references.Add(MetadataReference.CreateFromFile(path)); } catch { /* not a valid reference */ }
        }

        var directory = Path.GetDirectoryName(assemblyPath);
        if (directory is null) return references;

        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!seen.Add(dll)) continue;
            try { references.Add(MetadataReference.CreateFromFile(dll)); } catch { /* native or invalid */ }
        }

        return references;
    }

    private sealed record DecompiledDocument(string FullName, string FilePath, string Code)
    {
        private CompilationUnitSyntax? _root;

        public CompilationUnitSyntax Root => _root ??= (CompilationUnitSyntax)SyntaxFactory.ParseCompilationUnit(Code);
    }

    private sealed class AssemblyWorkspace
    {
        public AssemblyWorkspace(string assemblyPath, string assemblyName, string? mvid, AdhocWorkspace workspace,
            ProjectId projectId, List<DecompiledDocument> documents, bool truncated, List<string> errors)
        {
            AssemblyPath = assemblyPath;
            AssemblyName = assemblyName;
            Mvid = mvid;
            Workspace = workspace;
            ProjectId = projectId;
            Documents = documents;
            Truncated = truncated;
            Errors = errors;
        }

        public string AssemblyPath { get; }
        public string AssemblyName { get; }
        public string? Mvid { get; }
        public AdhocWorkspace Workspace { get; }
        public ProjectId ProjectId { get; }
        public List<DecompiledDocument> Documents { get; }
        public bool Truncated { get; }
        public List<string> Errors { get; }
        public DateTimeOffset LoadedAt { get; } = DateTimeOffset.UtcNow;

        public (string Document, MemberDeclarationSyntax Node, string Kind)? FindSyntax(string symbol)
        {
            var dot = symbol.LastIndexOf('.');

            // "Demo.Calculator.Add" first, because a namespace-qualified type also contains dots.
            if (dot > 0)
            {
                var memberName = symbol[(dot + 1)..];
                var owner = FindType(symbol[..dot]);
                if (owner is { } match && match.Node is TypeDeclarationSyntax declaration)
                {
                    var member = declaration.Members.FirstOrDefault(candidate =>
                        MemberName(candidate) is { } name && name.Equals(memberName, StringComparison.Ordinal));
                    if (member is not null) return (match.Document, member, MemberKind(member));
                }
            }

            var type = FindType(symbol);
            return type is { } found ? (found.Document, found.Node, KindOf(found.Node)) : null;
        }

        private (string Document, BaseTypeDeclarationSyntax Node)? FindType(string name)
        {
            var shortName = name.Split('.').Last();

            foreach (var document in Documents)
            {
                var type = document.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(candidate =>
                    candidate.Identifier.Text == name ||
                    candidate.Identifier.Text == shortName);

                if (type is not null) return (document.FilePath, type);
            }

            return null;
        }
    }
}
