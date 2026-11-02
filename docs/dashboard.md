# Web interface (the dashboard)

Every running VisualMCP server hosts a web view of itself and of the solution store it writes
(`<workspace>/.visualMCP/visualmcp.db`) — read-only, except the board page, which edits cards. It runs
**inside the MCP process** — there is no second process to launch, no port to forward by hand — and
answers on loopback only:

```
http://127.0.0.1:4677
```

It is an observer, with one deliberate exception: the **board page** edits cards. Every other route is a
`GET` that reads SQLite and the live process; no page deletes a row or changes a setting. The board's
writes go through the same service the `board_*` tools call — history, renumbering and all — and only
through a same-origin JSON POST that the page itself sends (see [Safety](#safety)). That is why it can
run at any time without touching the solution, and why a dashboard that cannot bind a port is a warning
in the log rather than a failure of the MCP session.

This page is the full reference: lifecycle, port policy, every page and query parameter, the complete
list of tables it can show, the JSON API, the response headers, troubleshooting and the limits.

## At a glance

| | |
|---|---|
| URL | `http://127.0.0.1:4677` by default; the bound URL is in `debug.log` and in `GET /api/health` |
| Process | Inside the VisualMCP MCP server (ASP.NET Core Kestrel, started in the background) |
| Lifecycle | Starts after the client's first message, stops with the host |
| Writes | None except the board page, which moves, edits, comments and deletes cards (same-origin JSON POST with an `X-VisualMCP: 1` header) |
| Exposure | Loopback by default; no authentication, so exposing it is a deliberate choice |
| Data | The solution store of the solution under the working directory, and the server's own `debug.log` |
| Dependencies | The `Microsoft.AspNetCore.App` shared framework; pages are server-rendered, inline, offline |
| Tests | `src/VisualMCP.Tests/DashboardTests.cs` — 11 cases over the data layer, the port policy, a real listener, and the board page and its guarded writes |

## Lifecycle

The dashboard is started by the server itself, in the same background task that opens the environment:

1. The process waits up to five seconds for the client to identify itself, so a **discovery probe**
   (`server/discover`) can be skipped entirely. A probe never pays for the store or the dashboard.
2. Once the client is known — or after those five seconds, when no message arrived — the dashboard
   binds **before** the solution load: it is independent of the load and never blocks the MCP transport.
3. The load continues in the same task. Tools also open on demand, so the ordering is a latency
   optimisation, not a correctness requirement.

The `--cli` mode never starts the dashboard (nor the MCP transport): that mode exists for single-command
invocations such as the routing hook and the client configuration edit.

On shutdown the host stops the listener with a three-second budget; a listener that is already gone is
not an error. A failure to bind any port is logged as a warning and leaves the MCP server untouched.

## Port and configuration

The dashboard tries, in order:

1. **4677** — `VISUALMCP_DASHBOARD_PORT` changes this first choice.
2. The ports kept by instances that are **no longer running**. Each process owns its own
   `dashboard.port.<pid>` row in the store's `context` table, so two live servers on the same workspace
   keep separate ports instead of overwriting one shared value.
3. **An OS-assigned port**, read back from the listener.

Whatever it binds is written to this instance's row, so a restart can reuse a dead instance's port. The
choice appears in `debug.log`:

```
Web dashboard on http://127.0.0.1:4677 — D:\REPO\.visualMCP\visualmcp.db
```

When every candidate fails, the last error is logged and `GET /api/health` reports
`"status": "failed"`; the MCP server continues. The current URL is always on the overview page and in
`GET /api/health`.

### More than one server on one workspace

A client starts one server per session, so two sessions on the same folder run two processes on one
store. Each writes its own `dashboard.port.<pid>` row, so a live sibling is never a candidate and the
two dashboards keep separate ports. The result is **one dashboard per process**, each showing the same
store, its own pid and its own port: open the URL from the log of the session you are in. The store,
the board and the history are shared; the metacode watcher is elected with a lease. The full model is
[The workspace, the store and multiple sessions](workspace-and-store.md).

| Variable | Effect |
|---|---|
| `VISUALMCP_DASHBOARD=off` (or `0`, `false`, `no`) | Do not start the dashboard |
| `VISUALMCP_DASHBOARD_PORT=<n>` | Change the preferred first port (still falls back to the remembered and then a random one) |
| `VISUALMCP_DASHBOARD_HOST=<host>` | Bind another interface. Loopback by default; the dashboard has no authentication, so exposing it on a LAN is a deliberate choice |

The log page needs a log file. The server passes its `debug.log` path to the dashboard; when no path is
configured, the log page says so and the rest of the dashboard keeps working.

## Pages

| Page | What it shows |
|---|---|
| `/` | The overview: process facts, the loaded solutions, the store's counts, the latest tool calls and edits, quick links by area |
| `/board` | The project's board: five columns, a card dragged to move it, and the detail panel of one card (fields, move, comments, history, delete) |
| `/tables` | Every table the store contains, grouped by area, with its row count, column count and a sentence describing a row |
| `/t/{table}` | One page per table: filter across every non-blob column, sortable headers, paging, and CSV/JSON export |
| `/schema` | The columns of every table as SQLite reports them (`type`, `not null`, `primary key`) |
| `/tools` | The tool catalogue this process advertises: name, read-only/destructive hint, description, and every argument with its type, whether it is required, and its description |
| `/log` | The tail of `debug.log`, newest first, refreshed every 2.5 s without reloading the page; filter by text and by kind |

Anything else answers a 404 page with a link back to the overview. An exception inside a page is caught
and rendered as a 500 page with the exception text — without the sidebar, because a failing store may be
the reason the page failed. `favicon.ico` answers `204`.

### `/` — the overview

- **KPI cards**: uptime, tool calls logged, file edits recorded, board items, symbol declarations,
  indexed documents, package facts.
- **Server**: process name and pid, start time, working set and thread count, managed heap, runtime and
  OS, transport (`stdio`, JSON-RPC on stdin/stdout), working directory, the dashboard URL, the database
  path and size, the log path, and the VisualMCP version.
- **Solutions**: every solution discovered under the working directory, its state (`loaded` /
  `not loaded`, plus `active` for the one the tools use), its project count and document count.
- **Recent tool calls**: the last twelve rows of `command_log` (when, tool, arguments) with a link to
  the whole log.
- **Recent edits**: the last eight rows of `applied_edit` (when, tool, file, characters before → after
  with the delta) with a link to the whole history.
- **Where to look**: quick links to the tables the server uses, grouped as Activity, Board, Indexes,
  Packages & artifacts and Native.

If no solution has been discovered yet, the page says so instead of showing empty tables: the store is
created when the environment is known.

### `/board`

The project's board, rendered as five columns — backlog, todo, in progress, review, done — with the
cards in their stored position. Each card shows its title, its priority and labels, the assignee, its
comment count and how long ago it was updated.

- **Drag a card** onto a column to move it. The drop position becomes the card's new position, the move
  goes through the same service `board_move` uses (history entry and renumbering included), and the page
  reloads with the new order.
- **Open a card** for its detail panel: title, description, priority, assignee and labels in an editable
  form; a *move to* selector, which is the keyboard alternative to dragging; the comments with their
  form; and the card's own history — every field change with the before and after values.
- **New card** opens a form with a title, a description, a column, a priority, an assignee and labels;
  creating it opens the new card's panel.
- **Delete** removes the card with its comments and history, after a confirmation.

The page is server-rendered and readable without JavaScript; the interactions are one inline script that
posts to the endpoints below and reloads. A card id in `?card=` that the board does not hold shows a
notice and the board alone, so a deleted card leaves the page usable.

### `/tables`

Every table and view from `sqlite_master`, one card per area, each row carrying the table name (a link),
its title, its row count, its column count and the sentence that describes what a row means. Tables are
discovered, not hard-coded, so a table added by a future feature gets a page immediately; it appears
under **Other** until a catalogue entry gives it a title, an area and a description.

### `/t/{table}`

The table's title and description, its kind (or `virtual/FTS5 table`), its row count, then a toolbar:

- a **filter** box (`q`) that is a substring match over every **non-blob** column; `%`, `_` and `\` are
  literal, so searching for `50%` finds `50%`;
- a **page size** selector (25, 50, 100, 250, 500, 1000; the server defaults to 100 and clamps to
  1–1000);
- **CSV** and **JSON** export buttons that keep the current filter, sort and direction;
- **clear filter** when a filter is active.

The grid shows every column with its SQLite type as a small chip. Clicking a header sorts by that column
and toggles ascending/descending; the default is **newest first** (`rowid DESC`) for a table that has a
rowid, and the first column ascending otherwise. Sorting is only accepted on a real column: an unknown
name falls back to the default order.

Cell rendering:

- `NULL` is a dim dash; an empty string is `(empty)`.
- Blobs are summarised as `<N B blob>` — the dashboard never serves a file's bytes.
- Numbers are formatted with a thousands separator; text whose column is named `*_at`, `at` or `date`
  is rendered as a timestamp.
- Long or multi-line text collapses into a `details`/`summary` element with the full value (capped)
  inside a `pre` block.

The pager shows `first–last of total`, the page number and count, and first/prev/next/last links. A page
beyond the last one is clamped to the last.

An unknown table is a 404 page, not an empty grid.

### `/schema`

The columns of every table, exactly as SQLite reports them through `PRAGMA table_info`: name, type,
`not null` and `pk` chips, grouped by area. Each table is an expandable section, open by default when it
has twelve columns or fewer. This is the page to open when a value on a table page needs explaining.

### `/tools`

The tools this process advertises to its MCP client. Each entry has the tool name, its title (when it has
one), a **read-only** or **destructive** chip from the tool's annotations, the description, and every
argument with its type and a `*` for required. A filter box matches the name and the start of the
description, client-side.

The page reads the live MCP tool collection. Before the MCP server has been constructed there may be no
live collection to read, so it falls back to the attributes on the tool classes — the same names and
descriptions the client is offered. The client's own tool list remains authoritative for argument
defaults and schema details; the dashboard is a convenient view of it.

### `/log`

The tail of `debug.log`, newest line first, as a coloured feed:

- **Filter** (`q`) over the line, a **line count** (100, 300, 1000, 3000; the server defaults to 300
  and clamps to 20–3000), and a **kind** filter: all lines, tool calls, requests, errors, warnings.
- **Kinds** are read from the log tag: `TOOL` → tool calls, `REQ` → requests, `ERROR`/`FAIL`/`CRITICAL`/
  `FATAL` → errors, `WARNING`/`WARN` → warnings; `DEBUG`, `TRACE` and `INFORMATION` are "other lines".
  A line with no recognised shape (for example the `--- VisualMCP started ---` banner) is shown as
  `BOOT`.
- **Live refresh**: the page fetches `/log?fragment=1` (the same filters, HTML only) every 2.5 seconds
  and replaces the feed without reloading. **Pause** stops the refresh and turns into **Resume**.
- `live=0` renders the page without the refresh script. `fragment=1` returns only the feed, which is
  what the auto-refresh asks for.
- Long messages are shortened to 600 characters with `… (N more chars)`, because a tool call's argument
  JSON can be tens of kilobytes and the feed refreshes every couple of seconds.
- Only the last 512 KB of the file are read, so a huge log stays fast; a line cut by the window is
  dropped rather than shown half-read.

When the page is opened with no filter and no log file exists, it says which of the two is missing.

### The fixed left column

Every page shares a column fixed on the left. It holds the navigation and, under it, **Commands
invoked**: the last fourteen tool calls the server received, newest first, each one a link to
`/t/command_log?q=<tool>` — that tool's calls only — with the total beside the heading opening the whole
log. The list reads the same `command_log` rows the overview's card shows, so the calls stay visible
while you read any other page. The process facts (pid, uptime, port) sit at its foot. On a window
narrower than 900 px the column folds to the top of the page.

## The tables it shows

The dashboard discovers tables and views from `sqlite_master` and hides `sqlite_*` internals and the
shadow tables of an FTS5 index (`ti_line_data`, `_idx`, `_content`, `_docsize`, `_config`). The known
tables carry a title, an area and a description (`DashboardCatalogue`); anything else appears under
**Other** with a generic description.

The store's schema defines these tables. Some exist only after the feature that fills them has run (an
index built, a package graph refreshed, a native binary decompiled), and they appear on the tables page
once they do.

| Area | Table | What a row means |
|---|---|---|
| Activity | `command_log` | Every tool call the server received: when, which tool, the arguments. Written as the call arrives, so a call that fails or is cancelled is still here. |
| Activity | `applied_edit` | Every file this server wrote: tool, rules, before/after hashes and sizes. The pre-write text is kept compressed, which is what makes an edit revertible. |
| Activity | `context` | Facts the server caches between restarts (the last inspection report, the dashboard port). A key, its value and a freshness stamp. |
| Activity | `file_snapshot` | Local history: a compressed whole-file snapshot per save, independent of commits (the newest 50 per file; a file over 1.5 MB is skipped). **Other** area until a catalogue entry describes it. |
| Board | `board_item` | The kanban cards: column, priority, labels, assignee. Position is the order inside the column. |
| Board | `board_comment` | Append-only comments attached to a board item. |
| Board | `board_history` | Every field change of a board item, in order — the card's own audit trail. |
| Board | `board_memory` | Durable notes kept with the board: one current version per topic, the superseded ones kept as history. **Other** area until a catalogue entry describes it. |
| Indexes | `si_symbol` | Declarations indexed by the symbol index: name, signature, kind, accessibility, container, project, source span. |
| Indexes | `si_usage` | The typed uses of each indexed symbol, with the file and span where they occur. |
| Indexes | `si_document` | One row per file the symbol index processed, with its content hash and counts. |
| Indexes | `si_metacode` | The metacode tree of a declaration stored against the symbol key the index knows it by. |
| Indexes | `source_document` | One row per source file of the solution in its own language: text, language tag, hash, size — including files whose language has no metacode frontend. |
| Indexes | `mc_document` | One row per document a metacode frontend read: hash, timestamp, language and the counts of declarations, nodes and diagnostics. |
| Indexes | `mc_tree` | One row per declaration: its canonical pseudocode (compressed) and its structural facts — cyclomatic and cognitive complexity, nesting, statements, branches. |
| Indexes | `mc_node` | The complete AST of every stored declaration, one row per node: parent link, parent property, position, depth, span and payload JSON. |
| Indexes | `ti_document` | One row per file in the full-text index, with length, modification time and line count. |
| Indexes | `ti_row` | The path/line to FTS row mapping of the text index. |
| Indexes | `ti_line` | The searchable FTS5 index: one row per line, with the raw line and the identifier-split copy FTS5 actually matches. |
| Packages | `package_fact` | The resolved package graph as the build sees it: one row per package and target framework, direct or transitive. |
| Packages | `package_dependency` | The edges of that graph: which package pulls which one in, at which version. |
| Artifacts | `decompiled_artifact` | Assemblies decompiled to source under `.visualMCP`, keyed on the module version id, so the same binary is never decompiled twice. |
| Artifacts | `native_artifact` | Native images Ghidra analysed: file, SHA-256, processor language, entry point, image base, functions recovered, where Ghidra lived. |
| Artifacts | `native_function` | One row per recovered function: name, entry address, prototype, Ghidra's decompiled C, the canonical pseudocode and its complexity. |
| Artifacts | `native_call` | Who calls whom in a recovered native image, by entry address. |

## Query parameters

`/board`

| Parameter | Meaning |
|---|---|
| `card` | The card whose detail panel opens above the board; an id the board does not hold shows a notice |

`/t/{table}`

| Parameter | Meaning |
|---|---|
| `q` | Substring over every non-blob column; `%` and `_` are literal |
| `page` | 1-based page; clamped to the last page |
| `size` | Page size, 1–1000 (default 100) |
| `sort` | Column to sort by; an unknown name is ignored |
| `dir` | `asc` or `desc` (default `desc`) |
| `format` | `html` (default), `csv` (attachment, up to 20,000 rows) or `json` |

`/log`

| Parameter | Meaning |
|---|---|
| `q` | Substring over the line |
| `lines` | How many trailing lines to read (20–3000, default 300) |
| `only` | `tool`, `req`, `err`, `warn` — the kinds of line; anything else is ignored |
| `fragment=1` | Return only the feed's HTML, which is what the auto-refresh fetches |
| `live=0` | Do not run the refresh script |

## Exports

- **CSV** — `format=csv` on a table page. The header is the table's columns and the rows are the ones
  matching `q`, newest first when the table has a rowid, capped at **20,000 rows** (the file is the
  limit, not the page). Values are written with the invariant culture and quoted when they contain a
  comma, a quote or a newline; the response is `text/csv` with a sanitised `Content-Disposition`
  filename and UTF-8 without a BOM.
- **JSON** — `format=json` on a table page returns one page of rows; `GET /api/tables/{table}` returns
  the same data with the column list, the total, the page and the sort state. Blobs are summarised in
  both (`<blob N bytes>`), so an export can never carry a file's bytes.

## JSON API

The read endpoints are `GET` and answer `application/json`; the five board endpoints are the only writes,
and each one requires the header and content type described under [Safety](#safety).

| Endpoint | Answer |
|---|---|
| `GET /api/health` | `ok`, pid, port, url, status, started-at, uptime in seconds, database path. The fastest way to find the dashboard if the port moved. |
| `GET /api/overview` | Process facts (`pid`, runtime, working directory, uptime), dashboard state (port, url, status), database path and size, and per-table row counts. |
| `GET /api/board` | Every column with its cards: `{ columns: [{ status, count, cards: [...] }] }`, the same document the MCP board tools return. |
| `GET /api/tables` | Every table: name, kind, rows, columns (name, type, primary key), title, area, description. |
| `GET /api/tables/{table}` | One page of rows (`page`, `size`, `q`, `sort`, `dir`): `table`, `kind`, `total`, `page`, `pageSize`, `pageCount`, `sort`, `descending`, `search`, `columns`, and `rows` as an array of arrays. An unknown table is a 404 with `{"error": "No table called '…'."}`. |
| `POST /api/board/move` | `{ id, status, position? }` — move a card; the position is omitted to append. Answers `{ ok, id }` or `{ ok: false, error }`. |
| `POST /api/board/update` | `{ id, title?, description?, priority?, assignee?, labels? }` — edit the fields; only the fields present change. |
| `POST /api/board/comment` | `{ id, body, author? }` — append a comment. |
| `POST /api/board/add` | `{ title, description?, status?, priority?, assignee?, labels? }` — create a card; answers with its `id`. |
| `POST /api/board/delete` | `{ id }` — delete the card with its comments and history. |

A refused write answers a 400 with the board service's own message (an unknown id, a column that does not
exist, an empty title); a call without the header, content type or same origin answers 403. Nothing is
written in either case.

## Safety

- **Loopback only** by default, so nothing is exposed to the network. There is **no authentication**:
  `VISUALMCP_DASHBOARD_HOST` on a LAN interface publishes the store and the log to whoever can reach
  the port. Do it only on a network you trust.
- **One write surface, guarded.** Only the five `/api/board/*` endpoints write. Each requires an
  `application/json` body, the `X-VisualMCP: 1` header, and — when the browser sends one — a
  same-origin `Origin`. A cross-origin script cannot send that header without a CORS preflight this
  host never grants, and an HTML form can set neither the content type nor the header, so a page on
  another origin cannot drive the board. This stops other origins; it is not authentication — anyone
  who can reach the port can call the endpoints directly.
- **The store connection only reads.** The pages read the SQLite file through their own connection,
  opened `ReadWrite` only because a read-only handle cannot roll back a hot journal when it opens; it
  issues nothing but `SELECT`s with a 4-second busy timeout. The board's writes go through the board
  service, which owns its transaction, history and renumbering.
- **Response headers** on every answer: `X-Content-Type-Options: nosniff`, `Referrer-Policy:
  no-referrer`, `Cache-Control: no-store`, and a CSP of `default-src 'none'` plus inline style/script
  only (`connect-src 'self'`, `img-src data:`, `base-uri 'none'`, `form-action 'self'`). The server
  header is not sent and the request body is capped at 64 KB.
- **Table names** come from `sqlite_master`: a name supplied in the URL is resolved against the
  database and the *returned* name is what reaches the SQL; identifiers are quoted, the filter is a
  parameter, and `%`/`_` in the user's text are escaped. A request cannot inject SQL.
- **Everything read from the database is HTML-encoded** — the table pages, the log, tool descriptions —
  and the pages are server-rendered from that escaped text. The escaped value is what the browser sees.
- **Blobs are summarised** in HTML, JSON and CSV answers; a dashboard response never carries a file's
  bytes.
- **No stdout.** The MCP transport owns stdout; the dashboard's host clears every logging provider it
  would otherwise install and logs through the same file logger as the rest of the server.
- **A discovery probe skips the dashboard**, like it skips the eager solution load: a probe never pays
  for opening the store.

## Troubleshooting

**The dashboard is unreachable.** The URL is in `debug.log` (`Web dashboard on http://…`) and in
`GET /api/health`. If 4677 is taken, this instance may be on its remembered port or an OS-assigned one —
open the URL from the log rather than assuming 4677. If every candidate failed, the log carries the
last error and `/api/health` reports `"status": "failed"`. Check `VISUALMCP_DASHBOARD` is not `off`.

**The pages are empty and there is no store.** No solution was discovered under the server's working
directory, so there is no `.visualMCP/visualmcp.db` yet. The overview says so explicitly; the store
appears as soon as a solution is found and a tool opens it. Start the client in the folder that holds
the `.sln`.

**The tools page is empty.** The MCP server has not been constructed yet and the reflected fallback found
no tool attributes — usually a build problem, not a dashboard one.

**The log page says "No log file is configured" or "No log file at … yet".** The server passes its log
path to the dashboard; without one the page is disabled. The two sentences say which case it is.

**A page answers 500.** The page caught an exception (a malformed schema, a store locked past the
timeout) and renders it with the stack trace. The MCP server keeps running; the 500 page is deliberately
rendered without the sidebar, which reads the same store.

**The log stops refreshing.** The feed polls only while the page is visible and the script is enabled:
check the **Pause** button, a `live=0` URL, or a client that blocks the fetch. A refresh failure is
ignored and retried, so a restart of the server recovers on its own.

## Limits

- **Read-only except the board.** No cell editing on a table page, no row deletion, no schema change, no
  setting change. The board editor is the only write surface, and it only touches board cards — through
  the board service, with the comment and history the tools would write.
- **Interactions need JavaScript.** The board renders and reads without it; dragging a card, saving the
  detail form, commenting and deleting are the page's inline script. Without it the board is a readable
  five-column view and every card still opens its detail panel. The JSON endpoints remain available to a
  script of your own.
- **One workspace.** It shows the store of the workspace the server was started in (its primary
  solution), not every solution on the machine. Another repository is another server instance, with its
  own database and port.
- **Counts are cached for up to 3 seconds** (the KPI cards and table row counts) to keep the overview
  cheap; the rows themselves are read live on every request.
- **Polling, not push.** The log refreshes every 2.5 seconds; every other page is a snapshot until you
  reload it.
- **Blobs and compressed text are not searchable.** The filter skips blob columns, so the pre-write text
  in `applied_edit` and a compressed tree in `mc_tree` are visible only through the features that read
  them. The FTS5 shadow tables are hidden as well.
- **The tools page is a view, not the protocol.** It reads the live tool collection (or the attributes
  before the server exists) and shows the schema it can read; the client's tool list is authoritative.
- **No charts, no history, no trace view.** The dashboard is a current-state viewer: it answers "what is
  in the store and what did the server just do", not "how did this metric evolve".
- **The pages are inline and offline.** No fonts, no CDN, no build step; that also means no rich
  component library, and the CSS is the one stylesheet in `Html.cs`.

## Source layout and tests

```
src/VisualMCP/Dashboard/
  DashboardOptions.cs    ← port policy (preferred → remembered → random), env overrides
  DashboardDatabase.cs   ← read-only SQLite: tables, columns, rows, paging, search, export
  DashboardBoard.cs      ← the board through IBoardService, projected into the typed records the page renders
  DashboardCatalogue.cs  ← title/area/description of the known tables
  DashboardPages.cs      ← the pages (overview, board, tables, one per table, schema, tools, log)
  Html.cs                ← document template, stylesheet, escaping, small components
  DashboardServer.cs     ← the Kestrel host, routing, JSON/CSV endpoints, the guarded board writes, port persistence
```

The host is wired in `Program.cs` (the `Dashboard/` block near the MCP server registration): options
from the environment, the database reader, the server singleton, and the `ApplicationStopping` hook that
stops the listener. The project references the `Microsoft.AspNetCore.App` shared framework; the
self-contained release bundles it and the framework-dependent publish adds no files.

`src/VisualMCP.Tests/DashboardTests.cs` covers the data layer (discovery, filtering, paging, blob
exclusion, injection refusal), the command list the sidebar reads, the port policy, the remembered port,
a real listener that serves every page, the CSV export, the JSON API and the live-log fragment, and the
board: the five columns and a card's detail panel, the guard on its writes (no header, a foreign origin,
an invalid column), and a move/add/comment/edit/delete round trip — 11 cases, including an assertion
that a `<script>` written into a tool argument comes back escaped and that the newest log line is
rendered first.
