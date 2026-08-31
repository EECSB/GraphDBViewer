using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>The vocabulary used for schema-aware editor autocomplete: distinct vertex labels, edge labels and property keys.</summary>
public class SchemaVocabulary
{
    public List<string> VertexLabels { get; set; } = new();
    public List<string> EdgeLabels { get; set; } = new();
    public List<string> PropertyKeys { get; set; } = new();
}

///<summary>
///Turns the schema queries (vertex/edge label counts, per-label property keys, and
///relationship triples) into a synthetic graph the normal viewer can render: each vertex
///label becomes a node (with count + property keys), each relationship triple an edge.
///</summary>
public static class SchemaBuilder
{
    public static string BuildSchemaGraphJson(JsonElement vertexLabelCounts, JsonElement edgeLabelCounts, JsonElement vertexKeys, JsonElement edgeTriples)
    {
        return BuildSchemaGraphJson(
            ReadCountMap(vertexLabelCounts),
            ReadCountMap(edgeLabelCounts),
            ReadKeysMap(vertexKeys),
            ReadTriples(edgeTriples));
    }

    ///<summary>
    ///Builds the schema meta-graph from schema facts that have already been read out of whatever shape the
    ///engine answered in — the GraphSON overload above parses Gremlin's maps into these, and the Cypher
    ///source reads its own rows into them, so both produce an identical meta-graph.
    ///</summary>
    public static string BuildSchemaGraphJson(
        Dictionary<string, long> vCounts,
        Dictionary<string, long> eCounts,
        Dictionary<string, List<string>> vKeys,
        List<(string Out, string Edge, string In)> triples)
    {
        //Collect every vertex label seen in counts or as a relationship endpoint.
        var labels = new HashSet<string>(vCounts.Keys);
        foreach (var t in triples)
        {
            labels.Add(t.Out);
            labels.Add(t.In);
        }

        var elements = new List<object>();

        foreach (var label in labels)
        {
            var props = new Dictionary<string, string> { ["name"] = label };

            if (vCounts.TryGetValue(label, out var count))
                props["count"] = count.ToString();

            if (vKeys.TryGetValue(label, out var keys) && keys.Count > 0)
                props["keys"] = string.Join(", ", keys);

            elements.Add(new { id = label, label, properties = props });
        }

        foreach (var t in triples)
        {
            var props = new Dictionary<string, string>();

            if (eCounts.TryGetValue(t.Edge, out var count))
                props["count"] = count.ToString();

            elements.Add(new { id = $"{t.Out}-{t.Edge}->{t.In}", label = t.Edge, outV = t.Out, inV = t.In, properties = props });
        }

        return JsonSerializer.Serialize(elements);
    }

    ///<summary>Extracts the distinct vertex labels, edge labels and (union of per-label) property keys for autocomplete.</summary>
    public static SchemaVocabulary ExtractVocabulary(JsonElement vertexLabelCounts, JsonElement edgeLabelCounts, JsonElement vertexKeys)
    {
        return ExtractVocabulary(
            ReadCountMap(vertexLabelCounts),
            ReadCountMap(edgeLabelCounts),
            ReadKeysMap(vertexKeys));
    }

    ///<summary>The vocabulary from already-read schema facts — see the note on the sibling overload.</summary>
    public static SchemaVocabulary ExtractVocabulary(
        Dictionary<string, long> vCounts,
        Dictionary<string, long> eCounts,
        Dictionary<string, List<string>> vKeys)
    {
        var keys = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var entry in vKeys)
            foreach (var key in entry.Value)
                keys.Add(key);

        return new SchemaVocabulary
        {
            VertexLabels = vCounts.Keys.OrderBy(x => x).ToList(),
            EdgeLabels = eCounts.Keys.OrderBy(x => x).ToList(),
            PropertyKeys = keys.ToList()
        };
    }

    private static JsonElement FirstMap(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
            return GraphDataConverter.UnwrapElement(result[0]);

        return GraphDataConverter.UnwrapElement(result);
    }

    private static Dictionary<string, long> ReadCountMap(JsonElement result)
    {
        var map = new Dictionary<string, long>();
        var obj = FirstMap(result);

        if (obj.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var p in obj.EnumerateObject())
        {
            var val = GraphDataConverter.UnwrapElement(p.Value);

            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out var n))
                map[p.Name] = n;
            else if (long.TryParse(val.ToString(), out var n2))
                map[p.Name] = n2;
        }

        return map;
    }

    private static Dictionary<string, List<string>> ReadKeysMap(JsonElement result)
    {
        var map = new Dictionary<string, List<string>>();
        var obj = FirstMap(result);

        if (obj.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var p in obj.EnumerateObject())
        {
            var keys = new List<string>();
            var val = GraphDataConverter.UnwrapElement(p.Value);

            if (val.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in val.EnumerateArray())
                    keys.Add(GraphDataConverter.UnwrapElement(item).ToString());
            }

            map[p.Name] = keys;
        }

        return map;
    }

    private static List<(string Out, string Edge, string In)> ReadTriples(JsonElement result)
    {
        var list = new List<(string, string, string)>();

        if (result.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in result.EnumerateArray())
        {
            var obj = GraphDataConverter.UnwrapElement(item);
            if (obj.ValueKind != JsonValueKind.Object)
                continue;

            string outLabel = GetMapString(obj, "out");
            string edgeLabel = GetMapString(obj, "edge");
            string inLabel = GetMapString(obj, "in");

            if (outLabel != null && edgeLabel != null && inLabel != null)
                list.Add((outLabel, edgeLabel, inLabel));
        }

        return list;
    }

    private static string GetMapString(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var v))
            return GraphDataConverter.UnwrapElement(v).ToString();

        return null;
    }
}
