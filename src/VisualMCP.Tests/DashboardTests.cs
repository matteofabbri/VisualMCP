using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Snitch.Storage;
using VisualMCP.Dashboard;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The web dashboard: the read-only SQLite view, the port policy, and the pages themselves
/// (served by a real listener on an OS-assigned port, so routing and escaping are exercised
/// the way the browser sees them).
/// </summary>
public sealed class DashboardTests : IDisposable
{
    private readonly FakeWorkspace _workspace;
    private readonly VisualMcpStore _store;
    private readonly DashboardDatabase _database;

    public DashboardTests()
    {
        _workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        _store = new VisualMcpStore(_workspace);

        _store.Set("inspection:probe", "{\"value\":1}", "stamp-1");
        _store.RecordCommand("find_symbol", "{\"symbolName\":\"C\"}");
        _store.RecordCommand("simplify_code", "{\"rules\":\"all\"}");
        _store.RecordEdit("simplify_code", _workspace.PathOf("C.cs"), "IDE0001",
            beforeText: "before secretneedle text", afterText: "after text");
        _store.ReplacePackages("Probe", [new PackageFact("Probe", "xunit", "2.9.3", "net10.0", true)]);
        _store.ReplacePackageDependencies("Probe",
            [new PackageDependency("Probe", "xunit", "2.9.3", "net10.0", "xunit.core", "2.9.3")]);
        _store.RecordArtifact(new DecompiledArtifact(
            "mvid-1", "Probe.dll", "Probe", _workspace.RootDirectory, 12, DateTimeOffset.UtcNow));

        _database = new DashboardDatabase(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        _workspace.Dispose();
    }

    // ── the data layer ───────────────────────────────────────────────────────

    [Fact]
    public void Every_table_gets_a_page_and_the_fts5_shadow_tables_are_left_out()
    {
        using var text = new Snitch.Search.TextIndexStore();
        Assert.True(text.Open(_store.DatabasePath!));
        using var symbols = new Snitch.Metacode.Symbols.SymbolIndexStore();
        Assert.True(symbols.Open(_store.DatabasePath!));

        var names = _database.Tables().Select(t => t.Name).ToList();

        Assert.Contains("command_log", names);
        Assert.Contains("applied_edit", names);
        Assert.Contains("package_fact", names);
        Assert.Contains("decompiled_artifact", names);
        Assert.Contains("ti_document", names);
        Assert.Contains("ti_line", names);
        Assert.Contains("si_symbol", names);

        // FTS5 keeps its index in shadow tables: they are not something to browse.
        Assert.DoesNotContain("ti_line_data", names);
        Assert.DoesNotContain("ti_line_idx", names);
        Assert.DoesNotContain("ti_line_content", names);

        foreach (var table in _database.Tables())
        {
            Assert.NotNull(_database.Info(table.Name));
            Assert.NotNull(_database.Read(table.Name, page: 1, pageSize: 25, sort: null, descending: true, search: null));
        }
    }

    [Fact]
    public void Rows_filter_by_substring_and_page()
    {
        var first = _database.Read("command_log", 1, 1, null, true, null);
        Assert.NotNull(first);
        Assert.Equal(2, first!.Total);
        Assert.Single(first.Rows);
        Assert.Equal(2, first.PageCount);

        var filtered = _database.Read("command_log", 1, 10, null, true, "simplify");
        Assert.NotNull(filtered);
        Assert.Equal(1, filtered!.Total);

        var tool = filtered.Columns.Select((column, index) => (column, index))
            .First(pair => pair.column.Name == "tool").index;
        Assert.Equal("simplify_code", filtered.Rows[0][tool]);
    }

    [Fact]
    public void A_name_that_is_not_a_table_never_reaches_the_sql()
    {
        Assert.Null(_database.Read("command_log; DROP TABLE command_log", 1, 10, null, true, null));
        Assert.Null(_database.Info("nope"));

        // The table survived the injection attempt above.
        Assert.Equal(2, _database.Read("command_log", 1, 10, null, true, null)!.Total);
    }

    [Fact]
    public void Blob_columns_are_neither_searched_nor_rendered_as_text()
    {
        // "secretneedle" exists only inside applied_edit.before_text, which is a compressed blob.
        Assert.Equal(0, _database.Read("applied_edit", 1, 10, null, true, "secretneedle")!.Total);
        Assert.Equal(1, _database.Read("applied_edit", 1, 10, null, true, "simplify_code")!.Total);
    }

    [Fact]
    public void The_port_policy_is_preferred_then_remembered_then_random()
    {
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, stored: null));
        Assert.Equal(new[] { 4677, 5123 }, DashboardOptions.Candidates(4677, 5123));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, 4677));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, stored: 0));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, stored: 70_000));
        Assert.Empty(DashboardOptions.Candidates(0, stored: null));
    }

    [Fact]
    public void A_bound_port_is_remembered_in_the_store()
    {
        _database.RememberPort(5123);

        Assert.Equal(5123, new DashboardDatabase(_store).StoredPort());
    }

    [Fact]
    public void The_commands_invoked_come_back_newest_first_with_the_total()
    {
        var trail = _database.RecentCommands();

        Assert.Equal(2, trail.Total);
        Assert.Equal(new[] { "simplify_code", "find_symbol" }, trail.Items.Select(item => item.Tool));
        Assert.All(trail.Items, item => Assert.True(
            DateTimeOffset.TryParse(item.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            item.OccurredAt));

        // The limit bounds the list, never the total: the sidebar still says how many were invoked.
        var one = _database.RecentCommands(limit: 1);
        Assert.Equal(2, one.Total);
        Assert.Equal("simplify_code", Assert.Single(one.Items).Tool);
    }

    // ── the served pages ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_dashboard_serves_a_page_per_table_and_escapes_what_it_reads()
    {
        _store.RecordCommand("echo", "{\"text\":\"<script>alert(1)</script>\"}");

        var logPath = Path.Combine(_workspace.RootDirectory, "dashboard.log");
        File.WriteAllText(logPath,
            "12:00:00.001 [TOOL ] VisualMCP.ToolCalls: >>> TOOL CALL read_file {\"path\":\"C.cs\"}\n" +
            "12:00:00.002 [INFO ] VisualMCP.Startup: ready\n");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVisualMcpStore>(_store);
        services.AddSingleton(new DashboardOptions { PreferredPort = 0, LogPath = logPath });
        services.AddSingleton<DashboardDatabase>();
        services.AddSingleton<DashboardServer>();

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<DashboardServer>();

        try
        {
            Assert.True(await server.StartAsync(), server.Info.LastError);
            Assert.NotNull(server.Info.Url);
            Assert.InRange(server.Info.Port, 1, 65535);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var root = server.Info.Url!.TrimEnd('/');

            var home = await client.GetStringAsync($"{root}/");
            Assert.Contains("What the server is doing", home);
            Assert.Contains("Commands invoked", home);
            Assert.Contains("href=\"/t/command_log?q=simplify_code\"", home);

            // The left column is on every page, not only the overview.
            var schema = await client.GetStringAsync($"{root}/schema");
            Assert.Contains("Commands invoked", schema);
            Assert.Contains("href=\"/t/command_log?q=find_symbol\"", schema);

            var tables = await client.GetStringAsync($"{root}/tables");
            foreach (var table in _database.Tables())
                Assert.Contains($"/t/{table.Name}", tables);

            foreach (var table in _database.Tables())
            {
                var response = await client.GetAsync($"{root}/t/{table.Name}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var commandLog = await client.GetStringAsync($"{root}/t/command_log");
            Assert.Contains("find_symbol", commandLog);
            Assert.DoesNotContain("<script>alert(1)</script>", commandLog);
            Assert.Contains("&lt;script&gt;", commandLog);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{root}/t/not-a-table")).StatusCode);

            var csv = await client.GetAsync($"{root}/t/command_log?format=csv");
            Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
            Assert.Contains("tool,arguments", await csv.Content.ReadAsStringAsync());

            var json = await client.GetStringAsync($"{root}/api/tables/command_log");
            Assert.Contains("\"total\": 3", json);

            var health = await client.GetStringAsync($"{root}/api/health");
            Assert.Contains("\"ok\": true", health);

            // No MCP server is registered in this container, so the tools page uses the
            // attributes on the tool classes — the same names the client is offered.
            var tools = await client.GetStringAsync($"{root}/tools");
            Assert.Contains("read_file", tools);

            var log = await client.GetStringAsync($"{root}/log");
            Assert.Contains("read_file", log);

            var fragment = await client.GetStringAsync($"{root}/log?fragment=1");
            Assert.StartsWith("<div class=\"log\">", fragment);
            Assert.Contains("read_file", fragment);
            Assert.True(
                fragment.IndexOf("ready", StringComparison.Ordinal) < fragment.IndexOf("read_file", StringComparison.Ordinal),
                "the newest line is rendered first");
        }
        finally
        {
            await server.StopAsync();
            Assert.False(server.IsRunning);
        }
    }
}
