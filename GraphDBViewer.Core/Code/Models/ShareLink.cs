using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>What a shared viewer opens: nothing in particular, a connection the address describes, or the instance's own graph.</summary>
public enum ShareSource
{
    None,
    Connection,
    InstanceGraph
}

///<summary>
///What the Share dialog is set to: what a link or an embed opens, and for an embed how it looks and whom it talks with.
///Held as what somebody chose rather than as parameters, so which parameter a choice becomes, and when it is left out
///because it would say nothing, is decided in one place (<see cref="ShareLink"/>) that a test can read. The dialog and its
///builder are GDS Viewer's (2026-10-31, the user's call), written for this viewer's address (<see cref="EmbedSettings"/>).
///</summary>
public sealed class ShareOptions
{
    public ShareSource Source { get; set; }

    ///<summary>The connection a <see cref="ShareSource.Connection"/> describes. Its password and username are never written.</summary>
    public GremlinDB.GremlinConnection Connection { get; set; }

    ///<summary>The query it opens with, or null for none.</summary>
    public string Query { get; set; }

    ///<summary>The editor's language for the query: gremlin, cypher, sparql, aql or dql.</summary>
    public string Language { get; set; }

    ///<summary>Whether the query runs as it opens, which it does unless the address says otherwise.</summary>
    public bool Run { get; set; } = true;

    ///<summary>The view it opens in, as the page numbers them: 1 JSON, 2 2D, 3 3D, 4 Table, 5 Map, 6 Globe.</summary>
    public int View { get; set; } = 2;

    ///<summary>An embed's theme, one of ShellState's, or null to leave the embedded viewer its own.</summary>
    public string Theme { get; set; }

    ///<summary>The embedding page's own origin, which turns the embedded viewer's messages on, or null for none.</summary>
    public string Origin { get; set; }

    ///<summary>The read-only token an embed of the instance's graph reads with, or null for none.</summary>
    public string Token { get; set; }
}

///<summary>
///A Share dialog's choices as an address, and as the iframe that embeds it. Only what changes something is written: a
///default written down is a default frozen, since every parameter an address names overrules what the viewer would do
///unasked. And never a password or a username: whoever can read a link or a page can read everything in its address.
///Names and values are the canonical ones <see cref="EmbedSettings.Parse"/> reads.
///</summary>
public static class ShareLink
{
    ///<summary>The full viewer's page, relative to the app's base address.</summary>
    public const string ViewerPath = "";

    ///<summary>The embedded viewer's page (Embed.razor), relative to the app's base address.</summary>
    public const string EmbedPath = "embed";

    ///<summary>The address that opens what the options say: the full viewer, or the embedded one at <see cref="EmbedPath"/>.</summary>
    public static string Address(string baseUri, ShareOptions options, bool embed)
    {
        var named = new List<string>();

        if (options.Source == ShareSource.InstanceGraph)
            named.Add("instance=true");
        else if (options.Source == ShareSource.Connection && options.Connection != null)
            AddConnection(named, options.Connection);

        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            named.Add($"query={Uri.EscapeDataString(options.Query.Trim())}");

            //Written whenever there is a query, since a link opens in somebody's own workspace, whose editor may be set to
            //another language than the one the query is in.
            if (!string.IsNullOrWhiteSpace(options.Language))
                named.Add($"lang={Uri.EscapeDataString(options.Language.Trim().ToLowerInvariant())}");

            if (!options.Run)
                named.Add("run=false");
        }

        named.Add($"view={ViewName(options.View)}");

        string fragment = "";

        if (embed)
        {
            if (ThemeName(options.Theme) is string theme)
                named.Add($"theme={theme}");

            if (EmbedSettings.NormalizeOrigin(options.Origin) is string origin)
                named.Add($"origin={Uri.EscapeDataString(origin)}");

            //In the fragment, which a browser sends to no server and puts in no Referer (EmbedSession), and only for the
            //instance's graph, the one thing the token reads.
            if (options.Source == ShareSource.InstanceGraph && !string.IsNullOrWhiteSpace(options.Token))
                fragment = $"#{EmbedSession.TokenKey}={Uri.EscapeDataString(options.Token.Trim())}";
        }

        string path = ViewerPath;

        if (embed)
            path = EmbedPath;

        return $"{baseUri}{path}?{string.Join("&", named)}{fragment}";
    }

    ///<summary>
    ///The iframe that embeds an address, ready to paste into a page. A size that is not a size is left at the default rather
    ///than written as given, since what is typed here goes into somebody else's page, and an attribute holding a quote or a
    ///bracket would break out of the tag it is in. A bare number is pixels, which is what the attribute means by one.
    ///</summary>
    public static string Iframe(string address, string width, string height, string title)
    {
        return $"<iframe src=\"{Attribute(address)}\" width=\"{SizeOr(width, DefaultWidth)}\" height=\"{SizeOr(height, DefaultHeight)}\" style=\"border: 0;\" title=\"{Attribute(title ?? "")}\"></iframe>";
    }

    public const string DefaultWidth = "100%";
    public const string DefaultHeight = "600";

    ///<summary>
    ///A connection as the address describes one, against what <see cref="EmbedSettings.BuildConnection"/> fills in: the
    ///engine unless it is the one the address implies, the port unless it is the engine's own, SSL only when it is off,
    ///which the address otherwise takes it to be, and the route only when it is the browser's.
    ///</summary>
    private static void AddConnection(List<string> named, GremlinDB.GremlinConnection connection)
    {
        string dbType = connection.DatabaseType ?? "ApacheTinkerPop";
        bool sparql = dbType == "Sparql";

        string impliedType = "ApacheTinkerPop";

        if (!string.IsNullOrWhiteSpace(connection.Endpoint))
            impliedType = "Sparql";

        if (dbType != impliedType)
            named.Add($"dbType={EngineName(dbType)}");

        //Whenever it is set, not for SPARQL alone: a GUN connection is addressed by its relay URL here too (GunDb.PeerUrls
        //reads it before the host), and the dialog offers one that names no host.
        if (!string.IsNullOrWhiteSpace(connection.Endpoint))
            named.Add($"endpoint={Uri.EscapeDataString(connection.Endpoint.Trim())}");

        if (!sparql)
        {
            if (!string.IsNullOrWhiteSpace(connection.Hostname))
                named.Add($"host={Uri.EscapeDataString(connection.Hostname.Trim())}");

            if (connection.Port != EmbedSettings.DefaultPortFor(dbType, connection.UseSSL))
                named.Add($"port={connection.Port}");

            if (!connection.UseSSL)
                named.Add("ssl=false");

            if (connection.Transport == "HTTP")
                named.Add("transport=http");

            if (!string.IsNullOrWhiteSpace(connection.Database))
                named.Add($"database={Uri.EscapeDataString(connection.Database.Trim())}");

            if (!string.IsNullOrWhiteSpace(connection.Collection))
                named.Add($"collection={Uri.EscapeDataString(connection.Collection.Trim())}");
        }

        if (!connection.ViaServer)
            named.Add("viaServer=false");
    }

    ///<summary>An engine by the short name the address takes for it.</summary>
    private static string EngineName(string databaseType)
    {
        if (databaseType == "CosmosDb")
            return "cosmos";
        else if (databaseType == "Sparql")
            return "sparql";
        else if (databaseType == "Neo4j")
            return "neo4j";
        else if (databaseType == "ArangoDb")
            return "arango";
        else if (databaseType == "Dgraph")
            return "dgraph";
        else if (databaseType == "Gun")
            return "gun";

        return "tinkerpop";
    }

    ///<summary>A view by the name the address takes for it. Anything else is 2D, which is what the page opens in unasked.</summary>
    public static string ViewName(int view)
    {
        if (view == 1)
            return "json";
        else if (view == 3)
            return "3d";
        else if (view == 4)
            return "table";
        else if (view == 5)
            return "map";
        else if (view == 6)
            return "globe";

        return "2d";
    }

    ///<summary>A theme by the shortest name the address takes for it, or null for anything that is not one of the six.</summary>
    private static string ThemeName(string theme)
    {
        if (theme == ShellState.ThemeDark)
            return "dark";
        else if (theme == ShellState.ThemeDarkBlue)
            return "dark-blue";
        else if (theme == ShellState.ThemeLight)
            return "light";
        else if (theme == ShellState.ThemeLightTinted)
            return "tinted";
        else if (theme == ShellState.ThemeLightBlue)
            return "light-blue";
        else if (theme == ShellState.ThemeLightBlueTinted)
            return "blue-tinted";

        return null;
    }

    ///<summary>A size as the attribute takes it, a whole or decimal number and optionally a percentage, or the default.</summary>
    private static string SizeOr(string given, string fallback)
    {
        string size = (given ?? "").Trim();

        if (size.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            size = size[..^2].TrimEnd();

        if (Regex.IsMatch(size, @"^\d{1,5}(\.\d{1,2})?%?$"))
            return size;

        return fallback;
    }

    ///<summary>Text made safe for a double-quoted attribute.</summary>
    private static string Attribute(string value)
    {
        return value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
