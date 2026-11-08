using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Memgraph's MAGE library (DESIGN.md Part 2, Graph algorithms, tier A): degree centrality, PageRank, betweenness,
///weakly connected components and Louvain community detection, each a procedure called over the whole database graph,
///and the shortest path in Memgraph's own Cypher: <c>*BFS</c> for the fewest edges, <c>*WSHORTEST</c> for the least
///total weight. Only the algorithms whose procedures Memgraph lists are offered (<see cref="FromProcedures"/>), since
///MAGE's modules are installed one by one; the path is Memgraph's own and comes with any of them.
///
///A procedure answers a row per vertex of the whole graph, and the query keeps the rows of the vertices drawn, by their
///element ids, which Memgraph gives as the text of a vertex's id. MAGE's procedures here only read: nothing is written.
///</summary>
public sealed class MageAlgorithms : IDatabaseAlgorithms
{
    ///<summary>The MAGE procedure each algorithm is, in the panel's order.</summary>
    private static readonly (string Algorithm, string Procedure)[] Procedures =
    [
        (AlgorithmNames.Degree, "degree_centrality.get"),
        (AlgorithmNames.PageRank, "pagerank.get"),
        (AlgorithmNames.Betweenness, "betweenness_centrality.get"),
        (AlgorithmNames.Components, "weakly_connected_components.get"),
        (AlgorithmNames.Communities, "community_detection.get")
    ];

    ///<summary>Every vertex counted, to say what a run covered.</summary>
    public const string CountQuery = "MATCH (n) RETURN count(n) AS vertices";

    ///<summary>
    ///What community detection is told to weigh by when every edge counts one. It weighs by an edge property named
    ///"weight" unless named another, and counts an edge without the named property as one, so a property no edge has
    ///is how its own default is set aside.
    ///</summary>
    public const string UnweightedKey = "gdbvNoWeight";

    public MageAlgorithms(IEnumerable<string>? algorithms)
    {
        var found = new HashSet<string>(algorithms ?? [], StringComparer.Ordinal);
        var offered = Procedures.Select(p => p.Algorithm).Where(found.Contains).ToList();

        offered.Add(AlgorithmNames.Path);
        Algorithms = offered;
    }

    ///<summary>
    ///Asked of a Bolt connection once Graph Data Science was not found: Memgraph lists its procedures, and which of MAGE's
    ///are among them says which algorithms it has. Neo4j has no mg.procedures and refuses it.
    ///</summary>
    public static string ProceduresQuery
    {
        get
        {
            var names = string.Join(", ", Procedures.Select(p => $"'{p.Procedure}'"));

            return $"CALL mg.procedures() YIELD name WITH name WHERE name IN [{names}] RETURN name";
        }
    }

    ///<summary>The library as Memgraph's procedure list has it, or null when none of MAGE's algorithms are installed.</summary>
    public static MageAlgorithms? FromProcedures(GraphDbResult result)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in DatabaseAlgorithms.Rows(result))
            if (row.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
                listed.Add(name);

        var algorithms = Procedures.Where(p => listed.Contains(p.Procedure)).Select(p => p.Algorithm).ToList();

        if (algorithms.Count == 0)
            return null;

        return new MageAlgorithms(algorithms);
    }

    public string Name => "Memgraph MAGE";

    public IReadOnlyList<string> Algorithms { get; }

    public string Caption(string algorithm)
    {
        if (algorithm == AlgorithmNames.Degree)
            return "Degree centrality";
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank";
        else if (algorithm == AlgorithmNames.Betweenness)
            return "Betweenness";
        else if (algorithm == AlgorithmNames.Components)
            return "Connected components (weakly)";
        else if (algorithm == AlgorithmNames.Communities)
            return "Communities (Louvain)";

        return "Shortest path";
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
            return "Each vertex's edges in the whole graph, as MAGE's degree centrality gives them: a share of the other vertices it could be linked to.";
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank by MAGE over the whole graph, along the edges' direction. The scores add up to one over the whole graph, and those of the vertices drawn come back.";
        else if (algorithm == AlgorithmNames.Betweenness)
            return "How many shortest paths between other vertices of the whole graph pass through each, by MAGE: the brokers. Along the edges or either way; against them is the same as along.";
        else if (algorithm == AlgorithmNames.Components)
            return "The pieces the whole graph falls into, the edges' direction ignored, by MAGE's weakly connected components.";
        else if (algorithm == AlgorithmNames.Communities)
            return "Groups more tightly linked among themselves than to the rest of the whole graph, by MAGE's community detection (Louvain), the edges' direction ignored.";

        return "The shortest way from one vertex to another through the whole graph, in Memgraph's own Cypher: the fewest edges, or the least total weight. The rest of the graph is dimmed.";
    }

    public async Task<DatabaseAlgorithmOutcome> RunAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        if (!Algorithms.Contains(request.Algorithm))
            return DatabaseAlgorithmOutcome.Failed($"{Name} has no {request.Algorithm}: run it in this browser.");

        if (request.Algorithm == AlgorithmNames.Path)
            return await PathAsync(request, run, cancellationToken);

        var answer = await run(Query(request), cancellationToken);

        if (answer.IsError)
            return DatabaseAlgorithmOutcome.Failed($"The database could not run it: {answer.Error}");

        var suffix = DatabaseAlgorithms.DirectionSuffix(request.Direction);
        DatabaseAlgorithmOutcome outcome;

        if (request.Algorithm == AlgorithmNames.Degree)
            outcome = DatabaseAlgorithms.Scored(request, $"Degree centrality{suffix}", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), null, null);
        else if (request.Algorithm == AlgorithmNames.PageRank)
            outcome = DatabaseAlgorithms.Scored(request, "PageRank", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), null, null);
        else if (request.Algorithm == AlgorithmNames.Betweenness)
            outcome = DatabaseAlgorithms.Scored(request, $"Betweenness{suffix}", DatabaseAlgorithms.ScoresOf(answer, "id", "score"), null, null);
        else if (request.Algorithm == AlgorithmNames.Components)
            outcome = DatabaseAlgorithms.Grouped(request, "Component", DatabaseAlgorithms.GroupsOf(answer, "id", "groupId"), null, null);
        else
            outcome = DatabaseAlgorithms.Grouped(request, "Community", Alone(DatabaseAlgorithms.GroupsOf(answer, "id", "groupId")), null, null);

        if (outcome.Overlay != null)
            outcome.Overlay.Where = DatabaseAlgorithms.ComputedOver(Name, await CountAsync(run, cancellationToken));

        return outcome;
    }

    private async Task<DatabaseAlgorithmOutcome> PathAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        var answer = await run(PathQuery(request.From, request.To, request.Direction, request.WeightProperty), cancellationToken);

        if (answer.IsError)
            return DatabaseAlgorithmOutcome.Failed($"The database could not run it: {answer.Error}");

        var rows = DatabaseAlgorithms.Rows(answer);

        if (rows.Count == 0)
            return DatabaseAlgorithmOutcome.PathNotFound();

        double? cost = null;

        if (rows[0].TryGetValue("cost", out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            cost = total;

        return DatabaseAlgorithms.Pathed(request, DatabaseAlgorithms.IdsOf(rows[0].GetValueOrDefault("path")), cost, DatabaseAlgorithms.ComputedOver(Name, await CountAsync(run, cancellationToken)));
    }

    private static async Task<long?> CountAsync(Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        return DatabaseAlgorithms.FirstCount(await run(CountQuery, cancellationToken), "vertices");
    }

    ///<summary>
    ///Community detection's answer with each vertex it put in no community (-1) in one of its own, as a group of one: it
    ///is not in a group with the others it left out.
    ///</summary>
    private static Dictionary<string, string> Alone(Dictionary<string, string> groups)
    {
        foreach (var id in groups.Where(g => g.Value == "-1").Select(g => g.Key).ToList())
            groups[id] = $"alone:{id}";

        return groups;
    }

    //── The queries ────────────────────────────────────────────────────────────────────────────

    ///<summary>The call a score or group algorithm makes, coming back for the vertices drawn.</summary>
    public static string Query(AlgorithmRequest request)
    {
        if (request.Algorithm == AlgorithmNames.Degree)
            return Stream($"degree_centrality.get('{DegreeType(request.Direction)}')", "degree", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.PageRank)
            return Stream($"pagerank.get({GraphAlgorithms.PageRankRounds}, {DatabaseAlgorithms.Number(request.Damping)})", "rank", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.Betweenness)
            return Stream($"betweenness_centrality.get({Directed(request.Direction)}, false)", "betweenness_centrality", "score", request.Vertices);
        else if (request.Algorithm == AlgorithmNames.Components)
            return Stream("weakly_connected_components.get()", "component_id", "groupId", request.Vertices);

        var weight = UnweightedKey;

        if (request.Weighed)
            weight = request.WeightProperty.Trim();

        return Stream($"community_detection.get({Literal(weight)})", "community_id", "groupId", request.Vertices);
    }

    ///<summary>
    ///A shortest path between two vertices named by element id through the whole graph, in Memgraph's own syntax, the
    ///way the direction allows: breadth first for the fewest edges, or weighed by an edge property, an edge without a
    ///number in it counting one, as it does in the browser.
    ///</summary>
    public static string PathQuery(string from, string to, GraphAlgorithms.Direction direction, string weightProperty)
    {
        var expansion = "*BFS";
        var cost = "size(relationships(found))";

        //Memgraph weighs the start of a path too, with no relationship in r: given the default of one there, every path
        //cost one more than its edges (found live on Memgraph 3.13, 2026-10-23). The start weighs nothing.
        if (!string.IsNullOrWhiteSpace(weightProperty))
        {
            expansion = $"*WSHORTEST (r, n | CASE WHEN r IS NULL THEN 0 ELSE coalesce(toFloatOrNull(r.{CypherQueryBuilder.QuoteIdentifier(weightProperty)}), 1.0) END) total_weight";
            cost = "total_weight";
        }

        return $@"MATCH (source) WHERE elementId(source) = {Literal(from)}
MATCH (target) WHERE elementId(target) = {Literal(to)}
MATCH found = (source){Pattern(direction, expansion)}(target)
RETURN [vertex IN nodes(found) | elementId(vertex)] AS path, {cost} AS cost";
    }

    private static string Stream(string call, string column, string alias, IReadOnlyList<DrawnVertex> vertices)
    {
        return $@"CALL {call}
YIELD node, {column}
WITH node, {column}
WHERE elementId(node) IN [{GdsAlgorithms.IdList(vertices)}]
RETURN elementId(node) AS id, {column} AS {alias}";
    }

    private static string Pattern(GraphAlgorithms.Direction direction, string expansion)
    {
        if (direction == GraphAlgorithms.Direction.Out)
            return $"-[{expansion}]->";
        else if (direction == GraphAlgorithms.Direction.In)
            return $"<-[{expansion}]-";

        return $"-[{expansion}]-";
    }

    private static string DegreeType(GraphAlgorithms.Direction direction)
    {
        if (direction == GraphAlgorithms.Direction.Out)
            return "out";
        else if (direction == GraphAlgorithms.Direction.In)
            return "in";

        return "undirected";
    }

    ///<summary>
    ///MAGE's betweenness follows the edges or ignores them. Against them it is followed along them, which counts the same:
    ///every shortest path reversed is a shortest path the other way, and every pair is counted either way round.
    ///</summary>
    private static string Directed(GraphAlgorithms.Direction direction)
    {
        if (direction == GraphAlgorithms.Direction.Both)
            return "false";

        return "true";
    }

    private static string Literal(string value)
    {
        return $"'{CypherQueryBuilder.Escape(value)}'";
    }
}
