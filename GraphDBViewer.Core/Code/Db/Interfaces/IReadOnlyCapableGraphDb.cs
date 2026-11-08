using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///An engine that can hold a query to reading itself, rather than only through a guard that reads the query
///first.
///
///A guard judges query text, and text can do more than a guard was taught to read: a procedure it has never
///heard of, a write where it does not look. A path the engine keeps for reads cannot write whatever the query
///says, so Ask AI's read tool sends its queries down one wherever there is one, behind the guard rather than
///instead of it (<see cref="QueryToolRunner"/>). Neo4j and Memgraph take them in a READ session, Dgraph at
///<c>/query</c>, which refuses a mutation, and the host route carries the request to whichever of those the
///host dials. ArangoDB has no such path short of a stream transaction of its own, so its reads rest on the
///guard alone.
///</summary>
public interface IReadOnlyCapableGraphDb
{
    ///<summary>Runs the query on the engine's read-only path, where a query that writes fails rather than writing.</summary>
    Task<GraphDbResult> ExecuteReadOnlyAsync(string query, CancellationToken cancellationToken = default);
}
