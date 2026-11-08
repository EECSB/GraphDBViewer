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
///Calls any OpenAI-compatible chat-completions endpoint directly from the browser — OpenAI itself, Google
///Gemini (via its OpenAI-compatibility layer), or a custom server (Ollama, LM Studio, vLLM, LocalAI, Azure
///OpenAI, self-hosted). Request building and response parsing are pure and unit-tested; the HTTP send is
///thin, with an agentic tool-use loop (function calling).
///
///<strong>Brought up to GDS Viewer's port of it on 2026-11-01</strong>: what each answer was billed for is kept as it
///arrives (<see cref="Spent"/>), a tool's picture follows its tool turn in a user turn of its own, since chat completions
///take an image only from the user, a run that gets nowhere is stopped and a long one paused, a call cut off at the
///length limit is not run, and the bodies are built as JSON nodes.
///
///Sending, billing, streaming and the tool loop are <see cref="LlmProviderBase"/>'s, shared with Anthropic since
///2026-11-07; this class says what chat completions' wire is.
///</summary>
public class OpenAiProvider : LlmProviderBase
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    ///<summary>What a server of one's own answers when it was given no address, and so is sent nothing.</summary>
    public const string NoAddress = "This model is a server of your own and has no address to send to: give it its base URL in the model's settings.";

    ///<summary>Google Gemini's OpenAI-compatible endpoint — free-tier keys come from Google AI Studio.</summary>
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly double? _temperature;
    private readonly string _providerType;

    ///<summary>Reasoning effort, or empty to send none. Only reasoning models accept it.</summary>
    private readonly string _effort;

    ///<summary>Who answered, as a failure names them (<see cref="LabelFor"/>).</summary>
    private readonly string _label;

    public OpenAiProvider(HttpClient http, LlmConnection connection) : base(http, connection)
    {
        _apiKey = (connection.ApiKey ?? "").Trim();
        _model = (connection.Model ?? "").Trim();
        _temperature = connection.Temperature;
        _effort = connection.Effort ?? "";
        _providerType = connection.ProviderType;
        _endpoint = ChatCompletionsUrl(connection.BaseUrl, DefaultBaseUrlFor(connection.ProviderType));
        _label = LabelFor(connection.ProviderType, _endpoint);
    }

    public override string Model => _model;

    protected override string Label => _label;

    ///<summary>
    ///Who answered, as a failure names them: OpenAI or Gemini by name, and a server of one's own by its host, which is
    ///what somebody running one recognizes — "localhost answered 404 Not Found: model 'x' not found".
    ///</summary>
    public static string LabelFor(string providerType, string endpoint)
    {
        if (providerType == LlmConnection.Gemini)
            return "Gemini";
        else if (providerType == LlmConnection.OpenAI)
            return "OpenAI";

        if (endpoint != null && Uri.TryCreate(endpoint, UriKind.Absolute, out var address))
            return address.Host;

        return "The server";
    }

    ///<summary>
    ///The endpoint used when a connection leaves its Base URL blank, chosen by provider type; none for a server of
    ///one's own ("Custom"), whose address is only ever the one it is given. Until 2026-10-03 that one fell back to
    ///OpenAI's too, so a blank field sent the person's key for their own server, and their question, to OpenAI.
    ///</summary>
    public static string DefaultBaseUrlFor(string providerType)
    {
        if (providerType == LlmConnection.Gemini)
            return GeminiBaseUrl;
        else if (providerType == LlmConnection.Custom)
            return null;

        return DefaultBaseUrl;
    }

    ///<summary>
    ///Resolves the /chat/completions URL from a base URL, falling back to the provider's default; null when there is
    ///neither, and the request is then refused with <see cref="NoAddress"/> rather than sent anywhere.
    ///</summary>
    public static string ChatCompletionsUrl(string baseUrl, string defaultBaseUrl = DefaultBaseUrl)
    {
        string root;

        if (string.IsNullOrWhiteSpace(baseUrl))
            root = defaultBaseUrl;
        else
            root = baseUrl.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(root))
            return null;

        return $"{root}/chat/completions";
    }

    ///<summary>Builds the chat-completions request body (model + system/user messages + optional temperature).</summary>
    public static string BuildRequestBody(string model, string system, string user, double? temperature, string effort = null)
    {
        return BuildRequestBody(model, system, LlmMessage.One(user), temperature, effort);
    }

    ///<summary>The same body over a whole conversation, which is what a follow-up question needs sent.</summary>
    public static string BuildRequestBody(string model, string system, IReadOnlyList<LlmMessage> conversation, double? temperature, string effort = null)
    {
        return BuildRequestBody(model, ToWireMessages(system, conversation), null, temperature, effort);
    }

    ///<summary>
    ///A request: the model, the conversation so far, the tools when there are any, a temperature when the connection has
    ///one, and an effort when one was chosen.
    ///
    ///<strong>The effort only when asked for.</strong> A reasoning model reads it and a chat model refuses the whole request
    ///over it, so the person decides (<see cref="LlmConnection.SupportsEffort"/>) rather than this guessing from the
    ///model's name. The tool loop is where a reasoning model earns its effort, so it carries the level too.
    ///</summary>
    public static string BuildRequestBody(string model, JsonArray messages, JsonArray tools, double? temperature, string effort = null)
    {
        return BuildBody(model, messages, tools, temperature, effort).ToJsonString();
    }

    ///<summary>The same body again, asking for the answer as it is written rather than when it is finished.</summary>
    public static string BuildStreamingRequestBody(string model, string system, IReadOnlyList<LlmMessage> conversation, double? temperature, string effort = null)
    {
        var body = BuildBody(model, ToWireMessages(system, conversation), null, temperature, effort);

        body["stream"] = true;

        //What the answer was billed for comes in a last chunk of its own before [DONE], and only when asked for.
        body["stream_options"] = new JsonObject { ["include_usage"] = true };

        return body.ToJsonString();
    }

    private static JsonObject BuildBody(string model, JsonArray messages, JsonArray tools, double? temperature, string effort)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone()
        };

        if (tools != null && tools.Count > 0)
            body["tools"] = tools.DeepClone();

        if (temperature.HasValue)
            body["temperature"] = temperature.Value;

        if (!string.IsNullOrEmpty(effort))
            body["reasoning_effort"] = effort;

        return body;
    }

    ///<summary>
    ///What one event of a streamed answer carries: a chunk holding <c>choices[0].delta.content</c> is text,
    ///the literal <c>[DONE]</c> is the end, and an <c>error</c> object is the reason it stopped. A chunk
    ///that carries only a role or a finish reason adds nothing; one carrying a <c>usage</c> report, which the
    ///body asks for in a last chunk of its own, says what the answer has been billed for so far.
    ///</summary>
    public static LlmStreamEvent ReadStreamEvent(string data)
    {
        if (data == "[DONE]")
            return LlmStreamEvent.Ended;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            //Not an object: Google's compatible endpoint wraps an error in an array, [{"error": ...}], mid-stream as it
            //does a whole answer, and that is read the same way; any other non-object adds nothing.
            if (root.ValueKind != JsonValueKind.Object)
            {
                if (LlmProviderFactory.ErrorIn(data) is string wrapped)
                    return LlmStreamEvent.Failed(wrapped);

                return LlmStreamEvent.Nothing;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ToString();

                if (error.TryGetProperty("message", out var m) && m.GetString() is string reported)
                    message = reported;

                return LlmStreamEvent.Failed(message);
            }

            var billed = LlmUsage.FromOpenAi(root);

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return LlmStreamEvent.Nothing.Billed(billed);

            if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return LlmStreamEvent.More(content.GetString()).Billed(billed);

            return LlmStreamEvent.Nothing.Billed(billed);
        }
        catch (JsonException)
        {
            //A line that is not JSON is not an answer, as one that is JSON but not an object is not (above); the stream
            //carries on rather than failing on either.
            return LlmStreamEvent.Nothing;
        }
    }

    ///<summary>
    ///The conversation in the chat-completions shape, led by the system turn when there is a prompt for it: a prompt with
    ///nothing in it is no prompt, and no turn is sent for it rather than one whose content is null.
    ///
    ///Empty turns are dropped rather than sent: a turn with nothing in it is what a canceled or failed
    ///generation leaves behind, and some OpenAI-compatible servers reject empty content outright.
    ///
    ///A question with files is a list of parts, the files ahead of its words, written for the provider it goes to, which
    ///decides whether a PDF can be one of them (<see cref="AiAttachment.SentTo"/>).
    ///</summary>
    public static JsonArray ToWireMessages(string system, IReadOnlyList<LlmMessage> conversation, string providerType = LlmConnection.OpenAI)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        return AddTurns(messages, conversation, turn => ContentWithFiles(turn, providerType));
    }

    ///<summary>A question's files as parts ahead of its words: a picture as an image_url, a PDF as a file, a text file as text between tags.</summary>
    public static JsonArray ContentWithFiles(LlmMessage turn, string providerType)
    {
        return FilesThenWords(turn, providerType, Part);
    }

    ///<summary>
    ///One part of a question's files as chat completions take it.
    ///
    ///A picture is a data URL in an image_url part, as a tool's pictures are. A PDF is a file part with its bytes as a
    ///data URL in file_data and its name beside them: OpenAI's documented shape (checked 2026-10-29 for GDS Viewer), where
    ///bare base64 or the name spelled file_name are reported failing. Only ever sent to OpenAI.
    ///</summary>
    public static JsonObject Part(AttachmentPart part)
    {
        if (part.Kind == AttachmentKind.Image)
        {
            return new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{part.MediaType};base64,{part.Base64}" }
            };
        }
        else if (part.Kind == AttachmentKind.Pdf)
        {
            return new JsonObject
            {
                ["type"] = "file",
                ["file"] = new JsonObject
                {
                    ["filename"] = part.Name,
                    ["file_data"] = $"data:application/pdf;base64,{part.Base64}"
                }
            };
        }

        return new JsonObject { ["type"] = "text", ["text"] = part.Text };
    }

    ///<summary>A plain reply read: choices[0].message.content, or what the server said was wrong.</summary>
    public static LlmResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            //Some OpenAI-compatible servers (notably Gemini) wrap errors in a top-level array: [{ "error": {...} }].
            if (LlmProviderFactory.ErrorIn(json) is string said)
                return LlmResult.Failure(said);

            if (FirstChoice(doc.RootElement) is not JsonElement choice || !choice.TryGetProperty("message", out var message))
                return LlmResult.Failure("Unexpected response shape from the model.");

            return Answered(ContentOf(message), FinishReason(choice));
        }
        catch (JsonException ex)
        {
            return LlmResult.Failure($"Could not parse model response: {ex.Message}");
        }
    }

    protected override JsonArray WireMessages(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return ToWireMessages(systemPrompt, conversation, _providerType);
    }

    protected override string WholeBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return BuildRequestBody(_model, ToWireMessages(systemPrompt, conversation, _providerType), null, _temperature, _effort);
    }

    protected override string StreamingBody(string systemPrompt, IReadOnlyList<LlmMessage> conversation)
    {
        return BuildStreamingRequestBody(_model, systemPrompt, conversation, _temperature, _effort);
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
        return LlmUsage.FromOpenAi(root);
    }

    protected override HttpRequestMessage Request(string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (_apiKey.Length > 0)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        return req;
    }

    ///<summary>
    ///Why nothing should be sent, or null when the connection is fit to send with. Every way of asking comes through
    ///here, so nothing is sent with no address to send it to.
    ///</summary>
    protected override string WhyNotSend()
    {
        if (_endpoint == null)
            return NoAddress;

        return null;
    }

    private static JsonElement? FirstChoice(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices))
            return null;

        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;

        return choices[0];
    }

    private static string ContentOf(JsonElement message)
    {
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return (content.GetString() ?? "").Trim();

        return "";
    }

    private static string FinishReason(JsonElement choice)
    {
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            return reason.GetString() ?? "";

        return "";
    }

    ///<summary>The text of a finished reply, said as cut off when the length limit is what finished it.</summary>
    private static LlmResult Answered(string text, string reason)
    {
        if (text.Length == 0 && reason == "length")
            return LlmResult.Failure("The model ran into its length limit before it wrote anything.");
        else if (text.Length == 0)
            return LlmResult.Failure("Empty response from the model.");
        else if (reason == "length")
            return LlmResult.Success(text + Environment.NewLine + Environment.NewLine + "(Cut off at the length limit.)");

        return LlmResult.Success(text);
    }

    #region Tool use

    ///<summary>Builds the OpenAI "tools" array from the provider-agnostic tool list.</summary>
    public static JsonArray BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            JsonNode parameters;

            if (string.IsNullOrWhiteSpace(tool.InputSchemaJson))
                parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            else
                parameters = JsonNode.Parse(tool.InputSchemaJson);

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters
                }
            });
        }

        return array;
    }

    ///<summary>Extracts the tool calls the model requested from an assistant message (empty when it answered).</summary>
    public static List<LlmToolCall> ParseToolCalls(JsonElement message)
    {
        var calls = new List<LlmToolCall>();

        if (!message.TryGetProperty("tool_calls", out var tc) || tc.ValueKind != JsonValueKind.Array)
            return calls;

        foreach (var call in tc.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var nameEl))
                continue;

            string id = null;

            if (call.TryGetProperty("id", out var idEl))
                id = idEl.GetString();

            var args = "{}";

            //A string as the API documents it; an object as some servers of one's own send it anyway.
            if (fn.TryGetProperty("arguments", out var argsEl))
            {
                if (argsEl.ValueKind == JsonValueKind.String)
                    args = argsEl.GetString() ?? "{}";
                else if (argsEl.ValueKind == JsonValueKind.Object)
                    args = argsEl.GetRawText();
            }

            calls.Add(new LlmToolCall { Id = id, Name = nameEl.GetString(), ArgumentsJson = args });
        }

        return calls;
    }

    ///<summary>
    ///What one call answered, as a tool turn. This API has no flag for a call that was refused or failed, as
    ///Anthropic's <c>is_error</c> is, so the text says it first (2026-10-14).
    ///</summary>
    public static JsonObject ToolTurn(string id, LlmToolOutput output)
    {
        var text = output?.Text ?? "";

        if (output != null && output.IsError)
            text = "Refused: " + text;

        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = text
        };
    }

    ///<summary>The pictures a round of calls took, as one user turn after their tool turns, each named by its call.</summary>
    public static JsonObject PictureTurn(IReadOnlyList<(string Tool, string Png)> pictures)
    {
        var parts = new JsonArray();

        foreach (var (tool, png) in pictures)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = $"The picture {tool} took:" });
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + png }
            });
        }

        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    ///<summary>
    ///The assistant's message as it goes back into the next request: its words and its calls.
    ///
    ///<strong>The calls are copied as they came</strong>, not rebuilt from what was read out of them. Gemini 3 hands back
    ///a signature of its thinking inside each call (<c>extra_content</c>) and refuses the next request if it does not come
    ///back with it. Only the role, the content and the calls, though, rather than the whole message: OpenAI's reply carries
    ///fields of its own that its requests do not take.
    ///</summary>
    public static JsonObject EchoAssistantMessage(JsonElement message)
    {
        var echo = new JsonObject { ["role"] = "assistant" };

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            echo["content"] = content.GetString();
        else
            echo["content"] = null;

        if (message.TryGetProperty("tool_calls", out var calls))
            echo["tool_calls"] = JsonNode.Parse(calls.GetRawText());

        return echo;
    }

    protected override JsonArray ToolsOf(IReadOnlyList<LlmTool> tools)
    {
        return BuildToolsArray(tools);
    }

    protected override string ToolRoundBody(string systemPrompt, JsonArray messages, JsonArray tools)
    {
        return BuildRequestBody(_model, messages, tools, _temperature, _effort);
    }

    protected override ToolRound ReadToolRound(JsonElement root, JsonArray messages)
    {
        if (FirstChoice(root) is not JsonElement choice || !choice.TryGetProperty("message", out var message))
            return ToolRound.End(LlmResult.Failure("Unexpected response shape from the model."));

        var calls = ParseToolCalls(message);

        if (calls.Count == 0)
            return ToolRound.End(Answered(ContentOf(message), FinishReason(choice)));

        //A call cut off at the limit has half its arguments, and running it would be running a guess.
        if (FinishReason(choice) == "length")
            return ToolRound.End(LlmResult.Failure("The model ran into its length limit partway through a call, so the call was not run."));

        messages.Add(EchoAssistantMessage(message));

        return ToolRound.Run(calls);
    }

    ///<summary>A tool turn per call, and then, when a call drew any, one user turn with the pictures (<see cref="PictureTurn"/>).</summary>
    protected override void AddToolResults(JsonArray messages, IReadOnlyList<(LlmToolCall Call, LlmToolOutput Output)> ran)
    {
        var pictures = new List<(string Tool, string Png)>();

        foreach (var (call, output) in ran)
        {
            messages.Add(ToolTurn(call.Id, output));

            if (output?.Png is string png)
                pictures.Add((call.Name, png));
        }

        if (pictures.Count > 0)
            messages.Add(PictureTurn(pictures));
    }

    #endregion
}
