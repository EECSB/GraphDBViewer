using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;

namespace GraphDBViewerWeb.Code;

///<summary>
///The instance graph, as a plugin reaches it (instance-graph task 4): part of the host API, and
///deliberately distinct from anything the viewer is browsing. Queries run on the server against the
///designated connection — the browser never holds that connection, which keeps its credentials out
///of every client and makes a container addressed by name reachable at all. Raw Gremlin by design
///(ADR 0009): the convenience layer waits until real plugins have shaped it.
///
///<see cref="InstanceGraphDb"/> wraps this as an <see cref="IGraphDb"/>, which is what puts the
///instance graph on the canvas.
///</summary>
public sealed class InstanceGraph
{
    private readonly HttpClient _http;

    public InstanceGraph(HttpClient http)
    {
        _http = http;
    }

    ///<summary>Whether a graph is configured, and roughly what it is. Never throws — an unreachable
    ///host answers "not configured", which is also what a plugin should render for it.</summary>
    public async Task<InstanceGraphStatus> StatusAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<InstanceGraphStatus>(InstanceGraphContract.StatusPath);

            if (status != null)
                return status;
        }
        catch
        {
        }

        return new InstanceGraphStatus { Configured = false };
    }

    ///<summary>
    ///Runs one Gremlin query against the instance graph. Failures come back as the result's own error
    ///shape — including "no instance graph is configured", which a plugin behind
    ///<c>NeedsInstanceGraph</c> should never see.
    ///</summary>
    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(InstanceGraphContract.ExecutePath, new InstanceGraphQuery { Query = query }, cancellationToken);

            //The one refusal worth a sentence of its own: reading the instance graph is a permission
            //since 2026-09-08, and an account without it should be told which, not shown a number.
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return GraphDbResult.Failure("Your account may not query the instance graph (403): that takes the instance.query permission, which an administrator grants through a role.");

            if (!response.IsSuccessStatusCode)
                return GraphDbResult.Failure($"The instance graph endpoint answered {(int)response.StatusCode}.");

            var payload = await response.Content.ReadFromJsonAsync<GraphQueryResponse>(cancellationToken);

            if (payload == null)
                return GraphDbResult.Failure("The instance graph endpoint answered with nothing.");

            return payload.ToResult();
        }
        catch (OperationCanceledException)
        {
            //Re-thrown like every other driver, so a caller can tell a cancel from a failure.
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure($"The instance graph could not be reached: {ex.Message}");
        }
    }

}

///<summary>
///The instance graph as the <em>viewer</em> sees it: an <see cref="IGraphDb"/>, so it can be the
///connection the canvas is browsing. The viewer asks an IGraphDb to run a query and knows nothing else
///about it, which is why the whole feature needs no change to the page's reading, drawing or
///traversal — the same drop-in trick <see cref="ServerProxyGraphDb"/> already plays. What differs is
///that the caller never names a connection, and that the endpoint refuses anything but a read.
///
///A class of its own rather than an interface on <see cref="InstanceGraph"/>, which is a registered
///scoped service: making that one <see cref="IAsyncDisposable"/> changes how every container holding
///it has to be disposed, and it gains nothing — nothing is held open here either way.
///</summary>
public sealed class InstanceGraphDb : IGraphDb
{
    private readonly InstanceGraph _graph;

    public InstanceGraphDb(HttpClient http)
    {
        _graph = new InstanceGraph(http);
    }

    public Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        return _graph.ExecuteAsync(query, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        //Nothing is held open: the host opens and closes the real connection around each request.
        return ValueTask.CompletedTask;
    }
}
