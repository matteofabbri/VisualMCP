using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Indexing;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Metacode as a COMPLETE AST kept in the solution database: every node of every declaration in
/// <c>mc_node</c>, with the parent link, the property slot, the position, the depth, the span and the
/// node's own payload; and the walk that produces it driven by the model's own declaration, so a node
/// type added to the model cannot be left out.
/// </summary>
public sealed class MetacodeAstTests
{
    private const string Sample = """
        namespace Demo;

        public sealed class Basket
        {
            private readonly List<int> _prices = new();

            public int Total(int threshold)
            {
                var total = 0;
                foreach (var price in _prices)
                {
                    if (price > threshold)
                    {
                        total += price;
                    }
                    else
                    {
                        total -= price;
                    }
                }

                return total;
            }
        }
        """;

    private static IReadOnlyList<MetacodeDeclaration> Declarations(out MetacodeDocument document)
    {
        document = new MetacodeTranslator([new CSharpFrontend()]).Translate(MetacodeSource.FromCSharp(Sample));
        return document.Modules.SelectMany(module => module.Declarations).ToList();
    }

    // ── the walk ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_walk_produces_a_complete_pre_order_tree()
    {
        var type = Assert.IsType<MetacodeTypeDeclaration>(Assert.Single(Declarations(out _)));

        var nodes = MetacodeAst.Serialize(type, "class:Demo.Basket");

        Assert.Equal("TypeDeclaration", nodes[0].Kind);
        Assert.Equal(1, nodes[0].NodeId);
        Assert.Equal(0, nodes[0].ParentId);
        Assert.Equal(0, nodes[0].Depth);
        Assert.Single(nodes, node => node.ParentId == 0);

        // One row per node, ids are the position, and a parent always comes first.
        Assert.Equal(Enumerable.Range(1, nodes.Count), nodes.Select(node => node.NodeId));
        Assert.All(nodes, node => Assert.True(node.ParentId < node.NodeId));
        Assert.All(nodes, node => Assert.Equal("class:Demo.Basket", node.DeclarationKey));

        // Siblings are numbered inside the parent's property, and every non-root row names one.
        foreach (var group in nodes.Where(node => node.ParentId != 0).GroupBy(node => (node.ParentId, node.Slot)))
        {
            Assert.Equal(Enumerable.Range(0, group.Count()), group.OrderBy(node => node.Ordinal).Select(node => node.Ordinal));
        }

        var kinds = nodes.Select(node => node.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "TypeDeclaration", "Function", "Parameter", "Block", "ForEach", "If", "Assignment",
                     "Binary", "LocalVariable", "Return", "Identifier", "Field", "ObjectCreation",
                 })
        {
            Assert.Contains(expected, kinds);
        }

        // Depth is not decoration: the if inside the loop is deeper than the loop.
        var loop = nodes.First(node => node.Kind == "ForEach");
        var branch = nodes.First(node => node.Kind == "If");
        Assert.True(branch.Depth > loop.Depth);
    }

    [Fact]
    public void A_row_carries_the_node_s_own_properties_as_json()
    {
        var type = Assert.IsType<MetacodeTypeDeclaration>(Assert.Single(Declarations(out _)));

        var nodes = MetacodeAst.Serialize(type, "class:Demo.Basket");
        var method = nodes.First(node => node.Kind == "Function");

        using var data = JsonDocument.Parse(method.Data);
        Assert.Equal("Total", data.RootElement.GetProperty("name").GetString());
        Assert.Equal("Method", data.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Public", data.RootElement.GetProperty("modifiers").GetString());

        // A child is a row, not a value embedded in the parent's JSON.
        Assert.False(data.RootElement.TryGetProperty("body", out _));
        Assert.False(data.RootElement.TryGetProperty("parameters", out _));
        Assert.Contains(nodes, node => node.ParentId == method.NodeId && node.Slot == nameof(MetacodeFunction.Body));
    }

    [Fact]
    public void Every_node_type_the_model_declares_is_walked()
    {
        var nodeTypes = typeof(MetacodeNode).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(MetacodeNode).IsAssignableFrom(type))
            .ToList();

        Assert.True(nodeTypes.Count >= 50, $"expected the model's node vocabulary, found {nodeTypes.Count} types");

        foreach (var type in nodeTypes)
        {
            var expected = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(constructor => constructor.GetParameters().Length)
                .First()
                .GetParameters()
                .Length;

            var mapped = MetacodeAst.DeclarationProperties(type);

            // Every value the record declares must resolve to a property the walk can read; a mismatch
            // is how a node would silently serialize without part of itself.
            Assert.True(
                mapped.Count == expected,
                $"{type.Name}: {expected} declared value(s), {mapped.Count} reachable through properties");

            foreach (var property in mapped)
            {
                if (MetacodeAst.IsChild(property.PropertyType)) continue;

                Assert.True(
                    IsPayload(property.PropertyType),
                    $"{type.Name}.{property.Name} is {property.PropertyType}, which the walk would JSON-serialize without knowing its shape");
            }
        }
    }

    // ── the store ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_build_stores_every_node_and_the_service_returns_the_tree()
    {
        using var workspace = FakeWorkspace.Create(("Basket.cs", Sample));
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        var report = await service.BuildAsync();
        Assert.Null(report.Error);
        Assert.True(report.Nodes > 30, $"expected the AST nodes, got {report.Nodes}");
        Assert.Equal(report.Nodes, service.Status().Nodes);

        var ast = service.Ast(symbol: "Total");
        Assert.Null(ast.Error);
        Assert.False(ast.Truncated);

        var tree = Assert.Single(ast.Declarations);
        Assert.Equal("method:Demo.Basket.Total(1)", tree.Key);
        Assert.Equal("method", tree.Kind);
        Assert.Equal("Function", Assert.Single(tree.Root).Kind);
        Assert.Equal("Total", Assert.Single(tree.Root).Data.GetProperty("name").GetString());
        Assert.NotEmpty(tree.Root[0].Children);
        Assert.True(tree.Nodes > 5);
        Assert.True(tree.MaxDepth > 2);

        using var connection = Open(store.DatabasePath!);

        // The rows are the tree: every non-root node names a parent that is in the same declaration.
        Assert.Equal(0, Scalar(connection, """
            SELECT COUNT(*) FROM mc_node child
            WHERE child.parent_id <> 0
              AND NOT EXISTS (
                  SELECT 1 FROM mc_node parent
                  WHERE parent.path = child.path
                    AND parent.declaration_key = child.declaration_key
                    AND parent.node_id = child.parent_id);
            """));

        // The method's AST is stored once under its own key (the type's tree nests it too).
        Assert.Equal(tree.Nodes, Scalar(connection,
            $"SELECT COUNT(*) FROM mc_node WHERE declaration_key = '{tree.Key}';"));
    }

    [Fact]
    public async Task A_path_answers_with_every_declaration_of_the_file()
    {
        using var workspace = FakeWorkspace.Create(("Basket.cs", Sample));
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        await service.BuildAsync();

        var ast = service.Ast(path: workspace.PathOf("Basket.cs"));

        Assert.Null(ast.Error);
        Assert.Equal(3, ast.Declarations.Count); // the class, its field and its method
        Assert.Equal("class:Demo.Basket", ast.Declarations[0].Key);
        Assert.Equal("TypeDeclaration", ast.Declarations[0].Root[0].Kind);
        Assert.Equal("field:Demo.Basket._prices", ast.Declarations[1].Key);
        Assert.Equal("method:Demo.Basket.Total(1)", ast.Declarations[2].Key);
        Assert.Equal("Function", ast.Declarations[2].Root[0].Kind);
    }

    [Fact]
    public async Task The_cap_cuts_the_answer_and_says_so()
    {
        using var workspace = FakeWorkspace.Create(("Basket.cs", Sample));
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        await service.BuildAsync();

        var cut = service.Ast(symbol: "Total", maxNodes: 3);

        Assert.True(cut.Truncated);
        Assert.Equal(3, cut.Nodes);
    }

    [Fact]
    public async Task Replacing_a_document_replaces_its_nodes()
    {
        using var workspace = FakeWorkspace.Create(("Basket.cs", Sample));
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        var first = await service.BuildAsync();
        var path = workspace.PathOf("Basket.cs");

        File.WriteAllText(path, "namespace Demo;\n\npublic sealed class Basket\n{\n}\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var second = await service.BuildAsync(force: true);
        Assert.Equal(0, second.Failed);

        using var connection = Open(store.DatabasePath!);
        Assert.Equal(0, Scalar(connection,
            "SELECT COUNT(*) FROM mc_node WHERE declaration_key = 'method:Demo.Basket.Total(1)';"));
        Assert.True(second.Nodes < first.Nodes, "the method's nodes should be gone with the method");
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM mc_node;"));
    }

    [Fact]
    public async Task An_index_written_before_mc_node_says_to_rebuild()
    {
        using var workspace = FakeWorkspace.Create(("Basket.cs", Sample));
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        await service.BuildAsync();

        using (var connection = Open(store.DatabasePath!))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mc_node;";
            command.ExecuteNonQuery();
        }

        var ast = service.Ast(symbol: "Total");

        Assert.NotNull(ast.Error);
        Assert.Contains("force=true", ast.Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The payload vocabulary: what the walk writes as JSON instead of turning into a row.</summary>
    private static bool IsPayload(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsEnum) return true;
        if (type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long)) return true;
        if (type == typeof(MetacodeTypeRef)) return true;
        if (type.IsArray) return type.GetElementType() is { } element && IsPayload(element);
        if (type.IsGenericType) return type.GetGenericArguments().All(IsPayload);
        return false;
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        return connection;
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
