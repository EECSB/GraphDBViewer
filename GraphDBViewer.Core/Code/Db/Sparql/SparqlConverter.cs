using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>The shape of a SPARQL response: a SELECT table, an ASK boolean, a CONSTRUCT/DESCRIBE graph, or unrecognized.</summary>
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

///<summary>What an RDF term is: a named resource, a blank node, or a literal value.</summary>
public enum RdfTermKind { Iri, Blank, Literal }

///<summary>One RDF term. A literal carries its datatype and language, which the graph does not show but a test can read.</summary>
public sealed record RdfTerm(RdfTermKind Kind, string Value, string? Datatype = null, string? Language = null);

///<summary>One RDF statement, as a SPARQL graph answer is read before it is drawn.</summary>
public sealed record RdfTriple(string Subject, string Predicate, RdfTerm Object);

///<summary>
///Turns a SPARQL HTTP response into a <see cref="SparqlResult"/>: SPARQL-Results-JSON becomes a
///SELECT bindings table or an ASK boolean; RDF/JSON or JSON-LD (CONSTRUCT/DESCRIBE) becomes a graph in the
///same flat vertex/edge JSON the rest of the app renders (each IRI a vertex, each predicate an edge, and
///literal objects folded into the subject's properties).
///</summary>
public static class SparqlConverter
{
    ///<summary>The response body, and the media type the endpoint labeled it with where that is known.</summary>
    public static SparqlResult Parse(string body, string? mediaType = null)
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

            //JSON-LD is JSON too, and read as RDF/JSON its "@graph" was taken for the only subject. SparqlDb asks for
            //RDF/JSON first, so this is an endpoint that does not offer it, and its answer is read as what it is.
            if (IsJsonLd(root, mediaType))
            {
                ReadJsonLd(root, result);
                return result;
            }

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
            result.GraphData = JsonDocument.Parse(RdfJsonToGraph(root)).RootElement;
            return result;
        }
    }

    //A JSON-LD document names its nodes with keywords (@graph, @id, @context), at the top or on each node of an
    //expanded array. Neither RDF/JSON nor SPARQL results has a key starting with "@", since an RDF/JSON subject is
    //an absolute IRI or a blank node label, and neither is ever an array: so an array is JSON-LD as well, including
    //the "[]" Oxigraph answers for a graph with nothing in it. The label, where there is one, settles it.
    private static bool IsJsonLd(JsonElement root, string? mediaType)
    {
        if (string.Equals(mediaType, "application/ld+json", System.StringComparison.OrdinalIgnoreCase))
            return true;

        if (root.ValueKind == JsonValueKind.Array)
            return true;

        return root.ValueKind == JsonValueKind.Object && HasKeyword(root);
    }

    private static bool HasKeyword(JsonElement node)
    {
        foreach (var property in node.EnumerateObject())
            if (property.Name.StartsWith('@'))
                return true;

        return false;
    }

    private static void ReadJsonLd(JsonElement root, SparqlResult result)
    {
        var graph = new GraphBuilder();

        try
        {
            foreach (var triple in JsonLdReader.Read(root))
                graph.Add(triple);
        }
        catch (JsonLdException ex)
        {
            result.IsError = true;
            result.Error = $"The endpoint answered in JSON-LD that could not be read: {ex.Message}.";
            return;
        }

        result.Kind = SparqlKind.Graph;
        result.GraphData = JsonDocument.Parse(graph.ToJson()).RootElement;
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
    private static string RdfJsonToGraph(JsonElement root)
    {
        var graph = new GraphBuilder();

        foreach (var subject in root.EnumerateObject())
        {
            //A subject is drawn even when nothing about it could be read.
            graph.Vertex(subject.Name);

            if (subject.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var predicate in subject.Value.EnumerateObject())
            {
                if (predicate.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var obj in predicate.Value.EnumerateArray())
                    if (obj.ValueKind == JsonValueKind.Object)
                        graph.Add(new RdfTriple(subject.Name, predicate.Name, RdfJsonTerm(obj)));
            }
        }

        return graph.ToJson();
    }

    private static RdfTerm RdfJsonTerm(JsonElement obj)
    {
        var type = "literal";
        var value = "";

        if (obj.TryGetProperty("type", out var t))
            type = t.GetString();

        if (obj.TryGetProperty("value", out var val))
            value = val.GetString() ?? "";

        if (type == "uri")
            return new RdfTerm(RdfTermKind.Iri, value);
        else if (type == "bnode")
            return new RdfTerm(RdfTermKind.Blank, value);

        return new RdfTerm(RdfTermKind.Literal, value);
    }

    ///<summary>
    ///The graph a SPARQL answer is drawn as, whichever format stated its triples: each IRI or blank node a vertex,
    ///each link an edge named for its predicate, and each literal folded into its subject's properties.
    ///</summary>
    private sealed class GraphBuilder
    {
        private readonly Dictionary<string, Dictionary<string, string>> _vertices = new();
        //Per vertex, the predicate each property name was folded from, so that a second predicate with the same local
        //name is told from a second value of the first.
        private readonly Dictionary<string, Dictionary<string, string>> _foldedFrom = new();
        private readonly List<object> _edges = new();
        private readonly HashSet<string> _edgeIds = new();

        public Dictionary<string, string> Vertex(string iri)
        {
            if (!_vertices.TryGetValue(iri, out var properties))
            {
                properties = new Dictionary<string, string> { ["uri"] = iri };
                _vertices[iri] = properties;
                _foldedFrom[iri] = new Dictionary<string, string>();
            }

            return properties;
        }

        public void Add(RdfTriple triple)
        {
            var properties = Vertex(triple.Subject);
            var name = LocalName(triple.Predicate);

            if (triple.Object.Kind == RdfTermKind.Literal)
            {
                properties[PropertyName(triple.Subject, triple.Predicate, name)] = triple.Object.Value;
                return;
            }

            Vertex(triple.Object.Value);

            var id = $"{triple.Subject}|{triple.Predicate}|{triple.Object.Value}";

            //Once: a JSON-LD document that describes a node in two places states some of its triples twice.
            if (_edgeIds.Add(id))
                _edges.Add(new { id, label = name, outV = triple.Subject, inV = triple.Object.Value, properties = new Dictionary<string, string>() });
        }

        //The name a literal is folded under: the predicate's local name, unless that is "uri", which holds the vertex's
        //own IRI and which the viewer needs, or another predicate of this subject already holds it (ex:name beside
        //foaf:name); then the predicate's full IRI, so that no value writes over another's. A second value of the same
        //predicate keeps the name, and the last one stays, as the viewer shows one value of a Gremlin multi-property.
        //Until 2026-11-07 every literal went under its local name, and the last writer won.
        private string PropertyName(string subject, string predicate, string name)
        {
            if (name == "uri")
                return predicate;

            var foldedFrom = _foldedFrom[subject];

            if (foldedFrom.TryGetValue(name, out var holder) && holder != predicate)
                return predicate;

            foldedFrom[name] = predicate;

            return name;
        }

        public string ToJson()
        {
            var elements = new List<object>();

            foreach (var (iri, properties) in _vertices)
                elements.Add(new { id = iri, label = LocalName(iri), properties });

            elements.AddRange(_edges);

            return JsonSerializer.Serialize(elements);
        }
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
