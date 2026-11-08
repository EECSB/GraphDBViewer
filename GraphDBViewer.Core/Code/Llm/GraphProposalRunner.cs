using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///The graph tool (2026-11-02, the user's choice of folding knowledge-graph generation into Ask AI rather than keeping it a
///window of its own): the model extracts a graph from text in the conversation, what the person typed, a file they sent or an
///article read, and proposes it. The call changes nothing and waits for nobody (<see cref="LlmTool.Proposes"/>); its card is
///the preview and the Use button, and using it is the person's press. The model is answered with what the person now sees and
///every repair made to it, so it can correct a graph in the conversation, where the window asked once more on its own.
///</summary>
public sealed class GraphProposalRunner : ILlmToolRunner, IWriteCapableToolRunner
{
    public const string ProposeName = "propose_graph";

    private readonly ILlmToolRunner _inner;
    private readonly Func<IEnumerable<string>> _edgeLabels;
    private readonly Func<string> _sourceText;
    private readonly Func<bool> _filesNotRead;
    private readonly KgExtractionOptions _options;

    ///<param name="inner">The other tools, or null where there are none.</param>
    ///<param name="edgeLabels">The connected graph's edge labels, which relationship names are made consistent with, or null.</param>
    ///<param name="sourceText">What the conversation holds as text, which a quote is looked for in.</param>
    ///<param name="filesNotRead">Whether the conversation sent a PDF or a picture, whose words are not in that text.</param>
    ///<param name="options">What the person asked the model for when it draws a graph, which decides the fields the tool shows.</param>
    public GraphProposalRunner(
        ILlmToolRunner inner,
        Func<IEnumerable<string>> edgeLabels,
        Func<string> sourceText,
        Func<bool> filesNotRead,
        KgExtractionOptions options = null)
    {
        _inner = inner;
        _edgeLabels = edgeLabels;
        _sourceText = sourceText;
        _filesNotRead = filesNotRead;
        _options = options ?? new KgExtractionOptions();
    }

    ///<summary>The tool with nothing extra asked for, as the options start.</summary>
    public static LlmTool ProposeTool => ProposeToolFor(new KgExtractionOptions());

    ///<summary>
    ///The tool, described for the model, with the shape of the graph it takes. The shape grows only by what was asked for, so a
    ///model is never shown a field the person did not want, the surest way to stop it sending one anyway (the knowledge-graph
    ///window's rule); each field has a key of its own, beside an edge's two ends rather than in place of one.
    ///</summary>
    public static LlmTool ProposeToolFor(KgExtractionOptions options)
    {
        var node = new JsonObject
        {
            ["id"] = Field("string", "Short and lowercase, used by the edges."),
            ["label"] = Field("string", null),
            ["properties"] = Field("object", "Its \"name\" and anything else the text says of it.")
        };

        var edge = new JsonObject
        {
            ["source"] = Field("string", "The id of the node it starts at."),
            ["target"] = Field("string", "The id of the node it ends at."),
            ["label"] = Field("string", null),
            ["properties"] = Field("object", null)
        };

        if (options.SourceSentence)
        {
            node[KgGraphParser.SourceSentenceProperty] = Field("string", "The sentence of the text it came from, verbatim.");
            edge[KgGraphParser.SourceSentenceProperty] = Field("string", "The sentence of the text it came from, verbatim.");
        }

        if (options.Confidence)
        {
            node[KgGraphParser.ConfidenceProperty] = Field("number", "0 to 1: how sure the text is of it.");
            edge[KgGraphParser.ConfidenceProperty] = Field("number", "0 to 1: how sure the text is of it.");
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["nodes"] = ListOf(node, "id", "label", "properties"),
                ["edges"] = ListOf(edge, "source", "target", "label")
            },
            ["required"] = new JsonArray("nodes", "edges")
        };

        return new LlmTool
        {
            Name = ProposeName,
            Description = "Proposes a knowledge graph you extracted from text, for the user to use or not. "
                + "They see it previewed, with its counts and every repair the app made, and draw it in place of their drawing or merged into it. Calling it changes nothing.",
            InputSchemaJson = schema.ToJsonString(),
            Proposes = true
        };
    }

    private static JsonObject Field(string type, string description)
    {
        var field = new JsonObject { ["type"] = type };

        if (description != null)
            field["description"] = description;

        return field;
    }

    private static JsonObject ListOf(JsonObject item, params string[] required)
    {
        var requiredNames = new JsonArray();

        foreach (var name in required)
            requiredNames.Add(name);

        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "object", ["properties"] = item, ["required"] = requiredNames }
        };
    }

    public IReadOnlyList<LlmTool> Tools
    {
        get
        {
            var tools = new List<LlmTool>();

            if (_inner != null)
                tools.AddRange(_inner.Tools);

            tools.Add(ProposeToolFor(_options));

            return tools;
        }
    }

    public bool AllowWrites
    {
        get
        {
            return _inner is IWriteCapableToolRunner writable && writable.AllowWrites;
        }
        set
        {
            if (_inner is IWriteCapableToolRunner writable)
                writable.AllowWrites = value;
        }
    }

    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (name == ProposeName)
            return Propose(argumentsJson);

        if (_inner == null)
            return LlmToolOutput.Failure($"There is no tool called {name} here.");

        return await _inner.RunToolAsync(name, argumentsJson, ct);
    }

    //A graph that cannot be read is refused, with why, so the model sends it again: what the window did with one more
    //request of its own, here in the conversation.
    private LlmToolOutput Propose(string argumentsJson)
    {
        var read = GraphProposal.Read(argumentsJson, _edgeLabels?.Invoke(), _sourceText?.Invoke(), _filesNotRead?.Invoke() ?? false);

        if (read.IsError)
            return LlmToolOutput.Failure($"{read.Error} Send it again as one object with \"nodes\" and \"edges\" of the shape the tool describes. If it ran out of room, send fewer nodes and edges rather than an unfinished graph.");

        return LlmToolOutput.Result(GraphProposal.AnswerFor(read));
    }
}
