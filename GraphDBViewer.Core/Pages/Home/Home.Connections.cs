using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

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
    private GraphDbCapabilities Caps => Provider.Capabilities;

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
        if (string.IsNullOrEmpty(key))
        {
            selectedConnectionKey = null;
            connectionName = null;
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
        if (!HostOptions.HasServerRoute)
            connection.ViaServer = false;

        try
        {
            var provider = Provider;

            //Same database either way — only who opens the socket changes. The provider still decides
            //capabilities, the probe query and the failure hint, since none of that depends on the route.
            //CreateBrowser hands back the browser-direct driver: the JS-interop one for Bolt (which needs
            //this handler's IJSRuntime, the static provider factory has none), the shared HTTP one otherwise.
            if (connection.ViaServer)
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
