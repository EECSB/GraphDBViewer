using System;
using System.Globalization;
namespace GraphDBViewerWeb.Code;

///<summary>
///A saved "AI model" connection used for natural-language → query generation. Stored in localStorage
///alongside the database connections. Bring-your-own-key: the user supplies the provider, endpoint and key.
///</summary>
public class LlmConnection
{
    public LlmConnection() { }

    public LlmConnection(LlmConnection copy)
    {
        Name = copy.Name;
        ProviderType = copy.ProviderType;
        BaseUrl = copy.BaseUrl;
        ApiKey = copy.ApiKey;
        Model = copy.Model;
        MaxTokens = copy.MaxTokens;
        Temperature = copy.Temperature;
    }

    public string Name { get; set; }

    ///<summary>Provider family: "Anthropic" | "OpenAI" | "Gemini" | "Custom". OpenAI, Gemini and Custom share the OpenAI-compatible adapter.</summary>
    public string ProviderType { get; set; } = "Anthropic";

    ///<summary>Base URL for OpenAI-compatible endpoints (e.g. http://localhost:11434/v1 for Ollama). Ignored for Anthropic.</summary>
    public string BaseUrl { get; set; }

    public string ApiKey { get; set; }

    ///<summary>Model id (e.g. claude-opus-4-8, gpt-4o-mini, or a local model name).</summary>
    public string Model { get; set; }

    ///<summary>
    ///Output token cap when the provider needs one (Anthropic sends it; the OpenAI-compatible adapter
    ///doesn't). Generous on purpose: it's a ceiling, not an allocation — you're billed for what the model
    ///actually writes — and a cap that's too low truncates the answer mid-sentence rather than costing less.
    ///The generated query is short, but a tool-using model spends this on its reasoning and tool calls too.
    ///</summary>
    public const int DefaultMaxTokens = 8192;

    public int MaxTokens { get; set; } = DefaultMaxTokens;

    ///<summary>Optional sampling temperature; null omits it (some newer models reject a non-default value).</summary>
    public double? Temperature { get; set; }

    ///<summary>
    ///How hard a reasoning model should think: "low", "medium" or "high", or null to say nothing and
    ///let the model default. Chosen per request rather than saved with the connection, so it is set on
    ///a copy at the call site.
    ///</summary>
    public string Effort { get; set; }

    ///<summary>
    ///Whether an effort level means anything here, which depends on the provider and on the model.
    ///
    ///Two families take one. OpenAI's reasoning models read reasoning_effort, and its chat models treat it
    ///as an error rather than ignoring it. Google's OpenAI-compatibility layer accepts the same field on
    ///the models that think, which is 2.5 and up; the families below that reject it.
    ///
    ///Anthropic is the one left out on purpose: it has no effort level, only a thinking-token budget,
    ///which is a different control and is not wired up. An OpenAI-compatible server behind "Other" may be
    ///anything at all, so it is not offered one either.
    ///</summary>
    public static bool SupportsEffort(LlmConnection connection)
    {
        var model = connection?.Model;

        if (string.IsNullOrWhiteSpace(model))
            return false;

        if (connection.ProviderType == "OpenAI")
            //The reasoning families, by the prefixes OpenAI names them with.
            return model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

        if (connection.ProviderType == "Gemini")
            return IsThinkingGemini(model);

        return false;
    }

    ///<summary>
    ///Whether a Gemini model id names a thinking model, read as a version number rather than a list of
    ///names — "gemini-2.5-flash" is 2.5, "gemini-3-pro" is 3 — so a family released after this was
    ///written is offered an effort level instead of silently missing one.
    ///</summary>
    private static bool IsThinkingGemini(string model)
    {
        const string prefix = "gemini-";

        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = model.Substring(prefix.Length);
        var end = 0;

        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.'))
            end++;

        if (!double.TryParse(rest.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var version))
            return false;

        //Thinking arrived with 2.5, and everything after it thinks too.
        return version >= 2.5;
    }
}
