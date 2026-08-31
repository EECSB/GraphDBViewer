using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GraphExpansionTests
{
    [Fact]
    public void Neighbors_CapsToTheLimit()
    {
        Assert.Equal("g.V('a').bothE().limit(25).union(__.identity(), __.otherV())", GremlinQueryBuilder.Neighbors("a", 25));
    }

    [Fact]
    public void Neighbors_ZeroOrLess_IsUncapped()
    {
        Assert.Equal("g.V('a').union(__.bothE(), __.bothE().otherV())", GremlinQueryBuilder.Neighbors("a", 0));
    }

    [Fact]
    public void MergeGraphResults_DeduplicatesByIdAndCombines()
    {
        var existing = JsonSerializer.Deserialize<JsonElement>("""
        [ { "id": "1", "label": "person" }, { "id": "2", "label": "person" } ]
        """);

        var incoming = JsonSerializer.Deserialize<JsonElement>("""
        [
          { "id": "2", "label": "person" },
          { "id": "3", "label": "person" },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2" }
        ]
        """);

        var merged = GraphDataConverter.MergeGraphResults(existing, incoming);
        var table = GraphDataConverter.ToTable(merged);

        //Vertex "2" appears in both inputs but should be merged once.
        Assert.Equal(3, table.Nodes.Count);
        Assert.Single(table.Edges);
    }

    [Fact]
    public void MergeGraphResults_ALaterAnswerReplacesRatherThanBeingWeighed()
    {
        //A live update is not another view of the same moment: a property the peer removed has to go, even
        //though that makes the newer version the barer one.
        var drawn = JsonSerializer.Deserialize<JsonElement>("""
        [ { "id": "alice", "label": "Person", "properties": { "name": "Alice", "age": "30" } } ]
        """);

        var pushed = JsonSerializer.Deserialize<JsonElement>("""
        [ { "id": "alice", "label": "Person", "properties": { "name": "Alice" } } ]
        """);

        var node = Assert.Single(GraphDataConverter.ToTable(GraphDataConverter.MergeGraphResults(drawn, pushed, true)).Nodes);

        Assert.False(node.Properties.ContainsKey("age"));
    }

    [Fact]
    public void WithoutEdgesFrom_ClearsANodesEdgesSoAReplacementDoesNotLinger()
    {
        //GUN keeps a node's links as keys, and a key holds one link — so re-pointing "knows" replaces the
        //edge rather than adding one. The pushed edges merge straight back in; only what really went stays gone.
        var drawn = JsonSerializer.Deserialize<JsonElement>("""
        [
          { "id": "alice", "label": "Person" },
          { "id": "bob", "label": "Person" },
          { "id": "e1", "label": "knows", "outV": "alice", "inV": "bob" },
          { "id": "e2", "label": "worksAt", "outV": "bob", "inV": "acme" }
        ]
        """);

        var table = GraphDataConverter.ToTable(GraphDataConverter.WithoutEdgesFrom(drawn, new HashSet<string> { "alice" }));

        //Alice's edge went; an edge that merely points at her would not have.
        Assert.Single(table.Edges);
        Assert.Equal("worksAt", table.Edges[0].Label);
        Assert.Equal(2, table.Nodes.Count);
    }

    [Fact]
    public void MergeGraphResults_TheFullerVersionOfANodeWins()
    {
        //An expand answers with the clicked node as a bare edge endpoint on some engines and as the whole
        //node on others, so neither side can simply be preferred. GUN is the second case: it stands in an
        //empty node for a link target it never walked, and the walk that finally reads it must win.
        var placeholder = JsonSerializer.Deserialize<JsonElement>("""
        [ { "id": "bob", "label": "node", "properties": {} } ]
        """);

        var walked = JsonSerializer.Deserialize<JsonElement>("""
        [ { "id": "bob", "label": "Person", "properties": { "name": "Bob", "age": "41" } } ]
        """);

        var node = Assert.Single(GraphDataConverter.ToTable(GraphDataConverter.MergeGraphResults(placeholder, walked)).Nodes);

        Assert.Equal("Person", node.Label);
        Assert.Equal("Bob", node.Properties["name"]);

        //And the other way round: what is already loaded is not thrown away by a barer answer.
        var kept = Assert.Single(GraphDataConverter.ToTable(GraphDataConverter.MergeGraphResults(walked, placeholder)).Nodes);

        Assert.Equal("Person", kept.Label);
    }

    [Fact]
    public void ToForceGraphJson_EdgeListedBeforeVertices_UsesRealVertexLabel()
    {
        //Mirrors a neighbor-expansion result (edges first, then vertices). The named
        //vertices must win over the id-only placeholders the edge would otherwise create.
        var json = JsonSerializer.Deserialize<JsonElement>("""
        [
          { "id": "e1", "label": "composes", "outV": "288", "inV": "306" },
          { "id": "288", "label": "part", "properties": { "name": "Table Top" } },
          { "id": "306", "label": "part", "properties": { "name": "Leg" } }
        ]
        """);

        var fg = JsonSerializer.Deserialize<JsonElement>(GraphDataConverter.ToForceGraphJson(json));

        string label288 = null;
        foreach (var n in fg.GetProperty("nodes").EnumerateArray())
        {
            if (n.GetProperty("id").GetString() == "288")
                label288 = n.GetProperty("label").GetString();
        }

        Assert.Equal("Table Top", label288);
    }

    [Fact]
    public void BuildSchemaGraphJson_BuildsLabelNodesAndRelationshipEdges()
    {
        var vLabels = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "person", {"@type":"g:Int64","@value":3}, "product", {"@type":"g:Int64","@value":2} ] } ]
        """).RootElement;

        var eLabels = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "buys", {"@type":"g:Int64","@value":4} ] } ]
        """).RootElement;

        var vKeys = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "person", {"@type":"g:List","@value":["name","age"]}, "product", {"@type":"g:List","@value":["name"]} ] } ]
        """).RootElement;

        var triples = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "out","person","edge","buys","in","product" ] } ]
        """).RootElement;

        var json = SchemaBuilder.BuildSchemaGraphJson(vLabels, eLabels, vKeys, triples);
        var table = GraphDataConverter.ToTable(JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(2, table.Nodes.Count);
        Assert.Single(table.Edges);

        var person = table.Nodes.Single(n => n.Id == "person");
        Assert.Equal("3", person.Properties["count"]);
        Assert.Equal("name, age", person.Properties["keys"]);

        var edge = table.Edges.Single();
        Assert.Equal("buys", edge.Label);
        Assert.Equal("person", edge.Source);
        Assert.Equal("product", edge.Target);
    }

    [Fact]
    public void ExtractVocabulary_ReturnsSortedLabelsAndUnionedKeys()
    {
        var vLabels = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "product", {"@type":"g:Int64","@value":2}, "person", {"@type":"g:Int64","@value":3} ] } ]
        """).RootElement;

        var eLabels = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "knows", {"@type":"g:Int64","@value":1}, "buys", {"@type":"g:Int64","@value":4} ] } ]
        """).RootElement;

        var vKeys = JsonDocument.Parse("""
        [ { "@type":"g:Map", "@value":[ "person", {"@type":"g:List","@value":["name","age"]}, "product", {"@type":"g:List","@value":["name","price"]} ] } ]
        """).RootElement;

        var vocab = SchemaBuilder.ExtractVocabulary(vLabels, eLabels, vKeys);

        Assert.Equal(new[] { "person", "product" }, vocab.VertexLabels);
        Assert.Equal(new[] { "buys", "knows" }, vocab.EdgeLabels);
        Assert.Equal(new[] { "age", "name", "price" }, vocab.PropertyKeys);
    }
}
