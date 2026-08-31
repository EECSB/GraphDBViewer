using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///<see cref="IAppStorage"/> kept on the host rather than in the browser, so a signed-in user's workspace
///follows them to any machine instead of living in one browser profile.
///
///It is deliberately only the translation — HTTP verb in, string out — with no caching, no retry and no
///opinion about what to do when the server is not there. That belongs to
///<see cref="FallbackAppStorage"/>, which composes this with a local store; keeping the two apart is what
///makes each one testable and this one honest about failing.
///
///Failure is a **throw**, not a null. A read that answered "nothing stored" when it really meant "could
///not ask" would let the app quietly start empty and then save that emptiness over a workspace the user
///still has — so the distinction is preserved all the way up to whoever is prepared to handle it.
///</summary>
public class RemoteAppStorage : IAppStorage
{
    private readonly HttpClient _http;

    public RemoteAppStorage(HttpClient http)
    {
        _http = http;
    }

    ///<summary>
    ///Never raised here. A quota belongs to a browser; a server that will not take a write says so with a
    ///status code, and that surfaces as an exception. Declared because the seam requires it.
    ///</summary>
    public event Action StorageQuotaExceeded;

    public async Task<T> GetAsync<T>(string key)
    {
        var json = await GetStringAsync(key);

        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            //A value written by an older serializer that no longer deserializes — treat as absent rather
            //than crash, matching every other adapter. The app defaults anything missing.
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        await SetStringAsync(key, JsonSerializer.Serialize(value));
    }

    public async Task<string> GetStringAsync(string key)
    {
        var response = await _http.GetAsync(AppStorageContract.PathFor(key));

        //A key that was never written is not a failure — it is the answer, and 204 says so without the
        //browser logging it as one. 404 is still honored so an older host, or a proxy in front of one,
        //does not read as "stored, empty".
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return StorageCompression.Decode(await response.Content.ReadAsStringAsync());
    }

    public async Task SetStringAsync(string key, string value)
    {
        var packed = StorageCompression.Encode(value) ?? "";
        var body = new StringContent(packed, Encoding.UTF8, AppStorageContract.ContentType);

        var response = await _http.PutAsync(AppStorageContract.PathFor(key), body);

        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveAsync(string key)
    {
        var response = await _http.DeleteAsync(AppStorageContract.PathFor(key));

        //Deleting what is not there is the state the caller asked for, so it is not an error.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        response.EnsureSuccessStatusCode();
    }
}
