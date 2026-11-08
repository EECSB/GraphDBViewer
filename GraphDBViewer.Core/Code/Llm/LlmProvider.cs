using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>Result of a single LLM completion: the generated text, or an error message.</summary>
public readonly struct LlmResult
{
    private LlmResult(string text, string error, bool isError, bool isPaused = false)
    {
        Text = text;
        Error = error;
        IsError = isError;
        IsPaused = isPaused;
    }

    public string Text { get; }
    public string Error { get; }
    public bool IsError { get; }

    ///<summary>A run of tool calls that reached its limit of rounds still going somewhere: not an answer, and not an error either. <see cref="Text"/> says so.</summary>
    public bool IsPaused { get; }

    public static LlmResult Success(string text)
    {
        return new LlmResult(text, null, false);
    }

    public static LlmResult Failure(string error)
    {
        return new LlmResult(null, error, true);
    }

    public static LlmResult Paused(string note)
    {
        return new LlmResult(note, null, false, true);
    }
}

///<summary>A chat-style completion provider. One implementation per provider family (Anthropic, OpenAI-compatible).</summary>
public interface ILlmProvider
{
    ///<summary>The model it asks for, as it is sent: what its spend is kept under.</summary>
    string Model { get; }

    ///<summary>
    ///Everything its requests were billed for, added as each answer comes back rather than when a run ends, so a run
    ///stopped or failed partway still has what it spent counted. A caller wanting one run's share copies this before and
    ///takes <see cref="LlmUsage.Since"/> after (2026-11-01, from GDS Viewer).
    ///</summary>
    LlmUsage Spent { get; }

    ///<summary>What its tokens cost, or null when no rate is known for the model (<see cref="AiPricing.For"/>).</summary>
    LlmPrice Price { get; }

    ///<summary>The single-prompt case: <see cref="CompleteAsync(string, IReadOnlyList{LlmMessage}, CancellationToken)"/> with a conversation of one.</summary>
    Task<LlmResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        return CompleteAsync(systemPrompt, LlmMessage.One(userPrompt), ct);
    }

    ///<summary>
    ///The same completion over a conversation rather than a single prompt, so a follow-up can be asked
    ///in the context of what was already said. The single-prompt overload is this one with a conversation
    ///of one, which is why both exist rather than every caller building a list.
    ///</summary>
    Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct);

    ///<summary>
    ///The conversation with the runner's tools offered, running each call the model makes and handing back what it
    ///answered, until the model replies in words, a run that gets nowhere is stopped (<see cref="ToolProgress"/>), or
    ///<paramref name="maxRounds"/> runs out and the run is paused for the person to carry on. Both families implement it,
    ///which is why it is here rather than on an interface of its own as it was until 2026-11-01.
    ///</summary>
    Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, ILlmToolRunner tools, CancellationToken ct, int maxRounds = LlmProviderFactory.DefaultToolRounds);

    ///<summary>
    ///The same completion again, with the answer so far handed to <paramref name="onProgress"/> as it
    ///arrives. Extracting a graph from a long text is a wait of half a minute or more with nothing to look
    ///at, and this is what gives it something.
    ///
    ///The result is the same either way, so a caller never has to ask which kind of provider it holds: one
    ///that cannot stream falls back to this default, which reports the whole answer once at the end.
    ///</summary>
    async Task<LlmResult> StreamAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, Action<string> onProgress, CancellationToken ct)
    {
        var result = await CompleteAsync(systemPrompt, conversation, ct);

        if (!result.IsError)
            onProgress?.Invoke(result.Text);

        return result;
    }
}

///<summary>Builds the right <see cref="ILlmProvider"/> for a saved connection, and holds what both families send with.</summary>
public static class LlmProviderFactory
{
    ///<summary>
    ///How many rounds of tool calls one message may take before the run is paused for the person to carry on.
    ///
    ///<strong>A pause, not an error, since 2026-11-01</strong> (GDS Viewer's, asked for there). It was six rounds and a
    ///red "kept calling tools without answering", which fires on a model doing what it was asked: a question answered from
    ///three lookups, each checked, is six rounds of calls that all worked. What the count still guards against is spend,
    ///every round sending the whole conversation again, so it stays, higher, and ends the run with Continue rather than a
    ///failure. A model that is actually stuck is <see cref="ToolProgress"/>'s to stop, long before this.
    ///</summary>
    public const int DefaultToolRounds = 30;

    ///<summary>What the run says when its rounds run out with the calls still going somewhere: the question Continue answers.</summary>
    public static string RoundsPaused(int rounds)
    {
        return $"The assistant reached the set limit of {rounds} rounds of tool calls, and is paused. Nothing went wrong - continue for another {rounds} rounds?";
    }

    ///<summary>
    ///How many rounds in a row may get nowhere before the run is stopped as stuck: every call in them refused, or one
    ///already made and answered the same way. Three, so a model that tries one thing that fails and then something else
    ///is left to find its way, and one going round in circles is stopped within a few requests.
    ///</summary>
    public const int MaxStuckRounds = 3;

    ///<summary>What the run says when <see cref="MaxStuckRounds"/> runs out: the one way a run of calls is an error.</summary>
    public static readonly string RoundsStuck = $"The model went {MaxStuckRounds} rounds of tool calls without getting anywhere - each call refused, or one it had already made - and was stopped.";

    ///<summary>
    ///How long one request may take before it is given up on.
    ///
    ///The client the panels send with has no timeout of its own (<see cref="NewHttpClient"/>), so this is the clock, and
    ///it is each request's rather than the client's. Five minutes is longer than a model takes over anything asked here,
    ///and short enough that a request nobody will answer does not hold the panel until the tab is closed. A streamed
    ///answer is held to it until it starts and not after, since knowledge-graph generation reads one for as long as it
    ///is written.
    ///</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static ILlmProvider Create(HttpClient http, LlmConnection connection)
    {
        if (connection.ProviderType == "Anthropic")
            return new AnthropicProvider(http, connection);
        else
            return new OpenAiProvider(http, connection);
    }

    ///<summary>
    ///The client to send to a model with: one with no timeout of its own.
    ///
    ///<strong>HttpClient gives up after 100 seconds unless told otherwise</strong>, and the page's own client, which the
    ///panels were handed until 2026-10-14, was never told. A long answer, a thinking model or a slow server of one's own
    ///was cut off, and the panel said "Generation canceled." when nobody had canceled. GDSViewer's port of this panel
    ///found it, and LabEquipmentController's AI client had hit the same 100 s. <see cref="RequestTimeout"/> is the
    ///clock instead, and a request that runs past it says so.
    ///</summary>
    public static HttpClient NewHttpClient()
    {
        return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    ///<summary>
    ///Sends a request for a whole answer and reads what came back: the body, or why there is none.
    ///
    ///<strong>The status is read, not only the body</strong> (2026-10-14). A provider that refuses says why in its body
    ///most of the time, and that is the sentence worth showing: a key that is wrong, a model that does not exist, a
    ///quota spent. But a gateway in between answers with a page of HTML, or with nothing, and a body that was not JSON
    ///read as "Could not parse model response", which names the wrong problem. So a failure that says nothing readable
    ///is said by its status.
    ///
    ///<strong>A request that never arrived is said as one</strong>, with the address and what to check, which is the
    ///likeliest failure for a server of one's own (<see cref="Unreachable"/>). The person's own Cancel is let through as
    ///it is, so the panel can tell it from a failure.
    ///</summary>
    internal static async Task<(string Body, string Problem)> SendAsync(HttpClient http, HttpRequestMessage request, string label, CancellationToken ct)
    {
        var clock = ClockOf(http);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);

        timer.CancelAfter(clock);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"{label} did not answer within {(int)clock.TotalSeconds} s.");
        }
        catch (HttpRequestException failed)
        {
            return (null, Unreachable(request, failed));
        }

        using (response)
        {
            string body;

            try
            {
                body = await response.Content.ReadAsStringAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, $"{label} did not finish answering within {(int)clock.TotalSeconds} s.");
            }

            if (response.IsSuccessStatusCode)
                return (body, null);

            return (null, Refusal(response, body, label));
        }
    }

    ///<summary>
    ///Sends a request for an answer to be streamed, and hands back the response once the answer has started, or why it
    ///did not. Held to the clock until then and not after, since a long answer is read for as long as it is written; the
    ///clock is gone once this returns.
    ///</summary>
    internal static async Task<(HttpResponseMessage Response, string Problem)> OpenStreamAsync(HttpClient http, HttpRequestMessage request, string label, CancellationToken ct)
    {
        var clock = ClockOf(http);

        HttpResponseMessage response;

        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timer.CancelAfter(clock);

            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, $"{label} did not start answering within {(int)clock.TotalSeconds} s.");
            }
            catch (HttpRequestException failed)
            {
                return (null, Unreachable(request, failed));
            }
        }

        if (response.IsSuccessStatusCode)
            return (response, null);

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            return (null, Refusal(response, body, label));
        }
    }

    ///<summary>
    ///The clock a request runs on: <see cref="RequestTimeout"/>, or the client's own timeout where it is the shorter,
    ///which is the page's client on a host that registers no <see cref="LlmHttpClient"/>, so the sentence names the time
    ///that actually ran out.
    ///</summary>
    private static TimeSpan ClockOf(HttpClient http)
    {
        if (http.Timeout > TimeSpan.Zero && http.Timeout < RequestTimeout)
            return http.Timeout;
        else
            return RequestTimeout;
    }

    ///<summary>
    ///A failed answer as a sentence: who answered, with what status, and the provider's own words where its body has
    ///them — "Anthropic answered 401 Unauthorized: invalid x-api-key". With none, the status alone, and for a 401 or a
    ///403 what is likeliest wrong.
    ///</summary>
    internal static string Refusal(HttpResponseMessage response, string body, string label)
    {
        var status = $"{(int)response.StatusCode} {ReasonOf(response)}".Trim();
        var said = ErrorIn(body);

        if (said != null)
            return $"{label} answered {status}: {said}";
        else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            return $"{label} answered {status}. The key may be wrong, or not allowed to use this model.";

        return $"{label} answered {status}.";
    }

    ///<summary>
    ///A status's words. A browser's fetch often has none to give, HTTP/2 having dropped them, so a known status is named
    ///from its own name: <c>TooManyRequests</c> as "Too Many Requests".
    ///</summary>
    private static string ReasonOf(HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
            return response.ReasonPhrase;

        if (!Enum.IsDefined(response.StatusCode))
            return "";

        var name = response.StatusCode.ToString();
        var words = new StringBuilder();

        foreach (var letter in name)
        {
            if (char.IsUpper(letter) && words.Length > 0)
                words.Append(' ');

            words.Append(letter);
        }

        return words.ToString();
    }

    ///<summary>
    ///A request that never arrived, as a sentence naming the address and what to check.
    ///
    ///In a browser a server that refuses a page's request, by not allowing it under CORS or by not being there, comes
    ///back as a bare "Failed to fetch". The panel meant to add a CORS hint to that and never could: the providers caught
    ///every exception first and turned it into the answer's error, so the hint's catch was never reached (GDSViewer's
    ///port found it, 2026-10-10). It is said here, where it is caught.
    ///</summary>
    internal static string Unreachable(HttpRequestMessage request, HttpRequestException failed)
    {
        var address = "the server";

        if (request.RequestUri != null && request.RequestUri.IsAbsoluteUri)
            address = request.RequestUri.GetLeftPart(UriPartial.Authority);

        return $"Could not reach {address}: {failed.Message.TrimEnd('.')}. If the address is right, the server may not allow requests from a web page (CORS), or may not be running.";
    }

    ///<summary>
    ///The message in an error body, or null when there is none to read. Every provider here puts it at
    ///<c>error.message</c>; Google's OpenAI-compatible endpoint sometimes wraps the object in an array,
    ///<c>[{ "error": ... }]</c>, which is read through.
    ///</summary>
    internal static string ErrorIn(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);

            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
                return null;

            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
            else if (error.ValueKind == JsonValueKind.String)
                return error.GetString();

            return error.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

///<summary>
///Whether a run of tool calls is getting anywhere (2026-11-01, from GDS Viewer): what tells a model at work from a stuck
///one, where a count of rounds could not; see <see cref="LlmProviderFactory.DefaultToolRounds"/>.
///
///<strong>A round gets somewhere when one of its calls worked and was not a repeat</strong>: a call already made, with the
///same arguments, that answered the same, picture included. A query run again after a write is not a repeat when its
///answer changed; a second run of the same read over nothing new is. A refusal is never progress, a declined call
///included.
///</summary>
public sealed class ToolProgress
{
    private readonly HashSet<string> _seen = new();

    private int _stuckRounds;

    ///<summary>Counts one round's calls, and says whether the run has now gone <see cref="LlmProviderFactory.MaxStuckRounds"/> rounds in a row without getting anywhere.</summary>
    public bool IsStuckAfter(IReadOnlyList<(LlmToolCall Call, LlmToolOutput Output)> round)
    {
        var anythingNew = false;

        foreach (var (call, output) in round)
        {
            if (output.IsError || output.IsDeclined)
                continue;

            var picture = "";

            if (output.Png is string png)
                picture = $"{png.Length}:{png.GetHashCode()}";

            if (_seen.Add(string.Join('\u0000', call.Name, call.ArgumentsJson, output.Text, picture)))
                anythingNew = true;
        }

        if (anythingNew)
            _stuckRounds = 0;
        else
            _stuckRounds++;

        return _stuckRounds >= LlmProviderFactory.MaxStuckRounds;
    }
}

///<summary>
///The client the AI panels send to a model with, where the host registers one: a client with no timeout of its own,
///from <see cref="LlmProviderFactory.NewHttpClient"/>.
///
///A service apart rather than the page's <see cref="HttpClient"/>, which the rest of the page needs to keep its
///100 seconds and its base address. Optional, so a host that registers none keeps working on the page's client, held
///to that client's timeout, and a test hands the panels its own handler as it always has.
///</summary>
public sealed class LlmHttpClient
{
    public LlmHttpClient(HttpClient http)
    {
        Http = http;
    }

    public HttpClient Http { get; }

    ///<summary>The registered client, or <paramref name="fallback"/> where the host registers none.</summary>
    public static HttpClient For(IServiceProvider services, HttpClient fallback)
    {
        if (services?.GetService(typeof(LlmHttpClient)) is LlmHttpClient own)
            return own.Http;

        return fallback;
    }
}
