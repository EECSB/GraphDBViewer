using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;

namespace GraphDBViewerWeb.Tests;

//Markup cover for the "make requests from" row: who opens the connection, and — for host/port databases —
//the transport and SSL it uses. Its buttons write the "HTTP"/"WebSocket" literals that GremlinDB reads
//back when it builds the connection URI, and it mutates the passed connection object directly (by
//design — the caller reads the values back off the same reference).
public class TransportSslSelectorTests : BunitContext
{
    //The selector asks the host whether a server route exists at all. These render as the hosted
    //deployment, where both routes are real.
    public TransportSslSelectorTests()
    {
        Services.AddSingleton(new ViewerHostOptions { AppName = "Treeality", HasServerRoute = true });
    }

    private const string BrowserHint = "must return CORS headers";
    private const string ServerHint = "The host opens the connection";

    private IRenderedComponent<TransportSslSelector> RenderFor(GremlinDB.GremlinConnection conn, bool showTransport = true, bool showSsl = true, bool bolt = false)
    {
        return Render<TransportSslSelector>(p => p
            .Add(c => c.Connection, conn)
            .Add(c => c.ShowTransport, showTransport)
            .Add(c => c.ShowSsl, showSsl)
            .Add(c => c.Bolt, bolt));
    }

    [Fact]
    public void ClickingHttp_WritesTheTransportTheClientReadsBack()
    {
        var conn = new GremlinDB.GremlinConnection { Transport = "WebSocket", UseSSL = true };
        var cut = RenderFor(conn);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "HTTP").Click();

        Assert.Equal("HTTP", conn.Transport);
    }

    [Theory]
    [InlineData("WebSocket", true, "wss")]
    [InlineData("WebSocket", false, "ws")]
    [InlineData("HTTP", true, "https")]
    [InlineData("HTTP", false, "http")]
    public void AddressPreview_ShowsTheSchemeTheConnectionWillUse(string transport, bool ssl, string expected)
    {
        var conn = new GremlinDB.GremlinConnection { Transport = transport, UseSSL = ssl, Hostname = "db.example.com", Port = 8182 };
        var cut = RenderFor(conn);

        //The preview is what replaced the standing "toggle SSL off or it will fail" warning: it states
        //the address that will actually be dialed instead of warning about one of the ways to get it wrong.
        Assert.Equal($"{expected}://db.example.com:8182", cut.Find("code").TextContent.Trim());
    }

    [Fact]
    public void AddressPreview_WithoutAHost_StaysReadable()
    {
        var conn = new GremlinDB.GremlinConnection { Transport = "WebSocket", UseSSL = false, Port = 8182 };

        Assert.Equal("ws://…:8182", RenderFor(conn).Find("code").TextContent.Trim());
    }

    [Theory]
    [InlineData("Server", true)]
    [InlineData("Browser", false)]
    public void ServerBrowserToggle_WritesTheChoice(string label, bool expected)
    {
        //Starts at the opposite of what the click should produce, so a no-op button would fail this.
        var conn = new GremlinDB.GremlinConnection { ViaServer = !expected };
        var cut = RenderFor(conn);

        cut.FindAll("button").First(b => b.TextContent.Trim() == label).Click();

        Assert.Equal(expected, conn.ViaServer);
    }

    [Fact]
    public void Hint_ViaServer_SaysNothingAtAll()
    {
        var markup = RenderFor(new GremlinDB.GremlinConnection { ViaServer = true }).Markup;

        //CORS constrains the browser, so that caveat is wrong once the host is the one dialing — and the
        //note that used to replace it described the server edition's own default, which is not news. So
        //the row stands alone, and the space under it belongs to the buttons that follow.
        Assert.DoesNotContain(BrowserHint, markup);
        Assert.DoesNotContain(ServerHint, markup);
        Assert.DoesNotContain("alert-warning", markup);
    }

    [Fact]
    public void Hint_BrowserDirect_WarnsAboutCors()
    {
        var markup = RenderFor(new GremlinDB.GremlinConnection { ViaServer = false }).Markup;

        Assert.Contains(BrowserHint, markup);
        Assert.DoesNotContain(ServerHint, markup);
    }

    [Fact]
    public void EndpointDatabase_HidesTransportAndAddressButKeepsTheRouteChoice()
    {
        //The endpoint-URL databases (SPARQL) carry scheme and port in the URL itself, so both the transport
        //buttons and the SSL / address preview are off — but who opens the connection still matters.
        var cut = RenderFor(new GremlinDB.GremlinConnection { ViaServer = true }, showTransport: false, showSsl: false);

        Assert.Empty(cut.FindAll("code"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "WebSocket");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Server");
    }

    [Theory]
    [InlineData(false, "bolt")]
    [InlineData(true, "bolt+s")]
    public void BoltDatabase_PreviewsABoltUrlAndKeepsSslButHidesTransportButtons(bool ssl, string scheme)
    {
        //Neo4j / Memgraph are always Bolt in the browser, so the HTTP/WebSocket choice is meaningless — but
        //SSL still picks bolt vs bolt+s, and the preview shows the Bolt URL rather than a ws/http one.
        var conn = new GremlinDB.GremlinConnection { UseSSL = ssl, Hostname = "localhost", Port = 7687 };
        var cut = RenderFor(conn, showTransport: false, showSsl: true, bolt: true);

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "WebSocket");
        Assert.Equal($"{scheme}://localhost:7687", cut.Find("code").TextContent.Trim());
    }
}

//The same component on a build with no host behind it. The Server choice stays on screen there — hiding
//it would hide the only reason to want the other edition — and the button explains itself rather than
//switching to a route that does not exist. Options are registered per test, since what is being tested
//is precisely how the component reads them.
public class TransportSslSelectorWithoutAHostTests : BunitContext
{
    private IRenderedComponent<TransportSslSelector> RenderWith(ViewerHostOptions options, GremlinDB.GremlinConnection conn)
    {
        Services.AddSingleton(options);

        return Render<TransportSslSelector>(p => p.Add(c => c.Connection, conn));
    }

    private static ViewerHostOptions WebOnly()
    {
        return new ViewerHostOptions
        {
            HasServerRoute = false,
            ServerEditionUrl = "https://example.test/server-edition"
        };
    }

    [Fact]
    public void TheRouteChoice_IsStillOffered()
    {
        var cut = RenderWith(WebOnly(), new GremlinDB.GremlinConnection());

        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Server");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Browser");
    }

    [Fact]
    public void PressingServer_ExplainsAndLinks_WithoutTouchingTheConnection()
    {
        var conn = new GremlinDB.GremlinConnection { ViaServer = false };
        var cut = RenderWith(WebOnly(), conn);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").Click();

        //The route is the point: a build with no host must not end up marked as routing through one.
        Assert.False(conn.ViaServer);
        Assert.Contains("web-only edition", cut.Markup);

        //It still looks pressed, though. A button that explains itself and then springs back reads as
        //broken rather than as an explanation.
        Assert.Contains("btn-primary", cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").ClassName);

        //And the browser-route warning stands aside: "your browser opens the connection" underneath
        //"there is no host to route through" is two answers to one question.
        Assert.DoesNotContain("Your browser opens the connection", cut.Markup);
        Assert.Equal("https://example.test/server-edition", cut.Find("a[target=\"_blank\"]").GetAttribute("href"));
    }

    //Nothing to switch to and nowhere to point at: the pair would be two dead buttons, so it is not drawn.
    [Fact]
    public void WithNowhereToPointAt_TheChoiceIsNotOffered()
    {
        var cut = RenderWith(new ViewerHostOptions { HasServerRoute = false }, new GremlinDB.GremlinConnection());

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Server");
    }

    //A connection defaults to the server route, which on a build with no server is a lie the card used to
    //keep until connect time: it opened showing Server selected, and the explanation behind that button
    //only appeared once you had pressed Browser and come back to it.
    [Fact]
    public void AConnectionArrivingOnTheServerRoute_IsPutOnTheBrowserOne()
    {
        var conn = new GremlinDB.GremlinConnection { ViaServer = true };
        var cut = RenderWith(WebOnly(), conn);

        Assert.False(conn.ViaServer);
        Assert.Contains("btn-primary", cut.FindAll("button").First(b => b.TextContent.Trim() == "Browser").ClassName);
        Assert.DoesNotContain("btn-primary", cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").ClassName);
    }

    //Choosing Browser again puts the pair back and closes the explanation.
    [Fact]
    public void ChoosingBrowserAfterwards_PutsTheChoiceBack()
    {
        var cut = RenderWith(WebOnly(), new GremlinDB.GremlinConnection());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Browser").Click();

        Assert.DoesNotContain("web-only edition", cut.Markup);
        Assert.Contains("btn-primary", cut.FindAll("button").First(b => b.TextContent.Trim() == "Browser").ClassName);
        Assert.DoesNotContain("btn-primary", cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").ClassName);
    }

    //The Connect button is on the page, not in this row, so the page has to be told when the chosen
    //route is one this build does not have. Both directions matter: picking Server stops it offering to
    //connect, and picking Browser has to give that back.
    [Fact]
    public void PressingServerThenBrowser_ReportsTheRouteEachWay()
    {
        var reported = new List<bool>();

        Services.AddSingleton(WebOnly());

        var cut = Render<TransportSslSelector>(p => p
            .Add(c => c.Connection, new GremlinDB.GremlinConnection())
            .Add(c => c.OnRouteUnavailable, EventCallback.Factory.Create<bool>(this, v => reported.Add(v))));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Server").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Browser").Click();

        Assert.Equal(new[] { true, false }, reported);
    }
}
