using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///An <see cref="IGraphDb"/> that runs the query on the host rather than in the browser: it posts the
///connection and the query to the execute endpoint, and the server dials the database with the very
///same <see cref="GremlinDB"/> / <see cref="SparqlDb"/> driver the browser would have used.
///
///This is what makes databases work that a page simply cannot reach — one that sends no CORS headers,
///a plain-http endpoint on an https page, a host only the server can route to, or a protocol like Bolt
///that needs a raw TCP socket. The viewer never notices the difference; it sees an IGraphDb either way.
///</summary>
public sealed class ServerProxyGraphDb : IGraphDb
{
    ///<summary>Relative on purpose — it resolves against the HttpClient's base address, which is the host.</summary>
    public const string ExecutePath = "api/graph/execute";

    ///<summary>
    ///Answered by any host that proxies database connections, and by nothing else. A build that ships
    ///without a host behind it cannot know at compile time whether one will be there — the same
    ///WebAssembly output is served both ways — so it asks at startup and takes 404 for an answer.
    ///GET, cheap, and no side effects, because it runs on every boot of every deployment. What separates a
    ///host from a static file server is the body, not the status -- see <see cref="GraphHostCapabilities"/>.
    ///</summary>
    public const string CapabilitiesPath = "api/graph/capabilities";

    private const int MaxErrorBodyLength = 500;

    private readonly HttpClient _http;
    private readonly GremlinDB.GremlinConnection _connection;

    public ServerProxyGraphDb(HttpClient httpClient, GremlinDB.GremlinConnection connection)
    {
        _http = httpClient;
        _connection = connection;
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GraphQueryRequest { Connection = _connection, Query = query };
            var response = await _http.PostAsJsonAsync(ExecutePath, request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                return GraphDbResult.Failure($"The server could not run the query ({(int)response.StatusCode}). {GraphWireText.Truncate(body)}".TrimEnd());
            }

            var payload = await response.Content.ReadFromJsonAsync<GraphQueryResponse>(cancellationToken);

            if (payload == null)
                return GraphDbResult.Failure("The server returned an empty response.");

            return payload.ToResult();
        }
        catch (OperationCanceledException)
        {
            //Always re-thrown, like the browser-direct drivers, so callers can tell a cancel from a failure.
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure($"Could not reach the server: {ex.Message}");
        }
    }


    public ValueTask DisposeAsync()
    {
        //Nothing is held open here: the host opens and closes the real connection around each request.
        return ValueTask.CompletedTask;
    }
}

///<summary>
///What a host answers <see cref="ServerProxyGraphDb.CapabilitiesPath"/> with. It exists as a type, and
///the client reads it as one, because a status code cannot answer the question: a static host that serves
///index.html for unknown paths — which is how a single-page app gets its deep links, and what the
///WebAssembly dev server does — replies 200 with the page itself. Only the shape of the body tells a host
///from a file server that says yes to everything.
///</summary>
public sealed class GraphHostCapabilities
{
    ///<summary>True from any host that proxies database connections. There is no other reason to answer.</summary>
    public bool ServerRoute { get; set; }
}
