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

    [Fact]
    public void BuildConnection_NonTlsPortImpliesNoSsl()
    {
        var s = EmbedSettings.Parse("?host=192.168.1.5&port=8182");
        var conn = s.BuildConnection();

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
