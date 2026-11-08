using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GremlinEditParserTests
{
    //── Typed-id normalization (canvas preview vs. Long ids) ────────────

    [Fact]
    public void Parse_DropEdge_LongLiteralId_YieldsPlainId()
    {
        //Regression: a typed drop g.E(727L).drop() must preview against the rendered edge id "727",
        //otherwise the edge stays on the canvas even though the DB commit removes it.
        var edits = GremlinEditParser.Parse("g.E(727L).drop()");

        Assert.Single(edits);
        Assert.Equal(GraphEditKind.RemoveEdge, edits[0].Kind);
        Assert.Equal("727", edits[0].Id);
    }

    [Fact]
    public void Parse_DropVertex_LongLiteralId_YieldsPlainId()
    {
        var edits = GremlinEditParser.Parse("g.V(775L).drop()");

        Assert.Single(edits);
        Assert.Equal(GraphEditKind.RemoveNode, edits[0].Kind);
        Assert.Equal("775", edits[0].Id);
    }

    [Fact]
    public void Parse_SetProperty_LongEdgeId_YieldsPlainId()
    {
        var edits = GremlinEditParser.Parse("g.E(727L).property('weight', '2')");

        Assert.Single(edits);
        Assert.Equal(GraphEditKind.SetProperty, edits[0].Kind);
        Assert.Equal("727", edits[0].Id);
        Assert.Equal("weight", edits[0].Key);
        Assert.Equal("2", edits[0].Value);
    }

    [Fact]
    public void Parse_QuotedStringIdEndingInL_IsPreserved()
    {
        //A string id that genuinely ends in 'L' must not be truncated.
        var edits = GremlinEditParser.Parse("g.E('727L').drop()");

        Assert.Single(edits);
        Assert.Equal(GraphEditKind.RemoveEdge, edits[0].Kind);
        Assert.Equal("727L", edits[0].Id);
    }

    [Fact]
    public void Parse_PlainNumericId_Unchanged()
    {
        var edits = GremlinEditParser.Parse("g.E(727).drop()");

        Assert.Single(edits);
        Assert.Equal(GraphEditKind.RemoveEdge, edits[0].Kind);
        Assert.Equal("727", edits[0].Id);
    }

    //── End-to-end preview (parse -> apply -> edge gone) ────────────────

    [Fact]
    public void BuildEffectiveGraphSON_DropTypedEdge_RemovesItFromPreview()
    {
        var json = """
        [
          { "id": { "@type": "g:Int64", "@value": 1 }, "label": "person" },
          { "id": { "@type": "g:Int64", "@value": 2 }, "label": "person" },
          { "id": { "@type": "g:Int64", "@value": 727 }, "label": "knows", "outV": { "@type": "g:Int64", "@value": 1 }, "inV": { "@type": "g:Int64", "@value": 2 } }
        ]
        """;
        var data = JsonDocument.Parse(json).RootElement;

        var edits = GremlinEditParser.Parse("g.E(727L).drop()");
        var effective = GraphDataConverter.BuildEffectiveGraphSON(data, edits);

        var table = GraphDataConverter.ToTable(effective);
        Assert.Empty(table.Edges);
        Assert.Equal(2, table.Nodes.Count);
    }

    [Fact]
    public void Parse_AnAddEWithNoTo_EndsAtTheVertexItWasCalledOn_AsGremlinMakesTheLoop()
    {
        //Gremlin takes the traverser's vertex for an end addE is not given: g.V('1').addE('knows') is a loop on 1, and
        //with a from() an edge into 1 from there. Called on no vertex, g.addE('knows') has no end to take and keeps none.
        var loop = Assert.Single(GremlinEditParser.Parse("g.V('1').addE('knows')"));
        var into = Assert.Single(GremlinEditParser.Parse("g.V('1').addE('knows').from(__.V('2'))"));
        var none = Assert.Single(GremlinEditParser.Parse("g.addE('knows')"));

        Assert.Equal(("1", "1"), (loop.Source, loop.Target));
        Assert.Equal(("2", "1"), (into.Source, into.Target));
        Assert.Null(none.Source);
        Assert.Null(none.Target);
    }

    [Fact]
    public void BuildEffectiveGraphSON_AddEdgeMissingAnEnd_PreviewsNoNodeForIt_AndTheLoopOfOneWithNoTo()
    {
        //Two staged edges with ends they do not name: one with no from() or to() at all, and one from a vertex with
        //no to(). The parser gives the second the loop Gremlin makes of it, vertex 1 at both ends, and the preview
        //draws that loop; the first has no end the preview could draw, and is a row in the table alone. Until
        //2026-10-03 null read as an id of "", a node with no id on both canvases and an edge to it; until 2026-11-07
        //the loop was the canvases' own fallback for any edge naming one end, which now draws nothing.
        var data = JsonDocument.Parse("""[{ "id": "1", "label": "person" }]""").RootElement;
        var edits = GremlinEditParser.Parse("""
            g.addE('knows')
            g.V('1').addE('knows')
            """);

        var effective = GraphDataConverter.BuildEffectiveGraphSON(data, edits);
        var elements = JsonDocument.Parse(GraphDataConverter.ToCytoscapeJson(effective)).RootElement.EnumerateArray().Select(e => e.GetProperty("data")).ToList();
        var graph3d = JsonDocument.Parse(GraphDataConverter.ToForceGraphJson(effective)).RootElement;

        Assert.Equal(new[] { "1" }, elements.Where(d => !d.TryGetProperty("source", out _)).Select(d => d.GetProperty("id").GetString()));
        Assert.Equal(new[] { "1" }, graph3d.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        Assert.Single(graph3d.GetProperty("links").EnumerateArray());

        var loop = Assert.Single(elements, d => d.TryGetProperty("source", out _));

        Assert.Equal("1", loop.GetProperty("source").GetString());
        Assert.Equal("1", loop.GetProperty("target").GetString());

        var rows = GraphDataConverter.ToTable(effective).Edges;

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, e => e.Source == null && e.Target == null);
        Assert.Contains(rows, e => e.Source == "1" && e.Target == "1");
    }

    //── Offline re-import: exported addV/addE queries → drawing ─────────

    [Fact]
    public void LooksLikeGremlin_DetectsAddVAddE_NotOtherFormats()
    {
        Assert.True(GraphImport.LooksLikeGremlin("g.addV('person').property('name','Alice')"));
        Assert.True(GraphImport.LooksLikeGremlin("g.V('1').addE('knows').to(__.V('2'))"));
        Assert.False(GraphImport.LooksLikeGremlin("digraph { a -> b }"));
        Assert.False(GraphImport.LooksLikeGremlin("""[{ "id": "1", "label": "x" }]"""));
        Assert.False(GraphImport.LooksLikeGremlin(""));
    }

    [Fact]
    public void GremlinImport_RebuildsDrawingOverEmptyBaseline()
    {
        //The offline round-trip: re-importing exported addV/addE queries reconstructs the drawn graph by
        //applying them as edits over an empty baseline (no database).
        var gremlin = """
        g.addV('person').property(T.id, '1').property('name', 'Alice')
        g.addV('person').property(T.id, '2').property('name', 'Bob')
        g.V('1').addE('knows').to(__.V('2'))
        """;

        var empty = JsonDocument.Parse("[]").RootElement;
        var effective = GraphDataConverter.BuildEffectiveGraphSON(empty, GremlinEditParser.Parse(gremlin));
        var table = GraphDataConverter.ToTable(effective);

        Assert.Equal(2, table.Nodes.Count);
        Assert.Single(table.Edges);
    }

    //── MergePropertyEdits (multi-element staged edits accumulate) ──────

    [Fact]
    public void MergePropertyEdits_DifferentElement_Accumulates()
    {
        //The reported bug: editing a property on a second node must add a line, not replace the first.
        var buffer = "g.V(1).property('gdbvModel', 'u1')";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "2",
            new[] { "gdbvModel" },
            new[] { "g.V(2).property('gdbvModel', 'u2')" });

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("g.V(1).property('gdbvModel', 'u1')", lines);
        Assert.Contains("g.V(2).property('gdbvModel', 'u2')", lines);
    }

    [Fact]
    public void MergePropertyEdits_SameElementSameKey_Replaces()
    {
        var buffer = "g.V(1).property('gdbvModel', 'u1')";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "1",
            new[] { "gdbvModel" },
            new[] { "g.V(1).property('gdbvModel', 'u2')" });

        Assert.Equal("g.V(1).property('gdbvModel', 'u2')", result);
    }

    [Fact]
    public void MergePropertyEdits_SameElementUntouchedKey_Preserved()
    {
        //Re-selecting an element and editing a different key must not drop its earlier staged edit.
        var buffer = "g.V(1).property('gdbvModel', 'u1')";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "1",
            new[] { "gdbvColor" },
            new[] { "g.V(1).property('gdbvColor', '#f00')" });

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("g.V(1).property('gdbvModel', 'u1')", lines);
        Assert.Contains("g.V(1).property('gdbvColor', '#f00')", lines);
    }

    [Fact]
    public void MergePropertyEdits_RevertedKey_LineRemoved()
    {
        //Key touched this session but with no regenerated line (value reverted to original) -> line dropped.
        var buffer = "g.V(1).property('gdbvColor', '#f00')";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "1",
            new[] { "gdbvColor" },
            new string[0]);

        Assert.Equal("", result);
    }

    [Fact]
    public void MergePropertyEdits_ALinkStagedToANewVertex_FollowsItWhenALineBeforeItMoves()
    {
        //A staged vertex's stand-in is its statement's position, and the merge takes an element's earlier line out and
        //puts its new one at the end, which moved every staged add after it: the link below went on naming statement 1,
        //itself by then, and was lost at commit (2026-10-05). A value put back moves them the same way.
        var gremlin = GremlinQueryBuilderAdapter.Instance;
        var buffer = string.Join(
            "\n",
            gremlin.SetProperty("node", "1", "name", "first", null),
            gremlin.AddVertex("Part"),
            gremlin.AddEdge(StagedIds.ForVertex(1), "uses", "5"));

        var changed = GremlinEditParser.Parse(GremlinEditParser.MergePropertyEdits(buffer, "node", "1", ["name"], [gremlin.SetProperty("node", "1", "name", "second", null)]));
        var putBack = GremlinEditParser.Parse(GremlinEditParser.MergePropertyEdits(buffer, "node", "1", ["name"], []));

        foreach (var edits in new[] { changed, putBack })
            Assert.Equal(Assert.Single(edits, edit => edit.Kind == GraphEditKind.AddNode).Id, Assert.Single(edits, edit => edit.Kind == GraphEditKind.AddEdge).Source);

        Assert.Equal("second", Assert.Single(changed, edit => edit.Kind == GraphEditKind.SetProperty).Value);
        Assert.DoesNotContain(putBack, edit => edit.Kind == GraphEditKind.SetProperty);
    }

    [Fact]
    public void MergePropertyEdits_InTheConnectionsLanguage_ReplacesItsLine_AndAValuePutBackLeavesNone()
    {
        //The lines are written in the connection's language and were read with Gremlin's parser alone, which recognized
        //no Cypher, AQL, DQL or GUN line: each change stacked another line for the same key, and a value put back left
        //the earlier change staged, to be committed (2026-10-05).
        var cypher = CypherQueryBuilder.Instance;
        var first = cypher.SetProperty("node", "4:abc:1", "name", "first", null);
        var second = cypher.SetProperty("node", "4:abc:1", "name", "second", null);

        var merged = GremlinEditParser.MergePropertyEdits(first, "node", "4:abc:1", ["name"], [second], cypher.ParseEdits, "cypher");

        Assert.Equal(second, merged);
        Assert.Equal("", GremlinEditParser.MergePropertyEdits(merged, "node", "4:abc:1", ["name"], [], cypher.ParseEdits, "cypher"));
    }

    [Fact]
    public void MergePropertyEdits_NonPropertyQueries_Preserved()
    {
        var buffer = "g.V(9).drop()\ng.V(1).addE('knows').to(__.V(2))";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "1",
            new[] { "gdbvColor" },
            new[] { "g.V(1).property('gdbvColor', '#f00')" });

        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("g.V(9).drop()", lines);
        Assert.Contains("g.V(1).addE('knows').to(__.V(2))", lines);
        Assert.Contains("g.V(1).property('gdbvColor', '#f00')", lines);
    }

    [Fact]
    public void MergePropertyEdits_LongIdElement_MatchesAndReplaces()
    {
        //The staged line uses a Long literal (g.V(775L)); the element id is the plain "775".
        var buffer = "g.V(775L).property('gdbvModel', 'u1')";
        var result = GremlinEditParser.MergePropertyEdits(
            buffer,
            "node",
            "775",
            new[] { "gdbvModel" },
            new[] { "g.V(775L).property('gdbvModel', 'u2')" });

        Assert.Equal("g.V(775L).property('gdbvModel', 'u2')", result);
    }

    //Escape has written a line break as \n since 2026-09-15; until 2026-09-18 the parser read it back as
    //the letter n, so a two-line description previewed as one line with an n in the middle of it.
    [Fact]
    public void AValueWithALineBreakRoundTripsThroughTheBuffer()
    {
        var staged = new GremlinQueryBuilderAdapter().SetProperty("node", "1", "notes", "line one\r\nline two", null);

        Assert.DoesNotContain("\n", staged);

        var edits = GremlinEditParser.Parse(staged);

        Assert.Single(edits);
        Assert.Equal("line one\r\nline two", edits[0].Value);
    }
}
