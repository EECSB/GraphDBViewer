using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///ArangoDB's debugger. AQL has both halves natively: the optimizer will explain a query without running
///it, and the cursor API will profile one it runs, reporting what each execution node did.
///
///Unlike Cypher's <c>PROFILE</c> / <c>EXPLAIN</c>, neither is a prefix — explain is a different endpoint
///and profile is an option on the request — so what this composes is a marked query that
///<see cref="ArangoDb"/> routes on. See <see cref="AqlPlan"/> for why the markers are AQL comments.
///
///Stepping is not offered, for the same reason as Cypher: the Steps tab runs the query truncated after
///each step, which only means something where a prefix is itself a valid query. <c>FOR p IN persons</c>
///is not a query. The plan answers the same question honestly instead.
///</summary>
public sealed class AqlQueryDebugger : IGraphQueryDebugger
{
    public static readonly AqlQueryDebugger Instance = new();

    public bool SupportsStepping => false;

    public string OperationHeader => "Node";
    public string ElementHeader => "Items";
    public string EffortHeader => "Calls";

    public string ProfileQuery(string query)
    {
        return Marked(AqlPlan.ProfileMarker, query);
    }

    public string ExplainQuery(string query)
    {
        return Marked(AqlPlan.ExplainMarker, query);
    }

    //A query already marked keeps only the marker being asked for, so profiling something the user has
    //explain-marked doesn't send both.
    private static string Marked(string marker, string query)
    {
        var (_, unmarked) = AqlPlan.ReadMarker(query);

        return marker + " " + (unmarked ?? "").Trim();
    }

    public (double TotalMs, List<MetricsRow> Rows) ParseProfile(GraphDbResult result)
    {
        var rows = new List<MetricsRow>();

        if (result.IsError || !AqlPlan.IsPlan(result.Table))
            return (0, rows);

        double total = 0;

        foreach (var row in result.Table.Rows)
        {
            var metrics = new MetricsRow
            {
                Depth = (int)ReadLong(row, AqlPlan.DepthColumn),
                Name = NodeText(row),
                ElementCount = ReadLong(row, AqlPlan.ItemsColumn),
                TraverserCount = ReadLong(row, AqlPlan.CallsColumn),
                DurationMs = ReadDouble(row, AqlPlan.TimeColumn)
            };

            total += metrics.DurationMs;
            rows.Add(metrics);
        }

        //The node times are each node's own share, so they add up to the execution — the phases before it
        //(parsing, optimizing) are in the raw response rather than folded in here, where they would make
        //the percentages describe something other than the rows above them.
        foreach (var row in rows)
        {
            if (total > 0)
                row.PercentDur = row.DurationMs / total * 100;
        }

        return (total, rows);
    }

    ///<summary>
    ///Renders an explain answer as an indented plan. Nothing ran, so what there is to show is the optimizer's
    ///own expectations: the node, what it works on, and how many items it thinks will come out of it.
    ///</summary>
    public string ParseExplain(GraphDbResult result)
    {
        if (result.IsError)
            return result.Error;

        if (!AqlPlan.IsPlan(result.Table))
            return result.ToString();

        var sb = new StringBuilder();

        foreach (var row in result.Table.Rows)
        {
            var depth = (int)ReadLong(row, AqlPlan.DepthColumn);
            sb.Append(new string(' ', depth * 2));
            sb.Append(NodeText(row));

            var estimate = Estimate(row);

            if (estimate.Length > 0)
                sb.Append("  ").Append(estimate);

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    //The node with what it works on — the line that says a plan will scan a collection rather than use an index.
    private static string NodeText(Dictionary<string, string> row)
    {
        var name = Cell(row, AqlPlan.NodeColumn);
        var details = Cell(row, AqlPlan.DetailsColumn);

        if (string.IsNullOrWhiteSpace(details))
            return name;

        return $"{name}  ({details})";
    }

    private static string Estimate(Dictionary<string, string> row)
    {
        var items = Cell(row, AqlPlan.EstimatedItemsColumn);
        var cost = Cell(row, AqlPlan.EstimatedCostColumn);

        if (items.Length == 0 && cost.Length == 0)
            return "";

        return $"est. {items} items, cost {cost}";
    }

    private static string Cell(Dictionary<string, string> row, string column)
    {
        if (row.TryGetValue(column, out var value))
            return value ?? "";

        return "";
    }

    private static long ReadLong(Dictionary<string, string> row, string column)
    {
        if (long.TryParse(Cell(row, column), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return value;

        return 0;
    }

    private static double ReadDouble(Dictionary<string, string> row, string column)
    {
        if (double.TryParse(Cell(row, column), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return value;

        return 0;
    }
}
