using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///A saved "AI model" connection used for natural-language → query generation. Kept through the app's
///storage (<see cref="LlmConnectionStore"/>, behind <c>IAppStorage</c>) alongside the database connections.
///Bring-your-own-key: the user supplies the provider, endpoint and key.
///</summary>
public class LlmConnection
{
    public const string Anthropic = "Anthropic";
    public const string OpenAI = "OpenAI";
    public const string Gemini = "Gemini";

    ///<summary>Any other OpenAI-compatible server: Ollama, LM Studio, vLLM, a company's own.</summary>
    public const string Custom = "Custom";

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
        Effort = copy.Effort;
        Fast = copy.Fast;
        InputPrice = copy.InputPrice;
        CachedPrice = copy.CachedPrice;
        OutputPrice = copy.OutputPrice;
        PdfAsText = copy.PdfAsText;
    }

    ///<summary>
    ///Whether saving this over the other would change nothing. A box typed in and emptied again is the same as one never
    ///touched. Its name is left out, being renamed apart, and so are Effort and Fast, which are never saved. A new saved
    ///property belongs here as well as in the copy constructor.
    ///</summary>
    public bool SameSettings(LlmConnection other)
    {
        return SameText(ProviderType, other.ProviderType)
            && SameText(BaseUrl, other.BaseUrl)
            && SameText(ApiKey, other.ApiKey)
            && SameText(Model, other.Model)
            && MaxTokens == other.MaxTokens
            && Temperature == other.Temperature
            && InputPrice == other.InputPrice
            && CachedPrice == other.CachedPrice
            && OutputPrice == other.OutputPrice
            && PdfAsText == other.PdfAsText;
    }

    private static bool SameText(string left, string right)
    {
        return string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    }

    public string Name { get; set; }

    ///<summary>Provider family: <see cref="Anthropic"/>, <see cref="OpenAI"/>, <see cref="Gemini"/> or <see cref="Custom"/>. The last three share the OpenAI-compatible adapter.</summary>
    public string ProviderType { get; set; } = Anthropic;

    ///<summary>Base URL for OpenAI-compatible endpoints (e.g. http://localhost:11434/v1 for Ollama). Ignored for Anthropic.</summary>
    public string BaseUrl { get; set; }

    public string ApiKey { get; set; }

    ///<summary>Model id (e.g. claude-opus-5-5, gpt-5-mini, or a local model name).</summary>
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
    ///How hard a reasoning model should think, one of <see cref="EffortLevels"/>, or empty to say nothing and let the
    ///model default. <strong>Never saved</strong>: chosen per request and set on a copy at the call site, since set on the
    ///saved one it would be written out against the model for every conversation after.
    ///</summary>
    [JsonIgnore]
    public string Effort { get; set; } = "";

    ///<summary>
    ///Whether Anthropic runs the request in fast mode: the same model writing up to two and a half times as fast, at twice
    ///the price (<see cref="SupportsFast"/>). Chosen per request and never saved, as <see cref="Effort"/> is not.
    ///</summary>
    [JsonIgnore]
    public bool Fast { get; set; }

    ///<summary>
    ///What a million input tokens cost on this model, in US dollars, when the person has said: null to take the published
    ///rate, or to show no cost where there is none. With <see cref="CachedPrice"/> and <see cref="OutputPrice"/>, the rates
    ///<see cref="AiPricing.For"/> prices a run at.
    ///</summary>
    public decimal? InputPrice { get; set; }

    ///<summary>A million input tokens read back from the provider's cache, in US dollars, or null for the published rate.</summary>
    public decimal? CachedPrice { get; set; }

    ///<summary>A million output tokens, thinking included, in US dollars, or null for the published rate.</summary>
    public decimal? OutputPrice { get; set; }

    ///<summary>
    ///Whether a PDF goes to this model as the words read out of it in the page (PdfPig, <see cref="SourceTextExtractor"/>)
    ///rather than as the file: for a model that cannot read one, a server of one's own or Gemini, or to spend less than a
    ///page's picture costs (2026-11-02, the user's). Off until ticked; a scan, which has no words to read, needs it off.
    ///</summary>
    public bool PdfAsText { get; set; }

    ///<summary>
    ///Whether a connection's calls go over TLS: Anthropic's always, since its endpoint is fixed; the others' when
    ///the address they are sent to is https, their own default address included where the base URL is left blank.
    ///The model form asks it so that a key saved for an encrypted address is not sent to one that is not (the
    ///user's rule of 2026-10-03).
    ///</summary>
    public static bool Encrypts(LlmConnection connection)
    {
        if (connection == null)
            return false;

        if (connection.ProviderType == Anthropic)
            return true;

        var address = connection.BaseUrl;

        if (string.IsNullOrWhiteSpace(address))
            address = OpenAiProvider.DefaultBaseUrlFor(connection.ProviderType);

        //A server of one's own with no address is sent nothing at all, so it encrypts nothing either.
        if (string.IsNullOrWhiteSpace(address))
            return false;

        return address.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>Whether an effort level means anything here: whether <see cref="EffortLevels"/> offers any.</summary>
    public static bool SupportsEffort(LlmConnection connection)
    {
        return EffortLevels(connection).Count > 0;
    }

    ///<summary>
    ///The effort levels a model takes, lowest first, which depend on the provider and on the model: none where it takes no
    ///level at all.
    ///
    ///OpenAI's reasoning models read reasoning_effort, and its chat models treat it as an error rather than ignoring it.
    ///Google's OpenAI-compatible endpoint takes the same field on the models that think, which is 2.5 and up. Both go to
    ///high.
    ///
    ///<strong>Anthropic reads output_config.effort</strong> (since 2026-11-01, from GDS Viewer; left out until then, when
    ///Claude's only control was taken for a budget of thinking tokens): from Opus 4.5, which took three levels; Opus and
    ///Sonnet 4.6 added max, and Opus 4.7 xhigh, which every model since takes. Sonnet 4.5 and Haiku 4.5 refuse the field
    ///rather than ignoring it, so they are offered none. A server behind Other may be anything at all, so it is not
    ///offered one either.
    ///</summary>
    public static IReadOnlyList<string> EffortLevels(LlmConnection connection)
    {
        var model = Runs(connection);

        if (model.Length == 0)
            return Array.Empty<string>();

        if (connection.ProviderType == OpenAI)
        {
            //The reasoning families, by the prefixes OpenAI names them with.
            if (model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
                return ThreeLevels;
        }
        else if (connection.ProviderType == Gemini)
        {
            //Thinking arrived with 2.5, and everything after it thinks too.
            if (VersionAfter(model, "gemini-") >= 2.5)
                return ThreeLevels;
        }
        else if (connection.ProviderType == Anthropic)
            return ClaudeLevels(model);

        return Array.Empty<string>();
    }

    ///<summary>
    ///Whether a model runs in fast mode, which only Anthropic offers and only on Opus 4.8 and Opus 5 on: Opus 4.7's was
    ///taken away, and a request asking it of any other model is refused rather than run slowly. By version, so an Opus
    ///released later is offered it too (the provider's documents, 2026-11-01).
    ///</summary>
    public static bool SupportsFast(LlmConnection connection)
    {
        if (connection == null || connection.ProviderType != Anthropic)
            return false;

        var (family, version) = ClaudeVersion(Runs(connection));

        if (family != "opus")
            return false;

        return version >= 5 || version == 4.8;
    }

    ///<summary>
    ///What a model does when it is sent no effort level, as its provider documents it: for the effort list's first row,
    ///which said only "default" and so told nobody how hard the model would think (asked 2026-10-18 in GDS Viewer).
    ///
    ///By family, read from the version number like <see cref="EffortLevels"/>, so a model released later lands in its
    ///family: OpenAI's o-series and GPT-5 reason at medium unless told otherwise, and GPT-5.1 and after at none; Gemini 3
    ///and after think at high, 2.5 Flash-Lite not at all, and the rest of 2.5 as much as a question needs; Opus 5.5 and
    ///Haiku 5.5 at medium and every other Claude that takes a level at high. Empty where no effort is offered.
    ///
    ///<strong>The providers' documents, not a measurement</strong>: nothing in an answer says what level it was given, so
    ///a family that changes its default is only noticed here by somebody reading the change.
    ///</summary>
    public static string DefaultEffort(LlmConnection connection)
    {
        if (!SupportsEffort(connection))
            return "";

        var model = Runs(connection);

        if (connection.ProviderType == Anthropic)
        {
            var (family, version) = ClaudeVersion(model);

            if ((family == "opus" || family == "haiku") && version >= 5.5)
                return "medium";

            return "high";
        }
        else if (connection.ProviderType == Gemini)
        {
            if (VersionAfter(model, "gemini-") >= 3)
                return "high";
            else if (model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase))
                return "none";

            return "as needed";
        }

        if (VersionAfter(model, "gpt-") > 5)
            return "none";

        return "medium";
    }

    ///<summary>
    ///The model a connection runs, trimmed: its own, or for Anthropic with none named the one the provider falls back to,
    ///so a blank model field is offered the levels and fast mode of the model it really asks for. Empty where there is
    ///none. GDS Viewer's read the field alone, and offered a blank Claude model nothing.
    ///</summary>
    private static string Runs(LlmConnection connection)
    {
        if (connection == null)
            return "";
        else if (connection.ProviderType == Anthropic)
            return AnthropicProvider.ResolveModel(connection);

        return (connection.Model ?? "").Trim();
    }

    private static readonly string[] ThreeLevels = { "low", "medium", "high" };

    private static readonly string[] ClaudeFourSix = { "low", "medium", "high", "max" };

    private static readonly string[] ClaudeAll = { "low", "medium", "high", "xhigh", "max" };

    private static IReadOnlyList<string> ClaudeLevels(string model)
    {
        var (family, version) = ClaudeVersion(model);

        if (family == "opus")
        {
            if (version >= 4.7)
                return ClaudeAll;
            else if (version >= 4.6)
                return ClaudeFourSix;
            else if (version >= 4.5)
                return ThreeLevels;
        }
        else if (family == "sonnet")
        {
            if (version >= 5)
                return ClaudeAll;
            else if (version >= 4.6)
                return ClaudeFourSix;
        }
        else if (family == "haiku" || family == "fable" || family == "mythos")
        {
            if (version >= 5)
                return ClaudeAll;
        }

        return Array.Empty<string>();
    }

    ///<summary>
    ///A Claude model id read as its family and version: claude-opus-5-5 is opus 5.5, claude-sonnet-5 sonnet 5, and
    ///claude-opus-4-5-20251101 opus 4.5, a part longer than two digits being a date and not a minor version. An id in no
    ///such shape is no family and version -1.
    ///</summary>
    private static (string Family, double Version) ClaudeVersion(string model)
    {
        var parts = model.ToLowerInvariant().Split('-');

        if (parts.Length < 3 || parts[0] != "claude" || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            return ("", -1);

        double version = major;

        if (parts.Length > 3 && parts[3].Length <= 2 && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            version = major + (minor / 10.0);

        return (parts[1], version);
    }

    ///<summary>
    ///The version at the front of a model id, after its family's prefix: 2.5 for gemini-2.5-flash, 5 for gpt-5-mini, 5.1
    ///for gpt-5.1-codex; -1 for an id that does not start with the prefix and a number.
    ///</summary>
    private static double VersionAfter(string model, string prefix)
    {
        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return -1;

        var rest = model.Substring(prefix.Length);
        var end = 0;

        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.'))
            end++;

        if (!double.TryParse(rest.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var version))
            return -1;

        return version;
    }
}
