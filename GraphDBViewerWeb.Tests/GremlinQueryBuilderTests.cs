using System.Collections.Generic;
using System.Reflection;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GremlinQueryBuilderTests
{
    //── Escape ──────────────────────────────────────────────────────────

    [Fact]
    public void Escape_Null_ReturnsEmpty()
    {
        Assert.Equal("", GremlinQueryBuilder.Escape(null));
    }

    [Fact]
    public void Escape_PlainValue_Unchanged()
    {
        Assert.Equal("plain", GremlinQueryBuilder.Escape("plain"));
    }

    [Fact]
    public void Escape_SingleQuote_IsBackslashEscaped()
    {
        Assert.Equal("O\\'Brien", GremlinQueryBuilder.Escape("O'Brien"));
    }

    [Fact]
    public void Escape_Backslash_IsDoubled()
    {
        Assert.Equal("a\\\\b", GremlinQueryBuilder.Escape("a\\b"));
    }

    [Fact]
    public void Escape_LineBreaks_AreEscaped_BecauseGroovyRefusesThemRaw()
    {
        //g.inject('Red<LF>Blue') is "Unexpected character" on a real Gremlin Server, and g.inject('Red\nBlue') is
        //the same two words on two lines (2026-09-15). A backslash before them is still only doubled.
        Assert.Equal("Red\\nBlue", GremlinQueryBuilder.Escape("Red\nBlue"));
        Assert.Equal("one\\r\\ntwo", GremlinQueryBuilder.Escape("one\r\ntwo"));
        Assert.Equal("a\\\\nb", GremlinQueryBuilder.Escape("a\\nb"));
    }

    //── FormatId ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("-7", "-7")]
    public void FormatId_Numeric_EmittedBare(string id, string expected)
    {
        Assert.Equal(expected, GremlinQueryBuilder.FormatId(id));
    }

    [Fact]
    public void FormatId_NonNumeric_IsQuoted()
    {
        Assert.Equal("'thomas'", GremlinQueryBuilder.FormatId("thomas"));
    }

    [Fact]
    public void FormatId_NonNumericWithQuote_IsQuotedAndEscaped()
    {
        Assert.Equal("'O\\'Brien'", GremlinQueryBuilder.FormatId("O'Brien"));
    }

    //Digits are written bare only in the form a number is written in. Bare, Groovy reads 007 as the octal 7, another
    //element's id, and 08 as no number at all; +5 and " 5" it reads as 5. Each is a string id, and quoted it is the
    //one it names. A leading zero is one rule whatever Groovy makes of the digits after it: 007 stands for 08 and 00.
    [Theory]
    [InlineData("007")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("-0")]
    public void FormatId_DigitsNotInANumbersOwnForm_AreQuoted(string id)
    {
        Assert.Equal($"'{id}'", GremlinQueryBuilder.FormatId(id));
        Assert.Equal($"'{id}'", GremlinQueryBuilder.FormatId(id, null));
    }

    //── ElementPrefix ───────────────────────────────────────────────────

    [Fact]
    public void ElementPrefix_Node_UsesV()
    {
        Assert.Equal("g.V(1)", GremlinQueryBuilder.ElementPrefix("node", "1"));
        Assert.Equal("g.V('x')", GremlinQueryBuilder.ElementPrefix("node", "x"));
    }

    [Fact]
    public void ElementPrefix_Edge_UsesE()
    {
        Assert.Equal("g.E(5)", GremlinQueryBuilder.ElementPrefix("edge", "5"));
    }

    //── Creation ────────────────────────────────────────────────────────

    [Fact]
    public void AddVertex_TrimsAndQuotesLabel()
    {
        Assert.Equal("g.addV('person')", GremlinQueryBuilder.AddVertex("  person  "));
    }

    [Fact]
    public void AddVertex_Null_ProducesEmptyLabel()
    {
        Assert.Equal("g.addV('')", GremlinQueryBuilder.AddVertex(null));
    }

    [Fact]
    public void AddVertexWithName_EmitsLabelAndNameProperty()
    {
        Assert.Equal(
            "g.addV('Component').property('name', 'Component 1')",
            GremlinQueryBuilder.AddVertexWithName("Component", "Component 1"));
    }

    [Fact]
    public void AddVertexWithName_EscapesQuotesInName()
    {
        Assert.Equal(
            "g.addV('Component').property('name', 'O\\'Brien')",
            GremlinQueryBuilder.AddVertexWithName("Component", "O'Brien"));
    }

    [Fact]
    public void AddVertexWithNameAt_EmitsNameAndGdbvPosition()
    {
        Assert.Equal(
            "g.addV('Component').property('name', 'Component 1').property('gdbvX', '12.5').property('gdbvY', '-3')",
            GremlinQueryBuilder.AddVertexWithNameAt("Component", "Component 1", 12.5, -3));
    }

    [Fact]
    public void AddEdge_NumericIds_UsesAnonymousTraversal()
    {
        Assert.Equal("g.V(1).addE('knows').to(__.V(2))", GremlinQueryBuilder.AddEdge("1", "knows", "2"));
    }

    [Fact]
    public void AddEdge_StringIds_AreQuoted()
    {
        Assert.Equal("g.V('a').addE('knows').to(__.V('b'))", GremlinQueryBuilder.AddEdge("a", "knows", "b"));
    }

    [Fact]
    public void AddVertexWithProperties_EmitsTIdAndEachProperty()
    {
        var props = new Dictionary<string, string> { ["name"] = "Alice", ["age"] = "30" };

        Assert.Equal(
            "g.addV('person').property(T.id, 'a').property('name', 'Alice').property('age', '30')",
            GremlinQueryBuilder.AddVertexWithProperties("person", "a", props));
    }

    [Fact]
    public void AddVertexWithProperties_NumericIdIsBare()
    {
        Assert.Equal(
            "g.addV('person').property(T.id, 5)",
            GremlinQueryBuilder.AddVertexWithProperties("person", "5", new Dictionary<string, string>()));
    }

    [Fact]
    public void AddEdgeWithProperties_AppendsProperties()
    {
        var props = new Dictionary<string, string> { ["since"] = "2020" };

        Assert.Equal(
            "g.V('a').addE('knows').to(__.V('b')).property('since', '2020')",
            GremlinQueryBuilder.AddEdgeWithProperties("a", "knows", "b", props));
    }

    //── Deletion ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("node", "g.V(1).drop()")]
    [InlineData("edge", "g.E(1).drop()")]
    public void DropElement_RoutesByType(string type, string expected)
    {
        Assert.Equal(expected, GremlinQueryBuilder.DropElement(type, "1"));
    }

    //── Property mutation ───────────────────────────────────────────────

    [Fact]
    public void SetProperty_Node_FormatsKeyAndValue()
    {
        Assert.Equal("g.V(1).property('name', 'Bob')", GremlinQueryBuilder.SetProperty("node", "1", "name", "Bob"));
    }

    [Fact]
    public void SetProperty_EscapesQuoteInValue()
    {
        Assert.Equal("g.V(1).property('name', 'O\\'Brien')", GremlinQueryBuilder.SetProperty("node", "1", "name", "O'Brien"));
    }

    [Fact]
    public void DropProperty_Edge_UsesE()
    {
        Assert.Equal("g.E(2).properties('weight').drop()", GremlinQueryBuilder.DropProperty("edge", "2", "weight"));
    }

    //── Graph loading ───────────────────────────────────────────────────

    [Fact]
    public void TestConnection_IsCheapLimitQuery()
    {
        Assert.Equal("g.V().limit(1)", GremlinQueryBuilder.TestConnection);
    }

    //One read for a whole label, whatever a generated graph named: this is what keeps matching a hundred
    //entities against the database from being a hundred queries. The names are one list, not an argument each,
    //since a Groovy call with 254 of them was refused on the development JanusGraph (2026-10-23).
    [Fact]
    public void VerticesByName_AsksForOneLabelsNames_Escaped_AsOneList()
    {
        var query = GremlinQueryBuilder.VerticesByName("Company", new[] { "Acme Inc.", "O'Neill & Sons" }, 100);

        Assert.Equal("g.V().hasLabel('Company').has('name', within(['Acme Inc.', 'O\\'Neill & Sons'])).limit(100)", query);
    }

    [Fact]
    public void LimitedVertices_AppliesLimit()
    {
        Assert.Equal("g.V().limit(10)", GremlinQueryBuilder.LimitedVertices(10));
    }

    [Fact]
    public void FullGraph_WithLimit_IncludesLimit()
    {
        Assert.Equal("g.V().limit(25).fold().union(__.unfold(), __.unfold().outE())", GremlinQueryBuilder.FullGraph(25));
    }

    [Fact]
    public void FullGraph_WithoutLimit_WalksWholeGraph()
    {
        //Regression: the old bothE()-based query dropped edgeless vertices. The union keeps every vertex.
        Assert.Equal("g.V().fold().union(__.unfold(), __.unfold().outE())", GremlinQueryBuilder.FullGraph(null));
    }

    //── Inspection ──────────────────────────────────────────────────────

    [Fact]
    public void VertexDisplayLabel_UsesCoalesceOverNameAndLabel()
    {
        var query = GremlinQueryBuilder.VertexDisplayLabel("1");
        Assert.StartsWith("g.V(1)", query);
        Assert.Contains("coalesce(values('name','Name','title','Title'), label())", query);
    }

    [Fact]
    public void InEdges_ProjectsEdgeAndSourceVertex()
    {
        var query = GremlinQueryBuilder.InEdges("1");
        Assert.Contains("g.V(1).inE().as('e').outV().as('v')", query);
        Assert.Contains("project('eId','eLabel','vId','vLabel')", query);
    }

    [Fact]
    public void OutEdges_ProjectsEdgeAndTargetVertex()
    {
        var query = GremlinQueryBuilder.OutEdges("1");
        Assert.Contains("g.V(1).outE().as('e').inV().as('v')", query);
        Assert.Contains("project('eId','eLabel','vId','vLabel')", query);
    }

    //── Viewer property cleanup ─────────────────────────────────────────

    [Fact]
    public void DropAllViewerProperties_TargetsBothVerticesAndEdges()
    {
        var query = GremlinQueryBuilder.DropAllViewerProperties();
        Assert.Contains("g.V().properties(", query);
        Assert.Contains("g.E().properties(", query);
        Assert.Contains(").drop()", query);
    }

    [Fact]
    public void DropAllViewerProperties_DropsEveryReservedKey()
    {
        var query = GremlinQueryBuilder.DropAllViewerProperties();

        foreach (var key in GdbvKeys.All)
            Assert.Contains($"'{key}'", query);
    }

    [Fact]
    public void GdbvKeys_All_HoldsEveryReservedKeyWithThePrefix()
    {
        //Every key constant, found by reflection, so one added later and left out of All, which the database cleanup
        //strips, fails here. And All holds nothing that is not one of them.
        var keys = typeof(GdbvKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(GdbvKeys.Prefix))
            .Select(f => (string)f.GetRawConstantValue())
            .ToList();

        Assert.Empty(keys.Except(GdbvKeys.All));
        Assert.Empty(GdbvKeys.All.Except(keys));
        Assert.All(GdbvKeys.All, k => Assert.StartsWith(GdbvKeys.Prefix, k));
    }

    //── Typed ids (GraphSON id-type preservation) ───────────────────────

    [Fact]
    public void FormatId_Int64_AppendsLongSuffix()
    {
        Assert.Equal("727L", GremlinQueryBuilder.FormatId("727", "g:Int64"));
    }

    [Fact]
    public void FormatId_Int32_StaysBare()
    {
        Assert.Equal("727", GremlinQueryBuilder.FormatId("727", "g:Int32"));
    }

    [Fact]
    public void FormatId_UnknownType_FallsBackToHeuristic()
    {
        //Null / untyped numeric stays bare, exactly like the type-less overload.
        Assert.Equal("727", GremlinQueryBuilder.FormatId("727", null));
    }

    [Fact]
    public void FormatId_NonNumericTypedInt64_IsQuotedNotSuffixed()
    {
        //A non-numeric id must never become an invalid 'abc'L literal, whatever type is claimed.
        Assert.Equal("'abc'", GremlinQueryBuilder.FormatId("abc", "g:Int64"));
    }

    //A typed id is the number its type says, and is written the way that number is: 010L is the octal 8 to Groovy.
    [Fact]
    public void FormatId_TypedNumberWithALeadingZero_IsWrittenAsTheNumber()
    {
        Assert.Equal("10L", GremlinQueryBuilder.FormatId("010", "g:Int64"));
        Assert.Equal("10", GremlinQueryBuilder.FormatId("010", "g:Int32"));
    }

    [Fact]
    public void DropVertex_Int64Id_UsesLongLiteral()
    {
        Assert.Equal("g.V(727L).drop()", GremlinQueryBuilder.DropVertex("727", "g:Int64"));
    }

    [Fact]
    public void SetProperty_Int64EdgeId_UsesLongLiteral()
    {
        Assert.Equal("g.E(727L).property('w', '2')", GremlinQueryBuilder.SetProperty("edge", "727", "w", "2", "g:Int64"));
    }

    [Fact]
    public void DropProperty_Int64EdgeId_UsesLongLiteral()
    {
        Assert.Equal("g.E(727L).properties('w').drop()", GremlinQueryBuilder.DropProperty("edge", "727", "w", "g:Int64"));
    }

    [Fact]
    public void DropElement_Int64Edge_UsesLongLiteral()
    {
        //Regression: a bare g.E(727) is an Integer and silently matches no Long-id edge.
        Assert.Equal("g.E(727L).drop()", GremlinQueryBuilder.DropElement("edge", "727", "g:Int64"));
    }
}
