using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace GraphDBViewerWeb.Code;

///<summary>
///Connections and API keys filled in by hand on a development machine, so the app comes up already
///pointed at them instead of being retyped every time storage is cleared.
///
///The file is git-ignored and is expected to be absent everywhere else. It is fetched like any other
///static asset, which means a static host that serves index.html for unknown paths answers this with the
///page rather than a 404 — so the marker below is what tells a real file from that, exactly as the
///capabilities probe does. Anything unexpected means "no dev secrets", which is the safe answer.
///
///It seeds; it does not own. An entry whose name is already saved is left alone, so what you change in
///the app survives, and a new entry added to the file later is picked up without wiping the rest.
///</summary>
public sealed class DevSecrets
{
    ///<summary>Relative on purpose: it resolves against the HttpClient base address, like every other asset.</summary>
    public const string Path = "dev-secrets.json";

    ///<summary>
    ///Present and true only in a real file. A page served in its place has no such property, and a file
    ///that omits it is treated as not meant for this, rather than guessed at.
    ///</summary>
    [JsonPropertyName("devSecrets")]
    public bool IsDevSecrets { get; set; }

    ///<summary>Saved database connections, keyed by the name they appear under.</summary>
    public Dictionary<string, GremlinDB.GremlinConnection> Connections { get; set; }

    ///<summary>Saved AI models, keyed by name.</summary>
    public Dictionary<string, LlmConnection> LlmConnections { get; set; }

    ///<summary>Reads the file, or returns null when there is not one to read.</summary>
    public static async Task<DevSecrets> LoadAsync(HttpClient http, string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            //Asked for without caching, every time. This is the one file in the app whose whole purpose
            //is to be edited between reloads, and a service worker or an HTTP cache holding the copy from
            //before the key was filled in looks exactly like the seeding being broken.
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            var secrets = await response.Content.ReadFromJsonAsync<DevSecrets>(cts.Token);

            if (secrets == null || !secrets.IsDevSecrets)
                return null;

            return secrets;
        }
        catch
        {
            //Absent, or a page where the file would be, or unreadable. All the same answer.
            return null;
        }
    }
}
