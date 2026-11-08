using System.Linq;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GraphDataConverterTests
{
    #region Cytoscape (2D)

    [Fact]
    public void ToCytoscapeJson_TraversalTriples_ExtractsNodesAndEdges()
    {
        var json = """
        [
          {
            "v": { "id": "1", "label": "person", "properties": { "Name": [{ "value": "Alice" }] } },
            "e": { "id": "e1", "label": "knows" },
            "o": { "id": "2", "label": "person", "properties": { "Name": [{ "value": "Bob" }] } }
          },
          {
            "v": { "id": "2", "label": "person", "properties": { "Name": [{ "value": "Bob" }] } },
            "e": { "id": "e2", "label": "likes" },
            "o": { "id": "3", "label": "software", "properties": { "Name": [{ "value": "Gremlin" }] } }
          }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        var nodes = new List<JsonElement>();
        var edges = new List<JsonElement>();
        foreach (var el in elements.EnumerateArray())
        {
            var data = el.GetProperty("data");
            if (data.TryGetProperty("source", out _))
                edges.Add(data);
            else
                nodes.Add(data);
        }

        Assert.Equal(3, nodes.Count);
        Assert.Equal(2, edges.Count);

        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "1" && n.GetProperty("label").GetString() == "Alice");
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "2" && n.GetProperty("label").GetString() == "Bob");
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "3" && n.GetProperty("label").GetString() == "Gremlin");

        Assert.Contains(edges, e => e.GetProperty("source").GetString() == "1" && e.GetProperty("target").GetString() == "2" && e.GetProperty("label").GetString() == "knows");
        Assert.Contains(edges, e => e.GetProperty("source").GetString() == "2" && e.GetProperty("target").GetString() == "3" && e.GetProperty("label").GetString() == "likes");
    }

    [Fact]
    public void ToCytoscapeJson_EdgesCarryPaletteColorForTheirLabel()
    {
        var json = """
        [
          { "v": { "id": "1", "label": "person" }, "e": { "id": "e1", "label": "knows" }, "o": { "id": "2", "label": "person" } },
          { "v": { "id": "2", "label": "person" }, "e": { "id": "e2", "label": "knows" }, "o": { "id": "3", "label": "person" } },
          { "v": { "id": "1", "label": "person" }, "e": { "id": "e3", "label": "likes" }, "o": { "id": "3", "label": "person" } }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement;

        var edges = elements.EnumerateArray().Select(e => e.GetProperty("data")).Where(d => d.TryGetProperty("source", out _)).ToList();

        var knows = edges.Where(e => e.GetProperty("label").GetString() == "knows").ToList();
        var likes = edges.Single(e => e.GetProperty("label").GetString() == "likes");

        //Every edge carries its label's palette color; same label → same color, matching GraphPalette.
        Assert.All(knows, e => Assert.Equal(GraphPalette.ColorForLabel("knows"), e.GetProperty("edgeColor").GetString()));
        Assert.Equal(GraphPalette.ColorForLabel("likes"), likes.GetProperty("edgeColor").GetString());
    }

    [Fact]
    public void ToForceGraphJson_LinksCarryPaletteColorForTheirLabel()
    {
        var json = """
        [ { "v": { "id": "1", "label": "person" }, "e": { "id": "e1", "label": "knows" }, "o": { "id": "2", "label": "person" } } ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var root = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results)).RootElement;

        var link = root.GetProperty("links").EnumerateArray().Single();
        Assert.Equal(GraphPalette.ColorForLabel("knows"), link.GetProperty("color").GetString());
    }

    //Two edge labels, used to exercise the edge-color modes below.
    private const string TwoEdgeLabelsJson = """
    [
      { "v": { "id": "1", "label": "person" }, "e": { "id": "e1", "label": "knows" }, "o": { "id": "2", "label": "person" } },
      { "v": { "id": "1", "label": "person" }, "e": { "id": "e2", "label": "likes" }, "o": { "id": "3", "label": "person" } }
    ]
    """;

    private static List<JsonElement> CytoscapeEdges(string output)
    {
        return JsonDocument.Parse(output).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("data"))
            .Where(d => d.TryGetProperty("source", out _))
            .ToList();
    }

    [Fact]
    public void ToCytoscapeJson_EdgeColorModeOff_UsesDefaultGrayForEveryEdge()
    {
        var results = JsonDocument.Parse(TwoEdgeLabelsJson).RootElement;
        var edges = CytoscapeEdges(GraphDataConverter.ToCytoscapeJson(results, null, edgeColorMode: 1));

        //Off mode: every edge draws in the shared default gray, regardless of label.
        Assert.All(edges, e => Assert.Equal("#6c757d", e.GetProperty("edgeColor").GetString()));
    }

    [Fact]
    public void ToCytoscapeJson_EdgeColorModeOff_ColorsOnlyThePickedLabel()
    {
        var results = JsonDocument.Parse(TwoEdgeLabelsJson).RootElement;
        var custom = new Dictionary<string, string> { ["knows"] = "#00ff00" };
        var edges = CytoscapeEdges(GraphDataConverter.ToCytoscapeJson(results, null, edgeColorMode: 1, edgeColors: custom));

        var knows = edges.Single(e => e.GetProperty("label").GetString() == "knows");
        var likes = edges.Single(e => e.GetProperty("label").GetString() == "likes");

        //Off mode with a picked color: only that label is colored — an untouched label stays gray instead
        //of picking up an auto color.
        Assert.Equal("#00ff00", knows.GetProperty("edgeColor").GetString());
        Assert.Equal("#6c757d", likes.GetProperty("edgeColor").GetString());
    }

    [Fact]
    public void ToCytoscapeJson_EdgeColorModeCustom_UsesPerLabelColor_AndFallsBackToAuto()
    {
        var results = JsonDocument.Parse(TwoEdgeLabelsJson).RootElement;
        var custom = new Dictionary<string, string> { ["knows"] = "#00ff00" };
        var edges = CytoscapeEdges(GraphDataConverter.ToCytoscapeJson(results, null, edgeColorMode: 2, edgeColors: custom));

        var knows = edges.Single(e => e.GetProperty("label").GetString() == "knows");
        var likes = edges.Single(e => e.GetProperty("label").GetString() == "likes");

        //Custom mode: the configured label uses its color; an unset label falls back to its auto color.
        Assert.Equal("#00ff00", knows.GetProperty("edgeColor").GetString());
        Assert.Equal(GraphPalette.ColorForLabel("likes"), likes.GetProperty("edgeColor").GetString());
    }

    [Fact]
    public void ToForceGraphJson_EdgeColorModeCustom_UsesPerLabelColor_AndFallsBackToAuto()
    {
        var results = JsonDocument.Parse(TwoEdgeLabelsJson).RootElement;
        var custom = new Dictionary<string, string> { ["knows"] = "#00ff00" };
        var links = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results, null, edgeColorMode: 2, edgeColors: custom)).RootElement.GetProperty("links");

        var knows = links.EnumerateArray().Single(l => l.GetProperty("label").GetString() == "knows");
        var likes = links.EnumerateArray().Single(l => l.GetProperty("label").GetString() == "likes");

        Assert.Equal("#00ff00", knows.GetProperty("color").GetString());
        Assert.Equal(GraphPalette.ColorForLabel("likes"), likes.GetProperty("color").GetString());
    }

    [Fact]
    public void EdgeLabels_ReturnsDistinctEdgeLabels()
    {
        var results = JsonDocument.Parse(TwoEdgeLabelsJson).RootElement;

        var labels = GraphDataConverter.EdgeLabels(results);

        Assert.Equal(2, labels.Count);
        Assert.Contains("knows", labels);
        Assert.Contains("likes", labels);
    }

    [Fact]
    public void ToCytoscapeJson_StandaloneVertices_CreatesNodesOnly()
    {
        var json = """
        [
          { "id": "v1", "label": "city", "properties": { "name": [{ "value": "Seattle" }] } },
          { "id": "v2", "label": "city", "properties": { "name": [{ "value": "Portland" }] } }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        Assert.Equal(2, elements.GetArrayLength());
        foreach (var el in elements.EnumerateArray())
        {
            var data = el.GetProperty("data");
            Assert.False(data.TryGetProperty("source", out _));
        }

        Assert.Contains(
            elements.EnumerateArray().ToArray(),
            el => el.GetProperty("data").GetProperty("label").GetString() == "Seattle");
    }

    [Fact]
    public void ToCytoscapeJson_StandaloneEdges_CreatesImplicitNodesAndEdge()
    {
        var json = """
        [
          { "id": "e1", "label": "route", "outV": "v1", "inV": "v2" }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        int nodeCount = 0, edgeCount = 0;
        foreach (var el in elements.EnumerateArray())
            if (el.GetProperty("data").TryGetProperty("source", out _))
                edgeCount++;
            else
                nodeCount++;

        Assert.Equal(2, nodeCount);
        Assert.Equal(1, edgeCount);
    }

    [Fact]
    public void ToCytoscapeJson_VerticesThenEdges_IncludesIsolatedVertex()
    {
        //Mirrors the FullGraph union query output: all vertices first (one with no edges),
        //then all edges. The isolated vertex (id 9) must still appear as a node.
        var json = """
        [
          { "id": "1", "label": "Component", "properties": { "name": [{ "value": "Leg" }] } },
          { "id": "2", "label": "Assembly", "properties": { "name": [{ "value": "Table" }] } },
          { "id": "9", "label": "asdasd", "properties": {} },
          { "id": "e1", "label": "composes", "outV": "1", "inV": "2" }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        var nodes = elements.EnumerateArray().Select(e => e.GetProperty("data"))
            .Where(d => !d.TryGetProperty("source", out _)).ToList();
        var edges = elements.EnumerateArray().Select(e => e.GetProperty("data"))
            .Where(d => d.TryGetProperty("source", out _)).ToList();

        Assert.Equal(3, nodes.Count);
        Assert.Single(edges);
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "9");//isolated vertex kept
    }

    [Fact]
    public void ToCytoscapeJson_EmptyArray_ReturnsEmptyArray()
    {
        var results = JsonDocument.Parse("[]").RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        Assert.Equal(0, elements.GetArrayLength());
    }

    [Fact]
    public void ToCytoscapeJson_GraphSONWrapped_UnwrapsCorrectly()
    {
        var json = """
        [
          {
            "@type": "g:Map",
            "@value": {
              "v": { "@type": "g:Vertex", "@value": { "id": "1", "label": "person" } },
              "e": { "@type": "g:Edge", "@value": { "id": "e1", "label": "created" } },
              "o": { "@type": "g:Vertex", "@value": { "id": "2", "label": "software" } }
            }
          }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        int nodeCount = 0, edgeCount = 0;
        foreach (var el in elements.EnumerateArray())
            if (el.GetProperty("data").TryGetProperty("source", out _))
                edgeCount++;
            else
                nodeCount++;

        Assert.Equal(2, nodeCount);
        Assert.Equal(1, edgeCount);
    }

    //A path() result is a g:Path holding what the traversal went through, vertices and edges among them, and it drew
    //nothing: unwrapped to its labels and its objects, it read as neither a vertex nor an edge (2026-11-07, JanusGraph's
    //paths at the showcase's refresh). The path's objects are the result's elements, on both canvases and in the table.
    [Fact]
    public void APathResult_DrawsTheVerticesAndEdgesItWentThrough()
    {
        var json = """
        [
          {
            "@type": "g:Path",
            "@value": {
              "labels": { "@type": "g:List", "@value": [ { "@type": "g:Set", "@value": [] }, { "@type": "g:Set", "@value": [] }, { "@type": "g:Set", "@value": [] } ] },
              "objects": { "@type": "g:List", "@value": [
                { "@type": "g:Vertex", "@value": { "id": "1", "label": "Material" } },
                { "@type": "g:Edge", "@value": { "id": "e1", "label": "cutInto", "inVLabel": "Component", "outVLabel": "Material", "inV": "2", "outV": "1" } },
                { "@type": "g:Vertex", "@value": { "id": "2", "label": "Component" } }
              ] }
            }
          }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var table = GraphDataConverter.ToTable(results);

        Assert.Equal(["1", "2"], table.Nodes.Select(n => n.Id));
        Assert.Equal("e1", Assert.Single(table.Edges).Id);

        var cytoscape = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement;
        var force = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results)).RootElement;

        Assert.Equal(3, cytoscape.GetArrayLength());
        Assert.Equal(2, force.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, force.GetProperty("links").GetArrayLength());
    }

    #endregion

    #region Force Graph (3D)

    [Fact]
    public void ToForceGraphJson_TraversalTriples_ProducesNodesAndLinks()
    {
        var json = """
        [
          {
            "v": { "id": "1", "label": "person", "properties": { "Name": [{ "value": "Alice" }] } },
            "e": { "id": "e1", "label": "knows" },
            "o": { "id": "2", "label": "person", "properties": { "Name": [{ "value": "Bob" }] } }
          }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToForceGraphJson(results);
        var graph = JsonDocument.Parse(output).RootElement;

        var nodes = graph.GetProperty("nodes");
        var links = graph.GetProperty("links");

        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal(1, links.GetArrayLength());

        var alice = nodes.EnumerateArray().First(n => n.GetProperty("id").GetString() == "1");
        Assert.Equal("Alice", alice.GetProperty("label").GetString());
        Assert.True(alice.TryGetProperty("group", out _));

        var link = links[0];
        Assert.Equal("1", link.GetProperty("source").GetString());
        Assert.Equal("2", link.GetProperty("target").GetString());
        Assert.Equal("knows", link.GetProperty("label").GetString());
    }

    [Fact]
    public void ToForceGraphJson_GroupsByVertexLabel()
    {
        var json = """
        [
          {
            "v": { "id": "1", "label": "person" },
            "e": { "id": "e1", "label": "created" },
            "o": { "id": "2", "label": "software" }
          },
          {
            "v": { "id": "3", "label": "person" },
            "e": { "id": "e2", "label": "created" },
            "o": { "id": "4", "label": "software" }
          }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToForceGraphJson(results);
        var nodes = JsonDocument.Parse(output).RootElement.GetProperty("nodes");

        var groups = nodes.EnumerateArray()
            .Select(n => new { id = n.GetProperty("id").GetString(), group = n.GetProperty("group").GetInt32() })
            .ToList();

        var personGroup = groups.First(g => g.id == "1").group;
        var softwareGroup = groups.First(g => g.id == "2").group;

        Assert.NotEqual(personGroup, softwareGroup);
        Assert.Equal(personGroup, groups.First(g => g.id == "3").group);
        Assert.Equal(softwareGroup, groups.First(g => g.id == "4").group);
    }

    [Fact]
    public void ToForceGraphJson_EmptyArray_ReturnsEmptyGraph()
    {
        var results = JsonDocument.Parse("[]").RootElement;
        var output = GraphDataConverter.ToForceGraphJson(results);
        var graph = JsonDocument.Parse(output).RootElement;

        Assert.Equal(0, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, graph.GetProperty("links").GetArrayLength());
    }

    [Fact]
    public void ToForceGraphJson_StandaloneVertices_CreatesNodesNoLinks()
    {
        var json = """
        [
          { "id": "v1", "label": "city" },
          { "id": "v2", "label": "city" }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToForceGraphJson(results);
        var graph = JsonDocument.Parse(output).RootElement;

        Assert.Equal(2, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, graph.GetProperty("links").GetArrayLength());
    }

    #endregion

    #region Table

    [Fact]
    public void ToTable_TraversalTriples_BuildsNodeAndEdgeRows()
    {
        var json = """
        [
          {
            "v": { "id": "1", "label": "Component", "properties": { "name": [{ "value": "Leg" }] } },
            "e": { "id": "e1", "label": "composes", "outV": "1", "inV": "2" },
            "o": { "id": "2", "label": "Assembly", "properties": { "name": [{ "value": "Table" }] } }
          }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Single(table.Edges);
        Assert.Contains("name", table.NodePropertyColumns);

        var leg = table.Nodes.First(n => n.Id == "1");
        Assert.Equal("Component", leg.Label);
        Assert.Equal("Leg", leg.Properties["name"]);

        var edge = table.Edges[0];
        Assert.Equal("composes", edge.Label);
        Assert.Equal("1", edge.Source);
        Assert.Equal("2", edge.Target);
    }

    [Fact]
    public void ToTable_IncludesIsolatedVertexWithNoEdges()
    {
        var json = """
        [
          { "id": "1", "label": "Component", "properties": { "name": [{ "value": "Leg" }] } },
          { "id": "9", "label": "asdasd", "properties": {} },
          { "id": "e1", "label": "composes", "outV": "1", "inV": "9" }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Contains(table.Nodes, n => n.Id == "9" && n.Label == "asdasd");
        Assert.Single(table.Edges);
    }

    [Fact]
    public void ToTable_CapturesGraphSonIdType()
    {
        var json = """
        [
          { "id": { "@type": "g:Int64", "@value": 1 }, "label": "person" },
          { "id": { "@type": "g:Int64", "@value": 2 }, "label": "person" },
          { "id": { "@type": "g:Int64", "@value": 727 }, "label": "knows", "outV": { "@type": "g:Int64", "@value": 1 }, "inV": { "@type": "g:Int64", "@value": 2 } }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        var edge = table.Edges.Single();
        Assert.Equal("727", edge.Id);
        Assert.Equal("g:Int64", edge.IdType);
        Assert.All(table.Nodes, n => Assert.Equal("g:Int64", n.IdType));
    }

    [Fact]
    public void FindIdType_ReturnsEdgeAndVertexIdTypes()
    {
        var json = """
        [
          { "id": { "@type": "g:Int64", "@value": 1 }, "label": "person" },
          { "id": { "@type": "g:Int64", "@value": 727 }, "label": "knows", "outV": { "@type": "g:Int64", "@value": 1 }, "inV": { "@type": "g:Int64", "@value": 1 } }
        ]
        """;

        var data = JsonDocument.Parse(json).RootElement;

        Assert.Equal("g:Int64", GraphDataConverter.FindIdType(data, "727", isEdge: true));
        Assert.Equal("g:Int64", GraphDataConverter.FindIdType(data, "1", isEdge: false));
    }

    [Fact]
    public void FindIdType_UnknownId_ReturnsNull()
    {
        var json = """
        [
          { "id": { "@type": "g:Int64", "@value": 1 }, "label": "person" }
        ]
        """;

        var data = JsonDocument.Parse(json).RootElement;

        Assert.Null(GraphDataConverter.FindIdType(data, "999", isEdge: false));
    }

    [Fact]
    public void FindIdType_UntypedId_ReturnsNull()
    {
        //A bare id with no GraphSON @type yields null, so FormatId keeps its type-less heuristic.
        var json = """
        [
          { "id": "1", "label": "person" }
        ]
        """;

        var data = JsonDocument.Parse(json).RootElement;

        Assert.Null(GraphDataConverter.FindIdType(data, "1", isEdge: false));
    }

    #endregion

    #region Label Extraction

    [Fact]
    public void ToCytoscapeJson_FallsBackToLabelAndId_WhenNoNameProperty()
    {
        var json = """
        [
          { "id": "42", "label": "device", "properties": {} }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var output = GraphDataConverter.ToCytoscapeJson(results);
        var elements = JsonDocument.Parse(output).RootElement;

        var label = elements[0].GetProperty("data").GetProperty("label").GetString();
        Assert.Equal("device (42)", label);
    }

    [Fact]
    public void VertexLabels_ReturnsDistinctTypeLabels()
    {
        var json = """
        [
          { "id": "1", "label": "person" },
          { "id": "2", "label": "person" },
          { "id": "3", "label": "product" }
        ]
        """;

        var labels = GraphDataConverter.VertexLabels(JsonDocument.Parse(json).RootElement);

        Assert.Equal(2, labels.Count);
        Assert.Contains("person", labels);
        Assert.Contains("product", labels);
    }

    [Fact]
    public void ToCytoscapeJson_DisplayPropertyOverride_UsesConfiguredProperty()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": { "name": "Alice", "email": "a@x.com" } } ]
        """;
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { DisplayProperty = "email" } };

        var withStyle = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles);
        var withoutStyle = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement);

        Assert.Contains("\"label\":\"a@x.com\"", withStyle);
        Assert.Contains("\"label\":\"Alice\"", withoutStyle);
    }

    [Fact]
    public void ToCytoscapeJson_LabelStyle_EmitsColorAndSize()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {} } ]
        """;
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Color = "#ff0000", Size = 60 } };

        var result = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles);

        Assert.Contains("\"bgColor\":\"#ff0000\"", result);
        Assert.Contains("\"nodeSize\":60", result);
    }

    [Fact]
    public void ToForceGraphJson_LabelIcon_FillsImageWhenNoPerNodeImage()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {} } ]
        """;
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Icon = "http://x/i.png" } };

        var result = GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement, styles);

        Assert.Contains("\"image\":\"http://x/i.png\"", result);
    }

    [Fact]
    public void ToCytoscapeJson_ImageShown_SetsImageData()
    {
        //gdbvImage renders only when the node is set to show it (its gdbvShow list names gdbvImage).
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "gdbvImage": "https://x/i.png", "gdbvShow": "gdbvImage" } }
        ]
        """;

        var results = JsonDocument.Parse(json).RootElement;
        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement;

        var data = elements[0].GetProperty("data");
        Assert.Equal("https://x/i.png", data.GetProperty("image").GetString());
    }

    [Fact]
    public void ToCytoscapeJson_ImageNotShown_OmitsImageData()
    {
        //gdbvImage present but not in gdbvShow — the "show" toggle is off, so no image is drawn.
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "gdbvImage": "https://x/i.png" } }
        ]
        """;

        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement;

        Assert.False(elements[0].GetProperty("data").TryGetProperty("image", out _));
    }

    [Fact]
    public void ToForceGraphJson_ModelShown_SetsModelData()
    {
        //A per-node gdbvModel maps to data["model"] (which the 3D view renders) only when shown.
        var json = """
        [ { "id": "1", "label": "part", "properties": { "gdbvModel": "https://x/m.obj", "gdbvShow": "gdbvModel" } } ]
        """;

        var node = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement)).RootElement.GetProperty("nodes")[0];

        Assert.Equal("https://x/m.obj", node.GetProperty("model").GetString());
    }

    [Fact]
    public void ToForceGraphJson_ModelNotShown_OmitsModelData()
    {
        //gdbvModel present but not in gdbvShow — the model is hidden, so the 3D node stays a sphere.
        var json = """
        [ { "id": "1", "label": "part", "properties": { "gdbvModel": "https://x/m.obj" } } ]
        """;

        var node = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement)).RootElement.GetProperty("nodes")[0];

        Assert.False(node.TryGetProperty("model", out _));
    }

    #endregion

    #region GraphSON unwrapping & property extraction

    [Fact]
    public void ToForceGraphJson_StandaloneEdge_CreatesNodesAndLink()
    {
        var json = """
        [
          { "id": "e1", "label": "route", "outV": "a", "inV": "b" }
        ]
        """;

        var graph = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement)).RootElement;

        Assert.Equal(2, graph.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, graph.GetProperty("links").GetArrayLength());

        var link = graph.GetProperty("links")[0];
        Assert.Equal("a", link.GetProperty("source").GetString());
        Assert.Equal("b", link.GetProperty("target").GetString());
    }

    [Fact]
    public void ToCytoscapeJson_GraphSONInt64Ids_AreUnwrapped()
    {
        //Mirrors real TinkerPop output: ids are { "@type": "g:Int64", "@value": 2 }.
        var json = """
        [
          {
            "@type": "g:Vertex",
            "@value": {
              "id": { "@type": "g:Int64", "@value": 2 },
              "label": "Component",
              "properties": {
                "name": [ { "@type": "g:VertexProperty", "@value": { "value": "M3 Screw", "label": "name" } } ]
              }
            }
          }
        ]
        """;

        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement;
        var data = elements[0].GetProperty("data");

        Assert.Equal("2", data.GetProperty("id").GetString());
        Assert.Equal("M3 Screw", data.GetProperty("label").GetString());
    }

    [Fact]
    public void ToTable_ExtractsEdgeProperties()
    {
        var json = """
        [
          { "id": "1", "label": "person" },
          { "id": "2", "label": "person" },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2", "properties": { "weight": { "value": "0.5" } } }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Single(table.Edges);
        Assert.Contains("weight", table.EdgePropertyColumns);
        Assert.Equal("0.5", table.Edges[0].Properties["weight"]);
    }

    [Fact]
    public void UnwrapElement_UnwrapsGraphSONVertex()
    {
        var wrapped = JsonDocument.Parse(
            """{ "@type": "g:Vertex", "@value": { "id": "1", "label": "person" } }""").RootElement;

        var unwrapped = GraphDataConverter.UnwrapElement(wrapped);

        Assert.Equal("person", unwrapped.GetProperty("label").GetString());
    }

    [Fact]
    public void UnwrapElement_GMap_BecomesObject()
    {
        var wrapped = JsonDocument.Parse(
            """{ "@type": "g:Map", "@value": [ "k", "v" ] }""").RootElement;

        var unwrapped = GraphDataConverter.UnwrapElement(wrapped);

        Assert.Equal("v", unwrapped.GetProperty("k").GetString());
    }

    [Fact]
    public void ExtractProperties_UnwrapsArrayValues_AndSkipsMetadata()
    {
        var el = JsonDocument.Parse(
            """{ "id": "1", "label": "person", "properties": { "name": [ { "value": "Bob" } ], "age": [ { "value": "30" } ] } }""").RootElement;

        var props = GraphDataConverter.ExtractProperties(el);

        Assert.Equal("Bob", props["name"]);
        Assert.Equal("30", props["age"]);
        Assert.False(props.ContainsKey("id"));
        Assert.False(props.ContainsKey("label"));
    }

    //A stored property named label, type or id is data, not the element's metadata — that sits beside the
    //properties object, as the test above has it. rdfs:label is the one that made this visible: it captions
    //the node on the canvas, and until 2026-09-16 it was in no column, no properties panel and no export.
    [Fact]
    public void ExtractProperties_KeepsAStoredPropertyNamedLikeMetadata()
    {
        var el = JsonDocument.Parse(
            """{ "id": "1", "label": "Person", "properties": { "label": [ { "value": "Ada Lovelace" } ], "type": "Countess", "id": "ABC-1", "_note": "kept" } }""").RootElement;

        var props = GraphDataConverter.ExtractProperties(el);

        Assert.Equal("Ada Lovelace", props["label"]);
        Assert.Equal("Countess", props["type"]);
        Assert.Equal("ABC-1", props["id"]);
        Assert.Equal("kept", props["_note"]);
    }

    //Where the element is the bag itself — a projected map, an edge stated inline, a document — the wire's
    //names sit among the stored ones and nothing else tells them apart, so there they are still skipped.
    [Fact]
    public void ExtractProperties_WithNoPropertiesObject_SkipsTheWiresOwnNames()
    {
        var el = JsonDocument.Parse(
            """{ "id": "e1", "label": "knows", "outV": "1", "inV": "2", "outVLabel": "Person", "inVLabel": "Person", "_rev": "9", "since": "2019" }""").RootElement;

        var props = GraphDataConverter.ExtractProperties(el);

        Assert.Equal("2019", Assert.Single(props).Value);
    }

    //A plain JSON boolean reads as the word Gremlin says in the flat shapes, as it has in the typed one since
    //ADR 0014: a properties object whose values are the values themselves, which is how untyped GraphSON gives an
    //edge's, GraphSON 1's and Cosmos DB's, and an element that is its own bag, a projected map. A JsonElement's
    //ToString is C#'s "True", which a reader comparing against "true" never matches.
    [Fact]
    public void ExtractProperties_APlainBoolean_ReadsAsTrueOrFalse_InTheFlatShapesToo()
    {
        var edge = JsonDocument.Parse(
            """{ "id": "e1", "label": "knows", "outV": "1", "inV": "2", "properties": { "current": true, "ended": false } }""").RootElement;

        var projected = JsonDocument.Parse(
            """{ "id": "1", "label": "Lot", "closed": true, "full": false }""").RootElement;

        var fromEdge = GraphDataConverter.ExtractProperties(edge);
        var fromProjection = GraphDataConverter.ExtractProperties(projected);

        Assert.Equal("true", fromEdge["current"]);
        Assert.Equal("false", fromEdge["ended"]);
        Assert.Equal("true", fromProjection["closed"]);
        Assert.Equal("false", fromProjection["full"]);
    }

    //One part as JanusGraph 1.1 answers g.V(4096) over GraphSON 3: every number typed, a date as epoch
    //milliseconds under g:Date, a UUID as a string under g:UUID, and each vertex property's id a RelationIdentifier.
    private const string TypedVertexWire = """
        [
          {"@type":"g:Vertex","@value":{
            "id":{"@type":"g:Int64","@value":4096},
            "label":"Part",
            "properties":{
              "name":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1kq-35s-sl"}},"value":"M3 Screw","label":"name"}}],
              "qty":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1l4-35s-2dh"}},"value":{"@type":"g:Int32","@value":250},"label":"qty"}}],
              "stock":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1li-35s-4qt"}},"value":{"@type":"g:Int64","@value":9007199254740993},"label":"stock"}}],
              "mass":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1lw-35s-5jp"}},"value":{"@type":"g:Double","@value":0.35},"label":"mass"}}],
              "pitch":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1ma-35s-6c5"}},"value":{"@type":"g:Float","@value":0.5},"label":"pitch"}}],
              "length":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1mo-35s-74l"}},"value":{"@type":"g:Double","@value":10.0},"label":"length"}}],
              "added":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1n2-35s-7x1"}},"value":{"@type":"g:Date","@value":1788566400000},"label":"added"}}],
              "lot":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1ng-35s-8ph"}},"value":{"@type":"g:UUID","@value":"41d2e28a-20a4-4ab0-b379-d810dede3786"},"label":"lot"}}],
              "rohs":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1nu-35s-9hx"}},"value":true,"label":"rohs"}}]
            }
          }}
        ]
        """;

    //The same part projected into a map shaped like it, which the table reads as a vertex:
    //g.V(4096).project('id','label','name','qty','stock','mass','pitch','length','added','lot','rohs').by(id).by(label)
    //.by('name').by('qty')…, as JanusGraph 1.1 answers it. The values are typed exactly as they are on the vertex.
    private const string TypedProjectionWire = """
        [
          {"@type":"g:Map","@value":[
            "id",{"@type":"g:Int64","@value":4096},
            "label","Part",
            "name","M3 Screw",
            "qty",{"@type":"g:Int32","@value":250},
            "stock",{"@type":"g:Int64","@value":9007199254740993},
            "mass",{"@type":"g:Double","@value":0.35},
            "pitch",{"@type":"g:Float","@value":0.5},
            "length",{"@type":"g:Double","@value":10.0},
            "added",{"@type":"g:Date","@value":1788566400000},
            "lot",{"@type":"g:UUID","@value":"41d2e28a-20a4-4ab0-b379-d810dede3786"},
            "rohs",true
          ]}
        ]
        """;

    //A typed scalar where the element is its own bag, a projected map, reads as its text: the text the same value
    //reads as in the vertex's own properties, through the same reading. Until 2026-09-27 the projected branch took
    //a bare scalar only, so every typed one, which on GraphSON 3 is every number, a date and a UUID, had no column
    //in the table and was missing from the properties panel and the CSV.
    [Fact]
    public void ExtractProperties_ATypedScalarInAProjectedMap_ReadsAsItsText_AsOnTheVertexItself()
    {
        var projected = GraphDataConverter.ToTable(JsonDocument.Parse(TypedProjectionWire).RootElement);
        var vertex = GraphDataConverter.ToTable(JsonDocument.Parse(TypedVertexWire).RootElement);

        var row = Assert.Single(projected.Nodes);
        var own = Assert.Single(vertex.Nodes);

        Assert.Equal("4096", row.Id);
        Assert.Equal("g:Int64", row.IdType);
        Assert.Equal("Part", row.Label);
        Assert.Equal("250", row.Properties.GetValueOrDefault("qty"));
        Assert.Equal("9007199254740993", row.Properties.GetValueOrDefault("stock"));
        Assert.Equal("0.35", row.Properties.GetValueOrDefault("mass"));
        Assert.Equal("0.5", row.Properties.GetValueOrDefault("pitch"));
        Assert.Equal("10", row.Properties.GetValueOrDefault("length"));
        Assert.Equal("2026-09-05T00:00:00Z", row.Properties.GetValueOrDefault("added"));
        Assert.Equal("41d2e28a-20a4-4ab0-b379-d810dede3786", row.Properties.GetValueOrDefault("lot"));

        //Every column the vertex's own row has, with the same text, and no other.
        Assert.Equal(own.Properties.OrderBy(p => p.Key), row.Properties.OrderBy(p => p.Key));
        Assert.Equal(vertex.NodePropertyColumns.Order(), projected.NodePropertyColumns.Order());

        //And the CSV, which is built on the table, has them too.
        var csv = GraphDataConverter.ToCsv(projected).Replace("\r\n", "\n").Trim().Split('\n');

        Assert.Equal("kind,id,label,source,target,name,qty,stock,mass,pitch,length,added,lot,rohs", csv[0]);
        Assert.Equal("vertex,4096,Part,,,M3 Screw,250,9007199254740993,0.35,0.5,10,2026-09-05T00:00:00Z,41d2e28a-20a4-4ab0-b379-d810dede3786,true", csv[1]);

        //So does the node on the canvas, whose properties are what the properties panel lists when it is clicked.
        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(TypedProjectionWire).RootElement)).RootElement[0];

        Assert.Equal("250", node.GetProperty("data").GetProperty("properties").GetProperty("qty").GetString());
    }

    //The same part as JanusGraph 1.1 answers g.V(4096).elementMap() over GraphSON 3. The id and the label are keyed by
    //g:T tokens rather than by strings, which is how the wire tells the element's own from what is stored on it.
    private const string ElementMapWire = """
        [
          {"@type":"g:Map","@value":[
            {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
            {"@type":"g:T","@value":"label"},"Part",
            "name","M3 Screw",
            "qty",{"@type":"g:Int32","@value":250},
            "stock",{"@type":"g:Int64","@value":9007199254740993},
            "mass",{"@type":"g:Double","@value":0.35},
            "pitch",{"@type":"g:Float","@value":0.5},
            "length",{"@type":"g:Double","@value":10.0},
            "added",{"@type":"g:Date","@value":1788566400000},
            "lot",{"@type":"g:UUID","@value":"41d2e28a-20a4-4ab0-b379-d810dede3786"},
            "rohs",true
          ]}
        ]
        """;

    //The same part as it answers g.V(4096).valueMap(true), the viewer's own "Vertices + props" example: the tokens
    //again, and each property's values in a list, since a vertex property can hold more than one.
    private const string ValueMapWithTokensWire = """
        [
          {"@type":"g:Map","@value":[
            {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
            {"@type":"g:T","@value":"label"},"Part",
            "name",{"@type":"g:List","@value":["M3 Screw"]},
            "qty",{"@type":"g:List","@value":[{"@type":"g:Int32","@value":250}]},
            "stock",{"@type":"g:List","@value":[{"@type":"g:Int64","@value":9007199254740993}]},
            "mass",{"@type":"g:List","@value":[{"@type":"g:Double","@value":0.35}]},
            "pitch",{"@type":"g:List","@value":[{"@type":"g:Float","@value":0.5}]},
            "length",{"@type":"g:List","@value":[{"@type":"g:Double","@value":10.0}]},
            "added",{"@type":"g:List","@value":[{"@type":"g:Date","@value":1788566400000}]},
            "lot",{"@type":"g:List","@value":[{"@type":"g:UUID","@value":"41d2e28a-20a4-4ab0-b379-d810dede3786"}]},
            "rohs",{"@type":"g:List","@value":[true]}
          ]}
        ]
        """;

    //An edge from the assembly 4096 to the part 8200, as JanusGraph 1.1 answers g.E(…) over GraphSON 3: its id a
    //RelationIdentifier, its ends' ids typed, each property a g:Property.
    private const string TypedEdgeWire = """
        [
          {"@type":"g:Edge","@value":{
            "id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"2ra-35s-9hx-6bs"}},
            "label":"uses",
            "inVLabel":"Part",
            "outVLabel":"Assembly",
            "inV":{"@type":"g:Int64","@value":8200},
            "outV":{"@type":"g:Int64","@value":4096},
            "properties":{
              "qty":{"@type":"g:Property","@value":{"key":"qty","value":{"@type":"g:Int32","@value":4}}},
              "position":{"@type":"g:Property","@value":{"key":"position","value":"R12"}}
            }
          }}
        ]
        """;

    //The same edge as it answers g.E(…).elementMap(): the tokens, and its two ends keyed by g:Direction, IN and OUT,
    //each a map of that vertex's id and label.
    private const string EdgeElementMapWire = """
        [
          {"@type":"g:Map","@value":[
            {"@type":"g:T","@value":"id"},{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"2ra-35s-9hx-6bs"}},
            {"@type":"g:T","@value":"label"},"uses",
            {"@type":"g:Direction","@value":"IN"},{"@type":"g:Map","@value":[{"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":8200},{"@type":"g:T","@value":"label"},"Part"]},
            {"@type":"g:Direction","@value":"OUT"},{"@type":"g:Map","@value":[{"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},{"@type":"g:T","@value":"label"},"Assembly"]},
            "qty",{"@type":"g:Int32","@value":4},
            "position","R12"
          ]}
        ]
        """;

    //A map keyed by tokens reads as the element it describes. Converting one threw an InvalidOperationException, since
    //a g:T key is an object and the conversion asked every key for its string, so ToTable of an elementMap() answer
    //threw, and with it every reader of the answer: the table, the canvas, the stats and the exports.
    [Fact]
    public void ToTable_AVertexsElementMap_ReadsAsTheSameRowTheVertexDoes()
    {
        var fromMap = GraphDataConverter.ToTable(JsonDocument.Parse(ElementMapWire).RootElement);
        var fromVertex = GraphDataConverter.ToTable(JsonDocument.Parse(TypedVertexWire).RootElement);

        var row = Assert.Single(fromMap.Nodes);
        var own = Assert.Single(fromVertex.Nodes);

        Assert.Empty(fromMap.Edges);
        Assert.Equal("4096", row.Id);
        Assert.Equal("g:Int64", row.IdType);
        Assert.Equal("Part", row.Label);
        Assert.Equal("M3 Screw", row.Properties.GetValueOrDefault("name"));
        Assert.Equal(own.Properties.OrderBy(p => p.Key), row.Properties.OrderBy(p => p.Key));
    }

    //An edge's elementMap names its ends by g:Direction. It reads as the edge it is, its source the OUT end and its
    //target the IN, and not as a vertex, which an id and a label with no outV would otherwise make it.
    [Fact]
    public void ToTable_AnEdgesElementMap_ReadsAsTheSameRowTheEdgeDoes()
    {
        var fromMap = GraphDataConverter.ToTable(JsonDocument.Parse(EdgeElementMapWire).RootElement);
        var fromEdge = GraphDataConverter.ToTable(JsonDocument.Parse(TypedEdgeWire).RootElement);

        var row = Assert.Single(fromMap.Edges);
        var own = Assert.Single(fromEdge.Edges);

        Assert.Empty(fromMap.Nodes);
        Assert.Equal(own.Id, row.Id);
        Assert.Equal("janusgraph:RelationIdentifier", row.IdType);
        Assert.Equal("uses", row.Label);
        Assert.Equal("4096", row.Source);
        Assert.Equal("8200", row.Target);
        Assert.Equal("4", row.Properties.GetValueOrDefault("qty"));
        Assert.Equal(own.Properties.OrderBy(p => p.Key), row.Properties.OrderBy(p => p.Key));

        //On the canvas it is an edge between its two ends, which stand in as nodes until the vertices are loaded.
        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(EdgeElementMapWire).RootElement)).RootElement;
        var data = elements.EnumerateArray().Select(e => e.GetProperty("data")).ToList();
        var edge = Assert.Single(data, d => d.TryGetProperty("source", out _));

        Assert.Equal("4096", edge.GetProperty("source").GetString());
        Assert.Equal("8200", edge.GetProperty("target").GetString());
        Assert.Equal(["4096", "8200"], data.Where(d => !d.TryGetProperty("source", out _)).Select(d => d.GetProperty("id").GetString()));
    }

    //valueMap(true) keys the id and the label by tokens too, and converts, and each property's list reads as its first
    //value, as the vertex's own properties do: the same row the vertex reads as. Until 2026-09-29 the lists were
    //dropped, and the row had the id and the label and nothing else.
    [Fact]
    public void ToTable_AValueMapWithTokens_ReadsAsTheSameRowTheVertexDoes()
    {
        var fromMap = GraphDataConverter.ToTable(JsonDocument.Parse(ValueMapWithTokensWire).RootElement);
        var fromVertex = GraphDataConverter.ToTable(JsonDocument.Parse(TypedVertexWire).RootElement);

        var row = Assert.Single(fromMap.Nodes);
        var own = Assert.Single(fromVertex.Nodes);

        Assert.Equal("4096", row.Id);
        Assert.Equal("g:Int64", row.IdType);
        Assert.Equal("Part", row.Label);
        Assert.Equal("M3 Screw", row.Properties.GetValueOrDefault("name"));
        Assert.Equal(own.Properties.OrderBy(p => p.Key), row.Properties.OrderBy(p => p.Key));
        Assert.Equal(fromVertex.NodePropertyColumns.Order(), fromMap.NodePropertyColumns.Order());
    }

    //A property that holds several values reads as its first, in a valueMap(true) answer as on the vertex itself: the
    //user's choice on 2026-09-29, over the values joined or the list's JSON, so that one vertex reads the same whichever
    //query brought it.
    [Fact]
    public void ToTable_APropertyWithSeveralValues_ReadsAsItsFirst_InAValueMapAsOnTheVertex()
    {
        const string vertex = """
            [
              {"@type":"g:Vertex","@value":{
                "id":{"@type":"g:Int64","@value":4096},
                "label":"Part",
                "properties":{
                  "tags":[
                    {"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1kq-35s-sl"}},"value":"motor","label":"tags"}},
                    {"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1l4-35s-sl"}},"value":"spare","label":"tags"}}
                  ]
                }
              }}
            ]
            """;

        const string valueMap = """
            [
              {"@type":"g:Map","@value":[
                {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
                {"@type":"g:T","@value":"label"},"Part",
                "tags",{"@type":"g:List","@value":["motor","spare"]}
              ]}
            ]
            """;

        var fromVertex = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(vertex).RootElement).Nodes);
        var fromMap = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(valueMap).RootElement).Nodes);

        Assert.Equal("motor", fromVertex.Properties["tags"]);
        Assert.Equal("motor", fromMap.Properties["tags"]);
    }

    //A server that types nothing, GraphSON 1 or plain JSON, answers valueMap(true) with the id and the label as strings
    //and each property's values in a plain array; a typed one's propertyMap() answers vertex properties in its lists.
    //Both read as the first value. An empty list is no value, as a property the vertex does not have is none.
    [Fact]
    public void ToTable_AnUntypedValueMap_AndAPropertyMap_ReadTheirListsAsTheFirstValue()
    {
        const string untyped = """
            [
              {"id":4096,"label":"Part","name":["M3 Screw"],"qty":[250],"mass":[0.35],"rohs":[true],"aliases":[]}
            ]
            """;

        const string propertyMap = """
            [
              {"@type":"g:Map","@value":[
                {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
                {"@type":"g:T","@value":"label"},"Part",
                "name",{"@type":"g:List","@value":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1kq-35s-sl"}},"value":"M3 Screw","label":"name"}}]},
                "qty",{"@type":"g:List","@value":[{"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1l4-35s-2dh"}},"value":{"@type":"g:Int32","@value":250},"label":"qty"}}]}
              ]}
            ]
            """;

        var row = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(untyped).RootElement).Nodes);

        Assert.Equal("4096", row.Id);
        Assert.Equal("Part", row.Label);
        Assert.Equal("M3 Screw", row.Properties["name"]);
        Assert.Equal("250", row.Properties["qty"]);
        Assert.Equal("0.35", row.Properties["mass"]);
        Assert.Equal("true", row.Properties["rohs"]);
        Assert.False(row.Properties.ContainsKey("aliases"));

        var mapped = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(propertyMap).RootElement).Nodes);

        Assert.Equal("M3 Screw", mapped.Properties["name"]);
        Assert.Equal("250", mapped.Properties["qty"]);
    }

    //A projection that gathers a vertex's values under properties, project('id','label','properties').by(id).by(label)
    //.by(valueMap()), holds them as valueMap() does, a plain list for each, in a map GraphSON 3 types. Each list reads as
    //its first value, as the same vertex's own properties do. Until 2026-10-31 reading it threw, and the viewer could
    //only say it could not read the result (GraphDBViewer issue 2).
    [Fact]
    public void ToTable_AProjectionsPropertiesMapOfLists_ReadsEachAsItsFirstValue()
    {
        const string untyped = """
            [
              {"id":4096,"label":"Part","properties":{"name":["M3 Screw"],"qty":[250],"mass":[0.35],"rohs":[true],"aliases":[]}}
            ]
            """;

        const string typed = """
            [
              {"@type":"g:Map","@value":[
                "id",{"@type":"g:Int64","@value":4096},
                "label","Part",
                "properties",{"@type":"g:Map","@value":[
                  "name",{"@type":"g:List","@value":["M3 Screw"]},
                  "qty",{"@type":"g:List","@value":[{"@type":"g:Int32","@value":250}]},
                  "aliases",{"@type":"g:List","@value":[]}
                ]}
              ]}
            ]
            """;

        var row = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(untyped).RootElement).Nodes);

        Assert.Equal("4096", row.Id);
        Assert.Equal("Part", row.Label);
        Assert.Equal("M3 Screw", row.Properties["name"]);
        Assert.Equal("250", row.Properties["qty"]);
        Assert.Equal("0.35", row.Properties["mass"]);
        Assert.Equal("true", row.Properties["rohs"]);
        Assert.False(row.Properties.ContainsKey("aliases"));

        var fromTyped = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(typed).RootElement).Nodes);

        Assert.Equal("4096", fromTyped.Id);
        Assert.Equal("M3 Screw", fromTyped.Properties["name"]);
        Assert.Equal("250", fromTyped.Properties["qty"]);
        Assert.False(fromTyped.Properties.ContainsKey("aliases"));
        Assert.Equal(["name", "qty"], fromTyped.Properties.Keys.Order());
    }

    //The same projection by(valueMap().by(unfold())) holds each value alone, and GraphSON 3 types a number. It reads as
    //the number, as a typed value read anywhere else does. Until 2026-10-31 reading it threw (GraphDBViewer issue 2).
    [Fact]
    public void ToTable_AProjectionsPropertiesMapOfSingleTypedValues_ReadsEachAsItsValue()
    {
        const string typed = """
            [
              {"@type":"g:Map","@value":[
                "id",{"@type":"g:Int64","@value":4096},
                "label","Part",
                "properties",{"@type":"g:Map","@value":[
                  "name","M3 Screw",
                  "qty",{"@type":"g:Int32","@value":250},
                  "mass",{"@type":"g:Double","@value":0.35}
                ]}
              ]}
            ]
            """;

        var row = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(typed).RootElement).Nodes);

        Assert.Equal("M3 Screw", row.Properties["name"]);
        Assert.Equal("250", row.Properties["qty"]);
        Assert.Equal("0.35", row.Properties["mass"]);
    }

    //g.E().valueMap(true) on JanusGraph, as the dev graph answered it on 2026-09-29, with a value of the edge's own: an
    //id typed as the edge's RelationIdentifier, a label, single values, and neither end. It is an edge, listed with its id
    //and its values and drawn on neither canvas, since nothing says where its ends are. Until then it read as a vertex,
    //drawn as a node, and an edit or a drop of it went to g.V() with the edge's id.
    private const string EdgeValueMapWire = """
        [
          {"@type":"g:Map","@value":[
            {"@type":"g:T","@value":"id"},{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"63ibk0-1csmbk-8lqt-215x60"}},
            {"@type":"g:T","@value":"label"},"follows",
            "weight",{"@type":"g:Double","@value":0.5}
          ]}
        ]
        """;

    [Fact]
    public void ToTable_AnEdgesValueMapOnJanusGraph_IsAnEdgeWithNoEnds_NotAVertex()
    {
        var results = JsonDocument.Parse(EdgeValueMapWire).RootElement;
        var table = GraphDataConverter.ToTable(results);

        Assert.Empty(table.Nodes);

        var edge = Assert.Single(table.Edges);

        Assert.Equal("63ibk0-1csmbk-8lqt-215x60", edge.Id);
        Assert.Equal("follows", edge.Label);
        Assert.Equal("0.5", edge.Properties["weight"]);
        Assert.Null(edge.Source);
        Assert.Null(edge.Target);
        Assert.Equal("janusgraph:RelationIdentifier", GraphDataConverter.FindIdType(results, edge.Id, isEdge: true));
        Assert.Empty(GraphDataConverter.VertexLabels(results));
        Assert.Equal(new[] { "follows" }, GraphDataConverter.EdgeLabels(results));

        var force = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results)).RootElement;

        Assert.Equal(0, force.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, force.GetProperty("links").GetArrayLength());
        Assert.Equal(0, JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement.GetArrayLength());
    }

    //A vertex property has an id of the same type, and reads as its id, its label and its value: g.V().properties() on
    //JanusGraph. Telling an edge's value map from a vertex's leaves it to read as it did, which is not as an edge.
    [Fact]
    public void ToTable_AVertexPropertyOnJanusGraph_IsNotReadAsAnEdgeWithNoEnds()
    {
        const string wire = """
            [
              {"@type":"g:VertexProperty","@value":{"id":{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"1kq-35s-sl"}},"value":"motor","label":"tags"}}
            ]
            """;

        Assert.Empty(GraphDataConverter.ToTable(JsonDocument.Parse(wire).RootElement).Edges);
    }

    //An edge naming one end, the other written null, as the staged-edit preview writes an end it does not know. The
    //table shows the data as it came: the row stays, with the end the edge did not name empty. Until 2026-11-07 the
    //missing end was filled in from the other, and the row said the edge was a loop on the end it named.
    private const string OneEndedEdgesWire = """
        [
          { "id": "1", "label": "person" },
          { "id": "e1", "label": "x", "outV": "1", "inV": null },
          { "id": "e2", "label": "y", "outV": null, "inV": "1" }
        ]
        """;

    [Fact]
    public void ToTable_AnEdgeNamingOneEnd_KeepsItsRow_WithTheOtherEndEmpty()
    {
        var table = GraphDataConverter.ToTable(JsonDocument.Parse(OneEndedEdgesWire).RootElement);

        Assert.Equal("1", Assert.Single(table.Nodes).Id);
        Assert.Equal(2, table.Edges.Count);

        var outOnly = table.Edges.Single(e => e.Id == "e1");
        var inOnly = table.Edges.Single(e => e.Id == "e2");

        Assert.Equal("1", outOnly.Source);
        Assert.Null(outOnly.Target);
        Assert.Null(inOnly.Source);
        Assert.Equal("1", inOnly.Target);
    }

    //There is nothing to draw the edge to, so neither canvas draws it, as ToDot leaves it out; until 2026-11-07 both
    //drew it as a loop on the end it named.
    [Fact]
    public void AnEdgeNamingOneEnd_IsDrawnOnNeitherCanvas_RatherThanAsALoopOnThatEnd()
    {
        var results = JsonDocument.Parse(OneEndedEdgesWire).RootElement;

        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement;

        Assert.Equal("1", Assert.Single(elements.EnumerateArray()).GetProperty("data").GetProperty("id").GetString());

        var force = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results)).RootElement;

        Assert.Equal(1, force.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, force.GetProperty("links").GetArrayLength());
    }

    //An edge naming only its in end, with no outV key at all, is an edge and nothing else, as one naming only its out end
    //is: until 2026-11-07 the vertex test ruled an element out by its outV alone, so this one was read as a vertex in the
    //table and drawn as a node on both canvases, beside its row as an edge.
    [Fact]
    public void AnEdgeWithAnInEndAndNoOutKey_IsAnEdgeAlone_NeverAVertex()
    {
        var results = JsonDocument.Parse("""
            [
              { "id": "1", "label": "person" },
              { "id": "e2", "label": "y", "inV": "1" }
            ]
            """).RootElement;

        var table = GraphDataConverter.ToTable(results);

        Assert.Equal("1", Assert.Single(table.Nodes).Id);
        Assert.Equal("e2", Assert.Single(table.Edges).Id);

        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(results)).RootElement;

        Assert.Equal("1", Assert.Single(elements.EnumerateArray()).GetProperty("data").GetProperty("id").GetString());

        var force = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results)).RootElement;

        Assert.Equal(1, force.GetProperty("nodes").GetArrayLength());
    }

    //A property stored under the name id, which JanusGraph takes (it refuses only label), is a key of its own beside
    //the g:T token. The token is the element's, and a stored name never takes the id or the label from it, whichever
    //comes first on the wire; the row's id is what every edit of it is addressed by.
    [Fact]
    public void ToTable_AStoredPropertyNamedId_NeverTakesTheElementsIdFromItsToken()
    {
        var json = """
            [
              {"@type":"g:Map","@value":[
                {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
                {"@type":"g:T","@value":"label"},"Part",
                "id","ABC-1",
                "name","M3 Screw"
              ]},
              {"@type":"g:Map","@value":[
                "id","XYZ-9",
                {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":8200},
                {"@type":"g:T","@value":"label"},"Part",
                "name","M4 Nut"
              ]}
            ]
            """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        Assert.Equal(["4096", "8200"], table.Nodes.Select(n => n.Id));
        Assert.All(table.Nodes, n => Assert.Equal("g:Int64", n.IdType));
    }

    //An element map's plain keys are the element's own property names, and a vertex may store a number under type.
    //Only a string there names the element's type, as Cosmos DB's "vertex" and "edge" do; a number is not asked for one.
    [Fact]
    public void ToTable_AnElementMapWithANumberStoredUnderType_ReadsAsTheVertex()
    {
        var json = """
            [
              {"@type":"g:Map","@value":[
                {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
                {"@type":"g:T","@value":"label"},"Part",
                "type",{"@type":"g:Int32","@value":3},
                "name","M3 Screw"
              ]}
            ]
            """;

        var row = Assert.Single(GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement).Nodes);

        Assert.Equal("4096", row.Id);
        Assert.Equal("M3 Screw", row.Properties.GetValueOrDefault("name"));
    }

    //Any key the wire types reads as the text of what it wraps: a token as its name, a number as its digits. A map
    //keyed by numbers, as groupCount() by a number property answers, converts rather than throwing.
    [Fact]
    public void UnwrapElement_AGMapWithTypedKeys_BecomesAnObjectKeyedByTheirText()
    {
        var wrapped = JsonDocument.Parse("""
            {"@type":"g:Map","@value":[
              {"@type":"g:T","@value":"label"},"Part",
              {"@type":"g:Direction","@value":"OUT"},"out",
              {"@type":"g:Int32","@value":250},{"@type":"g:Int64","@value":3},
              "name","M3 Screw"
            ]}
            """).RootElement;

        var unwrapped = GraphDataConverter.UnwrapElement(wrapped);

        Assert.Equal(["label", "OUT", "250", "name"], unwrapped.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Part", unwrapped.GetProperty("label").GetString());
        Assert.Equal("out", unwrapped.GetProperty("OUT").GetString());
        Assert.Equal(3, GraphDataConverter.UnwrapElement(unwrapped.GetProperty("250")).GetInt64());
    }

    //And it reaches the table, which is where it was missing: its own column beside the Id and Label the
    //table gives every row, with the element's own metadata still out of the columns.
    [Fact]
    public void ToTable_AStoredPropertyNamedLabel_IsItsOwnColumn()
    {
        var json = """
        [
          { "id": "1", "label": "Person", "properties": { "label": [ { "value": "Ada Lovelace" } ], "name": [ { "value": "Ada" } ] } }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var row = Assert.Single(table.Nodes);

        Assert.Contains("label", table.NodePropertyColumns);
        Assert.Equal("Ada Lovelace", row.Properties["label"]);
        Assert.Equal("Person", row.Label);
        Assert.DoesNotContain("id", table.NodePropertyColumns);
    }

    #endregion

    #region CSV export

    [Fact]
    public void ToCsv_ProducesHeaderVerticesEdges_AndQuotesCommas()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "name": [ { "value": "Bob" } ] } },
          { "id": "2", "label": "person", "properties": { "name": [ { "value": "Al,ice" } ] } },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2" }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var csv = GraphDataConverter.ToCsv(table);
        var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');

        Assert.Equal("kind,id,label,source,target,name", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("vertex,1,person,,,Bob"));
        Assert.Contains(lines, l => l.StartsWith("edge,e1,knows,1,2"));
        Assert.Contains(lines, l => l.Contains("\"Al,ice\""));
    }

    //Excel and LibreOffice run a CSV cell that begins with =, +, -, @, a tab or a carriage return as a formula, so a
    //stored =HYPERLINK(...) opened a link and =cmd|' /C calc'!A0 ran a program when the export was opened (OWASP's CSV
    //injection). Such a cell is written behind an apostrophe, which both read as text; a cell that reads as a plain
    //number, -5 or +3.5, is left alone, since it is one.
    [Fact]
    public void ToCsv_ACellThatASpreadsheetWouldRunAsAFormula_IsWrittenBehindAnApostrophe_AndANumberIsLeftAlone()
    {
        var json = """
        [
          { "id": "f", "label": "cell", "properties": { "v": [ { "value": "=HYPERLINK(\"http://x\",\"go\")" } ] } },
          { "id": "p", "label": "cell", "properties": { "v": [ { "value": "+cmd" } ] } },
          { "id": "m", "label": "cell", "properties": { "v": [ { "value": "-cmd|' /C calc'!A0" } ] } },
          { "id": "a", "label": "cell", "properties": { "v": [ { "value": "@SUM(1)" } ] } },
          { "id": "t", "label": "cell", "properties": { "v": [ { "value": "\t=1" } ] } },
          { "id": "r", "label": "cell", "properties": { "v": [ { "value": "\r=1" } ] } },
          { "id": "n", "label": "cell", "properties": { "v": [ { "value": "-5" } ] } },
          { "id": "d", "label": "cell", "properties": { "v": [ { "value": "+3.5" } ] } },
          { "id": "w", "label": "cell", "properties": { "v": [ { "value": "a=b" } ] } }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var lines = GraphDataConverter.ToCsv(table).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        //A cell holding a quote or a comma is still quoted, apostrophe and all.
        Assert.Equal("vertex,f,cell,,,\"'=HYPERLINK(\"\"http://x\"\",\"\"go\"\")\"", lines[1]);
        Assert.Equal("vertex,p,cell,,,'+cmd", lines[2]);
        Assert.Equal("vertex,m,cell,,,'-cmd|' /C calc'!A0", lines[3]);
        Assert.Equal("vertex,a,cell,,,'@SUM(1)", lines[4]);
        Assert.Equal("vertex,t,cell,,,'\t=1", lines[5]);
        Assert.Equal("vertex,r,cell,,,\"'\r=1\"", lines[6]);
        Assert.Equal("vertex,n,cell,,,-5", lines[7]);
        Assert.Equal("vertex,d,cell,,,+3.5", lines[8]);
        Assert.Equal("vertex,w,cell,,,a=b", lines[9]);
    }

    #endregion

    #region DOT export

    [Fact]
    public void ToDot_ProducesDigraphWithNodeNamesAndEdgeLabels()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "name": [ { "value": "Bob" } ] } },
          { "id": "2", "label": "person", "properties": { "name": [ { "value": "Alice" } ] } },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2" }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var dot = GraphDataConverter.ToDot(table).Replace("\r\n", "\n");

        Assert.StartsWith("digraph G {", dot);
        Assert.EndsWith("}", dot.TrimEnd());
        Assert.Contains("\"1\" [label=\"Bob\"];", dot);
        Assert.Contains("\"2\" [label=\"Alice\"];", dot);
        Assert.Contains("\"1\" -> \"2\" [label=\"knows\"];", dot);
    }

    [Fact]
    public void ToDot_EscapesQuotes_AndFallsBackToTypeLabel()
    {
        var json = """
        [
          { "id": "n1", "label": "device", "properties": { "title": [ { "value": "a \"quoted\" name" } ] } },
          { "id": "n2", "label": "device" }
        ]
        """;

        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var dot = GraphDataConverter.ToDot(table);

        //Quotes inside a label are backslash-escaped so the DOT stays valid.
        Assert.Contains("a \\\"quoted\\\" name", dot);
        //A vertex with no name property falls back to its type label.
        Assert.Contains("\"n2\" [label=\"device\"];", dot);
    }

    #endregion

    #region Schema (data model)

    [Fact]
    public void BuildSchemaFromData_AggregatesLabelsKeysAndRelationships()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "name": [{"value":"Alice"}], "age": [{"value":"30"}] } },
          { "id": "2", "label": "person", "properties": { "name": [{"value":"Bob"}] } },
          { "id": "3", "label": "company", "properties": { "name": [{"value":"Acme"}] } },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2" },
          { "id": "e2", "label": "worksAt", "outV": "1", "inV": "3" },
          { "id": "e3", "label": "worksAt", "outV": "2", "inV": "3" }
        ]
        """;

        var schemaJson = GraphDataConverter.BuildSchemaFromData(JsonDocument.Parse(json).RootElement);
        var schema = JsonDocument.Parse(schemaJson).RootElement;

        var nodes = new List<JsonElement>();
        var edges = new List<JsonElement>();
        foreach (var el in schema.EnumerateArray())
            if (el.TryGetProperty("outV", out _))
                edges.Add(el);
            else
                nodes.Add(el);

        //One node per vertex label; one edge per distinct (srcLabel, edgeLabel, tgtLabel).
        Assert.Equal(2, nodes.Count);
        Assert.Equal(2, edges.Count);

        var person = nodes.Single(n => n.GetProperty("id").GetString() == "person");
        Assert.Equal("2", person.GetProperty("properties").GetProperty("count").GetString());
        Assert.Equal("age, name", person.GetProperty("properties").GetProperty("keys").GetString());

        var company = nodes.Single(n => n.GetProperty("id").GetString() == "company");
        Assert.Equal("1", company.GetProperty("properties").GetProperty("count").GetString());

        Assert.Contains(edges, e => e.GetProperty("label").GetString() == "knows" && e.GetProperty("outV").GetString() == "person" && e.GetProperty("inV").GetString() == "person" && e.GetProperty("properties").GetProperty("count").GetString() == "1");
        Assert.Contains(edges, e => e.GetProperty("label").GetString() == "worksAt" && e.GetProperty("outV").GetString() == "person" && e.GetProperty("inV").GetString() == "company" && e.GetProperty("properties").GetProperty("count").GetString() == "2");
    }

    #endregion

    #region Pinned positions

    [Fact]
    public void ToForceGraphJson_EmitsFixedPositionsFrom3dReservedProps()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": {
              "name": [{"value":"Alice"}],
              "gdbvX3d": [{"value":"100"}], "gdbvY3d": [{"value":"-50"}], "gdbvZ3d": [{"value":"25"}] } }
        ]
        """;

        var output = GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement);
        var node = JsonDocument.Parse(output).RootElement.GetProperty("nodes")[0];

        Assert.Equal(100, node.GetProperty("fx").GetDouble());
        Assert.Equal(-50, node.GetProperty("fy").GetDouble());
        Assert.Equal(25, node.GetProperty("fz").GetDouble());
    }

    [Fact]
    public void PinnedPositions_2dAnd3dAreIndependent()
    {
        //Node 1 is pinned only in 2D (gdbvX/Y); node 2 only in 3D (gdbvX3d/Y3d/Z3d). Neither viewer
        //should pick up the other viewer's keys.
        var json = """
        [
          { "id": "1", "label": "person", "properties": {
              "gdbvX": [{"value":"12"}], "gdbvY": [{"value":"34"}] } },
          { "id": "2", "label": "person", "properties": {
              "gdbvX3d": [{"value":"5"}], "gdbvY3d": [{"value":"6"}], "gdbvZ3d": [{"value":"7"}] } }
        ]
        """;
        var data = JsonDocument.Parse(json).RootElement;

        var force = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(data)).RootElement.GetProperty("nodes");
        var n1Force = force.EnumerateArray().First(n => n.GetProperty("id").GetString() == "1");
        var n2Force = force.EnumerateArray().First(n => n.GetProperty("id").GetString() == "2");
        Assert.False(n1Force.TryGetProperty("fx", out _));//2D-only pin ignored in 3D
        Assert.Equal(5, n2Force.GetProperty("fx").GetDouble());

        var cyto = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(data)).RootElement;
        var n1Cyto = cyto.EnumerateArray().First(e => e.GetProperty("data").GetProperty("id").GetString() == "1");
        var n2Cyto = cyto.EnumerateArray().First(e => e.GetProperty("data").GetProperty("id").GetString() == "2");
        Assert.True(n1Cyto.TryGetProperty("position", out _));
        Assert.False(n2Cyto.TryGetProperty("position", out _));//3D-only pin ignored in 2D
    }

    [Fact]
    public void ToCytoscapeJson_EmitsPresetPositionFromReservedProps()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": {
              "gdbvX": [{"value":"12"}], "gdbvY": [{"value":"34"}] } }
        ]
        """;

        var output = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement);
        var node = JsonDocument.Parse(output).RootElement[0];

        Assert.Equal(12, node.GetProperty("position").GetProperty("x").GetDouble());
        Assert.Equal(34, node.GetProperty("position").GetProperty("y").GetDouble());
        Assert.Equal(12, node.GetProperty("data").GetProperty("px").GetDouble());
    }

    #endregion

    #region Per-node styling (database)

    [Fact]
    public void ToCytoscapeJson_EmitsColorAndSizeFromPerNodeProps()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {
            "gdbvColor": [{"value":"#e67e22"}], "gdbvSize": [{"value":"48"}] } } ]
        """;

        var result = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement);

        Assert.Contains("\"bgColor\":\"#e67e22\"", result);
        Assert.Contains("\"nodeSize\":48", result);
    }

    [Fact]
    public void ToForceGraphJson_EmitsColorAndSizeFromPerNodeProps()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {
            "gdbvColor": [{"value":"#123456"}], "gdbvSize": [{"value":"30"}] } } ]
        """;

        var result = GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement);

        Assert.Contains("\"bgColor\":\"#123456\"", result);
        Assert.Contains("\"nodeSize\":30", result);
    }

    [Fact]
    public void ToCytoscapeJson_PerNodeColor_OverridesLabelStyle()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": { "gdbvColor": [{"value":"#e67e22"}] } } ]
        """;
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Color = "#0000ff" } };

        var result = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles);

        Assert.Contains("\"bgColor\":\"#e67e22\"", result);
        Assert.DoesNotContain("#0000ff", result);
    }

    [Fact]
    public void ToCytoscapeJson_PerNodeDisplay_PicksThatPropertyAsLabel()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {
            "name": [{"value":"Alice"}], "code": [{"value":"A-42"}],
            "gdbvDisplay": [{"value":"code"}] } } ]
        """;

        var output = GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement);
        var node = JsonDocument.Parse(output).RootElement[0];

        Assert.Equal("A-42", node.GetProperty("data").GetProperty("label").GetString());
    }

    #endregion

    #region Search suggestions

    [Fact]
    public void SearchSuggestions_ReturnsDistinctSortedDisplayLabels()
    {
        var json = """
        [
          {
            "v": { "id": "1", "label": "person", "properties": { "Name": [{ "value": "Bob" }] } },
            "e": { "id": "e1", "label": "knows" },
            "o": { "id": "2", "label": "person", "properties": { "Name": [{ "value": "Alice" }] } }
          },
          {
            "v": { "id": "2", "label": "person", "properties": { "Name": [{ "value": "Alice" }] } },
            "e": { "id": "e2", "label": "made" },
            "o": { "id": "3", "label": "software", "properties": { "Name": [{ "value": "Gremlin" }] } }
          }
        ]
        """;

        var suggestions = GraphDataConverter.SearchSuggestions(JsonDocument.Parse(json).RootElement);

        //Distinct (Alice appears in both triples) and sorted case-insensitively.
        Assert.Equal(new[] { "Alice", "Bob", "Gremlin" }, suggestions);
    }

    [Fact]
    public void SearchSuggestions_StandaloneVertices_UsesDisplayLabels()
    {
        var json = """
        [
          { "id": "v1", "label": "city", "properties": { "name": [{ "value": "Seattle" }] } },
          { "id": "v2", "label": "city", "properties": { "name": [{ "value": "Portland" }] } }
        ]
        """;

        var suggestions = GraphDataConverter.SearchSuggestions(JsonDocument.Parse(json).RootElement);

        Assert.Equal(new[] { "Portland", "Seattle" }, suggestions);
    }

    [Fact]
    public void SearchSuggestions_DedupesByText_AcrossDifferentVertices()
    {
        var json = """
        [
          { "id": "v1", "label": "material", "properties": { "name": [{ "value": "Paint" }] } },
          { "id": "v2", "label": "material", "properties": { "name": [{ "value": "Paint" }] } }
        ]
        """;

        var suggestions = GraphDataConverter.SearchSuggestions(JsonDocument.Parse(json).RootElement);

        Assert.Equal(new[] { "Paint" }, suggestions);
    }

    [Fact]
    public void SearchSuggestions_NonArray_ReturnsEmpty()
    {
        var el = JsonDocument.Parse("{}").RootElement;

        Assert.Empty(GraphDataConverter.SearchSuggestions(el));
    }

    [Fact]
    public void SearchSuggestions_RespectsMaxCap()
    {
        var items = Enumerable.Range(0, 10)
            .Select(i => $$"""{ "id": "v{{i}}", "label": "n", "properties": { "name": [{ "value": "node{{i}}" }] } }""");
        var json = "[" + string.Join(",", items) + "]";

        var suggestions = GraphDataConverter.SearchSuggestions(JsonDocument.Parse(json).RootElement, max: 3);

        Assert.Equal(3, suggestions.Count);
    }

    #endregion

    #region Node shape (2D / 3D)

    [Fact]
    public void ToCytoscapeJson_DefaultShape_IsRectangle()
    {
        var json = """[ { "id": "1", "label": "person", "properties": {} } ]""";

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement[0];

        Assert.Equal("rectangle", node.GetProperty("data").GetProperty("shape").GetString());
    }

    [Fact]
    public void ToForceGraphJson_DefaultShape3d_IsSphere()
    {
        var json = """[ { "id": "1", "label": "person", "properties": {} } ]""";

        var node = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement)).RootElement.GetProperty("nodes")[0];

        Assert.Equal("sphere", node.GetProperty("shape3d").GetString());
    }

    [Fact]
    public void ToCytoscapeJson_LabelStyleShape_EmitsShape()
    {
        var json = """[ { "id": "1", "label": "person", "properties": {} } ]""";
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Shape = "hexagon" } };

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles)).RootElement[0];

        Assert.Equal("hexagon", node.GetProperty("data").GetProperty("shape").GetString());
    }

    [Fact]
    public void ToCytoscapeJson_PerNodeShape_OverridesLabelShape()
    {
        var json = """[ { "id": "1", "label": "person", "properties": { "gdbvShape": [{"value":"circle"}] } } ]""";
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Shape = "hexagon" } };

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles)).RootElement[0];

        Assert.Equal("circle", node.GetProperty("data").GetProperty("shape").GetString());
    }

    [Fact]
    public void ToForceGraphJson_Shape3d_FromPerNodeProperty()
    {
        var json = """[ { "id": "1", "label": "person", "properties": { "gdbvShape3d": [{"value":"pyramid"}] } } ]""";

        var result = GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement);

        Assert.Contains("\"shape3d\":\"pyramid\"", result);
    }

    [Fact]
    public void ToForceGraphJson_2dAnd3dShapes_AreIndependent()
    {
        //The 2D canvas shape and the 3D solid are separate settings on the label style.
        var json = """[ { "id": "1", "label": "person", "properties": {} } ]""";
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Shape = "circle", Shape3d = "cube" } };

        var node = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement, styles)).RootElement.GetProperty("nodes")[0];

        Assert.Equal("circle", node.GetProperty("shape").GetString());
        Assert.Equal("cube", node.GetProperty("shape3d").GetString());
    }

    #endregion

    #region Show details on canvas

    //A label whose style asks for details marks each of its vertices for the 2D canvas's card; one without, none.
    [Fact]
    public void ToCytoscapeJson_LabelStyleDetails_MarksItsVertices()
    {
        var json = """[ { "id": "1", "label": "person", "properties": {} }, { "id": "2", "label": "company", "properties": {} } ]""";
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Details = true } };

        var nodes = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles)).RootElement;

        Assert.True(nodes[0].GetProperty("data").GetProperty("details").GetBoolean());
        Assert.False(nodes[1].GetProperty("data").TryGetProperty("details", out _));
    }

    //A vertex's own gdbvDetails wins over its label's style, either way.
    [Theory]
    [InlineData("false", true, false)]
    [InlineData("true", false, true)]
    public void ToCytoscapeJson_PerNodeDetails_OverridesTheLabels(string own, bool labelOn, bool expected)
    {
        var json = $$"""[ { "id": "1", "label": "person", "properties": { "gdbvDetails": [{"value":"{{own}}"}] } } ]""";
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Details = labelOn } };

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement, styles)).RootElement[0];

        Assert.Equal(expected, node.GetProperty("data").GetProperty("details").GetBoolean());
    }

    //Detailed view on edges (2026-11-07): an edge label Styling turned it on for marks its edges, in 2D and 3D alike, by
    //either path an edge comes by: on its own, or between its two vertices in a traversal's triple.
    private const string EdgesOnTheirOwnJson = """
        [
          { "id": "1", "label": "person", "properties": {} },
          { "id": "2", "label": "person", "properties": {} },
          { "id": "e1", "label": "knows", "outV": "1", "inV": "2", "properties": { "since": "2020" } },
          { "id": "e2", "label": "likes", "outV": "2", "inV": "1", "properties": {} }
        ]
        """;

    private const string EdgesInTriplesJson = """
        [
          {
            "v": { "id": "1", "label": "person", "properties": {} },
            "e": { "id": "e1", "label": "knows", "outV": "1", "inV": "2", "properties": { "since": "2020" } },
            "o": { "id": "2", "label": "person", "properties": {} }
          },
          {
            "v": { "id": "2", "label": "person", "properties": {} },
            "e": { "id": "e2", "label": "likes", "outV": "2", "inV": "1", "properties": {} },
            "o": { "id": "1", "label": "person", "properties": {} }
          }
        ]
        """;

    //Each edge's details mark, by label, in the 2D elements and the 3D links.
    private static (Dictionary<string, bool> TwoD, Dictionary<string, bool> ThreeD) EdgeDetailsMarks(string json, IReadOnlyCollection<string> edgeDetails)
    {
        var results = JsonDocument.Parse(json).RootElement;
        var twoD = CytoscapeEdges(GraphDataConverter.ToCytoscapeJson(results, edgeDetails: edgeDetails))
            .ToDictionary(d => d.GetProperty("label").GetString(), d => d.GetProperty("details").GetBoolean());
        var links = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(results, edgeDetails: edgeDetails)).RootElement.GetProperty("links");
        var threeD = links.EnumerateArray().ToDictionary(l => l.GetProperty("label").GetString(), l => l.GetProperty("details").GetBoolean());

        return (twoD, threeD);
    }

    [Theory]
    [InlineData(EdgesOnTheirOwnJson)]
    [InlineData(EdgesInTriplesJson)]
    public void EdgeDetails_MarkTheEdgesOfTheLabelsTurnedOn(string json)
    {
        var (twoD, threeD) = EdgeDetailsMarks(json, ["knows"]);
        var (noneTwoD, noneThreeD) = EdgeDetailsMarks(json, null);

        Assert.True(twoD["knows"]);
        Assert.False(twoD["likes"]);
        Assert.True(threeD["knows"]);
        Assert.False(threeD["likes"]);
        Assert.False(noneTwoD["knows"]);
        Assert.False(noneThreeD["knows"]);
    }

    //An edge's own gdbvDetails wins over its label's switch, either way, as a vertex's does.
    [Theory]
    [InlineData("false", true, false)]
    [InlineData("true", false, true)]
    public void EdgeDetails_AnEdgesOwnGdbvDetailsWins(string own, bool labelOn, bool expected)
    {
        var properties = new Dictionary<string, string> { [GdbvKeys.Details] = own };
        IReadOnlyCollection<string> labels = [];

        if (labelOn)
            labels = ["knows"];

        Assert.Equal(expected, GraphDataConverter.EdgeDetailsOf("knows", properties, labels));
    }

    [Fact]
    public void GdbvDetails_IsAViewerKey()
    {
        Assert.Contains(GdbvKeys.Details, GdbvKeys.All);
    }

    #endregion

    #region Shown properties (gdbvShow)

    [Fact]
    public void ToCytoscapeJson_ShownProperties_EmitsSelectedKeyValueLines()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {
            "name": [{"value":"Bob"}], "age": [{"value":"27"}], "city": [{"value":"Paris"}],
            "gdbvShow": [{"value":"name,age"}] } } ]
        """;

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement[0];
        var shown = node.GetProperty("data").GetProperty("showProps").EnumerateArray().Select(e => e.GetString()).ToList();

        //Only the keys named in gdbvShow, in that order — "city" is excluded.
        Assert.Equal(new[] { "name: Bob", "age: 27" }, shown);
    }

    [Fact]
    public void ToCytoscapeJson_NoShowList_OmitsShowProps()
    {
        var json = """[ { "id": "1", "label": "person", "properties": { "name": [{"value":"Bob"}] } } ]""";

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement[0];

        Assert.False(node.GetProperty("data").TryGetProperty("showProps", out _));
    }

    [Fact]
    public void ToForceGraphJson_ShownProperties_EmitsShowProps()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": { "name": [{"value":"Bob"}], "gdbvShow": [{"value":"name"}] } } ]
        """;

        var node = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(JsonDocument.Parse(json).RootElement)).RootElement.GetProperty("nodes")[0];
        var shown = node.GetProperty("showProps").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(new[] { "name: Bob" }, shown);
    }

    [Fact]
    public void ShowList_WithImageAndModel_ExcludesThemFromShowProps()
    {
        //gdbvImage / gdbvModel share the gdbvShow list but render as the image / model itself, so they
        //must never appear in the text lines drawn beneath the node — only the real property does.
        var json = """
        [ { "id": "1", "label": "part", "properties": {
            "name": [{"value":"Bolt"}], "gdbvImage": "https://x/i.png", "gdbvModel": "https://x/m.obj",
            "gdbvShow": "name,gdbvImage,gdbvModel" } } ]
        """;

        var node = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(JsonDocument.Parse(json).RootElement)).RootElement[0];
        var shown = node.GetProperty("data").GetProperty("showProps").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(new[] { "name: Bolt" }, shown);
    }

    #endregion
}
