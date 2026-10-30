# Web dashboard

A read-only web view of the running server and of the solution store it writes
(`<solution>/.visualMCP/visualmcp.db`). It is hosted **inside the MCP process** — no separate
process to launch — and answers on loopback only.

```
http://127.0.0.1:4677
```

There is no page that writes: every route is a `GET` over SQLite and the live process. A viewer
therefore cannot corrupt the store, and an unreachable port cannot break the MCP session.

## Port: 4677, then the remembered port, then a random one

The dashboard tries, in order:

1. **4677** (`VISUALMCP_DASHBOARD_PORT` changes the first choice).
2. The port it **last bound**, kept in the store's `context` table as `dashboard.port`. This is
   what makes a second instance — two MCP clients on the same solution — land on a stable port
   of its own instead of hopping on every start.
3. **An OS-assigned port**, read back from the listener.

Whatever it binds is written back to `dashboard.port`, so the next start reuses it. The choice is
logged in `debug.log`:

```
Web dashboard on http://127.0.0.1:4677 — D:\REPO\.visualMCP\visualmcp.db
```

When every candidate fails, the dashboard logs a warning and the MCP server continues untouched.
The current URL is always visible on the overview page and in `GET /api/health`.

| Variable | Effect |
|---|---|
| `VISUALMCP_DASHBOARD=off` (or `0`, `false`, `no`) | Do not start the dashboard |
| `VISUALMCP_DASHBOARD_PORT=<n>` | Change the preferred first port (still falls back) |
| `VISUALMCP_DASHBOARD_HOST=<host>` | Bind another interface. Loopback by default; exposing it on a LAN is a deliberate choice |

## Pages

| Page | What it shows |
|---|---|
| `/` | Process facts (pid, uptime, working set, GC heap, runtime, transport, working directory, log path), the loaded solutions with their project/document counts, the counts of the most important tables, the latest tool calls and edits |
| `/tables` | Every table in the store, grouped by area, with its row count and column count |
| `/t/{table}` | One page per table: substring filter over every non-blob column, sortable headers, paging, and `CSV` / `JSON` exports |
| `/schema` | The columns of every table as SQLite reports them (`type`, `not null`, `primary key`) |
| `/tools` | The tool catalogue this process advertises: name, read-only/destructive hint, description and every argument (type, required, description), with a filter box |
| `/log` | The tail of `debug.log`, newest first, refreshed every 2.5 s without reloading the page; filter by text and by kind (tool calls, requests, errors, warnings) |

### The fixed left column

Every page shares a column fixed on the left. It holds the navigation, and under it **Commands
invoked**: the last fourteen tool calls the server received, newest first, each one a link to
`/t/command_log?q=<tool>` — that tool's calls only — with the total beside the heading opening the
whole log. It reads the same `command_log` rows that the overview's *Recent tool calls* card shows,
so the calls stay visible while you read any other page, and the process facts (pid, uptime, port)
sit at its foot. On a window narrower than 900 px the column folds to the top of the page.

**One page per table, whatever the schema is.** Tables are discovered from `sqlite_master`, not
from a hard-coded list, so a table added by a future feature gets a page immediately. The known
tables carry a title, an area and a sentence describing what a row means
(`DashboardCatalogue`); anything else appears under **Other** with a generic description.
FTS5 shadow tables (`ti_line_data`, `_idx`, `_content`, `_docsize`, `_config`) are hidden: they
are the index's storage, not something to browse.

The tables the store is known to contain today:

| Area | Tables |
|---|---|
| Activity | `command_log`, `applied_edit`, `context` |
| Board | `board_item`, `board_comment`, `board_history` |
| Indexes | `si_symbol`, `si_usage`, `si_document`, `si_metacode`, `source_document`, `mc_document`, `mc_tree`, `ti_document`, `ti_row`, `ti_line` |
| Packages | `package_fact`, `package_dependency` |
| Artifacts | `decompiled_artifact` |

### Query parameters

`/t/{table}`

| Parameter | Meaning |
|---|---|
| `q` | Substring over every non-blob column (`%` and `_` are literal) |
| `page`, `size` | 1-based page; page size 1–1000 (default 100) |
| `sort`, `dir` | Column to sort by and `asc`/`desc`; the default is newest first (`rowid desc`) |
| `format` | `html` (default), `csv` (attachment, up to 20,000 rows) or `json` |

`/log`

| Parameter | Meaning |
|---|---|
| `q` | Substring over the line |
| `lines` | How many trailing lines to read (20–3000, default 300) |
| `only` | `tool`, `req`, `err`, `warn` — the kinds of line |
| `fragment=1` | Return only the feed's HTML, which is what the auto-refresh fetches |
| `live=0` | Do not run the refresh script |

## JSON API

| Endpoint | Answer |
|---|---|
| `GET /api/health` | `ok`, pid, port, url, status, started-at, uptime, database path |
| `GET /api/overview` | Process facts, dashboard state, database path and size, per-table row counts |
| `GET /api/tables` | Every table: name, kind, rows, columns, title, area, description |
| `GET /api/tables/{table}` | One page of rows (`page`, `size`, `q`, `sort`, `dir`) as a column-name-free array plus the column list |

Blobs are summarized (`<blob N bytes>`) in every JSON and CSV answer: a dashboard answer must
never carry a file's bytes.

## Safety

- **Loopback only** by default, so nothing is exposed to the network.
- **No stdout.** The MCP transport owns stdout; the dashboard clears every logging provider of its
  own host and logs through the same file logger as the rest of the server.
- **Response headers** on every answer: `X-Content-Type-Options: nosniff`, `Referrer-Policy:
  no-referrer`, `Cache-Control: no-store`, and a CSP of `default-src 'none'` plus inline
  style/script only (the pages are server-rendered and every value read from the database is
  HTML-encoded).
- **Table names** come from `sqlite_master`: a name supplied in the URL is resolved against the
  database and the *returned* name is what reaches the SQL, so a request cannot inject SQL.
- **A discovery probe skips the dashboard**, like it skips the eager solution load: a probe never
  pays for opening the store.
- The dashboard starts only after the client's first message, in the same background task that
  opens the environment; the MCP host itself is never blocked by it.

## Files

```
src/VisualMCP/Dashboard/
  DashboardOptions.cs    ← port policy (preferred → remembered → random), env overrides
  DashboardDatabase.cs   ← read-only SQLite: tables, columns, rows, paging, search, export
  DashboardCatalogue.cs  ← title/area/description of the known tables
  DashboardPages.cs      ← the pages (overview, tables, one per table, schema, tools, log)
  Html.cs                ← document template, stylesheet, escaping, small components
  DashboardServer.cs     ← the Kestrel host, routing, JSON/CSV endpoints, port persistence
```

The project references the `Microsoft.AspNetCore.App` shared framework for the HTTP host; the
self-contained release bundles it and the framework-dependent publish adds no files.

Tests: `src/VisualMCP.Tests/DashboardTests.cs` — the data layer (discovery, filtering, paging,
blob exclusion, injection refusal), the command list the sidebar reads, the port policy, and a real
listener that serves every page.
