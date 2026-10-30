using System.Globalization;
using System.Net;
using System.Text;

namespace VisualMCP.Dashboard;

/// <summary>
/// The dashboard's presentation layer: one document template, one stylesheet and the handful
/// of components (card, KPI, grid, chip, pager) the pages are built from. Everything is
/// server-rendered and inline, so the dashboard works offline and needs no build step.
/// </summary>
internal static class Html
{
    /// <summary>Encodes text for HTML. Every value read out of the database goes through this.</summary>
    public static string E(object? value) => value switch
    {
        null => string.Empty,
        DateTimeOffset at => WebUtility.HtmlEncode(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        string text => WebUtility.HtmlEncode(text),
        _ => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>Encodes text for a quoted attribute.</summary>
    public static string A(object? value) => E(value).Replace("'", "&#39;", StringComparison.Ordinal);

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Counts read as words: "1 row", "2 rows", "231 tools".</summary>
    public static string Count(long count, string singular, string? plural = null) =>
        $"{Number(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0
            ? $"{value} B"
            : size.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays}d {value.Hours:00}h {value.Minutes:00}m"
        : value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes:00}m"
            : value.TotalMinutes >= 1
                ? $"{(int)value.TotalMinutes}m {value.Seconds:00}s"
                : $"{value.Seconds}s";

    /// <summary>A link with the current palette; the text is already-HTML when <paramref name="html"/> is set.</summary>
    public static string Link(string href, string text, string? css = null, bool html = false) =>
        $"<a{(css is null ? "" : $" class=\"{A(css)}\"")} href=\"{A(href)}\">{(html ? text : E(text))}</a>";

    public static string Chip(string text, string? css = null) =>
        $"<span class=\"chip{(css is null ? "" : " " + A(css))}\">{E(text)}</span>";

    public static string Card(string title, string body, string? actions = null, bool flush = false) =>
        $"""
         <section class="section">
           <h2><span>{E(title)}</span>{(actions is null ? "" : $"<span class=\"push\">{actions}</span>")}</h2>
           <div class="body{(flush ? " flush" : "")}">{body}</div>
         </section>
         """;

    public static string Kpi(string value, string label, string? hint = null) =>
        $"""
         <div class="kpi"><b>{E(value)}</b><span>{E(label)}</span>{(hint is null ? "" : $"<i>{E(hint)}</i>")}</div>
         """;

    public static string Notice(string html) => $"<div class=\"notice\">{html}</div>";

    public static string Empty(string text) => $"<div class=\"empty\">{E(text)}</div>";

    /// <summary>A data grid: headers then already-formatted cells.</summary>
    public static string Grid(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder("<div class=\"scroller\"><table class=\"grid\"><thead><tr>");
        foreach (var header in headers) builder.Append("<th>").Append(E(header)).Append("</th>");
        builder.Append("</tr></thead><tbody>");

        var any = false;
        foreach (var row in rows)
        {
            any = true;
            builder.Append("<tr>");
            foreach (var cell in row) builder.Append("<td>").Append(cell).Append("</td>");
            builder.Append("</tr>");
        }

        if (!any) builder.Append($"<tr><td colspan=\"{headers.Count}\" class=\"dim\">{E("No rows.")}</td></tr>");
        builder.Append("</tbody></table></div>");
        return builder.ToString();
    }

    /// <summary>A definition list of facts, two columns, monospace values.</summary>
    public static string Facts(IEnumerable<(string Name, string ValueHtml)> facts)
    {
        var builder = new StringBuilder("<dl class=\"facts\">");
        foreach (var (name, value) in facts)
            builder.Append("<dt>").Append(E(name)).Append("</dt><dd>").Append(value).Append("</dd>");
        builder.Append("</dl>");
        return builder.ToString();
    }

    /// <summary>Builds a URL from a path and the parameters that should survive the request.</summary>
    public static string Url(string path, params (string Key, object? Value)[] parameters)
    {
        var builder = new StringBuilder(path);
        var first = true;
        foreach (var (key, value) in parameters)
        {
            if (value is null) continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) continue;
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(text));
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A stored ISO timestamp as the reader's local time, with the original in the tooltip:
    /// the database keeps UTC, the dashboard shows the clock on this machine.
    /// </summary>
    public static string Timestamp(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return $"<span class=\"mono\">{E(iso)}</span>";

        return $"<time class=\"mono\" datetime=\"{A(at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}\" " +
               $"title=\"{A(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}\">" +
               $"{E(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}</time>";
    }

    // ── the document ─────────────────────────────────────────────────────────

    private static readonly (string Key, string Label, string Href)[] Navigation =
    [
        ("overview", "Overview", "/"),
        ("tables", "Tables", "/tables"),
        ("tools", "Tools", "/tools"),
        ("log", "Live log", "/log"),
        ("schema", "Schema", "/schema"),
    ];

    public static string Document(string title, string active, string meta, string body, string commands) => $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="dark light">
        <title>{E(title)} · VisualMCP</title>
        <style>{Css}</style>
        </head>
        <body>
        <aside class="side">
        <a class="brand" href="/">VisualMCP<span>dashboard</span></a>
        <nav>
        {Nav(active)}
        </nav>
        {commands}
        <div class="sidefoot">{meta}</div>
        </aside>
        <main>
        {body}
        </main>
        <div class="foot">Read-only view of the running server · <span class="mono">.visualMCP/visualmcp.db</span></div>
        </body>
        </html>
        """;

    private static string Nav(string active) => string.Join("\n", Navigation.Select(item =>
        $"<a class=\"{(item.Key == active ? "on" : "")}\" href=\"{item.Href}\">{item.Label}</a>"));

    private const string Css = """
        *,*::before,*::after{box-sizing:border-box}
        :root{
          color-scheme:dark;
          --bg:#0b0e13;--bg2:#0e1219;--panel:#121821;--panel2:#18202b;--line:#222c39;--line2:#2e3a4b;
          --text:#e7edf6;--dim:#8b98aa;--dim2:#6b7a8d;
          --accent:#7aa7ff;--ok:#3fb950;--warn:#d29922;--bad:#f85149;--mag:#c792ea;
          --mono:ui-monospace,SFMono-Regular,"Cascadia Mono",Menlo,Consolas,monospace;
          --sans:system-ui,-apple-system,"Segoe UI",Roboto,Inter,sans-serif;
          --r:10px;--side:252px;
        }
        @media (prefers-color-scheme:light){
          :root{color-scheme:light;
            --bg:#f4f6fa;--bg2:#eef1f7;--panel:#fff;--panel2:#f6f8fc;--line:#dde3ec;--line2:#c7d1df;
            --text:#111823;--dim:#59657a;--dim2:#7b8798;
            --accent:#2a5fd7;--ok:#11763a;--warn:#8a5b00;--bad:#b42318;--mag:#6d28d9;}
        }
        html{-webkit-text-size-adjust:100%}
        body{margin:0;padding-left:var(--side);background:var(--bg);color:var(--text);
             font:14px/1.55 var(--sans);-webkit-font-smoothing:antialiased}
        a{color:var(--accent);text-decoration:none}
        a:hover{text-decoration:underline}
        aside.side{position:fixed;top:0;left:0;bottom:0;z-index:20;width:var(--side);
                   display:flex;flex-direction:column;background:var(--bg2);
                   border-right:1px solid var(--line)}
        .side .brand{display:block;padding:14px 16px;border-bottom:1px solid var(--line);
                     color:var(--text);font-weight:700;font-size:15px;letter-spacing:-.01em}
        .brand span{margin-left:8px;color:var(--dim2);font-weight:500;font-size:11px;
                    text-transform:uppercase;letter-spacing:.09em}
        nav{display:flex;flex-direction:column;gap:1px;padding:10px}
        nav a{padding:6px 10px;border-radius:8px;color:var(--dim);font-size:13px}
        nav a:hover{color:var(--text);background:var(--panel2);text-decoration:none}
        nav a.on{color:var(--text);background:var(--panel2);box-shadow:inset 0 0 0 1px var(--line)}
        .sidesec{display:flex;align-items:baseline;gap:8px;padding:11px 16px 6px;
                 border-top:1px solid var(--line);color:var(--dim2);font-size:10px;font-weight:600;
                 text-transform:uppercase;letter-spacing:.09em}
        .sidesec a{margin-left:auto;color:var(--dim2);font:500 11px var(--sans);
                   text-transform:none;letter-spacing:0;white-space:nowrap}
        .sidesec a:hover{color:var(--accent)}
        .sidelist{flex:1;min-height:0;overflow-y:auto;display:flex;flex-direction:column;
                  gap:1px;padding:0 8px 12px}
        .cmd{display:flex;align-items:baseline;gap:8px;min-width:0;padding:3px 8px;
             border-radius:6px;color:var(--dim);font:12px/1.6 var(--mono)}
        .cmd:hover{background:var(--panel2);color:var(--text);text-decoration:none}
        .cmd b{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;
               font-weight:400}
        .cmd span{flex:none;color:var(--dim2);font-size:11px}
        .sidenote{padding:2px 16px 12px;color:var(--dim2);font-size:12px}
        .sidefoot{margin-top:auto;padding:9px 16px;border-top:1px solid var(--line);
                  color:var(--dim2);font:11px/1.5 var(--mono);overflow-wrap:anywhere}
        main{max-width:1500px;margin:0 auto;padding:22px 22px 8px}
        h1{font-size:22px;line-height:1.25;letter-spacing:-.02em;margin:0 0 3px}
        .sub{color:var(--dim);font-size:13px;margin:0 0 18px;max-width:90ch}
        .row{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}
        .push{margin-left:auto;font-weight:400;text-transform:none;letter-spacing:0}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px;margin:0 0 18px}
        .kpi{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);padding:13px 15px}
        .kpi b{display:block;font-size:23px;line-height:1.2;font-variant-numeric:tabular-nums;letter-spacing:-.01em}
        .kpi span{display:block;color:var(--dim);font-size:12px;margin-top:2px}
        .kpi i{display:block;color:var(--dim2);font-size:11px;font-style:normal;font-family:var(--mono)}
        .section{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);
                 margin:0 0 16px;overflow:hidden}
        .section>h2{display:flex;align-items:center;gap:10px;margin:0;padding:10px 15px;
                    font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.09em;
                    color:var(--dim);border-bottom:1px solid var(--line);background:var(--bg2)}
        .section>.body{padding:14px 15px}
        .section>.body.flush{padding:0}
        .grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px}
        @media (max-width:1100px){.grid2{grid-template-columns:1fr}}
        .scroller{overflow:auto;max-height:calc(100vh - 250px)}
        table.grid{width:100%;border-collapse:separate;border-spacing:0;font-size:13px}
        table.grid th{position:sticky;top:0;z-index:1;background:var(--panel2);color:var(--dim);
                      text-align:left;font-weight:600;white-space:nowrap;padding:7px 12px;
                      border-bottom:1px solid var(--line)}
        table.grid th a{color:var(--dim)}
        table.grid th a.on{color:var(--text)}
        table.grid td{padding:6px 12px;border-bottom:1px solid var(--line);vertical-align:top;
                      max-width:540px;overflow-wrap:anywhere}
        table.grid tbody tr:hover{background:var(--panel2)}
        table.grid tbody tr:last-child td{border-bottom:0}
        table.plain{width:100%;border-collapse:collapse;font-size:13px}
        table.plain th{text-align:left;color:var(--dim);font-weight:600;padding:6px 10px;
                       border-bottom:1px solid var(--line);white-space:nowrap}
        table.plain td{padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}
        table.plain tbody tr:last-child td{border-bottom:0}
        .mono,.cell,code{font-family:var(--mono);font-size:12px}
        .num{font-family:var(--mono);font-size:12px;font-variant-numeric:tabular-nums}
        .dim{color:var(--dim)}
        .pre{white-space:pre-wrap;overflow-wrap:anywhere}
        .chip{display:inline-block;padding:0 7px;border-radius:999px;border:1px solid var(--line2);
              color:var(--dim);font:11px/1.7 var(--mono);white-space:nowrap}
        .chip.ok{color:var(--ok);border-color:var(--ok)}
        .chip.warn{color:var(--warn);border-color:var(--warn)}
        .chip.bad{color:var(--bad);border-color:var(--bad)}
        .chip.acc{color:var(--accent);border-color:var(--accent)}
        .dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--ok);
             margin-right:6px;vertical-align:1px}
        .toolbar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:10px 15px;
                 border-bottom:1px solid var(--line);background:var(--panel)}
        .toolbar form{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0}
        input[type=search],input[type=text],select{background:var(--bg2);border:1px solid var(--line2);
          color:var(--text);border-radius:8px;padding:5px 9px;font:13px var(--sans);min-width:0}
        input[type=search]{width:280px}
        input[type=search]:focus,input[type=text]:focus,select:focus{outline:2px solid var(--accent);
          outline-offset:-1px}
        .btn{display:inline-block;padding:5px 11px;border-radius:8px;border:1px solid var(--line2);
             background:var(--panel2);color:var(--text);font-size:12px;cursor:pointer}
        .btn:hover{border-color:var(--accent);text-decoration:none}
        .pager{display:flex;align-items:center;gap:12px;padding:10px 15px;border-top:1px solid var(--line);
               color:var(--dim);font-size:12px}
        .pager .spacer{margin-left:auto}
        dl.facts{display:grid;grid-template-columns:190px 1fr;gap:5px 18px;margin:0;font-size:13px}
        dl.facts dt{color:var(--dim)}
        dl.facts dd{margin:0;font-family:var(--mono);font-size:12px;overflow-wrap:anywhere}
        @media (max-width:700px){dl.facts{grid-template-columns:1fr;gap:0}dl.facts dd{margin-bottom:8px}}
        .empty{padding:22px 15px;color:var(--dim);text-align:center;font-size:13px}
        .notice{border:1px solid var(--warn);border-radius:var(--r);padding:11px 15px;margin:0 0 16px;
                font-size:13px;color:var(--text)}
        .notice b{color:var(--warn)}
        .mutedbox{border:1px dashed var(--line2);border-radius:var(--r);padding:14px 16px;color:var(--dim);
                  font-size:13px;margin:0 0 16px}
        details summary{cursor:pointer;color:var(--accent);font-size:12px}
        details[open] summary{margin-bottom:8px}
        .schema details{padding:9px 15px;border-bottom:1px solid var(--line)}
        .schema details:last-child{border-bottom:0}
        .schema .scroller{overflow:visible;max-height:none}
        .schema details>summary{margin-bottom:6px}
        pre.full{margin:0;white-space:pre-wrap;word-break:break-word;font:12px/1.5 var(--mono);
                 color:var(--text)}
        .log{font:12px/1.65 var(--mono)}
        .log .line{display:grid;grid-template-columns:80px 56px 1fr;gap:10px;padding:1px 15px;
                   border-bottom:1px solid rgba(127,127,127,.08)}
        .log .line:hover{background:var(--panel2)}
        .log .t{color:var(--dim2)}
        .log .g{font-weight:600;white-space:nowrap}
        .log .g.tool{color:var(--accent)}
        .log .g.req{color:var(--mag)}
        .log .g.err{color:var(--bad)}
        .log .g.warn{color:var(--warn)}
        .log .g.start{color:var(--ok)}
        .log .g.lvl{color:var(--dim2);font-weight:400}
        .log .m{white-space:pre-wrap;overflow-wrap:anywhere}
        .log .cont{grid-template-columns:80px 56px 1fr;color:var(--dim)}
        .tool{padding:12px 15px;border-bottom:1px solid var(--line)}
        .tool:last-child{border-bottom:0}
        .tool h3{margin:0 0 3px;font:600 13px/1.4 var(--mono);display:flex;gap:8px;align-items:baseline;flex-wrap:wrap}
        .tool p{margin:0;color:var(--dim);font-size:12.5px;max-width:120ch}
        .tool ul{margin:7px 0 0;padding:0;list-style:none;display:flex;flex-wrap:wrap;gap:6px}
        .tool li{font:11px/1.6 var(--mono);border:1px solid var(--line);border-radius:6px;
                 padding:1px 7px;color:var(--dim);background:var(--bg2)}
        .tool li b{color:var(--text);font-weight:600}
        .foot{max-width:1500px;margin:0 auto;padding:18px 22px 40px;color:var(--dim2);font-size:11px;text-align:center}
        @media (max-width:900px){
          body{padding-left:0}
          aside.side{position:static;width:auto;border-right:0;border-bottom:1px solid var(--line)}
          aside.side nav{flex-direction:row;flex-wrap:wrap}
          .sidelist{flex:none;max-height:180px}
          .sidefoot{margin-top:0}
        }
        """;
}
