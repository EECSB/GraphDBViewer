using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>What the Algorithms panel calls each algorithm, in the browser and in a database alike.</summary>
public static class AlgorithmNames
{
    public const string Degree = "degree";
    public const string PageRank = "pagerank";
    public const string Betweenness = "betweenness";
    public const string Components = "components";
    public const string Communities = "communities";

    ///<summary>TinkerPop's community detection, which is not Louvain and is named as what it is.</summary>
    public const string PeerPressure = "peerpressure";

    public const string Path = "path";
}

///<summary>A vertex the canvas draws, as a run in the database names it: its id, and the GraphSON type of the id where the wire typed it.</summary>
public sealed record DrawnVertex(string Id, string? IdType = null);

///<summary>What the panel asks a database to run, from its own choices.</summary>
public sealed class AlgorithmRequest
{
    public string Algorithm { get; init; } = AlgorithmNames.Degree;
    public GraphAlgorithms.Direction Direction { get; init; } = GraphAlgorithms.Direction.Both;
    public double Damping { get; init; } = 0.85;

    ///<summary>The edge property a weighed algorithm reads, or empty for every edge counting one.</summary>
    public string WeightProperty { get; init; } = "";

    ///<summary>A path's ends, by id.</summary>
    public string From { get; init; } = "";
    public string To { get; init; } = "";

    ///<summary>The vertices drawn, in the order given: the ones a result comes back for.</summary>
    public IReadOnlyList<DrawnVertex> Vertices { get; init; } = [];

    public bool Weighed => !string.IsNullOrWhiteSpace(WeightProperty);
}

///<summary>What a run in the database came to: an overlay to draw, or why there is none.</summary>
public sealed class DatabaseAlgorithmOutcome
{
    public AlgorithmOverlay? Overlay { get; private init; }

    ///<summary>Why it gave no result, said for the panel, or null.</summary>
    public string? Error { get; private init; }

    ///<summary>
    ///A path asked for, and none found between the ends. The panel says so itself, in the names the canvas draws the
    ///ends by, which only it knows.
    ///</summary>
    public bool NoPath { get; private init; }

    public static DatabaseAlgorithmOutcome Computed(AlgorithmOverlay overlay)
    {
        return new DatabaseAlgorithmOutcome { Overlay = overlay };
    }

    public static DatabaseAlgorithmOutcome Failed(string error)
    {
        return new DatabaseAlgorithmOutcome { Error = error };
    }

    public static DatabaseAlgorithmOutcome PathNotFound()
    {
        return new DatabaseAlgorithmOutcome { NoPath = true };
    }

    ///<summary>The same outcome with something more the reader should know: a result's note, or an error's last sentence.</summary>
    public DatabaseAlgorithmOutcome With(string? more)
    {
        if (string.IsNullOrEmpty(more))
            return this;

        if (Overlay == null)
            return new DatabaseAlgorithmOutcome { Error = DatabaseAlgorithms.Join(Error, more), NoPath = NoPath };

        Overlay.Note = DatabaseAlgorithms.Join(Overlay.Note, more);

        return this;
    }
}

///<summary>
///A database's own algorithm library (DESIGN.md Part 2, Graph algorithms, tier A): what it offers, named as what it is,
///and how one of them is run there. A run reads: nothing is written to the database, and its numbers come back only for
///the vertices drawn, mapped onto them by id into the same <see cref="AlgorithmOverlay"/> the browser's results are.
///</summary>
public interface IDatabaseAlgorithms
{
    ///<summary>What the library is called where a result says where it was computed: "Neo4j Graph Data Science 2.13.2".</summary>
    string Name { get; }

    ///<summary>The algorithms it has, in the panel's order. Only these are offered.</summary>
    IReadOnlyList<string> Algorithms { get; }

    ///<summary>An algorithm as the panel's choice names it here: "Communities (peer pressure)".</summary>
    string Caption(string algorithm);

    ///<summary>Whether it follows the edges along, against or either way, as the panel's Edges followed chooses.</summary>
    bool Directs(string algorithm);

    ///<summary>Whether it can weigh the edges by a property, as the panel's Edge weight chooses.</summary>
    bool Weighs(string algorithm);

    ///<summary>What it does here, in a sentence or two.</summary>
    string About(string algorithm);

    ///<summary>Runs the algorithm in the database, its queries through <paramref name="run"/>.</summary>
    Task<DatabaseAlgorithmOutcome> RunAsync(AlgorithmRequest request, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken);
}

///<summary>
///Graph algorithms run in the database rather than in the browser (DESIGN.md Part 2, Graph algorithms, tier A): finding
///the library a connection has, and the pieces every library's run shares, which map what came back onto the vertices
///drawn. Tier B, the browser's, is <see cref="GraphAlgorithms"/>; both end in an <see cref="AlgorithmOverlay"/>.
///
///Three libraries: TinkerPop's graph computer on a Gremlin server (<see cref="TinkerPopAlgorithms"/>), Neo4j's Graph Data
///Science (<see cref="GdsAlgorithms"/>) and Memgraph's MAGE (<see cref="MageAlgorithms"/>). Cosmos DB has no graph
///computer, SPARQL no algorithm library, ArangoDB's Pregel is gone since 3.12, and Dgraph and GUN have none: those engines
///are not offered the choice at all.
///</summary>
public static class DatabaseAlgorithms
{
    ///<summary>
    ///Whether a connection to this engine could run algorithms of its own: a Gremlin server, which may have a graph
    ///computer, and Bolt, which may be Neo4j with Graph Data Science or Memgraph with MAGE. Every other engine has none.
    ///</summary>
    public static bool MayHaveLibrary(string databaseType)
    {
        return databaseType == GraphDbProviders.TinkerPop || databaseType == GraphDbProviders.Neo4j;
    }

    ///<summary>
    ///The library a connection has, looked for by a cheap read, or null when it has none. A Gremlin server is asked for an
    ///id or two, which tell JanusGraph, whose graph computer writes what it computes and is offered degree alone
    ///(<see cref="TinkerPopAlgorithms.FindAsync"/>); whether another has a computer shows when one runs, and the server's
    ///own error says it plainly. Bolt is asked for Graph Data Science's version first, and then for Memgraph's procedure
    ///list, which says which of MAGE's algorithms are installed.
    ///</summary>
    public static async Task<IDatabaseAlgorithms?> FindAsync(string databaseType, Func<string, CancellationToken, Task<GraphDbResult>> run, CancellationToken cancellationToken)
    {
        if (!MayHaveLibrary(databaseType))
            return null;

        try
        {
            if (databaseType == GraphDbProviders.TinkerPop)
                return await TinkerPopAlgorithms.FindAsync(run, cancellationToken);

            var gds = await run(GdsAlgorithms.VersionQuery, cancellationToken);

            if (!gds.IsError)
                return GdsAlgorithms.FromVersion(gds);

            var mage = await run(MageAlgorithms.ProceduresQuery, cancellationToken);

            if (!mage.IsError)
                return MageAlgorithms.FromProcedures(mage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            //A probe that could not be asked is a library not found: the browser's algorithms are still there.
        }

        return null;
    }

    ///<summary>
    ///How a run's queries are sent: down the engine's own read-only path where the driver has one, so a library cannot
    ///write to the database whatever it is asked (a READ session on Neo4j and Memgraph, the host's read path behind it),
    ///and as any other query where it has none.
    ///</summary>
    public static Func<string, CancellationToken, Task<GraphDbResult>> ReaderOf(IGraphDb db)
    {
        if (db is IReadOnlyCapableGraphDb reader)
            return (query, token) => reader.ExecuteReadOnlyAsync(query, token);

        return (query, token) => db.ExecuteAsync(query, token);
    }

    //── Mapping what came back onto the vertices drawn ────────────────────────────────────────

    ///<summary>
    ///Scores for the vertices drawn, from what the database answered for them. A vertex it had none for is said in the
    ///note; none at all is an error, since then the graph drawn is not the database's.
    ///</summary>
    public static DatabaseAlgorithmOutcome Scored(AlgorithmRequest request, string name, IReadOnlyDictionary<string, double> answered, string? where, string? note)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var vertex in request.Vertices)
            if (answered.TryGetValue(vertex.Id, out var score))
                scores[vertex.Id] = score;

        if (scores.Count == 0)
            return DatabaseAlgorithmOutcome.Failed(NoneFound);

        var overlay = new AlgorithmOverlay
        {
            Name = name,
            Kind = AlgorithmOverlay.Kinds.Score,
            Scores = scores,
            Where = where,
            Note = Join(note, Unanswered(request, scores.Count, "score"))
        };

        return DatabaseAlgorithmOutcome.Computed(overlay);
    }

    ///<summary>Groups for the vertices drawn, from the database's own group values, numbered as the browser's are (<see cref="NumberedGroups"/>).</summary>
    public static DatabaseAlgorithmOutcome Grouped(AlgorithmRequest request, string name, IReadOnlyDictionary<string, string> answered, string? where, string? note)
    {
        var drawn = request.Vertices.Select(v => v.Id).ToList();
        var groups = NumberedGroups(drawn, answered);

        if (groups.Count == 0)
            return DatabaseAlgorithmOutcome.Failed(NoneFound);

        var overlay = new AlgorithmOverlay
        {
            Name = name,
            Kind = AlgorithmOverlay.Kinds.Group,
            Groups = groups,
            Where = where,
            Note = Join(note, Unanswered(request, groups.Count, "group"))
        };

        return DatabaseAlgorithmOutcome.Computed(overlay);
    }

    ///<summary>
    ///A path the database found, its vertices in order from the first end, drawn where they are drawn: one that runs
    ///through vertices the canvas does not hold is said to. Its cost is what the database said it was, or, where it said
    ///nothing, its edges, the weights' total then unknown.
    ///</summary>
    public static DatabaseAlgorithmOutcome Pathed(AlgorithmRequest request, IReadOnlyList<string> path, double? cost, string? where)
    {
        if (path.Count == 0)
            return DatabaseAlgorithmOutcome.PathNotFound();

        var edges = path.Count - 1;
        var said = Count(edges, "edge", "edges");

        if (request.Weighed && cost != null)
            said += $", {AlgorithmOverlay.Formatted(cost.Value)} in {request.WeightProperty}";
        else if (request.Weighed)
            said += $", by the least total {request.WeightProperty}";

        var drawn = new HashSet<string>(request.Vertices.Select(v => v.Id), StringComparer.Ordinal);
        var away = path.Count(id => !drawn.Contains(id));
        string? note = null;

        if (away > 0)
            note = $"{Count(away, "vertex", "vertices")} of it {Plural(away, "is", "are")} not drawn: load {Plural(away, "it", "them")} to see the whole way.";

        var overlay = new AlgorithmOverlay
        {
            Name = "Path",
            Kind = AlgorithmOverlay.Kinds.Path,
            PathNodes = path.ToList(),
            PathCost = cost ?? edges,
            Where = where,
            Note = Join(said, note)
        };

        return DatabaseAlgorithmOutcome.Computed(overlay);
    }

    ///<summary>
    ///The database's group values numbered as the overlay numbers groups: from the largest among the vertices drawn, 0,
    ///down, and groups of one size in the order their first vertex was given. A vertex with no value is in no group.
    ///</summary>
    public static Dictionary<string, int> NumberedGroups(IReadOnlyList<string> drawn, IReadOnlyDictionary<string, string> groupOf)
    {
        var sizes = new Dictionary<string, (int Size, int First)>(StringComparer.Ordinal);

        for (var i = 0; i < drawn.Count; i++)
        {
            if (!groupOf.TryGetValue(drawn[i], out var group) || group == null)
                continue;

            if (sizes.TryGetValue(group, out var held))
                sizes[group] = (held.Size + 1, held.First);
            else
                sizes[group] = (1, i);
        }

        var number = sizes
            .OrderByDescending(g => g.Value.Size)
            .ThenBy(g => g.Value.First)
            .Select((g, i) => (g.Key, i))
            .ToDictionary(g => g.Key, g => g.i, StringComparer.Ordinal);

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in drawn)
            if (groupOf.TryGetValue(id, out var group) && group != null && number.TryGetValue(group, out var n))
                groups[id] = n;

        return groups;
    }

    //── What the panel says ────────────────────────────────────────────────────────────────────

    ///<summary>None of the vertices drawn came back: the graph drawn is not from this database, or not any longer.</summary>
    public const string NoneFound = "The database has none of the vertices drawn: the graph on the canvas did not come from it, or they are gone from it since.";

    ///<summary>Where a result was computed, by which library and over how many vertices, as the result says it.</summary>
    public static string ComputedOver(string library, long? vertices)
    {
        if (vertices == null)
            return $"Computed in the database by {library}, over the whole of it.";

        return $"Computed in the database by {library}, over all of its {Count(vertices.Value, "vertex", "vertices")}.";
    }

    ///<summary>Where a browser's result was computed, over the vertices drawn.</summary>
    public static string ComputedHere(int vertices)
    {
        return $"Computed in this browser, over the {Count(vertices, "vertex", "vertices")} drawn.";
    }

    ///<summary>"Degree (out)", "Betweenness (in)": an algorithm's name with the way its edges were followed, when that was one way.</summary>
    public static string DirectionSuffix(GraphAlgorithms.Direction direction)
    {
        if (direction == GraphAlgorithms.Direction.Out)
            return " (out)";
        else if (direction == GraphAlgorithms.Direction.In)
            return " (in)";

        return "";
    }

    ///<summary>A number of things in words: "1 edge", "2,000 vertices".</summary>
    public static string Count(long count, string one, string many)
    {
        return $"{count.ToString("N0", CultureInfo.InvariantCulture)} {Plural(count, one, many)}";
    }

    public static string Plural(long count, string one, string many)
    {
        if (count == 1)
            return one;

        return many;
    }

    ///<summary>Two sentences as one note, either of them possibly missing.</summary>
    public static string? Join(string? first, string? second)
    {
        if (string.IsNullOrEmpty(second))
            return first;

        if (string.IsNullOrEmpty(first))
            return second;

        return $"{first.TrimEnd('.')}. {second}";
    }

    ///<summary>A number as a query writes it, whatever the host's culture: 0.85, never 0,85.</summary>
    public static string Number(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    ///<summary>How many vertices drawn the database had nothing for, said, or null when it had something for each.</summary>
    private static string? Unanswered(AlgorithmRequest request, int answered, string what)
    {
        var missing = request.Vertices.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() - answered;

        if (missing <= 0)
            return null;

        return $"{Count(missing, "vertex", "vertices")} drawn {Plural(missing, "is", "are")} not in the database and {Plural(missing, "has", "have")} no {what} from it.";
    }

    //── Reading a Cypher answer ────────────────────────────────────────────────────────────────

    ///<summary>
    ///The rows of a Cypher answer, as both Bolt drivers hand them over (<see cref="Neo4jConverter"/>): a table, each cell
    ///its value's text. An answer with no rows is an empty graph there rather than a table, which reads as no rows here.
    ///</summary>
    public static List<Dictionary<string, string>> Rows(GraphDbResult result)
    {
        if (result.IsError || result.Table == null)
            return [];

        return result.Table.Rows;
    }

    ///<summary>Each row's id and number, for a stream that answered a score per vertex.</summary>
    public static Dictionary<string, double> ScoresOf(GraphDbResult result, string idColumn, string scoreColumn)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var row in Rows(result))
            if (row.TryGetValue(idColumn, out var id)
                && !string.IsNullOrEmpty(id)
                && row.TryGetValue(scoreColumn, out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                scores[id] = score;

        return scores;
    }

    ///<summary>Each row's id and group value, for a stream that answered a group per vertex.</summary>
    public static Dictionary<string, string> GroupsOf(GraphDbResult result, string idColumn, string groupColumn)
    {
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in Rows(result))
            if (row.TryGetValue(idColumn, out var id)
                && !string.IsNullOrEmpty(id)
                && row.TryGetValue(groupColumn, out var group)
                && !string.IsNullOrEmpty(group))
                groups[id] = group;

        return groups;
    }

    ///<summary>A path's vertex ids from a cell holding them as a list, which the drivers hand over as its JSON.</summary>
    public static List<string> IdsOf(string? cell)
    {
        var ids = new List<string>();

        if (string.IsNullOrWhiteSpace(cell))
            return ids;

        try
        {
            using var doc = JsonDocument.Parse(cell);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = GraphWireText.Stringify(item);

                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }
        catch (JsonException)
        {
        }

        return ids;
    }

    ///<summary>A count in the first cell of the first row, or null.</summary>
    public static long? FirstCount(GraphDbResult result, string column)
    {
        foreach (var row in Rows(result))
            if (row.TryGetValue(column, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return count;

        return null;
    }
}
