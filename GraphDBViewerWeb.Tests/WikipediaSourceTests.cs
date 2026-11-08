using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class WikipediaSourceTests
{
    [Fact]
    public void BuildExtractUrl_EncodesTheTitleAndStaysCorsOpen()
    {
        var url = WikipediaSource.BuildExtractUrl("Nikola Tesla");

        Assert.Contains("titles=Nikola%20Tesla", url);
        Assert.Contains("origin=*", url);
        Assert.Contains("explaintext=1", url);
    }

    [Fact]
    public void ParseExtract_ReadsThePageText()
    {
        var json = """
            {"query":{"pages":{"21473":{"pageid":21473,"title":"Nikola Tesla","extract":"Nikola Tesla was an inventor."}}}}
            """;

        Assert.Equal("Nikola Tesla was an inventor.", WikipediaSource.ParseExtract(json));
    }

    [Fact]
    public void ParseExtract_MissingPage_ReturnsNull()
    {
        var json = """
            {"query":{"pages":{"-1":{"title":"Nope Nope","missing":""}}}}
            """;

        Assert.Null(WikipediaSource.ParseExtract(json));
    }

    [Fact]
    public void ParseExtract_Garbage_ReturnsNull()
    {
        Assert.Null(WikipediaSource.ParseExtract("not json"));
    }

    //The box asks for a title, because that is what the API takes — but somebody with the article open
    //will paste the address bar, so a link is read for its title too. And for its Wikipedia: a German
    //link asking the English API would simply miss.
    [Theory]
    [InlineData("Nikola Tesla", "Nikola Tesla", "https://en.wikipedia.org/w/api.php")]
    [InlineData("  Graph database  ", "Graph database", "https://en.wikipedia.org/w/api.php")]
    [InlineData("https://en.wikipedia.org/wiki/Graph_database", "Graph database", "https://en.wikipedia.org/w/api.php")]
    [InlineData("https://de.wikipedia.org/wiki/Graphdatenbank", "Graphdatenbank", "https://de.wikipedia.org/w/api.php")]
    [InlineData("https://en.wikipedia.org/w/index.php?title=Graph_database&action=history", "Graph database", "https://en.wikipedia.org/w/api.php")]
    public void ASourceIsReadAsATitleOrAsALink(string input, string title, string apiBase)
    {
        var source = WikipediaSource.ParseSource(input);

        Assert.Equal(title, source.Title);
        Assert.Equal(apiBase, source.ApiBase);
    }

    //An escaped title survives the round trip, which is the case a link most often carries.
    [Fact]
    public void APercentEscapedLinkComesBackAsRealCharacters()
    {
        Assert.Equal("Gödel's theorem", WikipediaSource.ParseSource("https://en.wikipedia.org/wiki/G%C3%B6del%27s_theorem").Title);
    }

    //Anything that is not a Wikipedia link is left exactly as typed, so a title with a slash still works.
    [Theory]
    [InlineData("AC/DC")]
    [InlineData("https://example.com/wiki/Nope")]
    public void SomethingThatIsNotAWikipediaLinkIsLeftAlone(string input)
    {
        var source = WikipediaSource.ParseSource(input);

        Assert.Equal(input, source.Title);
        Assert.Equal(WikipediaSource.ApiBase, source.ApiBase);
    }

    ///<summary>
    ///A link to a host that only ends in the letters of Wikipedia's is not a link to an article there: it is left as it was
    ///typed, a title to ask the English Wikipedia for, as any other link that is not Wikipedia's is. From the audit of
    ///2026-11-05, fixed 2026-11-07: the host was matched on its ending alone, so a link the model was given or made up had
    ///the tool fetch from somebody else's server and answer the model with it.
    ///</summary>
    [Theory]
    [InlineData("https://evilwikipedia.org/wiki/Secret")]
    [InlineData("https://not-wikipedia.org/wiki/Secret")]
    [InlineData("https://wikipedia.org.example/wiki/Secret")]
    public void ALinkToAHostThatOnlyEndsLikeWikipedias_IsNotReadAsOneOfItsLinks(string link)
    {
        var source = WikipediaSource.ParseSource(link);

        Assert.Equal(link, source.Title);
        Assert.Equal(WikipediaSource.ApiBase, source.ApiBase);
        Assert.False(WikipediaSource.IsWikipedia(new Uri(link).Host));
    }

    //Wikipedia's own hosts, bare and with a language or a mobile subdomain, are its.
    [Theory]
    [InlineData("wikipedia.org")]
    [InlineData("en.wikipedia.org")]
    [InlineData("de.m.wikipedia.org")]
    [InlineData("EN.WIKIPEDIA.ORG")]
    public void WikipediasOwnHosts_AreIts(string host)
    {
        Assert.True(WikipediaSource.IsWikipedia(host));
    }

    //And the URL builder goes through the same reading, so pasting a link into the box actually fetches.
    [Fact]
    public void TheBuiltUrlAsksForTheTitleTheLinkNamed()
    {
        var url = WikipediaSource.BuildExtractUrl("https://en.wikipedia.org/wiki/Graph_database");

        Assert.Contains("titles=Graph%20database", url);
        Assert.DoesNotContain("wikipedia.org%2Fwiki", url);
    }
}
