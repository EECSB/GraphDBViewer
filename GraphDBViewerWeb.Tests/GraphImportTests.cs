using System.Linq;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GraphImportTests
{
    #region Detection

    [Theory]
    [InlineData("digraph { a -> b }")]
    [InlineData("graph { a -- b }")]
    [InlineData("graph LR\n A --> B")]
    [InlineData("flowchart TD\n A --> B")]
    public void LooksLikeGraphText_DotAndMermaid_True(string text)
    {
        Assert.True(GraphImport.LooksLikeGraphText(text));
    }

    [Theory]
    [InlineData("[ { \"id\": \"1\" } ]")]
    [InlineData("{ \"nodes\": [] }")]
    [InlineData("")]
    public void LooksLikeGraphText_JsonOrEmpty_False(string text)
    {
        Assert.False(GraphImport.LooksLikeGraphText(text));
    }

    #endregion

    #region DOT

    [Fact]
    public void Parse_Dot_ExtractsNodesEdgesLabelsAndProperties()
    {
        var dot = """
        digraph {
            alice [type=person, label="Alice", age=30]
            bob [type=person, label="Bob"]
            alice -> bob [label=knows, since=2020]
        }
        """;

        var g = GraphImport.Parse(dot);

        Assert.Equal(2, g.Nodes.Count);
        Assert.Single(g.Edges);

        var alice = g.Nodes.Single(n => n.Id == "alice");
        Assert.Equal("person", alice.Label);
        Assert.Equal("Alice", alice.Properties["name"]);
        Assert.Equal("30", alice.Properties["age"]);

        var edge = g.Edges.Single();
        Assert.Equal("alice", edge.Source);
        Assert.Equal("bob", edge.Target);
        Assert.Equal("knows", edge.Label);
        Assert.Equal("2020", edge.Properties["since"]);
    }

    [Fact]
    public void Parse_Dot_UndirectedAndChainedEdges()
    {
        var g = GraphImport.Parse("graph { a -- b -- c }");

        Assert.Equal(3, g.Nodes.Count);
        Assert.Equal(2, g.Edges.Count);
    }

    [Fact]
    public void Parse_Dot_IgnoresGraphAttributesAndDefaults()
    {
        var dot = """
        digraph {
            rankdir=LR
            node [shape=box]
            a -> b
        }
        """;

        var g = GraphImport.Parse(dot);

        Assert.Equal(2, g.Nodes.Count);
        Assert.Single(g.Edges);
        Assert.DoesNotContain(g.Nodes, n => n.Id == "rankdir" || n.Id == "node");
    }

    #endregion

    #region Mermaid

    [Fact]
    public void Parse_Mermaid_PipeLabelAndNodeText()
    {
        var mermaid = """
        graph LR
            A[Alice] -->|knows| B[Bob]
        """;

        var g = GraphImport.Parse(mermaid);

        Assert.Equal(2, g.Nodes.Count);
        Assert.Single(g.Edges);
        Assert.Equal("Alice", g.Nodes.Single(n => n.Id == "A").Properties["name"]);
        Assert.Equal("knows", g.Edges.Single().Label);
        Assert.Equal("A", g.Edges.Single().Source);
        Assert.Equal("B", g.Edges.Single().Target);
    }

    [Fact]
    public void Parse_Mermaid_InlineLabel()
    {
        var g = GraphImport.Parse("flowchart TD\n A -- knows --> B");

        Assert.Single(g.Edges);
        Assert.Equal("knows", g.Edges.Single().Label);
    }

    [Fact]
    public void Parse_Mermaid_UnlabeledEdgeDefaultsLabel()
    {
        var g = GraphImport.Parse("graph LR\n A --> B");

        Assert.Single(g.Edges);
        Assert.Equal("edge", g.Edges.Single().Label);
    }

    #endregion

    #region Output

    [Fact]
    public void ToRenderJson_ProducesVerticesAndEdgesThatConvert()
    {
        var g = GraphImport.Parse("digraph { a [type=person] ; a -> b [label=knows] }");

        var json = JsonSerializer.Deserialize<JsonElement>(GraphImport.ToRenderJson(g));
        var table = GraphDataConverter.ToTable(json);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Single(table.Edges);
    }

    [Fact]
    public void ToGremlin_EmitsAddVThenAddE()
    {
        var g = GraphImport.Parse("digraph { alice [type=person, label=\"Alice\"] ; alice -> bob [label=knows] }");

        var gremlin = GraphImport.ToQueries(g);

        Assert.Contains("g.addV('person').property(T.id, 'alice')", gremlin);
        Assert.Contains(".property('name', 'Alice')", gremlin);
        Assert.Contains("g.V('alice').addE('knows').to(__.V('bob'))", gremlin);

        //Vertices must come before edges so the edge endpoints resolve.
        Assert.True(gremlin.IndexOf("addV", System.StringComparison.Ordinal) < gremlin.IndexOf("addE", System.StringComparison.Ordinal));
    }

    [Fact]
    public void GremlinFromJson_BuildsAddVAndAddEFromPastedGraphSON()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("""
        [
          { "id": "a", "label": "person", "properties": { "name": "Alice" } },
          { "id": "b", "label": "person" },
          { "id": "e1", "label": "knows", "outV": "a", "inV": "b" }
        ]
        """);

        var gremlin = GraphImport.QueriesFromJson(data);

        Assert.Contains("g.addV('person').property(T.id, 'a')", gremlin);
        Assert.Contains(".property('name', 'Alice')", gremlin);
        Assert.Contains("g.V('a').addE('knows').to(__.V('b'))", gremlin);
    }

    [Fact]
    public void GremlinFromJson_HandlesQueryResultTraversalTriples()
    {
        //A real query result (e.g. g.V().outE().inV()) returns v/e/o triples with GraphSON-typed ids and
        //[{value}] property shapes; the result-import must turn those into addV/addE just like pasted JSON.
        var data = JsonSerializer.Deserialize<JsonElement>("""
        [
          {
            "v": { "id": { "@type": "g:Int64", "@value": 1 }, "label": "person", "properties": { "name": [{"value":"Alice"}] } },
            "e": { "id": { "@type": "g:Int64", "@value": 10 }, "label": "knows", "outV": { "@type": "g:Int64", "@value": 1 }, "inV": { "@type": "g:Int64", "@value": 2 } },
            "o": { "id": { "@type": "g:Int64", "@value": 2 }, "label": "person", "properties": { "name": [{"value":"Bob"}] } }
          }
        ]
        """);

        var gremlin = GraphImport.QueriesFromJson(data);

        Assert.Contains("g.addV('person').property(T.id, 1)", gremlin);
        Assert.Contains(".property('name', 'Alice')", gremlin);
        Assert.Contains(".property('name', 'Bob')", gremlin);
        Assert.Contains("g.V(1).addE('knows').to(__.V(2))", gremlin);
    }

    #endregion
}
