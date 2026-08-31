using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>The shape of a SPARQL response: a SELECT table, an ASK boolean, a CONSTRUCT/DESCRIBE graph, or unrecognised.</summary>
public enum SparqlKind { Select, Ask, Graph, Unknown }

///<summary>A parsed SPARQL response, normalized for the UI (a bindings table, a boolean, or a graph in the app's node/edge shape).</summary>
public class SparqlResult
{
    public bool IsError { get; set; }
    public string Error { get; set; }
    public string RawJson { get; set; }
    public SparqlKind Kind { get; set; } = SparqlKind.Unknown;
    public List<string> Vars { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
    public bool Boolean { get; set; }
    public JsonElement GraphData { get; set; }
}

///<summary>
///Turns a SPARQL HTTP response into a <see cref="SparqlResult"/>: SPARQL-Results-JSON becomes a
///SELECT bindings table or an ASK boolean; RDF/JSON (CONSTRUCT/DESCRIBE) becomes a graph in the same
///flat vertex/edge JSON the rest of the app renders (each IRI a vertex, each predicate an edge, and
///literal objects folded into the subject's properties).
///</summary>
public static class SparqlConverter
{
    public static SparqlResult Parse(string body)
    {
        var result = new SparqlResult { RawJson = Pretty(body) };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch
        {
            return result;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return result;

            if (root.TryGetProperty("boolean", out var b))
            {
                result.Kind = SparqlKind.Ask;
                result.Boolean = b.ValueKind == JsonValueKind.True;
                return result;
            }

            if (root.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("bindings", out var bindings))
            {
                ParseSelect(root, bindings, result);
                return result;
            }

            //No "head"/"results" — treat a plain object as RDF/JSON (subject → predicate → objects).
            result.Kind = SparqlKind.Graph;
            result.GraphData = JsonDocument.Parse(TriplesToGraph(root)).RootElement;
            return result;
        }
    }

    private static void ParseSelect(JsonElement root, JsonElement bindings, SparqlResult result)
    {
        result.Kind = SparqlKind.Select;

        if (root.TryGetProperty("head", out var head) && head.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Array)
            foreach (var v in vars.EnumerateArray())
                result.Vars.Add(v.GetString());

        foreach (var binding in bindings.EnumerateArray())
        {
            var row = new Dictionary<string, string>();

            foreach (var col in result.Vars)
                if (binding.TryGetProperty(col, out var cell) && cell.TryGetProperty("value", out var value))
                    row[col] = value.GetString() ?? value.ToString();

            result.Rows.Add(row);
        }
    }

    //RDF/JSON: { "subjectIRI": { "predicateIRI": [ { "type": "uri|literal|bnode", "value": "…" } ] } }.
    private static string TriplesToGraph(JsonElement root)
    {
        var vertices = new Dictionary<string, Dictionary<string, object>>();
        var edges = new List<object>();

        Dictionary<string, object> Vertex(string iri)
        {
            if (!vertices.TryGetValue(iri, out var v))
            {
                v = new Dictionary<string, object>
                {
                    ["id"] = iri,
                    ["label"] = LocalName(iri),
                    ["properties"] = new Dictionary<string, string> { ["uri"] = iri }
                };
                vertices[iri] = v;
            }

            return v;
        }

        foreach (var subject in root.EnumerateObject())
        {
            var subjectVertex = Vertex(subject.Name);
            var props = (Dictionary<string, string>)subjectVertex["properties"];

            if (subject.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var predicate in subject.Value.EnumerateObject())
            {
                var predicateName = LocalName(predicate.Name);

                if (predicate.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var obj in predicate.Value.EnumerateArray())
                {
                    string type = obj.TryGetProperty("type", out var t) ? t.GetString() : "literal";
                    string value = obj.TryGetProperty("value", out var val) ? val.GetString() : "";

                    if (type == "uri" || type == "bnode")
                    {
                        Vertex(value);
                        edges.Add(new { id = $"{subject.Name}|{predicate.Name}|{value}", label = predicateName, outV = subject.Name, inV = value, properties = new Dictionary<string, string>() });
                    }
                    else
                    {
                        props[predicateName] = value;
                    }
                }
            }
        }

        var elements = new List<object>();
        foreach (var v in vertices.Values)
            elements.Add(new { id = v["id"], label = v["label"], properties = v["properties"] });

        elements.AddRange(edges);

        return JsonSerializer.Serialize(elements);
    }

    //The last path/fragment segment of an IRI, for readable vertex/edge labels.
    private static string LocalName(string iri)
    {
        if (string.IsNullOrEmpty(iri))
            return iri;

        int cut = System.Math.Max(iri.LastIndexOf('#'), iri.LastIndexOf('/'));

        if (cut >= 0 && cut < iri.Length - 1)
            return iri.Substring(cut + 1);

        return iri;
    }

    private static string Pretty(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return body;
        }
    }
}
