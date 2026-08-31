using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///The DQL half of <see cref="IGraphQueryBuilder"/> — what lets the Dgraph provider switch its browse,
///traverse and stage-edits <see cref="GraphDbCapabilities"/> on.
///
///Dgraph shapes this differently from every other engine here in three ways:
///
///  * <b>Reads and writes are different languages.</b> A read is DQL; a write is a <b>JSON mutation</b>
///    posted to a different endpoint. So what this builder stages is not more query text — it is
///    <c>{"set":[{"uid":"0x1","name":"Alice"}]}</c>, and <see cref="DgraphDb"/> routes on the shape.
///    (The RDF N-Quad form would have been the obvious choice and cannot be used: Dgraph requires a
///    newline after every triple, and the staged buffer commits by splitting on newlines, so a two-triple
///    mutation would be torn in half. JSON says the same thing on one line.)
///  * <b>A query needs a root.</b> There is no "scan everything": every block starts from a function, so
///    "load the database" roots on <c>has(dgraph.type)</c> and the predicates to ask for have to be
///    spelled out — which is why this builder is built from the connected schema rather than being a
///    stateless singleton, as ArangoDB's is for its own reasons.
///  * <b>There are no edge objects.</b> An edge is a predicate holding a node, so it has no identity and
///    nowhere to keep a property. Dgraph's answer to that is facets, which the viewer does not read back,
///    so an edge property refuses rather than being written somewhere it would never be seen again.
///</summary>
public sealed class DqlQueryBuilder : IGraphQueryBuilder
{
    ///<summary>A builder with no schema yet: edits work, browse and traverse have nothing to name.</summary>
    public static readonly DqlQueryBuilder Empty = new(null, null);

    ///<summary>Predicate the viewer writes an imported node's original id into.</summary>
    public const string ImportIdPredicate = "gdbvId";

    ///<summary>How a node's display name is read, and what an added node's name is written to.</summary>
    public const string NamePredicate = "name";

    //What every node in an answer carries: its id, its type, and enough to name it on the canvas.
    private const string NodeHeader = "uid dgraph.type";

    private readonly List<string> _scalarPredicates;
    private readonly List<string> _edgePredicates;

    public DqlQueryBuilder(IEnumerable<string> scalarPredicates, IEnumerable<string> edgePredicates)
    {
        _scalarPredicates = Clean(scalarPredicates);
        _edgePredicates = Clean(edgePredicates);
    }

    ///<summary>The value predicates a browse query asks for.</summary>
    public IReadOnlyList<string> ScalarPredicates => _scalarPredicates;

    ///<summary>The <c>uid</c> predicates — Dgraph's edges — that traversals follow.</summary>
    public IReadOnlyList<string> EdgePredicates => _edgePredicates;

    ///<summary>True once the schema is known; before that there are no predicates to ask for.</summary>
    public bool HasSchema => _scalarPredicates.Count > 0 || _edgePredicates.Count > 0;

    private static List<string> Clean(IEnumerable<string> names)
    {
        var cleaned = new List<string>();

        if (names == null)
            return cleaned;

        foreach (var name in names)
        {
            //Dgraph's own predicates describe the cluster, not the user's graph.
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("dgraph.", StringComparison.Ordinal))
                continue;

            if (!cleaned.Contains(name))
                cleaned.Add(name);
        }

        return cleaned;
    }

    //── Browse ──────────────────────────────────────────────────────────

    ///<summary>
    ///The first <paramref name="limit"/> nodes with their values but not their edges.
    ///
    ///Rooted on <c>has(dgraph.type)</c>: Dgraph has no way to enumerate everything, so a query starts from
    ///a function, and the type predicate is the one thing every node the viewer writes carries. A node
    ///that declares no type is genuinely unreachable this way — which is Dgraph's model, not a shortcut.
    ///</summary>
    public string LimitedVertices(int limit)
    {
        return $"{{ nodes(func: has(dgraph.type){First(limit)}) {{ {NodeHeader}{Values()} }} }}";
    }

    ///<summary>The same nodes, with each edge predicate followed one hop so the graph has its edges.</summary>
    public string FullGraph(int? limit)
    {
        return $"{{ nodes(func: has(dgraph.type){First(limit)}) {{ {NodeHeader}{Values()}{Edges()} }} }}";
    }

    //── Traverse ────────────────────────────────────────────────────────

    ///<summary>
    ///The node with everything around it — its own edges out, and the nodes pointing at it. The second half
    ///is a block per edge predicate: Dgraph can only walk an edge backwards when the schema declared it
    ///<c>@reverse</c>, so <c>uid_in</c> is used instead, which works on any predicate.
    ///</summary>
    public string Neighbors(string id, int limit)
    {
        var blocks = new List<string> { $"neighbors(func: uid({Uid(id)})){Exists()} {{ {NodeHeader}{Values()}{Edges()} }}" };

        blocks.AddRange(IncomingBlocks(id));

        return "{ " + string.Join(" ", blocks) + " }";
    }

    ///<summary>
    ///The nodes pointing at this one, each carrying the link back so the answer reads as edges rather than
    ///as unrelated nodes. Empty when the schema names no edge predicates — there is nothing to search.
    ///</summary>
    public string InEdges(string vertexId)
    {
        var blocks = IncomingBlocks(vertexId);

        if (blocks.Count == 0)
            return EmptyQuery(vertexId);

        return "{ " + string.Join(" ", blocks) + " }";
    }

    public string OutEdges(string vertexId)
    {
        return $"{{ outgoing(func: uid({Uid(vertexId)})){Exists()} {{ {NodeHeader}{Values()}{Edges()} }} }}";
    }

    public string VertexDisplayLabel(string vertexId)
    {
        return $"{{ label(func: uid({Uid(vertexId)})){Exists()} {{ {NodeHeader} {NamePredicate} }} }}";
    }

    //One block per edge predicate: everything that has it, filtered to the rows whose predicate includes
    //this node, and re-stating that link so the converter sees the edge rather than a loose node.
    private List<string> IncomingBlocks(string vertexId)
    {
        var uid = Uid(vertexId);
        var blocks = new List<string>();

        for (int i = 0; i < _edgePredicates.Count; i++)
        {
            var predicate = Predicate(_edgePredicates[i]);

            blocks.Add($"in{i}(func: has({predicate})) @filter(uid_in({predicate}, {uid})) {{ {NodeHeader} {NamePredicate} {predicate} @filter(uid({uid})) {{ {NodeHeader} {NamePredicate} }} }}");
        }

        return blocks;
    }

    //A query that reads nothing without troubling the cluster: the node itself, and none of its predicates.
    private static string EmptyQuery(string vertexId)
    {
        return $"{{ none(func: uid({Uid(vertexId)})) @filter(eq(uid, 0)) {{ uid }} }}";
    }

    ///<summary>Reads the edges out of the converted answer — Dgraph builds them from nesting, so they are already there.</summary>
    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        var edges = new List<EdgeInfo>();

        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return edges;

        var table = GraphDataConverter.ToTable(result.Data);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in table.Nodes)
        {
            if (node.Properties.TryGetValue(NamePredicate, out var name) && !string.IsNullOrWhiteSpace(name))
                names[node.Id] = name;
            else
                names[node.Id] = node.Id;
        }

        foreach (var edge in table.Edges)
        {
            //Which end is "the other one" depends on the direction the query asked in; the panel keeps the
            //two lists apart, so the far end is simply whichever is not this query's subject. In-edge
            //blocks name the source, out-edge blocks the target, and both read the same way here.
            edges.Add(new EdgeInfo
            {
                EdgeId = edge.Id,
                //A Dgraph edge is the triple, not a stored thing with an id type of its own.
                EdgeIdType = null,
                Label = edge.Label,
                OtherNodeId = OtherEnd(edge, table),
                OtherNodeLabel = names.TryGetValue(OtherEnd(edge, table), out var label) ? label : OtherEnd(edge, table)
            });
        }

        return edges;
    }

    //The end of the edge that is not the node every row has in common — the one the query was about.
    private static string OtherEnd(GraphDataConverter.GraphRow edge, GraphDataConverter.GraphTable table)
    {
        var subject = Subject(table);

        if (subject != null && edge.Source == subject)
            return edge.Target;

        if (subject != null && edge.Target == subject)
            return edge.Source;

        return edge.Target;
    }

    //The node every edge in the answer touches: an in-edge answer shares its target, an out-edge answer
    //its source.
    private static string Subject(GraphDataConverter.GraphTable table)
    {
        if (table.Edges.Count == 0)
            return null;

        var sources = new HashSet<string>(table.Edges.Select(e => e.Source), StringComparer.Ordinal);
        var targets = new HashSet<string>(table.Edges.Select(e => e.Target), StringComparer.Ordinal);

        if (sources.Count == 1 && table.Edges.Count > 0)
            return sources.First();

        if (targets.Count == 1)
            return targets.First();

        return null;
    }

    public string ParseDisplayLabel(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var node in GraphDataConverter.ToTable(result.Data).Nodes)
            if (node.Properties.TryGetValue(NamePredicate, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

        return null;
    }

    //── Writes ──────────────────────────────────────────────────────────
    //
    //JSON mutations, one per line. See the class summary for why not RDF.

    public string AddVertex(string label)
    {
        return Set(NewNode(label, null));
    }

    public string AddVertexWithName(string label, string name)
    {
        return Set(NewNode(label, name));
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        var node = NewNode(label, name);
        node[GdbvKeys.X] = x.ToString(CultureInfo.InvariantCulture);
        node[GdbvKeys.Y] = y.ToString(CultureInfo.InvariantCulture);

        return Set(node);
    }

    ///<summary>
    ///An imported node, as an <b>upsert</b> keyed on the id it came with.
    ///
    ///This is where Dgraph differs most from every other engine. An import names its nodes with the source
    ///graph's ids and its edges reference those same ids — but Dgraph assigns a <c>uid</c> only when a
    ///mutation commits, and each staged line is its own mutation, so a blank node written on one line is
    ///simply gone by the next. The id has to live in the graph for the edges to find it, which is what
    ///<see cref="ImportIdPredicate"/> is, and finding it needs <c>eq()</c>, which needs an index — see
    ///<see cref="DgraphImportPreparation"/>, which is what asks before altering anyone's schema.
    ///
    ///Written this way it is also idempotent: importing the same graph twice matches what is there rather
    ///than making a second copy of it.
    ///</summary>
    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var node = new Dictionary<string, object>
        {
            ["uid"] = UpsertVariable(NodeVariable),
            [ImportIdPredicate] = id ?? ""
        };

        if (!string.IsNullOrWhiteSpace(label))
            node[DgraphConverter.TypeField] = label;

        foreach (var pair in properties)
            node[pair.Key] = pair.Value;

        return Upsert(node, MatchClause(NodeVariable, id));
    }

    public string AddEdge(string sourceId, string label, string targetId)
    {
        return AddEdgeWithProperties(sourceId, label, targetId, new Dictionary<string, string>());
    }

    ///<summary>
    ///An edge with its properties, which in Dgraph are <b>facets</b>: keys hung on the link itself, written
    ///<c>predicate|name</c> on the far node in a mutation and read back the same way. The traversal queries
    ///ask <c>@facets</c>, so what is written here is what comes back.
    ///</summary>
    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        //An endpoint that is not a uid is an imported id, so the link is made by finding both ends by that
        //id — the same upsert the imported nodes were written with, and the reason they carry it.
        bool imported = !IsUid(sourceId) || !IsUid(targetId);

        var target = new Dictionary<string, object>();

        if (imported)
            target["uid"] = UpsertVariable(TargetVariable);
        else
            target["uid"] = Uid(targetId);

        foreach (var pair in properties)
            target[Facet(label, pair.Key)] = pair.Value;

        var edge = new Dictionary<string, object>();

        if (imported)
            edge["uid"] = UpsertVariable(SourceVariable);
        else
            edge["uid"] = Uid(sourceId);

        edge[label] = new[] { target };

        if (!imported)
            return Set(edge);

        return Upsert(edge, MatchClause(SourceVariable, sourceId), MatchClause(TargetVariable, targetId));
    }

    ///<summary>How a facet is named in a mutation and in an answer: the predicate, a bar, and the key.</summary>
    public static string Facet(string predicate, string key)
    {
        return $"{predicate}{DgraphConverter.FacetSeparator}{key}";
    }

    ///<summary>
    ///Deletes every predicate the node has — Dgraph's <c>S * *</c>. Edges <i>pointing at</i> it are not
    ///reached by that (they belong to the other node), which is why the viewer stages a drop for each
    ///incident edge it knows about first.
    ///</summary>
    public string DropVertex(string id, string idType)
    {
        return Delete(new Dictionary<string, object> { ["uid"] = Uid(id) });
    }

    public string DropEdge(string id, string idType)
    {
        if (!DgraphConverter.TryReadEdgeId(id, out var source, out var predicate, out var target))
            throw new NotSupportedException($"'{id}' is not a Dgraph edge id. An edge there is the triple it is, written source-predicate->target.");

        var edge = new Dictionary<string, object>
        {
            ["uid"] = source,
            [predicate] = new[] { new Dictionary<string, object> { ["uid"] = target } }
        };

        return Delete(edge);
    }

    ///<summary>
    ///Writes one key. On a node that is a predicate; on an edge it is a <b>facet</b>, which is written by
    ///naming the link again and hanging the key off its far end.
    ///</summary>
    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        if (type == "edge")
            return EdgeFacet(id, key, value);

        return Set(new Dictionary<string, object> { ["uid"] = Uid(id), [key] = value });
    }

    ///<summary>
    ///Removes a key. A node's predicate is deleted by writing null; a facet has no delete of its own, so it
    ///is <b>overwritten with an empty value</b> — Dgraph keeps facets as part of the link, and the only way
    ///to drop one outright is to delete the link and write it again, which would lose the others.
    ///</summary>
    public string DropProperty(string type, string id, string key, string idType)
    {
        if (type == "edge")
            return EdgeFacet(id, key, "");

        return Delete(new Dictionary<string, object> { ["uid"] = Uid(id), [key] = null });
    }

    //A facet is written by re-stating the link with the key hung on the node it points at.
    private string EdgeFacet(string edgeId, string key, string value)
    {
        if (!DgraphConverter.TryReadEdgeId(edgeId, out var source, out var predicate, out var target))
            throw new NotSupportedException($"'{edgeId}' is not a Dgraph edge id. An edge there is the triple it is, written source-predicate->target.");

        var far = new Dictionary<string, object>
        {
            ["uid"] = target,
            [Facet(predicate, key)] = value
        };

        return Set(new Dictionary<string, object>
        {
            ["uid"] = source,
            [predicate] = new[] { far }
        });
    }

    ///<summary>
    ///Not offered. Stripping every <c>gdbv*</c> predicate means visiting every node that has one, and a
    ///Dgraph query can only start from a function — so this would need one <c>has()</c> block per viewer
    ///predicate, then a mutation per node, which is a query the staged-edit flow has no way to express.
    ///</summary>
    public string DropAllViewerProperties()
    {
        throw new NotSupportedException(
            "Dgraph deletes by naming the node, so clearing a predicate everywhere would need one mutation per node holding it. The viewer cannot stage that. Remove the properties from the nodes you can see instead.");
    }

    private Dictionary<string, object> NewNode(string label, string name)
    {
        //A blank node: Dgraph assigns the real uid when the mutation commits.
        var node = new Dictionary<string, object> { ["uid"] = "_:new" };

        if (!string.IsNullOrWhiteSpace(label))
            node[DgraphConverter.TypeField] = label;

        if (!string.IsNullOrWhiteSpace(name))
            node[NamePredicate] = name;

        return node;
    }

    ///<summary>Variable the upsert binds an imported node to.</summary>
    public const string NodeVariable = "v";

    ///<summary>Variables an imported edge binds its two ends to.</summary>
    public const string SourceVariable = "s";

    public const string TargetVariable = "t";

    //"v as var(func: eq(gdbvId, "alice"))" — how an upsert finds a node the import already wrote, or
    //finds nothing and so creates it.
    private static string MatchClause(string variable, string importId)
    {
        return $"{variable} as var(func: eq({ImportIdPredicate}, {JsonSerializer.Serialize(importId ?? "")}))";
    }

    //How the mutation refers to what the query found. An unmatched variable is not an error: Dgraph then
    //writes a new node, which is what makes an import create what is missing and match what is not.
    private static string UpsertVariable(string variable)
    {
        return $"uid({variable})";
    }

    //A mutation with a query in front of it. The HTTP API takes the pair as one JSON body, which is what
    //keeps a whole imported node on one line — the RDF upsert form would need a newline per triple.
    private static string Upsert(Dictionary<string, object> node, params string[] clauses)
    {
        var payload = new Dictionary<string, object>
        {
            ["query"] = "{ " + string.Join(" ", clauses) + " }",
            ["set"] = new[] { node }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string Set(Dictionary<string, object> node)
    {
        return Mutation("set", node);
    }

    private static string Delete(Dictionary<string, object> node)
    {
        return Mutation("delete", node);
    }

    private static string Mutation(string operation, Dictionary<string, object> node)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object> { [operation] = new[] { node } });
    }

    //── Guards and parsing ──────────────────────────────────────────────

    public bool IsMutating(string query)
    {
        return DgraphDb.IsMutation(query);
    }

    public List<GraphEdit> ParseEdits(string buffer)
    {
        return DqlMutationParser.Parse(buffer);
    }

    //── Query pieces ────────────────────────────────────────────────────

    //The value predicates, spelled out: Dgraph answers with what you ask for, and expand(_all_) would drag
    //every edge along with them.
    private string Values()
    {
        if (_scalarPredicates.Count == 0)
            return "";

        return " " + string.Join(" ", _scalarPredicates.Select(Predicate));
    }

    //Each edge predicate followed one hop, with enough of the far node to name it on the canvas — and
    //@facets, which is the only way an edge's own properties come back at all.
    private string Edges()
    {
        if (_edgePredicates.Count == 0)
            return "";

        return " " + string.Join(" ", _edgePredicates.Select(p => $"{Predicate(p)} @facets {{ {NodeHeader} {NamePredicate} }}"));
    }

    ///<summary>
    ///A filter meaning "this node is really there". Dgraph <b>echoes any uid you name</b> — asking for one
    ///that was never used answers with the uid and nothing else — so without this, expanding a node that
    ///had been emptied, or one whose neighbour has since gone, draws a nameless ghost that looks like a
    ///failed delete. Dgraph has no "has anything", so the test is "has any predicate the schema knows of".
    ///</summary>
    private string Exists()
    {
        var predicates = new List<string> { "dgraph.type" };

        predicates.AddRange(_scalarPredicates.Select(Predicate));
        predicates.AddRange(_edgePredicates.Select(Predicate));

        return $" @filter({string.Join(" OR ", predicates.Select(p => $"has({p})"))})";
    }

    private static string First(int? limit)
    {
        if (limit == null || limit <= 0)
            return "";

        return $", first: {limit.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    ///<summary>
    ///A uid as a query can use it. Dgraph's are hexadecimal (<c>0x1</c>); anything else is quoted so a
    ///stray id is a query that finds nothing rather than one that will not parse.
    ///</summary>
    public static string Uid(string id)
    {
        var text = (id ?? "").Trim();

        if (text.Length == 0)
            return "0x0";

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            return text;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return "0x" + number.ToString("x", CultureInfo.InvariantCulture);

        return "0x0";
    }

    ///<summary>
    ///True when an id is one Dgraph assigned. Anything else came from somewhere the graph does not know —
    ///an import, a pasted file — and has to be looked up by <see cref="ImportIdPredicate"/> instead.
    ///</summary>
    public static bool IsUid(string id)
    {
        var text = (id ?? "").Trim();

        return text.Length > 2 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>
    ///A predicate as a query can name it. Dgraph allows almost anything in a predicate name and takes it
    ///in angle brackets when it is not a bare identifier.
    ///</summary>
    public static string Predicate(string name)
    {
        var text = name ?? "";

        if (text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'))
            return text;

        return "<" + text.Replace(">", "") + ">";
    }
}
