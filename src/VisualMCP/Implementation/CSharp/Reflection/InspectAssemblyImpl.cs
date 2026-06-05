using System.Reflection;
using System.Runtime.InteropServices;

namespace VisualMCP.Implementation.CSharp.Reflection;

internal static class InspectAssemblyImpl
{
    internal static object Run(string assemblyPath, string? typeFilter, bool includeMembers, int maxTypes)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return new { error = $"Assembly not found: {assemblyPath}" };

        if (maxTypes < 1) maxTypes = 1;
        if (maxTypes > 2000) maxTypes = 2000;

        try
        {
            var asmDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in SafeDlls(runtimeDir)) paths.Add(f);
            foreach (var f in SafeDlls(asmDir))     paths.Add(f);
            paths.Add(Path.GetFullPath(assemblyPath));

            var resolver = new PathAssemblyResolver(paths);
            using var mlc = new MetadataLoadContext(resolver);
            var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

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

    private static object DescribeType(Type t, bool includeMembers)
    {
        string kind = t.IsEnum ? "enum"
            : t.IsInterface ? "interface"
            : t.IsValueType ? "struct"
            : (t.BaseType?.FullName == "System.MulticastDelegate" || t.BaseType?.FullName == "System.Delegate") ? "delegate"
            : "class";

        object? members = null;
        if (includeMembers && kind != "delegate")
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                var ctors = t.GetConstructors(flags).Select(c => SigCtor(c)).ToList();
                var methods = t.GetMethods(flags).Where(m => !m.IsSpecialName).Select(SigMethod).OrderBy(s => s).ToList();
                var properties = t.GetProperties(flags).Select(p =>
                    $"{TypeName(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}".Trim()).OrderBy(s => s).ToList();
                var fields = t.GetFields(flags).Where(f => !f.IsSpecialName).Select(f =>
                    $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{TypeName(f.FieldType)} {f.Name}").OrderBy(s => s).ToList();
                var events = t.GetEvents(flags).Select(e => $"{TypeName(e.EventHandlerType!)} {e.Name}").OrderBy(s => s).ToList();

                members = new { constructors = ctors, methods, properties, fields, events };
            }
            catch (Exception ex) { members = new { error = ex.Message }; }
        }

        string[] interfaces;
        try { interfaces = t.GetInterfaces().Select(i => TypeName(i)).OrderBy(s => s).ToArray(); }
        catch { interfaces = Array.Empty<string>(); }

        return new
        {
            fullName = t.FullName ?? t.Name,
            kind,
            isAbstract = t.IsAbstract && !t.IsInterface,
            isSealed = t.IsSealed,
            isStatic = t.IsAbstract && t.IsSealed,
            baseType = SafeBase(t),
            interfaces,
            members,
        };
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
}
