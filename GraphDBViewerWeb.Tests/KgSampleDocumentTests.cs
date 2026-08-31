using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

//The knowledge-graph pipeline against a real document rather than a sentence written to pass. Two kinds
//of test live here: the free ones, which run the pure code over the sample and cost nothing, and the one
//that calls a live model, which is skipped unless somebody asks for it — see LiveAiFactAttribute.
public class KgSampleDocumentTests
{
    //The size the modal chunks at, and the reason chunking exists at all.
    private const int ChunkChars = 6000;

    [SampleDocumentFact]
    public void TheSampleIsLongEnoughToNeedSplitting()
    {
        Assert.True(SampleDocument.Text.Length > ChunkChars * 2, "The sample should be long enough that chunking is exercised.");
    }

    //Every piece has to fit under the cap, or the split has not done its job and the request it feeds
    //comes back truncated mid-graph.
    [SampleDocumentFact]
    public void EveryPieceFitsUnderTheCap()
    {
        var pieces = KgGraphParser.SplitIntoChunks(SampleDocument.Text, ChunkChars);

        Assert.NotEmpty(pieces);
        Assert.All(pieces, piece => Assert.True(piece.Length <= ChunkChars, $"A piece was {piece.Length} characters, over the {ChunkChars} cap."));
    }

    //Splitting must not lose the document. Whitespace at the seams is fine; words are not.
    [SampleDocumentFact]
    public void SplittingKeepsEveryWord()
    {
        var text = SampleDocument.Text;
        var pieces = KgGraphParser.SplitIntoChunks(text, ChunkChars);

        var original = Words(text);
        var rejoined = Words(string.Join(" ", pieces));

        Assert.Equal(original.Count, rejoined.Count);
        Assert.Equal(original.First(), rejoined.First());
        Assert.Equal(original.Last(), rejoined.Last());
    }

    private static List<string> Words(string text)
    {
        return text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    //The one that costs money. It sends a single chunk to whatever model dev-secrets.json holds and
    //asks the parser to make a graph of the answer — the whole prompt-to-graph path, against a real
    //model, over real prose. One chunk rather than the whole document: this is here to catch a prompt
    //or parser that has drifted, not to bill for sixty thousand characters of encyclopedia.
    [LiveAiFact]
    public async Task ALiveModelReturnsAGraphTheParserUnderstands()
    {
        var connection = await LiveAi.FirstConnectionAsync();

        Assert.True(connection != null, $"No usable model in {DevSecrets.Path}. Fill one in to run this.");

        var chunk = KgGraphParser.SplitIntoChunks(SampleDocument.Text, ChunkChars).First();
        var provider = LlmProviderFactory.Create(new HttpClient(), connection);

        var result = await provider.CompleteAsync(KgPrompt.BuildSystemPrompt(null), chunk, CancellationToken.None);

        Assert.False(result.IsError, result.Error);

        var parsed = KgGraphParser.Parse(result.Text);

        Assert.True(parsed.Error == null, parsed.Error);
        Assert.NotEmpty(parsed.Graph.Nodes);

        //Encyclopedia prose about reactors should yield entities that are joined up, not a bag of nouns.
        Assert.NotEmpty(parsed.Graph.Edges);
    }
}
