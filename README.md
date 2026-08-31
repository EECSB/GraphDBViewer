<div align="center">

<h1 align="center">
  <img src="GraphDBViewerWeb/wwwroot/favicon.svg" alt="Graph DB Viewer logo" height="52" align="absmiddle" style="vertical-align: middle;" />
  &nbsp;Graph DB Viewer
</h1>

### Query, visualize and edit your graph database — right in the browser.

**A Blazor WebAssembly app** for exploring Gremlin, Cypher, AQL, DQL, GUN and SPARQL graphs. Run it as
**static files with no backend at all**, or behind a **small optional server** that reaches the databases
a browser cannot (one that sends no CORS headers, one on a private network, or Neo4j over Bolt).
Connect, run queries, and see your data as an interactive **2D** or **3D** graph, a **table**, or raw
**JSON** — then edit it and commit the changes back.

<sub>Blazor WebAssembly · .NET 10 · Cytoscape.js · 3d-force-graph / three.js · Monaco Editor · runs with or without a server</sub>

<p>
  <a href="https://eecs.blog/BlazorApps/GraphDBViewer/"><b>▶ Try it live</b></a>
  &nbsp;·&nbsp;
  <a href="#running-locally">Run it locally</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/EECSB/GraphDBViewerWeb/actions/workflows/ci.yml"><img src="https://github.com/EECSB/GraphDBViewerWeb/actions/workflows/ci.yml/badge.svg" alt="CI" style="vertical-align: middle;" /></a>
  &nbsp;
  <a href="https://hub.docker.com/r/eecsb/graphdbviewer-server"><img src="https://img.shields.io/docker/v/eecsb/graphdbviewer-server?label=docker&sort=semver" alt="Docker image version" style="vertical-align: middle;" /></a>
</p>

</div>

<div align="center">
<img src="docs/img/app-2d.png" alt="Graph DB Viewer showing a loaded graph in the interactive 2D view" width="900" />
</div>

---

## About

Graph DB Viewer began as part of another project of mine. While exploring graph databases, I wanted a viewer that runs entirely in the browser for quick and easy testing — and couldn't find one that did. So I built my own. It stands on its own as a full tool, but it's also designed to embed directly into that project: tightly integrated, with all of its functionality available behind the scenes.

> 🖥️ **Prefer a visual tour?** [**See it live**](https://eecs.blog/BlazorApps/GraphDBViewer/showcase/index.html) on the EECS blog — or open [`docs/index.html`](docs/index.html) locally (or serve the `docs/` folder via GitHub Pages). A one-page presentation of everything below.

---



## Table of contents

- [What it is](#what-it-is)
- [Highlights](#highlights)
- [Feature tour](#feature-tour)
  - [Connect to any graph](#-connect-to-any-graph)
  - [Write & run queries](#-write--run-queries)
  - [Visualize four ways](#-visualize-four-ways)
  - [Debug traversals step by step](#-debug-traversals-step-by-step)
  - [Edit the graph safely](#-edit-the-graph-safely)
  - [Import & export](#-import--export)
  - [Comfort features](#-comfort-features)
- [Supported databases](#supported-databases)
- [Running locally](#running-locally)
  - [Optional: a local Gremlin database to play with](#optional-a-local-gremlin-database-to-play-with)
- [Deploying](#deploying)
  - [The server edition (Docker)](#the-server-edition-docker)
- [Embedding in another page](#embedding-in-another-page)
- [How it works](#how-it-works)
- [Project layout](#project-layout)
- [Testing](#testing)
- [Privacy & limitations](#privacy--limitations)
- [License](#license)

---

## What it is

Graph DB Viewer runs **in your browser**. There is **no login and no install** — the app is static files
that talk to your database directly, and everything you save (connections, queries, history, theme, open
tabs) stays on your machine, in the browser's IndexedDB.

Some databases a browser simply cannot reach, however it is written: one that sends no CORS headers, one
on a private network, one on plain `http` behind an `https` page, or Neo4j and Memgraph over Bolt, which
rides a raw TCP socket. For those there is an **optional server edition** — the same app, with a small
host that makes the connection on its behalf, shipped as a container. It adds no accounts and no
server-side storage, and one build serves both ways. See [the server edition](#the-server-edition-docker).

It's designed for developers who want a fast, private, self-hostable alternative to heavier graph desktop tools: paste a query, get a picture, poke at the data, and move on.

## Highlights

| | |
|---|---|
| 🔌 **Six engines, browser-direct** | Gremlin (TinkerPop, Cosmos DB), Cypher (Neo4j, Memgraph), AQL (ArangoDB), DQL (Dgraph), GUN and SPARQL/RDF — straight from the browser, no proxy in between. |
| 🐳 **Optional server edition** | One container for the databases a browser cannot reach: no CORS headers, a private network, or Bolt. Same app, still no accounts and no server-side storage. |
| 🎨 **Four view modes** | The same result as **JSON**, an interactive **2D** graph, a **3D** graph, or a sortable **Table**. |
| 🧭 **Multiple layouts** | 6 layouts in 2D (force, tree, concentric, circle, grid, random) and 6 in 3D (force + five DAG modes). |
| 🐞 **gdotV-style query debugger** | Step through a traversal and watch the traverser count after every step — see exactly where results vanish. |
| ✏️ **Full editing** | Add / edit / delete vertices, edges and properties. Changes are staged and committed explicitly. |
| 🧠 **Schema-aware autocomplete** | Monaco editor with real vertex labels, edge labels and property keys pulled from your live database. |
| ✨ **Ask in English** | Describe what you want and a model writes the query, against your live schema — bring your own key (Anthropic, OpenAI, Gemini or any OpenAI-compatible endpoint). |
| ✨ **Text to knowledge graph** | Paste text, a document or a Wikipedia article and get a graph out, previewed and merged before anything is committed. |
| 📥 **Import** | Paste GraphSON, **Graphviz DOT** or **Mermaid** and visualize it offline — or turn it into `addV`/`addE`. |
| 📤 **Export** | Table → CSV / colored Excel; graph → PNG / JPEG / SVG; 3D scene → OBJ / PLY / STL / glTF. |
| 🌙 **Dark mode + PWA** | Persisted dark theme, keyboard shortcuts, and installable/offline via a service worker. |

---

## Feature tour

### 🔌 Connect to any graph

- **Gremlin databases** — Apache TinkerPop / TinkerGraph and **Azure Cosmos DB** (Gremlin API).
- **Two transports** — **WebSocket** (`ws(s)://host:port/gremlin`, GraphSON 3 `op:eval`) and **HTTP/REST** (TinkerPop REST channel, or Cosmos DB `executeGremlin` with HMAC-SHA256 auth).
- **SPARQL / RDF endpoints** — Fuseki, Blazegraph, GraphDB, Virtuoso, and public endpoints like **Wikidata** and **DBpedia**. `SELECT` → results table, `ASK` → boolean, `CONSTRUCT`/`DESCRIBE` → a graph.
- **Database-type selector** on the connection form, with a **CORS/reachability warning** and a **supported-databases table** right in the UI.
- **SSL toggle** (`ws`↔`wss`, `http`↔`https`), auto-derived from the port and type, with a live protocol badge and a reminder to switch SSL off for a database without a secure endpoint (and that you may need to enable CORS or put a proxy in front).
- **Saved connections** — full add / edit / delete with duplicate-name validation, persisted to the browser's IndexedDB.
- **Connection status** indicator in the top bar, a connectivity test on connect, and a graceful close on disconnect.

### 📝 Write & run queries

<img src="docs/img/query-editor.png" alt="The Monaco query editor with a syntax-highlighted Gremlin query" width="460" />

- **Monaco query editor** (self-hosted — no npm/bundler) with syntax highlighting for **Gremlin** (custom Monarch grammar), **openCypher** and **SPARQL**, auto-indentation and bracket/quote auto-closing. Pick the language per tab.
- **Schema-aware Gremlin autocomplete** — step completion **plus** real vertex labels, edge labels and property keys fetched from the connected database. It's context-aware: `hasLabel('…` suggests vertex labels, `out('…` edge labels, `has('…` / `values('…` property keys. Refreshed on connect and after every commit.
- **Load DB** — pull the whole graph (vertices **and** edges, including isolated/edgeless vertices) with a configurable vertex limit.
- **Saved queries** — CRUD, stored locally, one click to reload.
- **Query history** — the last 20 executed queries, de-duplicated (re-running one moves it to the top), click to restore.
- **Examples tab** — curated queries grouped into **Inspect**, **Visualize** and **Mutate**, plus one-click **sample graphs** (a table-assembly tree, a social network, flight routes, and 3D objects) that load additively so you always have something to look at.
- **✨ Ask AI** — describe what you want in plain English and a **bring-your-own-key** AI model (Anthropic / OpenAI / Gemini / any OpenAI-compatible server) writes the Gremlin / openCypher / SPARQL query into the editor for review — grounded in the connected schema. It is a running conversation, not one shot. Tool-capable models can run queries against the live graph to check themselves, under a per-panel setting: **ask each time** (the default), **auto-run reads**, or **auto-run reads and writes**. A write is always something the model proposes — below the top setting it waits for you, on a card that also offers to hand the query to the editor instead.
- **Cancellable** — connect, Run Query, Load DB, Schema fetch and Commit all run under a cancellation token with a Cancel button.

### 🎨 Visualize four ways

Switch any result between **JSON**, **2D**, **3D** and **Table** instantly.

| 2D — Cytoscape.js | 3D — three.js |
|:---|:---|
| <img src="docs/img/graph-2d.svg" alt="Interactive 2D graph view" width="400"> | <img src="docs/img/graph-3d.png" alt="Interactive 3D graph view" width="400"> |
| **Table** | **Raw JSON** |
| <img src="docs/img/graph-table.png" alt="Vertices and edges table view" width="400"> | <img src="docs/img/graph-json.png" alt="Raw JSON results view" width="400"> |

- **2D — Cytoscape.js** — 6 layouts (Force-directed `cose`, Tree/breadthfirst, Concentric, Circle, Grid, Random), directed labeled edges, and node/edge selection highlighting.
- **3D — 3d-force-graph / three.js** — 6 layout modes (free Force, Tree top-down, Tree bottom-up, Left→right, Radial, Z-stack), auto-coloring by vertex-label group, directional arrows, curved parallel edges and hover labels. Cyclic graphs are tolerated.
- **Table view** — vertices and edges as row/column tables with one column per property; click a row to select the element. Vertex rows are tinted with their label's configured color.
- **Search & filter** — find nodes by label / id / property value (2D highlights + fits, 3D centers), plus a type-label filter to show/hide vertices by label, in both graph views.
- **Styling — per label or per node** — color, size, display-property, icon URL and a new **3D-model URL** (an `.obj`/`.zip` that replaces the node's sphere in 3D), set either per vertex label (saved in the browser) or per node (saved to the database as `gdbv*` properties that override the label style and commit like any edit — the per-node scope can also pin the node's X/Y/Z position). Color/size apply live.
- **Double-click to expand** — grow the graph outward from a node by loading its neighbors.
- **Center / fit** control and **live layout switching** with no graph rebuild.
- **Graph stats** — a `N vertices · M edges` line under each graph, plus the **last query's execution time** (`… · 12 ms query`).
- **Multiple query tabs** — each tab is an independent workspace (its own query, results, view mode, layout, search/filter, styling and selection). All open tabs are persisted and restored on reload; double-click a tab to rename it.

### 🐞 Debug traversals step by step

A **gdotV-style step-through debugger** for Gremlin:

<img src="docs/img/debugger.png" alt="The step debugger showing the traverser count after each step, with the final step's drop to zero highlighted in red" width="640" />

- Runs the query truncated at each step (prefix re-execution) and shows the **traverser count after every step**, with **zero-drops highlighted** so you can see exactly where results disappear.
- Steps are **underlined and clickable in the editor** — click one to sample it or **visualize that intermediate result** in 2D/3D. The editor goes read-only during a debug session.
- A **Profile** tab (parsed `profile()` metrics — count / traversers / time / %) and an **Explain** tab.
- **Mutation-safe**: `addV` / `addE` / `drop` / `property` / `merge` queries are refused, so debugging never changes your graph.

### ✏️ Edit the graph safely

- **Select** a node or edge (click it in the graph, or click a table row) to open the **property editor**.
- **Add / edit / remove** vertex and edge properties.
- **Add / remove edges** on the selected node, choosing direction (in / out).
- **Two-click edge creation** — activate, click source then target, give it a label.
- **Create a new vertex** by label; **delete** an element via button, the <kbd>Delete</kbd> key, or a **delete mode** (trash-icon toggle) where clicking any node or edge stages a drop for it — deleting a vertex also drops its incident edges so none are left dangling.
- Every mutation is staged into a **Generated queries** buffer and **committed explicitly** — with per-line error reporting and optional auto-reload after commit — so nothing changes until you say so.
- **Preview uncommitted edits** — a *Preview data changes* switch below the 2D/3D canvas: turn it on to see staged adds, removes and style/position changes on the canvas immediately (with an on-canvas warning) before you commit.

### 📥 Import & export

**Import / paste**
- Paste **GraphSON** (from the app's own *Copy graph*), **Graphviz DOT**, or **Mermaid** flowcharts and **visualize them offline** — no connection needed.
- DOT / Mermaid also **generate `addV` / `addE`** into the query editor, ready to import into a real database.
- **✨ Generate with AI** — paste text, load a file (**PDF**, `.docx`, `.xlsx`, `.csv`, `.txt`/`.md` or any text-based format), or **fetch a Wikipedia article**, and an AI model extracts a knowledge graph from it: strict-JSON output, validated and entity-de-duplicated client-side, previewed with counts and warnings, then staged as `addV` / `addE` — **merging into or replacing** the current drawing, your choice, with nothing committed until you say so.
- **Cloud file picker** *(currently hidden)* — the **Dropbox / OneDrive / Google Drive** file-picker button is temporarily disabled; the provider interop still ships in the codebase.

**Export**
- **Table** → CSV or a color-styled **Excel `.xlsx`** (hand-rolled OpenXML, no dependency — vertex rows filled with their label color).
- **Image** → **PNG**, **JPEG**, or **SVG** (SVG for the 2D view).
- **3D scene** → **OBJ**, **PLY**, **STL** or **glTF**.

### 🌙 Comfort features

- **Dark mode**, persisted, with tuned contrast.
- **Keyboard shortcut**: <kbd>Delete</kbd> removes the selected element.
- **Installable PWA** with a service worker for offline use.
- **Full-width toggle** in the top bar — drop the page's side margins to fill the viewport; the margins are sized in `vw` so they hold steady when you zoom, and the choice is remembered per browser.
- **About** modal (with links to the blog post and the GitHub repo) and a **resizable two-pane layout** (results/graph on the left, editor + property panel on the right) — drag the divider to change the split, or double-click it to reset to the default ratio.

---

## Supported databases

| Database | Query language | Protocol | Route |
|---|---|---|---|
| Apache TinkerPop / TinkerGraph | Gremlin | WebSocket or HTTP | Browser or server |
| Azure Cosmos DB (Gremlin API) | Gremlin | WebSocket / HTTP + HMAC-SHA256 | Browser or server |
| Neo4j / Memgraph | Cypher | Bolt | Browser (vendored JS driver) or server (.NET driver) |
| ArangoDB | AQL | HTTP cursor API | Browser or server |
| Dgraph | DQL | HTTP | Browser or server |
| GUN | — (form-based) | peer-to-peer | Browser only — the page *is* a peer |
| Fuseki, Blazegraph, GraphDB, Virtuoso | SPARQL 1.1 | HTTP | Browser or server |
| Public SPARQL (Wikidata, DBpedia) | SPARQL | HTTPS | Browser or server |

Not every engine can do everything, and where one cannot it is a reason rather than an unfinished job:
GUN cannot enumerate itself, so it has no browse; SPARQL has no query builder, so every
compose-a-query-for-you feature is off for it.

**Amazon Neptune** stays out of scope: it needs VPC access plus SigV4 request signing, which is more than
a proxy.

> **Reachability.** On the Browser route the endpoint must be reachable from your machine *and* either
> allow CORS (HTTP/SPARQL) or accept a WebSocket from your origin; the app shows a warning to remind you.
> The [server edition](#the-server-edition-docker) lifts that — it dials from the host instead, which is
> the whole reason it exists.

---

## Running locally

**Prerequisites:**

- the [.NET 10 SDK](https://dotnet.microsoft.com/download) — all you need to run the app
- [Node.js](https://nodejs.org) 20+ — only for the JS unit tests and the Playwright e2e tests (see [Testing](#testing))
- Docker — only if you want a local Gremlin database to play with (optional, below)

**Clone and run:**

```bash
git clone https://github.com/EECSB/GraphDBViewerWeb.git
cd GraphDBViewerWeb
dotnet run --project GraphDBViewerWeb
```

> **Windows note:** clone into a reasonably short path (e.g. `C:\src`) or run `git config --global core.longpaths true` first — the repo contains deeply nested files that can exceed the default 260-character path limit.

Then open the URL it prints (dev server is configured for **http://localhost:5154**). No database is required to try it — open the **Examples** tab and load one of the sample graphs, or paste a Mermaid/DOT snippet into the *Visualize pasted graph* box.

To point it at your own database, expand the connection card in the top bar, pick the database type, fill in host/port (and auth for Cosmos DB / SPARQL), and hit **Connect**.

### Optional: a local Gremlin database to play with

```bash
docker run -d -p 8182:8182 tinkerpop/gremlin-server
```

That serves an **in-memory TinkerGraph** at `ws://localhost:8182/gremlin` — no SSL, and the data is lost when the container restarts. Seed it with a small product-composition sample by editing `$endpoint` at the top of [`gremlin-load-sample.ps1`](gremlin-load-sample.ps1) to your server and running the script with PowerShell 7. Then connect in the app with: type **Apache TinkerPop**, transport **WebSocket**, **SSL off**, host `localhost`, port `8182`.

No Docker handy? Pick the **SPARQL / RDF** database type and the public **Wikidata** endpoint `https://query.wikidata.org/sparql` — zero setup — and try `SELECT * WHERE { ?s ?p ?o } LIMIT 10`.

## Deploying

It's a **static Blazor WebAssembly** app, so it deploys anywhere that serves static files — GitHub Pages, Azure Static Web Apps, Netlify, S3, Nginx, etc.

Every tagged release attaches a ready-made **zip of the built app** — download it from
[Releases](https://github.com/EECSB/GraphDBViewerWeb/releases), unpack it into your web root, and that is
the deployment. It unpacks as the site root, so `index.html` lands where you point the host. Every CI run
attaches the same zip as a build artifact if you want the tip rather than a release.

To build it yourself:

```bash
cd GraphDBViewerWeb
dotnet publish -c Release
# output: bin/Release/net10.0/publish/wwwroot
```

Serve the contents of `publish/wwwroot`. To host the **presentation page** on GitHub Pages, point Pages at the repo's `docs/` folder — [`docs/index.html`](docs/index.html) is self-contained.

### The server edition (Docker)

[![Docker image version](https://img.shields.io/docker/v/eecsb/graphdbviewer-server?label=docker&sort=semver)](https://hub.docker.com/r/eecsb/graphdbviewer-server)

**Why there is a server version at all.** The viewer talks to your database straight from the browser,
and for most databases that is the whole story — it is why this app needs no backend. But there are four
things a browser will not do, and no amount of client-side code changes them:

| | |
|---|---|
| **No CORS headers** | A browser discards a cross-origin response the database did not explicitly permit. Most database servers send no such headers, and plenty cannot be configured to. |
| **Mixed content** | A page served over `https` may not open a plain-`http` connection. A database without TLS is unreachable from any hosted page. |
| **Private addresses** | A database on `10.x`, or behind a VPN, is reachable from your server and not from a visitor's browser. |
| **Bolt** | Neo4j and Memgraph speak a protocol that rides a raw TCP socket. No browser will open one. |

So the server edition is a small ASP.NET Core host that serves the very same app and adds one endpoint,
which runs the query on the server's behalf. Connections then carry a **Server / Browser** choice, per
connection — you pick the route only where you need it.

It is still not a backend in the usual sense. **No accounts, no database, no server-side storage:** your
connections, queries and workspace stay in your browser exactly as they do without it.

```bash
docker run --rm -p 8080:8080 eecsb/graphdbviewer-server
```

Then open <http://localhost:8080>. Or with Compose:

```yaml
services:
  graphdbviewer:
    image: eecsb/graphdbviewer-server
    ports:
      - "8080:8080"
    restart: unless-stopped
```

Images are published for **amd64 and arm64**, so a Raspberry Pi or an Apple-silicon Mac runs the same tag.

**One build serves both editions.** The app asks its host on startup whether it proxies connections, so
the identical output works either way — put it on a static host and the Server option simply never
appears. Nothing to configure, and no separate download.

> [!WARNING]
> **Keep it on a network you trust.** The proxy endpoint takes no authentication and will dial whatever
> it is asked to — that is the point of a developer tool, and it means anything able to reach the
> container can make it open connections on its behalf, including to addresses only it can route to. On
> the Server route a connection's credentials travel to the host rather than staying in your browser.
> Put TLS and access control in front of it before it goes anywhere public.

## Embedding in another page

Drop the viewer into any web page with an `<iframe>`. Pass settings in the URL's query string and it comes up **pre-configured** — connected, with a query already run, in the view you want:

```html
<iframe
  src="https://your-host/?dbType=tinkerpop&host=192.168.1.5&port=8182&ssl=false&query=g.V().limit(25)&view=2d"
  width="100%" height="720" style="border: 0;">
</iframe>
```

A SPARQL example (endpoint-only is inferred as SPARQL):

```html
<iframe
  src="https://your-host/?endpoint=https://query.wikidata.org/sparql&view=table&query=SELECT%20%2A%20WHERE%20%7B%20%3Fs%20%3Fp%20%3Fo%20%7D%20LIMIT%2010"
  width="100%" height="720" style="border: 0;">
</iframe>
```

> **Tip:** URL-encode the `query` value (e.g. `encodeURIComponent(...)`), especially when it contains spaces, quotes or `&`.

**Supported query-string parameters** (all optional, case-insensitive):

| Group | Parameter | Values / notes |
|---|---|---|
| Connection | `dbType` | `tinkerpop` · `cosmos` · `sparql` (endpoint-only is inferred as SPARQL) |
| | `transport` | `ws` (WebSocket) · `http` |
| | `host`, `port` | Gremlin host and port. A non-TLS port (e.g. `8182`) implies `ssl=false`. |
| | `ssl` | `true` / `false` — defaults from the port when omitted |
| | `database`, `collection` | Azure Cosmos DB only |
| | `authKey` | Cosmos key / password, or SPARQL basic-auth password |
| | `endpoint` | SPARQL endpoint URL |
| | `username` | SPARQL basic-auth username |
| Query | `query` (or `q`) | Initial query text |
| | `lang` | `gremlin` · `cypher` · `sparql` (editor highlighting) |
| | `run` | `true` (default) / `false` — auto-run the query once connected |
| View | `view` | `json` · `2d` · `3d` · `table` |
| Control | `connect` | `true` (default) / `false` — auto-connect using the details above |

The viewer connects **directly from the browser**, so the [same reachability/CORS rules](#supported-databases) apply to the embedded frame. Because `authKey` would appear in the URL, avoid putting production credentials in an embed on a shared or public page.

## How it works

- **Blazor WebAssembly** — no `Gremlin.Net` dependency; the drivers are built on `System.Net.Http` and
  `System.Text.Json` so they run in WASM. The optional server edition reuses those same drivers on the
  host, which is why the viewer cannot tell the two routes apart.
- **Transports:** raw WebSocket with GraphSON 3 framing, and HTTP REST (with Cosmos DB HMAC-SHA256 auth).
- **Persistence:** IndexedDB (far more room than localStorage, compressed, with a one-time migration of
  anything already saved) for connections, saved queries, history, last query, open tabs and theme.
- **Rendering:** Cytoscape.js (2D) and 3d-force-graph / three.js (3D), driven through thin JS interop layers. The Monaco editor is vendored in the `GraphDBViewer.Core` class library and served at
  `_content/GraphDBViewer.Core/lib/monaco` (no CDN, no bundler).

Key source files — all of them in the `GraphDBViewer.Core` class library:

| File | Responsibility |
|---|---|
| `Code/Db/GraphDb.cs` / `Code/Db/GraphDbProviders.cs` | The database seam: `IGraphDb` + the normalized `GraphDbResult`, and the per-database capabilities the UI gates on. |
| `Code/Gremlin/GremlinDB.cs` | An `IGraphDb`: connection + query execution over WS/HTTP; `GremlinConnection`. |
| `Code/Sparql/SparqlDb.cs` / `Code/Sparql/SparqlConverter.cs` | An `IGraphDb`: SPARQL HTTP query + results → table/graph. |
| `Code/Graph/GraphDataConverter.cs` | GraphSON → Cytoscape (2D) / 3d-force-graph (3D) / Table models. |
| `Code/Gremlin/GremlinQueries.cs` | Pure Gremlin query-string builder, traversal step-parser (for the debugger) and curated example queries. |
| `Code/Graph/GraphImport.cs` | DOT / Mermaid → node/edge model → render JSON + `addV`/`addE`. |
| `Code/Utils/ExcelExport.cs` | Hand-rolled colored `.xlsx` export. |
| `Code/Gremlin/SchemaBuilder.cs` | Builds the schema used for autocomplete. |
| `Pages/Home/Home.razor` | The single-page UI. |
| `wwwroot/js/*Interop.js` | Cytoscape, 3D force-graph, Monaco, export, keyboard, URL-availability and cloud-picker interop. |

## Project layout

```
GraphDBViewer.Core/          # the viewer, as a Razor class library — this is where the app lives
  Code/                      # C# core: Db/ (a folder per engine) · Graph/ · Llm/ · Models/
                             #          Storage/ · Utils/ · Interop/
  Components/                # Razor UI components (TopBar, TableView, MonacoEditor, …)
  Layout/ · Pages/Home/      # the layout, and the single page
  wwwroot/                   # JS interop, styles, icons, vendored libs (Cytoscape, three, Monaco)
                             # served to the host at _content/GraphDBViewer.Core/
GraphDBViewerWeb/            # the Blazor WebAssembly host — a composition root, and little else
  Program.cs                 # service registration, and what this edition tells the viewer it is
  App.razor                  # the router
  wwwroot/index.html         # the page shell, favicon, PWA manifest + service workers, showcase/
GraphDBViewerWeb.Server/     # the optional backend edition: serves the app + the query proxy
  Api/ · Db/                 # the proxy endpoint, its connection pool, the host-side Bolt driver
Dockerfile                   # builds that host into a container
GraphDBViewerWeb.Tests/      # xUnit tests for the pure C# logic + bUnit markup tests
docs/index.html             # this project's presentation / landing page
README.md
```

## Testing

The pure C# logic (query builder, GraphSON conversion, DOT/Mermaid import, SPARQL conversion, Excel export, step parsing, expansion, cancellation) is covered by xUnit tests, and the Razor markup layer — the option values and form shapes the C# reads back — by **bUnit** component tests in the same project:

```bash
dotnet test
```

The pure JS geometry helpers run under the built-in Node test runner:

```bash
npm test
```

The DOM / three.js / Cytoscape-bound rendering layer is covered by Playwright e2e tests in [`e2e/`](e2e/). The Playwright config starts the app itself (`dotnet run` on port 5000), and the specs load their fixture graph offline through the DOT import — no database needed:

```bash
npm install                        # once — pulls @playwright/test
npx playwright install chromium    # once — the test browser
npm run test:e2e
```

The double-click-expansion spec is the one test that needs a live, seeded Gremlin server; it is skipped unless `GREMLIN_E2E_HOST` (and optionally `GREMLIN_E2E_PORT`) points at one (seed the dev server with `gremlin-load-sample.ps1`).

## Privacy & limitations

- **Everything stays local.** Queries go straight from your browser to your database; connection details
  and history are stored only in your browser, in IndexedDB. Nothing is sent anywhere else.
- Auth keys are stored **in plain text** — appropriate for a single-user local/developer tool, but do not
  use it on a shared machine with production credentials.
- **On the Server route, that changes.** A connection proxied through the server edition sends its
  credentials to that host, and the host opens the connection instead of your browser. That is the trade the
  route exists to make. It is the default in that edition, and switchable per connection: set a
  connection to Browser and it behaves exactly as it does without a host.
- Some databases cannot be reached from a browser at all — no CORS headers, a private network, plain
  `http` under `https`, or Bolt. The [server edition](#the-server-edition-docker) is the answer to those.
  A few need more than a proxy (Amazon Neptune wants VPC access plus SigV4 signing) and remain out of
  scope.

## License

Graph DB Viewer is **dual-licensed**:

- **Noncommercial use is free** under the [PolyForm Noncommercial License 1.0.0](LICENSE) — personal and hobby projects, research, education, and nonprofits.
- **Commercial use requires a paid license** — see [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md).

Bundled third-party libraries keep their own permissive (MIT) licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---