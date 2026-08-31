using System.Collections.Concurrent;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Server.Api;

///<summary>
///Keeps one <see cref="IGraphDb"/> alive per distinct connection so proxied queries reuse an open socket
///instead of dialing afresh each time. The browser-direct path already reuses its connection; the proxy
///was creating and disposing a driver per request, so a bulk commit paid the WebSocket + Cosmos SASL
///handshake on every one of its (often hundreds of) queries. Reusing the driver collapses that to one
///handshake per connection — the driver reopens its own socket transparently if it is ever dropped.
///</summary>
public sealed class GraphConnectionPool : IAsyncDisposable
{
    private sealed class Pooled
    {
        public required IGraphDb Db { get; init; }
        public DateTime LastUsedUtc { get; set; }
    }

    ///<summary>Idle connections past this age are closed by the eviction sweep.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    //A separator no connection field would contain, so the key parts can't run together ambiguously.
    private const string KeySeparator = "\u0001";

    private readonly ConcurrentDictionary<string, Pooled> _pool = new();
    private readonly Func<GremlinDB.GremlinConnection, IGraphDb> _factory;

    //One long-lived HttpClient shared by every pooled driver (the drivers never dispose it). It carries the
    //User-Agent some endpoints require and is the recommended reuse pattern for the HTTP-transport path.
    private readonly HttpClient _http;

    public GraphConnectionPool() : this(null)
    {
    }

    ///<summary>The factory override exists for tests; production leaves it null and dials for real.</summary>
    public GraphConnectionPool(Func<GremlinDB.GremlinConnection, IGraphDb> factory)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(GraphApi.UserAgent);
        _factory = factory ?? DefaultFactory;
    }

    ///<summary>Runs the query on a driver reused across calls for the same connection.</summary>
    public async Task<GraphDbResult> ExecuteAsync(GremlinDB.GremlinConnection connection, string query, CancellationToken cancellationToken)
    {
        //The driver is created lazily and cheaply here — it does not open its socket until the first
        //ExecuteAsync — so the rare GetOrAdd factory race just discards an unused, socket-less driver.
        var pooled = _pool.GetOrAdd(KeyFor(connection), _ => new Pooled { Db = _factory(connection) });

        pooled.LastUsedUtc = DateTime.UtcNow;

        //GremlinDB serializes its own WebSocket exchanges, so concurrent reuse of one entry is already safe.
        return await pooled.Db.ExecuteAsync(query, cancellationToken);
    }

    ///<summary>Closes and drops connections idle longer than <see cref="IdleTimeout"/>.</summary>
    public async Task EvictIdleAsync()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;

        foreach (var kv in _pool)
        {
            if (kv.Value.LastUsedUtc >= cutoff)
                continue;

            if (_pool.TryRemove(kv.Key, out var removed))
                await SafeDisposeAsync(removed.Db);
        }
    }

    ///<summary>How many connections are currently pooled — for tests and diagnostics.</summary>
    public int Count
    {
        get { return _pool.Count; }
    }

    private IGraphDb DefaultFactory(GremlinDB.GremlinConnection connection)
    {
        //CreateServer hands back the host-side driver: the .NET Bolt driver the host registered for Neo4j
        //(its raw sockets can't ship to WebAssembly), or the shared HTTP driver for every other engine.
        return GraphDbProviders.For(connection.DatabaseType).CreateServer(_http, connection);
    }

    //The key is only ever an in-memory dictionary key — never logged — so carrying the auth key in it is fine.
    private static string KeyFor(GremlinDB.GremlinConnection c)
    {
        return string.Join(KeySeparator,
            c.DatabaseType, c.Transport, c.Hostname, c.Port, c.UseSSL,
            c.Database, c.Collection, c.Endpoint, c.Username, c.AuthKey);
    }

    private static async Task SafeDisposeAsync(IGraphDb db)
    {
        try
        {
            await db.DisposeAsync();
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _pool)
            await SafeDisposeAsync(kv.Value.Db);

        _pool.Clear();
        _http.Dispose();
    }
}

///<summary>Periodically closes idle pooled connections so open sockets do not accumulate.</summary>
public sealed class GraphConnectionPoolEvictionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private readonly GraphConnectionPool _pool;

    public GraphConnectionPoolEvictionService(GraphConnectionPool pool)
    {
        _pool = pool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await _pool.EvictIdleAsync();
        }
    }
}
