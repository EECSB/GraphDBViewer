using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;

namespace GraphDBViewerWeb.Tests;

//The knowledge-graph tool against a real document rather than a sentence written to pass: Ask AI's instructions and the graph
//tool sent to a live model with the first part of the sample, and what it proposes read as the card reads it. It calls a live
//model, so it is skipped unless somebody asks for it — see LiveAiFactAttribute. Until 2026-11-02 the free tests here split the
//sample into the pieces the knowledge-graph window extracted one at a time; in Ask AI the model proposes a long text in parts.
public class KgSampleDocumentTests
{
    //The graph tool as Ask AI offers it, keeping each graph the model proposed.
    private sealed class Proposals : ILlmToolRunner
    {
        private readonly GraphProposalRunner _runner;

        public Proposals(string text)
        {
            _runner = new GraphProposalRunner(null, () => null, () => text, () => false);
        }

        public List<string> Proposed { get; } = new();

        public IReadOnlyList<LlmTool> Tools => _runner.Tools;

        public Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Proposed.Add(argumentsJson);

            return _runner.RunToolAsync(name, argumentsJson, ct);
        }
    }

    //The one that costs money. It sends the first part of the sample to whatever model dev-secrets.json holds, asked for a
    //graph as the Import panel's Generate with AI asks, and reads what it proposes: the whole instructions-to-card path, against
    //a real model, over real prose. One part rather than the whole document: this is here to catch instructions or a parser that
    //have drifted, not to bill for sixty thousand characters of encyclopedia.
    [LiveAiFact]
    public async Task ALiveModelProposesAGraphTheCardReads()
    {
        var connection = await LiveAi.FirstConnectionAsync();

        Assert.True(connection != null, $"No usable model in {DevSecrets.Path}. Fill one in to run this.");

        var part = SampleDocument.Text.Substring(0, Math.Min(KgPrompt.PartChars, SampleDocument.Text.Length));
        var proposals = new Proposals(part);
        var provider = LlmProviderFactory.Create(new HttpClient(), connection);
        var system = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, canPropose: true);

        var result = await provider.CompleteWithToolsAsync(system, LlmMessage.One(NlQueryModal.GraphStart + part), proposals, CancellationToken.None);

        Assert.False(result.IsError, result.Error);
        Assert.NotEmpty(proposals.Proposed);

        var read = GraphProposal.Read(proposals.Proposed[^1], null, part, false);

        Assert.True(read.Error == null, read.Error);
        Assert.NotEmpty(read.Graph.Nodes);

        //Encyclopedia prose about reactors should yield entities that are joined up, not a bag of nouns.
        Assert.NotEmpty(read.Graph.Edges);
    }
}
