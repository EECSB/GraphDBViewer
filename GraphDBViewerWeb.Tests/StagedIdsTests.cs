using System.Collections.Generic;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

//Committing a batch that refers to elements the batch itself creates. The stand-in ids the canvas draws
//uncommitted nodes under are not ids any database can look up, so they have to become real ones between
//one statement and the next. Before this, they went out as written and the commit half-landed: the adds
//succeeded and everything referring to them came back "Expected an id convertible to java.lang.Long".
public class StagedIdsTests
{
    private static GraphDbResult Vertex(string idJson)
    {
        var json = "[{\"@type\":\"g:Vertex\",\"@value\":{\"id\":" + idJson + ",\"label\":\"Component\"}}]";

        return GraphDbResult.Success(JsonDocument.Parse(json).RootElement.Clone());
    }

    private static Dictionary<string, ResolvedId> Recorded(int index, string id, string idType)
    {
        var map = new Dictionary<string, ResolvedId>();

        StagedIds.Record(map, index, id, idType, "gremlin");

        return map;
    }

    [Fact]
    public void AGremlinVertexAnswersWithItsIdAndItsType()
    {
        Assert.True(StagedIds.TryReadCreatedId(Vertex("{\"@type\":\"g:Int64\",\"@value\":5}"), out var id, out var type));

        Assert.Equal("5", id);
        Assert.Equal("g:Int64", type);
    }

    //A drop or a property edit creates nothing, and having no id is the answer rather than a failure.
    [Fact]
    public void AResultWithNothingInItHasNoId()
    {
        var empty = GraphDbResult.Success(JsonDocument.Parse("[]").RootElement.Clone());

        Assert.False(StagedIds.TryReadCreatedId(empty, out _, out _));
    }

    [Fact]
    public void AnErrorHasNoId()
    {
        Assert.False(StagedIds.TryReadCreatedId(GraphDbResult.Failure("boom"), out _, out _));
    }

    //The whole point: the quoted stand-in becomes a typed literal, quotes and all. Gremlin is strict
    //about id types, so quoting a Long finds nothing — the substitution has to replace the quotes too.
    [Fact]
    public void AQuotedStandInBecomesATypedLiteral()
    {
        var map = Recorded(0, "5", "g:Int64");

        Assert.Equal("g.V(5L).drop()", StagedIds.Resolve("g.V('__opt_v_0').drop()", map));
    }

    [Fact]
    public void AStringIdKeepsItsQuotes()
    {
        var map = Recorded(3, "abc", null);

        Assert.Equal("g.V('abc').drop()", StagedIds.Resolve("g.V('__opt_v_3').drop()", map));
    }

    [Fact]
    public void AStatementWithNoStandInsIsLeftExactlyAsItWas()
    {
        const string statement = "g.addV('Component').property('name', 'Component 1')";

        Assert.Equal(statement, StagedIds.Resolve(statement, Recorded(0, "5", "g:Int64")));
    }

    //The reported failure, start to finish: three adds, a delete of one of them, and two edges between
    //them. Every line that named a stand-in is now a line the database can run.
    [Fact]
    public void TheReportedBatchResolvesIntoSomethingRunnable()
    {
        var map = new Dictionary<string, ResolvedId>();

        StagedIds.Record(map, 0, "10", "g:Int64", "gremlin");
        StagedIds.Record(map, 1, "11", "g:Int64", "gremlin");
        StagedIds.Record(map, 2, "12", "g:Int64", "gremlin");

        Assert.Equal("g.V(12L).drop()", StagedIds.Resolve("g.V('__opt_v_2').drop()", map));
        Assert.Equal("g.V(11L).addE('test').to(__.V(10L))", StagedIds.Resolve("g.V('__opt_v_1').addE('test').to(__.V('__opt_v_0'))", map));
        Assert.Equal("g.V(10L).addE('test').to(__.V(11L))", StagedIds.Resolve("g.V('__opt_v_0').addE('test').to(__.V('__opt_v_1'))", map));
    }

    //An edge created by a batch can be referred to later in the same batch too, so both shapes of the
    //stand-in for a statement point at whatever that statement made.
    [Fact]
    public void AnEdgeStandInResolvesFromTheSameRecord()
    {
        var map = Recorded(4, "77", "g:Int64");

        Assert.Equal("g.E(77L).drop()", StagedIds.Resolve("g.E('__opt_e_4').drop()", map));
    }

    [Theory]
    [InlineData("g.V('__opt_v_0').drop()", true)]
    [InlineData("g.E('__opt_e_9').drop()", true)]
    [InlineData("g.V(5L).drop()", false)]
    [InlineData("", false)]
    public void AStatementKnowsWhetherItStillMentionsOne(string statement, bool expected)
    {
        Assert.Equal(expected, StagedIds.MentionsPlaceholder(statement));
    }

    //Committing has to walk the buffer exactly as the parser walked it, or the index a stand-in is named
    //after points at a different statement. Gremlin counts non-empty statements...
    [Fact]
    public void GremlinIsSplitTheWayItsParserCountsStatements()
    {
        var statements = StagedIds.Split("g.addV('A')\n\ng.addV('B')", "gremlin");

        Assert.Equal(2, statements.Count);
        Assert.Equal("g.addV('B')", statements[1]);
    }

    //...and the others count raw lines, blanks included, so a blank line must not shift what follows it.
    [Fact]
    public void CypherIsSplitTheWayItsParserCountsLines()
    {
        var statements = StagedIds.Split("CREATE (n:A)\n\nCREATE (m:B)", "cypher");

        Assert.Equal(3, statements.Count);
        Assert.Equal("CREATE (m:B)", statements[2]);
    }

    //The numbering the parser mints has to be the numbering committing reads back, so the two are taken
    //from the same place rather than each being written out by hand.
    [Fact]
    public void TheParserAndTheResolverAgreeOnWhatAStandInIsCalled()
    {
        var edits = GremlinEditParser.Parse("g.addV('Component')\ng.addV('Component')");

        Assert.Equal(StagedIds.ForVertex(0), edits[0].Id);
        Assert.Equal(StagedIds.ForVertex(1), edits[1].Id);
    }

    //And a blank line between them must not shift the second one's number, since committing skips blanks
    //without renumbering.
    [Fact]
    public void ABlankLineDoesNotShiftWhatFollowsIt()
    {
        var buffer = "g.addV('A')\n\ng.addV('B')";
        var statements = StagedIds.Split(buffer, "gremlin");
        var edits = GremlinEditParser.Parse(buffer);

        //Whatever the numbering is, the statement at index i in the walk is the one that mints index i.
        int second = statements.IndexOf("g.addV('B')");

        Assert.Equal(StagedIds.ForVertex(second), edits[1].Id);
    }
}
