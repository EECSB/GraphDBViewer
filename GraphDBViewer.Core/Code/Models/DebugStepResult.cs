namespace GraphDBViewerWeb.Code;

///<summary>
///One debuggable step's result: the traverser count after running the query up to this step.
///</summary>
public class DebugStepResult
{
    public GremlinStep Step { get; set; }
    public string Prefix { get; set; }
    public long? Count { get; set; }
    public string Error { get; set; }
    public string Sample { get; set; }
    public bool SampleLoaded { get; set; }
}

///<summary>
///One element at a simulated step: whether it reaches the query's final result, and how many results it
///yields — the per-traverser rows of the gdotV-style "traversal simulation".
///</summary>
public class SimulationRow
{
    public string Id { get; set; }
    public string Label { get; set; }
    public string Name { get; set; }
    public bool Survives { get; set; }
    public long OutCount { get; set; }
}

///<summary>One step of a traverser inspection: the count for a single traced element after this step, used
///to pinpoint where that element drops out of the query.</summary>
public class InspectStepRow
{
    public string StepText { get; set; }
    public long? Count { get; set; }
    public bool IsError { get; set; }
}
