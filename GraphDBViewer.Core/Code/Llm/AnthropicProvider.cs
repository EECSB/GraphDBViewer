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
///pure and unit-tested; the HTTP send is thin (mirrors the GremlinDB / GremlinQueryBuilder split). Supports
///an agentic tool-use loop (Anthropic's tool_use / tool_result format) via <see cref="IToolUsingLlmProvider"/>.
///</summary>
public class AnthropicProvider : ILlmProvider, IToolUsingLlmProvider
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string DefaultModel = "claude-opus-4-8";

    ///<summary>Safety cap on the tool-use loop so a model that keeps calling tools can't spin forever.</summary>
    public const int MaxToolRounds = 6;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;

    public AnthropicProvider(HttpClient http, LlmConnection connection)
    {
        _http = http;
        _apiKey = connection.ApiKey;

        _maxTokens = ResolveMaxTokens(connection);
        _model = ResolveModel(connection);
    }

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

        return connection.Model;
    }

    ///<summary>Builds the Messages API request body (model + max_tokens + system + a single user message).</summary>
    public static string BuildRequestBody(string model, int maxTokens, string system, string user)
    {
        return BuildRequestBody(model, maxTokens, system, LlmMessage.One(user));
    }

    ///<summary>The same body over a whole conversation, which is what a follow-up question needs sent.</summary>
    public static string BuildRequestBody(string model, int maxTokens, string system, IReadOnlyList<LlmMessage> conversation)
    {
        var body = new
        {
            model,
            max_tokens = maxTokens,
            system,
            messages = ToWireMessages(conversation)
        };

        return JsonSerializer.Serialize(body);
    }

    ///<summary>
    ///The conversation in the Messages API's own shape.
    ///
    ///Empty turns are dropped rather than sent: the API rejects an empty content string, and a turn with
    ///nothing in it is what a canceled or failed generation leaves behind, so a conversation carrying one
    ///would fail every request made after it until it was cleared.
    ///</summary>
    public static List<object> ToWireMessages(IReadOnlyList<LlmMessage> conversation)
    {
        var messages = new List<object>();

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

    ///<summary>Extracts the concatenated text blocks, or surfaces an API error / refusal.</summary>
    public static LlmResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                string message = err.ToString();

                if (err.TryGetProperty("message", out var m) && m.GetString() is string s)
                    message = s;

                return LlmResult.Failure(message);
            }

            if (root.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "refusal")
                return LlmResult.Failure("The model declined to answer (refusal).");

            var sb = new StringBuilder();

            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                }
            }

            var text = sb.ToString().Trim();

            if (text.Length == 0)
                return LlmResult.Failure("Empty response from the model.");

            return LlmResult.Success(text);
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
            var body = BuildRequestBody(_model, _maxTokens, systemPrompt, conversation);
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
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        //Required for CORS when calling the Anthropic API directly from a browser.
        req.Headers.Add("anthropic-dangerous-direct-browser-access", "true");

        var resp = await _http.SendAsync(req, ct);

        return await resp.Content.ReadAsStringAsync(ct);
    }

    #region Tool use

    ///<summary>Builds the Anthropic "tools" array (name + description + input_schema) from the tool list.</summary>
    public static List<object> BuildToolsArray(IReadOnlyList<LlmTool> tools)
    {
        var arr = new List<object>();

        foreach (var t in tools)
        {
            JsonNode schema;

            if (string.IsNullOrWhiteSpace(t.InputSchemaJson))
                schema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}");
            else
                schema = JsonNode.Parse(t.InputSchemaJson);

            arr.Add(new { name = t.Name, description = t.Description, input_schema = schema });
        }

        return arr;
    }

    ///<summary>Serializes a full tool-use request: system + running message list + the tool definitions.</summary>
    public static string BuildRequestBodyWithTools(string model, int maxTokens, string system, IReadOnlyList<object> messages, IReadOnlyList<object> tools)
    {
        var body = new
        {
            model,
            max_tokens = maxTokens,
            system,
            messages,
            tools
        };

        return JsonSerializer.Serialize(body);
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

            string input = "{}";

            if (block.TryGetProperty("input", out var inp))
                input = inp.GetRawText();

            calls.Add(new LlmToolCall { Id = id, Name = nameEl.GetString(), ArgumentsJson = input });
        }

        return calls;
    }

    ///<summary>Concatenates the text blocks of a content array.</summary>
    private static string ExtractText(JsonElement content)
    {
        var sb = new StringBuilder();

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
            }
        }

        return sb.ToString().Trim();
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
            var messages = ToWireMessages(conversation);

            for (int round = 0; round < MaxToolRounds; round++)
            {
                var body = BuildRequestBodyWithTools(_model, _maxTokens, systemPrompt, messages, toolsArray);
                var json = await PostAsync(body, ct);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                {
                    string message = err.ToString();

                    if (err.TryGetProperty("message", out var m) && m.GetString() is string s)
                        message = s;

                    return LlmResult.Failure(message);
                }

                if (root.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "refusal")
                    return LlmResult.Failure("The model declined to answer (refusal).");

                if (!root.TryGetProperty("content", out var content))
                    return LlmResult.Failure("Unexpected response shape from the model.");

                var toolUses = ParseToolUses(content);

                if (toolUses.Count == 0)
                {
                    var text = ExtractText(content);

                    if (text.Length == 0)
                        return LlmResult.Failure("Empty response from the model.");

                    return LlmResult.Success(text);
                }

                //The model wants to call tools: echo its content, then return a tool_result for each.
                messages.Add(new { role = "assistant", content = JsonNode.Parse(content.GetRawText()) });

                var results = new List<object>();

                foreach (var call in toolUses)
                {
                    var result = await tools.RunToolAsync(call.Name, call.ArgumentsJson, ct);
                    results.Add(new { type = "tool_result", tool_use_id = call.Id, content = result ?? "" });
                }

                messages.Add(new { role = "user", content = results });
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
