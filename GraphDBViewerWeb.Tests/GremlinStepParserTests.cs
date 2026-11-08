using System.Linq;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class GremlinStepParserTests
{
    [Fact]
    public void Parse_SplitsTopLevelSteps()
    {
        var steps = GremlinStepParser.Parse("g.V().has('name','Alice').out('knows')");

        Assert.Equal(new[] { "g", "V()", "has('name','Alice')", "out('knows')" }, steps.Select(s => s.Text));
        Assert.True(steps[0].IsSource);
        Assert.False(steps[0].Debuggable);
        Assert.True(steps[1].Debuggable);
        Assert.True(steps[3].Debuggable);
    }

    [Fact]
    public void Parse_KeepsNestedTraversalAsOneStep()
    {
        var steps = GremlinStepParser.Parse("g.V().where(__.out('knows')).values('age')");

        Assert.Equal(new[] { "g", "V()", "where(__.out('knows'))", "values('age')" }, steps.Select(s => s.Text));
    }

    [Fact]
    public void Parse_DoesNotSplitOnDotsOrParensInsideStrings()
    {
        var steps = GremlinStepParser.Parse("g.V().has('name','a.b(c)').out()");

        Assert.Equal(new[] { "g", "V()", "has('name','a.b(c)')", "out()" }, steps.Select(s => s.Text));
    }

    [Fact]
    public void Parse_MarksModulatorsAndTerminals()
    {
        var steps = GremlinStepParser.Parse("g.V().group().by('name').by(count()).toList()");

        var by = steps.Where(s => s.Name == "by").ToList();
        Assert.Equal(2, by.Count);
        Assert.All(by, s => Assert.True(s.IsModulator));
        Assert.All(by, s => Assert.False(s.Debuggable));

        Assert.True(steps.Single(s => s.Name == "group").Debuggable);
        Assert.True(steps.Single(s => s.Name == "toList").IsTerminal);
        Assert.False(steps.Single(s => s.Name == "toList").Debuggable);
    }

    [Fact]
    public void Parse_RangesMapBackToTheQuery()
    {
        var query = "g.V().out('knows')";
        var steps = GremlinStepParser.Parse(query);

        var outStep = steps.Single(s => s.Name == "out");
        Assert.Equal("out('knows')", query.Substring(outStep.Start, outStep.End - outStep.Start));

        //A prefix up to and including a step is the query text from 0 to the step's end.
        Assert.Equal("g.V().out('knows')", query.Substring(0, outStep.End));
    }

    [Theory]
    [InlineData("g.addV('person')", true)]
    [InlineData("g.V().property('age', 30)", true)]
    [InlineData("g.V().sideEffect(addV('x'))", true)]
    [InlineData("g.V().hasLabel('person').drop()", true)]
    [InlineData("g.V(1).next().remove()", true)]
    [InlineData("graph.addVertex('person')", true)]
    [InlineData("g.io('/tmp/x.json').read().iterate()", true)]
    [InlineData("g.V().has('name','Alice')", false)]
    [InlineData("g.V().properties('name')", false)]
    [InlineData("g.V().has('drop','x')", false)]
    //A mutating step's call inside a string is text, whichever quote holds it, and neither an escaped quote nor the
    //other kind of quote ends the string early.
    [InlineData("""g.V().has("name","drop()")""", false)]
    [InlineData("""g.V().has('name','it\'s drop()')""", false)]
    [InlineData("""g.V().has("name","it's drop()")""", false)]
    public void IsMutating_DetectsGraphWrites(string query, bool expected)
    {
        Assert.Equal(expected, GremlinStepParser.IsMutating(query));
    }

    private static string[] TreeNamesDepths(string query)
    {
        return GremlinStepParser.ParseTree(query).Select(s => $"{s.Name}:{s.Depth}").ToArray();
    }

    [Fact]
    public void ParseTree_FlatQuery_AllDepthZero()
    {
        Assert.Equal(new[] { "g:0", "V:0", "hasLabel:0", "out:0" }, TreeNamesDepths("g.V().hasLabel('Component').out('composes')"));
    }

    [Fact]
    public void ParseTree_RecursesIntoWhereSubTraversal()
    {
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "__:1", "out:1", "hasLabel:1" }, TreeNamesDepths("g.V().where(__.out('composes').hasLabel('Assembly'))"));
    }

    [Fact]
    public void ParseTree_RepeatWithModulatorAndSubTraversal()
    {
        Assert.Equal(new[] { "g:0", "V:0", "repeat:0", "__:1", "both:1", "times:0" }, TreeNamesDepths("g.V().repeat(__.both()).times(2)"));
    }

    [Fact]
    public void ParseTree_AndWithTwoBranches()
    {
        Assert.Equal(new[] { "g:0", "V:0", "and:0", "__:1", "out:1", "__:1", "in:1" }, TreeNamesDepths("g.V().and(__.out('a'), __.in('b'))"));
    }

    [Fact]
    public void ParseTree_NestedTwoLevelsDeep()
    {
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "__:1", "and:1", "__:2", "out:2", "__:2", "in:2" }, TreeNamesDepths("g.V().where(__.and(__.out(), __.in()))"));
    }

    [Fact]
    public void ParseTree_IgnoresPredicateAndLiteralArgs()
    {
        //P.gt(5) is a predicate and 'age' a literal — neither is a __-sub-traversal, so no depth-1 steps.
        Assert.All(GremlinStepParser.ParseTree("g.V().has('age', P.gt(5)).out()"), s => Assert.Equal(0, s.Depth));
    }

    [Fact]
    public void ParseTree_LeavesABareArgumentAlone_UnlessAskedToReadIt()
    {
        var q = "g.V().where(V().has('name', 'b')).has('age', P.gt(5))";

        Assert.Equal(new[] { "g:0", "V:0", "where:0", "has:0" }, TreeNamesDepths(q));
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "V:1", "has:1", "has:0" }, GremlinStepParser.ParseTree(q, true).Select(s => $"{s.Name}:{s.Depth}").ToArray());
    }

    [Fact]
    public void ParseTree_SubStepRangesAreAbsolute()
    {
        var q = "g.V().where(__.out('composes'))";

        var outStep = GremlinStepParser.ParseTree(q).First(s => s.Name == "out" && s.Depth == 1);

        Assert.Equal("out('composes')", q.Substring(outStep.Start, outStep.End - outStep.Start));
    }

    private static string[] DebugTreeNamesDepths(string query)
    {
        return GremlinStepParser.ParseDebugTree(query).Select(s => $"{s.Name}:{s.Depth}").ToArray();
    }

    [Fact]
    public void ParseDebugTree_StepsIntoATraversalWrittenWithout__WhereAStepTakesOne()
    {
        //Groovy runs where(out('created')) as where(__.out('created')), so the debugger steps into it the same.
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "out:1", "values:0" }, DebugTreeNamesDepths("g.V().where(out('created')).values('name')"));
        Assert.Equal(new[] { "g:0", "V:0", "repeat:0", "out:1", "until:0", "has:1" }, DebugTreeNamesDepths("g.V().repeat(out()).until(has('name', 'x'))"));
        Assert.Equal(new[] { "g:0", "V:0", "choose:0", "hasLabel:1", "out:1", "in:1" }, DebugTreeNamesDepths("g.V().choose(hasLabel('person'), out(), in())"));
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "not:1", "out:2" }, DebugTreeNamesDepths("g.V().where(not(out('x')))"));
        Assert.Equal(new[] { "g:0", "V:0", "group:0", "by:0", "by:0", "count:1" }, DebugTreeNamesDepths("g.V().group().by(label).by(count())"));
    }

    [Fact]
    public void ParseDebugTree_LeavesPredicatesAndValuesAlone_And__TraversalsAsTheyWere()
    {
        //A predicate reads like a call, and a step that takes values takes no traversal: nothing to step into.
        Assert.All(GremlinStepParser.ParseDebugTree("g.V().has('age', gt(30)).has('name', within('a', 'b'))"), s => Assert.Equal(0, s.Depth));
        Assert.All(GremlinStepParser.ParseDebugTree("g.V().has('age', not(within(1, 2)))"), s => Assert.Equal(0, s.Depth));
        Assert.Equal(new[] { "g:0", "V:0", "as:0", "out:0", "where:0" }, DebugTreeNamesDepths("g.V().as('a').out().where('a', eq('b'))"));

        //And one written with __ is read as it always was.
        Assert.Equal(new[] { "g:0", "V:0", "where:0", "__:1", "out:1", "hasLabel:1" }, DebugTreeNamesDepths("g.V().where(__.out('composes').hasLabel('Assembly'))"));
    }

    [Fact]
    public void ParseTree_ReadingBareCalls_StillReadsAPredicateAsOne_ForTheCallersThatLookForSteps()
    {
        //The instance graph's time limit looks for particular steps at any depth (GremlinTimeLimit), and asks for every call.
        Assert.Equal(new[] { "g:0", "V:0", "has:0", "gt:1" }, GremlinStepParser.ParseTree("g.V().has('age', gt(5))", true).Select(s => $"{s.Name}:{s.Depth}").ToArray());
    }
}
