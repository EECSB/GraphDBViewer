using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>Result of a single LLM completion: the generated text, or an error message.</summary>
public readonly struct LlmResult
{
    private LlmResult(string text, string error, bool isError)
    {
        Text = text;
        Error = error;
        IsError = isError;
    }

    public string Text { get; }
    public string Error { get; }
    public bool IsError { get; }

    public static LlmResult Success(string text)
    {
        return new LlmResult(text, null, false);
    }

    public static LlmResult Failure(string error)
    {
        return new LlmResult(null, error, true);
    }
}

///<summary>A chat-style completion provider. One implementation per provider family (Anthropic, OpenAI-compatible).</summary>
public interface ILlmProvider
{
    Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);

    ///<summary>
    ///The same completion over a conversation rather than a single prompt, so a follow-up can be asked
    ///in the context of what was already said. The single-prompt overload is this one with a conversation
    ///of one, which is why both exist rather than every caller building a list.
    ///</summary>
    Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct);
}

///<summary>Builds the right <see cref="ILlmProvider"/> for a saved connection.</summary>
public static class LlmProviderFactory
{
    public static ILlmProvider Create(HttpClient http, LlmConnection connection)
    {
        if (connection.ProviderType == "Anthropic")
            return new AnthropicProvider(http, connection);
        else
            return new OpenAiProvider(http, connection);
    }
}
