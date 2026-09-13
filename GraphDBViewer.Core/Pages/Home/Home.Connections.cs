using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;
using System.Net.Http.Json;

namespace GraphDBViewerWeb.Pages;

//Connection management: the active connection and its lifecycle (connect / disconnect), plus the
//saved-connections list and its add / edit / delete form.
public partial class Home
{

    private const string LocalStorageKey = "graphdbviewer:connections";

    #region Active connection state

    //Database provider — chosen per connection. Apache TinkerPop / Cosmos DB speak Gremlin; SPARQL is a
    //plain HTTP RDF endpoint. Database/Collection inputs apply only to Cosmos DB.
    private bool RequiresDatabaseCollection => connection.DatabaseType == "CosmosDb";
    private bool showSupportedDbs;

    //The current database's provider and what it can do. Both read off the connection rather than a live
    //client, because the UI gates on them before there is one — the Load DB row, for instance, renders
    //while disconnected so that clicking it can prompt you to connect.
    private GraphDbProvider Provider => GraphDbProviders.For(connection.DatabaseType);

    //Nearly always the engine's own answer. The exception is the instance's own graph, which is offered
    //here to be LOOKED at: the endpoint behind it refuses anything but a read, so the page should not
    //draw a Commit button whose only possible outcome is a refusal.
    private GraphDbCapabilities Caps
    {
        get
        {
            if (instanceGraph)
                return Provider.Capabilities.ReadOnly();
            else
                return Provider.Capabilities;
        }
    }

    //The query language the viewer writes in on this connection's behalf — Load DB, expand, staged edits.
    //Only ever reached through a capability that the provider turned on by supplying it, so the null a
    //builder-less engine carries here is unreachable rather than a hazard.
    //
    //An engine whose queries need the schema (ArangoDB, whose AQL cannot name a collection dynamically)
    //gets a rebuilt one as soon as the schema is read; until then, and for every other engine, the
    //provider's own instance stands.
    private IGraphQueryBuilder Qb => schemaQueryBuilder ?? Provider.QueryBuilder;

    private IGraphQueryBuilder schemaQueryBuilder;

    //True when this database is addressed by an endpoint URL rather than host/port, which is the one
    //thing that still changes the shape of the connection form.
    private bool UsesEndpointUrl => connection.DatabaseType == "Sparql";

    //Neo4j / Memgraph over Bolt. It is the one provider whose browser and server routes use different
    //drivers (the JavaScript driver in the browser, the .NET driver on the host), so the connect handler
    //and the connection form both special-case it.
    private bool IsNeo4j => connection.DatabaseType == GraphDbProviders.Neo4j;

    //ArangoDB over its HTTP cursor API. Plain HTTP, so one driver serves both routes.
    private bool IsArango => connection.DatabaseType == GraphDbProviders.Arango;

    //Dgraph over its HTTP API. Addressed by host and port with an optional access token, so it needs no
    //form of its own — the default host/port/auth-key one already says exactly that.
    private bool IsDgraph => connection.DatabaseType == GraphDbProviders.Dgraph;

    //GUN, which is addressed by relay-peer URLs rather than one host, and may legitimately have none.
    private bool IsGun => connection.DatabaseType == GraphDbProviders.Gun;

    //Databases addressed by host + port with a user and a single named database — Neo4j and ArangoDB share
    //one form, differing only in the hints below.
    private bool UsesUserDatabaseLogin => IsNeo4j || IsArango;

    //A stock Gremlin server with authentication on (JanusGraph, or TinkerPop's own) logs in with a username
    //and a password, so its form asks for both. Cosmos DB and Dgraph take a single key instead.
    private bool UsesGremlinLogin => connection.DatabaseType == GraphDbProviders.TinkerPop;

    private string DefaultUsernameHint
    {
        get
        {
            if (IsArango)
                return "root";

            return "neo4j";
        }
    }

    private string DefaultDatabaseHint
    {
        get
        {
            if (IsArango)
                return ArangoDb.DefaultDatabase;

            return "neo4j";
        }
    }

    //Host/port shown in the top bar — an endpoint-URL database parses them back out of the URL.
    private string DisplayHost => Provider.DisplayHost(connection);

    ///<summary>
    ///What the toolbar calls this connection when a host and a port are the wrong words for it. Null for
    ///every ordinary connection, which is addressed by exactly those two; the instance's own graph is
    ///addressed by neither, because the browser is never given it.
    ///</summary>
    private string ConnectionLabel
    {
        get
        {
            if (instanceGraph)
                return "this instance's graph";

            return null;
        }
    }
    private int DisplayPort => Provider.DisplayPort(connection);

    private GremlinDB.GremlinConnection connection = new("WebSocket", 443, true, string.Empty, string.Empty, string.Empty, string.Empty);

    //The live database, whichever kind it is. Null until Connect succeeds.
    private IGraphDb db;
    private bool isConnected;
    private bool isConnecting;
    private CancellationTokenSource connectCts;
    private string statusMessage;
    private string statusClass;

    #endregion

    #region Saved connections
    private Dictionary<string, GremlinDB.GremlinConnection> savedConnections = new();
    private string selectedConnectionKey;
    #endregion

    #region Add-connection form state
    //The name the fields below are saved under when the + button is clicked; the connection itself is the
    //active `connection`, so there's no separate form — the + just captures what's already entered.
    private string connectionName;
    private bool connectionNameError;
    private string connectionNameErrorMessage;
    #endregion


    #region Saved-connections persistence

    private async Task LoadConnectionsAsync()
    {
        var stored = await Storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>(LocalStorageKey);

        if (stored is { Count: > 0 })
        {
            savedConnections = stored;
            SelectConnection(savedConnections.Keys.First());
        }
    }

    private async Task PersistConnectionsAsync()
    {
        await Storage.SetAsync(LocalStorageKey, savedConnections);
    }

    #endregion


    #region Add / Delete

    //Saves the connection currently entered in the fields below to the saved-connections list under the
    //name box's value — adding a new one, or overwriting the existing one when the name already matches
    //(so selecting a connection, tweaking the fields and clicking + updates it). No separate form.
    private async Task AddCurrentConnectionAsync()
    {
        connectionNameError = false;

        var name = connectionName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            connectionNameError = true;
            connectionNameErrorMessage = "Connection name is required.";

            return;
        }

        savedConnections[name] = new GremlinDB.GremlinConnection(connection);
        await PersistConnectionsAsync();

        selectedConnectionKey = name;
        connectionName = name;

        statusMessage = $"Connection \"{name}\" saved.";
        statusClass = "text-success";
    }

    private async Task DeleteConnectionAsync()
    {
        if (string.IsNullOrEmpty(selectedConnectionKey))
            return;

        if (isConnected)
            Disconnect("Disconnected because the active connection was deleted.");

        savedConnections.Remove(selectedConnectionKey);
        selectedConnectionKey = null;

        await PersistConnectionsAsync();
    }

    #endregion


    #region The instance's own graph

    ///<summary>
    ///The key the saved-connection picker uses for the instance graph. The trailing space is what makes it
    ///safe: a saved name is trimmed before it becomes a key, so no saved connection can ever hold this one.
    ///It is not a saved connection and is never stored as one — the picker is only where somebody looks for what can be opened.
    ///</summary>
    private const string InstanceGraphKey = "instance-graph ";

    ///<summary>
    ///The platform's own graph, browsed rather than plugged into (PIM's question 3, the user's
    ///decision of 2026-09-04: it is what lets someone open the catalog as a graph, which is half the
    ///pitch).
    ///
    ///Offered only where all three hold: this host has the plugin platform, the account holds
    ///<c>instance.query</c>, and a graph is actually configured. The last is asked once on startup — the
    ///status endpoint carries no secret and no data — because an option that opens onto "none is
    ///configured" is worse than no option.
    ///</summary>
    private bool OffersInstanceGraph
    {
        get
        {
            if (!HostOptions.HasPlugins || !instanceGraphConfigured)
                return false;

            return Account != null && Account.Can(AccountInfo.InstanceQuery);
        }
    }

    private bool instanceGraphConfigured;

    ///<summary>Plugins taken down from the registry that are still installed. Empty on a viewer with no plugin host.</summary>
    private List<SeenTakedown> takedowns = new();

    ///<summary>
    ///Whether Connect has anything to do. Normally that is "the engine has what it needs and the route
    ///is available"; the instance graph's answer is always yes, because there is nothing to fill in and
    ///no route to choose — the host holds the connection and dials it itself.
    ///</summary>
    private bool MayConnect
    {
        get
        {
            if (instanceGraph)
                return true;

            return !routeUnavailable && Provider.IsConfigured(connection);
        }
    }

    //Whether the ACTIVE connection is it. Drives the capabilities, the driver, and the form being hidden
    //rather than shown empty: there is nothing about this connection for anyone to fill in.
    private bool instanceGraph;

    ///<summary>Asks once whether a graph is configured. Failure is "no", which is what the picker should show.</summary>
    private async Task LoadInstanceGraphOfferAsync()
    {
        if (!HostOptions.HasPlugins)
            return;

        try
        {
            var status = await new InstanceGraph(Http).StatusAsync();

            instanceGraphConfigured = status.Configured;
        }
        catch
        {
        }
    }

    ///<summary>
    ///Plugins withdrawn from the registry that are still installed here (plugin-store criterion 15).
    ///
    ///<strong>Repeated until the plugin is gone, and never dismissible.</strong> The Store tab is where
    ///a take-down is learned, and it is a tab nobody has a reason to revisit — seen, quite possibly, by
    ///somebody who is not the person who decides. A banner that could be closed would be closed once and
    ///never seen again, which for "this thing you are running was withdrawn, and we changed nothing on
    ///your behalf" is the wrong shape.
    ///
    ///The server crosses the seen list with what is actually installed, so uninstalling ends the
    ///reminder without anybody clearing anything.
    ///</summary>
    private async Task LoadTakedownsAsync()
    {
        if (!HostOptions.HasPlugins)
            return;

        try
        {
            takedowns = await Http.GetFromJsonAsync<List<SeenTakedown>>(PluginTakedowns.Path) ?? new();
        }
        catch
        {
            //A viewer with no such endpoint, or a request that failed: no reminder, and no error on a
            //page that has nothing to do with plugins.
        }
    }

    //Selecting it clears the connection form to the engine it is — TinkerPop, always: the designated
    //connection may only be one an instance graph can live on — so the provider, the query builder, the
    //editor language and the schema source are all the right ones without a special case anywhere.
    private void SelectInstanceGraph()
    {
        connection = new GremlinDB.GremlinConnection { DatabaseType = GraphDbProviders.TinkerPop };
        instanceGraph = true;
        selectedConnectionKey = InstanceGraphKey;
        connectionName = "This instance's graph";
    }

    #endregion


    #region Connect / disconnect

    private void OnConnectionSelected(ChangeEventArgs e)
    {
        if (isConnected)
            Disconnect("Disconnected due to connection change.");

        SelectConnection(e.Value?.ToString());
    }

    //Loads the saved connection with the given key into the active fields.
    private void SelectConnection(string key)
    {
        instanceGraph = false;

        if (string.IsNullOrEmpty(key))
        {
            selectedConnectionKey = null;
            connectionName = null;
            return;
        }

        if (key == InstanceGraphKey)
        {
            SelectInstanceGraph();
            return;
        }

        if (savedConnections.TryGetValue(key, out var saved))
        {
            connection = new GremlinDB.GremlinConnection(saved);
            selectedConnectionKey = key;
            connectionName = key;
        }
    }

    private void OnDatabaseChanged(ChangeEventArgs e)
    {
        if (isConnected)
            Disconnect("Disconnected due to database change.");

        connection.Database = e.Value?.ToString();
    }

    ///<summary>
    ///Switches the connection to a database type, filling in that engine's defaults without clobbering
    ///anything the user typed. The port comes from the provider: each handler used to carry a list of every
    ///*other* engine's port to tell a leftover default from a chosen one, which meant adding an engine
    ///silently broke the handlers that predated it — switching from ArangoDB to Neo4j kept dialling 8529.
    ///</summary>
    private void SelectDatabaseType(string databaseType)
    {
        var provider = GraphDbProviders.For(databaseType);

        connection.DatabaseType = databaseType;

        if (GraphDbProviders.IsUnchosenPort(connection.Port))
            connection.Port = provider.DefaultPort;
    }

    //Bolt also comes with a conventional user and database, which spares the usual four-field setup.
    private void SelectNeo4j()
    {
        SelectDatabaseType(GraphDbProviders.Neo4j);

        if (string.IsNullOrWhiteSpace(connection.Username))
            connection.Username = "neo4j";

        if (string.IsNullOrWhiteSpace(connection.Database))
            connection.Database = "neo4j";
    }

    //ArangoDB's are the "root" user and the "_system" database. The transport is pinned to HTTP because
    //that is all its API speaks — which is also what makes the address preview show http:// rather than a
    //WebSocket scheme.
    private void SelectArango()
    {
        SelectDatabaseType(GraphDbProviders.Arango);
        connection.Transport = "HTTP";

        if (string.IsNullOrWhiteSpace(connection.Username))
            connection.Username = "root";

        if (string.IsNullOrWhiteSpace(connection.Database))
            connection.Database = ArangoDb.DefaultDatabase;
    }

    //GUN has no host-side driver at all, so the route is pinned to the browser — the default (Server)
    //could only ever fail for it.
    private void SelectGun()
    {
        SelectDatabaseType(GraphDbProviders.Gun);
        connection.Transport = "HTTP";
        connection.ViaServer = false;
    }

    //Dgraph needs no user or database — an access token in Auth Key is the whole of its authentication —
    //and HTTP is the only API it exposes here.
    private void SelectDgraph()
    {
        SelectDatabaseType(GraphDbProviders.Dgraph);
        connection.Transport = "HTTP";
    }

    private async Task ToggleConnectionAsync()
    {
        if (isConnected)
        {
            Disconnect("Disconnected by user.");
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        isConnecting = true;
        statusMessage = null;

        //Any builder rebuilt for the previous connection's schema is stale now.
        schemaQueryBuilder = null;
        connectCts?.Dispose();
        connectCts = new CancellationTokenSource();

        //A static build has no host to proxy through, and ViaServer defaults to true — so a connection
        //saved on a hosted deployment, or a URL parameter asking for the server route, would otherwise
        //aim at something that is not there. What the host says it is wins over what the connection asks.
        //The same for an account the host would refuse: routing through it takes a permission, and the
        //browser route is the one that can still work.
        if (!HostOptions.HasServerRoute || !MayRouteThroughHost)
            connection.ViaServer = false;

        try
        {
            var provider = Provider;

            //Same database either way — only who opens the socket changes. The provider still decides
            //capabilities, the probe query and the failure hint, since none of that depends on the route.
            //CreateBrowser hands back the browser-direct driver: the JS-interop one for Bolt (which needs
            //this handler's IJSRuntime, the static provider factory has none), the shared HTTP one otherwise.
            //Three routes now, and the third names no connection at all: the instance graph's is the
            //administrator's, held on the host under a reserved key, and a browser is never given it.
            if (instanceGraph)
                db = new InstanceGraphDb(Http);
            else if (connection.ViaServer)
                db = new ServerProxyGraphDb(Http, connection);
            else
                db = provider.CreateBrowser(Http, JS, connection);

            //A trivial query surfaces reachability problems immediately (CORS being the #1 SPARQL gotcha).
            var test = await db.ExecuteAsync(provider.ProbeQuery, connectCts.Token);

            if (test.IsError)
            {
                statusMessage = $"Connection failed: {test.Error}{FailedHint(provider)}";
                statusClass = "text-danger";
                isConnected = false;
                await DisposeDbAsync();
            }
            else
            {
                statusMessage = provider.ConnectedMessage(connection);
                statusClass = "text-success";
                isConnected = true;
                offlineMode = false;//connecting and offline mode are mutually exclusive
                showConnectionCard = false;
                showImportExport = false;

                //Only when the database dictates one — otherwise the user's own choice stands.
                if (provider.EditorLanguage != null)
                    editorLanguage = provider.EditorLanguage;

                //Examples is hidden for an engine with no query language, so a tab left sitting on it would
                //show a panel with no way back to it.
                if (provider.NoQueryLanguage && queryEditorTab == 3)
                    queryEditorTab = 1;

                await LoadQueryAsync();

                //An engine read by form has no use for query text left behind by another one.
                if (provider.NoQueryLanguage)
                    NormalizeFormQueryText(provider.ProbeQuery);

                if (provider.Capabilities.BrowseGraph)
                    await RefreshSchemaVocabularyAsync();
            }
        }
        catch (OperationCanceledException)
        {
            statusMessage = "Connection canceled.";
            statusClass = "text-muted";
            isConnected = false;
            await DisposeDbAsync();
        }
        catch (Exception ex)
        {
            statusMessage = ex.Message;
            statusClass = "text-danger";
            isConnected = false;
            await DisposeDbAsync();
        }
        finally
        {
            isConnecting = false;
            connectCts?.Dispose();
            connectCts = null;
        }
    }

    ///<summary>
    ///True while the Server route is selected on a build that has no server. The route row reports it,
    ///because the choice is made there and the Connect button lives here.
    ///</summary>
    private bool routeUnavailable;

    ///<summary>
    ///Why the Connect button is grayed out, or null when it is not — a disabled button that explains
    ///nothing reads as a feature that does not work, which is how the empty SPARQL endpoint field looked.
    ///</summary>
    private string ConnectTitle
    {
        get
        {
            if (isConnected || isConnecting)
                return null;

            //Chosen a route this build does not have: there is nothing to connect to until it goes back.
            if (routeUnavailable)
                return "This build has no host to route through. Switch to Browser to connect.";

            if (Provider.IsConfigured(connection))
                return null;

            if (string.IsNullOrEmpty(Provider.MissingRequirement))
                return null;

            return $"Enter {Provider.MissingRequirement} first";
        }
    }

    ///<summary>
    ///Adds anything a development machine left in dev-secrets.json that is not saved already: the
    ///database connections and the AI models, so neither has to be retyped after storage is cleared.
    ///Absent everywhere else, and it only ever adds, so an edit made in the app is never overwritten.
    ///</summary>
    //Whether a template entry has been filled in. Which field carries the address depends on the engine:
    //most take a hostname, SPARQL and GUN take a URL, so either one standing in means someone meant it.
    private static bool IsFilledIn(GremlinDB.GremlinConnection connection)
    {
        if (connection == null)
            return false;

        return !string.IsNullOrWhiteSpace(connection.Hostname) || !string.IsNullOrWhiteSpace(connection.Endpoint);
    }

    private async Task SeedDevSecretsAsync()
    {
        var secrets = await DevSecrets.LoadAsync(Http, HostOptions.DevSecretsPath);

        if (secrets == null)
            return;

        var addedConnection = false;

        foreach (var entry in secrets.Connections ?? new())
        {
            if (savedConnections.ContainsKey(entry.Key))
                continue;

            //The file ships as a template listing every database type, so most of it is blank on any
            //given machine. A blank entry is a placeholder someone did not fill in, not a connection,
            //and seeding it would put a row that cannot connect in the list for each type unused.
            if (!IsFilledIn(entry.Value))
                continue;

            //A connection routes through the host by default, which is right where there is one and
            //impossible where there is not: in the web-only build it would seed a row whose Connect
            //button is disabled until the user notices the route and switches it. The file says nothing
            //about routing on purpose — it is shared by both editions — so the edition decides.
            if (!HostOptions.HasServerRoute)
                entry.Value.ViaServer = false;

            savedConnections[entry.Key] = entry.Value;
            addedConnection = true;
        }

        if (addedConnection)
        {
            await Storage.SetAsync(LocalStorageKey, savedConnections);

            if (string.IsNullOrEmpty(selectedConnectionKey))
                SelectConnection(savedConnections.Keys.First());
        }

        var models = await LlmConnections.LoadAsync();
        var addedModel = false;

        foreach (var entry in secrets.LlmConnections ?? new())
        {
            if (models.ContainsKey(entry.Key))
                continue;

            if (string.IsNullOrWhiteSpace(entry.Value?.ApiKey))
                continue;

            models[entry.Key] = entry.Value;
            addedModel = true;
        }

        if (addedModel)
            await LlmConnections.SaveAsync(models);
    }

    //Cancels an in-progress connection attempt.
    private void CancelConnect()
    {
        connectCts?.Cancel();
    }

    private void Disconnect(string reason = null)
    {
        //Nothing left to follow — and the driver is about to go with the subscription.
        liveUpdates = false;

        _ = DisposeDbAsync();//fire-and-forget; closes WebSocket gracefully
        isConnected = false;
        schemaQueryBuilder = null;//it described the database we just left
        queryResults = null;
        queryText = null;
        statusMessage = reason ?? "Disconnected.";
        statusClass = "text-muted";
        showConnectionCard = true;

        //Drop the schema autocomplete so it doesn't suggest the disconnected DB's labels/keys.
        _ = ClearSchemaAsync();
    }

    private async Task ClearSchemaAsync()
    {
        schemaVocab = null;//disconnected: the schema is unknown again, which isn't the same as empty

        try
        {
            await JS.InvokeVoidAsync("monacoInterop.setSchema", new SchemaVocabulary());
        }
        catch { }
    }

    private async Task DisposeDbAsync()
    {
        if (db is not null)
        {
            await db.DisposeAsync();
            db = null;
        }
    }

    //Trailing note on a failed connection, for databases whose usual failure has a known cause. Empty
    //for the rest, which end on the database's own message.
    private static string FailedHint(GraphDbProvider provider)
    {
        if (provider.ConnectFailedHint == null)
            return "";

        return $" {provider.ConnectFailedHint}.";
    }

    #endregion
}
