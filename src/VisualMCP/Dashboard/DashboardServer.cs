using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace VisualMCP.Dashboard;

/// <summary>
/// The dashboard's HTTP host: a small ASP.NET Core application started next to the MCP stdio
/// transport, bound to loopback. It is an observer — every route is a GET that reads the SQLite
/// store and the process — so it can run at any time without touching the solution, and a
/// failure to bind a port leaves the MCP server completely unaffected.
/// </summary>
/// <remarks>
/// Port choice, in order: the preferred port (4677 by default), the port the dashboard last
/// bound (kept in the store's <c>context</c> table as <c>dashboard.port</c>), then an
/// OS-assigned port. The remembered port is what makes a second instance — two MCP clients on
/// the same solution — land on a stable port of its own instead of hopping on every start.
/// </remarks>
public sealed class DashboardServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DashboardOptions _options;
    private readonly DashboardDatabase _database;
    private readonly DashboardPages _pages;
    private readonly ILogger<DashboardServer> _logger;

    private WebApplication? _application;
    private volatile DashboardInfo _info;
    private string? _lastError;
    private int _started;

    public DashboardServer(
        IServiceProvider services,
        DashboardOptions options,
        DashboardDatabase database,
        ILogger<DashboardServer> logger)
    {
        _options = options;
        _database = database;
        _logger = logger;
        _pages = new DashboardPages(database, options, services);
        _info = new DashboardInfo(0, null, "starting", null, ProcessStart());
    }

    /// <summary>The live state of the web host, shown on every page's header and on the overview.</summary>
    public DashboardInfo Info => _info;

    /// <summary>True once a listener is answering.</summary>
    public bool IsRunning => _application is not null;

    /// <summary>
    /// Starts the listener. Never throws: a dashboard that cannot bind is a warning in the log,
    /// not a reason for the MCP server to fail.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null) return true;
        if (Interlocked.Exchange(ref _started, 1) == 1) return false;   // a start is in flight, or already failed

        if (!_options.Enabled)
        {
            _info = _info with { Status = "disabled" };
            _logger.LogInformation("Web dashboard disabled (VISUALMCP_DASHBOARD).");
            return false;
        }

        foreach (var port in DashboardOptions.Candidates(_options.PreferredPort, _database.StoredPort()))
        {
            if (await TryStartAsync(port, cancellationToken)) return true;
        }

        // Nothing preferred is free: let the OS pick, and remember it for the next start.
        if (await TryStartAsync(0, cancellationToken)) return true;

        _info = _info with { Status = "failed", LastError = _lastError };
        _logger.LogWarning(
            "Web dashboard could not bind any port. The MCP server is unaffected. Last error: {Error}", _lastError);
        return false;
    }

    private async Task<bool> TryStartAsync(int port, CancellationToken cancellationToken)
    {
        WebApplication? application = null;
        try
        {
            application = Build(port);
            await application.StartAsync(cancellationToken);

            var url = application.Urls.FirstOrDefault() ?? $"http://{_options.Host}:{port}";
            var bound = Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Port > 0 ? parsed.Port : port;

            _application = application;
            _info = new DashboardInfo(bound, url, "running", null, _info.StartedAt);
            Remember(bound);

            _logger.LogInformation(
                "Web dashboard on {Url} — {Database}",
                url, _database.DatabasePath ?? "(no solution store yet)");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogDebug(ex, "Web dashboard could not bind {Url}.", $"http://{_options.Host}:{port}");

            if (application is not null)
            {
                try { await application.DisposeAsync(); }
                catch { /* the failed host is already gone */ }
            }

            return false;
        }
    }

    /// <summary>
    /// Remembers the bound port in the store so the next start reuses it. Best-effort by
    /// design: no store (no solution) simply means the port is chosen afresh next time.
    /// </summary>
    private void Remember(int port)
    {
        if (_database.DatabasePath is null) return;
        try { _database.RememberPort(port); }
        catch (Exception ex) { _logger.LogDebug(ex, "Web dashboard could not remember its port."); }
    }

    /// <summary>Stops the listener. Safe to call when it never started.</summary>
    public async Task StopAsync()
    {
        var application = _application;
        _application = null;
        if (application is null) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await application.StopAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Web dashboard did not stop cleanly.");
        }

        try { await application.DisposeAsync(); }
        catch { /* process is going away */ }

        _info = _info with { Status = "stopped", Url = null };
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── the HTTP application ─────────────────────────────────────────────────

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // stdout belongs to the MCP transport: no console or debug provider may ever write
        // a startup banner there. The dashboard logs through the same file logger as the rest.
        builder.Logging.ClearProviders();

        builder.WebHost.UseUrls($"http://{_options.Host}:{port}");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cache-Control"] = "no-store";
            headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
                "connect-src 'self'; img-src data:; base-uri 'none'; form-action 'self'";
            await next(context);
        });

        Map(app);
        return app;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/", (HttpContext http) => Render(http, info => _pages.Home(info)));
        app.MapGet("/tables", (HttpContext http) => Render(http, info => _pages.TablesPage(info)));
        app.MapGet("/schema", (HttpContext http) => Render(http, info => _pages.Schema(info)));
        app.MapGet("/tools", (HttpContext http) => Render(http, info => _pages.ToolsPage(info)));

        app.MapGet("/t/{table}", (string table, HttpContext http) => Table(http, table));

        app.MapGet("/log", (HttpContext http) =>
        {
            var query = LogQuery(http);
            return Render(http, info => query.Fragment
                ? _pages.LogFragment(query)
                : _pages.LogPage(info, query));
        });

        app.MapGet("/api/health", () => Results.Json(new
        {
            ok = IsRunning,
            pid = Environment.ProcessId,
            port = _info.Port,
            url = _info.Url,
            status = _info.Status,
            startedAt = _info.StartedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _info.StartedAt).TotalSeconds,
            database = _database.DatabasePath,
        }, JsonOptions));

        app.MapGet("/api/tables", () => Results.Json(
            _database.Catalogue().Select(t => new
            {
                table = t.Table.Name,
                kind = t.Table.Kind,
                rows = t.Rows,
                columns = t.Columns.Select(c => new { name = c.Name, type = c.Type, primaryKey = c.PrimaryKey }),
                title = DashboardCatalogue.Title(t.Table.Name),
                area = DashboardCatalogue.Area(t.Table.Name),
                description = DashboardCatalogue.Description(t.Table.Name),
            }), JsonOptions));

        app.MapGet("/api/tables/{table}", (string table, HttpContext http) =>
        {
            var query = TableQuery(http);
            var rows = _database.Read(table, query.Page, query.Size, query.Sort, query.Descending, query.Search);
            if (rows is null)
                return Results.Json(new { error = $"No table called '{table}'." }, JsonOptions, statusCode: 404);

            return Results.Json(new
            {
                table = rows.Table.Name,
                kind = rows.Table.Kind,
                total = rows.Total,
                page = rows.Page,
                pageSize = rows.PageSize,
                pageCount = rows.PageCount,
                sort = rows.Sort,
                descending = rows.Descending,
                search = rows.Search,
                columns = rows.Columns.Select(c => new { name = c.Name, type = c.Type, primaryKey = c.PrimaryKey }),
                rows = rows.Rows.Select(row => row.Select(PlainValue).ToArray()),
            }, JsonOptions);
        });

        app.MapGet("/api/overview", () =>
        {
            var counts = _database.Catalogue()
                .ToDictionary(t => t.Table.Name, t => t.Rows, StringComparer.OrdinalIgnoreCase);

            return Results.Json(new
            {
                process = new
                {
                    pid = Environment.ProcessId,
                    runtime = Environment.Version.ToString(),
                    workingDirectory = Environment.CurrentDirectory,
                    uptimeSeconds = (long)(DateTimeOffset.UtcNow - _info.StartedAt).TotalSeconds,
                },
                dashboard = new { port = _info.Port, url = _info.Url, status = _info.Status },
                database = new { path = _database.DatabasePath, sizeBytes = _database.SizeBytes },
                tables = counts,
            }, JsonOptions);
        });

        app.MapGet("/favicon.ico", () => Results.NoContent());
        app.MapFallback((HttpContext http) => Results.Content(
            Shell("Not found", $"<h1>404 — no page here</h1>" +
                               $"<p class=\"sub\">Nothing is served at <span class=\"mono\">{Html.E(http.Request.Path)}</span>.</p>" +
                               "<p><a class=\"btn\" href=\"/\">Back to the overview</a></p>"),
            "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status404NotFound));
    }

    private IResult Table(HttpContext http, string table)
    {
        var query = TableQuery(http);

        if (string.Equals(query.Format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var info = _database.Info(table);
            if (info is null) return NotFound(http, table);

            var rows = _database.Export(info.Table.Name, query.Search);
            http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{SafeFileName(info.Table.Name)}.csv\"";

            return Results.Text(BuildCsv(info.Columns, rows), "text/csv; charset=utf-8", new UTF8Encoding(false));
        }

        if (string.Equals(query.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var rows = _database.Read(table, query.Page, query.Size, query.Sort, query.Descending, query.Search);
            if (rows is null)
                return Results.Json(new { error = $"No table called '{table}'." }, JsonOptions, statusCode: 404);

            return Results.Json(new
            {
                table = rows.Table.Name,
                total = rows.Total,
                page = rows.Page,
                pageSize = rows.PageSize,
                sort = rows.Sort,
                descending = rows.Descending,
                search = rows.Search,
                rows = rows.Rows.Select(row => row.Select(PlainValue).ToArray()),
            }, JsonOptions);
        }

        return Render(http, info =>
        {
            if (_pages.TryTable(info, table, query, out var html, out var status))
                return html;

            http.Response.StatusCode = status;
            return html;
        });
    }

    private IResult NotFound(HttpContext http, string table) => Results.Content(
        Shell("Unknown table", $"<h1>Unknown table</h1>" +
                               $"<p class=\"sub\">The store has no table called <span class=\"mono\">{Html.E(table)}</span>.</p>" +
                               "<p><a class=\"btn\" href=\"/tables\">See every table</a></p>"),
        "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status404NotFound);

    private IResult Render(HttpContext http, Func<DashboardInfo, string> page)
    {
        try
        {
            return Results.Content(page(_info), "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web dashboard page failed: {Path}", http.Request.Path);
            return Results.Content(
                Shell("Error", "<h1>500 — this page failed</h1>" +
                               "<p class=\"sub\">The dashboard caught the exception below; the MCP server keeps running.</p>" +
                               $"<pre class=\"full\">{Html.E(ex.ToString())}</pre>" +
                               "<p><a class=\"btn\" href=\"/\">Back to the overview</a></p>"),
                "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status500InternalServerError);
        }
    }

    // An error page is rendered without the store's command list: it may be the reason the page failed.
    private string Shell(string title, string body) =>
        Html.Document(title, "", Html.E($"pid {Environment.ProcessId} · :{_info.Port}"), body, "");

    // ── query strings ────────────────────────────────────────────────────────

    private static DashboardTableQuery TableQuery(HttpContext http)
    {
        var query = http.Request.Query;
        var size = Number(query["size"], DashboardDatabase.DefaultPageSize, 1, DashboardDatabase.MaxPageSize);

        return new DashboardTableQuery(
            Page: Number(query["page"], 1, 1, int.MaxValue),
            Size: size,
            Sort: Text(query["sort"]),
            Descending: !string.Equals(query["dir"].ToString(), "asc", StringComparison.OrdinalIgnoreCase),
            Search: Text(query["q"]),
            Format: (Text(query["format"]) ?? "html").ToLowerInvariant());
    }

    private static DashboardLogQuery LogQuery(HttpContext http)
    {
        var query = http.Request.Query;

        var only = Text(query["only"]);
        if (only is not (null or "tool" or "req" or "err" or "warn")) only = null;

        return new DashboardLogQuery(
            Search: Text(query["q"]),
            Lines: Number(query["lines"], 300, 20, 3000),
            Only: only,
            Live: !string.Equals(query["live"].ToString(), "0", StringComparison.Ordinal),
            Fragment: string.Equals(query["fragment"].ToString(), "1", StringComparison.Ordinal));
    }

    private static string? Text(StringValues value)
    {
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int Number(StringValues value, int fallback, int min, int max) =>
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number, min, max)
            : fallback;

    // ── payloads ─────────────────────────────────────────────────────────────

    /// <summary>A blob is summarized: a dashboard answer must never carry a file's bytes.</summary>
    private static object? PlainValue(object? value) => value switch
    {
        byte[] blob => $"<blob {blob.Length} bytes>",
        _ => value,
    };

    private static string BuildCsv(IReadOnlyList<DashboardColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(column => CsvField(column.Name))));

        foreach (var row in rows)
        {
            var cells = new string[columns.Count];
            for (var i = 0; i < columns.Count; i++)
                cells[i] = CsvField(i < row.Length ? PlainValue(row[i]) : null);
            builder.AppendLine(string.Join(",", cells));
        }

        return builder.ToString();
    }

    private static string CsvField(object? value)
    {
        var text = value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SafeFileName(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    private static DateTimeOffset ProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
