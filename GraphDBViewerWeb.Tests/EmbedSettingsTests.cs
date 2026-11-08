using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class EmbedSettingsTests
{
    [Fact]
    public void Parse_Empty_HasNothing()
    {
        var s = EmbedSettings.Parse("");

        Assert.False(s.HasAny);
        Assert.False(s.HasConnection);
    }

    [Fact]
    public void Parse_IgnoresLeadingQuestionMark()
    {
        var s = EmbedSettings.Parse("?view=3d");

        Assert.Equal(3, s.View);
        Assert.True(s.HasAny);
    }

    //The documented link names the flag with no value: /embed?instance&q=…. Until 2026-10-31 that read as nothing.
    [Fact]
    public void AFlagNamedWithNoValue_IsOn()
    {
        var s = EmbedSettings.Parse("?instance&q=g.V().limit(1)&run");

        Assert.True(s.InstanceGraph);
        Assert.True(s.AutoRun);
        Assert.Equal("g.V().limit(1)", s.Query);
        Assert.False(EmbedSettings.Parse("?instance=false").InstanceGraph);
        Assert.Null(EmbedSettings.Parse("?instance=maybe").InstanceGraph);
    }

    [Fact]
    public void Parse_TheInstancesOwnGraph_IsNamedRatherThanDescribed()
    {
        //How a plugin's page opens the viewer on what it found: recall's "Draw it" (Customers' phase 4).
        var s = EmbedSettings.Parse("?instance=true&view=2d&query=g.V().limit(1)");

        Assert.True(s.InstanceGraph);
        Assert.False(s.HasConnection);
        Assert.True(s.HasAny);
        Assert.Equal("g.V().limit(1)", s.Query);
        Assert.Null(EmbedSettings.Parse("?view=2d").InstanceGraph);
    }

    [Fact]
    public void Parse_GremlinConnectionFields()
    {
        var s = EmbedSettings.Parse("?dbType=tinkerpop&transport=ws&host=192.168.1.5&port=8182&ssl=false");

        Assert.Equal("ApacheTinkerPop", s.DatabaseType);
        Assert.Equal("WebSocket", s.Transport);
        Assert.Equal("192.168.1.5", s.Hostname);
        Assert.Equal(8182, s.Port);
        Assert.False(s.UseSsl);
        Assert.True(s.HasConnection);
    }

    [Theory]
    [InlineData("json", 1)]
    [InlineData("2d", 2)]
    [InlineData("graph", 2)]
    [InlineData("3d", 3)]
    [InlineData("table", 4)]
    [InlineData("4", 4)]
    [InlineData("TABLE", 4)]
    public void ParseView_MapsFriendlyNames(string input, int expected)
    {
        Assert.Equal(expected, EmbedSettings.ParseView(input));
    }

    [Fact]
    public void ParseView_UnknownIsNull()
    {
        Assert.Null(EmbedSettings.ParseView("hexagon"));
    }

    [Theory]
    [InlineData("cosmos", "CosmosDb")]
    [InlineData("cosmosdb", "CosmosDb")]
    [InlineData("sparql", "Sparql")]
    [InlineData("rdf", "Sparql")]
    [InlineData("gremlin", "ApacheTinkerPop")]
    [InlineData("apachetinkerpop", "ApacheTinkerPop")]
    [InlineData("neo4j", "Neo4j")]
    [InlineData("memgraph", "Neo4j")]
    [InlineData("cypher", "Neo4j")]
    public void NormalizeDbType_MapsAliases(string input, string expected)
    {
        Assert.Equal(expected, EmbedSettings.NormalizeDbType(input));
    }

    [Fact]
    public void Parse_DecodesEncodedQueryAndPlusAsSpace()
    {
        //query = g.V().has('name','A B')
        var s = EmbedSettings.Parse("?q=g.V().has(%27name%27%2C%27A+B%27)");

        Assert.Equal("g.V().has('name','A B')", s.Query);
    }

    [Fact]
    public void Parse_RunAndConnectFlags()
    {
        var s = EmbedSettings.Parse("?run=false&connect=0");

        Assert.False(s.AutoRun);
        Assert.False(s.Connect);
    }

    [Fact]
    public void Parse_KeysAreCaseInsensitive()
    {
        var s = EmbedSettings.Parse("?HOST=db.example.com&View=Table");

        Assert.Equal("db.example.com", s.Hostname);
        Assert.Equal(4, s.View);
    }

    [Fact]
    public void BuildConnection_AppliesGremlinDefaults()
    {
        var s = EmbedSettings.Parse("?host=db.example.com");
        var conn = s.BuildConnection();

        Assert.Equal("ApacheTinkerPop", conn.DatabaseType);
        Assert.Equal("WebSocket", conn.Transport);
        Assert.Equal("db.example.com", conn.Hostname);
        Assert.Equal(443, conn.Port);
        Assert.True(conn.UseSSL);
    }

    //An address that says nothing about SSL gets it, on whatever port it names (the user's rule of 2026-10-03:
    //what the product defaults to is the secure option). It used to be read off the port, so 8182 meant plain.
    [Fact]
    public void BuildConnection_APortSaysNothingAboutSsl_SoSslStaysOn()
    {
        var s = EmbedSettings.Parse("?host=192.168.1.5&port=8182");
        var conn = s.BuildConnection();

        Assert.Equal(8182, conn.Port);
        Assert.True(conn.UseSSL);
    }

    //Plain is still anybody's choice, and an address that asks for it gets it.
    [Fact]
    public void BuildConnection_SslFalse_IsKept()
    {
        var conn = EmbedSettings.Parse("?host=192.168.1.5&port=8182&ssl=false").BuildConnection();

        Assert.Equal(8182, conn.Port);
        Assert.False(conn.UseSSL);
    }

    [Fact]
    public void BuildConnection_EndpointOnlyIsTreatedAsSparql()
    {
        var s = EmbedSettings.Parse("?endpoint=https://query.wikidata.org/sparql");
        var conn = s.BuildConnection();

        Assert.Equal("Sparql", conn.DatabaseType);
        Assert.Equal("https://query.wikidata.org/sparql", conn.Endpoint);
        Assert.True(s.HasConnection);
    }

    [Theory]
    [InlineData("?host=db.example.com&viaServer=false", false)]
    [InlineData("?host=db.example.com&viaServer=true", true)]
    [InlineData("?host=db.example.com&proxy=off", false)]
    [InlineData("?host=db.example.com&PROXY=0", false)]
    public void BuildConnection_ViaServerFollowsTheUrl(string query, bool expected)
    {
        var conn = EmbedSettings.Parse(query).BuildConnection();

        Assert.Equal(expected, conn.ViaServer);
    }

    [Fact]
    public void BuildConnection_ViaServerOmitted_KeepsTheAppDefault()
    {
        var s = EmbedSettings.Parse("?host=db.example.com");

        //Nothing said, nothing pinned: the embed tracks whatever the app defaults to rather than
        //freezing the value that happened to be current when the embed URL was written.
        Assert.Null(s.ViaServer);
        Assert.Equal(new GremlinDB.GremlinConnection().ViaServer, s.BuildConnection().ViaServer);
    }

    ///<summary>The embedded viewer's theme, for the page around it (2026-10-21): ShellState's three, by their names, and nothing else.</summary>
    [Fact]
    public void Parse_TheEmbeddedViewersTheme_IsOneOfTheShells()
    {
        Assert.Equal(ShellState.ThemeLight, EmbedSettings.Parse("?theme=light").Theme);
        Assert.Equal(ShellState.ThemeDark, EmbedSettings.Parse("?theme=DARK").Theme);
        Assert.Equal(ShellState.ThemeLightTinted, EmbedSettings.Parse("?theme=tinted").Theme);
        Assert.Equal(ShellState.ThemeLightTinted, EmbedSettings.Parse("?theme=light-tinted").Theme);
        Assert.Equal(ShellState.ThemeDarkBlue, EmbedSettings.Parse("?theme=dark-blue").Theme);
        Assert.Equal(ShellState.ThemeLightBlue, EmbedSettings.Parse("?theme=light-blue").Theme);
        Assert.Equal(ShellState.ThemeLightBlueTinted, EmbedSettings.Parse("?theme=blue-tinted").Theme);
        Assert.Null(EmbedSettings.Parse("?theme=solarized").Theme);
        Assert.Null(EmbedSettings.Parse("?view=2d").Theme);

        //A look is not a setting the viewer acts on, so a theme alone leaves it as it was.
        Assert.False(EmbedSettings.Parse("?theme=light").HasAny);
    }

    ///<summary>
    ///The embedding page's origin, which turns the embedded viewer's messages on (2026-10-23): written the way a browser
    ///writes a message's origin, so the two compare exactly.
    ///</summary>
    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("http://127.0.0.1:5998", "http://127.0.0.1:5998")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("HTTPS://Example.COM", "https://example.com")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("http://example.com:80", "http://example.com")]
    [InlineData("http://example.com:443", "http://example.com:443")]
    [InlineData("https://shop.example.com:8443", "https://shop.example.com:8443")]
    [InlineData("http://[::1]:5998", "http://[::1]:5998")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("  https://example.com  ", "https://example.com")]
    public void NormalizeOrigin_AnAbsoluteHttpOrigin_IsWrittenAsABrowserWritesIt(string given, string expected)
    {
        Assert.Equal(expected, EmbedSettings.NormalizeOrigin(given));
    }

    ///<summary>Anything that is not an absolute http(s) origin leaves the messages off, rather than being guessed at.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("example.com")]
    [InlineData("//example.com")]
    [InlineData("/embed")]
    [InlineData("https://")]
    [InlineData("https:///example.com")]
    [InlineData("https://example.com/page")]
    [InlineData("https://example.com/..")]
    [InlineData("https://example.com//")]
    [InlineData("https://example.com?x=1")]
    [InlineData("https://example.com#top")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com\\evil.example")]
    [InlineData("https://ex%61mple.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://example.com:99999")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/page.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ws://example.com")]
    public void NormalizeOrigin_AnythingElse_IsNone(string given)
    {
        Assert.Null(EmbedSettings.NormalizeOrigin(given));
    }

    [Fact]
    public void Parse_TheEmbeddingPagesOrigin_IsReadEncodedOrNot_AndActsOnNothingElse()
    {
        Assert.Equal("http://127.0.0.1:5998", EmbedSettings.Parse("?instance=true&origin=http%3A%2F%2F127.0.0.1%3A5998").Origin);
        Assert.Equal("https://example.com", EmbedSettings.Parse("?ORIGIN=https://example.com/&view=2d").Origin);
        Assert.Null(EmbedSettings.Parse("?origin=https://example.com/page").Origin);
        Assert.Null(EmbedSettings.Parse("?view=2d").Origin);

        //Like the theme, it says who may talk with the viewer, not what the viewer shows: alone, it applies nothing.
        Assert.False(EmbedSettings.Parse("?origin=https://example.com").HasAny);
    }

    [Fact]
    public void BuildConnection_CosmosCarriesDatabaseAndCollectionAndKey()
    {
        var s = EmbedSettings.Parse("?dbType=cosmos&host=x.gremlin.cosmos.azure.com&port=443&database=mydb&collection=mygraph&authKey=secret");
        var conn = s.BuildConnection();

        Assert.Equal("CosmosDb", conn.DatabaseType);
        Assert.Equal("mydb", conn.Database);
        Assert.Equal("mygraph", conn.Collection);
        Assert.Equal("secret", conn.AuthKey);
        Assert.True(conn.UseSSL);
    }
}
