using System.Linq;
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

    //Fuseki 6.2.0's answer to a CONSTRUCT when JSON-LD won the negotiation, as the live database check got
    //it. Read as RDF/JSON, "@graph" was taken for the only subject and neither node was drawn.
    [Fact]
    public void Parse_JsonLd_IsReportedRatherThanMisreadAsRdfJson()
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

        Assert.True(result.IsError);
        Assert.Contains("JSON-LD", result.Error);
    }

    //Expanded JSON-LD is a bare array of nodes, which fell through to an empty bindings table.
    [Fact]
    public void Parse_ExpandedJsonLd_IsReportedToo()
    {
        var result = SparqlConverter.Parse("""[ { "@id": "urn:gdbv:check-a", "urn:gdbv:knows": [ { "@id": "urn:gdbv:check-b" } ] } ]""");

        Assert.True(result.IsError);
        Assert.Contains("JSON-LD", result.Error);
    }
}
