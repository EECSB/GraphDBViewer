# Builds the backend edition of Graph DB Viewer: an ASP.NET Core host that serves the same WebAssembly
# app the static build serves, plus the query proxy. Build from the repo root:
#
#   docker build -t graphdbviewer .
#   docker run --rm -p 8080:8080 graphdbviewer      # then open http://localhost:8080
#
# ---- What the host is for ----
#
# The viewer talks to databases from the browser, and for most of them that is enough. This host exists
# for the ones where it is not: an endpoint that sends no CORS headers, a plain-http endpoint on an https
# page, a database only the server can route to, and Bolt, which needs a raw TCP socket the browser
# cannot open. Connections carry a "Make requests from" toggle, and the Server side of it lands here.
#
# The app detects the host by asking for api/graph/capabilities on startup, so the same build works
# either way -- served as static files it gets a 404, hides the toggle, and stays browser-direct.
#
# ---- What it is not ----
#
# There are no accounts, no database and no server-side storage here: the workspace stays in the
# browser's IndexedDB exactly as it does without a host. The full-stack platform, with accounts and
# saved workspaces that follow a user between machines, is a separate product (Treeality).
#
# ---- Two things to know before exposing it ----
#
# **The proxy endpoint is unauthenticated and dials whatever it is asked to.** That is the feature -- it
# is a developer tool, and pointing it at an arbitrary endpoint is the point -- but it means anything
# that can reach this container can make it open connections on its behalf, including to addresses only
# it can route to. Do not put this on the open internet.
#
# **Credentials travel to the host.** On the Server route a connection's key is sent here rather than
# staying in the browser. TLS is expected to terminate at a reverse proxy in front of the container; the
# app serves plain HTTP on 8080.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The host, the client it serves, and the core they share. Tests and tooling stay out.
COPY GraphDBViewer.Core/ GraphDBViewer.Core/
COPY GraphDBViewerWeb/ GraphDBViewerWeb/
COPY GraphDBViewerWeb.Server/ GraphDBViewerWeb.Server/

# WasmBuildNative=false skips the Emscripten native relink of the WASM runtime, so the image needs no
# wasm-tools workload and builds fast and reliably. The client payload is a little larger; if a smaller
# download matters more, install the wasm-tools workload in this stage and drop the flag.
RUN dotnet publish GraphDBViewerWeb.Server/GraphDBViewerWeb.Server.csproj -c Release -p:WasmBuildNative=false -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# The .NET 8+ aspnet image already defaults Kestrel to http://+:8080.
EXPOSE 8080
ENTRYPOINT ["dotnet", "GraphDBViewerWeb.Server.dll"]
