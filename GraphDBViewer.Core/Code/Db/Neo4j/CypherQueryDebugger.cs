using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///Neo4j's debugger, built on the two things Cypher has natively: <c>PROFILE</c> runs the query and reports
///what each operator actually did, <c>EXPLAIN</c> plans it without running it. Both are prefixes rather
///than appended steps, and both answer with a plan tree that the Bolt drivers flatten into
///<see cref="CypherPlan"/> rows.
///
///Stepping is deliberately not offered. The Steps tab runs the query truncated after each step, which only
///means anything when a prefix is itself a valid query — true of a Gremlin traversal, not of a Cypher
///clause. The plan is the honest answer to the same question here: it shows what the engine will do and
///where the rows go, without pretending a half-written query can run.
///</summary>
public sealed class CypherQueryDebugger : IGraphQueryDebugger
{
    public static readonly CypherQueryDebugger Instance = new();

    public bool SupportsStepping => false;

    public string OperationHeader => "Operator";
    public string ElementHeader => "Rows";
    public string EffortHeader => "DB hits";

    public string ProfileQuery(string query)
    {
        return Prefixed("PROFILE", query);
    }

    public string ExplainQuery(string query)
    {
        return Prefixed("EXPLAIN", query);
    }

    //Both are query prefixes, and neither may be applied twice — a query the user already prefixed keeps
    //the one it has rather than becoming "PROFILE EXPLAIN …", which Neo4j rejects.
    private static string Prefixed(string keyword, string query)
    {
        var trimmed = (query ?? "").Trim();

        if (StartsWithKeyword(trimmed, "PROFILE"))
            trimmed = trimmed.Substring("PROFILE".Length).TrimStart();
        else if (StartsWithKeyword(trimmed, "EXPLAIN"))
            trimmed = trimmed.Substring("EXPLAIN".Length).TrimStart();

        return keyword + " " + trimmed;
    }

    private static bool StartsWithKeyword(string query, string keyword)
    {
        if (!query.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        //Only a whole word counts, so a query starting with an identifier like "PROFILES" is left alone.
        return query.Length == keyword.Length || char.IsWhiteSpace(query[keyword.Length]);
    }

    public (double TotalMs, List<MetricsRow> Rows) ParseProfile(GraphDbResult result)
    {
        var rows = new List<MetricsRow>();

        if (result.IsError || !CypherPlan.IsPlan(result.Table))
            return (0, rows);

        double total = 0;

        foreach (var row in result.Table.Rows)
        {
            var metrics = new MetricsRow
            {
                Depth = (int)ReadLong(row, CypherPlan.DepthColumn),
                Name = OperatorText(row),
                ElementCount = ReadLong(row, CypherPlan.RowsColumn),
                TraverserCount = ReadLong(row, CypherPlan.DbHitsColumn),
                DurationMs = ReadDouble(row, CypherPlan.TimeColumn)
            };

            total += metrics.DurationMs;
            rows.Add(metrics);
        }

        foreach (var row in rows)
        {
            if (total > 0)
                row.PercentDur = row.DurationMs / total * 100;
        }

        return (total, rows);
    }

    ///<summary>
    ///Renders an explain answer as an indented plan. EXPLAIN has not run the query, so there is nothing
    ///measured to show — only the operators and what each works on.
    ///</summary>
    public string ParseExplain(GraphDbResult result)
    {
        if (result.IsError)
            return result.Error;

        if (!CypherPlan.IsPlan(result.Table))
            return result.ToString();

        var sb = new StringBuilder();

        foreach (var row in result.Table.Rows)
        {
            var depth = (int)ReadLong(row, CypherPlan.DepthColumn);
            sb.Append(new string(' ', depth * 2));
            sb.AppendLine(OperatorText(row));
        }

        return sb.ToString().TrimEnd();
    }

    //The operator with what it worked on, which is what says the plan will scan a label rather than an index.
    private static string OperatorText(Dictionary<string, string> row)
    {
        var name = OperatorName(Cell(row, CypherPlan.OperatorColumn));
        var details = Cell(row, CypherPlan.DetailsColumn);

        if (string.IsNullOrWhiteSpace(details))
            return name;

        return $"{name}  ({details})";
    }

    //Neo4j suffixes an operator with the database it ran against — "ProduceResults@neo4j". The database is
    //the same for every row and already on the connection, so it is noise in a plan table.
    private static string OperatorName(string operatorType)
    {
        if (string.IsNullOrEmpty(operatorType))
            return "";

        int at = operatorType.IndexOf('@');

        if (at <= 0)
            return operatorType;

        return operatorType.Substring(0, at);
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
