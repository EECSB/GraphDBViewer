using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///What requests were billed for, in tokens: one answer's, a whole run's, or everything a provider has sent.
///
///<strong>Three of the five are parts, not additions.</strong> <see cref="InputTokens"/> is everything read, and
///<see cref="CachedInputTokens"/> and <see cref="CacheWriteTokens"/> are the parts of it read from a provider's cache and
///written into it, each billed at a rate of its own; <see cref="OutputTokens"/> is everything written, and
///<see cref="ReasoningTokens"/> the part of it that was thinking. Priced in <see cref="LlmPrice.Cost"/>. Ported from GDS
///Viewer's (2026-11-01), which had it from ConfigurationRuleEditor.
///
///<strong>The names are pinned</strong> (2026-11-03, as GDS Viewer's): a saved chat carries what each answer cost under
///them, and a renamed property would read every chat saved before it as nothing spent.
///</summary>
public sealed class LlmUsage
{
    [JsonPropertyName("InputTokens")]
    public long InputTokens { get; set; }

    ///<summary>Part of <see cref="InputTokens"/>: read back from the provider's cache, at a tenth of the rate or less.</summary>
    [JsonPropertyName("CachedInputTokens")]
    public long CachedInputTokens { get; set; }

    ///<summary>Part of <see cref="InputTokens"/>: put into Anthropic's cache, at a quarter over the rate. Nobody else reports writes.</summary>
    [JsonPropertyName("CacheWriteTokens")]
    public long CacheWriteTokens { get; set; }

    [JsonPropertyName("OutputTokens")]
    public long OutputTokens { get; set; }

    ///<summary>Part of <see cref="OutputTokens"/>: the thinking rather than the answer, billed the same.</summary>
    [JsonPropertyName("ReasoningTokens")]
    public long ReasoningTokens { get; set; }

    ///<summary>How many answers said what they cost. A run of tool calls is several billed requests.</summary>
    [JsonPropertyName("Requests")]
    public int Requests { get; set; }

    ///<summary>False until an answer has said what it cost. A server of one's own may never, and then nothing is shown rather than a zero.</summary>
    [JsonIgnore]
    public bool Reported => Requests > 0;

    public void Add(LlmUsage more)
    {
        if (more == null)
            return;

        InputTokens += more.InputTokens;
        CachedInputTokens += more.CachedInputTokens;
        CacheWriteTokens += more.CacheWriteTokens;
        OutputTokens += more.OutputTokens;
        ReasoningTokens += more.ReasoningTokens;
        Requests += more.Requests;
    }

    public LlmUsage Copy()
    {
        var copy = new LlmUsage();

        copy.Add(this);

        return copy;
    }

    ///<summary>What was added since <paramref name="before"/> was copied from this: one run's share of a provider's total.</summary>
    public LlmUsage Since(LlmUsage before)
    {
        return new LlmUsage
        {
            InputTokens = InputTokens - before.InputTokens,
            CachedInputTokens = CachedInputTokens - before.CachedInputTokens,
            CacheWriteTokens = CacheWriteTokens - before.CacheWriteTokens,
            OutputTokens = OutputTokens - before.OutputTokens,
            ReasoningTokens = ReasoningTokens - before.ReasoningTokens,
            Requests = Requests - before.Requests
        };
    }

    ///<summary>
    ///What an answer has been billed for after one more report of it (2026-11-07). A streamed answer reports its usage as
    ///it goes rather than once at the end, and cumulatively: the Messages API says what was read in message_start and what
    ///was written so far in message_delta, and a compatible endpoint may say the whole so far in every chunk. So each
    ///count is the larger of the two, and the answer stays one request however often it is reported. The first report is
    ///the answer's so far, and a report of nothing changes nothing.
    ///</summary>
    public static LlmUsage Latest(LlmUsage soFar, LlmUsage report)
    {
        if (report == null)
            return soFar;

        if (soFar == null)
            return report;

        soFar.InputTokens = Math.Max(soFar.InputTokens, report.InputTokens);
        soFar.CachedInputTokens = Math.Max(soFar.CachedInputTokens, report.CachedInputTokens);
        soFar.CacheWriteTokens = Math.Max(soFar.CacheWriteTokens, report.CacheWriteTokens);
        soFar.OutputTokens = Math.Max(soFar.OutputTokens, report.OutputTokens);
        soFar.ReasoningTokens = Math.Max(soFar.ReasoningTokens, report.ReasoningTokens);
        soFar.Requests = Math.Max(soFar.Requests, report.Requests);

        return soFar;
    }

    ///<summary>
    ///What one Messages API answer says it cost, or null when it says nothing.
    ///
    ///<strong>Anthropic's input_tokens leaves its cache out</strong>: it counts what came after the last place the cache
    ///was read or written, and cache_read_input_tokens and cache_creation_input_tokens count the rest. So everything read
    ///is the three together, which is the shape the other providers report it in.
    ///</summary>
    public static LlmUsage FromAnthropic(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var fresh = Count(usage, "input_tokens");
        var read = Count(usage, "cache_read_input_tokens");
        var written = Count(usage, "cache_creation_input_tokens");

        return new LlmUsage
        {
            InputTokens = fresh + read + written,
            CachedInputTokens = read,
            CacheWriteTokens = written,
            OutputTokens = Count(usage, "output_tokens"),
            Requests = 1
        };
    }

    ///<summary>
    ///What one chat-completions answer says it cost, or null when it says nothing, which a server of one's own may.
    ///
    ///prompt_tokens counts everything read, its cached part in prompt_tokens_details; completion_tokens everything
    ///written, its reasoning in completion_tokens_details. <strong>Gemini, through Google's compatible endpoint, may leave
    ///its thinking out of completion_tokens</strong> and count it in total_tokens alone, so what total_tokens holds beyond
    ///the other two is taken for thinking, which Google bills as output. That is how the endpoint answers as people using
    ///it report it, not anything Google documents for it (its page says nothing of the fields, read 2026-10-29), so a
    ///reasoning count said beside the surplus is taken for the same thinking said twice, and the larger is the thinking.
    ///</summary>
    public static LlmUsage FromOpenAi(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var prompt = Count(usage, "prompt_tokens");
        var completion = Count(usage, "completion_tokens");
        long reasoning = 0;
        long cached = 0;

        if (usage.TryGetProperty("prompt_tokens_details", out var read) && read.ValueKind == JsonValueKind.Object)
            cached = Count(read, "cached_tokens");

        if (usage.TryGetProperty("completion_tokens_details", out var written) && written.ValueKind == JsonValueKind.Object)
            reasoning = Count(written, "reasoning_tokens");

        var unsaid = Count(usage, "total_tokens") - prompt - completion;

        if (unsaid > 0)
        {
            completion += unsaid;
            reasoning = Math.Max(reasoning, unsaid);
        }

        return new LlmUsage
        {
            InputTokens = prompt,
            CachedInputTokens = cached,
            OutputTokens = completion,
            ReasoningTokens = reasoning,
            Requests = 1
        };
    }

    private static long Count(JsonElement owner, string name)
    {
        if (owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return Math.Max(0, number);

        return 0;
    }
}
