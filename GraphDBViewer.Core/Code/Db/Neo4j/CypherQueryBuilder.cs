using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///The Cypher half of <see cref="IGraphQueryBuilder"/> — the Neo4j / Memgraph counterpart to
///<see cref="GremlinQueryBuilder"/>. Writing it is what lets the Neo4j provider switch its browse /
///traverse / stage-edits <see cref="GraphDbCapabilities"/> on: those features are the viewer composing
///queries on the user's behalf, and until now it could only compose Gremlin.
///
///Elements are addressed by <c>elementId()</c>, which is what <see cref="Neo4jConverter"/> reports as an
///element's id, so an id that came out of a result can be handed straight back in. (Neo4j 5 deprecates the
///old integer <c>id()</c>; Memgraph exposes <c>id()</c> instead, so it needs its own dialect if added.)
///</summary>
public sealed class CypherQueryBuilder : IGraphQueryBuilder
{
    public static readonly CypherQueryBuilder Instance = new();

    ///<summary>
    ///Property holding a node's original id when a graph is imported. Neo4j assigns element ids itself and
    ///will not take one, so an import keeps the source id here — that is what the edges then match on.
    ///</summary>
    public const string ImportIdKey = "gdbvId";

    //Property names tried, in order, for a node's human-readable display label — the Cypher mirror of the
    //Gremlin coalesce(values('name','Name','title','Title'), label()).
    private const string DisplayLabelExpression = "coalesce({0}.name, {0}.Name, {0}.title, {0}.Title, head(labels({0})), '')";

    //── Escaping ────────────────────────────────────────────────────────

    ///<summary>Escapes a value for a single-quoted Cypher string literal.</summary>
    public static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }

    ///<summary>
    ///Quotes a label, relationship type or property key as a backticked identifier, so a name with a space
    ///or a dash is still legal Cypher. A backtick inside the name is escaped by doubling it.
    ///</summary>
    public static string QuoteIdentifier(string name)
    {
        return "`" + (name ?? string.Empty).Trim().Replace("`", "``") + "`";
    }

    private static string Literal(string value)
    {
        return "'" + Escape(value) + "'";
    }

    ///<summary>
    ///How a node is addressed: by its element id, or by the id it was imported under. Both are needed
    ///because a staged import has not been committed yet — its nodes have no element id the viewer could
    ///know, so an edge or a property update staged alongside them can only name the import id. Once
    ///committed, the same edit addressed by element id still finds them.
    ///</summary>
    private static string NodeIdPredicate(string variable, string id)
    {
        return $"(elementId({variable}) = {Literal(id)} OR {variable}.{QuoteIdentifier(ImportIdKey)} = {Literal(id)})";
    }

    //Binds a node or relationship. "node" matches a node, anything else a relationship — and only nodes
    //carry an import id, since edges are always created against nodes that already resolve.
    private static string MatchElement(string type, string id, string variable)
    {
        if (type == "node")
            return $"MATCH ({variable}) WHERE {NodeIdPredicate(variable, id)}";

        return $"MATCH ()-[{variable}]-() WHERE elementId({variable}) = {Literal(id)}";
    }

    private static string DisplayLabel(string variable)
    {
        return string.Format(DisplayLabelExpression, variable);
    }

    //── Browse ──────────────────────────────────────────────────────────

    public string LimitedVertices(int limit)
    {
        return $"MATCH (n) RETURN n LIMIT {limit}";
    }

    ///<summary>
    ///Every node — isolated ones included — with the relationships leaving them. OPTIONAL MATCH is what
    ///keeps an edgeless node in the answer, mirroring the fold/union the Gremlin builder needs for the
    ///same reason. The limit caps root nodes, not rows, so it is applied before the expansion.
    ///</summary>
    public string FullGraph(int? limit)
    {
        if (limit.HasValue)
            return $@"MATCH (n) WITH n LIMIT {limit.Value}
OPTIONAL MATCH (n)-[r]->(m)
RETURN n, r, m";

        return @"MATCH (n)
OPTIONAL MATCH (n)-[r]->(m)
RETURN n, r, m";
    }

    //── Traverse ────────────────────────────────────────────────────────

    ///<summary>
    ///The node with the relationships on either side of it and whatever sits at their far end. The node
    ///itself is returned so an expansion that finds nothing still resolves to a real node rather than
    ///leaving the canvas to invent a placeholder.
    ///</summary>
    public string Neighbors(string id, int limit)
    {
        var match = $"MATCH (n) WHERE elementId(n) = {Literal(id)}\nOPTIONAL MATCH (n)-[r]-(m)\nRETURN n, r, m";

        if (limit <= 0)
            return match;

        return match + $"\nLIMIT {limit}";
    }

    public string InEdges(string vertexId)
    {
        return EdgeList(vertexId, "<-[e]-");
    }

    public string OutEdges(string vertexId)
    {
        return EdgeList(vertexId, "-[e]->");
    }

    //The connections panel wants one row per edge: the edge's id and type, and the id and display label of
    //the node at the other end — the same four fields the Gremlin builder projects.
    private static string EdgeList(string vertexId, string pattern)
    {
        return $@"MATCH (v){pattern}(o) WHERE elementId(v) = {Literal(vertexId)}
RETURN elementId(e) AS eId, type(e) AS eLabel, elementId(o) AS vId, {DisplayLabel("o")} AS vLabel";
    }

    public string VertexDisplayLabel(string vertexId)
    {
        return $@"MATCH (n) WHERE elementId(n) = {Literal(vertexId)}
RETURN {DisplayLabel("n")} AS label";
    }

    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        var edges = new List<EdgeInfo>();

        //Cypher answers these with plain rows rather than the GraphSON objects the Gremlin projection
        //produces, so they arrive as a table.
        if (result.IsError || result.Table == null)
            return edges;

        foreach (var row in result.Table.Rows)
        {
            edges.Add(new EdgeInfo
            {
                EdgeId = Cell(row, "eId"),
                //Neo4j element ids are strings, so there is no numeric id type to preserve.
                EdgeIdType = null,
                Label = Cell(row, "eLabel"),
                OtherNodeId = Cell(row, "vId"),
                OtherNodeLabel = Cell(row, "vLabel")
            });
        }

        return edges;
    }

    public string ParseDisplayLabel(GraphDbResult result)
    {
        if (result.IsError || result.Table == null || result.Table.Rows.Count == 0)
            return null;

        var label = Cell(result.Table.Rows[0], "label");

        if (string.IsNullOrEmpty(label) || label == "?")
            return null;

        return label;
    }

    private static string Cell(Dictionary<string, string> row, string column)
    {
        if (row.TryGetValue(column, out var value) && !string.IsNullOrEmpty(value))
            return value;

        return "?";
    }

    //── Creation ────────────────────────────────────────────────────────

    public string AddVertex(string label)
    {
        return $"CREATE (n:{QuoteIdentifier(label)}) RETURN n";
    }

    public string AddVertexWithName(string label, string name)
    {
        return $"CREATE (n:{QuoteIdentifier(label)} {{name: {Literal(name)}}}) RETURN n";
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        var xs = x.ToString(CultureInfo.InvariantCulture);
        var ys = y.ToString(CultureInfo.InvariantCulture);

        //The positions are stored as strings, matching what the Gremlin builder writes and what the
        //converter reads back — the viewer parses them itself.
        return $"CREATE (n:{QuoteIdentifier(label)} {{name: {Literal(name)}, {ImportSafeKey(GdbvKeys.X)}: {Literal(xs)}, {ImportSafeKey(GdbvKeys.Y)}: {Literal(ys)}}}) RETURN n";
    }

    ///<summary>
    ///Creates a node carrying its original id in <see cref="ImportIdKey"/>. Neo4j assigns element ids and
    ///will not accept one, so an imported id becomes a property — which is also what
    ///<see cref="AddEdgeWithProperties"/> then matches its endpoints on.
    ///</summary>
    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE (n:{QuoteIdentifier(label)} {{{ImportSafeKey(ImportIdKey)}: {Literal(id)}");

        foreach (var kv in properties)
            sb.Append($", {ImportSafeKey(kv.Key)}: {Literal(kv.Value)}");

        sb.Append("}) RETURN n");

        return sb.ToString();
    }

    //Every staged query stays on one line: the Generated buffer is committed by splitting on newlines, so
    //a statement that wrapped would be run as two broken halves.
    public string AddEdge(string sourceId, string label, string targetId)
    {
        return AddEdgeWithProperties(sourceId, label, targetId, new Dictionary<string, string>());
    }

    ///<summary>
    ///Links two nodes named by element id or by import id (see <see cref="NodeIdPredicate"/>), so an edge
    ///staged alongside a not-yet-committed import still finds its endpoints.
    ///</summary>
    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        var sb = new StringBuilder();
        sb.Append($"MATCH (a), (b) WHERE {NodeIdPredicate("a", sourceId)} AND {NodeIdPredicate("b", targetId)} ");
        sb.Append($"CREATE (a)-[r:{QuoteIdentifier(label)}");

        if (properties.Count > 0)
        {
            sb.Append(" {");
            bool first = true;

            foreach (var kv in properties)
            {
                if (!first)
                    sb.Append(", ");

                sb.Append($"{ImportSafeKey(kv.Key)}: {Literal(kv.Value)}");
                first = false;
            }

            sb.Append('}');
        }

        sb.Append("]->(b) RETURN r");

        return sb.ToString();
    }

    //A property key written inside a map literal or after a dot — always backticked, so a key with a
    //space or a leading digit stays legal.
    private static string ImportSafeKey(string key)
    {
        return QuoteIdentifier(key);
    }

    //── Deletion ────────────────────────────────────────────────────────

    ///<summary>DETACH so the node's relationships go with it, matching Gremlin's drop() semantics.</summary>
    public string DropVertex(string id, string idType)
    {
        return $"MATCH (n) WHERE {NodeIdPredicate("n", id)} DETACH DELETE n";
    }

    public string DropEdge(string id, string idType)
    {
        return $"MATCH ()-[r]-() WHERE elementId(r) = {Literal(id)} DELETE r";
    }

    //── Property mutation ───────────────────────────────────────────────

    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        var variable = VariableFor(type);

        return $"{MatchElement(type, id, variable)} SET {variable}.{QuoteIdentifier(key)} = {Literal(value)}";
    }

    public string DropProperty(string type, string id, string key, string idType)
    {
        var variable = VariableFor(type);

        return $"{MatchElement(type, id, variable)} REMOVE {variable}.{QuoteIdentifier(key)}";
    }

    private static string VariableFor(string type)
    {
        if (type == "node")
            return "n";

        return "r";
    }

    ///<summary>
    ///Strips every viewer-written (gdbv*) property from all nodes and all relationships — node clean-up on
    ///the first line, relationship clean-up on the second, so the two run as separate statements exactly as
    ///the Gremlin version does.
    ///</summary>
    public string DropAllViewerProperties()
    {
        var nodeKeys = new List<string>();
        var edgeKeys = new List<string>();

        foreach (var key in GdbvKeys.All)
        {
            nodeKeys.Add("n." + QuoteIdentifier(key));
            edgeKeys.Add("r." + QuoteIdentifier(key));
        }

        return $@"MATCH (n) REMOVE {string.Join(", ", nodeKeys)}
MATCH ()-[r]-() REMOVE {string.Join(", ", edgeKeys)}";
    }

    //── Guards and parsing ──────────────────────────────────────────────

    public bool IsMutating(string query)
    {
        return CypherStatementParser.IsMutating(query);
    }

    public List<GraphEdit> ParseEdits(string buffer)
    {
        return CypherStatementParser.Parse(buffer);
    }
}
