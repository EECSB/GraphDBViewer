using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class KgPromptTests
{
    //What the one-shot prompt asked of an extraction still holds in a conversation: one canonical name per entity, ids and a
    //name, camelCase relationships, the parser's own caps. The knowledge-graph window's options (2026-11-02, its checkboxes
    //again) are asked for only when ticked, in its words: each costs an answer's length or a request.
    [Fact]
    public void TheExtractionsRules_AreKept_AndEachOptionIsAskedForOnlyWhenTicked()
    {
        var plain = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, canPropose: true);
        var ticked = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, canPropose: true, extraction: new KgExtractionOptions(false, true, true, true, true));

        Assert.Contains("ONE canonical name", plain);
        Assert.Contains("short lowercase id and a human-readable \"name\" property", plain);
        Assert.Contains("camelCase verbs", plain);
        Assert.Contains($"at most {KgGraphParser.MaxNodes} nodes and {KgGraphParser.MaxEdges} edges", plain);

        Assert.DoesNotContain($"\"{KgGraphParser.SourceSentenceProperty}\" field", plain);
        Assert.DoesNotContain($"\"{KgGraphParser.ConfidenceProperty}\" field", plain);
        Assert.DoesNotContain("in pieces", plain);
        Assert.DoesNotContain("once more for what you missed", plain);

        Assert.Contains($"\"{KgGraphParser.SourceSentenceProperty}\" field: the sentence from the text it came from, quoted verbatim", ticked);
        Assert.Contains($"\"{KgGraphParser.ConfidenceProperty}\" field between 0 and 1", ticked);
        Assert.Contains($"longer than about {KgPrompt.PartChars:N0} characters is proposed in pieces", ticked);
        Assert.Contains("once more for what you missed", ticked);
    }

    //Ask AI with the graph tool (2026-11-02) is told when to propose, and labels in the window's three states: invented unless
    //grounding was ticked, and then the graph's own and no others, or, where the graph was read and is empty, invented and said.
    [Fact]
    public void AskAiWithTheGraphTool_IsToldWhenToPropose_AndWhichLabels()
    {
        var schema = new SchemaVocabulary { VertexLabels = new() { "Person" }, EdgeLabels = new() { "worksAt" }, PropertyKeys = new() { "name" } };
        var ground = new KgExtractionOptions(GroundInSchema: true);

        var free = NlQueryPrompt.BuildChatSystemPrompt("gremlin", schema, canPropose: true);
        var grounded = NlQueryPrompt.BuildChatSystemPrompt("gremlin", schema, canPropose: true, extraction: ground);
        var empty = NlQueryPrompt.BuildChatSystemPrompt("gremlin", new SchemaVocabulary(), canPropose: true, extraction: ground);
        var without = NlQueryPrompt.BuildChatSystemPrompt("gremlin", schema);

        Assert.Contains($"call {GraphProposalRunner.ProposeName}", free);
        Assert.Contains("Invent a proposed graph's labels from the text itself", free);
        Assert.DoesNotContain("use ONLY", free);
        Assert.Contains("use ONLY the connected graph's vertex labels, edge labels and property keys", grounded);
        Assert.Contains("The connected graph is empty", empty);
        Assert.DoesNotContain("use ONLY", empty);
        Assert.Contains($"call {WikipediaRunner.ReadName} to read it", free);
        Assert.DoesNotContain(GraphProposalRunner.ProposeName, without);
        Assert.DoesNotContain(WikipediaRunner.ReadName, without);
    }

    //Switched off in the picker, the tool is said to be, and how to have it back, rather than its rules given.
    [Fact]
    public void ASwitchedOffGraphTool_IsSaidToBe()
    {
        var prompt = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, toolsOff: new[] { GraphProposalRunner.ProposeName, WikipediaRunner.ReadName }, canPropose: true);

        Assert.Contains($"{GraphProposalRunner.ProposeName} is switched off", prompt);
        Assert.DoesNotContain($"call {GraphProposalRunner.ProposeName}", prompt);
        Assert.Contains($"{WikipediaRunner.ReadName} is switched off", prompt);
        Assert.DoesNotContain($"call {WikipediaRunner.ReadName}", prompt);
    }
}
