using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Snitch.Planning;
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
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, Array.Empty<int>()));
        Assert.Equal(new[] { 4677, 5123 }, DashboardOptions.Candidates(4677, new[] { 5123 }));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, new[] { 4677 }));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, new[] { 0 }));
        Assert.Equal(new[] { 4677 }, DashboardOptions.Candidates(4677, new[] { 70_000 }));
        Assert.Empty(DashboardOptions.Candidates(0, Array.Empty<int>()));
    }

    [Fact]
    public void A_bound_port_is_remembered_per_instance()
    {
        _database.RememberPort(5123);

        var stored = Assert.Single(new DashboardDatabase(_store).StoredPorts());
        Assert.Equal(5123, stored.Port);
        Assert.Equal(Environment.ProcessId, stored.Pid);
        Assert.True(stored.IsAlive());
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
        services.AddSingleton<DashboardBoard>();
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

    // ── the board ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_board_page_renders_the_five_columns_and_the_card_detail()
    {
        await new BoardService(_store).AddAsync(
            "First card", "The description", "todo", "high", ["api"], "me");

        var (provider, server) = await StartBoardAsync();
        await using var host = provider;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var root = server.Info.Url!.TrimEnd('/');

            var page = await client.GetStringAsync($"{root}/board");
            Assert.Contains("First card", page);
            Assert.Contains("In progress", page);       // the five columns are always drawn
            Assert.Contains("Drag a card", page);
            Assert.Contains("New card", page);

            var detail = await client.GetStringAsync($"{root}/board?card=1");
            Assert.Contains("Card #1", detail);
            Assert.Contains("The description", detail);
            Assert.Contains("Save changes", detail);
            Assert.Contains("History", detail);

            var api = await client.GetStringAsync($"{root}/api/board");
            Assert.Contains("\"title\": \"First card\"", api);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task A_board_write_needs_the_header_and_a_same_origin_json_body()
    {
        await new BoardService(_store).AddAsync("Movable", null, "backlog", "medium", null, null);

        var (provider, server) = await StartBoardAsync();
        await using var host = provider;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var root = server.Info.Url!.TrimEnd('/');

            // A plain JSON POST — what a form or another site could send — is refused.
            var refused = await client.PostAsync(
                $"{root}/api/board/move", JsonContent.Create(new { id = 1, status = "done" }));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // The header is there, the origin is not: still refused.
            var foreign = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/board/move")
            {
                Content = JsonContent.Create(new { id = 1, status = "done" }),
            };
            foreign.Headers.Add("X-VisualMCP", "1");
            foreign.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

            // This page's own request succeeds — and a same-origin Origin header is accepted.
            var move = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/board/move")
            {
                Content = JsonContent.Create(new { id = 1, status = "done", position = 0 }),
            };
            move.Headers.Add("X-VisualMCP", "1");
            move.Headers.Add("Origin", root);
            var moved = await client.SendAsync(move);
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            Assert.Contains("\"ok\": true", await moved.Content.ReadAsStringAsync());

            var snapshot = await client.GetStringAsync($"{root}/api/board");
            Assert.Contains("\"status\": \"done\"", snapshot);

            // A status the board does not have is a 400 with the service's own explanation.
            var invalid = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/board/move")
            {
                Content = JsonContent.Create(new { id = 1, status = "nope" }),
            };
            invalid.Headers.Add("X-VisualMCP", "1");
            var bad = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("not a column", await bad.Content.ReadAsStringAsync());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task A_card_can_be_added_commented_edited_and_deleted_from_the_page()
    {
        var (provider, server) = await StartBoardAsync();
        await using var host = provider;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var root = server.Info.Url!.TrimEnd('/');

            var added = await BoardWrite(client, $"{root}/api/board/add", new
            {
                title = "From the web", status = "review", priority = "highest",
            });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);

            int id;
            using (var json = JsonDocument.Parse(await added.Content.ReadAsStringAsync()))
                id = json.RootElement.GetProperty("id").GetInt32();
            Assert.True(id > 0);

            var commented = await BoardWrite(client, $"{root}/api/board/comment", new
            {
                id, body = "looks good", author = "test",
            });
            Assert.Equal(HttpStatusCode.OK, commented.StatusCode);

            var updated = await BoardWrite(client, $"{root}/api/board/update", new
            {
                id, title = "Renamed", description = "New body", priority = "low",
                assignee = "qa", labels = new[] { "web" },
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

            var detail = await client.GetStringAsync($"{root}/board?card={id}");
            Assert.Contains("Renamed", detail);
            Assert.Contains("New body", detail);
            Assert.Contains("looks good", detail);
            Assert.Contains("qa", detail);

            // An empty label set clears the labels; the board answers with none.
            var cleared = await BoardWrite(client, $"{root}/api/board/update", new
            {
                id, labels = Array.Empty<string>(),
            });
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            Assert.Contains("\"labels\": []", await client.GetStringAsync($"{root}/api/board"));

            var deleted = await BoardWrite(client, $"{root}/api/board/delete", new { id });
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            var after = await client.GetStringAsync($"{root}/board");
            Assert.DoesNotContain("Renamed", after);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>A dashboard host whose port is assigned by the OS; the caller stops it.</summary>
    private async Task<(ServiceProvider Provider, DashboardServer Server)> StartBoardAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVisualMcpStore>(_store);
        services.AddSingleton(new DashboardOptions { PreferredPort = 0 });
        services.AddSingleton<DashboardDatabase>();
        services.AddSingleton<DashboardBoard>();
        services.AddSingleton<DashboardServer>();

        var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<DashboardServer>();
        Assert.True(await server.StartAsync(), server.Info.LastError);
        return (provider, server);
    }

    /// <summary>One guarded board write: JSON body plus the header the page's script sends.</summary>
    private static async Task<HttpResponseMessage> BoardWrite(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-VisualMCP", "1");
        return await client.SendAsync(request);
    }
}
