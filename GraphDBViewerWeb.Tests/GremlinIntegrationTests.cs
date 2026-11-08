using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///Integration tests that connect to a live Gremlin server, seed a small test graph
///(if it doesn't already exist), and verify queries + data conversion.
///
///Opt-in, configured via environment variables (<see cref="LiveGremlin"/>):
///GREMLIN_HOST      (no default: without it every test here is skipped)
///GREMLIN_PORT      (default: 8182)
///GREMLIN_TRANSPORT (default: WebSocket)
///
///The test graph stays on the server for the next run, so point GREMLIN_HOST at a throwaway server, never at the
///development graph (treeality-dev-graph, 192.168.1.7:8186). A server it names that does not answer fails every test
///here, naming the address.
///</summary>
[Collection("Gremlin")]
public class GremlinIntegrationTests : IAsyncLifetime
{
    private GremlinDB _gremlin = null!;
    private readonly HttpClient _http = new();

    //Sentinel property value to identify our test vertices
    private const string TestMarker = "__graphdbviewer_test__";

    public async Task InitializeAsync()
    {
        //Only a test that runs gets here: without GREMLIN_HOST each is skipped before its class is made.
        await LiveGremlin.AnswersAsync();

        var connection = new GremlinDB.GremlinConnection(LiveGremlin.Transport, LiveGremlin.Port, false, LiveGremlin.Host, string.Empty, string.Empty, string.Empty);
        _gremlin = new GremlinDB(_http, connection);

        await SeedTestGraphAsync();
    }

    public async Task DisposeAsync()
    {
        if (_gremlin is not null)
            await _gremlin.DisposeAsync();
        _http.Dispose();
    }

    //── Seed ──────────────────────────────────────────────────────────

    private async Task SeedTestGraphAsync()
    {
        //Check if test graph already exists
        var check = await _gremlin.ExecuteAsync(
            $"g.V().has('_testMarker', '{TestMarker}').count()");

        if (!check.IsError)
        {
            long count = ExtractCount(check.Data);
            if (count >= 4)
                return;//already seeded
        }

        //Drop any partial leftovers and recreate
        await _gremlin.ExecuteAsync($"g.V().has('_testMarker', '{TestMarker}').drop()");

        //Vertices: 4 people in a small social network
        var addVertices = new List<string>
        {
            $"g.addV('person').property('name', 'Alice').property('age', 30).property('_testMarker', '{TestMarker}')",
            $"g.addV('person').property('name', 'Bob').property('age', 27).property('_testMarker', '{TestMarker}')",
            $"g.addV('person').property('name', 'Charlie').property('age', 35).property('_testMarker', '{TestMarker}')",
            $"g.addV('software').property('name', 'GraphDB Viewer').property('lang', 'C#').property('_testMarker', '{TestMarker}')"
        };
        await _gremlin.ExecuteManyAsync(addVertices);

        //Edges: knows + created relationships
        var addEdges = new List<string>
        {
            $"g.V().has('name', 'Alice').has('_testMarker', '{TestMarker}').as('a').V().has('name', 'Bob').has('_testMarker', '{TestMarker}').addE('knows').from('a').property('since', 2020)",
            $"g.V().has('name', 'Alice').has('_testMarker', '{TestMarker}').as('a').V().has('name', 'Charlie').has('_testMarker', '{TestMarker}').addE('knows').from('a').property('since', 2019)",
            $"g.V().has('name', 'Bob').has('_testMarker', '{TestMarker}').as('b').V().has('name', 'Charlie').has('_testMarker', '{TestMarker}').addE('knows').from('b').property('since', 2021)",
            $"g.V().has('name', 'Alice').has('_testMarker', '{TestMarker}').as('a').V().has('name', 'GraphDB Viewer').has('_testMarker', '{TestMarker}').addE('created').from('a').property('weight', 1.0)",
            $"g.V().has('name', 'Bob').has('_testMarker', '{TestMarker}').as('b').V().has('name', 'GraphDB Viewer').has('_testMarker', '{TestMarker}').addE('created').from('b').property('weight', 0.5)"
        };
        await _gremlin.ExecuteManyAsync(addEdges);
    }

    private static long ExtractCount(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Number)
            return data.GetInt64();

        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.ValueKind == JsonValueKind.Number)
                return first.GetInt64();

            if (first.TryGetProperty("@value", out var inner) && inner.ValueKind == JsonValueKind.Number)
                return inner.GetInt64();
        }

        return 0;
    }

    //── Connection ────────────────────────────────────────────────────
    //What the server answers to count, hasLabel, has, outE and path over the seed is the server's to get right. The
    //client's part is the round trip below, and reading the rows back is GremlinResultShapeTests' (2026-10-23).

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Connection_CanExecuteSimpleQuery()
    {
        var result = await _gremlin.ExecuteAsync("g.V().limit(1)");

        Assert.False(result.IsError, $"Expected success but got: {result.Error}");
    }

    //── Converter Integration (live data → Cytoscape / ForceGraph) ───

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Converter_LiveTraversalTriples_ProducesValidCytoscapeJson()
    {
        var result = await _gremlin.ExecuteAsync(
            $"g.V().has('_testMarker', '{TestMarker}').as('v').bothE().as('e').otherV().as('o').select('v','e','o')");
        Assert.False(result.IsError, result.Error);

        var cytoscapeJson = GraphDataConverter.ToCytoscapeJson(result.Data);
        var elements = JsonDocument.Parse(cytoscapeJson).RootElement;

        Assert.Equal(JsonValueKind.Array, elements.ValueKind);
        Assert.True(elements.GetArrayLength() > 0, "Cytoscape output should have elements");

        int nodeCount = 0, edgeCount = 0;
        foreach (var el in elements.EnumerateArray())
        {
            var data = el.GetProperty("data");
            Assert.True(data.TryGetProperty("id", out _), "Every element needs an id");

            if (data.TryGetProperty("source", out _))
            {
                Assert.True(data.TryGetProperty("target", out _), "Edge needs target");
                Assert.True(data.TryGetProperty("label", out _), "Edge needs label");
                edgeCount++;
            }
            else
            {
                Assert.True(data.TryGetProperty("label", out _), "Node needs label");
                nodeCount++;
            }
        }

        Assert.True(nodeCount >= 2, $"Expected at least 2 nodes, got {nodeCount}");
        Assert.True(edgeCount >= 1, $"Expected at least 1 edge, got {edgeCount}");
    }

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Converter_LiveTraversalTriples_ProducesValidForceGraphJson()
    {
        var result = await _gremlin.ExecuteAsync(
            $"g.V().has('_testMarker', '{TestMarker}').as('v').bothE().as('e').otherV().as('o').select('v','e','o')");
        Assert.False(result.IsError, result.Error);

        var forceJson = GraphDataConverter.ToForceGraphJson(result.Data);
        var graph = JsonDocument.Parse(forceJson).RootElement;

        var nodes = graph.GetProperty("nodes");
        var links = graph.GetProperty("links");

        Assert.True(nodes.GetArrayLength() >= 2, $"Expected at least 2 nodes, got {nodes.GetArrayLength()}");
        Assert.True(links.GetArrayLength() >= 1, $"Expected at least 1 link, got {links.GetArrayLength()}");

        foreach (var node in nodes.EnumerateArray())
        {
            Assert.True(node.TryGetProperty("id", out _), "Node needs id");
            Assert.True(node.TryGetProperty("label", out _), "Node needs label");
            Assert.True(node.TryGetProperty("group", out _), "Node needs group");
        }

        foreach (var link in links.EnumerateArray())
        {
            Assert.True(link.TryGetProperty("source", out _), "Link needs source");
            Assert.True(link.TryGetProperty("target", out _), "Link needs target");
            Assert.True(link.TryGetProperty("label", out _), "Link needs label");
        }
    }

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Converter_LiveVertexOnly_ProducesNodesNoEdges()
    {
        var result = await _gremlin.ExecuteAsync(
            $"g.V().has('_testMarker', '{TestMarker}')");
        Assert.False(result.IsError, result.Error);

        var forceJson = GraphDataConverter.ToForceGraphJson(result.Data);
        var graph = JsonDocument.Parse(forceJson).RootElement;

        Assert.True(graph.GetProperty("nodes").GetArrayLength() >= 4);
        Assert.Equal(0, graph.GetProperty("links").GetArrayLength());
    }

    //── Error Handling ────────────────────────────────────────────────

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Query_InvalidGremlin_ReturnsError()
    {
        var result = await _gremlin.ExecuteAsync("this is not valid gremlin");

        Assert.True(result.IsError, "Expected an error for invalid Gremlin syntax");
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task Query_EmptyResult_ReturnsEmptyOrNoContent()
    {
        var result = await _gremlin.ExecuteAsync(
            "g.V().has('_nonexistent_property_xyz', 'impossible_value_abc')");

        //TinkerPop returns 204 No Content for empty results, which is treated as an error by GremlinDB.
        //Either an empty array or a 204-based error is acceptable.
        if (result.IsError)
            Assert.Contains("204", result.Error);
        else
        {
            Assert.Equal(JsonValueKind.Array, result.Data.ValueKind);
            Assert.Equal(0, result.Data.GetArrayLength());
        }
    }

    //── Batch Execution ──────────────────────────────────────────────

    [OptInFact(LiveGremlin.HostVariable)]
    public async Task ExecuteMany_RunsMultipleQueries()
    {
        var queries = new Dictionary<string, string>
        {
            ["vertexCount"] = $"g.V().has('_testMarker', '{TestMarker}').count()",
            ["edgeCount"] = $"g.V().has('_testMarker', '{TestMarker}').bothE().dedup().count()",
            ["labels"] = $"g.V().has('_testMarker', '{TestMarker}').label().dedup()"
        };

        var results = await _gremlin.ExecuteManyAsync(queries);

        Assert.Equal(3, results.Count);
        Assert.False(results["vertexCount"].IsError, results["vertexCount"].Error);
        Assert.False(results["edgeCount"].IsError, results["edgeCount"].Error);
        Assert.False(results["labels"].IsError, results["labels"].Error);
    }
}
