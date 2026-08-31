using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///The browser-direct Bolt driver: an <see cref="IGraphDb"/> that runs Cypher against Neo4j / Memgraph
///straight from the page, over the vendored JavaScript driver (<c>neo4jInterop</c>, which speaks Bolt over
///WebSocket). It is the counterpart to the host-side <c>Neo4jServerDb</c>: the "Make requests from" toggle
///picks between them, since Bolt is the one engine whose two routes need different drivers rather than the
///same C# one — the browser has no raw socket, so it cannot run the .NET driver.
///
///The interop hands back the same driver-agnostic envelope the .NET driver builds, so
///<see cref="Neo4jConverter"/> does the record→graph mapping for both.
///</summary>
public sealed class Neo4jBrowserDb : IGraphDb
{
    private readonly IJSRuntime _js;
    private readonly GremlinDB.GremlinConnection _connection;

    //Identifies this connection's driver on the JS side, so a bulk commit reuses one open WebSocket rather
    //than dialing per query — the same reuse the pool gives the server route.
    private readonly string _handle = Guid.NewGuid().ToString("N");

    public Neo4jBrowserDb(IJSRuntime js, GremlinDB.GremlinConnection connection)
    {
        _js = js;
        _connection = connection;
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            //The interop creates the driver on first use for this handle from the config, then reuses it,
            //and catches its own driver / query errors into an { "error": ".." } envelope.
            string envelope = await _js.InvokeAsync<string>("neo4jInterop.run", cancellationToken, _handle, Config(), query ?? "");

            return Neo4jConverter.ToGraphDbResult(envelope);
        }
        catch (OperationCanceledException)
        {
            //Re-thrown like the other browser-direct drivers so callers can tell a cancel from a failure.
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure(ex.Message);
        }
    }

    private object Config()
    {
        //In the browser the driver rides WebSocket: bolt → ws, bolt+s → wss. Neo4j's default user is
        //"neo4j"; a blank database name leaves the driver on its own default.
        string scheme = _connection.UseSSL ? "bolt+s" : "bolt";

        return new
        {
            uri = $"{scheme}://{_connection.Hostname}:{_connection.Port}",
            username = string.IsNullOrEmpty(_connection.Username) ? "neo4j" : _connection.Username,
            password = _connection.AuthKey ?? "",
            database = _connection.Database ?? ""
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("neo4jInterop.close", _handle);
        }
        catch { }
    }
}
