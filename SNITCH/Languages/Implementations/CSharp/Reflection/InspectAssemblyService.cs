using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Snitch.Languages.CSharp.Reflection;

public interface IInspectAssemblyService
{
    object Run(string assemblyPath, string? typeFilter, bool includeMembers, int maxTypes);

    /// <summary>
    /// Regex search over the exported type and member names of one assembly. A type whose name
    /// matches comes back with all its members; a member whose name matches comes back as one
    /// line under its type. This is what replaces one <see cref="Run"/> call per type name.
    /// </summary>
    object FindApi(string assemblyPath, string pattern, string? kind, int maxMatches);
}

public sealed class InspectAssemblyService : IInspectAssemblyService
{
    private static readonly string[] TypeKinds = ["class", "interface", "struct", "enum", "delegate"];
    private static readonly string[] MemberKinds = ["constructor", "method", "property", "field", "event"];

    public object Run(string assemblyPath, string? typeFilter, bool includeMembers, int maxTypes)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };

        if (maxTypes < 1) maxTypes = 1;
        if (maxTypes > 2000) maxTypes = 2000;

        try
        {
            using var context = Open(assemblyPath, out var asm);

            Type[] exported;
            try { exported = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t is not null).ToArray()!; }

            var filtered = exported
                .Where(t => typeFilter is null || (t.FullName ?? t.Name).Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var truncated = filtered.Count > maxTypes;
            var types = filtered.Take(maxTypes).Select(t => DescribeType(t, includeMembers)).ToList();

            var name = asm.GetName();
            return new
            {
                assembly = name.Name,
                version = name.Version?.ToString(),
                path = Path.GetFullPath(assemblyPath),
                typeFilter,
                exportedTypeCount = filtered.Count,
                returnedTypeCount = types.Count,
                truncated,
                types,
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to inspect assembly: {ex.Message}", assemblyPath };
        }
    }

    public object FindApi(string assemblyPath, string pattern, string? kind, int maxMatches)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };

        if (string.IsNullOrWhiteSpace(pattern))
            return new { error = "find_assembly_api needs a pattern: a regex over type and member names." };

        if (maxMatches < 1) maxMatches = 1;
        if (maxMatches > 2000) maxMatches = 2000;

        KindFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalized = kind.Trim().ToLowerInvariant();
            if (TypeKinds.Contains(normalized)) filter = new KindFilter(normalized, IsMember: false);
            else if (MemberKinds.Contains(normalized)) filter = new KindFilter(normalized, IsMember: true);
            else
                return new
                {
                    error = $"kind '{kind}' is not valid. Use {string.Join(", ", TypeKinds)} (type kind) " +
                            $"or {string.Join(", ", MemberKinds)} (member kind).",
                };
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new { error = $"Invalid regex '{pattern}': {ex.Message}" };
        }

        try
        {
            using var context = Open(assemblyPath, out var asm);

            Type[] exported;
            try { exported = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t is not null).ToArray()!; }

            var matches = new List<object>();
            var leftOut = new List<string>();
            var matchedTypes = 0;

            foreach (var type in exported.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var row = DescribeMatch(type, regex, filter);
                if (row is null) continue;

                matchedTypes++;
                if (matches.Count < maxMatches) matches.Add(row);
                else leftOut.Add(type.FullName ?? type.Name);
            }

            var name = asm.GetName();
            return new
            {
                assembly = name.Name,
                version = name.Version?.ToString(),
                path = Path.GetFullPath(assemblyPath),
                pattern,
                kind,
                matchedTypeCount = matchedTypes,
                returnedTypeCount = matches.Count,
                truncated = leftOut.Count > 0,
                types = matches,
                leftOut,
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return new { error = $"Pattern '{pattern}' took too long to evaluate against {assemblyPath}.", assemblyPath };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to search assembly: {ex.Message}", assemblyPath };
        }
    }

    private static object? DescribeMatch(Type t, Regex regex, KindFilter? kind)
    {
        var typeKind = ClassifyType(t);

        // A type-kind filter rules out the whole type, name or members alike.
        if (kind is { IsMember: false } && !string.Equals(typeKind, kind.Name, StringComparison.OrdinalIgnoreCase))
            return null;

        var onlyMembers = kind is { IsMember: true };
        if (!onlyMembers && regex.IsMatch(t.FullName ?? t.Name))
        {
            // "Give me this type": the full member list, exactly like inspect_assembly(typeFilter:).
            var members = typeKind == "delegate" ? null : MembersOf(t, nameFilter: null, kind);
            return DescribeMatchRow(t, typeKind, "type", members);
        }

        // Otherwise only the members whose name matches, in the groups the kind filter allows.
        var selected = MembersOf(t, name => regex.IsMatch(name), kind);
        return selected.Any() ? DescribeMatchRow(t, typeKind, "member", selected) : null;
    }

    private static object DescribeMatchRow(Type t, string kind, string matchedBy, MemberSet? members) => new
    {
        fullName = t.FullName ?? t.Name,
        kind,
        matchedBy,
        baseType = SafeBase(t),
        members,
    };

    private static MetadataLoadContext Open(string assemblyPath, out Assembly assembly)
    {
        var asmDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in SafeDlls(runtimeDir)) paths.Add(f);
        foreach (var f in SafeDlls(asmDir)) paths.Add(f);
        paths.Add(Path.GetFullPath(assemblyPath));

        var resolver = new PathAssemblyResolver(paths);
        var context = new MetadataLoadContext(resolver);
        try
        {
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static object DescribeType(Type t, bool includeMembers)
    {
        string kind = ClassifyType(t);

        object? members = null;
        if (includeMembers && kind != "delegate")
        {
            try { members = MembersOf(t, nameFilter: null, kind: null); }
            catch (Exception ex) { members = new { error = ex.Message }; }
        }

        return new
        {
            fullName = t.FullName ?? t.Name,
            kind,
            isAbstract = t.IsAbstract && !t.IsInterface,
            isSealed = t.IsSealed,
            isStatic = t.IsAbstract && t.IsSealed,
            baseType = SafeBase(t),
            interfaces = InterfacesOf(t),
            members,
        };
    }

    private static MemberSet MembersOf(Type t, Predicate<string>? nameFilter, KindFilter? kind)
    {
        var set = MemberSet.Empty();
        foreach (var group in Enum.GetValues<MemberGroup>())
        {
            if (kind is { IsMember: true } && !string.Equals(GroupName(group), kind.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var member in EnumerateMembers(t, group))
                if (nameFilter is null || nameFilter(member.Name))
                    set.Add(group, member.Line);
        }

        set.SortLines();
        return set;
    }

    private static IEnumerable<(string Name, string Line)> EnumerateMembers(Type t, MemberGroup group)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return group switch
        {
            MemberGroup.Constructor => t.GetConstructors(flags)
                .Select(c => (c.DeclaringType?.Name ?? ".ctor", SigCtor(c))),
            MemberGroup.Method => t.GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Select(m => (m.Name, SigMethod(m))),
            MemberGroup.Property => t.GetProperties(flags)
                .Select(p => (p.Name,
                    $"{TypeName(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}".Trim())),
            MemberGroup.Field => t.GetFields(flags)
                .Where(f => !f.IsSpecialName)
                .Select(f => (f.Name,
                    $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{TypeName(f.FieldType)} {f.Name}")),
            MemberGroup.Event => t.GetEvents(flags)
                .Select(e => (e.Name, $"{TypeName(e.EventHandlerType!)} {e.Name}")),
            _ => [],
        };
    }

    private static string GroupName(MemberGroup group) => group switch
    {
        MemberGroup.Constructor => "constructor",
        MemberGroup.Method => "method",
        MemberGroup.Property => "property",
        MemberGroup.Field => "field",
        MemberGroup.Event => "event",
        _ => group.ToString().ToLowerInvariant(),
    };

    private static string ClassifyType(Type t) => t.IsEnum ? "enum"
        : t.IsInterface ? "interface"
        : t.IsValueType ? "struct"
        : (t.BaseType?.FullName == "System.MulticastDelegate" || t.BaseType?.FullName == "System.Delegate") ? "delegate"
        : "class";

    private static string[] InterfacesOf(Type t)
    {
        try { return t.GetInterfaces().Select(i => TypeName(i)).OrderBy(s => s).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static string? SafeBase(Type t)
    {
        try { return t.BaseType is { } b && b.FullName != "System.Object" ? TypeName(b) : null; }
        catch { return null; }
    }

    private static string SigMethod(MethodInfo m) =>
        $"{(m.IsStatic ? "static " : "")}{TypeName(m.ReturnType)} {m.Name}({Params(m)})";

    private static string SigCtor(ConstructorInfo c) => $"{c.DeclaringType?.Name}({Params(c)})";

    private static string Params(MethodBase m) =>
        string.Join(", ", m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

    private static string TypeName(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        }
        return t.Name;
    }

    private static IEnumerable<string> SafeDlls(string dir)
    {
        try { return Directory.GetFiles(dir, "*.dll"); }
        catch { return Array.Empty<string>(); }
    }

    private enum MemberGroup { Constructor, Method, Property, Field, Event }

    private sealed record KindFilter(string Name, bool IsMember);

    /// <summary>
    /// The five member lists of a type. A record so it serializes as the same
    /// { constructors, methods, properties, fields, events } shape inspect_assembly
    /// already returned; the helpers below are methods so they are not serialized.
    /// </summary>
    private sealed record MemberSet(
        List<string> Constructors,
        List<string> Methods,
        List<string> Properties,
        List<string> Fields,
        List<string> Events)
    {
        public static MemberSet Empty() => new([], [], [], [], []);

        public bool Any() =>
            Constructors.Count + Methods.Count + Properties.Count + Fields.Count + Events.Count > 0;

        public void Add(MemberGroup group, string line)
        {
            switch (group)
            {
                case MemberGroup.Constructor: Constructors.Add(line); break;
                case MemberGroup.Method: Methods.Add(line); break;
                case MemberGroup.Property: Properties.Add(line); break;
                case MemberGroup.Field: Fields.Add(line); break;
                case MemberGroup.Event: Events.Add(line); break;
            }
        }

        public void SortLines()
        {
            // Constructors keep declaration order, like inspect_assembly printed them.
            Methods.Sort(StringComparer.Ordinal);
            Properties.Sort(StringComparer.Ordinal);
            Fields.Sort(StringComparer.Ordinal);
            Events.Sort(StringComparer.Ordinal);
        }
    }
}
