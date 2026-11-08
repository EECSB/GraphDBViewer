using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class LlmProviderTests
{
    //── Anthropic model resolution ──────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Anthropic_ResolveModel_BlankFallsBackToTheDefault(string model)
    {
        //The connection form leaves Anthropic's model optional and shows DefaultModel as the placeholder,
        //so a blank field has to run exactly that — anything else bills the user for a model they were
        //never shown.
        var connection = new LlmConnection { ProviderType = "Anthropic", Model = model };

        Assert.Equal(AnthropicProvider.DefaultModel, AnthropicProvider.ResolveModel(connection));
    }

    [Fact]
    public void Anthropic_ResolveModel_KeepsAnExplicitChoice()
    {
        var connection = new LlmConnection { ProviderType = "Anthropic", Model = "claude-haiku-4-5" };

        Assert.Equal("claude-haiku-4-5", AnthropicProvider.ResolveModel(connection));
    }

    //── Anthropic output cap ────────────────────────────────────────────

    [Fact]
    public void Anthropic_ResolveMaxTokens_KeepsTheUsersChoice()
    {
        var connection = new LlmConnection { ProviderType = "Anthropic", MaxTokens = 200 };

        Assert.Equal(200, AnthropicProvider.ResolveMaxTokens(connection));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Anthropic_ResolveMaxTokens_NonPositiveFallsBackToTheDefault(int maxTokens)
    {
        //The Messages API rejects a non-positive max_tokens, so it can't be sent as-is.
        var connection = new LlmConnection { ProviderType = "Anthropic", MaxTokens = maxTokens };

        Assert.Equal(LlmConnection.DefaultMaxTokens, AnthropicProvider.ResolveMaxTokens(connection));
    }

    [Fact]
    public void NewConnection_HasRoomForMoreThanASingleShortAnswer()
    {
        //The cap is a ceiling, not an allowance — too low truncates the answer rather than costing less,
        //and a tool-using model spends it on reasoning and tool calls as well as the query.
        Assert.Equal(LlmConnection.DefaultMaxTokens, new LlmConnection().MaxTokens);
        Assert.True(LlmConnection.DefaultMaxTokens >= 8192);
    }

    //── Anthropic request building ──────────────────────────────────────

    [Fact]
    public void Anthropic_BuildRequestBody_HasModelMaxTokensSystemAndUserMessage()
    {
        var json = AnthropicProvider.BuildRequestBody("claude-haiku-4-5", 512, "SYS", "find products");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYS", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("find products", messages[0].GetProperty("content").GetString());
    }

    //A system prompt with nothing in it is no prompt, so the body carries no system field rather than a null the API
    //refuses: every caller in the core passes words, so this is a plugin's edge (the audit of 2026-11-07).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Anthropic_ASystemPromptWithNothingInIt_IsLeftOutOfTheBody(string system)
    {
        using var doc = JsonDocument.Parse(AnthropicProvider.BuildRequestBody("claude-haiku-4-5", 512, system, "find products"));
        using var streamed = JsonDocument.Parse(AnthropicProvider.BuildStreamingRequestBody("claude-haiku-4-5", 512, system, LlmMessage.One("find products")));

        Assert.False(doc.RootElement.TryGetProperty("system", out _));
        Assert.Equal("find products", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(streamed.RootElement.TryGetProperty("system", out _));
    }

    [Fact]
    public void Anthropic_ParseResponse_ConcatenatesTextBlocks()
    {
        var json = @"{""content"":[{""type"":""text"",""text"":""g.V()""},{""type"":""text"",""text"":"".limit(5)""}],""stop_reason"":""end_turn""}";
        var result = AnthropicProvider.ParseResponse(json);

        Assert.False(result.IsError);
        Assert.Equal("g.V().limit(5)", result.Text);
    }

    [Fact]
    public void Anthropic_ParseResponse_SurfacesApiError()
    {
        var json = @"{""type"":""error"",""error"":{""type"":""authentication_error"",""message"":""invalid x-api-key""}}";
        var result = AnthropicProvider.ParseResponse(json);

        Assert.True(result.IsError);
        Assert.Equal("invalid x-api-key", result.Error);
    }

    [Fact]
    public void Anthropic_ParseResponse_RefusalIsError()
    {
        var json = @"{""content"":[],""stop_reason"":""refusal""}";
        var result = AnthropicProvider.ParseResponse(json);

        Assert.True(result.IsError);
    }

    //── OpenAI-compatible request building ──────────────────────────────

    [Fact]
    public void OpenAi_BuildRequestBody_HasSystemAndUserMessages()
    {
        var json = OpenAiProvider.BuildRequestBody("gpt-4o-mini", "SYS", "find products", null);
        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void OpenAi_BuildRequestBody_IncludesTemperatureWhenSet()
    {
        var json = OpenAiProvider.BuildRequestBody("m", "s", "u", 0.2);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0.2, doc.RootElement.GetProperty("temperature").GetDouble(), 3);
    }

    //The same at the chat-completions shape: no system turn leads the conversation when the prompt has nothing in it,
    //rather than one whose content is null (the audit of 2026-11-07).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenAi_ASystemPromptWithNothingInIt_LeadsWithNoSystemTurn(string system)
    {
        var messages = OpenAiProvider.ToWireMessages(system, LlmMessage.One("find products"));

        Assert.Single(messages);
        Assert.Equal("user", messages[0]["role"].GetValue<string>());

        using var doc = JsonDocument.Parse(OpenAiProvider.BuildRequestBody("gpt-4o-mini", system, "find products", null));
        using var streamed = JsonDocument.Parse(OpenAiProvider.BuildStreamingRequestBody("gpt-4o-mini", system, LlmMessage.One("find products"), null));

        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(1, streamed.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void OpenAi_ParseResponse_ExtractsChoiceContent()
    {
        var json = @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""g.V().hasLabel('Product')""}}]}";
        var result = OpenAiProvider.ParseResponse(json);

        Assert.False(result.IsError);
        Assert.Equal("g.V().hasLabel('Product')", result.Text);
    }

    [Fact]
    public void OpenAi_ParseResponse_SurfacesApiError()
    {
        var json = @"{""error"":{""message"":""model not found""}}";
        var result = OpenAiProvider.ParseResponse(json);

        Assert.True(result.IsError);
        Assert.Equal("model not found", result.Error);
    }

    [Theory]
    [InlineData(null, "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    public void OpenAi_ChatCompletionsUrl_ResolvesEndpoint(string baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAiProvider.ChatCompletionsUrl(baseUrl));
    }

    //── Factory ─────────────────────────────────────────────────────────

    [Fact]
    public void Factory_PicksProviderByType()
    {
        using var http = new HttpClient();

        Assert.IsType<AnthropicProvider>(LlmProviderFactory.Create(http, new LlmConnection { ProviderType = "Anthropic" }));
        Assert.IsType<OpenAiProvider>(LlmProviderFactory.Create(http, new LlmConnection { ProviderType = "OpenAI" }));
        Assert.IsType<OpenAiProvider>(LlmProviderFactory.Create(http, new LlmConnection { ProviderType = "Custom" }));
        Assert.IsType<OpenAiProvider>(LlmProviderFactory.Create(http, new LlmConnection { ProviderType = "Gemini" }));
    }

    //── Gemini (OpenAI-compatible) ──────────────────────────────────────

    [Theory]
    [InlineData("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("OpenAI", "https://api.openai.com/v1")]
    [InlineData("Custom", null)]
    public void OpenAi_DefaultBaseUrlFor_PicksGeminiOrOpenAi_AndNoneForAServerOfOnesOwn(string providerType, string expected)
    {
        Assert.Equal(expected, OpenAiProvider.DefaultBaseUrlFor(providerType));
    }

    //A server of one's own ("Other") has no address but the one it is given. Left blank, the person's key and their
    //question went to OpenAI's, a third party's, which the transport sweep of 2026-10-03 found. Nothing is sent now,
    //and the answer says why.
    [Fact]
    public async Task OpenAi_AServerOfOnesOwnWithNoAddress_IsSentNothing_AndTheAnswerSaysWhy()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http, new LlmConnection { ProviderType = "Custom", ApiKey = "a-local-key", Model = "m" });

        var answered = await provider.CompleteAsync("system", "question", CancellationToken.None);
        var streamed = await provider.StreamAsync("system", LlmMessage.One("question"), null, CancellationToken.None);

        Assert.True(answered.IsError);
        Assert.Equal(OpenAiProvider.NoAddress, answered.Error);
        Assert.Equal(OpenAiProvider.NoAddress, streamed.Error);
        Assert.Empty(handler.Sent);
        Assert.Null(OpenAiProvider.ChatCompletionsUrl(" ", null));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request.RequestUri);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""g.V()""}}]}") });
        }
    }

    [Fact]
    public void OpenAi_ChatCompletionsUrl_BlankBaseUrlUsesGeminiDefault()
    {
        var url = OpenAiProvider.ChatCompletionsUrl(null, OpenAiProvider.GeminiBaseUrl);

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", url);
    }

    [Fact]
    public void OpenAi_ParseResponse_UnwrapsGeminiArrayWrappedError()
    {
        //Gemini's OpenAI-compat layer returns errors as a top-level array.
        var json = @"[{""error"":{""code"":400,""message"":""Please pass a valid API key"",""status"":""INVALID_ARGUMENT""}}]";
        var result = OpenAiProvider.ParseResponse(json);

        Assert.True(result.IsError);
        Assert.Equal("Please pass a valid API key", result.Error);
    }

    //── Tool use ────────────────────────────────────────────────────────

    [Fact]
    public void OpenAi_ParseToolCalls_ExtractsCalls()
    {
        var msg = JsonDocument.Parse(@"{
            ""role"":""assistant"",
            ""tool_calls"":[
                {""id"":""call_1"",""type"":""function"",""function"":{""name"":""run_read_query"",""arguments"":""{\""query\"":\""g.V().limit(1)\""}""}}
            ]
        }").RootElement;

        var calls = OpenAiProvider.ParseToolCalls(msg);

        Assert.Single(calls);
        Assert.Equal("call_1", calls[0].Id);
        Assert.Equal("run_read_query", calls[0].Name);
        Assert.Contains("g.V().limit(1)", calls[0].ArgumentsJson);
    }

    [Fact]
    public void OpenAi_ParseToolCalls_NoToolCallsIsEmpty()
    {
        var msg = JsonDocument.Parse(@"{""role"":""assistant"",""content"":""g.V().count()""}").RootElement;

        Assert.Empty(OpenAiProvider.ParseToolCalls(msg));
    }

    [Fact]
    public void OpenAi_BuildToolsArray_ProducesFunctionShape()
    {
        var tools = new List<LlmTool>
        {
            new() { Name = "run_read_query", Description = "Run a read-only query", InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}" }
        };

        var json = OpenAiProvider.BuildRequestBody("gemini-2.5-flash", OpenAiProvider.ToWireMessages("sys", LlmMessage.One("hi")), OpenAiProvider.BuildToolsArray(tools), null);
        using var doc = JsonDocument.Parse(json);
        var tool0 = doc.RootElement.GetProperty("tools")[0];

        Assert.Equal("function", tool0.GetProperty("type").GetString());
        Assert.Equal("run_read_query", tool0.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("string", tool0.GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
    }

    //── Tool use — Anthropic ────────────────────────────────────────────

    [Fact]
    public void Anthropic_ParseToolUses_ExtractsToolUseBlocks()
    {
        //Anthropic's tool_use input is an object, not a JSON string.
        var content = JsonDocument.Parse(@"[
            {""type"":""text"",""text"":""Let me check.""},
            {""type"":""tool_use"",""id"":""toolu_1"",""name"":""run_read_query"",""input"":{""query"":""g.V().count()""}}
        ]").RootElement;

        var calls = AnthropicProvider.ParseToolUses(content);

        Assert.Single(calls);
        Assert.Equal("toolu_1", calls[0].Id);
        Assert.Equal("run_read_query", calls[0].Name);
        Assert.Contains("g.V().count()", calls[0].ArgumentsJson);
    }

    [Fact]
    public void Anthropic_ParseToolUses_TextOnlyIsEmpty()
    {
        var content = JsonDocument.Parse(@"[{""type"":""text"",""text"":""g.V().count()""}]").RootElement;

        Assert.Empty(AnthropicProvider.ParseToolUses(content));
    }

    [Fact]
    public void Anthropic_BuildToolsArray_UsesInputSchemaShape()
    {
        var tools = new List<LlmTool>
        {
            new() { Name = "run_read_query", Description = "Run a read-only query", InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}" }
        };

        var json = AnthropicProvider.BuildRequestBody("claude-haiku-4-5", 1024, "sys", AnthropicProvider.ToWireMessages(LlmMessage.One("hi")), AnthropicProvider.BuildToolsArray(tools));
        using var doc = JsonDocument.Parse(json);
        var tool0 = doc.RootElement.GetProperty("tools")[0];

        Assert.Equal("run_read_query", tool0.GetProperty("name").GetString());
        Assert.Equal("object", tool0.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("string", tool0.GetProperty("input_schema").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
    }

    //Effort is only meaningful to OpenAI reasoning models, so the body carries it only when a caller
    //asked for one. A chat model rejects reasoning_effort rather than ignoring it.
    [Fact]
    public void OpenAiBody_CarriesTheEffortWhenOneIsChosen()
    {
        var body = OpenAiProvider.BuildRequestBody("o3", "sys", "user", null, "high");

        Assert.Contains("\"reasoning_effort\":\"high\"", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OpenAiBody_SaysNothingAboutEffortWhenNoneIsChosen(string effort)
    {
        var body = OpenAiProvider.BuildRequestBody("gpt-4o", "sys", "user", null, effort);

        Assert.DoesNotContain("reasoning_effort", body);
    }

    //The tool loop is where a reasoning model earns the setting, so it must carry it too.
    [Fact]
    public void OpenAiToolBody_CarriesTheEffortToo()
    {
        var body = OpenAiProvider.BuildRequestBody("o3", new JsonArray(), new JsonArray(), null, "low");

        Assert.Contains("\"reasoning_effort\":\"low\"", body);
    }

    [Theory]
    [InlineData("OpenAI", "o1", true)]
    [InlineData("OpenAI", "o3", true)]
    [InlineData("OpenAI", "o4-mini", true)]
    [InlineData("OpenAI", "gpt-5-mini", true)]
    [InlineData("OpenAI", "gpt-4o", false)]
    [InlineData("OpenAI", "", false)]
    [InlineData("Anthropic", "o3", false)]
    [InlineData("Gemini", "o3", false)]
    //Google's compatibility layer takes the same field, on the models that think: 2.5 and up.
    [InlineData("Gemini", "gemini-2.5-flash", true)]
    [InlineData("Gemini", "gemini-3-pro-preview", true)]
    [InlineData("Gemini", "gemini-2.0-flash", false)]
    [InlineData("Custom", "gemini-2.5-flash", false)]
    public void EffortIsOfferedOnlyWhereItIsUnderstood(string provider, string model, bool expected)
    {
        var connection = new LlmConnection { ProviderType = provider, Model = model };

        Assert.Equal(expected, LlmConnection.SupportsEffort(connection));
    }
}
