using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>How Ask AI's microphone turns speech into words: see <see cref="SpeechPreference"/>.</summary>
public enum SpeechMode
{
    ///<summary>The browser's own recognition, live as the words are spoken. Chrome and Edge, through Google's and Microsoft's servers.</summary>
    Browser,

    ///<summary>A recording sent to the AI provider (OpenAI's transcription, or Gemini) when the microphone is pressed again.</summary>
    Provider
}

///<summary>
///<strong>A recording written out by the AI provider</strong> (2026-11-02, GDS Viewer's, beside the browser's own
///recognition): the microphone's WAV (see speechWav.js) sent to OpenAI's transcription endpoint, or to Gemini, which
///takes audio in an ordinary request. Anthropic's API takes no audio, so a Claude model picked in the window leaves it
///to the first saved OpenAI or Gemini model (<see cref="Pick"/>).
///
///<strong>Told the words a graph conversation is full of</strong>: the connected graph's labels and property keys, and
///the viewer's own terms, which is most of what the provider does better than the browser, where "hasLabel" comes back
///as "has label" and a label as whatever it sounds like. OpenAI takes them as its prompt, Gemini in its instruction.
///</summary>
public static class SpeechTranscriber
{
    ///<summary>OpenAI's transcription model: more accurate than whisper-1 on technical words, at about the same price.</summary>
    public const string OpenAiModel = "gpt-4o-transcribe";

    ///<summary>Gemini's own API, which takes a WAV inline: its OpenAI-compatible endpoint has no transcription.</summary>
    public const string GeminiApi = "https://generativelanguage.googleapis.com/v1beta/models/";

    ///<summary>The model Gemini is asked with when the saved one names none.</summary>
    public const string GeminiDefaultModel = "gemini-2.5-flash";

    ///<summary>The words a graph conversation is full of, said to the provider so it writes them as the viewer does.</summary>
    public const string GraphWords = "graph, vertex, vertices, edge, label, property, Gremlin, Cypher, SPARQL, AQL, DQL, traversal, g.V(), outE, inE, hasLabel, valueMap, MATCH, RETURN, JanusGraph, Neo4j, Memgraph, ArangoDB, Dgraph";

    ///<summary>The most words told: a prompt is for hints, and a long one costs on every recording.</summary>
    public const int MostWords = 80;

    ///<summary>Whether a saved model's provider can write out a recording: OpenAI's own, and Gemini's own.</summary>
    public static bool CanTranscribe(LlmConnection connection)
    {
        if (connection == null)
            return false;

        if (connection.ProviderType != LlmConnection.OpenAI && connection.ProviderType != LlmConnection.Gemini)
            return false;

        //The provider's own address only: a server in between that speaks chat completions may have no transcription,
        //and the key saved for it is that server's, not the provider's to be sent. Gemini was taken as its own whatever
        //its address until 2026-11-07, so a gateway's key went to Google with the recording.
        var address = (connection.BaseUrl ?? "").Trim().TrimEnd('/');

        return address.Length == 0 || string.Equals(address, OpenAiProvider.DefaultBaseUrlFor(connection.ProviderType), StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The model a recording goes to: the one picked in the window when it can, or else the first saved one that can, or null.</summary>
    public static LlmConnection Pick(LlmConnection picked, IEnumerable<LlmConnection> saved)
    {
        if (CanTranscribe(picked))
            return picked;

        return (saved ?? Array.Empty<LlmConnection>()).FirstOrDefault(CanTranscribe);
    }

    ///<summary>The words to tell the provider: the viewer's own and the graph's labels and keys, once each.</summary>
    public static string Vocabulary(IEnumerable<string> graphNames)
    {
        var words = new List<string>(GraphWords.Split(", "));

        foreach (var name in graphNames ?? Array.Empty<string>())
        {
            var trimmed = (name ?? "").Trim();

            if (trimmed.Length > 0 && !words.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                words.Add(trimmed);
        }

        return string.Join(", ", words.Take(MostWords));
    }

    public static HttpRequestMessage OpenAiRequest(LlmConnection connection, byte[] wav, string vocabulary)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);

        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(OpenAiModel), "model");
        form.Add(new StringContent($"A question to a graph database viewer's assistant. Words that may come up: {vocabulary}."), "prompt");
        form.Add(new StringContent("json"), "response_format");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{OpenAiProvider.DefaultBaseUrl}/audio/transcriptions") { Content = form };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey ?? "");

        return request;
    }

    public static HttpRequestMessage GeminiRequest(LlmConnection connection, byte[] wav, string vocabulary)
    {
        var model = connection.Model ?? "";

        if (string.IsNullOrWhiteSpace(model))
            model = GeminiDefaultModel;

        var instruction = $@"Write out exactly what is said in this recording, a question to a graph database viewer's assistant.
Reply with the words alone: no quotes, no notes, nothing about the recording. Write numbers as digits, and query words
and names as they are written in code (hasLabel, g.V()). Words that may come up: {vocabulary}.";

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = instruction },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) }
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{GeminiApi}{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        //In its header rather than the address, where it would land in logs along the way.
        request.Headers.Add("x-goog-api-key", connection.ApiKey ?? "");

        return request;
    }

    ///<summary>The words in a provider's answer, or null when it holds none.</summary>
    public static string TextIn(string providerType, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;

            if (providerType == LlmConnection.Gemini)
                return GeminiText(root);

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString()?.Trim();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GeminiText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            return null;

        if (!candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return null;

        var said = new StringBuilder();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                said.Append(text.GetString());
        }

        return said.ToString().Trim();
    }

    ///<summary>Sends a recording to the model's provider, and answers the words it heard or why there are none.</summary>
    public static async Task<(string Text, string Problem)> TranscribeAsync(HttpClient http, LlmConnection connection, byte[] wav, string vocabulary, CancellationToken ct)
    {
        if (!CanTranscribe(connection))
            return (null, $"{connection?.Name} cannot write out speech: that takes an OpenAI or Gemini model.");

        HttpRequestMessage request;

        if (connection.ProviderType == LlmConnection.Gemini)
            request = GeminiRequest(connection, wav, vocabulary);
        else
            request = OpenAiRequest(connection, wav, vocabulary);

        var label = connection.ProviderType;
        var (json, problem) = await LlmProviderFactory.SendAsync(http, request, label, ct);

        if (problem != null)
            return (null, problem);

        var text = TextIn(connection.ProviderType, json ?? "");

        if (text == null)
            return (null, $"{label} answered, with no words in it.");

        return (text, null);
    }
}
