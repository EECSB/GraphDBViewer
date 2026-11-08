namespace GraphDBViewerWeb.Code;

///<summary>
///One of the viewer's open connections as the browser keeps it between sessions (the multiple-connections spec, task 3):
///its form without its key, the saved connection it was picked from, whether it was drawing offline, and its query tabs.
///</summary>
public class ConnectionSnapshot
{
    public string Id { get; set; }

    //The saved connection picked into the form, which fills it again on the way back, key and all, as the saved list
    //keeps it; null for a form typed in and never saved.
    public string SavedKey { get; set; }

    //The form as it stood, its key left out: a form nobody saved does not keep a password in a second place.
    public GremlinDB.GremlinConnection Form { get; set; }

    public bool Offline { get; set; }
    public List<TabSnapshot> Tabs { get; set; }
    public int ActiveTab { get; set; }
}

///<summary>The open connections a previous session left, and which was in front.</summary>
public class OpenConnections
{
    public List<ViewerConnection> Connections { get; set; }
    public int Active { get; set; }
}
