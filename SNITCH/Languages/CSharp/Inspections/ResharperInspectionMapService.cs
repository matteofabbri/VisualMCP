using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Where a ReSharper inspection id stands in this product. The table is curated: the ~2500 ReSharper
/// inspections are closed-source analyzers on JetBrains' PSI, so the honest answer is not "all ported" but
/// "this id maps to that capability here, and these ids have none".
/// </summary>
public interface IResharperInspectionMapService
{
    Task<object> ReportAsync(string? riderPath, bool includeInstall, int maxUncovered);
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
        ("RedundantArgument", "rider pack: RedundantArgumentAnalyzer", "rider", "an argument equal to the parameter default"),
        ("EmptyConstructor", "rider pack: EmptyConstructorAnalyzer", "rider", "the only, public, parameterless, empty constructor"),
        ("RedundantIfElseBlock", "rider pack: RedundantIfElseAnalyzer", "rider", "if/else returning opposite bool literals"),
        ("ConditionIsAlwaysTrueOrFalse", "rider pack: ConstantConditionAnalyzer", "rider", "a computed constant condition (not the literal)"),
        ("RedundantBaseConstructorCall", "rider pack: RedundantBaseConstructorCallAnalyzer", "rider", "an explicit parameterless base() call"),

        // The style engine rewrites these under their IDE ids.
        ("RedundantCast", "simplify_code IDE0004", "style", "unnecessary cast"),
        ("RedundantNameQualifier", "simplify_code IDE0002", "style", "qualifier that is in scope without it"),
        ("ForRedundantThisQualifier", "simplify_code IDE0003", "style", "removal only: requiring 'this.' is not a rewrite"),
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
        ("UseNameofExpression", "—", "none", "string literal → nameof: not implemented as a rewrite"),
        ("MemberCanBePrivate.Global", "—", "none", "needs whole-solution usage plus an accessibility edit"),
        ("MemberCanBeInternal.Global", "—", "none", "the same, across assemblies"),
        ("RedundantOverload.Global", "—", "none", "overload set analysis, not implemented"),
        ("LoopCanBeConvertedToQuery", "—", "none", "LINQ conversion analysis, not implemented"),
        ("RedundantLambdaParameterType", "—", "none", "not implemented"),
        ("UseImplicitlyTypedLambda", "—", "none", "IDE0350 is catalogued as not a rewrite"),
        ("AssignmentIsNotUsed", "—", "none", "not implemented"),
        ("SuspiciousTypeConversion.Global", "—", "none", "type-conversion analysis, not implemented"),
    ];

    public Task<object> ReportAsync(string? riderPath = null, bool includeInstall = true, int maxUncovered = 100)
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

        return Task.FromResult<object>(new
        {
            curatedCount = entries.Count,
            byKind,
            covered = entries.Count - uncovered.Count,
            uncovered = uncovered.Take(limit).ToList(),
            uncoveredCount = uncovered.Count,
            entries,
            install = includeInstall ? ScanInstall(riderPath) : null,
            note = "The table is curated, not exhaustive: the ReSharper catalogue has ~2500 ids, implemented as " +
                   "closed-source analyzers over JetBrains' PSI, and each one would be a reimplementation rather " +
                   "than a registration here. The install scan counts the Inspection/Inspector types in the local " +
                   "Rider to show the scale; their ids are runtime registrations and cannot be enumerated from " +
                   "metadata. 'rider' is an opt-in inspection pack: inspect_code packs=rider.",
        });
    }

    /// <summary>
    /// Counts the Inspection/Inspector types in the local Rider installation — evidence of the scale, not a
    /// catalogue of ids (a type name is not the inspection id).
    /// </summary>
    private static object? ScanInstall(string? riderPath)
    {
        var path = ResolveRiderPath(riderPath);
        if (path is null)
            return new { found = false, note = "No Rider installation was found; pass riderPath or set RIDER_HOME." };

        var host = Path.Combine(path, "lib", "ReSharperHost");
        if (!Directory.Exists(host))
            return new { found = true, path, error = "lib/ReSharperHost was not found under this installation." };

        var assemblies = 0;
        var inspectionTypes = 0;
        var sample = new List<string>();

        foreach (var file in Directory.EnumerateFiles(host, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;

                assemblies++;
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.TypeDefinitions)
                {
                    var definition = reader.GetTypeDefinition(handle);
                    var name = reader.GetString(definition.Name);
                    if (name.StartsWith('<')) continue;
                    if (!name.EndsWith("Inspection", StringComparison.Ordinal) &&
                        !name.EndsWith("Inspector", StringComparison.Ordinal))
                        continue;

                    inspectionTypes++;
                    if (sample.Count < 20 && !sample.Contains(name)) sample.Add(name);
                }
            }
            catch (Exception)
            {
                // A native or unreadable DLL in the folder is not a failure of the scan.
            }
        }

        sample.Sort(StringComparer.Ordinal);
        return new
        {
            found = true,
            path,
            assembliesScanned = assemblies,
            inspectionTypes,
            sample,
            note = "Type names are evidence of the scale; the inspection IDs are runtime registrations and settings keys.",
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
