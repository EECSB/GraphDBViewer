using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///What one source answered: the index, or why it did not, and how old the answer is.
///
///<strong>A source that does not answer never hides the others.</strong> One registry being down is a
///line on the page, not an empty store — and a stale cache is shown WITH ITS AGE rather than replaced
///by nothing, because a card an hour old is more use than a blank panel.
///</summary>
public sealed class StoreSourceState
{
    public string Source { get; set; }
    public StoreIndex Index { get; set; }
    public string Error { get; set; }

    ///<summary>When this text was fetched. What "cached, 40 minutes old" is said from.</summary>
    public DateTimeOffset? FetchedUtc { get; set; }

    public bool FromCache { get; set; }

    public bool Ok
    {
        get { return Index != null; }
    }

    ///<summary>How old, in the words the source bar shows.</summary>
    public string Age(DateTimeOffset now)
    {
        if (FetchedUtc == null)
            return "";

        var old = now - FetchedUtc.Value;

        if (old < TimeSpan.FromMinutes(1))
            return "just now";

        if (old < TimeSpan.FromHours(1))
            return $"{(int)old.TotalMinutes} minute(s) ago";

        return $"{(int)old.TotalHours} hour(s) ago";
    }
}

///<summary>The index text and when it was fetched, as the browser keeps it.</summary>
public sealed class StoreCacheEntry
{
    [JsonPropertyName("json")]
    public string Json { get; set; }

    [JsonPropertyName("fetchedUtc")]
    public DateTimeOffset FetchedUtc { get; set; }
}

///<summary>
///Fetching the store's indexes, with the browser-side cache the design asks for (open question 2: one
///hour, and the refresh control fetches with no-cache).
///
///<strong>Nothing per plugin is fetched here</strong> (criterion 9). The cards are drawn from the index
///alone; icons load as ordinary <c>&lt;img&gt;</c> elements and a README is fetched only when somebody
///opens an entry. An instance that opens the Store tab makes one request per source and no more — which
///is also why the registry is a static index rather than the GitHub API, whose sixty unauthenticated
///calls an hour would break the page on its first busy afternoon.
///
///The cache is read before fetching and replaced after, so a source that goes down between two visits
///still shows what it last said. <see cref="IAppStorage"/> is what holds it, which in this build is the
///host's per-user store — so the cache follows the person rather than the browser profile, exactly as
///the search mode does.
///</summary>
public sealed class PluginStoreClient
{
    ///<summary>How long a cached index is good for (open question 2, closed 2026-09-06).</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly IAppStorage _storage;
    private readonly Func<DateTimeOffset> _now;

    public PluginStoreClient(HttpClient http, IAppStorage storage = null, Func<DateTimeOffset> now = null)
    {
        _http = http;
        _storage = storage;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    ///<summary>Where one source's cached index lives.</summary>
    public static string CacheKeyFor(string source)
    {
        return "plugins:store:" + (source ?? "");
    }

    ///<summary>
    ///Every source, read. <paramref name="refresh"/> skips the cache and asks with <c>no-cache</c>, the
    ///same header the installer already sends for a manifest — so a refresh really refreshes rather than
    ///re-reading whatever a proxy kept.
    ///</summary>
    public async Task<IReadOnlyList<StoreSourceState>> ReadAsync(IEnumerable<string> sources, bool refresh)
    {
        var states = new List<StoreSourceState>();

        foreach (var source in sources ?? [])
        {
            if (!string.IsNullOrWhiteSpace(source))
                states.Add(await OneAsync(source, refresh));
        }

        return states;
    }

    private async Task<StoreSourceState> OneAsync(string source, bool refresh)
    {
        var state = new StoreSourceState { Source = source };
        var cached = await CachedAsync(source);

        if (!refresh && cached != null && _now() - cached.FetchedUtc < CacheFor)
        {
            Read(state, cached.Json);

            state.FetchedUtc = cached.FetchedUtc;
            state.FromCache = true;

            if (state.Ok)
                return state;

            //A cached index that no longer parses is worth nothing; ask the source instead of showing
            //an error about a file nobody can see.
            state.Error = null;
        }

        string json;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);

            if (refresh)
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return Stale(state, cached, $"The source answered {(int)response.StatusCode}.");

            json = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return Stale(state, cached, ex.Message);
        }

        Read(state, json);

        if (!state.Ok)
            return Stale(state, cached, state.Error);

        state.FetchedUtc = _now();
        state.FromCache = false;

        await KeepAsync(source, json, state.FetchedUtc.Value);

        return state;
    }

    ///<summary>
    ///A source that would not answer, shown with whatever it last said. The error stands whether or not
    ///there is a cache — the point is that the page never claims a registry is empty when it is only
    ///unreachable.
    ///</summary>
    private static StoreSourceState Stale(StoreSourceState state, StoreCacheEntry cached, string why)
    {
        state.Error = why;
        state.Index = null;

        if (cached == null)
            return state;

        Read(state, cached.Json);

        state.FetchedUtc = cached.FetchedUtc;
        state.FromCache = true;
        state.Error = why;

        return state;
    }

    private static void Read(StoreSourceState state, string json)
    {
        try
        {
            state.Index = PluginStore.Parse(json);
            state.Error = null;
        }
        catch (StoreIndexException ex)
        {
            state.Index = null;
            state.Error = ex.Message;
        }
    }

    private async Task<StoreCacheEntry> CachedAsync(string source)
    {
        if (_storage == null)
            return null;

        try
        {
            var json = await _storage.GetStringAsync(CacheKeyFor(source));

            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<StoreCacheEntry>(json);
        }
        catch
        {
            //A cache that cannot be read is a cache that is not there. Never a reason to fail a fetch.
            return null;
        }
    }

    private async Task KeepAsync(string source, string json, DateTimeOffset at)
    {
        if (_storage == null)
            return;

        try
        {
            await _storage.SetStringAsync(CacheKeyFor(source), JsonSerializer.Serialize(new StoreCacheEntry { Json = json, FetchedUtc = at }));
        }
        catch
        {
            //Storage full, or a browser that keeps none. The store works without a cache; it just asks
            //again next time.
        }
    }
}
