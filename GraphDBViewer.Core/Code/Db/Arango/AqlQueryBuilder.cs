using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///The AQL half of <see cref="IGraphQueryBuilder"/> — what lets the ArangoDB provider switch its browse,
///traverse and stage-edits <see cref="GraphDbCapabilities"/> on.
///
///ArangoDB shapes this differently from the other engines in one important way: <b>AQL cannot name a
///collection dynamically</b>. <c>FOR d IN someExpression</c> is an error, and <c>COLLECTIONS()</c> only
///lists names — so "load the database" has to spell every collection out in the query text. That is why
///this builder is constructed with the connected database's collections (from <see cref="AqlSchemaSource"/>)
///rather than being a stateless singleton like the others.
///
///Edits need no such knowledge: a document's <c>_id</c> is <c>"collection/key"</c>, so the collection to
///write to is parsed straight out of the id, and a new element's label *is* its collection.
///</summary>
public sealed class AqlQueryBuilder : IGraphQueryBuilder
{
    ///<summary>A builder with no schema yet: edits work, browse and traverse have nothing to enumerate.</summary>
    public static readonly AqlQueryBuilder Empty = new(null, null);

    //Characters ArangoDB allows in a document key. Anything else is folded to "_" — deterministically, so
    //an edge that references an imported id lands on the same key the node was created under.
    private const string KeyPunctuation = "_-:.@()+,=;$!*'%";

    private readonly List<string> _vertexCollections;
    private readonly List<string> _edgeCollections;

    public AqlQueryBuilder(IEnumerable<string> vertexCollections, IEnumerable<string> edgeCollections)
    {
        _vertexCollections = Clean(vertexCollections);
        _edgeCollections = Clean(edgeCollections);
    }

    ///<summary>Document collections the browse queries enumerate.</summary>
    public IReadOnlyList<string> VertexCollections => _vertexCollections;

    ///<summary>Edge collections the traversals walk.</summary>
    public IReadOnlyList<string> EdgeCollections => _edgeCollections;

    ///<summary>True once the schema is known — before that, browse and traverse have nothing to name.</summary>
    public bool HasSchema => _vertexCollections.Count > 0 || _edgeCollections.Count > 0;

    private static List<string> Clean(IEnumerable<string> names)
    {
        var cleaned = new List<string>();

        if (names == null)
            return cleaned;

        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(name) && !cleaned.Contains(name))
                cleaned.Add(name);

        return cleaned;
    }

    //── Escaping ────────────────────────────────────────────────────────

    ///<summary>Escapes a value for a single-quoted AQL string literal.</summary>
    public static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }

    ///<summary>Quotes a collection or attribute name as a backticked identifier.</summary>
    public static string QuoteIdentifier(string name)
    {
        return "`" + (name ?? string.Empty).Trim().Replace("`", "") + "`";
    }

    private static string Literal(string value)
    {
        return "'" + Escape(value) + "'";
    }

    ///<summary>
    ///The collection half of an <c>_id</c> — <c>"persons/alice"</c> → <c>"persons"</c>. An id with no
    ///slash has no collection to name, which the callers treat as "not addressable".
    ///</summary>
    public static string CollectionOf(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        int slash = id.IndexOf('/');

        if (slash <= 0)
            return null;

        return id.Substring(0, slash);
    }

    ///<summary>
    ///The key half of an <c>_id</c>, folded to characters ArangoDB accepts in a key. An id that is already
    ///bare (an imported one, say) is folded whole — and because the folding is deterministic, an edge that
    ///names the same original id resolves to the same key.
    ///</summary>
    public static string DocumentKey(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";

        int slash = id.IndexOf('/');
        var key = slash >= 0 ? id.Substring(slash + 1) : id;

        var sb = new StringBuilder(key.Length);

        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c) && c < 128)
                sb.Append(c);
            else if (KeyPunctuation.IndexOf(c) >= 0)
                sb.Append(c);
            else
                sb.Append('_');
        }

        return sb.ToString();
    }

    //A comma-separated, backticked collection list for a traversal's edge collections.
    private string EdgeCollectionList()
    {
        return string.Join(", ", _edgeCollections.Select(QuoteIdentifier));
    }

    //A UNION over the named collections, each capped so one huge collection can't crowd out the rest.
    private static string UnionOver(IReadOnlyList<string> collections, int? perCollectionLimit)
    {
        var parts = new List<string>();

        foreach (var collection in collections)
        {
            if (perCollectionLimit.HasValue)
                parts.Add($"(FOR x IN {QuoteIdentifier(collection)} LIMIT {perCollectionLimit.Value} RETURN x)");
            else
                parts.Add($"(FOR x IN {QuoteIdentifier(collection)} RETURN x)");
        }

        if (parts.Count == 0)
            return "[]";

        //UNION needs at least two arrays; one collection is just that array.
        if (parts.Count == 1)
            return parts[0];

        return $"UNION({string.Join(", ", parts)})";
    }

    //The AQL expression for a document's human-readable label. AQL's || yields the first truthy operand,
    //so this is the same name/Name/title/Title fallback the other builders use.
    private static string DisplayLabel(string variable)
    {
        return $"({variable}.name || {variable}.Name || {variable}.title || {variable}.Title || PARSE_IDENTIFIER({variable}._id).collection)";
    }

    //── Browse ──────────────────────────────────────────────────────────

    public string LimitedVertices(int limit)
    {
        if (_vertexCollections.Count == 0)
            return "RETURN []";

        return $"FOR d IN {UnionOver(_vertexCollections, limit)} LIMIT {limit} RETURN d";
    }

    ///<summary>
    ///Every document, then every edge between them. Documents come first for the same reason the other
    ///builders order them that way — an edge listed before its endpoints would otherwise draw a
    ///placeholder node that the real one never replaces.
    ///</summary>
    public string FullGraph(int? limit)
    {
        if (!HasSchema)
            return "RETURN []";

        var vertices = UnionOver(_vertexCollections, limit);
        var edges = UnionOver(_edgeCollections, limit);

        return $"FOR d IN APPEND({vertices}, {edges}) RETURN d";
    }

    //── Traverse ────────────────────────────────────────────────────────

    ///<summary>
    ///The document plus everything one hop away, in either direction. The start document is returned too,
    ///so an expansion that finds nothing still resolves to a real node.
    ///</summary>
    public string Neighbors(string id, int limit)
    {
        if (_edgeCollections.Count == 0)
            return $"RETURN [DOCUMENT({Literal(id)})]";

        string cap = "";
        if (limit > 0)
            cap = $" LIMIT {limit}";

        return $"LET start = DOCUMENT({Literal(id)}) "
            + $"FOR d IN APPEND([start], FLATTEN(FOR v, e IN 1..1 ANY start {EdgeCollectionList()}{cap} RETURN [v, e])) RETURN d";
    }

    public string InEdges(string vertexId)
    {
        return EdgeList(vertexId, "INBOUND");
    }

    public string OutEdges(string vertexId)
    {
        return EdgeList(vertexId, "OUTBOUND");
    }

    //One row per edge: its id and collection, and the id and display label of the document at the far end —
    //the four fields the connections panel reads.
    private string EdgeList(string vertexId, string direction)
    {
        if (_edgeCollections.Count == 0)
            return "RETURN []";

        return $"FOR v, e IN 1..1 {direction} {Literal(vertexId)} {EdgeCollectionList()} "
            + $"RETURN {{ eId: e._id, eLabel: PARSE_IDENTIFIER(e._id).collection, vId: v._id, vLabel: {DisplayLabel("v")} }}";
    }

    public string VertexDisplayLabel(string vertexId)
    {
        return $"LET d = DOCUMENT({Literal(vertexId)}) RETURN {{ label: {DisplayLabel("d")} }}";
    }

    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        var edges = new List<EdgeInfo>();

        //These project named fields rather than documents, so they come back as a table.
        if (result.IsError || result.Table == null)
            return edges;

        foreach (var row in result.Table.Rows)
        {
            edges.Add(new EdgeInfo
            {
                EdgeId = Cell(row, "eId"),
                //ArangoDB ids are strings, so there is no numeric id type to preserve.
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

    //A new element's label is the collection it goes into — ArangoDB has no separate notion of a label.
    public string AddVertex(string label)
    {
        return $"INSERT {{}} INTO {QuoteIdentifier(label)} RETURN NEW";
    }

    public string AddVertexWithName(string label, string name)
    {
        return $"INSERT {{ name: {Literal(name)} }} INTO {QuoteIdentifier(label)} RETURN NEW";
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        var xs = x.ToString(CultureInfo.InvariantCulture);
        var ys = y.ToString(CultureInfo.InvariantCulture);

        //Positions are stored as strings, matching what the other builders write and the converter reads.
        return $"INSERT {{ name: {Literal(name)}, {QuoteIdentifier(GdbvKeys.X)}: {Literal(xs)}, {QuoteIdentifier(GdbvKeys.Y)}: {Literal(ys)} }} INTO {QuoteIdentifier(label)} RETURN NEW";
    }

    ///<summary>
    ///Creates a document under a key derived from the incoming id, so an import keeps its own identity and
    ///the edges that reference it can find it again.
    ///</summary>
    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var fields = new List<string> { $"_key: {Literal(DocumentKey(id))}" };

        foreach (var kv in properties)
            fields.Add($"{QuoteIdentifier(kv.Key)}: {Literal(kv.Value)}");

        return $"INSERT {{ {string.Join(", ", fields)} }} INTO {QuoteIdentifier(label)} RETURN NEW";
    }

    public string AddEdge(string sourceId, string label, string targetId)
    {
        return AddEdgeWithProperties(sourceId, label, targetId, new Dictionary<string, string>());
    }

    ///<summary>
    ///Links two documents. Endpoints that already carry their collection (<c>"persons/alice"</c> — anything
    ///that came off the canvas) are used as-is; a bare id (an import's) is resolved by key against the known
    ///document collections, which is the only way to find it without knowing which collection it landed in.
    ///</summary>
    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        var fields = new List<string> { $"_from: {Endpoint(sourceId)}", $"_to: {Endpoint(targetId)}" };

        foreach (var kv in properties)
            fields.Add($"{QuoteIdentifier(kv.Key)}: {Literal(kv.Value)}");

        return $"INSERT {{ {string.Join(", ", fields)} }} INTO {QuoteIdentifier(label)} RETURN NEW";
    }

    //The AQL expression for one end of an edge.
    private string Endpoint(string id)
    {
        if (CollectionOf(id) != null)
            return Literal(id);

        //A bare id: look it up by key across the document collections. FIRST over a UNION keeps it to one
        //statement, which the staged-edit buffer requires.
        var key = DocumentKey(id);
        var lookups = _vertexCollections
            .Select(c => $"(FOR x IN {QuoteIdentifier(c)} FILTER x._key == {Literal(key)} LIMIT 1 RETURN x._id)")
            .ToList();

        if (lookups.Count == 0)
            return Literal(id);

        if (lookups.Count == 1)
            return $"FIRST({lookups[0]})";

        return $"FIRST(UNION({string.Join(", ", lookups)}))";
    }

    //── Deletion ────────────────────────────────────────────────────────

    ///<summary>
    ///Removes the document. Its edges are not swept up here — ArangoDB has no DETACH, and AQL cannot name
    ///the edge collections to clear dynamically. The viewer already stages a removal for each incident edge
    ///it knows about before this one, which is what keeps the graph consistent.
    ///</summary>
    public string DropVertex(string id, string idType)
    {
        return RemoveById(id);
    }

    public string DropEdge(string id, string idType)
    {
        return RemoveById(id);
    }

    private static string RemoveById(string id)
    {
        var collection = CollectionOf(id);

        if (collection == null)
            return $"RETURN {Literal($"Cannot remove '{id}'. An ArangoDB id must be collection/key.")}";

        return $"REMOVE {Literal(DocumentKey(id))} IN {QuoteIdentifier(collection)}";
    }

    //── Property mutation ───────────────────────────────────────────────

    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        var collection = CollectionOf(id);

        if (collection == null)
            return $"RETURN {Literal($"Cannot update '{id}'. An ArangoDB id must be collection/key.")}";

        return $"UPDATE {Literal(DocumentKey(id))} WITH {{ {QuoteIdentifier(key)}: {Literal(value)} }} IN {QuoteIdentifier(collection)}";
    }

    ///<summary>
    ///Removes an attribute. Setting it to null and switching <c>keepNull</c> off is how AQL unsets a field —
    ///without the option it would store a null instead of removing it.
    ///</summary>
    public string DropProperty(string type, string id, string key, string idType)
    {
        var collection = CollectionOf(id);

        if (collection == null)
            return $"RETURN {Literal($"Cannot update '{id}'. An ArangoDB id must be collection/key.")}";

        return $"UPDATE {Literal(DocumentKey(id))} WITH {{ {QuoteIdentifier(key)}: null }} IN {QuoteIdentifier(collection)} OPTIONS {{ keepNull: false }}";
    }

    ///<summary>
    ///Strips every viewer-written (gdbv*) attribute from all documents and edges — one statement per
    ///collection, since AQL updates one collection at a time.
    ///</summary>
    public string DropAllViewerProperties()
    {
        var nulls = string.Join(", ", GdbvKeys.All.Select(k => $"{QuoteIdentifier(k)}: null"));
        var lines = new List<string>();

        foreach (var collection in _vertexCollections.Concat(_edgeCollections))
            lines.Add($"FOR d IN {QuoteIdentifier(collection)} UPDATE d WITH {{ {nulls} }} IN {QuoteIdentifier(collection)} OPTIONS {{ keepNull: false }}");

        if (lines.Count == 0)
            return "RETURN []";

        return string.Join("\n", lines);
    }

    //── Guards and parsing ──────────────────────────────────────────────

    public bool IsMutating(string query)
    {
        return AqlStatementParser.IsMutating(query);
    }

    public List<GraphEdit> ParseEdits(string buffer)
    {
        return AqlStatementParser.Parse(buffer);
    }
}
