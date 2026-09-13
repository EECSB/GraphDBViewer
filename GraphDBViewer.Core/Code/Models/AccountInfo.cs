namespace GraphDBViewerWeb.Code;

///<summary>
///Who the browser is signed in as.
///
///The viewer runs in WebAssembly and the sign-in is a cookie the *server* reads, so the client cannot
///work any of this out for itself — it has to ask. One request on startup, and the answer decides
///whether the top bar shows an account menu: a host without accounts (the standalone web-only edition)
///simply does not serve this path, and the failed fetch leaves the menu unrendered.
///</summary>
public class AccountInfo
{
    ///<summary>Path the client asks. Named once so the host builds its route from the same constant.</summary>
    public const string Path = "api/auth/me";

    ///<summary>
    ///The permission names the shared shell and its platform pages gate on. Declared here rather than
    ///in a host because the names cross the server–client seam inside this very payload; the platform
    ///host's own constants alias these, so the two sides cannot drift. A plugin's permission names are
    ///data from its manifest and never become constants anywhere.
    ///</summary>
    public const string PluginsInstall = "plugins.install";

    ///<summary>Configuring the instance-graph connection — the settings page gates on it.</summary>
    public const string InstanceConfigure = "instance.configure";

    ///<summary>Provisioning a graph container through the Provisioner — its page gates on it.</summary>
    public const string InstanceProvision = "instance.provision";

    ///<summary>
    ///Querying the instance graph directly — browsing the platform's own data on the canvas rather than
    ///through the pages a plugin puts over it. The connection card gates on it, and offers the
    ///connection at all only to an account that holds it.
    ///</summary>
    public const string InstanceQuery = "instance.query";

    ///<summary>
    ///Routing one's own database connections through the host — the Server side of the "Make requests
    ///from" choice. The host dials whatever address the connection names, including ones only it can
    ///route to, which on a shared instance is a capability rather than a convenience; the connection
    ///card pins an account without it to the browser route and says why.
    ///</summary>
    public const string ConnectionsRoute = "connections.route";

    ///<summary>Whether someone is signed in. Always true when the app is reachable at all.</summary>
    public bool SignedIn { get; set; }

    ///<summary>The address they signed in with, shown in the menu so it is obvious *which* account this is.</summary>
    public string Email { get; set; }

    ///<summary>
    ///An antiforgery token for the sign-out form. Handed over here because a form rendered by WebAssembly
    ///has no server-side render to get one from, and signing out is a POST — a link would let any page on
    ///the internet sign you out of this one.
    ///</summary>
    public string AntiforgeryToken { get; set; }

    ///<summary>
    ///The permissions this account holds, through its roles. The client uses them to decide what to
    ///<em>show</em>; the server independently refuses what they forbid, because a WebAssembly build is
    ///a file the user can read and edit (ADR 0008). A host without permissions just leaves this empty.
    ///</summary>
    public string[] Permissions { get; set; } = [];

    ///<summary>Whether the account holds a permission. Null-safe: an older host may not send the field.</summary>
    public bool Can(string permission)
    {
        return Permissions != null && Permissions.Contains(permission);
    }

    ///<summary>What to show when there is no name to show.</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Email))
                return "Account";

            return Email;
        }
    }

    ///<summary>The first letter of the address, for the avatar circle.</summary>
    public string Initial
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Email))
                return "?";

            return Email.Substring(0, 1).ToUpperInvariant();
        }
    }
}
