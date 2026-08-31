using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///How an engine answers "why is this query slow, and what will it actually do" — the Profile and Explain
///tabs of the query debugger, and whether it can be stepped through at all.
///
///The two halves of the debugger turn out to be very different things. Profile and Explain are a
///capability nearly every engine has natively, so they port. <b>Stepping does not</b>: it works by running
///the query truncated after each step, which only means something for a language whose prefixes are
///themselves valid queries. Gremlin's are; a Cypher clause prefix is not. Hence
///<see cref="SupportsStepping"/> — an engine can offer the debugger without offering that tab.
///</summary>
public interface IGraphQueryDebugger
{
    ///<summary>
    ///Whether the query can be run truncated after each step, which is what the Steps tab does. False for
    ///an engine whose partial queries are not valid queries.
    ///</summary>
    bool SupportsStepping { get; }

    ///<summary>The query that measures the given one — Gremlin's profile(), Cypher's PROFILE.</summary>
    string ProfileQuery(string query);

    ///<summary>The query that plans the given one without running it — Gremlin's explain(), Cypher's EXPLAIN.</summary>
    string ExplainQuery(string query);

    ///<summary>
    ///Reads a profile answer into the table the panel shows. Returns no rows when the shape wasn't
    ///recognized, which the caller falls back on by showing the raw response.
    ///</summary>
    (double TotalMs, List<MetricsRow> Rows) ParseProfile(GraphDbResult result);

    ///<summary>Reads an explain answer into the text the panel shows.</summary>
    string ParseExplain(GraphDbResult result);

    ///<summary>Heading for the column naming each row's operation — "Step" in Gremlin, "Operator" in Cypher.</summary>
    string OperationHeader { get; }

    ///<summary>Heading for the count of things produced — Gremlin counts elements, Cypher counts rows.</summary>
    string ElementHeader { get; }

    ///<summary>Heading for the engine's own work measure — Gremlin's traversers, Cypher's database hits.</summary>
    string EffortHeader { get; }
}
