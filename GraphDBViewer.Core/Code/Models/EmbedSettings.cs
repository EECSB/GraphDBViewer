namespace GraphDBViewerWeb.Code;

///<summary>
///Settings parsed from the page URL's query string, used to initialize the viewer when it is embedded
///(for example, in an iframe). Covers the connection to open, an initial query to run, and the initial
///view mode. Everything is optional; absent values fall back to the app's normal defaults.
///</summary>
///<remarks>
///Recognized keys (case-insensitive):
///<list type="bullet">
///<item>Connection: dbType, transport, host, port, ssl, database, collection, authKey, endpoint, username, viaServer</item>
///<item>Or the instance's own graph: instance (a host with plugins, for an account that may browse it)</item>
///<item>Query: query (q), lang, run</item>
///<item>View: view — json | 2d | 3d | table | map | globe</item>
///<item>Control: connect</item>
///<item>Look: theme — dark | light | tinted, or dark-blue | light-blue | blue-tinted, for the embedded viewer (<c>/embed</c>), which keeps no theme of its own</item>
///<item>Messages: origin — the embedding page's own origin, which turns on the embedded viewer's messages with it</item>
///</list>
///</remarks>
public class EmbedSettings
{
    //Connection
    public string DatabaseType { get; set; }
    public string Transport { get; set; }
    public string Hostname { get; set; }
    public int? Port { get; set; }
    public bool? UseSsl { get; set; }
    public string Database { get; set; }
    public string Collection { get; set; }
    public string AuthKey { get; set; }
    public string Endpoint { get; set; }
    public string Username { get; set; }

    ///<summary>
    ///Whether the host opens the connection instead of the browser. Null means "say nothing", which
    ///leaves the app's own default in place rather than pinning a choice the URL never made.
    ///</summary>
    public bool? ViaServer { get; set; }

    ///<summary>
    ///Whether to open the instance's own graph rather than a connection the URL describes: how a plugin's page
    ///hands the viewer something to draw, such as recall's paths from a lot to every customer. Only a host with
    ///plugins has one, and only an account that may browse it is given it; anywhere else it says nothing.
    ///</summary>
    public bool? InstanceGraph { get; set; }

    //Query & view
    public string Query { get; set; }
    public string Language { get; set; }
    public int? View { get; set; }//1 = JSON, 2 = 2D, 3 = 3D, 4 = Table, 5 = Map, 6 = Globe
    public bool? Connect { get; set; }
    public bool? AutoRun { get; set; }

    ///<summary>
    ///The theme the embedded viewer wears, so it can match the page around it (2026-10-21): one of ShellState's, or null to
    ///leave the viewer's own. Only the embedded viewer reads it, since anywhere else the theme is the person's choice.
    ///</summary>
    public string Theme { get; set; }

    ///<summary>
    ///The origin of the page that embeds the viewer, which turns the embedded viewer's messages on (2026-10-23,
    ///Home.EmbedMessages.cs): it reads a message only from its parent window at this origin, and posts only to that
    ///window, addressed to it. Null, which is every address that names none or names something that is not an absolute
    ///http(s) origin, leaves them off. Only the embedded viewer reads it, as it does the theme.
    ///</summary>
    public string Origin { get; set; }

    ///<summary>True when enough was supplied to open a connection (a host, or an endpoint such as a SPARQL endpoint or a GUN relay).</summary>
    public bool HasConnection => !string.IsNullOrWhiteSpace(Hostname) || !string.IsNullOrWhiteSpace(Endpoint);

    ///<summary>True when the URL carried any recognized setting at all.</summary>
    public bool HasAny => HasConnection
        || InstanceGraph == true
        || DatabaseType != null
        || Query != null
        || Language != null
        || View.HasValue;

    ///<summary>Parses the query-string portion of a URL (with or without the leading '?').</summary>
    public static EmbedSettings Parse(string queryString)
    {
        var s = new EmbedSettings();
        var map = ParseQueryString(queryString);

        if (map.Count == 0)
            return s;

        s.DatabaseType = NormalizeDbType(GetValue(map, "dbtype", "databasetype"));
        s.Transport = NormalizeTransport(GetValue(map, "transport"));
        s.Hostname = GetValue(map, "host", "hostname");
        s.Port = ParseInt(GetValue(map, "port"));
        s.UseSsl = ParseFlag(map, "ssl", "usessl");
        s.Database = GetValue(map, "database");
        s.Collection = GetValue(map, "collection");
        s.AuthKey = GetValue(map, "authkey", "key");
        s.Endpoint = GetValue(map, "endpoint", "url");
        s.Username = GetValue(map, "username", "user");
        s.ViaServer = ParseFlag(map, "viaserver", "proxy");
        s.InstanceGraph = ParseFlag(map, "instance", "instancegraph");

        s.Query = GetValue(map, "query", "q");
        s.Language = NormalizeLanguage(GetValue(map, "lang", "language"));
        s.View = ParseView(GetValue(map, "view", "mode"));
        s.Connect = ParseFlag(map, "connect", "autoconnect");
        s.AutoRun = ParseFlag(map, "run", "autorun");
        s.Theme = NormalizeTheme(GetValue(map, "theme"));
        s.Origin = NormalizeOrigin(GetValue(map, "origin"));

        return s;
    }

    ///<summary>Builds a connection from the parsed settings, filling in sensible defaults for anything omitted.</summary>
    public GremlinDB.GremlinConnection BuildConnection()
    {
        string dbType;

        if (DatabaseType != null)
            dbType = DatabaseType;
        else if (!string.IsNullOrWhiteSpace(Endpoint))
            dbType = "Sparql";
        else
            dbType = "ApacheTinkerPop";

        var transport = Transport ?? "WebSocket";
        var port = Port ?? DefaultPortFor(dbType, UseSsl);

        //An address that says nothing about SSL gets it, on whatever port (the user's rule of 2026-10-03: what the
        //product defaults to is the secure option, and plain is a choice somebody makes). Until then it was read off
        //the port, and only 443 meant SSL; a plain database is now one the address says ssl=false for.
        var useSsl = UseSsl ?? true;

        var conn = new GremlinDB.GremlinConnection(transport, port, useSsl, Hostname ?? "", AuthKey ?? "", Database ?? "", Collection ?? "")
        {
            DatabaseType = dbType,
            Endpoint = Endpoint,
            Username = Username
        };

        //Only assigned when the URL actually said something, so an embed that omits it keeps following
        //the app's default instead of being frozen at whatever the default happened to be when the
        //embed URL was written.
        if (ViaServer.HasValue)
            conn.ViaServer = ViaServer.Value;

        return conn;
    }

    public static string NormalizeDbType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "cosmos" || v == "cosmosdb" || v == "azure")
            return "CosmosDb";
        else if (v == "sparql" || v == "rdf")
            return "Sparql";
        else if (v == "tinkerpop" || v == "apachetinkerpop" || v == "gremlin")
            return "ApacheTinkerPop";
        else if (v == "neo4j" || v == "memgraph" || v == "bolt" || v == "cypher")
            return "Neo4j";
        else if (v == "arango" || v == "arangodb" || v == "aql")
            return "ArangoDb";
        else if (v == "dgraph" || v == "dql")
            return "Dgraph";
        else if (v == "gun" || v == "gunjs" || v == "gun.js")
            return "Gun";

        return value;//pass an already-canonical value through unchanged
    }

    public static string NormalizeTransport(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "ws" || v == "websocket")
            return "WebSocket";
        else if (v == "http" || v == "https" || v == "rest")
            return "HTTP";

        return value;
    }

    public static string NormalizeLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "gremlin")
            return "gremlin";
        else if (v == "cypher" || v == "opencypher")
            return "cypher";
        else if (v == "sparql")
            return "sparql";
        else if (v == "aql")
            return "aql";
        else if (v == "dql")
            return "dql";

        return value;
    }

    ///<summary>A theme as ShellState names it, or null for anything else.</summary>
    public static string NormalizeTheme(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "dark")
            return ShellState.ThemeDark;
        else if (v == "light")
            return ShellState.ThemeLight;
        else if (v == "tinted" || v == "light-tinted")
            return ShellState.ThemeLightTinted;
        else if (v == "dark-blue")
            return ShellState.ThemeDarkBlue;
        else if (v == "light-blue")
            return ShellState.ThemeLightBlue;
        else if (v == "blue-tinted" || v == "light-blue-tinted")
            return ShellState.ThemeLightBlueTinted;

        return null;
    }

    ///<summary>
    ///An origin as a browser writes one in a message event's <c>origin</c>: the scheme and the host in lowercase, an
    ///international host in its ASCII form, and the port only where it is not the scheme's own, so that comparing the two
    ///exactly is enough. Only an absolute http or https origin is one: a scheme, a host and a port, with at most a lone
    ///slash after them. A path, a query, a fragment or a user, a bare host, another scheme, <c>*</c> and the opaque
    ///<c>null</c> are all null, and leave the messages off rather than guessing what the page meant.
    ///</summary>
    public static string NormalizeOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim();

        if (v.EndsWith('/'))
            v = v.Substring(0, v.Length - 1);

        var schemeEnds = v.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnds <= 0)
            return null;

        //Read before the parser, which would quietly drop some of it: "https://example.com/.." is a path, and so not an
        //origin, however little of it is left once the dot segments are resolved.
        var authority = v.Substring(schemeEnds + 3);

        if (authority.Length == 0 || authority.Any(c => c <= ' ' || NotInAnOrigin.Contains(c)))
            return null;

        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        //A browser writes an IPv6 host in its brackets and any other in its ASCII form, which is what IdnHost is.
        string host;

        if (uri.HostNameType == UriHostNameType.IPv6)
            host = uri.Host;
        else
            host = uri.IdnHost;

        if (string.IsNullOrEmpty(host))
            return null;

        if (uri.IsDefaultPort)
            return $"{uri.Scheme}://{host}";

        return $"{uri.Scheme}://{host}:{uri.Port}";
    }

    //What ends an origin's host and port, or would have a parser read something other than one: a path, a query, a
    //fragment, a user, a backslash a browser reads as a slash, and an escape.
    private const string NotInAnOrigin = "/?#@\\%";

    public static int? ParseView(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "json" || v == "1")
            return 1;
        else if (v == "2d" || v == "graph" || v == "2")
            return 2;
        else if (v == "3d" || v == "3")
            return 3;
        else if (v == "table" || v == "4")
            return 4;
        else if (v == "map" || v == "5")
            //The two geographic modes: the 2D view on a world map, the 3D view on a globe.
            return 5;
        else if (v == "globe" || v == "6")
            return 6;

        return null;
    }

    ///<summary>
    ///Where to dial when the URL named no port: the engine's own default.
    ///
    ///It used to be Gremlin's alone — 8182, or 443 unless the URL said <c>ssl=false</c> — which meant an
    ///embed for Bolt, ArangoDB, Dgraph or GUN quietly dialed 443 and could only fail. Those engines get
    ///their own port now, where each serves TLS as well as plain; a Gremlin endpoint keeps assuming a
    ///hosted, TLS one, since an embed URL is usually pointing at something public and an unencrypted default
    ///would be the worse guess.
    ///</summary>
    public static int DefaultPortFor(string databaseType, bool? useSsl)
    {
        var provider = GraphDbProviders.For(databaseType);

        if (provider.DefaultPort == GremlinDefaultPort && useSsl != false)
            return TlsPort;

        return provider.DefaultPort;
    }

    ///<summary>TinkerPop's own port, and the one an embed URL only falls back to when it says ssl=false.</summary>
    public const int GremlinDefaultPort = 8182;

    ///<summary>Where a hosted TLS endpoint listens, and where a Gremlin embed dials when it names no port.</summary>
    public const int TlsPort = 443;

    private static string GetValue(Dictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
            if (map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;

        return null;
    }

    ///<summary>
    ///A yes-or-no setting: its value read by <see cref="ParseBool"/>, and the key named with no value at all on, as
    ///in the documented <c>/embed?instance&amp;q=…</c>. Until 2026-10-31 a bare key read as nothing, so that link did
    ///not open the instance's graph.
    ///</summary>
    private static bool? ParseFlag(Dictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
            if (map.TryGetValue(key, out var v))
            {
                if (string.IsNullOrEmpty(v))
                    return true;

                return ParseBool(v);
            }

        return null;
    }

    private static int? ParseInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, out var n))
            return n;

        return null;
    }

    private static bool? ParseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "true" || v == "1" || v == "yes" || v == "on")
            return true;
        else if (v == "false" || v == "0" || v == "no" || v == "off")
            return false;

        return null;
    }

    private static Dictionary<string, string> ParseQueryString(string queryString)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(queryString))
            return map;

        var s = queryString;

        if (s.StartsWith("?"))
            s = s.Substring(1);

        if (s.Length == 0)
            return map;

        var pairs = s.Split('&', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            string key;
            string value;

            if (eq < 0)
            {
                key = pair;
                value = "";
            }
            else
            {
                key = pair.Substring(0, eq);
                value = pair.Substring(eq + 1);
            }

            key = Decode(key);

            if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                map[key] = Decode(value);
        }

        return map;
    }

    private static string Decode(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        //'+' means space in a query string; decode it before unescaping the %XX sequences.
        return Uri.UnescapeDataString(s.Replace('+', ' '));
    }
}
