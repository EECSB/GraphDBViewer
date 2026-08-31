namespace GraphDBViewerWeb.Code;

///<summary>
///What a host tells the shared viewer about itself.
///
///The viewer is one codebase serving two very different deployments — a static build with no server
///behind it, and a full-stack one — and a handful of its decisions depend on which it is running in.
///Rather than let those leak in as build flags or #if, each host registers one of these and the viewer
///reads it: the name it wears, whether a server route exists at all, and whether there is a showcase
///page to offer.
///
///Deliberately a plain registered instance rather than IOptions: there is no configuration binding,
///no reloading and no named variants here — a host states these once at startup and they never change.
///</summary>
public sealed class ViewerHostOptions
{
    ///<summary>
    ///The product name, shown in the top bar, the browser tab and the About dialog. The default is the
    ///neutral one, so a host that forgets to register anything still reads sensibly.
    ///</summary>
    public string AppName { get; set; } = "Graph DB Viewer";

    ///<summary>
    ///A small label under the title naming which build this is -- "Web edition" on the static one -- or
    ///null for a deployment that does not distinguish itself. The title keeps its own spacing either way.
    ///</summary>
    public string EditionLabel { get; set; }

    ///<summary>
    ///Whether a host is there to open database connections on the browser's behalf.
    ///
    ///False in the static build, and it has to be: <see cref="GremlinDB.GremlinConnection.ViaServer"/>
    ///defaults to true, so without this every new connection would be routed at a server that does not
    ///exist. It also hides the Server/Browser choice, which is not a choice when there is one route.
    ///</summary>
    public bool HasServerRoute { get; set; }

    ///<summary>
    ///Where to send someone who wants the server route this deployment does not have. Set on the static
    ///build, which offers the Server choice anyway rather than hiding it: a database it cannot reach is
    ///better explained than silently absent. Null on a deployment that already has a host, where the
    ///choice is real and there is nothing to point at.
    ///</summary>
    public string ServerEditionUrl { get; set; }

    ///<summary>
    ///URL of a bundled showcase page to offer from the settings menu, or null when the deployment has
    ///none — which is the case for anything but the public web-only build.
    ///</summary>
    public string ShowcaseUrl { get; set; }

    ///<summary>
    ///Where to look for a developer's local credentials file, or null to not look at all. Only a
    ///Development build sets it: the file is git-ignored and absent everywhere else, and asking for
    ///something that is not there costs a 404 in the console of every user who never had one.
    ///</summary>
    public string DevSecretsPath { get; set; }
}
