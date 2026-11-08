using System.Linq;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class SparqlConverterTests
{
    [Fact]
    public void Parse_SelectResults_ProducesVarsAndRows()
    {
        var body = """
        {
          "head": { "vars": ["s","name"] },
          "results": { "bindings": [
            { "s": {"type":"uri","value":"http://ex/1"}, "name": {"type":"literal","value":"Alice"} },
            { "s": {"type":"uri","value":"http://ex/2"}, "name": {"type":"literal","value":"Bob"} }
          ] }
        }
        """;

        var result = SparqlConverter.Parse(body);

        Assert.Equal(SparqlKind.Select, result.Kind);
        Assert.Equal(new[] { "s", "name" }, result.Vars);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("http://ex/1", result.Rows[0]["s"]);
        Assert.Equal("Alice", result.Rows[0]["name"]);
        Assert.Equal("Bob", result.Rows[1]["name"]);
    }

    [Fact]
    public void Parse_AskResult_ProducesBoolean()
    {
        var result = SparqlConverter.Parse("""{ "head": {}, "boolean": true }""");

        Assert.Equal(SparqlKind.Ask, result.Kind);
        Assert.True(result.Boolean);
    }

    [Fact]
    public void Parse_RdfJson_ProducesGraphOfVerticesAndEdges()
    {
        var body = """
        {
          "http://ex/alice": {
            "http://xmlns.com/foaf/0.1/name": [ {"type":"literal","value":"Alice"} ],
            "http://xmlns.com/foaf/0.1/knows": [ {"type":"uri","value":"http://ex/bob"} ]
          }
        }
        """;

        var result = SparqlConverter.Parse(body);

        Assert.Equal(SparqlKind.Graph, result.Kind);

        var table = GraphDataConverter.ToTable(result.GraphData);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Contains(table.Nodes, n => n.Id == "http://ex/alice");
        Assert.Contains(table.Nodes, n => n.Id == "http://ex/bob");

        var edge = Assert.Single(table.Edges);
        Assert.Equal("knows", edge.Label);
        Assert.Equal("http://ex/alice", edge.Source);
        Assert.Equal("http://ex/bob", edge.Target);

        var alice = table.Nodes.Single(n => n.Id == "http://ex/alice");
        Assert.Equal("Alice", alice.Properties["name"]);
    }

    //The vertex's own IRI is kept under "uri", which the viewer needs to name the node. A literal predicate whose local
    //name is uri overwrote it until 2026-11-07; now the vertex's uri wins and that predicate is keyed by its full IRI.
    [Fact]
    public void Parse_ALiteralPredicateNamedUri_DoesNotOverwriteTheVerticesOwnIri()
    {
        var body = """
        {
          "http://ex/s": {
            "http://x/uri": [ {"type":"literal","value":"v"} ]
          }
        }
        """;

        var node = Assert.Single(GraphDataConverter.ToTable(SparqlConverter.Parse(body).GraphData).Nodes);

        Assert.Equal("http://ex/s", node.Properties["uri"]);
        Assert.Equal("v", node.Properties["http://x/uri"]);
    }

    //Two predicates with one local name, ex:name beside foaf:name, collided on "name" until 2026-11-07, and the second
    //overwrote the first. The first keeps the local name; a predicate whose local name another predicate already holds
    //is keyed by its full IRI, so no value is lost.
    [Fact]
    public void Parse_TwoPredicatesWithOneLocalName_KeepBothValues_TheSecondUnderItsFullIri()
    {
        var body = """
        {
          "http://ex/s": {
            "http://example.org/name": [ {"type":"literal","value":"Ex"} ],
            "http://xmlns.com/foaf/0.1/name": [ {"type":"literal","value":"Foaf"} ]
          }
        }
        """;

        var node = Assert.Single(GraphDataConverter.ToTable(SparqlConverter.Parse(body).GraphData).Nodes);

        Assert.Equal("Ex", node.Properties["name"]);
        Assert.Equal("Foaf", node.Properties["http://xmlns.com/foaf/0.1/name"]);
    }

    //One predicate with two literal values is one property holding the last, as it has been: the viewer shows one value
    //of a Gremlin multi-property too (the user's choice of 2026-09-29). A second value of a predicate is not a second
    //predicate, so it is not keyed by the full IRI.
    [Fact]
    public void Parse_APredicateWithTwoLiteralValues_IsOnePropertyHoldingTheLast()
    {
        var body = """
        {
          "http://ex/s": {
            "http://x/tag": [ {"type":"literal","value":"a"}, {"type":"literal","value":"b"} ]
          }
        }
        """;

        var node = Assert.Single(GraphDataConverter.ToTable(SparqlConverter.Parse(body).GraphData).Nodes);

        Assert.Equal("b", node.Properties["tag"]);
        Assert.DoesNotContain("http://x/tag", node.Properties.Keys);
    }

    //── JSON-LD ──────────────────────────────────────────────────────────

    //Fuseki 6.2.0's answer to a CONSTRUCT when JSON-LD won the negotiation, as the live database check got it on
    //2026-09-13. Read as RDF/JSON, "@graph" was the only subject and neither node was drawn; until 2026-09-15 the
    //converter said it could not read JSON-LD at all.
    [Fact]
    public void Parse_JsonLd_DrawsItsNodesAndTheEdgeBetweenThem()
    {
        var body = """
        {
          "@graph": [
            { "@id": "urn:gdbv:check-b", "http://www.w3.org/2000/01/rdf-schema#label": "check-b" },
            { "@id": "urn:gdbv:check-a", "http://www.w3.org/2000/01/rdf-schema#label": "check-a", "urn:gdbv:knows": { "@id": "urn:gdbv:check-b" } }
          ]
        }
        """;

        var result = SparqlConverter.Parse(body);

        Assert.False(result.IsError, result.Error);
        Assert.Equal(SparqlKind.Graph, result.Kind);

        var table = GraphDataConverter.ToTable(result.GraphData);

        Assert.Contains(table.Nodes, n => n.Id == "urn:gdbv:check-a");
        Assert.Contains(table.Nodes, n => n.Id == "urn:gdbv:check-b");

        var edge = Assert.Single(table.Edges);
        Assert.Equal("urn:gdbv:check-a", edge.Source);
        Assert.Equal("urn:gdbv:check-b", edge.Target);
    }

    //Expanded JSON-LD is a bare array of nodes, which fell through to an empty bindings table before it was reported.
    [Fact]
    public void Parse_ExpandedJsonLd_IsDrawnToo()
    {
        var result = SparqlConverter.Parse("""[ { "@id": "urn:gdbv:check-a", "urn:gdbv:knows": [ { "@id": "urn:gdbv:check-b" } ] } ]""");

        Assert.False(result.IsError, result.Error);

        var edge = Assert.Single(GraphDataConverter.ToTable(result.GraphData).Edges);

        Assert.Equal("urn:gdbv:check-a", edge.Source);
        Assert.Equal("urn:gdbv:check-b", edge.Target);
    }

    //The same CONSTRUCT asked of the same Fuseki twice, 2026-09-15: once for JSON-LD and once for RDF/JSON, over data
    //with prefixes, typed and language-tagged values, a blank node, a list and a value on two lines. One graph.
    [Fact]
    public void Parse_FusekisJsonLd_DrawsWhatItsRdfJsonDraws()
    {
        Assert.Equal(Drawn(SparqlConverter.Parse(FusekiRdfJson)), Drawn(SparqlConverter.Parse(FusekiJsonLd)));
    }

    //Oxigraph 0.5.11 has no RDF/JSON, so the viewer's own Accept header gets this; the data is the same as Fuseki's.
    //One difference is the stores' and not the reader's: Oxigraph writes the double 9.5 where Fuseki writes 9.5E0.
    [Fact]
    public void Parse_OxigraphsJsonLd_DrawsWhatFusekisRdfJsonDraws()
    {
        var expected = Drawn(SparqlConverter.Parse(FusekiRdfJson)).Select(line => line.Replace("score=9.5E0", "score=9.5")).OrderBy(line => line, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, Drawn(SparqlConverter.Parse(OxigraphJsonLd, "application/ld+json")));
    }

    //rdfs:label is the caption the canvas draws for an IRI, and its local name is "label" — which the table read as
    //the element's own metadata until 2026-09-16 and left out of every column. It is the one property most of the
    //RDF world names, so a Fuseki answer had a node captioned Alice with no column saying where that came from.
    [Fact]
    public void Parse_RdfsLabel_IsAColumnAndNotReadAsTheVerticesLabel()
    {
        var table = GraphDataConverter.ToTable(SparqlConverter.Parse(FusekiJsonLd).GraphData);
        var alice = table.Nodes.Single(n => n.Id == "http://example.org/alice");

        Assert.Contains("label", table.NodePropertyColumns);
        Assert.Equal("Alice", alice.Properties["label"]);
        Assert.Equal("alice", alice.Label);
    }

    //A node described in two places states its triples twice. The 2D view adds every edge it is handed, and
    //Cytoscape refuses a second element with an id it already has, so a triple is one edge however often it is
    //stated. (The table dedupes on its own, which is why this reads the 2D view's elements.)
    [Fact]
    public void Parse_ATripleStatedTwice_IsOneEdge()
    {
        var body = """
        {
          "@context": {"@vocab": "http://example.org/"},
          "@graph": [
            {"@id": "http://example.org/a", "knows": {"@id": "http://example.org/b"}},
            {"@id": "http://example.org/a", "knows": {"@id": "http://example.org/b"}, "name": "A"}
          ]
        }
        """;

        var result = SparqlConverter.Parse(body);

        using var drawn = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(result.GraphData));

        Assert.Single(drawn.RootElement.EnumerateArray(), element => element.GetProperty("data").TryGetProperty("source", out _));
        Assert.Equal("A", GraphDataConverter.ToTable(result.GraphData).Nodes.Single(n => n.Id == "http://example.org/a").Properties["name"]);
    }

    //Oxigraph answers an empty CONSTRUCT with "[]" and Fuseki with "{}", which only its label says is JSON-LD. Both
    //are a graph with nothing in it, not a table.
    [Theory]
    [InlineData("[]", null)]
    [InlineData("{\n}", "application/ld+json")]
    public void Parse_AnEmptyJsonLdAnswer_IsAnEmptyGraph(string body, string? mediaType)
    {
        var result = SparqlConverter.Parse(body, mediaType);

        Assert.False(result.IsError, result.Error);
        Assert.Equal(SparqlKind.Graph, result.Kind);
        Assert.Empty(GraphDataConverter.ToTable(result.GraphData).Nodes);
    }

    //Labeled JSON-LD, it is read as JSON-LD even with no keyword in it: here a blank node with one value, which as
    //RDF/JSON would have been a node named for the property with nothing on it.
    [Fact]
    public void Parse_AnAnswerLabeledJsonLd_IsReadAsJsonLd_EvenWithoutAKeyword()
    {
        var result = SparqlConverter.Parse("""{ "http://example.org/color": "red" }""", "application/ld+json");

        var node = Assert.Single(GraphDataConverter.ToTable(result.GraphData).Nodes);

        Assert.Equal("red", node.Properties["color"]);
    }

    [Fact]
    public void Parse_JsonLdWhoseContextIsAUrl_SaysSo_RatherThanDrawingAGuess()
    {
        var result = SparqlConverter.Parse("""{ "@context": "https://schema.org/", "@id": "http://example.org/ada", "name": "Ada" }""");

        Assert.True(result.IsError);
        Assert.Contains("JSON-LD", result.Error);
        Assert.Contains("https://schema.org/", result.Error);
    }

    //What a graph draws, as sorted lines, with every blank node written "_": a store's blank node labels are its own,
    //and JSON-LD leaves a list's nodes for the reader to name where RDF/JSON names them.
    private static List<string> Drawn(SparqlResult result)
    {
        Assert.False(result.IsError, result.Error);
        Assert.Equal(SparqlKind.Graph, result.Kind);

        var table = GraphDataConverter.ToTable(result.GraphData);
        var lines = new List<string>();

        foreach (var node in table.Nodes)
        {
            var values = node.Properties.Where(p => p.Key != "uri").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}");

            lines.Add(string.Join(" ", new[] { Blank(node.Id) }.Concat(values)));
        }

        foreach (var edge in table.Edges)
            lines.Add($"{Blank(edge.Source)} -{edge.Label}-> {Blank(edge.Target)}");

        return lines.OrderBy(line => line, StringComparer.Ordinal).ToList();
    }

    private static string Blank(string id)
    {
        if (id.StartsWith("_:"))
            return "_";

        return id;
    }

    //── The answers, as the endpoints gave them on 2026-09-15 ────────────

    private const string FusekiJsonLd = """
    {
      "@graph": [
        {"@id": "_:afe98382-6645-4c56-b55f-2888ea436225", "foaf:knows": {"@id": "ex:alice"}, "foaf:name": "Carol"},
        {"@id": "ex:alice", "ex:note": "line one\nline two", "ex:tags": {"@list": ["red", "green", {"@id": "ex:blue"}]}, "foaf:knows": [{"@id": "_:afe98382-6645-4c56-b55f-2888ea436225"}, {"@id": "ex:bob"}], "ex:born": {"@value": "1984-03-02", "@type": "xsd:date"}, "ex:member": {"@value": "true", "@type": "xsd:boolean"}, "ex:score": {"@value": "9.5E0", "@type": "xsd:double"}, "ex:height": {"@value": "1.68", "@type": "xsd:decimal"}, "foaf:age": {"@value": "42", "@type": "xsd:integer"}, "rdfs:label": [{"@language": "es", "@value": "Alicia"}, {"@language": "en", "@value": "Alice"}], "foaf:name": "Alice", "@type": "foaf:Person"},
        {"@id": "ex:bob", "foaf:name": "Bob", "@type": "foaf:Person"}
      ],
      "@context": {"xsd": "http://www.w3.org/2001/XMLSchema#", "rdfs": "http://www.w3.org/2000/01/rdf-schema#", "ex": "http://example.org/", "foaf": "http://xmlns.com/foaf/0.1/"}
    }
    """;

    private const string FusekiRdfJson = """
    {
      "_:e8845e1e-b80f-48aa-ac5e-c6883eb07dba": {"http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"type": "bnode", "value": "_:1a7e6bce-c20c-45a5-aab8-3a524275ff3b"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"type": "literal", "value": "red"}]},
      "http://example.org/bob": {"http://xmlns.com/foaf/0.1/name": [{"type": "literal", "value": "Bob"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#type": [{"type": "uri", "value": "http://xmlns.com/foaf/0.1/Person"}]},
      "_:1a7e6bce-c20c-45a5-aab8-3a524275ff3b": {"http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"type": "bnode", "value": "_:df8e1bbf-2e0c-4528-a197-2084a5152e00"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"type": "literal", "value": "green"}]},
      "_:df8e1bbf-2e0c-4528-a197-2084a5152e00": {"http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"type": "uri", "value": "http://www.w3.org/1999/02/22-rdf-syntax-ns#nil"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"type": "uri", "value": "http://example.org/blue"}]},
      "_:afe98382-6645-4c56-b55f-2888ea436225": {"http://xmlns.com/foaf/0.1/name": [{"type": "literal", "value": "Carol"}], "http://xmlns.com/foaf/0.1/knows": [{"type": "uri", "value": "http://example.org/alice"}]},
      "http://example.org/alice": {"http://example.org/tags": [{"type": "bnode", "value": "_:e8845e1e-b80f-48aa-ac5e-c6883eb07dba"}], "http://example.org/score": [{"type": "literal", "value": "9.5E0", "datatype": "http://www.w3.org/2001/XMLSchema#double"}], "http://xmlns.com/foaf/0.1/name": [{"type": "literal", "value": "Alice"}], "http://example.org/note": [{"type": "literal", "value": "line one\nline two"}], "http://xmlns.com/foaf/0.1/knows": [{"type": "uri", "value": "http://example.org/bob"}, {"type": "bnode", "value": "_:afe98382-6645-4c56-b55f-2888ea436225"}], "http://example.org/born": [{"type": "literal", "value": "1984-03-02", "datatype": "http://www.w3.org/2001/XMLSchema#date"}], "http://example.org/member": [{"type": "literal", "value": "true", "datatype": "http://www.w3.org/2001/XMLSchema#boolean"}], "http://xmlns.com/foaf/0.1/age": [{"type": "literal", "value": "42", "datatype": "http://www.w3.org/2001/XMLSchema#integer"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#type": [{"type": "uri", "value": "http://xmlns.com/foaf/0.1/Person"}], "http://example.org/height": [{"type": "literal", "value": "1.68", "datatype": "http://www.w3.org/2001/XMLSchema#decimal"}], "http://www.w3.org/2000/01/rdf-schema#label": [{"type": "literal", "value": "Alicia", "lang": "es"}, {"type": "literal", "value": "Alice", "lang": "en"}]}
    }
    """;

    private const string OxigraphJsonLd = """
    [
      {"@id": "_:e9994ee7899d54cec5d45cbb78790824", "http://xmlns.com/foaf/0.1/knows": [{"@id": "http://example.org/alice"}], "http://xmlns.com/foaf/0.1/name": [{"@value": "Carol"}]},
      {"@id": "http://example.org/bob", "http://xmlns.com/foaf/0.1/name": [{"@value": "Bob"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#type": [{"@id": "http://xmlns.com/foaf/0.1/Person"}]},
      {"@id": "http://example.org/alice", "http://example.org/note": [{"@value": "line one\nline two"}], "http://example.org/tags": [{"@id": "_:c18d74018ecc8dc88317e3e67cad5274"}], "http://xmlns.com/foaf/0.1/knows": [{"@id": "_:e9994ee7899d54cec5d45cbb78790824"}, {"@id": "http://example.org/bob"}], "http://example.org/born": [{"@type": "http://www.w3.org/2001/XMLSchema#date", "@value": "1984-03-02"}], "http://example.org/member": [{"@type": "http://www.w3.org/2001/XMLSchema#boolean", "@value": "true"}], "http://example.org/score": [{"@type": "http://www.w3.org/2001/XMLSchema#double", "@value": "9.5"}], "http://example.org/height": [{"@type": "http://www.w3.org/2001/XMLSchema#decimal", "@value": "1.68"}], "http://xmlns.com/foaf/0.1/age": [{"@type": "http://www.w3.org/2001/XMLSchema#integer", "@value": "42"}], "http://www.w3.org/2000/01/rdf-schema#label": [{"@language": "es", "@value": "Alicia"}, {"@language": "en", "@value": "Alice"}], "http://xmlns.com/foaf/0.1/name": [{"@value": "Alice"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#type": [{"@id": "http://xmlns.com/foaf/0.1/Person"}]},
      {"@id": "_:c18d74018ecc8dc88317e3e67cad5274", "http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"@id": "_:e212030565c5952dffe4c64ad437fc29"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"@value": "red"}]},
      {"@id": "_:e212030565c5952dffe4c64ad437fc29", "http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"@id": "_:deb98e538964395b4aedbda1c6a167c7"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"@value": "green"}]},
      {"@id": "_:deb98e538964395b4aedbda1c6a167c7", "http://www.w3.org/1999/02/22-rdf-syntax-ns#rest": [{"@id": "http://www.w3.org/1999/02/22-rdf-syntax-ns#nil"}], "http://www.w3.org/1999/02/22-rdf-syntax-ns#first": [{"@id": "http://example.org/blue"}]}
    ]
    """;
}
