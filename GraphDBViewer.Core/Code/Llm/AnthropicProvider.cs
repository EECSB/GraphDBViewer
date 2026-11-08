using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Calls the Anthropic Messages API directly from the browser. Request building and response parsing are
///pure and unit-tested; the HTTP send is thin (mirrors the GremlinDB / GremlinQueryBuilder split), with an
///agentic tool-use loop in Anthropic's tool_use / tool_result format.
///
///<strong>Brought up to GDS Viewer's port of it on 2026-11-01</strong>: what each answer was billed for is kept as it
///arrives (<see cref="Spent"/>), an effort goes in output_config and fast mode as the speed, a tool loop is marked for
///Anthropic's cache, a tool's picture goes back inside its result, a run that gets nowhere is stopped and a long one
///paused, and the bodies are built as JSON nodes rather than serialized from anonymous objects.
///
///Sending, billing, streaming and the tool loop are <see cref="LlmProviderBase"/>'s, shared with the OpenAI family since
///2026-11-07; this class says what Anthropic's wire is.
///</summary>
public class AnthropicProvider : LlmProviderBase
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";

    ///<summary>
    ///The model a connection runs when its model field is left blank: the most capable one there is, since that is what
    ///somebody who did not choose is best served by. The form's placeholder reads it from here, so what runs is what was
    ///offered.
    ///</summary>
    public const string DefaultModel = "claude-opus-5-5";

    ///<summary>The beta a request in fast mode is sent under, beside its speed; see <see cref="LlmConnection.SupportsFast"/>.</summary>
    public const string FastBeta = "fast-mode-2026-02-01";

    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;
    private readonly string _effort;
    private readonly bool _fast;

    public AnthropicProvider(HttpClient http, LlmConnection connection) : base(http, connection)
    {
        _apiKey = (connection.ApiKey ?? "").Trim();
        _maxTokens = ResolveMaxTokens(connection);
        _model = ResolveModel(connection);
        _effort = ResolveEffort(connection);
        _fast = LlmConnection.SupportsFast(connection) && connection.Fast;
    }

    public override string Model => _model;

    protected override string Label => "Anthropic";

    ///<summary>
    ///The output cap a connection runs with. A missing or nonsensical value falls back to the shared
    ///default rather than being sent as-is — the Messages API requires a positive max_tokens.
    ///</summary>
    public static int ResolveMaxTokens(LlmConnection connection)
    {
        if (connection.MaxTokens > 0)
            return connection.MaxTokens;

        return LlmConnection.DefaultMaxTokens;
    }

    ///<summary>
    ///The model a connection actually runs. Anthropic is the one provider whose model field is optional, so a
    ///blank one falls back to <see cref="DefaultModel"/> — which is the model the connection form's
    ///placeholder names, so what runs is what was offered.
    ///</summary>
    public static string ResolveModel(LlmConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Model))
            return DefaultModel;

        return connection.Model.Trim();
    }

    ///<summary>
    ///The effort a connection is sent with: its own when the model takes that level, and none otherwise. A level chosen
    ///for another model is refused by this one rather than ignored, so it is dropped rather than sent.
    ///</summary>
    public static string ResolveEffort(LlmConnection connection)
    {
        foreach (var level in LlmConnection.EffortLevels(connection))
            if (level == connection.Effort)
                return level;

        return "";
    }

    ///<summary>Builds the Messages API request body (model + max_tokens + system + a single user message).</summary>
    public static string BuildRequestBody(string model, int maxTokens, string system, string user)
    {
        return BuildRequestBody(model, maxTokens, system, LlmMessage.One(user));
    }

    ///<summary>The same body over a whole conversation, which is what a follow-up question needs sent.</summary>
    public static string BuildRequestBody(string model, int maxTokens, string system, IReadOnlyList<LlmMessage> conversation)
    {
        return BuildRequestBody(model, maxTokens, system, ToWireMessages(conversation), null);
    }

    ///<summary>
    ///A request: the model, the cap, the system prompt when there is one, the conversation so far and, when there are any,
    ///the tools, marked where Anthropic should cache when asked. An effort goes in output_config and fast mode as the speed, both
    ///only when chosen, so a request that chose neither is the one it always was.
    ///</summary>
    public static string BuildRequestBody(
        string model,
        int maxTokens,
        string system,
        JsonArray messages,
        JsonArray tools,
        bool cache = false,
        string effort = "",
        bool fast = false)
    {
        return BuildBody(model, maxTokens, system, messages, tools, cache, effort, fast).ToJsonString();
    }

    ///<summary>The same body again, asking for the answer as it is written rather than when it is finished.</summary>
    public static string BuildStreamingRequestBody(string model, int maxTokens, string system, IReadOnlyList<LlmMessage> conversation, string effort = "", bool fast = false)
    {
        var body = BuildBody(model, maxTokens, system, ToWireMessages(conversation), null, false, effort, fast);

        body["stream"] = true;

        return body.ToJsonString();
    }

    private static JsonObject BuildBody(string model, int maxTokens, string system, JsonArray messages, JsonArray tools, bool cache, string effort, bool fast)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = messages.DeepClone()
        };

        //A prompt with nothing in it is no prompt: the field is left out, since the API refuses a null or an empty one.
        if (!string.IsNullOrWhiteSpace(system))
            body["system"] = system;

        if (tools != null && tools.Count > 0)
            body["tools"] = tools.DeepClone();

        if (!string.IsNullOrEmpty(effort))
            body["output_config"] = new JsonObject { ["effort"] = effort };

        if (fast)
            body["speed"] = "fast";

        if (cache)
            MarkForCache(body);

        return body;
    }

    ///<summary>
    ///Tells Anthropic what to cache: everything up to the last tool, and everything up to the end of the request.
    ///
    ///<strong>Anthropic caches only what it is told to</strong>, where OpenAI and Google cache a repeated opening by
    ///themselves, and a request reads back what an earlier one cached up to one of its own marks. So two marks: after the
    ///tools, which are the same in every request of every conversation (the system prompt comes after them and is not
    ///under this mark, since the schema in it changes with the graph), and after the last block of the conversation, so
    ///the next round of a run of tool calls, which sends all of this again with one more call and its answer, reads it
    ///back at a tenth of the rate. A string is made a block to carry it.
    ///
    ///<strong>On the copy each request is built from</strong>, never on the conversation the loop keeps adding to: marks
    ///left on every round's last block would pass Anthropic's limit of four in three rounds. A write costs a quarter over
    ///the rate and lasts five minutes, which a run of calls uses at once; a request answered in one round pays it for
    ///nothing, which is why only the tool loop asks.
    ///</summary>
    public static void MarkForCache(JsonObject body)
    {
        if (body["tools"] is JsonArray tools && tools.Count > 0 && tools[tools.Count - 1] is JsonObject lastTool)
            lastTool["cache_control"] = Ephemeral();

        if (body["messages"] is not JsonArray messages || messages.Count == 0 || messages[messages.Count - 1] is not JsonObject last)
            return;

        if (last["content"] is JsonValue words)
            last["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = words.GetValue<string>() } };

        if (last["content"] is JsonArray blocks && blocks.Count > 0 && blocks[blocks.Count - 1] is JsonObject block)
            block["cache_control"] = Ephemeral();
    }

    private static JsonObject Ephemeral()
    {
        return new JsonObject { ["type"] = "ephemeral" };
    }

    ///<summary>
    ///What one event of a streamed answer carries. The Messages API sends a named event per line pair, and
    ///the payload names its own type, so the name is not needed: a <c>content_block_delta</c> holding a
    ///<c>text_delta</c> is text, <c>message_stop</c> is the end, an <c>error</c> is the reason it stopped,
    ///<c>message_start</c> and <c>message_delta</c> carry what the answer has been billed for so far (what was read,
    ///then what was written, cumulatively), and everything else — a ping, a content block's start and stop — adds
    ///nothing.
    ///</summary>
    public static LlmStreamEvent ReadStreamEvent(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = "";

            //Not an object: an error wrapped in an array, [{"error": ...}], is read as a whole answer's is; any other
            //non-object adds nothing.
            if (root.ValueKind != JsonValueKind.Object)
            {
                if (LlmProviderFactory.ErrorIn(data) is string wrapped)
                    return LlmStreamEvent.Failed(wrapped);

                return LlmStreamEvent.Nothing;
            }

            if (root.TryGetProperty("type", out var t))
                type = t.GetString();

            if (type == "error")
            {
                var message = "The model stopped mid-answer.";

                if (root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m) && m.GetString() is string reported)
                    message = reported;

                return LlmStreamEvent.Failed(message);
            }
            else if (type == "message_stop")
                return LlmStreamEvent.Ended;
            else if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("text", out var text))
                return LlmStreamEvent.More(text.GetString());
            else if (type == "message_start" && root.TryGetProperty("message", out var started))
                return LlmStreamEvent.Nothing.Billed(LlmUsage.FromAnthropic(started));
            else if (type == "message_delta")
                return LlmStreamEvent.Nothing.Billed(LlmUsage.FromAnthropic(root));

            return LlmStreamEvent.Nothing;
        }
        catch (JsonException)
        {
            //A line that is not JSON is not an answer, as one that is JSON but not an object is not (above); the stream
            //carries on rather than failing on either.
            return LlmStreamEvent.Nothing;
        }
    }

    ///<summary>
    ///The conversation in the Messages API's own shape.
    ///
    ///Empty turns are dropped rather than sent: the API rejects an empty content string, and a turn with
    ///nothing in it is what a canceled or failed generation leaves behind, so a conversation carrying one
    ///would fail every request made after it until it was cleared.
    ///
    ///A question with files is a list of blocks, the files ahead of its words (<see cref="ContentWithFiles"/>); anything
    ///else stays the string it always was.
    ///</summary>
    public static JsonArray ToWireMessages(IReadOnlyList<LlmMessage> conversation)
    {
        return AddTurns(new JsonArray(), conversation, ContentWithFiles);
    }

    ///<summary>A question's files as blocks ahead of its words: a picture as an image block, a PDF as a document, a text file as text between tags.</summary>
    public static JsonArray ContentWithFiles(LlmMessage turn)
    {
        return FilesThenWords(turn, LlmConnection.Anthropic, Block);
    }

    ///<summary>One part of a question's files as the Messages API takes it.</summary>
    public static JsonObject Block(AttachmentPart part)
    {
        if (part.Kind != AttachmentKind.Image && part.Kind != AttachmentKind.Pdf)
            return new JsonObject { ["type"] = "text", ["text"] = part.Text };

        var type = "image";

        if (part.Kind == AttachmentKind.Pdf)
            type = "document";

        return new JsonObject
        {
            ["type"] = type,
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = part.MediaType,
                ["data"] = part.Base64
            }
        };
    }

    ///<summary>A plain reply read: its text, or what the API said was wrong, or that it declined.</summary>
    public static LlmResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (LlmProviderFactory.ErrorIn(json) is string said)
                return LlmResult.Failure(said);

            if (StopReason(root) == "refusal")
                return LlmResult.Failure("The model declined to answer (refusal).");

            if (!root.TryGetProperty("content", out var content))
                return LlmResult.Failure("Anthropic answered in a shape this app does not know.");

            return Answered(TextOf(content), StopReason(root));
        }
        catch (JsonException)
        {
            return LlmResult.Failure("Anthropic answered with something that is not JSON.");
        }
    }

    protected override JsonArray WireMessages(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return ToWireMessages(conversation);
    }

    protected override string WholeBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return BuildRequestBody(_model, _maxTokens, systemPrompt, ToWireMessages(conversation), null, false, _effort, _fast);
    }

    protected override string StreamingBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return BuildStreamingRequestBody(_model, _maxTokens, systemPrompt, conversation, _effort, _fast);
    }

    protected override LlmResult ReadWhole(string json)
    {
        return ParseResponse(json);
    }

    protected override LlmStreamEvent ReadEvent(string data)
    {
        return ReadStreamEvent(data);
    }

    protected override LlmUsage UsageOf(JsonElement root)
    {
        return LlmUsage.FromAnthropic(root);
    }

    protected override HttpRequestMessage Request(string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);

        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        if (_fast)
            req.Headers.TryAddWithoutValidation("anthropic-beta", FastBeta);

        //Required for CORS when calling the Anthropic API directly from a browser.
        req.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");

        return req;
    }

    ///<summary>Why nothing should be sent, or null when the connection is fit to send with: a hosted provider with no key would only say so with a 401.</summary>
    protected override string WhyNotSend()
    {
        if (_apiKey.Length == 0)
            return "This model has no API key. Add one under AI settings.";

        return null;
    }

    private static string StopReason(JsonElement root)
    {
        if (root.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            return reason.GetString() ?? "";

        return "";
    }

    ///<summary>The text of a finished reply, said as cut off when the cap is what finished it.</summary>
    private static LlmResult Answered(string text, string reason)
    {
        if (text.Length == 0 && reason == "max_tokens")
            return LlmResult.Failure("The model ran into its token limit before it wrote anything. Raise Max tokens for this model under AI settings.");
        else if (text.Length == 0)
            return LlmResult.Failure("Empty response from the model.");
        else if (reason == "max_tokens")
            return LlmResult.Success(text + Environment.NewLine + Environment.NewLine + "(Cut off at the token limit.)");

        return LlmResult.Success(text);
    }

    #region Tool use

    ///<summary>Builds the Anthropic "tools" array (name + description + input_schema) from the tool list.</summary>
    public static JsonArray BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            JsonNode schema;

            if (string.IsNullOrWhiteSpace(tool.InputSchemaJson))
                schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            else
                schema = JsonNode.Parse(tool.InputSchemaJson);

            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = schema
            });
        }

        return array;
    }

    ///<summary>Extracts the tool_use blocks the model emitted from a response's content array (empty when it answered).</summary>
    public static List<LlmToolCall> ParseToolUses(JsonElement content)
    {
        var calls = new List<LlmToolCall>();

        if (content.ValueKind != JsonValueKind.Array)
            return calls;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var t) || t.GetString() != "tool_use")
                continue;

            if (!block.TryGetProperty("name", out var nameEl))
                continue;

            string id = null;

            if (block.TryGetProperty("id", out var idEl))
                id = idEl.GetString();

            var input = "{}";

            if (block.TryGetProperty("input", out var inp))
                input = inp.GetRawText();

            calls.Add(new LlmToolCall { Id = id, Name = nameEl.GetString(), ArgumentsJson = input });
        }

        return calls;
    }

    ///<summary>
    ///What one call answered, as a <c>tool_result</c> block: marked <c>is_error</c> when the call was refused or failed,
    ///so the model tries another way rather than building on the refusal's words as if they were data (2026-10-14). A
    ///picture goes in beside the words, which the Messages API takes inside a tool's result, so the model sees it as the
    ///answer to the call that asked for it rather than as something the person said (2026-11-01).
    ///</summary>
    public static JsonObject ToolResult(string id, LlmToolOutput output)
    {
        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id
        };

        var text = output?.Text ?? "";

        if (output?.Png == null)
            result["content"] = text;
        else
        {
            result["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = output.Png
                    }
                }
            };
        }

        if (output != null && output.IsError)
            result["is_error"] = true;

        return result;
    }

    ///<summary>The text blocks of a reply's content, joined.</summary>
    public static string TextOf(JsonElement content)
    {
        var sb = new StringBuilder();

        if (content.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());

        return sb.ToString().Trim();
    }

    protected override JsonArray ToolsOf(IReadOnlyList<LlmTool> tools)
    {
        return BuildToolsArray(tools);
    }

    //Marked for the cache: the next round reads all of this back.
    protected override string ToolRoundBody(string systemPrompt, JsonArray messages, JsonArray tools)
    {
        return BuildRequestBody(_model, _maxTokens, systemPrompt, messages, tools, true, _effort, _fast);
    }

    protected override ToolRound ReadToolRound(JsonElement root, JsonArray messages)
    {
        if (StopReason(root) == "refusal")
            return ToolRound.End(LlmResult.Failure("The model declined to answer (refusal)."));

        if (!root.TryGetProperty("content", out var content))
            return ToolRound.End(LlmResult.Failure("Anthropic answered in a shape this app does not know."));

        var toolUses = ParseToolUses(content);

        if (toolUses.Count == 0)
            return ToolRound.End(Answered(TextOf(content), StopReason(root)));

        //A call cut off at the cap has half its arguments, and running it would be running a guess.
        if (StopReason(root) == "max_tokens")
            return ToolRound.End(LlmResult.Failure($"The model ran into its limit of {_maxTokens} tokens partway through a call, so the call was not run. Raise Max tokens for this model under AI settings."));

        //Its reply goes back as it came, tool_use blocks and all, followed by an answer to each call.
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });

        return ToolRound.Run(toolUses);
    }

    protected override void AddToolResults(JsonArray messages, IReadOnlyList<(LlmToolCall Call, LlmToolOutput Output)> ran)
    {
        var results = new JsonArray();

        foreach (var (call, output) in ran)
            results.Add(ToolResult(call.Id, output));

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
    }

    #endregion
}
