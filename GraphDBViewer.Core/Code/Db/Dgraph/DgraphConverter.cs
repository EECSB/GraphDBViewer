using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Maps a Dgraph DQL answer onto the shared <see cref="GraphDbResult"/>.
///
///Dgraph models a graph differently from every other engine here, and the difference is the whole of this
///class: <b>there are no edge objects</b>. An edge is a predicate, and it appears in the answer as a nested
///node under that predicate's name —
///<c>{"uid":"0x1","name":"Alice","knows":[{"uid":"0x2","name":"Bob"}]}</c> is one node, one edge labeled
///<c>knows</c>, and a second node. So the reading is a tree walk: any object carrying a <c>uid</c> is a
///node, and finding one nested under a key means an edge from the enclosing node to it, named by that key.
///
///An edge therefore has no id of its own. One is synthesised from the triple it is —
///<c>"0x1-knows-&gt;0x2"</c> — which is the same shape the schema meta-graph already uses for a relationship.
///</summary>
public static class DgraphConverter
{
    ///<summary>Dgraph's own id predicate. Its presence is what makes an object a node.</summary>
    public const string UidField = "uid";

    ///<summary>Dgraph's type predicate. A node may carry several; the first is shown as its label.</summary>
    public const string TypeField = "dgraph.type";

    ///<summary>Label used for a node that declares no <c>dgraph.type</c> — Dgraph does not require one.</summary>
    public const string DefaultLabel = "node";

    ///<summary>The single column a result of bare values is shown under.</summary>
    public const string ValueColumn = "value";

    ///<summary>
    ///Builds a result from a DQL response body. The graph lives under <c>data</c>, keyed by query block —
    ///every block is walked, so a query asking several questions renders as one graph.
    ///</summary>
    public static GraphDbResult ToGraphDbResult(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return GraphDbResult.Success(EmptyGraph());

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(responseJson);
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure($"Could not parse the Dgraph response: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var error = ReadError(root);

            if (error != null)
                return GraphDbResult.Failure(error);

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
                return GraphDbResult.Success(EmptyGraph(), responseJson);

            return FromData(data, responseJson);
        }
    }

    ///<summary>Dgraph reports failures in an <c>errors</c> array; returns the joined messages, or null.</summary>
    public static string ReadError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
            return null;

        var messages = new List<string>();

        foreach (var error in errors.EnumerateArray())
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                messages.Add(message.GetString());

        if (messages.Count == 0)
            return "Dgraph reported an error.";

        return string.Join("; ", messages);
    }

    private static GraphDbResult FromData(JsonElement data, string raw)
    {
        var nodes = new Dictionary<string, object>();
        var edges = new Dictionary<string, object>();
        var plainRows = new List<JsonElement>();

        if (data.ValueKind == JsonValueKind.Object)
        {
            //Each property is one query block's results; a mutation answers with a plain object instead.
            foreach (var block in data.EnumerateObject())
                Collect(block.Value, null, null, nodes, edges, plainRows);
        }
        else
        {
            Collect(data, null, null, nodes, edges, plainRows);
        }

        if (nodes.Count > 0 || edges.Count > 0)
            return GraphDbResult.Success(BuildGraph(nodes, edges), raw);

        if (plainRows.Count == 0)
            return GraphDbResult.Success(EmptyGraph(), raw);

        return BuildTable(plainRows, raw);
    }

    ///<summary>
    ///Walks one value. <paramref name="parentUid"/> and <paramref name="predicate"/> carry where it came
    ///from: when they are set and the value turns out to be a node, that descent *is* an edge.
    ///</summary>
    private static void Collect(
        JsonElement value,
        string parentUid,
        string predicate,
        Dictionary<string, object> nodes,
        Dictionary<string, object> edges,
        List<JsonElement> plainRows)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                Collect(item, parentUid, predicate, nodes, edges, plainRows);

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
            return;

        string uid = GetString(value, UidField);

        if (uid == null)
        {
            //No uid: an aggregate, a projection, or a mutation's receipt — a row rather than a node.
            plainRows.Add(value.Clone());

            return;
        }

        AddNode(value, uid, nodes);

        if (parentUid != null && predicate != null)
            AddEdge(parentUid, predicate, uid, edges, ReadFacets(value, predicate));

        //Descend: a nested node under a predicate is that predicate's edge.
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == UidField || property.Name == TypeField)
                continue;

            if (HoldsNode(property.Value))
                Collect(property.Value, uid, property.Name, nodes, edges, plainRows);
        }
    }

    //True when a value is a node, or an array containing one — which is what separates an edge predicate
    //from a scalar one, Dgraph declaring no such distinction in the answer itself.
    private static bool HoldsNode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.TryGetProperty(UidField, out _);

        if (value.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(UidField, out _))
                return true;

        return false;
    }

    private static void AddNode(JsonElement node, string uid, Dictionary<string, object> nodes)
    {
        //The same node routinely appears more than once in one answer — as a result in its own right, and
        //again as somebody's neighbour — and the two rarely carry the same fields, a query asking for
        //different predicates at each level. So they are merged rather than one replacing the other: the
        //union of properties, and a declared dgraph.type always beating the generic label, whichever
        //appearance happened to come first.
        var label = ReadLabel(node);
        var properties = ReadProperties(node);

        if (nodes.TryGetValue(uid, out var existing) && existing is NodeRecord previous)
        {
            if (label == DefaultLabel)
                label = previous.label;

            //The earlier appearance's values stand; this one only fills in what it did not have.
            foreach (var pair in previous.properties)
                properties[pair.Key] = pair.Value;
        }

        nodes[uid] = new NodeRecord
        {
            id = uid,
            label = label,
            properties = properties
        };
    }

    //Named so the anonymous-object shape the other converters emit stays identical on the wire, while the
    //property count is still readable for the "richest appearance wins" check above.
    private sealed class NodeRecord
    {
        public string id { get; init; }
        public string label { get; init; }
        public Dictionary<string, string> properties { get; init; }
    }

    ///<summary>
    ///The id an edge gets. Dgraph gives an edge none of its own — it is a predicate holding a node — so the
    ///id is the triple it is, in the shape <see cref="GraphWireText"/> writes for every engine that has to
    ///synthesise one.
    ///</summary>
    public static string EdgeId(string source, string predicate, string target)
    {
        return GraphWireText.EdgeId(source, predicate, target);
    }

    ///<summary>Reads such an id back into its triple, or returns false when it is not one.</summary>
    public static bool TryReadEdgeId(string id, out string source, out string predicate, out string target)
    {
        return GraphWireText.TryReadEdgeId(id, out source, out predicate, out target);
    }

    private static void AddEdge(string fromUid, string predicate, string toUid, Dictionary<string, object> edges, Dictionary<string, string> facets)
    {
        //Dgraph gives an edge no id of its own — it is the triple.
        var id = EdgeId(fromUid, predicate, toUid);

        if (edges.ContainsKey(id))
            return;

        edges[id] = new
        {
            id,
            label = predicate,
            outV = fromUid,
            inV = toUid,
            properties = facets
        };
    }

    ///<summary>Separates a facet's name from the predicate it hangs on, in Dgraph's answers.</summary>
    public const char FacetSeparator = '|';

    ///<summary>
    ///An edge's properties. Dgraph calls them <b>facets</b>, and answers with them on the <i>child</i> node
    ///keyed <c>predicate|facet</c> — <c>"knows|since": "2020"</c> sits on Bob, though it describes Alice's
    ///knowing of him. They are read off there and put where they belong, which is the edge.
    ///
    ///They arrive only when the query asked <c>@facets</c>; a query that did not simply has none.
    ///</summary>
    public static Dictionary<string, string> ReadFacets(JsonElement node, string predicate)
    {
        var facets = new Dictionary<string, string>();

        if (node.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(predicate))
            return facets;

        var prefix = predicate + FacetSeparator;

        foreach (var property in node.EnumerateObject())
        {
            if (!property.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var value = GraphWireText.Stringify(property.Value);

            //Dgraph has no way to delete a facet — the only way to be rid of one is to delete the edge and
            //write it again, which would take the others with it. So the viewer clears a facet by writing
            //it empty, and an empty facet reads as the absence it was meant to be.
            if (!string.IsNullOrEmpty(value))
                facets[property.Name.Substring(prefix.Length)] = value;
        }

        return facets;
    }

    ///<summary>The node's first declared <c>dgraph.type</c>, or the generic label when it declares none.</summary>
    public static string ReadLabel(JsonElement node)
    {
        if (!node.TryGetProperty(TypeField, out var types))
            return DefaultLabel;

        if (types.ValueKind == JsonValueKind.String)
            return types.GetString();

        if (types.ValueKind == JsonValueKind.Array)
            foreach (var type in types.EnumerateArray())
                if (type.ValueKind == JsonValueKind.String)
                    return type.GetString();

        return DefaultLabel;
    }

    //The node's scalar predicates. The ones that hold nodes are its edges, and are walked instead.
    private static Dictionary<string, string> ReadProperties(JsonElement node)
    {
        var properties = new Dictionary<string, string>();

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == UidField || property.Name == TypeField || HoldsNode(property.Value))
                continue;

            //A "predicate|facet" key describes the edge that reached this node, not the node — it is read
            //as the edge's property instead, by ReadFacets.
            if (property.Name.IndexOf(FacetSeparator) >= 0)
                continue;

            string value = GraphWireText.Stringify(property.Value);

            if (value != null)
                properties[property.Name] = value;
        }

        return properties;
    }

    private static GraphDbResult BuildTable(List<JsonElement> rows, string raw)
    {
        var columns = new List<string>();

        foreach (var row in rows)
            if (row.ValueKind == JsonValueKind.Object)
                foreach (var property in row.EnumerateObject())
                    if (!columns.Contains(property.Name))
                        columns.Add(property.Name);

        if (columns.Count == 0)
            columns.Add(ValueColumn);

        var table = new GraphDbTable { Vars = columns };

        foreach (var row in rows)
        {
            var cells = new Dictionary<string, string>();

            foreach (var column in columns)
                cells[column] = "";

            if (row.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in row.EnumerateObject())
                    cells[property.Name] = GraphWireText.Stringify(property.Value) ?? "";
            }
            else
            {
                cells[columns[0]] = GraphWireText.Stringify(row) ?? "";
            }

            table.Rows.Add(cells);
        }

        return GraphDbResult.Tabular(table, raw);
    }

    private static JsonElement BuildGraph(Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        var elements = new List<object>();
        elements.AddRange(nodes.Values);
        elements.AddRange(edges.Values);

        return JsonDocument.Parse(JsonSerializer.Serialize(elements)).RootElement;
    }

    private static JsonElement EmptyGraph()
    {
        return JsonDocument.Parse("[]").RootElement;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }
}
