# Graph DB Viewer — server edition

A browser-based viewer and editor for graph databases: **Gremlin** (Apache TinkerPop, Azure Cosmos DB),
**Cypher** (Neo4j, Memgraph), **AQL** (ArangoDB), **DQL** (Dgraph), **GUN** and **SPARQL/RDF**. Query them,
see the result as a graph in 2D or 3D, edit nodes and edges on the canvas, and commit the changes back.

This image is the app **plus a host**. The viewer normally talks to a database straight from the browser
and needs no server at all — the [GitHub releases](https://github.com/EECSB/GraphDBViewerWeb/releases)
ship a zip of static files for exactly that. What the host adds is a query proxy for the databases a
browser cannot reach on its own:

- an endpoint that sends no CORS headers
- a plain `http` endpoint on an `https` page
- a database only the server can route to
- **Neo4j and Memgraph over Bolt**, which needs a raw TCP socket the browser cannot open

The app asks the host for `api/graph/capabilities` when it starts, so one build works either way. Served as
static files it gets a 404, hides the routing toggle, and stays browser-direct.

## Run it

```
docker run --rm -p 8080:8080 eecsb/graphdbviewer-server
```

Then open <http://localhost:8080>. Multi-architecture: `linux/amd64` and `linux/arm64`.

Each connection carries a **Make requests from** toggle — *Browser* or *Server* — so the proxy is used per
connection, only where it is needed.

## Read this before exposing it

**The proxy endpoint is unauthenticated and dials whatever it is asked to.** That is the feature — it is a
developer tool, and pointing it at an arbitrary endpoint is the point — but it means anything that can
reach this container can make it open connections on its behalf, including to addresses only it can route
to. **Do not put this on the open internet.**

**Credentials travel to the host.** On the Server route a connection's key is sent here rather than staying
in the browser. TLS is expected to terminate at a reverse proxy in front of the container; the app serves
plain HTTP on 8080.

## What it is not

There are no accounts, no database and no server-side storage. Your workspace stays in the browser's
IndexedDB exactly as it does without a host, so the container holds nothing and can be replaced freely.

A full-stack platform with accounts and saved workspaces that follow a user between machines is a separate
product (Treeality).

## Tags

- `latest` — the newest release
- `1`, `1.2`, `1.2.3` — semantic version tags
- `sha-<commit>` — a specific build

## License

Source-available under the [PolyForm Noncommercial License 1.0.0](https://github.com/EECSB/GraphDBViewerWeb/blob/main/LICENSE).
Noncommercial use — personal and hobby projects, research, education, nonprofits — is free. Commercial use
requires a paid license; see
[COMMERCIAL-LICENSE.md](https://github.com/EECSB/GraphDBViewerWeb/blob/main/COMMERCIAL-LICENSE.md).

Source and documentation: <https://github.com/EECSB/GraphDBViewerWeb>
