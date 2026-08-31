using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GraphDBViewerWeb.Code;
using Neo4j.Driver;

namespace GraphDBViewerWeb.Server.Db;

///<summary>
///The host-side Bolt driver: an <see cref="IGraphDb"/> that runs Cypher against Neo4j / Memgraph with the
///official .NET driver. It exists because Bolt rides a raw TCP socket, which the browser cannot open — so
///a connection marked <see cref="GremlinDB.GremlinConnection.ViaServer"/> runs here on the host instead of
///in WebAssembly. The browser-direct route uses the vendored JavaScript driver (<c>Neo4jBrowserDb</c>);
///both shape their records into the same envelope and hand it to <see cref="Neo4jConverter"/>, so the
///record→graph mapping is written and tested once.
///</summary>
public sealed class Neo4jServerDb : IGraphDb
{
    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jServerDb(GremlinDB.GremlinConnection connection)
    {
        //bolt+s adds TLS with certificate verification; plain bolt is the local / Docker case. The scheme
        //picks the encryption, so nothing else here has to. Building the driver does not dial — the .NET
        //driver opens its socket lazily on the first query, matching how the pool creates drivers up front.
        string scheme = connection.UseSSL ? "bolt+s" : "bolt";
        var uri = new Uri($"{scheme}://{connection.Hostname}:{connection.Port}");

        _driver = GraphDatabase.Driver(uri, AuthFor(connection));
        _database = connection.Database;
    }

    private static IAuthToken AuthFor(GremlinDB.GremlinConnection connection)
    {
        string username = connection.Username;
        string password = connection.AuthKey;

        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
            return AuthTokens.None;

        //Neo4j's default user is "neo4j"; a blank username with a password almost always means it.
        if (string.IsNullOrEmpty(username))
            username = "neo4j";

        return AuthTokens.Basic(username, password ?? "");
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //A blank database name leaves the driver on its default (Neo4j's "neo4j", Memgraph's "memgraph").
        IAsyncSession session;
        if (string.IsNullOrWhiteSpace(_database))
            session = _driver.AsyncSession();
        else
            session = _driver.AsyncSession(o => o.WithDatabase(_database));

        try
        {
            var cursor = await session.RunAsync(query);
            var keys = await cursor.KeysAsync();

            var records = new List<IRecord>();
            while (await cursor.FetchAsync())
                records.Add(cursor.Current);

            //The summary carries the query plan when EXPLAIN or PROFILE asked for one. It has to be read
            //here rather than left behind: EXPLAIN produces no records at all, so a caller that only looked
            //at those would see an empty result instead of the plan it asked for.
            var summary = await cursor.ConsumeAsync();
            var plan = PlanEnvelope(summary);

            if (plan != null)
                return Neo4jConverter.ToGraphDbResult(plan);

            return Neo4jConverter.ToGraphDbResult(BuildEnvelope(keys, records));
        }
        catch (OperationCanceledException)
        {
            //Re-thrown like the browser-direct drivers so callers can tell a cancel from a failure.
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure(ex.Message);
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    ///<summary>
    ///Flattens the summary's query plan into <see cref="CypherPlan"/> rows, or returns null when the query
    ///asked for no plan. PROFILE fills in the measured columns; EXPLAIN leaves them empty, having only
    ///planned the query rather than run it.
    ///</summary>
    private static string? PlanEnvelope(IResultSummary summary)
    {
        if (summary == null)
            return null;

        var rows = new List<Dictionary<string, object?>>();

        if (summary.Profile != null)
            AppendProfile(summary.Profile, 0, rows);
        else if (summary.Plan != null)
            AppendPlan(summary.Plan, 0, rows);
        else
            return null;

        var columns = new List<string>
        {
            CypherPlan.DepthColumn,
            CypherPlan.OperatorColumn,
            CypherPlan.RowsColumn,
            CypherPlan.DbHitsColumn,
            CypherPlan.TimeColumn,
            CypherPlan.DetailsColumn
        };

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["columns"] = columns, ["records"] = rows });
    }

    private static void AppendProfile(IProfiledPlan plan, int depth, List<Dictionary<string, object?>> rows)
    {
        rows.Add(new Dictionary<string, object?>
        {
            [CypherPlan.DepthColumn] = depth,
            [CypherPlan.OperatorColumn] = plan.OperatorType,
            [CypherPlan.RowsColumn] = plan.Records,
            [CypherPlan.DbHitsColumn] = plan.DbHits,
            [CypherPlan.TimeColumn] = TimeMilliseconds(plan.Arguments),
            [CypherPlan.DetailsColumn] = Details(plan.Arguments)
        });

        foreach (var child in plan.Children)
            AppendProfile(child, depth + 1, rows);
    }

    private static void AppendPlan(IPlan plan, int depth, List<Dictionary<string, object?>> rows)
    {
        rows.Add(new Dictionary<string, object?>
        {
            [CypherPlan.DepthColumn] = depth,
            [CypherPlan.OperatorColumn] = plan.OperatorType,
            //EXPLAIN never ran the query, so there is nothing measured to report.
            [CypherPlan.RowsColumn] = null,
            [CypherPlan.DbHitsColumn] = null,
            [CypherPlan.TimeColumn] = null,
            [CypherPlan.DetailsColumn] = Details(plan.Arguments)
        });

        foreach (var child in plan.Children)
            AppendPlan(child, depth + 1, rows);
    }

    //Neo4j reports an operator's time in nanoseconds, under an argument whose casing has varied by version.
    private static double? TimeMilliseconds(IDictionary<string, object> arguments)
    {
        foreach (var key in new[] { "Time", "time" })
        {
            if (!arguments.TryGetValue(key, out var value) || value == null)
                continue;

            if (double.TryParse(value.ToString(), out var nanoseconds))
                return nanoseconds / 1_000_000d;
        }

        return null;
    }

    ///<summary>
    ///What the operator worked on — the label, index or expression — as one short line.
    ///
    ///Neo4j supplies exactly that under a "Details" argument, so it is preferred outright. The fallback
    ///joins whatever else is there, minus the bookkeeping the table already has columns for and minus
    ///<c>string-representation</c>, which is the entire pretty-printed plan rendered as ASCII art and would
    ///swamp the row it appears on.
    ///</summary>
    private static string Details(IDictionary<string, object> arguments)
    {
        if (arguments.TryGetValue("Details", out var details) && details != null)
            return Shorten(details.ToString());

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Time", "time", "Rows", "DbHits", "PageCacheHits", "PageCacheMisses", "PageCacheHitRatio",
            "EstimatedRows", "planner", "planner-impl", "planner-version", "runtime", "runtime-impl",
            "runtime-version", "version", "Memory", "GlobalMemory", "Id", "string-representation", "Cypher"
        };

        var parts = new List<string>();

        foreach (var pair in arguments)
        {
            if (skip.Contains(pair.Key) || pair.Value == null)
                continue;

            parts.Add(pair.Value.ToString());
        }

        return Shorten(string.Join(", ", parts));
    }

    //A detail is a table cell, so it stays on one line and within a sane width.
    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var flattened = text.Replace("\r", " ").Replace("\n", " ").Trim();

        if (flattened.Length <= 200)
            return flattened;

        return flattened.Substring(0, 200) + "…";
    }

    //Shapes the driver's records into the driver-agnostic envelope Neo4jConverter reads — the same shape
    //the browser interop produces from the JavaScript driver's records.
    private static string BuildEnvelope(IReadOnlyList<string> keys, List<IRecord> records)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var record in records)
        {
            var row = new Dictionary<string, object?>();

            foreach (var key in keys)
                row[key] = MapValue(record[key]);

            rows.Add(row);
        }

        var envelope = new Dictionary<string, object> { ["columns"] = keys, ["records"] = rows };

        return JsonSerializer.Serialize(envelope);
    }

    //Maps one Bolt value to its envelope form: nodes / relationships / paths get their $e tag, a returned
    //map or list recurses, and everything else travels as a JSON scalar (temporal and spatial types, which
    //have no JSON scalar, fall back to their string form).
    private static object? MapValue(object? value)
    {
        if (value == null)
            return null;

        if (value is INode node)
            return NodeObject(node);

        if (value is IRelationship relationship)
            return RelationshipObject(relationship);

        if (value is IPath path)
        {
            var nodes = new List<object>();
            foreach (var n in path.Nodes)
                nodes.Add(NodeObject(n));

            var rels = new List<object>();
            foreach (var r in path.Relationships)
                rels.Add(RelationshipObject(r));

            return new Dictionary<string, object> { ["$e"] = "path", ["nodes"] = nodes, ["rels"] = rels };
        }

        if (value is IReadOnlyDictionary<string, object> map)
        {
            var result = new Dictionary<string, object?>();
            foreach (var pair in map)
                result[pair.Key] = MapValue(pair.Value);

            return result;
        }

        if (value is string || value is bool || value is long || value is int || value is double || value is float)
            return value;

        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        if (value is IEnumerable sequence)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
                list.Add(MapValue(item));

            return list;
        }

        return value.ToString();
    }

    private static Dictionary<string, object> NodeObject(INode node)
    {
        return new Dictionary<string, object>
        {
            ["$e"] = "node",
            ["id"] = node.ElementId,
            ["labels"] = node.Labels,
            ["props"] = MapProperties(node.Properties)
        };
    }

    private static Dictionary<string, object> RelationshipObject(IRelationship relationship)
    {
        return new Dictionary<string, object>
        {
            ["$e"] = "rel",
            ["id"] = relationship.ElementId,
            ["type"] = relationship.Type,
            ["start"] = relationship.StartNodeElementId,
            ["end"] = relationship.EndNodeElementId,
            ["props"] = MapProperties(relationship.Properties)
        };
    }

    private static Dictionary<string, object?> MapProperties(IReadOnlyDictionary<string, object> properties)
    {
        var result = new Dictionary<string, object?>();

        foreach (var pair in properties)
            result[pair.Key] = MapValue(pair.Value);

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }
}

///<summary>
///Plugs the host's Bolt driver into the shared provider table when the host assembly loads, so
///<see cref="GraphDBViewerWeb.Server.Api.GraphConnectionPool"/> builds it through <see cref="GraphDbProvider.CreateServer"/>
///alongside every other engine — no per-engine branch anywhere. It lives on the host, not the client,
///because <see cref="Neo4jServerDb"/> uses raw TCP sockets, which WebAssembly cannot open. Running as a
///module initializer means merely loading this assembly (the running host, a test, a tool) registers it —
///there is nothing to remember to call.
///</summary>
internal static class Neo4jServerRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        GraphDbProviders.RegisterServerDriver(GraphDbProviders.Neo4j, connection => new Neo4jServerDb(connection));
    }
}
