using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Snitch;

namespace VisualMCP.Dashboard;

/// <summary>What the page needs to know about the web host that is rendering it.</summary>
public sealed record DashboardInfo(int Port, string? Url, string Status, string? LastError, DateTimeOffset StartedAt);

/// <summary>The query behind one table page: filter, sort, page, and the output format.</summary>
public sealed record DashboardTableQuery(
    int Page = 1,
    int Size = DashboardDatabase.DefaultPageSize,
    string? Sort = null,
    bool Descending = true,
    string? Search = null,
    string Format = "html");

/// <summary>The query behind the live log page.</summary>
public sealed record DashboardLogQuery(
    string? Search = null, int Lines = 300, string? Only = null, bool Live = true, bool Fragment = false);

/// <summary>
/// The dashboard's pages. They are server-rendered strings assembled from <see cref="Html"/>
/// and read-only facts from <see cref="DashboardDatabase"/> and the running process; nothing
/// here writes to the solution, so a page can be requested at any time without side effects.
/// </summary>
internal sealed class DashboardPages
{
    private const int CellPreview = 420;
    private const int CellMultilinePreview = 220;
    private const int CellFull = 8_000;

    // The tag is the logger's short name (TOOL, REQ) or the level ("Information", "Warning"),
    // which is why the bracket content allows more than a handful of characters.
    private static readonly Regex LogLine = new(
        @"^(?<time>\d{2}:\d{2}:\d{2}\.\d{3}) \[(?<tag>[^\]]{1,16})\] (?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly DashboardDatabase _database;
    private readonly DashboardOptions _options;
    private readonly IServiceProvider _services;
    private readonly string _version;

    public DashboardPages(DashboardDatabase database, DashboardOptions options, IServiceProvider services)
    {
        _database = database;
        _options = options;
        _services = services;
        _version = typeof(DashboardPages).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    // ── overview ─────────────────────────────────────────────────────────────

    public string Home(DashboardInfo info)
    {
        var catalogue = _database.Catalogue();
        var counts = catalogue.ToDictionary(t => t.Table.Name, t => t.Rows, StringComparer.OrdinalIgnoreCase);

        long Count(string table) => counts.TryGetValue(table, out var value) ? value : 0;

        var uptime = DateTimeOffset.UtcNow - info.StartedAt;
        var body = new StringBuilder();

        body.Append("<h1>What the server is doing</h1>");
        body.Append("<p class=\"sub\">A read-only view of this VisualMCP process and the solution store it writes. ");
        body.Append("Everything on this page is live: reload it, or follow the live log.</p>");

        if (!_database.Exists)
            body.Append(Html.Notice(
                "<b>No solution store yet.</b> The dashboard is up, but no solution has been located under " +
                $"<span class=\"mono\">{Html.E(Environment.CurrentDirectory)}</span>, so there are no tables to show. " +
                "The store appears as soon as a solution is discovered."));

        body.Append("<div class=\"cards\">");
        body.Append(Html.Kpi(Html.Duration(uptime), "Uptime", info.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        body.Append(Html.Kpi(Html.Number(Count("command_log")), "Tool calls logged"));
        body.Append(Html.Kpi(Html.Number(Count("applied_edit")), "File edits recorded"));
        body.Append(Html.Kpi(Html.Number(Count("board_item")), "Board items"));
        body.Append(Html.Kpi(Html.Number(Count("si_symbol")), "Symbol declarations"));
        body.Append(Html.Kpi(Html.Number(Count("ti_document")), "Indexed documents"));
        body.Append(Html.Kpi(Html.Number(Count("package_fact")), "Package facts"));
        body.Append("</div>");

        body.Append("<div class=\"grid2\">");
        body.Append(Html.Card("Server", ServerFacts(info)));        body.Append(Html.Card("Solutions", SolutionsTable()));
        body.Append("</div>");

        body.Append(Html.Card("Recent tool calls", RecentToolCalls(),
            Html.Link("/t/command_log", "all calls →", "btn")));

        body.Append(Html.Card("Recent edits", RecentEdits(),
            Html.Link("/t/applied_edit", "all edits →", "btn")));

        body.Append(Html.Card("Where to look", QuickLinks()));

        return Page("Overview", "overview", info, body.ToString());
    }

    private string ServerFacts(DashboardInfo info)
    {
        using var process = Process.GetCurrentProcess();

        var facts = new List<(string, string)>
        {
            ("Process", $"{Html.E(process.ProcessName)} <span class=\"dim\">·</span> pid {process.Id}"),
            ("Started", Html.E(info.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))),
            ("Working set", $"{Html.Bytes(process.WorkingSet64)} <span class=\"dim\">·</span> {process.Threads.Count} threads"),
            ("Managed heap", Html.Bytes(GC.GetTotalMemory(forceFullCollection: false))),
            ("Runtime", $"dotnet {Html.E(Environment.Version)} <span class=\"dim\">·</span> {Html.E(Environment.OSVersion.VersionString)}"),
            ("Transport", "stdio <span class=\"dim\">(JSON-RPC on stdin/stdout)</span>"),
            ("Working directory", Html.E(Environment.CurrentDirectory)),
            ("Dashboard", info.Url is null
                ? Html.E(info.Status)
                : $"<a href=\"{Html.A(info.Url)}\">{Html.E(info.Url)}</a>"),
            ("Database", _database.DatabasePath is { } db
                ? $"{Html.E(db)} <span class=\"dim\">·</span> {Html.Bytes(_database.SizeBytes)}"
                : "<span class=\"dim\">not created yet</span>"),
            ("Log file", _options.LogPath is { } log
                ? Html.Link("/log", Html.E(log), "mono", html: true)
                : "<span class=\"dim\">not configured</span>"),
            ("VisualMCP", Html.E(_version) + " <span class=\"dim\">· dashboard</span>"),
        };

        return Html.Facts(facts);
    }

    private string SolutionsTable()
    {
        var context = _services.GetService<SnitchContext>();
        IReadOnlyList<SnitchSolution> solutions;
        try { solutions = context?.Solutions ?? []; }
        catch { solutions = []; }

        if (solutions.Count == 0)
            return Html.Empty("No solution discovered under the working directory.");

        var active = context?.ActiveSolution?.Path;
        var rows = solutions.Select(solution =>
        {
            var loaded = solution.IsLoaded;
            var projects = solution.Loaded?.Projects.Count();
            var documents = solution.Loaded?.Projects.Sum(p => p.Documents.Count());
            var state = loaded
                ? Html.Chip("loaded", "ok")
                : Html.Chip("not loaded", "warn");
            if (string.Equals(solution.Path, active, StringComparison.OrdinalIgnoreCase))
                state += " " + Html.Chip("active", "acc");

            return (IReadOnlyList<string>)
            [
                Html.E(solution.Name) + "<br><span class=\"dim mono\">" + Html.E(solution.Directory) + "</span>",
                state,
                projects is null ? "<span class=\"dim\">—</span>" : Html.Number(projects.Value),
                documents is null ? "<span class=\"dim\">—</span>" : Html.Number(documents.Value),
            ];
        });

        return Html.Grid(["Solution", "State", "Projects", "Documents"], rows);
    }

    private string RecentToolCalls()
    {
        var rows = _database.Read("command_log", 1, 12, null, true, null);
        if (rows is null || rows.Rows.Count == 0)
            return Html.Empty("No tool call has been logged yet.");

        var at = Column(rows, "occurred_at");
        var tool = Column(rows, "tool");
        var arguments = Column(rows, "arguments");

        return Html.Grid(["When", "Tool", "Arguments"], rows.Rows.Select(row => (IReadOnlyList<string>)
        [
            Cell(row, at, rows.Columns),
            Html.Chip(AsText(row, tool), "acc"),
            Cell(row, arguments, rows.Columns),
        ]));
    }

    private string RecentEdits()
    {
        var rows = _database.Read("applied_edit", 1, 8, null, true, null);
        if (rows is null || rows.Rows.Count == 0)
            return Html.Empty("No edit has been recorded yet.");

        var at = Column(rows, "applied_at");
        var tool = Column(rows, "tool");
        var file = Column(rows, "file");
        var before = Column(rows, "before_chars");
        var after = Column(rows, "after_chars");

        return Html.Grid(["When", "Tool", "File", "Chars"], rows.Rows.Select(row =>
        {
            var path = AsText(row, file);
            var name = Path.GetFileName(path);
            var delta = Convert.ToInt64(row[after] ?? 0L, CultureInfo.InvariantCulture)
                      - Convert.ToInt64(row[before] ?? 0L, CultureInfo.InvariantCulture);
            var sign = delta > 0 ? "+" : "";
            return (IReadOnlyList<string>)
            [
                Cell(row, at, rows.Columns),
                Html.Chip(AsText(row, tool), "acc"),
                $"<span class=\"mono\" title=\"{Html.A(path)}\">{Html.E(name)}</span>",
                $"<span class=\"num\">{Html.Number(Convert.ToInt64(row[before] ?? 0L, CultureInfo.InvariantCulture))} → " +
                $"{Html.Number(Convert.ToInt64(row[after] ?? 0L, CultureInfo.InvariantCulture))} " +
                $"<span class=\"dim\">({sign}{Html.Number(delta)})</span></span>",
            ];
        }));
    }

    private string QuickLinks()
    {
        var areas = new (string Title, string[] Tables)[]
        {
            ("Activity", ["command_log", "applied_edit", "context"]),
            ("Board", ["board_item", "board_comment", "board_history"]),
            ("Indexes", ["si_symbol", "si_usage", "si_document", "ti_document", "ti_line"]),
            ("Packages & artifacts", ["package_fact", "package_dependency", "decompiled_artifact"]),
            ("Native", ["native_artifact", "native_function", "native_call"]),
        };

        var builder = new StringBuilder("<div class=\"cards\" style=\"margin:0\">");
        foreach (var (title, tables) in areas)
        {
            builder.Append("<div class=\"kpi\"><span style=\"color:var(--text);font-weight:600\">").Append(Html.E(title)).Append("</span><span>");
            builder.Append(string.Join("<br>", tables.Select(table =>
                $"<a href=\"/t/{Uri.EscapeDataString(table)}\">{Html.E(DashboardCatalogue.Title(table))}</a>")));
            builder.Append("</span></div>");
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    // ── tables ───────────────────────────────────────────────────────────────

    public string TablesPage(DashboardInfo info)
    {
        var catalogue = _database.Catalogue();
        var body = new StringBuilder();

        body.Append("<h1>Tables</h1>");
        body.Append("<p class=\"sub\">Every table in the solution store, with its row count. ");
        body.Append("Tables are discovered from the database itself, so one added by a future feature gets a page automatically.</p>");

        if (catalogue.Count == 0)
        {
            body.Append(Html.Empty(_database.Exists
                ? "The store exists but has no tables yet."
                : "No solution store yet: no solution has been discovered under the working directory."));
            return Page("Tables", "tables", info, body.ToString());
        }

        foreach (var area in catalogue.GroupBy(t => DashboardCatalogue.Area(t.Table.Name)).OrderBy(g => g.Key))
        {
            var rows = area
                .OrderBy(t => DashboardCatalogue.Title(t.Table.Name), StringComparer.OrdinalIgnoreCase)
                .Select(t => (IReadOnlyList<string>)
                [
                    Html.Link("/t/" + Uri.EscapeDataString(t.Table.Name), t.Table.Name, "mono"),
                    Html.E(DashboardCatalogue.Title(t.Table.Name)),
                    $"<span class=\"num\">{Html.Number(t.Rows)}</span>",
                    $"<span class=\"num\">{t.Columns.Count}</span>",
                    Html.E(DashboardCatalogue.Description(t.Table.Name)),
                ]);

            body.Append(Html.Card($"{area.Key} · {Html.Count(area.Count(), "table")}",
                Html.Grid(["Table", "Title", "Rows", "Cols", "What it holds"], rows), flush: true));
        }

        return Page("Tables", "tables", info, body.ToString());
    }

    public bool TryTable(DashboardInfo info, string tableName, DashboardTableQuery query, out string html, out int status)
    {
        var rows = _database.Read(tableName, query.Page, query.Size, query.Sort, query.Descending, query.Search);
        if (rows is null)
        {
            status = 404;
            html = Page("Unknown table", "tables", info,
                $"<h1>Unknown table</h1><p class=\"sub\">The store has no table called <span class=\"mono\">{Html.E(tableName)}</span>.</p>" +
                Html.Card("Tables", Html.Link("/tables", "See every table →", "btn")));
            return false;
        }

        status = 200;
        var table = rows.Table;
        var body = new StringBuilder();

        body.Append($"<h1>{Html.E(DashboardCatalogue.Title(table.Name))}</h1>");
        body.Append($"<p class=\"sub\">{Html.E(DashboardCatalogue.Description(table.Name))} ");
        body.Append($"<span class=\"mono\">{Html.E(table.Name)}</span> · {(table.IsVirtual ? "virtual/FTS5 table" : table.Kind)} · ");
        body.Append($"<span class=\"num\">{Html.Number(rows.Total)}</span> row(s){(rows.Search is null ? "" : " matching the filter")}.</p>");

        body.Append("<section class=\"section\">");
        body.Append("<div class=\"toolbar\">");
        body.Append($"<form method=\"get\" action=\"/t/{Uri.EscapeDataString(table.Name)}\">");
        body.Append($"<input type=\"search\" name=\"q\" value=\"{Html.A(query.Search)}\" placeholder=\"filter every column…\" aria-label=\"Filter rows\">");
        body.Append("<button class=\"btn\" type=\"submit\">Filter</button>");
        body.Append($"<select name=\"size\" aria-label=\"Page size\" onchange=\"this.form.submit()\">");
        foreach (var size in new[] { 25, 50, 100, 250, 500, 1000 })
            body.Append($"<option value=\"{size}\"{(size == rows.PageSize ? " selected" : "")}>{size} / page</option>");
        body.Append("</select>");
        if (rows.Sort is not null)
        {
            body.Append($"<input type=\"hidden\" name=\"sort\" value=\"{Html.A(rows.Sort)}\">");
            body.Append($"<input type=\"hidden\" name=\"dir\" value=\"{(rows.Descending ? "desc" : "asc")}\">");
        }

        body.Append("</form>");
        if (query.Search is not null)
            body.Append(Html.Link($"/t/{Uri.EscapeDataString(table.Name)}", "clear filter", "btn"));

        var export = Html.Url($"/t/{Uri.EscapeDataString(table.Name)}",
            ("q", query.Search), ("size", rows.PageSize), ("sort", rows.Sort),
            ("dir", rows.Descending ? "desc" : "asc"));
        body.Append("<span class=\"push\"></span>");
        body.Append(Html.Link(export + (export.Contains('?') ? "&" : "?") + "format=csv", "CSV", "btn"));
        body.Append(Html.Link(export + (export.Contains('?') ? "&" : "?") + "format=json", "JSON", "btn"));
        body.Append("</div>");

        body.Append(RowsGrid(rows, query));
        body.Append(Pager(rows, query));
        body.Append("</section>");

        html = Page(DashboardCatalogue.Title(table.Name), "tables", info, body.ToString());
        return true;
    }

    private string RowsGrid(DashboardRows rows, DashboardTableQuery query)
    {
        if (rows.Rows.Count == 0)
            return Html.Empty(query.Search is null ? "This table is empty." : $"No row matches \"{query.Search}\".");

        var builder = new StringBuilder("<div class=\"scroller\"><table class=\"grid\"><thead><tr>");
        foreach (var column in rows.Columns)
        {
            var sorted = string.Equals(column.Name, rows.Sort, StringComparison.OrdinalIgnoreCase);
            var descending = sorted && rows.Descending;
            var href = Html.Url($"/t/{Uri.EscapeDataString(rows.Table.Name)}",
                ("q", query.Search),
                ("size", rows.PageSize),
                ("sort", column.Name),
                ("dir", sorted && rows.Descending ? "asc" : sorted ? "desc" : "asc"));

            builder.Append("<th>")
                .Append($"<a class=\"{(sorted ? "on" : "")}\" href=\"{Html.A(href)}\" title=\"Sort by {Html.A(column.Name)}\">")
                .Append(Html.E(column.Name))
                .Append(sorted ? (descending ? " ▼" : " ▲") : "")
                .Append("</a>");
            if (!string.IsNullOrEmpty(column.Type))
                builder.Append($" <span class=\"chip\">{Html.E(Shorten(column.Type, 18))}</span>");
            builder.Append("</th>");
        }

        builder.Append("</tr></thead><tbody>");
        foreach (var row in rows.Rows)
        {
            builder.Append("<tr>");
            for (var i = 0; i < row.Length && i < rows.Columns.Count; i++)
                builder.Append("<td>").Append(Cell(row[i], rows.Columns[i])).Append("</td>");
            builder.Append("</tr>");
        }

        builder.Append("</tbody></table></div>");
        return builder.ToString();
    }

    private string Pager(DashboardRows rows, DashboardTableQuery query)
    {
        string Href(int page) => Html.Url($"/t/{Uri.EscapeDataString(rows.Table.Name)}",
            ("q", query.Search), ("size", rows.PageSize), ("sort", rows.Sort),
            ("dir", rows.Descending ? "desc" : "asc"), ("page", page > 1 ? page : null));

        var first = (rows.Page - 1) * rows.PageSize + 1;
        var last = Math.Min(rows.Total, (long)rows.Page * rows.PageSize);
        var shown = rows.Total == 0 ? "0" : $"{Html.Number(first)}–{Html.Number(last)}";

        var builder = new StringBuilder("<div class=\"pager\">");
        builder.Append($"<span>{shown} of {Html.Number(rows.Total)}</span>");
        builder.Append("<span class=\"spacer\"></span>");
        if (rows.Page > 1)
        {
            builder.Append(Html.Link(Href(1), "« first", "btn"));
            builder.Append(Html.Link(Href(rows.Page - 1), "‹ prev", "btn"));
        }

        builder.Append($"<span class=\"mono\">page {rows.Page} / {rows.PageCount}</span>");
        if (rows.Page < rows.PageCount)
        {
            builder.Append(Html.Link(Href(rows.Page + 1), "next ›", "btn"));
            builder.Append(Html.Link(Href(rows.PageCount), "last »", "btn"));
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    // ── schema ───────────────────────────────────────────────────────────────

    public string Schema(DashboardInfo info)
    {
        var catalogue = _database.Catalogue();
        var body = new StringBuilder();

        body.Append("<h1>Schema</h1>");
        body.Append("<p class=\"sub\">The columns of every table, exactly as SQLite reports them. ");
        body.Append("Useful when a page shows a value you want to understand.</p>");

        if (catalogue.Count == 0)
        {
            body.Append(Html.Empty("No solution store yet."));
            return Page("Schema", "schema", info, body.ToString());
        }

        foreach (var area in catalogue.GroupBy(t => DashboardCatalogue.Area(t.Table.Name)).OrderBy(g => g.Key))
        {
            var inner = new StringBuilder();
            foreach (var table in area.OrderBy(t => t.Table.Name, StringComparer.OrdinalIgnoreCase))
            {
                var rows = table.Columns.Select(column => (IReadOnlyList<string>)
                [
                    Html.E(column.Name),
                    $"<span class=\"dim\">{Html.E(string.IsNullOrEmpty(column.Type) ? "—" : column.Type)}</span>",
                    column.NotNull ? Html.Chip("not null", "warn") : "<span class=\"dim\">null</span>",
                    column.PrimaryKey ? Html.Chip("pk", "acc") : "",
                ]);

                inner.Append($"<details{(table.Columns.Count <= 12 ? " open" : "")}>");
                inner.Append($"<summary><span class=\"mono\">{Html.E(table.Table.Name)}</span> ");
                inner.Append($"<span class=\"dim\">· {table.Columns.Count} column(s) · {Html.Number(table.Rows)} row(s)</span></summary>");
                inner.Append(Html.Grid(["Column", "Type", "Null", "Key"], rows));
                inner.Append("</details>");
            }

            body.Append(Html.Card(area.Key, "<div class=\"schema\">" + inner + "</div>"));
        }

        return Page("Schema", "schema", info, body.ToString());
    }

    // ── MCP tools ────────────────────────────────────────────────────────────

    public string ToolsPage(DashboardInfo info)
    {
        var tools = ToolSummaries();
        var body = new StringBuilder();

        body.Append("<h1>Tools</h1>");
        body.Append($"<p class=\"sub\">The {Html.Count(tools.Count, "tool")} this process advertises to its MCP client, ");
        body.Append("with the arguments each one accepts. An argument marked <b>*</b> is required.</p>");

        if (tools.Count == 0)
        {
            body.Append(Html.Empty("No tool is registered yet (the MCP server has not been built)."));
            return Page("Tools", "tools", info, body.ToString());
        }

        body.Append("<section class=\"section\">");
        body.Append("<div class=\"toolbar\">");
        body.Append("<input type=\"search\" id=\"toolq\" placeholder=\"filter tools…\" aria-label=\"Filter tools\">");
        body.Append($"<span class=\"dim\">{Html.Count(tools.Count, "tool")}</span>");
        body.Append("</div>");

        foreach (var tool in tools)
        {
            // The filter matches the name and the start of the description; keeping the whole
            // description in the attribute would double the page for no gain.
            var searchText = (tool.Name + " " + Shorten(tool.Description, 200)).ToLowerInvariant();
            body.Append($"<div class=\"tool\" data-tool=\"{Html.A(searchText)}\">");
            body.Append("<h3>").Append(Html.E(tool.Name));
            if (!string.IsNullOrWhiteSpace(tool.Title)) body.Append($" <span class=\"dim\">{Html.E(tool.Title)}</span>");
            if (tool.ReadOnly is true) body.Append(" ").Append(Html.Chip("read-only", "ok"));
            if (tool.Destructive is true) body.Append(" ").Append(Html.Chip("destructive", "bad"));
            body.Append("</h3>");

            if (!string.IsNullOrWhiteSpace(tool.Description))
                body.Append("<p>").Append(Html.E(tool.Description)).Append("</p>");

            if (tool.Parameters.Count > 0)
            {
                body.Append("<ul>");
                foreach (var parameter in tool.Parameters)
                {
                    var required = parameter.Required ? "*" : "";
                    var description = string.IsNullOrWhiteSpace(parameter.Description)
                        ? ""
                        : $" <span class=\"dim\">— {Html.E(parameter.Description)}</span>";
                    body.Append($"<li><b>{Html.E(parameter.Name)}{required}</b> <span class=\"dim\">{Html.E(parameter.Type)}</span>{description}</li>");
                }

                body.Append("</ul>");
            }

            body.Append("</div>");
        }

        body.Append("</section>");
        body.Append("""
            <script>
            (() => {
              const input = document.getElementById('toolq');
              const items = [...document.querySelectorAll('[data-tool]')];
              if (!input) return;
              input.addEventListener('input', () => {
                const value = input.value.trim().toLowerCase();
                for (const item of items) item.hidden = value !== '' && !item.dataset.tool.includes(value);
              });
            })();
            </script>
            """);

        return Page("Tools", "tools", info, body.ToString());
    }

    private sealed record ToolParameter(string Name, string Type, bool Required, string? Description);

    private sealed record ToolSummary(
        string Name, string? Title, string? Description, bool? ReadOnly, bool? Destructive,
        IReadOnlyList<ToolParameter> Parameters);

    private IReadOnlyList<ToolSummary> ToolSummaries()
    {
        var registered = RegisteredTools();
        if (registered.Count > 0)
            return registered
                .Select(Summarize)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Before the MCP server has been constructed there may be no live tool instance to read;
        // the attributes on the tool classes describe the same catalogue.
        return ReflectedTools().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<McpServerTool> RegisteredTools()
    {
        try
        {
            if (_services.GetService<McpServerOptions>()?.ToolCollection is { Count: > 0 } direct)
                return direct.ToArray();
        }
        catch
        {
            // Fall through to the next source.
        }

        try
        {
            if (_services.GetService<IOptions<McpServerOptions>>()?.Value?.ToolCollection is { Count: > 0 } options)
                return options.ToArray();
        }
        catch
        {
            // Fall through.
        }

        try
        {
            return _services.GetServices<McpServerTool>().ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static ToolSummary Summarize(McpServerTool tool)
    {
        try
        {
            return Describe(tool);
        }
        catch
        {
            // A tool whose protocol representation cannot be read still lists by name.
            return new ToolSummary(SafeName(tool), null, null, null, null, []);
        }
    }

    private static string SafeName(McpServerTool tool)
    {
        try { return tool.ProtocolTool.Name; }
        catch { return "(unnamed)"; }
    }

    private static ToolSummary Describe(McpServerTool tool)
    {
        var protocol = tool.ProtocolTool;
        var parameters = new List<ToolParameter>();

        try
        {
            var schema = protocol.InputSchema;
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                var required = new HashSet<string>(StringComparer.Ordinal);
                if (schema.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array)
                    foreach (var item in requiredNode.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                            required.Add(name);

                foreach (var property in properties.EnumerateObject())
                {
                    var type = "any";
                    string? description = null;

                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (property.Value.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String)
                            type = typeNode.GetString() ?? "any";
                        else if (property.Value.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
                            type = string.Join(" | ", anyOf.EnumerateArray()
                                .Select(node => node.ValueKind == JsonValueKind.Object
                                        && node.TryGetProperty("type", out var alternative)
                                        && alternative.ValueKind == JsonValueKind.String
                                    ? alternative.GetString()
                                    : null)
                                .Where(text => text is not null));

                        if (property.Value.TryGetProperty("description", out var descriptionNode)
                            && descriptionNode.ValueKind == JsonValueKind.String)
                            description = descriptionNode.GetString();
                    }

                    parameters.Add(new ToolParameter(
                        property.Name, Shorten(type, 40), required.Contains(property.Name), Shorten(description, 160)));
                }
            }
        }
        catch
        {
            // A tool with an unreadable schema still lists by name and description.
        }

        return new ToolSummary(
            protocol.Name,
            protocol.Title,
            Shorten(protocol.Description, 900),
            protocol.Annotations?.ReadOnlyHint,
            protocol.Annotations?.DestructiveHint,
            parameters);
    }

    private static IReadOnlyList<ToolSummary> ReflectedTools()
    {
        var summaries = new List<ToolSummary>();
        foreach (var type in typeof(DashboardPages).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null) continue;

                var parameters = method.GetParameters()
                    .Where(parameter => parameter.ParameterType != typeof(CancellationToken)
                                     && parameter.ParameterType != typeof(IServiceProvider)
                                     && !typeof(McpServer).IsAssignableFrom(parameter.ParameterType)
                                     && !(parameter.ParameterType.IsGenericType
                                          && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>)))
                    .Select(parameter => new ToolParameter(
                        parameter.Name ?? "argument",
                        Shorten(FriendlyType(parameter.ParameterType), 40),
                        !parameter.HasDefaultValue,
                        Shorten(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description, 160)))
                    .ToList();

                summaries.Add(new ToolSummary(
                    attribute.Name ?? SnakeCase(method.Name),
                    attribute.Title,
                    Shorten(method.GetCustomAttribute<DescriptionAttribute>()?.Description, 900),
                    attribute.ReadOnly ? true : null,
                    attribute.Destructive ? true : null,
                    parameters));
            }
        }

        return summaries;
    }

    private static string FriendlyType(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        if (type == typeof(double) || type == typeof(decimal) || type == typeof(float)) return "number";
        if (type.IsArray) return FriendlyType(type.GetElementType()!) + "[]";
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return FriendlyType(underlying);
        return type.Name;
    }

    private static string SnakeCase(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    // ── live log ─────────────────────────────────────────────────────────────

    public string LogPage(DashboardInfo info, DashboardLogQuery query)
    {
        var body = new StringBuilder();

        body.Append("<h1>Live log</h1>");
        body.Append("<p class=\"sub\">Every request and tool call this process logged, newest first. ");
        body.Append("The feed refreshes every couple of seconds without reloading the page.</p>");

        var path = _options.LogPath;
        if (string.IsNullOrWhiteSpace(path))
            return Page("Live log", "log", info, body.Append(Html.Empty("No log file is configured.")).ToString());

        var exists = File.Exists(path);
        var size = "missing";
        try
        {
            if (exists) size = Html.Bytes(new FileInfo(path).Length);
        }
        catch (IOException)
        {
            // The file was replaced between the two calls: the page still renders.
        }

        body.Append("<section class=\"section\">");
        body.Append("<div class=\"toolbar\">");
        body.Append($"<form method=\"get\" action=\"/log\">");
        body.Append($"<input type=\"search\" name=\"q\" value=\"{Html.A(query.Search)}\" placeholder=\"filter lines…\" aria-label=\"Filter log lines\">");
        body.Append("<select name=\"lines\" aria-label=\"Lines\">");
        foreach (var lines in new[] { 100, 300, 1000, 3000 })
            body.Append($"<option value=\"{lines}\"{(lines == query.Lines ? " selected" : "")}>{lines} lines</option>");
        body.Append("</select>");
        body.Append("<select name=\"only\" aria-label=\"Kind of line\">");
        foreach (var (value, label) in LogKinds)
            body.Append($"<option value=\"{value}\"{(string.Equals(value, query.Only, StringComparison.Ordinal) ? " selected" : "")}>{label}</option>");
        body.Append("</select>");
        body.Append("<button class=\"btn\" type=\"submit\">Apply</button>");
        body.Append("</form>");
        if (query.Search is not null || query.Only is not null)
            body.Append(Html.Link(Html.Url("/log", ("lines", query.Lines)), "clear", "btn"));
        body.Append("<span class=\"push\"></span>");
        body.Append("<span class=\"dim mono\">").Append(Html.E(path)).Append(" · ").Append(Html.E(size.ToString())).Append("</span>");
        body.Append("<button class=\"btn\" id=\"pause\" type=\"button\">Pause</button>");
        body.Append("</div>");
        body.Append($"<div id=\"feed\">{LogFeed(query)}</div>");
        body.Append("</section>");

        if (query.Live)
        {
            body.Append("""
                <script>
                (() => {
                  const feed = document.getElementById('feed');
                  const pause = document.getElementById('pause');
                  const url = new URL(location.href);
                  url.searchParams.set('fragment', '1');
                  let paused = false;
                  pause?.addEventListener('click', () => {
                    paused = !paused;
                    pause.textContent = paused ? 'Resume' : 'Pause';
                  });
                  setInterval(async () => {
                    if (paused) return;
                    try {
                      const response = await fetch(url, { cache: 'no-store' });
                      if (response.ok) feed.innerHTML = await response.text();
                    } catch { /* the server may be restarting */ }
                  }, 2500);
                })();
                </script>
                """);
        }

        return Page("Live log", "log", info, body.ToString());
    }

    public string LogFragment(DashboardLogQuery query) => LogFeed(query);

    /// <summary>The kinds of log line the filter offers; the key is the <c>only</c> query value.</summary>
    private static readonly (string Value, string Label)[] LogKinds =
    [
        ("", "all lines"),
        ("tool", "tool calls"),
        ("req", "requests"),
        ("err", "errors"),
        ("warn", "warnings"),
    ];

    /// <summary>The short label, the CSS class and the filter kind of a log tag.</summary>
    private static (string Label, string Css, string Kind) Classify(string tag) => tag.ToUpperInvariant() switch
    {
        "TOOL" => ("TOOL", "tool", "tool"),
        "REQ" => ("REQ", "req", "req"),
        "ERROR" or "FAIL" or "CRITICAL" or "FATAL" => ("ERR", "err", "err"),
        "WARNING" or "WARN" => ("WARN", "warn", "warn"),
        "DEBUG" => ("DBG", "lvl", "other"),
        "TRACE" => ("TRC", "lvl", "other"),
        "INFORMATION" or "INFO" => ("INFO", "lvl", "other"),
        var other => (other.Length > 6 ? other[..6] : other, "lvl", "other"),
    };

    private string LogFeed(DashboardLogQuery query)
    {
        var path = _options.LogPath;
        if (string.IsNullOrWhiteSpace(path)) return Html.Empty("No log file is configured.");
        if (!File.Exists(path)) return Html.Empty($"No log file at {path} yet.");

        var lines = ReadTail(path, Math.Clamp(query.Lines, 20, 3000));
        var filter = query.Search?.Trim();
        var only = string.IsNullOrWhiteSpace(query.Only) ? null : query.Only;

        var filtered = lines.Where(line =>
        {
            if (!string.IsNullOrEmpty(filter) && !line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (only is null) return true;

            var match = LogLine.Match(line);
            return match.Success && string.Equals(Classify(match.Groups["tag"].Value).Kind, only, StringComparison.Ordinal);
        }).ToArray();

        if (filtered.Length == 0)
            return Html.Empty(filter is null && only is null ? "The log file is empty." : "No line matches the filter.");

        var builder = new StringBuilder("<div class=\"log\">");
        for (var i = filtered.Length - 1; i >= 0; i--)
        {
            var line = filtered[i];
            if (line.Length == 0) continue;

            var match = LogLine.Match(line);
            if (!match.Success)
            {
                var css = line.StartsWith("--- VisualMCP started", StringComparison.Ordinal) ? "start" : "lvl";
                builder.Append("<div class=\"line\"><span class=\"t\"></span><span class=\"g ").Append(css).Append("\">")
                    .Append(css == "start" ? "BOOT" : "")
                    .Append("</span><span class=\"m\">").Append(Html.E(line)).Append("</span></div>");
                continue;
            }

            var tag = match.Groups["tag"].Value.Trim();
            var (label, cssClass, _) = Classify(tag);

            builder.Append("<div class=\"line\">")
                .Append("<span class=\"t\">").Append(Html.E(match.Groups["time"].Value)).Append("</span>")
                .Append("<span class=\"g ").Append(cssClass).Append("\">").Append(Html.E(label)).Append("</span>")
                .Append("<span class=\"m\">").Append(Html.E(Preview(StripCategory(match.Groups["rest"].Value)))).Append("</span>")
                .Append("</div>");
        }

        builder.Append("</div>");
        return builder.ToString();
    }

    /// <summary>
    /// One log line, shortened. A tool call's argument JSON can be tens of kilobytes, and the
    /// feed refreshes every couple of seconds — the full text goes in the tooltip.
    /// </summary>
    private static string Preview(string text)
    {
        const int limit = 600;
        if (text.Length <= limit) return text;
        return text[..limit] + "… (" + Html.Number(text.Length - limit) + " more chars)";
    }

    /// <summary>Drops the <c>VisualMCP.ToolCalls: </c> prefix: the tag column already says it.</summary>
    private static string StripCategory(string message)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon < 60 && message.StartsWith("VisualMCP.", StringComparison.Ordinal))
            return message[(colon + 2)..];
        return message;
    }

    /// <summary>The last <paramref name="maxLines"/> lines, reading only the tail of the file.</summary>
    private static string[] ReadTail(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            const long window = 512 * 1024;
            var start = Math.Max(0, stream.Length - window);
            var partial = start > 0;
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();

            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            if (partial && lines.Count > 0) lines.RemoveAt(0);   // started mid-line
            return lines.TakeLast(maxLines).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ── cells ────────────────────────────────────────────────────────────────

    private static string Cell(object?[] row, int index, IReadOnlyList<DashboardColumn> columns) =>
        index < 0 || index >= row.Length || index >= columns.Count
            ? "<span class=\"dim\">—</span>"
            : Cell(row[index], columns[index]);

    private static string AsText(object?[] row, int index) =>
        index < 0 || index >= row.Length ? "" : Convert.ToString(row[index], CultureInfo.InvariantCulture) ?? "";

    private static string Cell(object? value, DashboardColumn column) => value switch
    {
        null => "<span class=\"dim\">—</span>",
        byte[] blob => $"<span class=\"dim\">&lt;{Html.Number(blob.Length)} B blob&gt;</span>",
        long number => $"<span class=\"num\">{Html.Number(number)}</span>",
        int number => $"<span class=\"num\">{Html.Number(number)}</span>",
        double number => $"<span class=\"num\">{number.ToString("G", CultureInfo.InvariantCulture)}</span>",
        decimal number => $"<span class=\"num\">{number.ToString("G", CultureInfo.InvariantCulture)}</span>",
        string text when IsTimestamp(column.Name) && text.Length is >= 19 and <= 40 => Html.Timestamp(text),
        string text => Text(text),
        _ => Html.E(value),
    };

    private static string Text(object? value) => value switch
    {
        null => "<span class=\"dim\">—</span>",
        string text => Text(text),
        _ => Html.E(value),
    };

    private static string Text(string text)
    {
        if (text.Length == 0) return "<span class=\"dim\">(empty)</span>";

        var multiline = text.Contains('\n');
        var limit = multiline ? CellMultilinePreview : CellPreview;
        var css = multiline ? "cell pre" : "cell";

        if (text.Length <= limit && !multiline)
            return $"<span class=\"{css}\">{Html.E(text)}</span>";

        var preview = text.Length > limit ? text[..limit] + "…" : text;
        var full = text.Length > CellFull ? text[..CellFull] + "\n… (truncated)" : text;

        return $"<details><summary><span class=\"{css}\">{Html.E(preview)}</span></summary>" +
               $"<pre class=\"full\">{Html.E(full)}</pre></details>";
    }

    private static bool IsTimestamp(string column) =>
        column.EndsWith("_at", StringComparison.OrdinalIgnoreCase)
        || string.Equals(column, "at", StringComparison.OrdinalIgnoreCase)
        || string.Equals(column, "date", StringComparison.OrdinalIgnoreCase);

    private static int Column(DashboardRows rows, string name)
    {
        for (var i = 0; i < rows.Columns.Count; i++)
            if (string.Equals(rows.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Shorten(string? text, int length) =>
        text is null ? "" : text.Length <= length ? text : text[..length] + "…";

    // ── the fixed left column ────────────────────────────────────────────────

    /// <summary>
    /// The left column's list of invoked commands: the newest tool calls first, each one a link
    /// to the command log filtered by that tool, under the total that opens the whole log.
    /// </summary>
    private string Sidebar()
    {
        var exists = _database.Exists;
        var trail = exists ? _database.RecentCommands() : new DashboardCommandTrail(0, []);
        var builder = new StringBuilder();

        builder.Append("<div class=\"sidesec\"><span>Commands invoked</span>");
        if (exists)
            builder.Append(Html.Link("/t/command_log",
                trail.Total > 0 ? Html.Number(trail.Total) + " total →" : "open log →"));
        builder.Append("</div>");

        if (!exists)
            return builder.Append("<div class=\"sidenote\">No solution store yet.</div>").ToString();

        if (trail.Items.Count == 0)
            return builder.Append("<div class=\"sidenote\">No tool call has been invoked yet.</div>").ToString();

        builder.Append("<div class=\"sidelist\">");
        foreach (var command in trail.Items)
        {
            var (clock, full) = Clock(command.OccurredAt);
            var href = Html.Url("/t/command_log", ("q", command.Tool));
            builder
                .Append($"<a class=\"cmd\" href=\"{Html.A(href)}\" title=\"{Html.A(command.Tool + " · " + full)}\">")
                .Append($"<b>{Html.E(command.Tool)}</b><span>{Html.E(clock)}</span></a>");
        }

        return builder.Append("</div>").ToString();
    }

    /// <summary>A stored timestamp as the sidebar's short local time, plus the full text for the tooltip.</summary>
    private static (string Clock, string Full) Clock(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? (at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
               at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            : (iso, iso);

    // ── chrome ───────────────────────────────────────────────────────────────

    private string Page(string title, string active, DashboardInfo info, string body)
    {
        var uptime = Html.Duration(DateTimeOffset.UtcNow - info.StartedAt);
        var meta = $"pid {Environment.ProcessId} · up {uptime} · :{info.Port}";
        return Html.Document(title, active, Html.E(meta), body, Sidebar());
    }
}
