using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Maps an ArangoDB AQL answer onto the shared <see cref="GraphDbResult"/>.
///
///ArangoDB has no distinct vertex/edge types on the wire — everything is a JSON document. What makes a
///document a graph element is its metadata: <c>_id</c> identifies it, and a document that also carries
///<c>_from</c> and <c>_to</c> is an edge. An <c>_id</c> is <c>"collection/key"</c>, and the collection is
///the closest thing ArangoDB has to a label, so that is what the viewer groups and colors by.
///
///A result holding any document becomes a graph; anything else (projections, counts, plain values) becomes
///a table — the same split <see cref="Neo4jConverter"/> and SPARQL make.
///</summary>
public static class ArangoConverter
{
    public const string IdField = "_id";
    public const string KeyField = "_key";
    public const string RevisionField = "_rev";
    public const string FromField = "_from";
    public const string ToField = "_to";

    ///<summary>The single column a result of bare values is shown under, since AQL names only projections.</summary>
    public const string ValueColumn = "value";

    //Arango's own document metadata, dropped rather than repeated as properties: _id and _from / _to are
    //already the element's id and endpoints, _key is just the tail of _id, and _rev is storage plumbing.
    //Keeping any of them would be pointless besides — GraphDataConverter filters every "_"-prefixed
    //property key out of the render data, so they would never reach the UI anyway.
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        IdField, KeyField, RevisionField, FromField, ToField
    };

    ///<summary>Builds a result from the <c>result</c> array of an AQL response.</summary>
    public static GraphDbResult ToGraphDbResult(JsonElement rows, string raw = null)
    {
        if (rows.ValueKind != JsonValueKind.Array)
            return GraphDbResult.Success(EmptyGraph(), raw);

        var nodes = new Dictionary<string, object>();
        var edges = new Dictionary<string, object>();

        foreach (var row in rows.EnumerateArray())
            Collect(row, nodes, edges);

        if (nodes.Count > 0 || edges.Count > 0)
            return GraphDbResult.Success(BuildGraph(nodes, edges), raw);

        //A query that matched nothing stays a graph, so a graph query with no hits lands on the graph view
        //rather than an empty table.
        if (rows.GetArrayLength() == 0)
            return GraphDbResult.Success(EmptyGraph(), raw);

        return BuildTable(rows, raw);
    }

    //Walks one row, gathering every document it contains — directly, inside a traversal path
    //({vertices, edges}), or nested in a returned list or object.
    private static void Collect(JsonElement value, Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                Collect(item, nodes, edges);

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
            return;

        if (IsDocument(value))
        {
            Add(value, nodes, edges);

            return;
        }

        //Not a document itself — a projection, or a traversal path's {vertices, edges} — so look inside.
        foreach (var property in value.EnumerateObject())
            Collect(property.Value, nodes, edges);
    }

    ///<summary>True when the object carries an <c>_id</c>, which is what makes it a stored document.</summary>
    public static bool IsDocument(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(IdField, out var id)
            && id.ValueKind == JsonValueKind.String;
    }

    ///<summary>True when the document is an edge — it names both ends.</summary>
    public static bool IsEdge(JsonElement element)
    {
        return IsDocument(element)
            && element.TryGetProperty(FromField, out _)
            && element.TryGetProperty(ToField, out _);
    }

    private static void Add(JsonElement document, Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        string id = GetString(document, IdField);

        if (id == null)
            return;

        if (IsEdge(document))
        {
            if (edges.ContainsKey(id))
                return;

            edges[id] = new
            {
                id,
                label = CollectionOf(id),
                outV = GetString(document, FromField),
                inV = GetString(document, ToField),
                properties = ReadProperties(document)
            };

            return;
        }

        if (nodes.ContainsKey(id))
            return;

        nodes[id] = new { id, label = CollectionOf(id), properties = ReadProperties(document) };
    }

    ///<summary>
    ///The collection an <c>_id</c> belongs to — <c>"persons/1"</c> → <c>"persons"</c>. This is the label the
    ///viewer shows, ArangoDB having no separate notion of one. An id without a slash is returned unchanged.
    ///</summary>
    public static string CollectionOf(string id)
    {
        if (string.IsNullOrEmpty(id))
            return "document";

        int slash = id.IndexOf('/');

        if (slash <= 0)
            return id;

        return id.Substring(0, slash);
    }

    private static Dictionary<string, string> ReadProperties(JsonElement document)
    {
        var properties = new Dictionary<string, string>();

        foreach (var property in document.EnumerateObject())
        {
            if (MetadataFields.Contains(property.Name))
                continue;

            string value = GraphWireText.Stringify(property.Value);

            if (value != null)
                properties[property.Name] = value;
        }

        return properties;
    }

    //Flattens a JSON value to the string the property map (and the table's cells) hold. Numbers keep their
    //exact text; a nested list or object keeps its JSON.
    private static GraphDbResult BuildTable(JsonElement rows, string raw)
    {
        var columns = new List<string>();
        var table = new GraphDbTable();

        //AQL names a column only when the query projects one; a row that is a bare value has none, so it
        //shows under a single "value" column.
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in row.EnumerateObject())
                    if (!columns.Contains(property.Name))
                        columns.Add(property.Name);
            }
            else if (!columns.Contains(ValueColumn))
            {
                columns.Add(ValueColumn);
            }
        }

        foreach (var row in rows.EnumerateArray())
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
                cells[ValueColumn] = GraphWireText.Stringify(row) ?? "";
            }

            table.Rows.Add(cells);
        }

        table.Vars = columns;

        return GraphDbResult.Tabular(table, raw);
    }

    private static JsonElement BuildGraph(Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        var elements = new List<object>();
        elements.AddRange(nodes.Values);
        elements.AddRange(edges.Values);

        //Reparsed into its own document so the element outlives the response's, as the other converters do.
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
