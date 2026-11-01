using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// The EF Core migration workflow, which the C# model alone cannot give: list, add, script, remove
/// and apply migrations through the project's own <c>dotnet ef</c> tool, with a structured result
/// instead of a shell transcript. <c>model-diff</c> is the one action that needs no tool: it reads
/// the <c>ModelSnapshot</c> the migration carries and compares it, field by field, with the entity
/// classes the compiler sees.
/// </summary>
public interface IEfMigrationsService
{
    Task<object> RunAsync(string? projectName, string action, string? name, string? output,
        string? startupProject, bool noBuild, int timeoutSeconds, string? fromMigration, string? toMigration);
}

public sealed class EfMigrationsService : IEfMigrationsService
{
    private static readonly string[] Actions =
        ["list", "add", "script", "diff", "remove", "update", "info", "dbcontext-list", "pending-model-changes",
         "model-diff"];

    private readonly IRoslynWorkspace _workspace;
    public EfMigrationsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RunAsync(string? projectName = null, string action = "list", string? name = null,
        string? output = null, string? startupProject = null, bool noBuild = false, int timeoutSeconds = 120,
        string? fromMigration = null, string? toMigration = null)
    {
        var command = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!Actions.Contains(command))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}." };

        if (command == "diff" && string.IsNullOrWhiteSpace(fromMigration))
            return new
            {
                error = "The diff action needs fromMigration (use '0' for an empty database); toMigration is " +
                        "optional and defaults to the latest migration.",
            };

        if (command == "add" && string.IsNullOrWhiteSpace(name))
            return new { error = "The add action needs a migration name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory." };

        Project? project;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new { error = $"Project '{projectName}' not found in the solution." };
        }
        else
        {
            var candidates = solution.Projects
                .Where(candidate => string.Equals(Path.GetExtension(candidate.FilePath), ".csproj",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1) project = candidates[0];
            else
                return new
                {
                    error = "Several projects could be the migrations project; pass projectName.",
                    candidates = candidates.Select(candidate => candidate.Name).ToList(),
                };
        }

        if (project.FilePath is null) return new { error = $"Project '{project.Name}' has no file on disk." };

        // model-diff reads the snapshot and the entity classes through Roslyn: it never runs dotnet ef,
        // so it answers the field-level question pending-model-changes can only answer with a boolean.
        if (command == "model-diff")
            return await ModelDiffAsync(project, name);

        var directory = Path.GetDirectoryName(project.FilePath)!;
        var args = CommandArguments(command, name, output, fromMigration, toMigration);

        args.AddRange(["--project", Quote(project.FilePath)]);
        if (!string.IsNullOrWhiteSpace(startupProject))
        {
            var startup = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(startupProject, StringComparison.OrdinalIgnoreCase));
            if (startup?.FilePath is null)
                return new { error = $"Startup project '{startupProject}' not found in the solution." };
            args.AddRange(["--startup-project", Quote(startup.FilePath)]);
        }
        if (noBuild) args.Add("--no-build");

        var commandLine = "dotnet " + string.Join(" ", args);
        var timeout = Math.Clamp(timeoutSeconds, 5, 900);
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", string.Join(" ", args), directory, timeout);

        bool? pendingModelChanges = command != "pending-model-changes" || timedOut
            ? null
            : exitCode switch { 0 => false, 1 => true, _ => null };

        return new
        {
            project = project.Name,
            projectPath = project.FilePath,
            workingDirectory = directory,
            action = command,
            command = commandLine,
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            durationMs = (long)elapsed.TotalMilliseconds,
            succeeded = !timedOut && exitCode == 0,
            pendingModelChanges,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
            hint = command switch
            {
                "pending-model-changes" =>
                    "Exit code 0: the model and the migrations agree. 1: the model has changed since the last " +
                    "migration, so a migration is pending. Any other code is a tool or project error.",
                "diff" =>
                    "The SQL between fromMigration and toMigration: that is the schema delta the database would " +
                    "receive. A field-level comparison of the model itself is not made here — the C# of the " +
                    "migration and the snapshot is reviewed with the normal tools.",
                _ when exitCode != 0 =>
                    "A failing 'dotnet ef' usually means the 'dotnet-ef' tool is missing (dotnet tool install --global dotnet-ef) " +
                    "or the project does not reference Microsoft.EntityFrameworkCore.Design.",
                _ =>
                    "A migration file is C#: review it with the normal C# tools; nothing here proves the model and the " +
                    "migrations agree, which is what a script or a diff does.",
            },
        };
    }

    // ── model-diff: the snapshot against the entity classes ───────────────────
    private sealed record SnapshotProperty(string Name, string? Type);

    private sealed class SnapshotEntity(string? table)
    {
        public string? Table { get; set; } = table;
        public List<SnapshotProperty> Properties { get; } = [];
    }

    private sealed record ModelProperty(string Name, string? Type);

    private static async Task<object> ModelDiffAsync(Project project, string? snapshotHint)
    {
        var documents = project.Documents
            .Where(document => document.FilePath is not null &&
                               Path.GetFileName(document.FilePath)
                                   .EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (documents.Count == 0)
            return new
            {
                error = $"No *ModelSnapshot.cs document in project '{project.Name}'. Run 'dotnet ef migrations add <name>' " +
                        "once, or pass projectName of the migrations project.",
            };

        if (!string.IsNullOrWhiteSpace(snapshotHint))
        {
            var matched = documents.Where(document =>
                Path.GetFileNameWithoutExtension(document.FilePath)!
                    .Contains(snapshotHint!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count > 0) documents = matched;
        }

        if (documents.Count > 1)
            return new
            {
                error = "Several model snapshots match; pass name (part of the snapshot or DbContext name).",
                candidates = documents.Select(document => Path.GetFileName(document.FilePath)).ToList(),
            };

        var document = documents[0];
        var root = await document.GetSyntaxRootAsync();
        var semantic = await document.GetSemanticModelAsync();
        if (root is null || semantic is null)
            return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var snapshotClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.BaseList?.Types.Any(type =>
                type.Type.ToString().EndsWith("ModelSnapshot", StringComparison.Ordinal)) == true);
        if (snapshotClass is null)
            return new { error = "No ModelSnapshot class in the document." };

        var buildModel = snapshotClass.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.Text == "BuildModel");

        var snapshotEntities = new Dictionary<string, SnapshotEntity>(StringComparer.Ordinal);
        if (buildModel is not null)
            foreach (var invocation in buildModel.DescendantNodes().OfType<InvocationExpressionSyntax>())
                ReadEntityCall(invocation, semantic, snapshotEntities);

        var compilation = await project.GetCompilationAsync();
        var dbContext = FindDbContext(compilation);
        var modelEntities = ModelEntityTypes(dbContext);

        var entities = new List<object>();
        var drift = false;

        foreach (var (typeName, snapshot) in snapshotEntities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var symbol = compilation?.GetTypeByMetadataName(typeName) ?? FindType(compilation, typeName);
            var declared = symbol is null ? new List<ModelProperty>() : ModelProperties(symbol);

            var declaredNames = declared.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var snapshotNames = snapshot.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

            var added = declaredNames.Except(snapshotNames, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();
            var notOnTheType = snapshotNames.Except(declaredNames, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();

            var mismatches = new List<object>();
            foreach (var property in snapshot.Properties.Where(property => property.Type is not null))
            {
                var declaredProperty = declared.FirstOrDefault(candidate => candidate.Name == property.Name);
                if (declaredProperty?.Type is null) continue;
                if (TypeShape(declaredProperty.Type) == TypeShape(property.Type!)) continue;

                mismatches.Add(new { property = property.Name, model = declaredProperty.Type, snapshot = property.Type });
            }

            if (added.Count > 0 || mismatches.Count > 0) drift = true;

            entities.Add(new
            {
                type = typeName,
                table = snapshot.Table,
                resolved = symbol is not null,
                propertiesInModel = declared.Count,
                propertiesInSnapshot = snapshot.Properties.Count,
                addedInTheModel = added,
                notOnTheEntityType = notOnTheType,
                typeMismatches = mismatches,
            });
        }

        var modelOnly = modelEntities.Where(type => !snapshotEntities.ContainsKey(type))
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (modelOnly.Count > 0) drift = true;

        return new
        {
            project = project.Name,
            snapshot = Path.GetFileName(document.FilePath),
            snapshotClass = snapshotClass.Identifier.Text,
            dbContext = dbContext?.Name,
            entityCount = snapshotEntities.Count,
            drifted = drift,
            entities,
            entitiesMissingFromSnapshot = modelOnly,
            note = "Field-by-field, from the ModelSnapshot C# and the entity classes Roslyn resolves. An entity the " +
                   "compilation did not resolve is reported with resolved=false, not as drift; a snapshot property " +
                   "with no CLR member is usually a shadow property and is listed, not flagged. " +
                   "pending-model-changes answers the same question as a boolean from EF itself.",
        };
    }

    private static void ReadEntityCall(InvocationExpressionSyntax invocation, SemanticModel model,
        Dictionary<string, SnapshotEntity> entities)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access) return;
        if (access.Name.Identifier.Text != "Entity") return;

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0) return;

        var typeName = EntityTypeName(invocation, arguments[0].Expression, model);
        if (string.IsNullOrWhiteSpace(typeName)) return;

        if (!entities.TryGetValue(typeName, out var entity))
        {
            entity = new SnapshotEntity(null);
            entities[typeName] = entity;
        }

        var lambda = arguments.Skip(1).Select(argument => argument.Expression)
            .OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (lambda is null) return;

        var parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text,
            ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count > 0 =>
                parenthesized.ParameterList.Parameters[0].Identifier.Text,
            _ => null,
        };

        foreach (var call in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax member) continue;
            if (parameter is not null && member.Expression is IdentifierNameSyntax receiver &&
                receiver.Identifier.Text != parameter) continue;

            var name = member.Name.Identifier.Text;

            if (name == "Property")
            {
                var argument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                if (argument is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                    continue;

                var propertyType = member.Name is GenericNameSyntax generic &&
                                   generic.TypeArgumentList.Arguments.Count == 1
                    ? generic.TypeArgumentList.Arguments[0].ToString()
                    : null;

                entity.Properties.Add(new SnapshotProperty(literal.Token.ValueText, propertyType));
            }
            else if (name == "ToTable" && entity.Table is null)
            {
                var argument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                if (argument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    entity.Table = literal.Token.ValueText;
            }
        }
    }

    private static string? EntityTypeName(InvocationExpressionSyntax invocation, ExpressionSyntax first,
        SemanticModel model)
    {
        if (first is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;

        if (first is TypeOfExpressionSyntax typeOf)
            return model.GetTypeInfo(typeOf.Type).Type?.ToDisplayString();

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } &&
            generic.TypeArgumentList.Arguments.Count == 1)
            return model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type?.ToDisplayString();

        return null;
    }

    private static INamedTypeSymbol? FindDbContext(Compilation? compilation)
    {
        if (compilation is null) return null;

        return Types(compilation.GlobalNamespace)
            .FirstOrDefault(type => type.BaseType is { } baseType &&
                                    (baseType.Name == "DbContext" ||
                                     baseType.Name.EndsWith("DbContext", StringComparison.Ordinal)));
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            foreach (var nested in Walk(type))
                yield return nested;

        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in Types(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> Walk(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var child in Walk(nested))
                yield return child;
    }

    private static List<string> ModelEntityTypes(INamedTypeSymbol? context)
    {
        var names = new List<string>();
        if (context is null) return names;

        foreach (var property in context.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is not INamedTypeSymbol { Name: "DbSet" } set || set.TypeArguments.Length != 1) continue;
            if (set.TypeArguments[0] is INamedTypeSymbol entity) names.Add(entity.ToDisplayString());
        }

        return names;
    }

    private static List<ModelProperty> ModelProperties(INamedTypeSymbol type)
    {
        var properties = new List<ModelProperty>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic) continue;

                if (member is IPropertySymbol property && property.Parameters.Length == 0 &&
                    property.DeclaredAccessibility != Accessibility.Private)
                {
                    if (property.GetAttributes().Any(attribute =>
                            attribute.AttributeClass?.Name is "NotMappedAttribute" or "NotMapped"))
                        continue;
                    if (seen.Add(property.Name))
                        properties.Add(new ModelProperty(property.Name, property.Type.ToDisplayString()));
                }
                else if (member is IFieldSymbol field && field.DeclaredAccessibility == Accessibility.Public &&
                         !field.IsConst && field.AssociatedSymbol is null)
                {
                    if (seen.Add(field.Name))
                        properties.Add(new ModelProperty(field.Name, field.Type.ToDisplayString()));
                }
            }
        }

        return properties;
    }

    private static INamedTypeSymbol? FindType(Compilation? compilation, string name)
    {
        if (compilation is null) return null;

        var direct = compilation.GetTypeByMetadataName(name);
        if (direct is not null) return direct;

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol) continue;
                if (symbol.ToDisplayString() == name ||
                    symbol.ToDisplayString().EndsWith("." + name, StringComparison.Ordinal))
                    return symbol;
            }
        }

        return null;
    }

    /// <summary>A type name reduced to its shape: namespaces dropped, nullable spelled one way, generics kept.</summary>
    private static string TypeShape(string type)
    {
        var text = type.Trim().Replace("global::", string.Empty, StringComparison.Ordinal);
        if (text.EndsWith('?')) text = text[..^1];

        var open = text.IndexOf('<');
        if (open > 0 && text.EndsWith('>'))
        {
            var outer = SimpleName(text[..open]);
            if (outer == "Nullable") return TypeShape(text[(open + 1)..^1]);

            var arguments = SplitTopLevel(text[(open + 1)..^1]).Select(TypeShape);
            return outer + "<" + string.Join(",", arguments) + ">";
        }

        return SimpleName(text);
    }

    private static string SimpleName(string text)
    {
        var dot = text.LastIndexOf('.');
        return (dot < 0 ? text : text[(dot + 1)..]).Trim();
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text[start..index]);
                    start = index + 1;
                    break;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>The 'dotnet ef' arguments before the common project options.</summary>
    public static List<string> CommandArguments(string action, string? name, string? output,
        string? fromMigration = null, string? toMigration = null)
    {
        if (action == "info") return ["ef", "dbcontext", "info"];
        if (action == "dbcontext-list") return ["ef", "dbcontext", "list"];

        // 'diff' is 'script from to': the SQL between two migrations, which is the schema delta.
        var args = new List<string> { "ef", "migrations" };
        args.Add(action switch
        {
            "pending-model-changes" => "has-pending-model-changes",
            "diff" => "script",
            _ => action,
        });

        switch (action)
        {
            case "add" when !string.IsNullOrWhiteSpace(name): args.Add(Quote(name)); break;
            case "update" when !string.IsNullOrWhiteSpace(name): args.Add(Quote(name)); break;
            case "diff" when !string.IsNullOrWhiteSpace(fromMigration):
                args.Add(Quote(fromMigration));
                if (!string.IsNullOrWhiteSpace(toMigration)) args.Add(Quote(toMigration));
                break;
            case "script" when !string.IsNullOrWhiteSpace(output): args.AddRange(["--output", Quote(output)]); break;
        }

        if (action == "diff" && !string.IsNullOrWhiteSpace(output))
            args.AddRange(["--output", Quote(output)]);

        return args;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
