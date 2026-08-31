using System.Net;
using System.Text;
using GraphDBViewerWeb;

namespace GraphDBViewerWeb.Tests;

//The startup probe decides whether this build believes a host is behind it, and getting it wrong is
//silent and total: every connection would be aimed at a proxy that is not there. The case that matters
//most is the second one — it is how the probe was wrong before these existed.
public class HostProbeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _reply;

        public StubHandler(Func<HttpResponseMessage> reply)
        {
            _reply = reply;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_reply());
        }
    }

    private static HttpClient ClientAnswering(Func<HttpResponseMessage> reply)
    {
        return new HttpClient(new StubHandler(reply)) { BaseAddress = new Uri("https://example.test/") };
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task AHostAnsweringWithItsCapabilities_IsAHost()
    {
        var found = await HostProbe.HostAnswersAsync(ClientAnswering(() => Json("{\"serverRoute\":true}")));

        Assert.True(found);
    }

    //A single-page app needs deep links, so static hosts serve index.html for unknown paths and answer
    //this probe 200 — with the page. Reading the status alone made the static build think it had a proxy.
    [Fact]
    public async Task AStaticHostServingItsIndexPage_IsNotAHost()
    {
        var page = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>the app</body></html>", Encoding.UTF8, "text/html")
        };

        var found = await HostProbe.HostAnswersAsync(ClientAnswering(() => page));

        Assert.False(found);
    }

    [Fact]
    public async Task ANotFound_IsNotAHost()
    {
        var found = await HostProbe.HostAnswersAsync(ClientAnswering(() => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.False(found);
    }

    //A host that has the endpoint but says no is answering honestly, and the answer is no.
    [Fact]
    public async Task AHostSayingItDoesNotProxy_IsNotAHost()
    {
        var found = await HostProbe.HostAnswersAsync(ClientAnswering(() => Json("{\"serverRoute\":false}")));

        Assert.False(found);
    }

    //Nothing there at all — the offline start. It must answer, not propagate, or the app never boots.
    [Fact]
    public async Task AnUnreachableHost_IsNotAHost()
    {
        var found = await HostProbe.HostAnswersAsync(ClientAnswering(() => throw new HttpRequestException("no route to host")));

        Assert.False(found);
    }
}
