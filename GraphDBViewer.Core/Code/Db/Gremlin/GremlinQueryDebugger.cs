using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///Gremlin's debugger: <c>profile()</c> and <c>explain()</c> appended to the traversal, and the
///<c>g:TraversalMetrics</c> answer read by <see cref="TraversalMetricsParser"/>. The behavior the viewer
///has always had, moved behind <see cref="IGraphQueryDebugger"/> so another engine can answer the same
///questions its own way.
///</summary>
public sealed class GremlinQueryDebugger : IGraphQueryDebugger
{
    public static readonly GremlinQueryDebugger Instance = new();

    ///<summary>Gremlin can: a traversal truncated after any step is itself a valid traversal.</summary>
    public bool SupportsStepping => true;

    public string OperationHeader => "Step";
    public string ElementHeader => "Elements";
    public string EffortHeader => "Traversers";

    public string ProfileQuery(string query)
    {
        return BaseQuery(query) + ".profile()";
    }

    public string ExplainQuery(string query)
    {
        return BaseQuery(query) + ".explain()";
    }

    public (double TotalMs, List<MetricsRow> Rows) ParseProfile(GraphDbResult result)
    {
        if (result.IsError)
            return (0, new List<MetricsRow>());

        return TraversalMetricsParser.Parse(result.Data);
    }

    public string ParseExplain(GraphDbResult result)
    {
        return result.ToString();
    }

    ///<summary>
    ///The query with any trailing terminal steps (toList / next / profile …) removed, so profile() or
    ///explain() can be appended to it.
    ///</summary>
    public static string BaseQuery(string query)
    {
        var trimmed = (query ?? "").Trim();
        var steps = GremlinStepParser.Parse(trimmed);

        for (int i = steps.Count - 1; i >= 0; i--)
            if (!steps[i].IsTerminal)
                return trimmed.Substring(0, steps[i].End);

        return trimmed;
    }
}
