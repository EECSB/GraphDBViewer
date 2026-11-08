using System.Linq;
using GraphDBViewerWeb.Code;
using Xunit;

namespace GraphDBViewerWeb.Tests;

public class GremlinDebugQueriesTests
{
    private static GremlinStep StepNamed(string query, string name)
    {
        return GremlinStepParser.Parse(query).First(s => s.Name == name);
    }

    [Fact]
    public void SimulationSuffix_ReturnsRemainingTraversal()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        Assert.Equal(".hasLabel('Component').out('composes')", GremlinDebugQueries.SimulationSuffix(q, StepNamed(q, "V")));
        Assert.Equal(".out('composes')", GremlinDebugQueries.SimulationSuffix(q, StepNamed(q, "hasLabel")));
    }

    [Fact]
    public void SimulationSuffix_LastStep_IsNull()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        Assert.Null(GremlinDebugQueries.SimulationSuffix(q, StepNamed(q, "out")));
    }

    [Fact]
    public void SimulationSuffix_StripsTrailingTerminalStep()
    {
        var q = "g.V().out('composes').toList()";

        Assert.Equal(".out('composes')", GremlinDebugQueries.SimulationSuffix(q, StepNamed(q, "V")));
    }

    [Fact]
    public void CanSimulate_TrueForElementStepWithDownstream()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        Assert.True(GremlinDebugQueries.CanSimulate(q, StepNamed(q, "V")));
        Assert.True(GremlinDebugQueries.CanSimulate(q, StepNamed(q, "hasLabel")));
    }

    [Fact]
    public void CanSimulate_FalseForLastStep()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        Assert.False(GremlinDebugQueries.CanSimulate(q, StepNamed(q, "out")));
    }

    [Fact]
    public void CanSimulate_FalseForNonElementStep()
    {
        var q = "g.V().values('name').dedup()";

        Assert.False(GremlinDebugQueries.CanSimulate(q, StepNamed(q, "values")));
    }

    [Fact]
    public void BuildSimulation_ProducesProjectionWithAnonymousSuffix()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        var sim = GremlinDebugQueries.BuildSimulation(q, StepNamed(q, "hasLabel"), 1000);

        Assert.Equal(
            "g.V().hasLabel('Component').limit(1000).project('id','label','name','survives','outCount').by(id()).by(label()).by(coalesce(values('name'),constant(''))).by(__.out('composes').limit(1).count()).by(__.out('composes').count())",
            sim);
    }

    [Fact]
    public void BuildSimulation_NullForNonSimulableSteps()
    {
        var q = "g.V().hasLabel('Component').out('composes')";

        Assert.Null(GremlinDebugQueries.BuildSimulation(q, StepNamed(q, "out"), 1000));//last step
        Assert.Null(GremlinDebugQueries.BuildSimulation("g.V().count()", StepNamed("g.V().count()", "count"), 1000));
    }

    [Fact]
    public void RemainingSteps_ReturnsDebuggableStepsAfterTheStep()
    {
        var q = "g.V().hasLabel('Component').out('composes').hasLabel('Assembly')";

        var names = GremlinDebugQueries.RemainingSteps(q, StepNamed(q, "hasLabel")).Select(s => s.Name).ToList();

        Assert.Equal(new[] { "out", "hasLabel" }, names);
    }

    //Digits not in a number's own form are a string id: bare, 007 is the octal 7 to Groovy. The debugger writes an id
    //by the rule every query does, whose own rows are GremlinQueryBuilderTests' FormatId tests.
    [Fact]
    public void FormatDebugId_QuotesDigitsNotInANumbersOwnForm_AsEveryQueryDoes()
    {
        Assert.Equal("'007'", GremlinDebugQueries.FormatDebugId("007"));
    }

    [Fact]
    public void BuildInspectionCount_ReentersFilteredToTheElement()
    {
        var q = "g.V().hasLabel('Component').out('composes').hasLabel('Assembly')";
        var step = StepNamed(q, "hasLabel");//the first hasLabel (Component)
        var upto = GremlinDebugQueries.RemainingSteps(q, step)[0];//out('composes')

        var count = GremlinDebugQueries.BuildInspectionCount(q, step, "964", upto);

        Assert.Equal("g.V().hasLabel('Component').hasId(964).limit(1).out('composes').count()", count);
    }
}
