using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///TinkerPop's own algorithms (DESIGN.md Part 2, Graph algorithms, tier A): the OLAP steps a Gremlin server runs on its
///graph computer, over the whole graph: <c>pageRank()</c>, <c>connectedComponent()</c>, <c>peerPressure()</c> and
///<c>shortestPath()</c>. A server with no computer refuses them, and the panel says so in plain words beside the server's
///own (<see cref="Explain"/>). Degree needs no computer: it is each drawn vertex's edges, counted where they are stored.
///
///A step holds its value under a key of its own (<see cref="ScoreKey"/>, <see cref="GroupKey"/>), which the traversal
///reads back for the vertices drawn, filtered by their ids as the wire typed them (4128L for a long). TinkerGraph's
///computer keeps it in a view of its own, and nothing is written to the graph (checked live, 2026-10-23). JanusGraph's
///FulgoraGraphComputer writes it into the graph: run over the development graph the same day, PageRank and components
///left a score and a group on every vertex and the path its traversers on its start, and seven property keys in the
///schema, which JanusGraph cannot drop. So JanusGraph, told by the RelationIdentifier it types a property's and an edge's
///id as, is offered degree alone (<see cref="JanusGraph"/>), and so is a server that cannot be told (<see cref="Untold"/>):
///the computer's algorithms only where a read showed the graph is not JanusGraph's (<see cref="FindAsync"/>). TinkerPop
///has no betweenness and no Louvain: its community detection is peer pressure, named as what it is.
///</summary>
public sealed class TinkerPopAlgorithms : IDatabaseAlgorithms
{
    ///<summary>The graph computer's algorithms and degree: a server whose graph a read showed is not JanusGraph's.</summary>
    public static readonly TinkerPopAlgorithms Instance = new(
        "TinkerPop's graph computer",
        [AlgorithmNames.Degree, AlgorithmNames.PageRank, AlgorithmNames.Components, AlgorithmNames.PeerPressure, AlgorithmNames.Path],
        null);

    ///<summary>JanusGraph, whose graph computer writes what it computes into the graph: degree alone, which reads.</summary>
    public static readonly TinkerPopAlgorithms JanusGraph = new(
        "JanusGraph",
        [AlgorithmNames.Degree],
        "JanusGraph's graph computer writes what it computes into the graph, so its PageRank, components and paths are not run here: run those in this browser.");

    ///<summary>A server that could not be told from JanusGraph: degree alone, rather than risk its computer writing.</summary>
    public static readonly TinkerPopAlgorithms Untold = new(
        "this Gremlin server",
        [AlgorithmNames.Degree],
        "Whether this server's graph computer would write what it computes into the graph could not be told, so its PageRank, components and paths are not run here: run those in this browser.");

    private readonly string? _whyDegreeAlone;

    private TinkerPopAlgorithms(string name, IReadOnlyList<string> algorithms, string? whyDegreeAlone)
    {
        Name = name;
        Algorithms = algorithms;
        _whyDegreeAlone = whyDegreeAlone;
    }

    ///<summary>
    ///What a Gremlin server is offered, told by a read or two: a vertex property's id, or failing one an edge's. JanusGraph
    ///types both as a RelationIdentifier and is offered degree alone; any other id shows a graph whose computer can be
    ///offered; neither, or a refusal, cannot tell, and is offered degree alone too.
    ///</summary>
    public static async Task<TinkerPopAlgorithms> FindAsync(Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        var told = Tell(await run(PropertyIdQuery, cancellationToken));

        if (told == null)
            told = Tell(await run(EdgeIdQuery, cancellationToken));

        if (told == null)
            return Untold;
        else if (told.Value)
            return JanusGraph;

        return Instance;
    }

    ///<summary>Whether an id read back is JanusGraph's, or null when there was none to tell by.</summary>
    private static bool? Tell(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array || result.Data.GetArrayLength() == 0)
            return null;

        return GraphDataConverter.IsRelationIdentifier(result.Data[0]);
    }

    ///<summary>The key a score is held under while the traversal runs: read back, never written.</summary>
    public const string ScoreKey = "gdbvScore";

    ///<summary>The key a group is held under while the traversal runs.</summary>
    public const string GroupKey = "gdbvGroup";

    ///<summary>Every vertex counted, to say what a run covered: the whole graph, which is where it ran.</summary>
    public const string CountQuery = "g.V().count()";

    ///<summary>A vertex property's id, asked for to tell JanusGraph, which types it as a RelationIdentifier.</summary>
    public const string PropertyIdQuery = "g.V().properties().limit(1).id()";

    ///<summary>An edge's id, asked for when no vertex has a property, for the same reason.</summary>
    public const string EdgeIdQuery = "g.E().limit(1).id()";

    ///<summary>
    ///How many vertices drawn one query names at most. A Groovy script compiles into one method of at most 64 KB, and
    ///Gremlin Server 3.7 took 3,000 long ids in a list and refused 3,500 (2026-10-23); more are asked for in turn.
    ///</summary>
    public const int MaxIdsPerQuery = 2500;

    ///<summary>What a degree counted, said where the result says where it was computed.</summary>
    public const string CountedWhere = "Counted in the database: every edge each vertex drawn has there, drawn or not.";

    public string Name { get; }

    public IReadOnlyList<string> Algorithms { get; }

    public string Caption(string algorithm)
    {
        if (algorithm == AlgorithmNames.Degree)
            return "Degree";
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank";
        else if (algorithm == AlgorithmNames.Components)
            return "Connected components";
        else if (algorithm == AlgorithmNames.PeerPressure)
            return "Communities (peer pressure)";

        return "Shortest path";
    }

    public bool Directs(string algorithm)
    {
        return algorithm == AlgorithmNames.Degree || algorithm == AlgorithmNames.Path;
    }

    public bool Weighs(string algorithm)
    {
        return algorithm == AlgorithmNames.Path;
    }

    public string About(string algorithm)
    {
        if (algorithm == AlgorithmNames.Degree)
            return DatabaseAlgorithms.Join("How many edges each vertex drawn has in the database, drawn here or not, counted where they are stored. It needs no graph computer.", _whyDegreeAlone);
        else if (algorithm == AlgorithmNames.PageRank)
            return "PageRank by the server's graph computer, along the edges' direction, over the whole graph. The scores of the vertices drawn come back.";
        else if (algorithm == AlgorithmNames.Components)
            return "The pieces the whole graph falls into, the edges' direction ignored, by the server's graph computer. Each piece the vertices drawn are in is drawn in a color of its own.";
        else if (algorithm == AlgorithmNames.PeerPressure)
            return "Clusters by peer pressure, TinkerPop's community detection, which is not Louvain: each vertex joins the cluster most of its neighbors are in, round after round, the edges' direction ignored.";

        return "The shortest way from one vertex to another through the whole graph, by the server's graph computer: the fewest edges, or the least total weight. The rest of the graph is dimmed.";
    }

    public async Task<DatabaseAlgorithmOutcome> RunAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        if (!Algorithms.Contains(request.Algorithm))
            return DatabaseAlgorithmOutcome.Failed($"{Name} has no {request.Algorithm}: run it in this browser.");

        if (request.Algorithm == AlgorithmNames.Path)
            return await PathAsync(request, run, cancellationToken);

        //Asked for in turn when one query cannot name every vertex drawn (MaxIdsPerQuery), and the answers merged.
        var chunks = request.Vertices.Chunk(MaxIdsPerQuery).ToList();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var chunk in chunks)
        {
            var answer = await run(Query(request, chunk), cancellationToken);

            if (answer.IsError)
                return DatabaseAlgorithmOutcome.Failed(Explain(answer.Error));

            foreach (var (id, score) in ParseScores(answer))
                scores[id] = score;

            foreach (var (id, group) in ParseGroups(answer))
                groups[id] = group;
        }

        string? note = null;

        if (chunks.Count > 1)
            note = $"Asked for {chunks.Count} times, {MaxIdsPerQuery.ToString("N0", CultureInfo.InvariantCulture)} vertices drawn at a time, since one Gremlin query can name only so many.";

        if (request.Algorithm == AlgorithmNames.Degree)
            return DatabaseAlgorithms.Scored(request, $"Degree{DatabaseAlgorithms.DirectionSuffix(request.Direction)}", scores, CountedWhere, note);

        DatabaseAlgorithmOutcome outcome;

        if (request.Algorithm == AlgorithmNames.PageRank)
            outcome = DatabaseAlgorithms.Scored(request, "PageRank", scores, null, note);
        else if (request.Algorithm == AlgorithmNames.Components)
            outcome = DatabaseAlgorithms.Grouped(request, "Component", groups, null, note);
        else
            outcome = DatabaseAlgorithms.Grouped(request, "Cluster (peer pressure)", groups, null, note);

        if (outcome.Overlay != null)
            outcome.Overlay.Where = DatabaseAlgorithms.ComputedOver(Name, await CountAsync(run, cancellationToken));

        return outcome;
    }

    private async Task<DatabaseAlgorithmOutcome> PathAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        var answer = await run(PathQuery(Drawn(request, request.From), Drawn(request, request.To), request.Direction, request.WeightProperty), cancellationToken);

        if (answer.IsError)
            return DatabaseAlgorithmOutcome.Failed(Explain(answer.Error));

        var path = ParsePath(answer);

        if (path.Count == 0)
            return DatabaseAlgorithmOutcome.PathNotFound();

        //A path's weights' total is not in what shortestPath() answers, so a weighed one says its edges alone.
        return DatabaseAlgorithms.Pathed(request, path, null, DatabaseAlgorithms.ComputedOver(Name, await CountAsync(run, cancellationToken)));
    }

    ///<summary>How many vertices the graph has, or null when the server would not count them (a JanusGraph that forces an index refuses a full count).</summary>
    private static async Task<long?> CountAsync(Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        return ParseCount(await run(CountQuery, cancellationToken));
    }

    ///<summary>A path's end as the vertex drawn, its id's type with it, or the id alone when it is not drawn.</summary>
    private static DrawnVertex Drawn(AlgorithmRequest request, string id)
    {
        return request.Vertices.FirstOrDefault(v => v.Id == id) ?? new DrawnVertex(id);
    }

    //── The queries ────────────────────────────────────────────────────────────────────────────

    ///<summary>The query a score or group algorithm runs, for these of the vertices drawn.</summary>
    public static string Query(AlgorithmRequest request, IReadOnlyList<DrawnVertex> vertices)
    {
        if (request.Algorithm == AlgorithmNames.Degree)
            return DegreeQuery(vertices, request.Direction);
        else if (request.Algorithm == AlgorithmNames.PageRank)
            return PageRankQuery(vertices, request.Damping);
        else if (request.Algorithm == AlgorithmNames.Components)
            return ComponentsQuery(vertices);

        return PeerPressureQuery(vertices);
    }

    ///<summary>
    ///The vertices drawn as one Gremlin list, <c>within([1L, 2L, 'marko'])</c>, each id written as its GraphSON type
    ///says. One list, not an argument per id: a Groovy call takes at most 255 argument slots and a long takes two, so
    ///Gremlin Server refused <c>hasId()</c> with "bad parameter count" past about 127 ids (found live, 2026-10-23). A
    ///list is limited only by the 64 KB a script compiles into, which <see cref="MaxIdsPerQuery"/> keeps under.
    ///</summary>
    public static string IdFilter(IReadOnlyList<DrawnVertex> vertices)
    {
        var ids = string.Join(", ", vertices.Select(v => GremlinQueryBuilder.FormatId(v.Id, v.IdType)));

        return $"within([{ids}])";
    }

    ///<summary>Each vertex drawn, with its edges counted where they are stored: out, in, or both. An OLTP traversal, which every Gremlin server runs.</summary>
    public static string DegreeQuery(IReadOnlyList<DrawnVertex> vertices, GraphAlgorithms.Direction direction)
    {
        return $"g.V().hasId({IdFilter(vertices)}).project('id', 'score').by(__.id()).by(__.{EdgeStep(direction)}().count())";
    }

    ///<summary>PageRank over the whole graph along the edges' direction, read back for the vertices drawn.</summary>
    public static string PageRankQuery(IReadOnlyList<DrawnVertex> vertices, double damping)
    {
        return $"g.withComputer().V().pageRank({DatabaseAlgorithms.Number(damping)}d).with(PageRank.edges, __.outE()).with(PageRank.propertyName, '{ScoreKey}').hasId({IdFilter(vertices)}).project('id', 'score').by(__.id()).by(__.values('{ScoreKey}'))";
    }

    ///<summary>The whole graph's connected components, the edges' direction ignored, read back for the vertices drawn.</summary>
    public static string ComponentsQuery(IReadOnlyList<DrawnVertex> vertices)
    {
        return $"g.withComputer().V().connectedComponent().with(ConnectedComponent.edges, __.bothE()).with(ConnectedComponent.propertyName, '{GroupKey}').hasId({IdFilter(vertices)}).project('id', 'group').by(__.id()).by(__.values('{GroupKey}'))";
    }

    ///<summary>The whole graph's peer-pressure clusters, the edges' direction ignored, read back for the vertices drawn.</summary>
    public static string PeerPressureQuery(IReadOnlyList<DrawnVertex> vertices)
    {
        return $"g.withComputer().V().peerPressure().with(PeerPressure.edges, __.bothE()).with(PeerPressure.propertyName, '{GroupKey}').hasId({IdFilter(vertices)}).project('id', 'group').by(__.id()).by(__.values('{GroupKey}'))";
    }

    ///<summary>
    ///A shortest path between two vertices through the whole graph, the way the direction allows. Weighed by an edge
    ///property, an edge without it counts one, as it does in the browser.
    ///</summary>
    public static string PathQuery(DrawnVertex from, DrawnVertex to, GraphAlgorithms.Direction direction, string weightProperty)
    {
        var source = GremlinQueryBuilder.FormatId(from.Id, from.IdType);
        var target = GremlinQueryBuilder.FormatId(to.Id, to.IdType);
        var query = $"g.withComputer().V({source}).shortestPath().with(ShortestPath.edges, Direction.{DirectionToken(direction)}).with(ShortestPath.target, __.hasId({target}))";

        if (string.IsNullOrWhiteSpace(weightProperty))
            return query;

        return $"{query}.with(ShortestPath.distance, __.coalesce(__.values('{GremlinQueryBuilder.Escape(weightProperty.Trim())}'), __.constant(1)))";
    }

    private static string EdgeStep(GraphAlgorithms.Direction direction)
    {
        if (direction == GraphAlgorithms.Direction.Out)
            return "outE";
        else if (direction == GraphAlgorithms.Direction.In)
            return "inE";

        return "bothE";
    }

    private static string DirectionToken(GraphAlgorithms.Direction direction)
    {
        if (direction == GraphAlgorithms.Direction.Out)
            return "OUT";
        else if (direction == GraphAlgorithms.Direction.In)
            return "IN";

        return "BOTH";
    }

    //── Reading the answers ────────────────────────────────────────────────────────────────────

    ///<summary>
    ///Each vertex's score from a project('id', 'score') answer, as GremlinDB hands it over: the rows GraphSON 3 typed (a
    ///g:Map of a typed id and a g:Double), or plain. The id is read as the canvas reads one (<see cref="GraphDataConverter.IdText"/>).
    ///</summary>
    public static Dictionary<string, double> ParseScores(GraphDbResult result)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var row in Rows(result))
        {
            if (!row.TryGetProperty("id", out var id) || !row.TryGetProperty("score", out var score))
                continue;

            var number = GraphDataConverter.UnwrapElement(score);

            if (number.ValueKind == JsonValueKind.Number && number.TryGetDouble(out var value))
                scores[GraphDataConverter.IdText(id)] = value;
        }

        return scores;
    }

    ///<summary>Each vertex's group value from a project('id', 'group') answer: a component's smallest id, or the vertex a peer-pressure cluster is named by.</summary>
    public static Dictionary<string, string> ParseGroups(GraphDbResult result)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in Rows(result))
            if (row.TryGetProperty("id", out var id) && row.TryGetProperty("group", out var group))
                groups[GraphDataConverter.IdText(id)] = GraphDataConverter.IdText(group);

        return groups;
    }

    ///<summary>The first path's vertex ids, in order from the first end, from shortestPath()'s answer: g:Path objects, each holding the vertices it went through.</summary>
    public static List<string> ParsePath(GraphDbResult result)
    {
        foreach (var row in Rows(result))
        {
            if (!row.TryGetProperty("objects", out var objects))
                continue;

            var list = GraphDataConverter.UnwrapElement(objects);

            if (list.ValueKind != JsonValueKind.Array)
                continue;

            var ids = new List<string>();

            foreach (var item in list.EnumerateArray())
            {
                var vertex = GraphDataConverter.UnwrapElement(item);

                if (vertex.ValueKind == JsonValueKind.Object && vertex.TryGetProperty("id", out var id))
                    ids.Add(GraphDataConverter.IdText(id));
            }

            if (ids.Count > 0)
                return ids;
        }

        return [];
    }

    ///<summary>A count's answer, a typed long, or null when there is none.</summary>
    public static long? ParseCount(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in result.Data.EnumerateArray())
        {
            var number = GraphDataConverter.UnwrapElement(item);

            if (number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var count))
                return count;
        }

        return null;
    }

    ///<summary>The answer's rows, each unwrapped from its GraphSON type into the object it stands for.</summary>
    private static IEnumerable<JsonElement> Rows(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in result.Data.EnumerateArray())
        {
            var row = GraphDataConverter.UnwrapElement(item);

            if (row.ValueKind == JsonValueKind.Object)
                yield return row;
        }
    }

    ///<summary>
    ///What a refused run says. A server with no graph computer is said plainly, since that refusal has a remedy, the
    ///browser's algorithms; every refusal carries the server's own words.
    ///</summary>
    public static string Explain(string error)
    {
        var said = (error ?? "").Trim();

        if (HasNoComputer(said))
            return $"This Gremlin server has no graph computer, which TinkerPop's algorithms run on, so they cannot run in this database: run them in this browser instead. The server said: {said}";

        return $"The database could not run it: {said}";
    }

    ///<summary>Whether an error is a server saying it has no graph computer: "Graph does not support graph computer", and the like.</summary>
    private static bool HasNoComputer(string error)
    {
        if (!error.Contains("computer", StringComparison.OrdinalIgnoreCase))
            return false;

        return error.Contains("not support", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not available", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no graph computer", StringComparison.OrdinalIgnoreCase);
    }
}
