namespace GraphDBViewerWeb.Code;

///<summary>
///The wire shape of a Bolt query plan, shared by everything that touches one.
///
///A plan does not arrive as records. <c>EXPLAIN</c> returns <b>no records at all</b> — the plan lives on
///the result summary — and <c>PROFILE</c> returns the query's normal records with the plan alongside them.
///Both Bolt drivers therefore flatten the plan tree into rows using these column names, so it reaches the
///viewer as an ordinary <see cref="GraphDbTable"/> and <see cref="CypherQueryDebugger"/> can read it back
///without either driver's object model.
///
///The JavaScript driver's interop mirrors these names literally; keep the two in step.
///</summary>
public static class CypherPlan
{
    ///<summary>Depth in the plan tree, so the table can indent an operator under its parent.</summary>
    public const string DepthColumn = "depth";

    ///<summary>The operator's name — NodeByLabelScan, Expand(All), Filter…</summary>
    public const string OperatorColumn = "operator";

    ///<summary>Rows the operator produced. PROFILE only — EXPLAIN has not run the query.</summary>
    public const string RowsColumn = "rows";

    ///<summary>Storage-engine accesses the operator made. PROFILE only.</summary>
    public const string DbHitsColumn = "dbHits";

    ///<summary>Time attributed to the operator, in milliseconds. PROFILE only, and not every operator reports it.</summary>
    public const string TimeColumn = "timeMs";

    ///<summary>The operator's arguments — the index or expression it worked on — as one readable string.</summary>
    public const string DetailsColumn = "details";

    ///<summary>True when a result's columns are a plan rather than ordinary query output.</summary>
    public static bool IsPlan(GraphDbTable table)
    {
        return table != null
            && table.Vars.Contains(OperatorColumn)
            && table.Vars.Contains(DepthColumn);
    }
}
