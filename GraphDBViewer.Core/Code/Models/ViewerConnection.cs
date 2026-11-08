namespace GraphDBViewerWeb.Code;

///<summary>
///One of the viewer's connections and its own query tabs (the multiple-connections spec, 2026-11-03): the form, the live
///database, its state, and the tabs that run against it. The viewer holds a list of these with one in front, and the page
///surfaces the one in front's members as its own fields (Home.State.cs), as it surfaces the query tab in front.
///</summary>
public class ViewerConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    //The form: what Connect dials.
    public GremlinDB.GremlinConnection Connection { get; set; } = EmptyForm();

    //The live database, whichever kind it is. Null until Connect succeeds.
    public IGraphDb Db { get; set; }

    //The tab of this connection whose run is under way, or null: one run at a time on a connection, since its tabs share
    //one live link; another connection's tabs run beside it (the multiple-connections spec, story 4).
    public QueryTab RunningTab { get; set; }
    public bool IsConnected { get; set; }
    public bool IsConnecting { get; set; }
    public CancellationTokenSource ConnectCts { get; set; }
    public string StatusMessage { get; set; }
    public string StatusClass { get; set; }

    //Drawing without a database.
    public bool OfflineMode { get; set; }

    //Whether this connection is the instance's own graph.
    public bool InstanceGraph { get; set; }

    //True while the Server route is selected on a build that has no server.
    public bool RouteUnavailable { get; set; }

    //Rebuilt from the schema for an engine whose queries need it (ArangoDB); null until then.
    public IGraphQueryBuilder SchemaQueryBuilder { get; set; }

    //Whether the canvas follows the database (GUN), rather than showing one moment of it.
    public bool LiveUpdates { get; set; }

    //Set while the connection's tab pulses to point at where to connect.
    public bool PulseConnect { get; set; }

    //The saved connection picked into the form, and the name box the form is saved under.
    public string SelectedConnectionKey { get; set; }
    public string ConnectionName { get; set; }

    //The fields as they stood when the form last stopped being a saved connection, to tell whether a new one has anything to save.
    public GremlinDB.GremlinConnection UnsavedStart { get; set; } = EmptyForm();
    public bool ConnectionNameError { get; set; }
    public string ConnectionNameErrorMessage { get; set; }

    //The password the form's connection was saved with, kept to the transport it was saved with.
    public SavedSecretRule SavedPassword { get; } = new();

    //Its query tabs, and the one in front.
    public List<QueryTab> Tabs { get; set; } = new() { new QueryTab { Name = "Query 1" } };
    public int ActiveTabIndex { get; set; }

    private static GremlinDB.GremlinConnection EmptyForm()
    {
        return new("WebSocket", 443, true, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    ///<summary>Closes this connection's live link, for a connection that is going: its database goes, and with it any subscription.</summary>
    public async Task CloseAsync()
    {
        if (Db is not null)
            await Db.DisposeAsync();
    }
}
