using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class NlQueryPromptTests
{
    [Theory]
    [InlineData("gremlin", "Gremlin")]
    [InlineData("cypher", "openCypher")]
    [InlineData("sparql", "SPARQL")]
    [InlineData("something-else", "Gremlin")]
    public void LanguageDisplayName_MapsEditorLanguages(string input, string expected)
    {
        Assert.Equal(expected, NlQueryPrompt.LanguageDisplayName(input));
    }

    [Fact]
    public void BuildSystemPrompt_IncludesLanguageAndSchema()
    {
        var schema = new SchemaVocabulary
        {
            VertexLabels = new() { "Product", "Material" },
            EdgeLabels = new() { "composes" },
            PropertyKeys = new() { "name", "description" }
        };

        var prompt = NlQueryPrompt.BuildSystemPrompt("gremlin", schema);

        Assert.Contains("Gremlin", prompt);
        Assert.Contains("Product", prompt);
        Assert.Contains("composes", prompt);
        Assert.Contains("description", prompt);
        Assert.Contains("No explanation", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OmitsEmptySchemaSections()
    {
        var schema = new SchemaVocabulary { VertexLabels = new() { "Product" } };
        var prompt = NlQueryPrompt.BuildSystemPrompt("sparql", schema);

        Assert.Contains("Vertex labels:", prompt);
        Assert.DoesNotContain("Edge labels:", prompt);
        Assert.DoesNotContain("Property keys:", prompt);
    }

    //The "use ONLY the labels listed below" rule used to be emitted unconditionally while the label
    //sections below it were each gated on being non-empty — so an unread schema constrained the model to
    //the empty set and then told it to answer anyway.
    [Fact]
    public void BuildSystemPrompt_NullSchemaDoesNotClaimAListing()
    {
        var prompt = NlQueryPrompt.BuildSystemPrompt("gremlin", null);

        Assert.DoesNotContain("listed below", prompt);
        Assert.Contains("unknown", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_EmptySchemaSaysEmptyRatherThanClaimingAListing()
    {
        var prompt = NlQueryPrompt.BuildSystemPrompt("gremlin", new SchemaVocabulary());

        Assert.DoesNotContain("listed below", prompt);
        Assert.Contains("empty", prompt);
    }

    //A read-but-empty vocabulary and an unread one are different facts, and the model is told which.
    [Fact]
    public void BuildSystemPrompt_DistinguishesEmptyGraphFromUnknownSchema()
    {
        var empty = NlQueryPrompt.BuildSystemPrompt("gremlin", new SchemaVocabulary());
        var unknown = NlQueryPrompt.BuildSystemPrompt("gremlin", null);

        Assert.NotEqual(empty, unknown);
    }

    [Fact]
    public void BuildSystemPrompt_PopulatedSchemaKeepsTheUseOnlyRule()
    {
        var schema = new SchemaVocabulary { VertexLabels = new() { "Product" } };
        var prompt = NlQueryPrompt.BuildSystemPrompt("gremlin", schema);

        Assert.Contains("Use ONLY", prompt);
        Assert.Contains("Vertex labels: Product", prompt);
    }

    [Fact]
    public void CleanQuery_ReturnsPlainQueryUnchanged()
    {
        Assert.Equal("g.V().limit(5)", NlQueryPrompt.CleanQuery("  g.V().limit(5)  "));
    }

    [Fact]
    public void CleanQuery_StripsFencedBlockWithLanguageTag()
    {
        var fenced = "```gremlin\ng.V().hasLabel('Product')\n```";
        Assert.Equal("g.V().hasLabel('Product')", NlQueryPrompt.CleanQuery(fenced));
    }

    [Fact]
    public void CleanQuery_StripsPlainFence()
    {
        var fenced = "```\nSELECT * WHERE { ?s ?p ?o }\n```";
        Assert.Equal("SELECT * WHERE { ?s ?p ?o }", NlQueryPrompt.CleanQuery(fenced));
    }

    [Fact]
    public void CleanQuery_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, NlQueryPrompt.CleanQuery(null));
    }
}
