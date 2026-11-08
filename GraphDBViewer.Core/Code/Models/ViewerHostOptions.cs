namespace GraphDBViewerWeb.Code;

///<summary>
///What a host tells the shared viewer about itself.
///
///The viewer is one codebase serving two very different deployments — a static build with no server
///behind it, and a full-stack one — and a handful of its decisions depend on which it is running in.
///Rather than let those leak in as build flags or #if, each host registers one of these and the viewer
///reads it: the name it wears, whether a server route exists at all, whether there is a showcase page
///to offer, and where it keeps the keys people give the AI models.
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
    ///The mark drawn before the title in the top bar, a path under the host's wwwroot. The default is the
    ///favicon every host has; one with a logo of its own drawn apart from its favicon names it (2026-10-24).
    ///</summary>
    public string LogoPath { get; set; } = "favicon.svg";

    ///<summary>
    ///The mark drawn instead of <see cref="LogoPath"/> on the dark theme, or null to keep the one mark on every
    ///theme. Treeality's has brighter colors, which hold up on a dark bar (2026-10-25).
    ///</summary>
    public string? DarkLogoPath { get; set; }

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
    ///Whether this host has the plugin platform — a Plugins page for the Settings menu to link to.
    ///True in Treeality; false (the default) in the standalone web-only viewer, which must show no
    ///trace of a plugin system it does not have.
    ///</summary>
    public bool HasPlugins { get; set; }

    ///<summary>
    ///Whether the viewer offers its Share button, the dialog that writes a link to it and the code that embeds it in
    ///another site's page. On unless a host says otherwise, as the standalone viewer does not; Treeality turns it off
    ///(the user, 2026-10-31: Share is the standalone app's).
    ///</summary>
    public bool OffersShare { get; set; } = true;

    ///<summary>
    ///The theme someone who has chosen none starts with, one of <see cref="ShellState"/>'s: Dark green unless a host says
    ///otherwise, as the standalone viewer does, Light blue 1 (the user, 2026-11-02). A theme once chosen is kept.
    ///</summary>
    public string DefaultTheme { get; set; } = ShellState.ThemeDark;

    ///<summary>
    ///Where the host serves its user documentation, which the settings menu links before About, or null when it serves
    ///none. Treeality serves its wiki at /wiki on every instance (2026-10-30); the standalone viewer has no wiki.
    ///</summary>
    public string? WikiUrl { get; set; }

    ///<summary>
    ///Where this host keeps the AI models people add, keys and all, in its own words: what the AI settings
    ///say under the models, and what the About box says after "You supply the key." The viewer cannot know
    ///it, since where a workspace lives is the storage the host composes. Null for a host that keeps them in
    ///the browser alone, as the web-only edition does, and then both say the browser, as they always have.
    ///Treeality keeps them in each person's workspace on its server, and says so.
    ///</summary>
    public string WhereAiKeysAreKept { get; set; }

    ///<summary>
    ///Where this host keeps Ask AI's chats, in its own words after "Kept" at the foot of the window's chats list
    ///(2026-11-02): they are kept where the models are, in the storage the host composes. Null for a host that keeps them in
    ///the browser, as the web-only edition does, and then the list says this browser. Treeality keeps them with the account.
    ///</summary>
    public string WhereAiChatsAreKept { get; set; }

    ///<summary>
    ///Where to look for a developer's local credentials file, or null to not look at all. Only a
    ///Development build sets it: the file is git-ignored and absent everywhere else, and asking for
    ///something that is not there costs a 404 in the console of every user who never had one.
    ///</summary>
    public string DevSecretsPath { get; set; }
}
