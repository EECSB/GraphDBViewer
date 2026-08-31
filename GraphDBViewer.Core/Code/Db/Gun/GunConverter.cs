using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Maps a GUN graph onto the shared <see cref="GraphDbResult"/>.
///
///GUN keeps a graph as a flat map of <b>souls</b> to nodes, and each node is itself a flat map of keys to
///values. A value is either a scalar — a property — or a <b>link</b>, written <c>{"#": "otherSoul"}</c>,
///which is an edge named by the key that holds it:
///<code>{"alice": {"_": {...}, "name": "Alice", "knows": {"#": "bob"}}}</code>
///is one node, one property and one <c>knows</c> edge.
///
///Like Dgraph, GUN gives an edge no identity of its own, so one is synthesised from the triple it is. And
///like Dgraph it has no notion of a type: a node's label falls back to a <c>type</c> property when the data
///happens to carry one, since nothing in GUN itself supplies one.
///</summary>
public static class GunConverter
{
    ///<summary>GUN's per-node metadata key, holding the soul and the per-key timestamps.</summary>
    public const string MetadataField = "_";

    ///<summary>The key inside a link — and inside the metadata — that names a soul.</summary>
    public const string SoulField = "#";

    ///<summary>Property consulted for a node's label, GUN having no type system of its own.</summary>
    public const string TypeProperty = "type";

    ///<summary>Label for a node whose data suggests no type.</summary>
    public const string DefaultLabel = "node";

    ///<summary>
    ///Builds a result from a GUN graph — souls mapped to their nodes.
    ///
    ///<paramref name="fillMissingEndpoints"/> is what a link pointing at a node the answer does not carry
    ///becomes. A whole read stands one in, so the edge is drawn rather than dropped. A <b>live push</b>
    ///must not: it carries the one node that changed, and its links point at nodes already on the canvas —
    ///standing in empty ones there would overwrite the real ones with blanks.
    ///</summary>
    public static GraphDbResult ToGraphDbResult(string graphJson, bool fillMissingEndpoints = true)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
            return GraphDbResult.Success(EmptyGraph());

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(graphJson);
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure($"Could not parse the GUN response: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return GraphDbResult.Failure(error.GetString() ?? "");

            if (root.ValueKind != JsonValueKind.Object)
                return GraphDbResult.Success(EmptyGraph(), graphJson);

            return FromGraph(root, graphJson, fillMissingEndpoints);
        }
    }

    private static GraphDbResult FromGraph(JsonElement graph, string raw, bool fillMissingEndpoints)
    {
        var nodes = new Dictionary<string, object>();
        var edges = new Dictionary<string, object>();

        foreach (var entry in graph.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            //The map key is the soul; the node's own metadata carries it too, and is preferred when present
            //because a peer may key an entry differently from what the node says it is.
            var soul = ReadSoul(entry.Value) ?? entry.Name;

            nodes[soul] = new
            {
                id = soul,
                label = ReadLabel(entry.Value),
                properties = ReadProperties(entry.Value)
            };

            foreach (var property in entry.Value.EnumerateObject())
            {
                if (property.Name == MetadataField)
                    continue;

                var target = ReadLink(property.Value);

                if (target == null)
                    continue;

                //GUN gives an edge no identity, so it is the triple.
                var id = EdgeId(soul, property.Name, target);

                if (edges.ContainsKey(id))
                    continue;

                edges[id] = new EdgeRecord
                {
                    id = id,
                    label = property.Name,
                    outV = soul,
                    inV = target,
                    properties = new Dictionary<string, string>()
                };
            }
        }

        //An edge may point at a soul the walk never fetched. Left dangling it would draw an id-only
        //placeholder, so the endpoint becomes a real (if empty) node instead.
        if (fillMissingEndpoints)
            foreach (var edge in new List<object>(edges.Values))
            {
                var target = (edge as EdgeRecord)?.inV;

                if (target != null && !nodes.ContainsKey(target))
                    nodes[target] = new { id = target, label = DefaultLabel, properties = new Dictionary<string, string>() };
            }

        if (nodes.Count == 0 && edges.Count == 0)
            return GraphDbResult.Success(EmptyGraph(), raw);

        return GraphDbResult.Success(BuildGraph(nodes, edges), raw);
    }

    //An edge, in the flat shape the renderers read. Named rather than anonymous so its target can be read
    //back with a cast — it used to be serialized to JSON and re-parsed to get at one field, once per edge.
    private sealed class EdgeRecord
    {
        public string id { get; init; }
        public string label { get; init; }
        public string outV { get; init; }
        public string inV { get; init; }
        public Dictionary<string, string> properties { get; init; }
    }

    ///<summary>
    ///The id an edge gets. GUN gives an edge none of its own — it is a key on a node holding a link — so
    ///the id is the triple it is, in the shape <see cref="GraphWireText"/> writes for every engine that has
    ///to synthesise one.
    ///</summary>
    public static string EdgeId(string source, string label, string target)
    {
        return GraphWireText.EdgeId(source, label, target);
    }

    ///<summary>Reads such an id back into its triple, or returns false when it is not one.</summary>
    public static bool TryReadEdgeId(string id, out string source, out string label, out string target)
    {
        return GraphWireText.TryReadEdgeId(id, out source, out label, out target);
    }

    ///<summary>The soul a node declares in its metadata, or null when it carries none.</summary>
    public static string ReadSoul(JsonElement node)
    {
        if (!node.TryGetProperty(MetadataField, out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;

        if (meta.TryGetProperty(SoulField, out var soul) && soul.ValueKind == JsonValueKind.String)
            return soul.GetString();

        return null;
    }

    ///<summary>
    ///The soul a value links to, or null when the value is not a link. A link is the only two-character
    ///object GUN uses: <c>{"#": "soul"}</c>.
    ///</summary>
    public static string ReadLink(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        if (!value.TryGetProperty(SoulField, out var soul) || soul.ValueKind != JsonValueKind.String)
            return null;

        return soul.GetString();
    }

    ///<summary>
    ///A node's label. GUN has no types, so this is the <c>type</c> property when the data carries one —
    ///a convention rather than something the database knows — and the generic label otherwise.
    ///</summary>
    public static string ReadLabel(JsonElement node)
    {
        if (node.TryGetProperty(TypeProperty, out var type) && type.ValueKind == JsonValueKind.String)
        {
            var label = type.GetString();

            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        return DefaultLabel;
    }

    //A node's scalar keys. The ones holding links are its edges, and are read as those instead.
    private static Dictionary<string, string> ReadProperties(JsonElement node)
    {
        var properties = new Dictionary<string, string>();

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == MetadataField || ReadLink(property.Value) != null)
                continue;

            var value = GraphWireText.Stringify(property.Value);

            if (value != null)
                properties[property.Name] = value;
        }

        return properties;
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
}
