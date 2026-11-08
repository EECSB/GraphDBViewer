using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Neo4j's Graph Data Science library (DESIGN.md Part 2, Graph algorithms, tier A): PageRank, betweenness, weakly
///connected components, Louvain, degree and Dijkstra's shortest path, each streamed from a projection of the whole
///database graph.
///
///Every run projects the graph into the library's in-memory catalog under a name of its own (<see cref="GraphPrefix"/> and
///a fresh GUID), streams the algorithm over it, and drops it after, a failed or canceled run's too: a projection holds
///the graph in the server's memory until it is dropped. Only the stream modes are used, never write or mutate, so
///nothing is written to the database; the stream's numbers come back for the vertices drawn alone, by their element ids.
///The projection follows the edges as the algorithm asks: along them for PageRank, either way for components and
///communities, and as the panel's Edges followed says for degree, betweenness and the path.
///</summary>
public sealed class GdsAlgorithms : IDatabaseAlgorithms
{
    ///<summary>
    ///Asked of a Bolt connection to find the library, as a standalone call so that it answers whatever its column is
    ///named. The procedure arrived in Graph Data Science 2.5, which has every procedure used here; an older release, or
    ///none, refuses it.
    ///</summary>
    public const string VersionQuery = "CALL gds.version()";

    ///<summary>The relationship property a weighed run's projection holds the chosen edge property under.</summary>
    public const string WeightKey = "gdbvWeight";

    ///<summary>The start of a projection's name; the rest is a GUID, so no two runs, nor anybody's own graph, share one.</summary>
    public const string GraphPrefix = "gdbv-algorithms-";

    private readonly Func<string> _graphNames;

    public GdsAlgorithms(string? version, Func<string>? graphNames = null)
    {
        Version = version;
        _graphNames = graphNames ?? NewGraphName;
    }

    ///<summary>The library as <see cref="VersionQuery"/> answered: its version, whatever the column holding it is called.</summary>
    public static GdsAlgorithms FromVersion(GraphDbResult result)
    {
        var rows = DatabaseAlgorithms.Rows(result);
        string? version = null;

        if (rows.Count > 0)
            version = rows[0].Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        return new GdsAlgorithms(version);
    }

    public string? Version { get; }

    public string Name
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Version))
                return "Neo4j Graph Data Science";

            return $"Neo4j Graph Data Science {Version}";
        }
    }

    public IReadOnlyList<string> Algorithms { get; } =
    [
        AlgorithmNames.Degree,
        AlgorithmNames.PageRank,
        AlgorithmNames.Betweenness,
        AlgorithmNames.Components,
        AlgorithmNames.Communities,
        AlgorithmNames.Path
    ];

    public string Caption(string algorithm)
    {
        if (algorithm == AlgorithmNames.Degree)
            return "Degree";
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank";
        else if (algorithm == AlgorithmNames.Betweenness)
            return "Betweenness";
        else if (algorithm == AlgorithmNames.Components)
            return "Connected components (weakly)";
        else if (algorithm == AlgorithmNames.Communities)
            return "Communities (Louvain)";

        return "Shortest path (Dijkstra)";
    }

    public bool Directs(string algorithm)
    {
        return algorithm == AlgorithmNames.Degree || algorithm == AlgorithmNames.Betweenness || algorithm == AlgorithmNames.Path;
    }

    public bool Weighs(string algorithm)
    {
        return algorithm == AlgorithmNames.Communities || algorithm == AlgorithmNames.Path;
    }

    public string About(string algorithm)
    {
        if (algorithm == AlgorithmNames.Degree)
            return "How many edges each vertex has in the whole graph, counted by Graph Data Science. The vertices drawn come back, by the edges drawn here or not.";
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank by Graph Data Science over the whole graph, along the edges' direction. The scores of the vertices drawn come back.";
        else if (algorithm == AlgorithmNames.Betweenness)
            return "How many shortest paths between other vertices of the whole graph pass through each, by Graph Data Science: the brokers. Past 2,000 vertices it is estimated from 2,000 of them as starts.";
        else if (algorithm == AlgorithmNames.Components)
            return "The pieces the whole graph falls into, the edges' direction ignored, by Graph Data Science's weakly connected components.";
        else if (algorithm == AlgorithmNames.Communities)
            return "Groups more tightly linked among themselves than to the rest of the whole graph, by Graph Data Science's Louvain, the edges' direction ignored.";

        return "The shortest way from one vertex to another through the whole graph, by Graph Data Science's Dijkstra: the fewest edges, or the least total weight. The rest of the graph is dimmed.";
    }

    public async Task<DatabaseAlgorithmOutcome> RunAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        if (!Algorithms.Contains(request.Algorithm))
            return DatabaseAlgorithmOutcome.Failed($"{Name} has no {request.Algorithm}: run it in this browser.");

        var graph = _graphNames();
        DatabaseAlgorithmOutcome outcome;
        GraphDbResult dropped;

        try
        {
            outcome = await ProjectAndRunAsync(graph, request, run, cancellationToken);
        }
        finally
        {
            //Dropped whatever became of the run, and not canceled with it: until it is, the projection holds the graph
            //in the server's memory.
            dropped = await run(DropQuery(graph), CancellationToken.None);
        }

        if (dropped.IsError)
            return outcome.With($"The projection {graph}, made for the run, could not be dropped ({dropped.Error}): it holds the graph in the server's memory until CALL gds.graph.drop('{graph}') is run.");

        return outcome;
    }

    private async Task<DatabaseAlgorithmOutcome> ProjectAndRunAsync(string graph, AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        var weighed = Weighs(request.Algorithm) && request.Weighed;
        var weight = "";

        if (weighed)
            weight = request.WeightProperty;

        var projected = await run(ProjectQuery(graph, Orientation(request.Algorithm, request.Direction), weight), cancellationToken);

        if (projected.IsError)
            return DatabaseAlgorithmOutcome.Failed($"Graph Data Science could not project the database's graph for the run: {projected.Error}");

        var vertices = DatabaseAlgorithms.FirstCount(projected, "nodeCount");
        var where = DatabaseAlgorithms.ComputedOver(Name, vertices);

        if (request.Algorithm == AlgorithmNames.Path)
            return await PathAsync(graph, request, weighed, where, run, cancellationToken);

        long? sampled = null;
        string? note = null;

        if (request.Algorithm == AlgorithmNames.Betweenness && vertices > GraphAlgorithms.BetweennessSources)
        {
            sampled = GraphAlgorithms.BetweennessSources;
            note = $"Estimated by Graph Data Science from walks out of {sampled.Value.ToString("N0", CultureInfo.InvariantCulture)} of the {vertices.Value.ToString("N0", CultureInfo.InvariantCulture)} vertices, picked at random.";
        }

        var answer = await run(StreamQuery(graph, request, weighed, sampled), cancellationToken);

        if (answer.IsError)
            return DatabaseAlgorithmOutcome.Failed($"The database could not run it: {answer.Error}");

        var suffix = DatabaseAlgorithms.DirectionSuffix(request.Direction);

        if (request.Algorithm == AlgorithmNames.Degree)
            return DatabaseAlgorithms.Scored(request, $"Degree{suffix}", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), where, null);
        else if (request.Algorithm == AlgorithmNames.PageRank)
            return DatabaseAlgorithms.Scored(request, "PageRank", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), where, null);
        else if (request.Algorithm == AlgorithmNames.Betweenness)
            return DatabaseAlgorithms.Scored(request, $"Betweenness{suffix}", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), where, note);
        else if (request.Algorithm == AlgorithmNames.Components)
            return DatabaseAlgorithms.Grouped(request, "Component", DatabaseAlgorithms.GroupsOf(answer, "id", "groupId"), where, null);

        return DatabaseAlgorithms.Grouped(request, "Community", DatabaseAlgorithms.GroupsOf(answer, "id", "groupId"), where, null);
    }

    private static async Task<DatabaseAlgorithmOutcome> PathAsync(
        string graph,
        AlgorithmRequest request,
        bool weighed,
        string where,
        Func<string, CancellationToken, Task<GraphDbResult>> run,
        CancellationToken cancellationToken)
    {
        var answer = await run(PathQuery(graph, request.From, request.To, weighed), cancellationToken);

        if (answer.IsError)
            return DatabaseAlgorithmOutcome.Failed($"The database could not run it: {answer.Error}");

        var rows = DatabaseAlgorithms.Rows(answer);

        if (rows.Count == 0)
            return DatabaseAlgorithmOutcome.PathNotFound();

        double? cost = null;

        if (rows[0].TryGetValue("cost", out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            cost = total;

        return DatabaseAlgorithms.Pathed(request, DatabaseAlgorithms.IdsOf(rows[0].GetValueOrDefault("path")), cost, where);
    }

    private static string NewGraphName()
    {
        return $"{GraphPrefix}{Guid.NewGuid():N}";
    }

    //── The queries ────────────────────────────────────────────────────────────────────────────

    ///<summary>
    ///How the projection follows the edges: along them for PageRank, either way for components and communities, and for
    ///degree, betweenness and a path the way the panel's Edges followed says.
    ///</summary>
    public static string Orientation(string algorithm, GraphAlgorithms.Direction direction)
    {
        if (algorithm == AlgorithmNames.PageRank)
            return "NATURAL";
        else if (algorithm == AlgorithmNames.Components || algorithm == AlgorithmNames.Communities)
            return "UNDIRECTED";

        if (direction == GraphAlgorithms.Direction.Out)
            return "NATURAL";
        else if (direction == GraphAlgorithms.Direction.In)
            return "REVERSE";

        return "UNDIRECTED";
    }

    ///<summary>
    ///The whole database graph projected for one run: every label and every relationship type, oriented as the algorithm
    ///needs, and, weighed, the chosen edge property under <see cref="WeightKey"/>, a relationship without it counting one.
    ///</summary>
    public static string ProjectQuery(string graph, string orientation, string weightProperty)
    {
        var properties = "";

        if (!string.IsNullOrWhiteSpace(weightProperty))
            properties = $", properties: {{{WeightKey}: {{property: {Literal(weightProperty.Trim())}, defaultValue: 1.0}}}}";

        return $@"CALL gds.graph.project({Literal(graph)}, '*', {{ALL: {{type: '*', orientation: '{orientation}'{properties}}}}})
YIELD nodeCount, relationshipCount
RETURN nodeCount, relationshipCount";
    }

    ///<summary>The stream a score or group algorithm runs over the projection, coming back for the vertices drawn.</summary>
    public static string StreamQuery(string graph, AlgorithmRequest request, bool weighed, long? sampled)
    {
        if (request.Algorithm == AlgorithmNames.Degree)
            return Stream("gds.degree.stream", graph, "{}", "score", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.PageRank)
            return Stream("gds.pageRank.stream", graph, $"{{dampingFactor: {DatabaseAlgorithms.Number(request.Damping)}, maxIterations: {GraphAlgorithms.PageRankRounds}}}", "score", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.Betweenness)
            return Stream("gds.betweenness.stream", graph, Sampling(sampled), "score", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.Components)
            return Stream("gds.wcc.stream", graph, "{}", "componentId", "groupId", request.Vertices);

        var config = "{}";

        if (weighed)
            config = $"{{relationshipWeightProperty: '{WeightKey}'}}";

        return Stream("gds.louvain.stream", graph, config, "communityId", "groupId", request.Vertices);
    }

    ///<summary>Dijkstra's shortest path over the projection between two vertices named by element id, its vertices' element ids in order and its total cost.</summary>
    public static string PathQuery(string graph, string from, string to, bool weighed)
    {
        var weight = "";

        if (weighed)
            weight = $", relationshipWeightProperty: '{WeightKey}'";

        return $@"MATCH (source) WHERE elementId(source) = {Literal(from)}
MATCH (target) WHERE elementId(target) = {Literal(to)}
CALL gds.shortestPath.dijkstra.stream({Literal(graph)}, {{sourceNode: source, targetNode: target{weight}}})
YIELD totalCost, nodeIds
RETURN totalCost AS cost, [nodeId IN nodeIds | elementId(gds.util.asNode(nodeId))] AS path";
    }

    ///<summary>The run's projection dropped, and no error if it is already gone.</summary>
    public static string DropQuery(string graph)
    {
        return $"CALL gds.graph.drop({Literal(graph)}, false) YIELD graphName RETURN graphName";
    }

    private static string Stream(string procedure, string graph, string config, string column, string alias, IReadOnlyList<DrawnVertex> vertices)
    {
        return $@"CALL {procedure}({Literal(graph)}, {config})
YIELD nodeId, {column}
WITH gds.util.asNode(nodeId) AS node, {column}
WHERE elementId(node) IN [{IdList(vertices)}]
RETURN elementId(node) AS id, {column} AS {alias}";
    }

    private static string Sampling(long? sampled)
    {
        if (sampled == null)
            return "{}";

        return $"{{samplingSize: {sampled.Value.ToString(CultureInfo.InvariantCulture)}, samplingSeed: 0}}";
    }

    ///<summary>The vertices drawn as a Cypher list of their element ids, which is what both Bolt drivers name a node by.</summary>
    public static string IdList(IReadOnlyList<DrawnVertex> vertices)
    {
        return string.Join(", ", vertices.Select(v => Literal(v.Id)));
    }

    private static string Literal(string value)
    {
        return $"'{CypherQueryBuilder.Escape(value)}'";
    }
}
