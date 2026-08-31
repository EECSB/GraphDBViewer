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
///<item>Query: query (q), lang, run</item>
///<item>View: view — json | 2d | 3d | table</item>
///<item>Control: connect</item>
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

    //Query & view
    public string Query { get; set; }
    public string Language { get; set; }
    public int? View { get; set; }//1 = JSON, 2 = 2D, 3 = 3D, 4 = Table
    public bool? Connect { get; set; }
    public bool? AutoRun { get; set; }

    ///<summary>True when enough was supplied to open a connection (a Gremlin host or a SPARQL endpoint).</summary>
    public bool HasConnection => !string.IsNullOrWhiteSpace(Hostname) || !string.IsNullOrWhiteSpace(Endpoint);

    ///<summary>True when the URL carried any recognized setting at all.</summary>
    public bool HasAny => HasConnection
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
        s.UseSsl = ParseBool(GetValue(map, "ssl", "usessl"));
        s.Database = GetValue(map, "database");
        s.Collection = GetValue(map, "collection");
        s.AuthKey = GetValue(map, "authkey", "key");
        s.Endpoint = GetValue(map, "endpoint", "url");
        s.Username = GetValue(map, "username", "user");
        s.ViaServer = ParseBool(GetValue(map, "viaserver", "proxy"));

        s.Query = GetValue(map, "query", "q");
        s.Language = NormalizeLanguage(GetValue(map, "lang", "language"));
        s.View = ParseView(GetValue(map, "view", "mode"));
        s.Connect = ParseBool(GetValue(map, "connect", "autoconnect"));
        s.AutoRun = ParseBool(GetValue(map, "run", "autorun"));

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
        var useSsl = UseSsl ?? DefaultSsl(port);

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

        if (v == "sparql" || v == "rdf")
            return "Sparql";

        if (v == "tinkerpop" || v == "apachetinkerpop" || v == "gremlin")
            return "ApacheTinkerPop";

        if (v == "neo4j" || v == "memgraph" || v == "bolt" || v == "cypher")
            return "Neo4j";

        if (v == "arango" || v == "arangodb" || v == "aql")
            return "ArangoDb";

        if (v == "dgraph" || v == "dql")
            return "Dgraph";

        if (v == "gun" || v == "gunjs" || v == "gun.js")
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

        if (v == "http" || v == "https" || v == "rest")
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

        if (v == "cypher" || v == "opencypher")
            return "cypher";

        if (v == "sparql")
            return "sparql";

        if (v == "aql")
            return "aql";

        if (v == "dql")
            return "dql";

        return value;
    }

    public static int? ParseView(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToLowerInvariant();

        if (v == "json" || v == "1")
            return 1;

        if (v == "2d" || v == "graph" || v == "2")
            return 2;

        if (v == "3d" || v == "3")
            return 3;

        if (v == "table" || v == "4")
            return 4;

        return null;
    }

    ///<summary>
    ///Where to dial when the URL named no port: the engine's own default.
    ///
    ///It used to be Gremlin's alone — 8182, or 443 unless the URL said <c>ssl=false</c> — which meant an
    ///embed for Bolt, ArangoDB, Dgraph or GUN quietly dialled 443 and could only fail. Those engines get
    ///their own port now; a Gremlin endpoint keeps assuming a hosted, TLS one, since an embed URL is
    ///usually pointing at something public and an unencrypted default would be the worse guess.
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

    ///<summary>Where anything TLS listens, and the only port SSL is assumed on.</summary>
    public const int TlsPort = 443;

    ///<summary>
    ///Whether to use SSL when the URL did not say. Only 443 implies it: every other port an engine listens
    ///on by default — 8182, 7687, 8529, 8080, 8765 — is a plain one, and a database behind TLS on a
    ///non-standard port is the case worth spelling out rather than the case worth assuming.
    ///</summary>
    public static bool DefaultSsl(int port)
    {
        return port == TlsPort;
    }

    private static string GetValue(Dictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
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

        if (v == "false" || v == "0" || v == "no" || v == "off")
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
