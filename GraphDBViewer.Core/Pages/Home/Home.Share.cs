using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The Share dialog (2026-10-31, GDS Viewer's Share popup, the user's call): a link that opens the viewer set up as this
//one is, or the iframe that embeds it in another site's page. What the dialog starts from is read here, off what is open.
public partial class Home
{
    //Whether the Share dialog is open, from its button in the workspace's row.
    private bool showShare;

    //Whether the connection names something a share can carry: a host, or an endpoint (a SPARQL endpoint, a GUN relay). The instance's own graph is
    //shared as itself, by name, since its address is the host's and never the browser's.
    private bool CanShareConnection
    {
        get
        {
            if (instanceGraph)
                return false;

            return !string.IsNullOrWhiteSpace(connection.Hostname) || !string.IsNullOrWhiteSpace(connection.Endpoint);
        }
    }

    //The view a share opens in: the one open, with the Map and the Globe by the numbers the address gives them.
    private int ShareView
    {
        get
        {
            if (IsMap)
                return MapMode;

            if (IsGlobe)
                return GlobeMode;

            return visualizationMode;
        }
    }

    //Whether this host has accounts, whose server route answers only somebody signed in to it.
    private bool HasAccounts => Account != null && Account.SignedIn;
}
