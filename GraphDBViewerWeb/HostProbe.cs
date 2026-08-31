using System.Net.Http.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb;

///<summary>
///Asks at startup whether a host is there to proxy database connections, because the build cannot know:
///the same WebAssembly output is served from a static file host and from GraphDBViewerWeb.Server.
///
///The answer has to be read from the body, not the status. A single-page app needs its deep links, so
///static hosts serve index.html for any unknown path — the WebAssembly dev server, GitHub Pages with a
///404 fallback, Netlify redirects — and every one of them answers this probe 200, with the page. Only a
///body shaped like <see cref="GraphHostCapabilities"/> means a host.
///
///Every failure means "no host", which is the safe way to be wrong: browser-direct connections work
///everywhere, while assuming a proxy that is not there breaks every connection made.
///</summary>
public static class HostProbe
{
    ///<summary>
    ///Short on purpose. This runs on every boot of every deployment, and a host that hangs must not hold
    ///the app up when the answer only decides whether one button is offered.
    ///</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    ///<summary>True only when something answered as a host would.</summary>
    public static async Task<bool> HostAnswersAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var response = await http.GetAsync(ServerProxyGraphDb.CapabilitiesPath, cts.Token);

            if (!response.IsSuccessStatusCode)
                return false;

            var capabilities = await response.Content.ReadFromJsonAsync<GraphHostCapabilities>(cts.Token);

            return capabilities?.ServerRoute == true;
        }
        catch
        {
            //404, a page that is not JSON and throws on the read, a timeout, an offline start. All the
            //same answer.
            return false;
        }
    }
}
