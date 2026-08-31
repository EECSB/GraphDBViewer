using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads a GUN graph from the page, over the vendored bundle (<c>gunInterop</c>).
///
///GUN is the one backend here that <b>cannot run on the host</b>. Every other engine either speaks HTTP —
///so the same C# serves both routes — or has a .NET driver, as Bolt does. GUN is a JavaScript library with
///no .NET counterpart, so a GUN connection is browser-direct or nothing; the provider forces the choice
///rather than letting a proxied attempt fail obscurely.
///
///It also has no query language. The "query" is a path of keys — <c>alice</c>, <c>alice/knows</c> —
///optionally followed by <c>~depth</c>, which is as close to <c>gun.get('alice').get('knows')</c> as a
///text box gets.
///</summary>
public sealed class GunDb : IGraphDb, ILiveGraphDb
{
    ///<summary>What the editor shows when a GUN connection opens, since there is no query language to learn.</summary>
    public const string ExampleQuery = "alice ~2";

    private readonly IJSRuntime _js;
    private readonly GremlinDB.GremlinConnection _connection;

    //Identifies this connection's GUN instance on the JS side, so one peer is reused across queries.
    private readonly string _handle = Guid.NewGuid().ToString("N");

    //Handed to the interop so a subscription can call back in. Created on the first watch, since a
    //connection that never goes live never needs one.
    private DotNetObjectReference<GunDb> _self;

    public GunDb(IJSRuntime js, GremlinDB.GremlinConnection connection)
    {
        _js = js;
        _connection = connection;
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //Some questions GUN simply cannot answer — chiefly "what links to this node", there being no
        //reverse index. Those resolve to an empty graph here rather than troubling the peer with a read
        //that could only come back empty anyway.
        if ((query ?? "").Trim() == GunQuery.Nothing)
            return GunConverter.ToGraphDbResult("{}");

        //A staged edit is a statement rather than a key path. It is parsed here and applied as an
        //operation — the text is never evaluated, so nothing typed into the Generated tab runs as code.
        if (GunWrite.IsWrite(query))
            return await ApplyAsync(query, cancellationToken);

        try
        {
            string graph = await _js.InvokeAsync<string>("gunInterop.run", cancellationToken, _handle, Config(), query ?? "");

            return GunConverter.ToGraphDbResult(graph);
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

    ///<summary>
    ///Performs one staged write. The statement is parsed into an operation on this side and the operation
    ///is what crosses to JavaScript — so a line edited by hand in the Generated tab is checked, not run.
    ///</summary>
    private async Task<GraphDbResult> ApplyAsync(string statement, CancellationToken cancellationToken)
    {
        var write = GunWrite.Parse(statement);

        if (write == null)
            return GraphDbResult.Failure($"Not a GUN write this viewer can run: {statement}");

        try
        {
            var operation = new
            {
                kind = write.Kind.ToString().ToLowerInvariant(),
                soul = write.Soul,
                edge = write.Edge,
                target = write.Target,
                values = write.ValuesJson()
            };

            string error = await _js.InvokeAsync<string>("gunInterop.apply", cancellationToken, _handle, Config(), operation);

            if (!string.IsNullOrEmpty(error))
                return GraphDbResult.Failure(error);

            //A write answers with nothing to draw. GUN acknowledges locally and syncs to peers after.
            return GunConverter.ToGraphDbResult("{}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure(ex.Message);
        }
    }

    //── Live updates ────────────────────────────────────────────────────

    public event Action<GraphDbResult> GraphChanged;

    public bool IsWatching { get; private set; }

    ///<summary>
    ///Subscribes to everything the query reaches, with GUN's own <c>.on()</c>. What comes back is not a
    ///second answer to the query — it is whichever node a peer changed, pushed as it happens.
    ///</summary>
    public async Task WatchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //A query that reads nothing has nothing to watch either.
        if ((query ?? "").Trim() == GunQuery.Nothing)
            return;

        _self ??= DotNetObjectReference.Create(this);

        await _js.InvokeAsync<string>("gunInterop.watch", cancellationToken, _handle, Config(), query ?? "", _self);

        IsWatching = true;
    }

    public async Task StopWatchingAsync()
    {
        if (!IsWatching)
            return;

        IsWatching = false;

        try
        {
            await _js.InvokeVoidAsync("gunInterop.unwatch", _handle);
        }
        catch { }
    }

    ///<summary>Called from the subscription with the node a peer just changed.</summary>
    [JSInvokable]
    public void OnGunGraphChanged(string graphJson)
    {
        //A push that arrives after the viewer stopped watching is not an update to anything.
        if (!IsWatching)
            return;

        //No stand-in endpoints: a push carries the one node that changed, and its links point at nodes
        //already drawn. Standing in empty ones would blank the real ones when the update is merged.
        GraphChanged?.Invoke(GunConverter.ToGraphDbResult(graphJson, false));
    }

    private object Config()
    {
        return new { peers = PeerUrls(_connection) };
    }

    ///<summary>
    ///The relay peers to sync with. A GUN peer is a full URL ending in <c>/gun</c>, and several may be given
    ///comma-separated — GUN's own model being that a client talks to as many peers as it likes. No peer at
    ///all is legitimate: that is a purely in-browser graph.
    ///</summary>
    public static List<string> PeerUrls(GremlinDB.GremlinConnection connection)
    {
        var peers = new List<string>();

        //An explicit endpoint wins: it is the only field that can carry a full URL with its path.
        if (!string.IsNullOrWhiteSpace(connection.Endpoint))
        {
            foreach (var peer in connection.Endpoint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                peers.Add(peer);

            return peers;
        }

        if (string.IsNullOrWhiteSpace(connection.Hostname))
            return peers;

        string scheme;
        if (connection.UseSSL)
            scheme = "https";
        else
            scheme = "http";

        peers.Add($"{scheme}://{connection.Hostname}:{connection.Port}/gun");

        return peers;
    }

    public async ValueTask DisposeAsync()
    {
        IsWatching = false;

        try
        {
            //Closing takes the subscription with it, so there is nothing to unwatch separately.
            await _js.InvokeVoidAsync("gunInterop.close", _handle);
        }
        catch { }

        _self?.Dispose();
        _self = null;
    }
}
