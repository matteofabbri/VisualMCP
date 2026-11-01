namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Where a ReSharper inspection id stands in this product. Two layers: the catalogue is read from the Rider
/// installation's metadata (every id the engine ships), while the coverage is a curated table that says which
/// of those ids a capability here answers — the honest half of "the proprietary catalogue".
/// </summary>
public interface IResharperInspectionMapService
{
    Task<object> ReportAsync(string? riderPath, bool includeInstall, int maxUncovered, string? outputPath = null);
}

public sealed class ResharperInspectionMapService : IResharperInspectionMapService
{
    /// <summary>How the capability that answers a ReSharper id is delivered.</summary>
    private static readonly (string Id, string Capability, string Kind, string Note)[] Map =
    [
        // Implemented natively, under ReSharper's own id, by the optional 'rider' inspection pack.
        ("ConvertToConstant.Local", "rider pack: ConvertToConstantLocalAnalyzer (reported as ConvertToConstant_Local)", "rider", "a local assigned once with a constant can be const; Roslyn ids cannot contain dots"),
        ("LoopVariableIsNeverChangedInsideLoop", "rider pack: LoopVariableChangedAnalyzer", "rider", "a for index written inside the body"),
        ("MemberCanBeMadeStatic.Global", "rider pack: MemberCanBeMadeStaticAnalyzer (reported as MemberCanBeMadeStatic_Global)", "rider", "a method that touches no instance state; Roslyn ids cannot contain dots"),
        ("RedundantArgumentDefaultValue", "rider pack: RedundantArgumentAnalyzer", "rider", "an argument equal to the parameter default; Rider 2026 id (was RedundantArgument)"),
        ("EmptyConstructor", "rider pack: EmptyConstructorAnalyzer", "rider", "the only, public, parameterless, empty constructor"),
        ("RedundantIfElseBlock", "rider pack: RedundantIfElseAnalyzer", "rider", "if/else returning opposite bool literals"),
        ("ConditionIsAlwaysTrueOrFalse", "rider pack: ConstantConditionAnalyzer", "rider", "a computed constant condition (not the literal)"),
        ("RedundantBaseConstructorCall", "rider pack: RedundantBaseConstructorCallAnalyzer", "rider", "an explicit parameterless base() call"),
        ("ConvertToCompoundAssignment", "rider pack: ConvertToCompoundAssignmentAnalyzer", "rider", "x = x + y → x += y, on a side-effect-free target"),
        ("RedundantLambdaSignatureParentheses", "rider pack: RedundantLambdaSignatureParenthesesAnalyzer", "rider", "(x) => x → x => x for a single untyped parameter"),
        ("StringLiteralAsInterpolationArgument", "rider pack: StringLiteralAsInterpolationArgumentAnalyzer", "rider", "a literal inside an interpolation merges into the text"),
        ("RedundantCheckBeforeAssignment", "rider pack: RedundantCheckBeforeAssignmentAnalyzer", "rider", "if (a != b) a = b; writes the value either way"),
        ("UseNameofExpression", "rider pack: UseNameofExpressionAnalyzer", "rider", "a literal naming a parameter → nameof, in the ArgumentException family and paramName arguments"),
        ("RedundantStringInterpolation", "rider pack: RedundantStringInterpolationAnalyzer", "rider", "an interpolated string with no holes is a plain string; ported from RedundantInterpolatedStringExpressionAnalyzer"),
        ("MethodOverloadWithOptionalParameter", "rider pack: MethodOverloadWithOptionalParameterAnalyzer", "rider", "an overload that only adds optional parameters is shadowed by the shorter one; ported from MethodOverloadWithOptionalParametersProblemAnalyzer"),
        ("ReplaceSequenceEqualWithConstantPattern", "rider pack: ReplaceSequenceEqualWithConstantPatternAnalyzer", "rider", "a char span compared to a string literal with SequenceEqual is a C# 11 constant pattern; ported from ReplaceSequenceEqualWithConstantPatternProblemAnalyzer"),
        ("RedundantPatternParentheses", "rider pack: RedundantPatternParenthesesAnalyzer", "rider", "parentheses pattern precedence does not need; ported from RedundantPatternParenthesesProblemAnalyzer"),
        ("MergeIntoPattern", "rider pack: MergeIntoPatternAnalyzer", "rider", "x is A && x is B → x is A and B; ported from SequentialPatternCheck / MergeIntoPatternWarning"),
        ("ReplaceWithStringIsNullOrEmpty", "rider pack: ReplaceWithStringIsNullOrEmptyAnalyzer", "rider", "s == null || s.Length == 0 → string.IsNullOrEmpty(s), and the negated form; ported from ReplaceWithStringIsNullOrEmptyWarning"),

        // The style engine rewrites these under their IDE ids.
        ("RedundantCast", "simplify_code IDE0004", "style", "unnecessary cast"),
        ("RedundantNameQualifier", "simplify_code IDE0002", "style", "qualifier that is in scope without it"),
        ("ArrangeThisQualifier", "simplify_code IDE0003", "style", "removal only: requiring 'this.' is not a rewrite; Rider 2026 id (was ForRedundantThisQualifier)"),
        ("SuggestVarOrType_BuiltInTypes", "simplify_code IDE0007/IDE0008", "style", "option-driven csharp_style_var_*"),
        ("SuggestVarOrType_Elsewhere", "simplify_code IDE0007/IDE0008", "style", "option-driven csharp_style_var_*"),
        ("SuggestVarOrType_SimpleTypes", "simplify_code IDE0007/IDE0008", "style", "option-driven csharp_style_var_*"),
        ("UseNullPropagation", "simplify_code IDE0031", "style", "null-propagation"),
        ("RedundantBooleanLiteral", "simplify_code IDE0100", "style", "x == true → x"),
        ("UseStringInterpolation", "simplify_code IDE0071", "style", "interpolation simplification"),
        ("ArrangeModifiersOrder", "simplify_code IDE0036", "style", "csharp_preferred_modifier_order"),
        ("RedundantNullableDirective", "simplify_code IDE0240/IDE0241", "style", "#nullable that changes nothing"),
        ("ConvertToUsingDeclaration", "simplify_code IDE0063", "style", "using block → using declaration"),
        ("UsePreferredNamespaceBody", "simplify_code IDE0160/IDE0161", "style", "block- vs file-scoped namespace"),
        ("ConvertIfStatementToConditionalTernaryExpression", "simplify_code IDE0045/IDE0046", "style", "if/else → conditional expression"),
        ("UseDeconstruction", "simplify_code IDE0042", "style", "tuple deconstruction"),
        ("ConvertToPrimaryConstructor", "convert_to_primary_constructor (IDE0290)", "tool", "the delegated tool"),
        ("ConvertToAutoProperty", "property_to_auto (IDE0032)", "tool", "the delegated tool"),
        ("RedundantUsingDirective", "optimize_usings (IDE0005)", "tool", "unused using directives"),
        ("StringLiteralTypo", "find_spelling_issues", "tool", "near-miss words against the solution and the dictionary"),
        ("InconsistentNaming", "naming_rules", "tool", "the .editorconfig naming policy, applied through rename"),
        ("UnusedMember.Global", "find_unused_symbols", "tool", "zero-reference members, framework-invoked reported apart"),
        ("UnusedVariable", "find_unused_symbols / get_diagnostics", "tool", "CS0219 and the symbol scan"),
        ("NotAccessedField.Global", "find_unused_symbols / inspect_code IDE0052", "tool", "written-but-never-read fields"),
        ("UnusedParameter.Local", "simplify_code IDE0060", "style", "unused-parameter removal"),
        ("UseCollectionExpression", "simplify_code IDE0028/IDE0300/IDE0301", "style", "collection expressions"),

        // The ReSharper refactoring-shaped ids are context actions or refactoring tools.
        ("InvertIf", "context_action invert_if", "context", "if/else branch swap"),
        ("MemberCanBePrivate.Global", "—", "none", "needs whole-solution usage plus an accessibility edit"),
        ("MemberCanBeInternal", "—", "none", "the same, across assemblies; Rider 2026 id (was MemberCanBeInternal.Global)"),
        ("RedundantOverload.Global", "—", "none", "overload set analysis, not implemented"),
        ("LoopCanBeConvertedToQuery", "—", "none", "LINQ conversion analysis, not implemented"),
        ("RedundantLambdaParameterType", "—", "none", "not implemented"),
        ("UseImplicitlyTypedLambda", "—", "none", "IDE0350 is catalogued as not a rewrite"),
        ("AssignmentIsNotUsed", "—", "none", "not implemented"),
        ("SuspiciousTypeConversion.Global", "—", "none", "type-conversion analysis, not implemented"),
    ];

    public Task<object> ReportAsync(
        string? riderPath = null, bool includeInstall = true, int maxUncovered = 100, string? outputPath = null)
    {
        var entries = Map.Select(entry => new
        {
            id = entry.Id,
            capability = entry.Capability,
            kind = entry.Kind,
            note = entry.Note,
        }).ToList();

        var byKind = Map.GroupBy(entry => entry.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var uncovered = Map.Where(entry => entry.Kind == "none").Select(entry => entry.Id).ToList();
        var limit = Math.Clamp(maxUncovered < 1 ? 100 : maxUncovered, 1, 1000);

        var rider = includeInstall ? ResolveRiderPath(riderPath) : null;
        var catalogue = rider is null ? null : ReSharperCatalogueReader.Read(rider);

        object? install = null;
        object? coverage = null;
        object? export = null;
        var catalogueIdCount = 0;

        if (includeInstall)
        {
            if (rider is null)
            {
                install = new
                {
                    found = false,
                    note = "No Rider installation was found; pass riderPath or set RIDER_HOME.",
                };
            }
            else if (catalogue is not { Found: true })
            {
                install = new
                {
                    found = true,
                    path = rider,
                    error = catalogue?.Error ?? "lib/ReSharperHost was not found under this installation.",
                };
            }
            else
            {
                install = Describe(catalogue);
                coverage = Coverage(catalogue, limit);
                catalogueIdCount = catalogue.Ids.Count;
            }
        }

        if (outputPath is { Length: > 0 })
        {
            export = catalogue is { Found: true }
                ? new
                {
                    path = ReSharperToCoverExport.Write(catalogue, Map.Select(entry => entry.Id).ToList(), outputPath),
                    listed = catalogue.Ids.Count,
                    ids = catalogue.Ids.Count(id => Map.All(entry => entry.Id != id.Id)),
                }
                : new
                {
                    path = (string?)null,
                    note = "Nothing was written: the page is the catalogue annotated with the curated map, so it needs " +
                           "the installation's catalogue (includeInstall=true and a readable Rider installation).",
                };
        }

        return Task.FromResult<object>(new
        {
            curatedCount = entries.Count,
            byKind,
            covered = entries.Count - uncovered.Count,
            uncovered = uncovered.Take(limit).ToList(),
            uncoveredCount = uncovered.Count,
            entries,
            catalogueIdCount,
            coverage,
            install,
            export,
            note = "Two layers. The catalogue is read from the Rider installation's metadata: each " +
                   "ConfigurableSeverityHighlighting id on a highlighting class, linked to the ElementProblemAnalyzer that " +
                   "produces it. The coverage is curated: each id this product answers, with the capability that answers it, " +
                   "or 'none'. A catalogue id with no entry here is not implemented. 'rider' is an opt-in inspection pack: " +
                   "inspect_code packs=rider. outputPath writes the whole catalogue, every language group, with each id's coverage.",
        });
    }

    /// <summary>What the installation ships, by language group.</summary>
    private static object Describe(ReSharperCatalogue catalogue)
    {
        var groups = catalogue.Ids
            .GroupBy(id => id.Languages, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { group = group.Key, count = group.Count() })
            .ToList();

        return new
        {
            found = true,
            path = catalogue.Path,
            assembliesScanned = catalogue.AssembliesScanned,
            idCount = catalogue.Ids.Count,
            groups,
            withAnalyzer = catalogue.Ids.Count(id => id.AnalyzerType is not null),
            withDescription = catalogue.Ids.Count(id => id.Description is not null),
            decodeFailures = catalogue.DecodeFailures,
            decodeFailureSample = catalogue.DecodeFailureSample,
            resourceErrors = catalogue.ResourceErrors,
            sample = catalogue.Ids.Take(20).Select(id => id.Id).ToList(),
            note = "Read from the installation's metadata: the ids are ConfigurableSeverityHighlighting arguments, the analyzer " +
                   "is the ElementProblemAnalyzer that names the highlighting, and the description is the tooltip text the " +
                   "attribute's resource name resolves to.",
        };
    }

    /// <summary>The curated map compared against the real catalogue, in both directions.</summary>
    private static object Coverage(ReSharperCatalogue catalogue, int limit)
    {
        var known = catalogue.Ids.Select(id => id.Id).ToHashSet(StringComparer.Ordinal);
        var mapped = Map.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        var mappedInCatalogue = Map.Select(entry => entry.Id).Where(known.Contains).ToList();
        var mappedNotInCatalogue = Map.Select(entry => entry.Id).Where(id => !known.Contains(id)).ToList();
        var uncoveredHere = catalogue.Ids.Count(id => !mapped.Contains(id.Id));
        var uncoveredSample = catalogue.Ids
            .Where(id => !mapped.Contains(id.Id))
            .Take(limit)
            .Select(id => new
            {
                id = id.Id,
                group = id.Languages,
                analyzer = id.AnalyzerType,
                description = id.Description,
            })
            .ToList();

        return new
        {
            mappedInCatalogueCount = mappedInCatalogue.Count,
            mappedNotInCatalogueCount = mappedNotInCatalogue.Count,
            mappedNotInCatalogue = mappedNotInCatalogue.Take(limit).ToList(),
            uncoveredFromCatalogueCount = uncoveredHere,
            uncoveredFromCatalogue = uncoveredSample,
        };
    }

    private static string? ResolveRiderPath(string? riderPath)
    {
        if (!string.IsNullOrWhiteSpace(riderPath))
            return Directory.Exists(riderPath) ? Path.GetFullPath(riderPath) : null;

        var home = Environment.GetEnvironmentVariable("RIDER_HOME");
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home)) return Path.GetFullPath(home);

        var jetbrains = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains");
        if (!Directory.Exists(jetbrains)) return null;

        return Directory.EnumerateDirectories(jetbrains, "*Rider*")
            .Where(directory => Directory.Exists(Path.Combine(directory, "lib", "ReSharperHost")))
            .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
