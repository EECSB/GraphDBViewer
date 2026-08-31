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
///thin. Supports an agentic tool-use loop (function calling) via <see cref="IToolUsingLlmProvider"/>.
///</summary>
public class OpenAiProvider : ILlmProvider, IToolUsingLlmProvider
{
    ///<summary>Safety cap on the tool-use loop so a model that keeps calling tools can't spin forever.</summary>
    public const int MaxToolRounds = 6;

    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    ///<summary>Google Gemini's OpenAI-compatible endpoint — free-tier keys come from Google AI Studio.</summary>
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly double? _temperature;

    ///<summary>Reasoning effort, or null to send none. Only OpenAI reasoning models accept it.</summary>
    private readonly string _effort;

    public OpenAiProvider(HttpClient http, LlmConnection connection)
    {
        _http = http;
        _apiKey = connection.ApiKey;
        _model = connection.Model;
        _temperature = connection.Temperature;
        _effort = connection.Effort;
        _endpoint = ChatCompletionsUrl(connection.BaseUrl, DefaultBaseUrlFor(connection.ProviderType));
    }

    ///<summary>The endpoint used when a connection leaves its Base URL blank, chosen by provider type.</summary>
    public static string DefaultBaseUrlFor(string providerType)
    {
        if (providerType == "Gemini")
            return GeminiBaseUrl;

        return DefaultBaseUrl;
    }

    ///<summary>Resolves the /chat/completions URL from a base URL, falling back to the provider's default.</summary>
    public static string ChatCompletionsUrl(string baseUrl, string defaultBaseUrl = DefaultBaseUrl)
    {
        string root;

        if (string.IsNullOrWhiteSpace(baseUrl))
            root = defaultBaseUrl;
        else
            root = baseUrl.TrimEnd('/');

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
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = ToWireMessages(system, conversation)
        };

        if (temperature.HasValue)
            body["temperature"] = temperature.Value;

        //Only sent when the caller asked for one. A reasoning model reads it and a chat model
        //rejects it, which is why the caller decides rather than this guessing from the model name.
        if (!string.IsNullOrEmpty(effort))
            body["reasoning_effort"] = effort;

        return JsonSerializer.Serialize(body);
    }

    ///<summary>
    ///The conversation in the chat-completions shape, led by the system turn.
    ///
    ///Empty turns are dropped rather than sent: a turn with nothing in it is what a canceled or failed
    ///generation leaves behind, and some OpenAI-compatible servers reject empty content outright.
    ///</summary>
    public static List<object> ToWireMessages(string system, IReadOnlyList<LlmMessage> conversation)
    {
        var messages = new List<object> { new { role = "system", content = system } };

        if (conversation == null)
            return messages;

        foreach (var turn in conversation)
        {
            if (string.IsNullOrWhiteSpace(turn?.Text))
                continue;

            messages.Add(new { role = turn.Role, content = turn.Text });
        }

        return messages;
    }

    ///<summary>Extracts choices[0].message.content, or surfaces an API error.</summary>
    public static LlmResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            //Some OpenAI-compatible servers (notably Gemini) wrap errors in a top-level array: [{ "error": {...} }].
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];

            if (root.TryGetProperty("error", out var err))
            {
                string message = err.ToString();

                if (err.TryGetProperty("message", out var m) && m.GetString() is string s)
                    message = s;

                return LlmResult.Failure(message);
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];

                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                {
                    var text = (content.GetString() ?? "").Trim();

                    if (text.Length == 0)
                        return LlmResult.Failure("Empty response from the model.");

                    return LlmResult.Success(text);
                }
            }

            return LlmResult.Failure("Unexpected response shape from the model.");
        }
        catch (Exception ex)
        {
            return LlmResult.Failure($"Could not parse model response: {ex.Message}");
        }
    }

    public Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        return CompleteAsync(systemPrompt, LlmMessage.One(userPrompt), ct);
    }

    public async Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        try
        {
            var body = BuildRequestBody(_model, systemPrompt, conversation, _temperature, _effort);
            var json = await PostAsync(body, ct);

            return ParseResponse(json);
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

    private async Task<string> PostAsync(string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Add("Authorization", $"Bearer {_apiKey}");

        var resp = await _http.SendAsync(req, ct);

        return await resp.Content.ReadAsStringAsync(ct);
    }

    #region Tool use

    ///<summary>Builds the OpenAI "tools" array from the provider-agnostic tool list.</summary>
    public static List<object> BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var arr = new List<object>();

        foreach (var t in tools)
        {
            JsonNode parameters;

            if (string.IsNullOrWhiteSpace(t.InputSchemaJson))
                parameters = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}");
            else
                parameters = JsonNode.Parse(t.InputSchemaJson);

            arr.Add(new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters }
            });
        }

        return arr;
    }

    ///<summary>Serializes a full tool-use request: the running message list plus the tool definitions.</summary>
    public static string BuildRequestBodyWithTools(string model, IReadOnlyList<object> messages, IReadOnlyList<object> tools, double? temperature, string effort = null)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = tools
        };

        if (temperature.HasValue)
            body["temperature"] = temperature.Value;

        //Same as the plain path: the tool loop is where a reasoning model earns its effort, so it
        //must carry the level too or choosing one would quietly do nothing.
        if (!string.IsNullOrEmpty(effort))
            body["reasoning_effort"] = effort;

        return JsonSerializer.Serialize(body);
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

            string args = "{}";

            if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                args = argsEl.GetString();

            calls.Add(new LlmToolCall { Id = id, Name = nameEl.GetString(), ArgumentsJson = args });
        }

        return calls;
    }

    ///<summary>Rebuilds the assistant message (content + tool_calls) so it can be echoed into the next request.</summary>
    private static object EchoAssistantMessage(JsonElement msg)
    {
        string content = null;

        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            content = c.GetString();

        JsonNode toolCalls = null;

        if (msg.TryGetProperty("tool_calls", out var tc))
            toolCalls = JsonNode.Parse(tc.GetRawText());

        return new { role = "assistant", content, tool_calls = toolCalls };
    }

    public Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, string userPrompt, ILlmToolRunner tools, CancellationToken ct)
    {
        return CompleteWithToolsAsync(systemPrompt, LlmMessage.One(userPrompt), tools, ct);
    }

    public async Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, ILlmToolRunner tools, CancellationToken ct)
    {
        try
        {
            var toolsArray = BuildToolsArray(tools.Tools);
            var messages = ToWireMessages(systemPrompt, conversation);

            for (int round = 0; round < MaxToolRounds; round++)
            {
                var body = BuildRequestBodyWithTools(_model, messages, toolsArray, _temperature, _effort);
                var json = await PostAsync(body, ct);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    root = root[0];

                if (root.TryGetProperty("error", out var err))
                {
                    string message = err.ToString();

                    if (err.TryGetProperty("message", out var m) && m.GetString() is string s)
                        message = s;

                    return LlmResult.Failure(message);
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    return LlmResult.Failure("Unexpected response shape from the model.");

                var msg = choices[0].GetProperty("message");
                var calls = ParseToolCalls(msg);

                if (calls.Count == 0)
                {
                    string text = "";

                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        text = content.GetString().Trim();

                    if (text.Length == 0)
                        return LlmResult.Failure("Empty response from the model.");

                    return LlmResult.Success(text);
                }

                //The model wants to call tools: echo its message, run each tool, feed the results back.
                messages.Add(EchoAssistantMessage(msg));

                foreach (var call in calls)
                {
                    var result = await tools.RunToolAsync(call.Name, call.ArgumentsJson, ct);
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = result ?? "" });
                }
            }

            return LlmResult.Failure($"The model kept calling tools without answering (stopped after {MaxToolRounds} rounds).");
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

    #endregion
}
