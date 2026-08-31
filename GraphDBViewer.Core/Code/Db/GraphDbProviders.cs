using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a database can do, beyond running a query. The UI asks these instead of asking which engine it
///is talking to, so a new provider turns features on as it grows them rather than the page learning its
///name. They describe the user-facing feature, not the dialect it happens to be built from today.
///</summary>
public sealed class GraphDbCapabilities
{
    ///<summary>Load DB, DB schema, the query limit — the whole graph can be enumerated and its schema reported.</summary>
    public bool BrowseGraph { get; init; }

    ///<summary>Expand-on-double-click and a selected element's in/out edge lists — traversal from a known id.</summary>
    public bool Traverse { get; init; }

    ///<summary>The Generated tab, Commit, Save positions, Database cleanup — edits can be staged as queries and run.</summary>
    public bool StageEdits { get; init; }

    ///<summary>Run each line — the editor's text can be split into independently executed statements.</summary>
    public bool MultiStatement { get; init; }

    ///<summary>The query debugger at all — its Profile and Explain tabs.</summary>
    public bool Debug { get; init; }

    ///<summary>
    ///The debugger's Steps tab: running the query truncated after each step to count what survives. Only
    ///meaningful where a query's prefixes are themselves valid queries — Gremlin's are, Cypher's and AQL's
    ///are not — so an engine can have <see cref="Debug"/> without this.
    ///</summary>
    public bool DebugSteps { get; init; }

    ///<summary>Ask AI may hand the model a read-only query tool bound to this database.</summary>
    public bool AiTools { get; init; }

    ///<summary>
    ///The Live toggle: this engine can go on answering after the answer arrives, pushing a peer's changes
    ///into the canvas. Only GUN can — see <see cref="ILiveGraphDb"/> — because only GUN is built around a
    ///subscription rather than a request.
    ///</summary>
    public bool LiveUpdates { get; init; }
}

///<summary>
///Everything the app needs to know about one kind of database before it connects: what it can do, how
///to build a client for it, and how to describe it. Keyed by <see cref="GremlinDB.GremlinConnection.DatabaseType"/>.
///</summary>
public sealed class GraphDbProvider
{
    public string Id { get; init; }
    public string DisplayName { get; init; }

    ///<summary>Monaco language to force on connect, or null to leave the user's own choice alone.</summary>
    public string EditorLanguage { get; init; }

    ///<summary>
    ///Monaco language for the staged-edit buffer, when it isn't the editor's. GUN is the case: its reads
    ///are a form and its writes are JavaScript, so the two boxes hold different things.
    ///</summary>
    public string StagedLanguage { get; init; }

    ///<summary>
    ///What this engine needs in place before a graph can be imported into it, or null when it needs
    ///nothing. Dgraph is the only one — see <see cref="IGraphImportPreparation"/>.
    ///</summary>
    public IGraphImportPreparation ImportPreparation { get; init; }

    ///<summary>A trivial query run on connect to prove the endpoint is reachable.</summary>
    public string ProbeQuery { get; init; }

    ///<summary>
    ///The port this engine listens on when nothing else says otherwise.
    ///
    ///It lives here so there is one answer to "where does Dgraph listen" rather than one per caller. Both
    ///the connection card and an embed URL read it, and neither has to know the others' numbers — which
    ///they used to, each engine's handler carrying a list of every other engine's port to recognize a
    ///leftover default by.
    ///</summary>
    public int DefaultPort { get; init; }

    public GraphDbCapabilities Capabilities { get; init; }

    ///<summary>
    ///Builds the queries the viewer runs on the user's behalf — Load DB, expand, the staged edits. Null for
    ///an engine that has no builder yet, which is exactly why its browse / traverse / stage-edits
    ///<see cref="GraphDbCapabilities"/> are off, so the UI never reaches for it.
    ///</summary>
    public IGraphQueryBuilder QueryBuilder { get; init; }

    ///<summary>Reads the database's schema — labels, keys and relationship triples. Null when unsupported.</summary>
    public IGraphSchemaSource SchemaSource { get; init; }

    ///<summary>Profiles and explains a query, and says whether it can be stepped. Null when Debug is off.</summary>
    public IGraphQueryDebugger Debugger { get; init; }

    ///<summary>
    ///Rebuilds the query builder once the connected database's schema is known, for an engine whose queries
    ///cannot be written without it. ArangoDB is the case: AQL will not take a collection named by an
    ///expression, so "load the database" has to list every collection literally. Null for the engines whose
    ///builder never varies — they keep the one <see cref="QueryBuilder"/> instance.
    ///</summary>
    public Func<SchemaVocabulary, IGraphQueryBuilder> QueryBuilderForSchema { get; init; }

    ///<summary>
    ///Builds the driver for the HTTP-transport engines — the same C# class in the browser and on the host
    ///(Gremlin, SPARQL). Bolt is the exception: it needs a different driver per route, so it leaves this
    ///and supplies <see cref="BrowserCreate"/> / <see cref="ServerCreate"/> instead. Prefer the
    ///<see cref="CreateBrowser"/> / <see cref="CreateServer"/> methods over calling this directly — they
    ///pick the right driver for the side they run on.
    ///</summary>
    public Func<HttpClient, GremlinDB.GremlinConnection, IGraphDb> Create { get; init; }

    ///<summary>The browser-direct driver when it isn't the HTTP one — a JS-interop driver needing the page's
    ///IJSRuntime (Bolt's Neo4jBrowserDb). Null means "use <see cref="Create"/>".</summary>
    public Func<IJSRuntime, GremlinDB.GremlinConnection, IGraphDb> BrowserCreate { get; init; }

    ///<summary>The host-side driver when it isn't the HTTP one — a driver whose assembly cannot ship to the
    ///browser (Bolt's Neo4jServerDb uses raw TCP sockets), so the host plugs it in at startup through
    ///<see cref="GraphDbProviders.RegisterServerDriver"/>. Null means "use <see cref="Create"/>".</summary>
    public Func<GremlinDB.GremlinConnection, IGraphDb> ServerCreate { get; internal set; }

    ///<summary>Host shown in the top bar — for an endpoint-URL database it's parsed back out of the URL.</summary>
    public Func<GremlinDB.GremlinConnection, string> DisplayHost { get; init; }
    public Func<GremlinDB.GremlinConnection, int> DisplayPort { get; init; }

    ///<summary>Whether the connection form holds enough to attempt a connection.</summary>
    public Func<GremlinDB.GremlinConnection, bool> IsConfigured { get; init; }

    ///<summary>
    ///What <see cref="IsConfigured"/> is still waiting for, phrased to finish "Enter …". It titles the
    ///Connect button while that button is disabled, which otherwise grays out saying nothing: the field
    ///is marked with an asterisk, but only if you thought to look for one. Null where a provider can
    ///always attempt a connection.
    ///</summary>
    public string MissingRequirement { get; init; }

    public Func<GremlinDB.GremlinConnection, string> ConnectedMessage { get; init; }

    ///<summary>Extra hint appended when a connection attempt fails, or null.</summary>
    public string ConnectFailedHint { get; init; }

    ///<summary>
    ///True when this engine can only be reached from the page, so the "Make requests from" choice does not
    ///apply. GUN is the case: it is a JavaScript library with no .NET counterpart, so unlike Bolt — which
    ///has a driver on each side — there is nothing for the host to run.
    ///</summary>
    public bool BrowserOnly { get; init; }

    ///<summary>
    ///True when the engine has no query language at all, so there is nothing for anyone to type. The
    ///editor is replaced by form controls, the language picker and Examples are hidden, and the Generated
    ///tab shows — read-only — the code the viewer will actually run. GUN is the case: it is a chained
    ///JavaScript API, and a text box implying otherwise would be inviting a query it cannot answer.
    ///</summary>
    public bool NoQueryLanguage { get; init; }

    ///<summary>The browser-direct driver: the JS-interop one when this engine has it, else the HTTP one.</summary>
    public IGraphDb CreateBrowser(HttpClient http, IJSRuntime js, GremlinDB.GremlinConnection connection)
    {
        if (BrowserCreate != null)
            return BrowserCreate(js, connection);

        return Create(http, connection);
    }

    ///<summary>The host-side driver: the server-only one the host registered when this engine has it, else the HTTP one.</summary>
    public IGraphDb CreateServer(HttpClient http, GremlinDB.GremlinConnection connection)
    {
        //A browser-only engine has nothing to run here. Saying so plainly beats the NullReferenceException
        //a missing Create would otherwise throw inside the proxy endpoint.
        if (BrowserOnly)
            throw new NotSupportedException($"{DisplayName} runs in the browser only. It has no host-side driver, so set \"Make requests from\" to Browser.");

        if (ServerCreate != null)
            return ServerCreate(connection);

        return Create(http, connection);
    }
}

///<summary>The databases the app knows how to talk to, looked up by a connection's DatabaseType.</summary>
public static class GraphDbProviders
{
    private static readonly GraphDbCapabilities GremlinCapabilities = new()
    {
        BrowseGraph = true,
        Traverse = true,
        StageEdits = true,
        MultiStatement = true,
        Debug = true,
        DebugSteps = true,
        AiTools = true
    };

    //Every Gremlin-backed feature is built from GremlinQueryBuilder strings, so a plain RDF endpoint has
    //none of them. Not a judgement about SPARQL — a Cypher provider will switch them on one at a time.
    private static readonly GraphDbCapabilities SparqlCapabilities = new();

    //Cypher has a query builder of its own, so the features that are really "the viewer composing a query"
    //work here too, and PROFILE / EXPLAIN give it a debugger natively. Two stay off:
    //  * DebugSteps — the Steps tab runs the query truncated after each step, which needs a language whose
    //    prefixes are valid queries. A Cypher clause prefix is not, so the plan stands in for it instead.
    //  * MultiStatement — "run each line" treats one line as one query, and Cypher is habitually written
    //    across several lines, so splitting a user's query on newlines would break far more than it helps.
    private static readonly GraphDbCapabilities Neo4jCapabilities = new()
    {
        BrowseGraph = true,
        Traverse = true,
        StageEdits = true,
        Debug = true,
        AiTools = true
    };

    //AQL has a query builder of its own, and a debugger: the optimizer explains a query without running it,
    //and the cursor API profiles one it runs. DebugSteps stays off — that tab runs truncated query prefixes,
    //which only means something in Gremlin — and MultiStatement because AQL, like Cypher, spans lines.
    private static readonly GraphDbCapabilities ArangoCapabilities = new()
    {
        BrowseGraph = true,
        Traverse = true,
        StageEdits = true,
        Debug = true,
        AiTools = true
    };

    public const string TinkerPop = "ApacheTinkerPop";
    public const string CosmosDb = "CosmosDb";
    public const string Sparql = "Sparql";
    public const string Neo4j = "Neo4j";
    public const string Arango = "ArangoDb";
    public const string Dgraph = "Dgraph";
    public const string Gun = "Gun";

    //GUN cannot enumerate its own graph: souls are reachable only by walking from a key you already know,
    //so there is no "load the database" to switch BrowseGraph on for. Everything else it can do, and
    //LiveUpdates it alone can do — GUN's own model is a subscription, not a request.
    private static readonly GraphDbCapabilities GunCapabilities = new()
    {
        Traverse = true,
        StageEdits = true,
        LiveUpdates = true
    };

    //DQL has a query builder of its own now, so browse, traverse and staged edits work — the last of them
    //by staging JSON mutations rather than query text, since that is what a Dgraph write is. Debug stays
    //off (a DQL block prefix is not a query, and Dgraph has no plan endpoint the viewer can read), and
    //MultiStatement because a DQL query is one braced block spanning lines.
    private static readonly GraphDbCapabilities DgraphCapabilities = new()
    {
        BrowseGraph = true,
        Traverse = true,
        StageEdits = true,
        AiTools = true
    };

    private static readonly GraphDbProvider TinkerPopProvider = new()
    {
        Id = TinkerPop,
        DefaultPort = 8182,
        DisplayName = "Apache TinkerPop (Gremlin)",
        EditorLanguage = null,//leave the editor on whatever the user picked
        ProbeQuery = GremlinQueryBuilder.TestConnection,
        Capabilities = GremlinCapabilities,
        QueryBuilder = GremlinQueryBuilderAdapter.Instance,
        SchemaSource = GremlinSchemaSource.Instance,
        Debugger = GremlinQueryDebugger.Instance,
        Create = (http, connection) => new GremlinDB(http, connection),
        DisplayHost = connection => connection.Hostname,
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => true,
        ConnectedMessage = connection => $"Connected OK via {connection.Transport}.",
        ConnectFailedHint = null
    };

    private static readonly GraphDbProvider CosmosDbProvider = new()
    {
        Id = CosmosDb,
        DefaultPort = 443,
        DisplayName = "Cosmos DB (Gremlin)",
        EditorLanguage = null,
        ProbeQuery = GremlinQueryBuilder.TestConnection,
        //Cosmos speaks Gremlin but has no OLAP, so it gets its own entry rather than aliasing TinkerPop —
        //that difference is expected to show up here as capabilities are split finer.
        Capabilities = GremlinCapabilities,
        QueryBuilder = GremlinQueryBuilderAdapter.Instance,
        SchemaSource = GremlinSchemaSource.Instance,
        Debugger = GremlinQueryDebugger.Instance,
        Create = (http, connection) => new GremlinDB(http, connection),
        DisplayHost = connection => connection.Hostname,
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => true,
        ConnectedMessage = connection => $"Connected OK via {connection.Transport}.",
        ConnectFailedHint = null
    };

    private static readonly GraphDbProvider SparqlProvider = new()
    {
        Id = Sparql,
        DefaultPort = 443,
        DisplayName = "SPARQL / RDF",
        EditorLanguage = "sparql",
        ProbeQuery = "SELECT * WHERE { ?s ?p ?o } LIMIT 1",
        Capabilities = SparqlCapabilities,
        Create = (http, connection) => new SparqlDb(http, connection),
        DisplayHost = EndpointHost,
        DisplayPort = EndpointPort,
        IsConfigured = connection => !string.IsNullOrWhiteSpace(connection.Endpoint),
        MissingRequirement = "the SPARQL endpoint URL",
        ConnectedMessage = connection => "Connected to SPARQL endpoint.",
        ConnectFailedHint = "(if this is a CORS error, the endpoint must allow this origin)"
    };

    //Neo4j and Memgraph both speak Cypher over Bolt. Unlike the Gremlin and SPARQL drivers — which are the
    //same C# either side — Bolt needs a different driver per route: the host runs the .NET driver via
    //ServerCreate (registered at startup, since Neo4j.Driver can't ship to WebAssembly), and the browser
    //runs a JavaScript driver via BrowserCreate. The shared HTTP-factory Create is left throwing, since
    //neither route goes through it.
    private static readonly GraphDbProvider Neo4jProvider = new()
    {
        Id = Neo4j,
        DefaultPort = 7687,
        DisplayName = "Neo4j / Memgraph (Cypher)",
        EditorLanguage = "cypher",
        ProbeQuery = "RETURN 1",
        Capabilities = Neo4jCapabilities,
        QueryBuilder = CypherQueryBuilder.Instance,
        SchemaSource = CypherSchemaSource.Instance,
        Debugger = CypherQueryDebugger.Instance,
        Create = (http, connection) => throw new NotSupportedException(
            "Bolt is built per route: CreateBrowser (the JS driver) or CreateServer (the host's .NET driver)."),
        BrowserCreate = (js, connection) => new Neo4jBrowserDb(js, connection),
        //ServerCreate is registered by the host at startup — Neo4j.Driver cannot ship to WebAssembly.
        DisplayHost = connection => connection.Hostname,
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => !string.IsNullOrWhiteSpace(connection.Hostname),
        MissingRequirement = "a hostname",
        ConnectedMessage = connection => "Connected to Neo4j.",
        ConnectFailedHint = "(check the Bolt port and credentials; the browser route also needs the database to accept Bolt over WebSocket)"
    };

    //ArangoDB speaks AQL over plain HTTP, so — unlike Bolt — one driver serves both routes and it needs
    //nothing beyond the shared Create.
    private static readonly GraphDbProvider ArangoProvider = new()
    {
        Id = Arango,
        DefaultPort = 8529,
        DisplayName = "ArangoDB (AQL)",
        EditorLanguage = "aql",
        ProbeQuery = "RETURN 1",
        Capabilities = ArangoCapabilities,
        //Edits work from a document's own id, so the schema-less builder handles them; browse and traverse
        //need the collection names, which arrive with the schema.
        QueryBuilder = AqlQueryBuilder.Empty,
        SchemaSource = AqlSchemaSource.Instance,
        Debugger = AqlQueryDebugger.Instance,
        QueryBuilderForSchema = schema => new AqlQueryBuilder(schema.VertexLabels, schema.EdgeLabels),
        Create = (http, connection) => new ArangoDb(http, connection),
        DisplayHost = connection => connection.Hostname,
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => !string.IsNullOrWhiteSpace(connection.Hostname),
        MissingRequirement = "a hostname",
        ConnectedMessage = connection => "Connected to ArangoDB.",
        ConnectFailedHint = "(check the port, database and credentials; browser-direct also needs the server to allow this origin)"
    };

    //Dgraph speaks DQL over plain HTTP, so — like ArangoDB — one driver serves both routes.
    private static readonly GraphDbProvider DgraphProvider = new()
    {
        Id = Dgraph,
        DefaultPort = 8080,
        DisplayName = "Dgraph (DQL)",
        EditorLanguage = "dql",
        //Dgraph has no "return 1": the cheapest real query is asking the schema for itself.
        ProbeQuery = "schema {}",
        Capabilities = DgraphCapabilities,
        //Edits name the node by its uid, so they need no schema; browse and traverse have to spell out the
        //predicates to ask for, which arrive with it.
        QueryBuilder = DqlQueryBuilder.Empty,
        SchemaSource = DgraphSchemaSource.Instance,
        QueryBuilderForSchema = schema => new DqlQueryBuilder(schema.PropertyKeys, schema.EdgeLabels),
        StagedLanguage = "jsonview",//a Dgraph write is a JSON mutation, not more DQL
        ImportPreparation = DgraphImportPreparation.Instance,
        Create = (http, connection) => new DgraphDb(http, connection),
        DisplayHost = connection => connection.Hostname,
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => !string.IsNullOrWhiteSpace(connection.Hostname),
        MissingRequirement = "a hostname",
        ConnectedMessage = connection => "Connected to Dgraph.",
        ConnectFailedHint = "(check the Alpha HTTP port: 8080 by default; an ACL or Cloud cluster also needs its access token in Auth Key)"
    };

    //GUN is decentralized and browser-resident: the page IS a peer, syncing with whatever relays it is
    //given. That makes it the only engine with no host-side option at all.
    private static readonly GraphDbProvider GunProvider = new()
    {
        Id = Gun,
        DefaultPort = 8765,
        DisplayName = "GUN (peer-to-peer)",
        EditorLanguage = null,//a path, not a language — nothing to highlight
        StagedLanguage = "gunjs",//but a staged edit is real GUN JavaScript
        //GUN answers nothing for a key that does not exist, so a probe can only prove the page is wired
        //up, not that a peer replied. Reading the key the example query uses keeps the two consistent.
        ProbeQuery = GunDb.ExampleQuery,
        Capabilities = GunCapabilities,
        BrowserOnly = true,
        NoQueryLanguage = true,
        QueryBuilder = GunQueryBuilder.Instance,
        Create = (http, connection) => throw new NotSupportedException(
            "GUN runs in the browser only. It is built by the connect handler, which has the IJSRuntime it needs."),
        BrowserCreate = (js, connection) => new GunDb(js, connection),
        DisplayHost = connection => GunDisplayHost(connection),
        DisplayPort = connection => connection.Port,
        IsConfigured = connection => true,//no peer at all is valid: that is a purely in-browser graph
        ConnectedMessage = connection => GunConnectedMessage(connection),
        ConnectFailedHint = "(a GUN peer URL usually ends in /gun; with no peer the graph is in-browser only)"
    };

    private static string GunDisplayHost(GremlinDB.GremlinConnection connection)
    {
        var peers = GunDb.PeerUrls(connection);

        if (peers.Count == 0)
            return "in-browser";

        if (Uri.TryCreate(peers[0], UriKind.Absolute, out var uri))
            return uri.Host;

        return peers[0];
    }

    private static string GunConnectedMessage(GremlinDB.GremlinConnection connection)
    {
        if (GunDb.PeerUrls(connection).Count == 0)
            return "Ready: no peer, so this graph lives in the browser only.";

        return "Connected to GUN. It syncs in the background, so data may arrive after a query.";
    }

    private static readonly Dictionary<string, GraphDbProvider> ById = new(StringComparer.OrdinalIgnoreCase)
    {
        [TinkerPop] = TinkerPopProvider,
        [CosmosDb] = CosmosDbProvider,
        [Sparql] = SparqlProvider,
        [Neo4j] = Neo4jProvider,
        [Arango] = ArangoProvider,
        [Dgraph] = DgraphProvider,
        [Gun] = GunProvider
    };

    public static IEnumerable<GraphDbProvider> All => ById.Values;

    ///<summary>
    ///The provider for a connection's DatabaseType. An unknown or missing type falls back to TinkerPop,
    ///matching the long-standing behavior that anything which isn't SPARQL takes the Gremlin path — an
    ///embed URL can carry a database type this build has never heard of.
    ///</summary>
    public static GraphDbProvider For(string databaseType)
    {
        if (databaseType != null && ById.TryGetValue(databaseType, out var provider))
            return provider;

        return TinkerPopProvider;
    }

    ///<summary>
    ///True when a port is one no one chose — unset, or some engine's default left behind by the database
    ///type it was picked for. Switching engines replaces one of those and keeps anything else, so a port
    ///typed by hand survives and a stale 7687 does not follow you to ArangoDB.
    ///</summary>
    public static bool IsUnchosenPort(int port)
    {
        if (port <= 0)
            return true;

        foreach (var provider in All)
            if (provider.DefaultPort == port)
                return true;

        return false;
    }

    ///<summary>
    ///Lets the host plug in a server-side driver for an engine whose assembly cannot ship to the browser —
    ///Bolt's .NET driver, which opens a raw TCP socket. Called once at host startup; a host that never
    ///calls it (and the client) simply leaves that engine to its browser-direct driver and the proxy.
    ///</summary>
    public static void RegisterServerDriver(string databaseType, Func<GremlinDB.GremlinConnection, IGraphDb> factory)
    {
        For(databaseType).ServerCreate = factory;
    }

    private static string EndpointHost(GremlinDB.GremlinConnection connection)
    {
        if (Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri))
            return uri.Host;

        return connection.Endpoint;
    }

    private static int EndpointPort(GremlinDB.GremlinConnection connection)
    {
        if (Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri))
            return uri.Port;

        return 0;
    }
}
