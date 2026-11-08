using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///What the two provider families do the same way, written once: a whole answer sent and what it was billed for kept, an
///answer streamed, the tool loop, and a conversation's turns as the wire takes them. Until 2026-11-07 each family had a
///copy of its own, the tool loop byte for byte, and two bugs of that day's audit (a streamed event that is not an object,
///a streamed answer's spend) had each to be found and fixed twice. What differs, each family says through the members
///below: where a request goes and with which headers, its bodies, how a reply, a stream's event and the spend are read,
///and how a round's calls and their results are written.
///</summary>
public abstract class LlmProviderBase : ILlmProvider
{
    private readonly HttpClient _http;

    protected LlmProviderBase(HttpClient http, LlmConnection connection)
    {
        _http = http;
        Price = AiPricing.For(connection, DateTime.UtcNow);
    }

    public abstract string Model { get; }

    public LlmUsage Spent { get; } = new LlmUsage();

    public LlmPrice Price { get; }

    ///<summary>Who answered, as a failure names them.</summary>
    protected abstract string Label { get; }

    ///<summary>Why nothing should be sent, or null when the connection is fit to send with. Every way of asking comes through here.</summary>
    protected abstract string WhyNotSend();

    ///<summary>A request carrying <paramref name="body"/>, to the family's address with its headers.</summary>
    protected abstract HttpRequestMessage Request(string body);

    ///<summary>The conversation's turns as the family's wire takes them, the system prompt among them where the family puts it there.</summary>
    protected abstract JsonArray WireMessages(string systemPrompt, IReadOnlyList<LlmMessage> conversation);

    ///<summary>The body asking for a whole answer.</summary>
    protected abstract string WholeBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation);

    ///<summary>The body asking for the answer as it is written.</summary>
    protected abstract string StreamingBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation);

    ///<summary>A whole answer read: its text, or what was wrong.</summary>
    protected abstract LlmResult ReadWhole(string json);

    ///<summary>One event of a stream read.</summary>
    protected abstract LlmStreamEvent ReadEvent(string data);

    ///<summary>What an answer says it was billed for.</summary>
    protected abstract LlmUsage UsageOf(JsonElement root);

    ///<summary>The tools as the family's wire offers them.</summary>
    protected abstract JsonArray ToolsOf(IReadOnlyList<LlmTool> tools);

    ///<summary>The body for one round of the tool loop: the conversation so far, with the tools offered.</summary>
    protected abstract string ToolRoundBody(string systemPrompt, JsonArray messages, JsonArray tools);

    ///<summary>
    ///One round's reply read: the run's end (an answer in words, or why it stops there), or the calls to run, the reply
    ///having been added to <paramref name="messages"/> as the family sends it back.
    ///</summary>
    protected abstract ToolRound ReadToolRound(JsonElement root, JsonArray messages);

    ///<summary>What each call of a round answered, added to <paramref name="messages"/> as the family sends it.</summary>
    protected abstract void AddToolResults(JsonArray messages, IReadOnlyList<(LlmToolCall Call, LlmToolOutput Output)> ran);

    ///<summary>A round of the tool loop read: the run's end, or the calls to run.</summary>
    protected readonly record struct ToolRound(LlmResult? Ended, IReadOnlyList<LlmToolCall> Calls)
    {
        public static ToolRound End(LlmResult result)
        {
            return new ToolRound(result, null);
        }

        public static ToolRound Run(IReadOnlyList<LlmToolCall> calls)
        {
            return new ToolRound(null, calls);
        }
    }

    public Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        return CompleteAsync(systemPrompt, LlmMessage.One(userPrompt), ct);
    }

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        if (WhyNotSend() is string why)
            return LlmResult.Failure(why);

        try
        {
            var (json, problem) = await PostAsync(WholeBody(systemPrompt, conversation), ct);

            if (problem != null)
                return LlmResult.Failure(problem);

            RecordUsage(json);

            return ReadWhole(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LlmResult.Failure(ex.Message);
        }
    }

    ///<summary>
    ///The answer as it is written. The response is read from the headers rather than in full — otherwise
    ///the client waits for the last byte and there is nothing to stream — and the events are read by
    ///<see cref="LlmStream"/>. A failed request is said as a whole answer's is, by
    ///<see cref="LlmProviderFactory.OpenStreamAsync"/>.
    ///</summary>
    public async Task<LlmResult> StreamAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, Action<string> onProgress, CancellationToken ct)
    {
        if (WhyNotSend() is string why)
            return LlmResult.Failure(why);

        try
        {
            using var req = Request(StreamingBody(systemPrompt, conversation));

            var (opened, problem) = await LlmProviderFactory.OpenStreamAsync(_http, req, Label, ct);

            if (problem != null)
                return LlmResult.Failure(problem);

            using var resp = opened;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            LlmUsage billed = null;

            var result = await LlmStream.ReadAsync(stream, data =>
            {
                var next = ReadEvent(data);

                billed = LlmUsage.Latest(billed, next.Usage);

                return next;
            }, onProgress, ct);

            //What the answer says it cost, once it has been read, as a whole answer's is (RecordUsage).
            Spent.Add(billed);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LlmResult.Failure(ex.Message);
        }
    }

    public Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, string userPrompt, ILlmToolRunner tools, CancellationToken ct)
    {
        return CompleteWithToolsAsync(systemPrompt, LlmMessage.One(userPrompt), tools, ct);
    }

    public async Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, ILlmToolRunner tools, CancellationToken ct, int maxRounds = LlmProviderFactory.DefaultToolRounds)
    {
        if (WhyNotSend() is string why)
            return LlmResult.Failure(why);

        try
        {
            return await RunToolsAsync(systemPrompt, conversation, tools, ct, maxRounds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LlmResult.Failure(ex.Message);
        }
    }

    private async Task<LlmResult> RunToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, ILlmToolRunner tools, CancellationToken ct, int maxRounds)
    {
        var toolsArray = ToolsOf(tools.Tools);
        var messages = WireMessages(systemPrompt, conversation);
        var progress = new ToolProgress();

        for (int round = 0; round < maxRounds; round++)
        {
            var (json, problem) = await PostAsync(ToolRoundBody(systemPrompt, messages, toolsArray), ct);

            if (problem != null)
                return LlmResult.Failure(problem);

            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return LlmResult.Failure($"{Label} answered with something that is not JSON.");
            }

            using (doc)
            {
                //Before anything can end the run: a round is billed whatever comes of it.
                Spent.Add(UsageOf(doc.RootElement));

                if (LlmProviderFactory.ErrorIn(json) is string said)
                    return LlmResult.Failure(said);

                var read = ReadToolRound(doc.RootElement, messages);

                if (read.Ended is LlmResult ended)
                    return ended;

                var ran = new List<(LlmToolCall Call, LlmToolOutput Output)>();

                foreach (var call in read.Calls)
                    ran.Add((call, await tools.RunToolAsync(call.Name, call.ArgumentsJson, ct)));

                AddToolResults(messages, ran);

                if (progress.IsStuckAfter(ran))
                    return LlmResult.Failure(LlmProviderFactory.RoundsStuck);
            }
        }

        return LlmResult.Paused(LlmProviderFactory.RoundsPaused(maxRounds));
    }

    ///<summary>Sends a body for a whole answer: its JSON, or why there is none (<see cref="LlmProviderFactory.SendAsync"/>).</summary>
    private async Task<(string Body, string Problem)> PostAsync(string body, CancellationToken ct)
    {
        using var req = Request(body);

        var (json, problem) = await LlmProviderFactory.SendAsync(_http, req, Label, ct);

        if (problem == null && json == null)
            return (null, $"{Label} sent nothing back.");

        return (json, problem);
    }

    ///<summary>Adds what an answer says it cost to <see cref="Spent"/>.</summary>
    private void RecordUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            Spent.Add(UsageOf(document.RootElement));
        }
        catch (JsonException) { }
    }

    ///<summary>
    ///A conversation's turns added to <paramref name="messages"/>, as both families send them: a turn with files as the
    ///list <paramref name="withFiles"/> makes of it, any other as its text, and an empty one dropped.
    ///</summary>
    protected static JsonArray AddTurns(JsonArray messages, IReadOnlyList<LlmMessage> conversation, Func<LlmMessage, JsonArray> withFiles)
    {
        if (conversation == null)
            return messages;

        foreach (var turn in conversation)
        {
            if (turn != null && turn.Attachments.Count > 0)
            {
                messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = withFiles(turn) });

                continue;
            }

            if (string.IsNullOrWhiteSpace(turn?.Text))
                continue;

            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Text });
        }

        return messages;
    }

    ///<summary>A question's files ahead of its words, each file as <paramref name="part"/> writes it for the family it goes to.</summary>
    protected static JsonArray FilesThenWords(LlmMessage turn, string providerType, Func<AttachmentPart, JsonObject> part)
    {
        var content = new JsonArray();

        foreach (var one in AiAttachment.Parts(turn.Attachments, providerType))
            content.Add(part(one));

        if (!string.IsNullOrWhiteSpace(turn.Text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });

        return content;
    }
}
