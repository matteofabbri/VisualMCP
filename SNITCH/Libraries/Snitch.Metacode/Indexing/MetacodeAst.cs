using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snitch.Metacode.Indexing;

/// <summary>
/// One node of a declaration's AST in the flat shape the store keeps: which node it is, the node that
/// holds it (and through which property), its position among siblings, how deep it sits, where it came
/// from, and everything the node says about itself as JSON.
/// </summary>
/// <remarks>
/// <see cref="ParentId"/> 0 marks the declaration itself, so a subtree is reconstructible from
/// <c>ORDER BY node_id</c> alone. <see cref="Slot"/> is the parent's property name
/// (<c>Then</c>, <c>Arguments</c>) and <see cref="Ordinal"/> the index inside it, which is what turns a
/// parent link back into a field of the model.
/// </remarks>
public sealed record MetacodeAstNode(
    string DeclarationKey,
    int NodeId,
    int ParentId,
    string Slot,
    int Ordinal,
    int Depth,
    string Kind,
    int Line,
    int Column,
    int EndLine,
    string Data);

/// <summary>
/// Turns a metacode node into the rows that make it a complete, queryable tree in SQLite.
/// </summary>
/// <remarks>
/// <para>
/// The walk is driven by each record's primary constructor, not by a hand-written switch: every
/// positional property of a node type is, by construction, either a child node, a list of child nodes,
/// or a scalar that goes into <see cref="MetacodeAstNode.Data"/>. A node type added to the model is
/// therefore serialized the day it exists, with no list to update — which is the difference between a
/// complete tree and a rendering that happens to cover today's constructs.
/// </para>
/// <para>
/// <see cref="MetacodeOrigin"/> is not a constructor property of any concrete node: it is carried by the
/// span columns (<see cref="MetacodeAstNode.Line"/>, column, end line) instead of being repeated in
/// every row's JSON. Computed properties (<c>ParameterList</c>, <c>ArgumentList</c>, <c>IsAuto</c>) are
/// not constructor properties either, so they cannot duplicate a child that is already there.
/// </para>
/// </remarks>
public static class MetacodeAst
{
    private static readonly ConcurrentDictionary<Type, ParameterInfo[]> ConstructorCache = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        // The payload is a dictionary, and a naming policy alone does not touch dictionary keys:
        // without DictionaryKeyPolicy the stored JSON would keep the record's PascalCase.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new TypeRefConverter() },
    };

    /// <summary>
    /// Every node of <paramref name="declaration"/>'s subtree, the declaration itself included as the
    /// root (id 1, parent 0), in pre-order.
    /// </summary>
    public static IReadOnlyList<MetacodeAstNode> Serialize(MetacodeDeclaration declaration, string declarationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationKey);

        var rows = new List<MetacodeAstNode>();
        Walk(declaration, declarationKey, parent: 0, slot: string.Empty, ordinal: 0, depth: 0, rows);
        return rows;
    }

    /// <summary>
    /// True when the property is a child edge — a node, or a list of nodes — and therefore a row of its
    /// own. Anything else is part of the parent's JSON payload.
    /// </summary>
    public static bool IsChild(Type propertyType) =>
        typeof(MetacodeNode).IsAssignableFrom(propertyType) || NodeElement(propertyType) is not null;

    /// <summary>The node type a list property holds, or null when the list is not a list of nodes.</summary>
    public static Type? NodeElement(Type propertyType)
    {
        if (propertyType == typeof(string)) return null;

        if (propertyType.IsArray)
        {
            var element = propertyType.GetElementType();
            return element is not null && typeof(MetacodeNode).IsAssignableFrom(element) ? element : null;
        }

        if (!propertyType.IsGenericType || propertyType.GetGenericArguments().Length != 1) return null;

        var argument = propertyType.GetGenericArguments()[0];
        return typeof(MetacodeNode).IsAssignableFrom(argument) ? argument : null;
    }

    /// <summary>The model's name for a node type: <c>MetacodeIf</c> is <c>If</c>, <c>MetacodeBinary</c> is <c>Binary</c>.</summary>
    public static string KindOf(Type type)
    {
        var name = type.Name;
        return name.StartsWith("Metacode", StringComparison.Ordinal) ? name["Metacode".Length..] : name;
    }

    /// <summary>
    /// The properties a node type declares through its primary constructor, in declaration order. For a
    /// record that is exactly what the type says it holds; for a class with no such constructor it is
    /// empty, and <see cref="Describe"/> reports the mismatch instead of dropping data silently.
    /// </summary>
    public static IReadOnlyList<PropertyInfo> DeclarationProperties(Type type)
    {
        var parameters = ConstructorCache.GetOrAdd(type, static t =>
            t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(constructor => constructor.GetParameters().Length)
                .FirstOrDefault()
                ?.GetParameters() ?? []);

        var properties = new List<PropertyInfo>(parameters.Length);
        foreach (var parameter in parameters)
        {
            if (parameter.Name is not { Length: > 0 } name) continue;

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is not null) properties.Add(property);
        }

        return properties;
    }

    private static void Walk(
        MetacodeNode node, string key, int parent, string slot, int ordinal, int depth, List<MetacodeAstNode> rows)
    {
        // Pre-order and one row per node: the id is the row's position, so a parent is always before
        // its children and ORDER BY node_id rebuilds the tree.
        var id = rows.Count + 1;
        var origin = node.Origin;

        rows.Add(new MetacodeAstNode(
            key, id, parent, slot, ordinal, depth, KindOf(node.GetType()),
            origin.IsKnown ? origin.StartLine : 0,
            origin.IsKnown ? origin.StartColumn : 0,
            origin.IsKnown ? origin.EndLine : 0,
            Describe(node)));

        foreach (var property in DeclarationProperties(node.GetType()))
        {
            var value = property.GetValue(node);
            if (value is null) continue;

            // Ordinal counts inside one property of one parent: a single-node slot always holds its
            // child at 0, a list numbers its items.
            if (value is MetacodeNode child)
            {
                Walk(child, key, id, property.Name, ordinal: 0, depth + 1, rows);
                continue;
            }

            if (NodeElement(property.PropertyType) is not null && value is System.Collections.IEnumerable children)
            {
                var index = 0;
                foreach (var item in children)
                    if (item is MetacodeNode childOfList)
                        Walk(childOfList, key, id, property.Name, index++, depth + 1, rows);
            }
        }
    }

    /// <summary>The node's own non-child properties as one JSON object, so a row is self-describing.</summary>
    private static string Describe(MetacodeNode node)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in DeclarationProperties(node.GetType()))
        {
            if (IsChild(property.PropertyType)) continue;

            var value = property.GetValue(node);
            if (value is not null) values[property.Name] = value;
        }

        return values.Count == 0 ? "{}" : JsonSerializer.Serialize(values, Json);
    }

    /// <summary>
    /// A type reference has computed properties (<c>Arguments</c>, <c>IsDynamic</c>) that would duplicate
    /// what the record already stores, so the payload keeps only what was written.
    /// </summary>
    private sealed class TypeRefConverter : JsonConverter<MetacodeTypeRef>
    {
        public override MetacodeTypeRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // The stored form is written, never read back by the index.
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, MetacodeTypeRef value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);

            if (value.TypeArguments is { Count: > 0 } arguments)
            {
                writer.WritePropertyName("typeArguments");
                writer.WriteStartArray();
                foreach (var argument in arguments) Write(writer, argument, options);
                writer.WriteEndArray();
            }

            if (value.IsArray) writer.WriteBoolean("isArray", true);
            if (value.IsPointer) writer.WriteBoolean("isPointer", true);
            if (value.IsReference) writer.WriteBoolean("isReference", true);
            if (value.IsNullable) writer.WriteBoolean("isNullable", true);
            writer.WriteEndObject();
        }
    }
}
