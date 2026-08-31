using System;

namespace GraphDBViewerWeb.Code;

///<summary>Text cleanup for model output, shared by the AI features ("Ask AI" and knowledge-graph
///generation) so the fence handling exists once. NlQueryPrompt.CleanQuery delegates here.</summary>
public static class LlmText
{
    ///<summary>
    ///Strips a surrounding markdown code fence (```lang … ```) and trims, so a fenced reply still
    ///parses cleanly whether it is a query or JSON.
    ///</summary>
    public static string StripMarkdownFences(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return string.Empty;

        var text = modelOutput.Trim();

        if (!text.StartsWith("```"))
            return text;

        //Drop the opening fence line (``` optionally followed by a language tag).
        int firstNewline = text.IndexOf('\n');

        if (firstNewline < 0)
            return text;

        text = text.Substring(firstNewline + 1);

        //Drop the closing fence.
        int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);

        if (lastFence >= 0)
            text = text.Substring(0, lastFence);

        return text.Trim();
    }

    ///<summary>
    ///The contents of the last fenced block inside a longer reply, or null when there is no complete one.
    ///
    ///Distinct from <see cref="StripMarkdownFences"/>, which unwraps a reply that is nothing but a fence.
    ///In a conversation the query arrives surrounded by prose, and the last block is the one to take: a
    ///model that reconsiders shows its earlier attempt first and its answer last.
    ///</summary>
    public static string ExtractFencedBlock(string text)
    {
        if (!TryFindLastFence(text, out int open, out int close))
            return null;

        var inner = text.Substring(open, close - open + 3);
        var body = StripMarkdownFences(inner);

        if (string.IsNullOrWhiteSpace(body))
            return null;

        //StripMarkdownFences hands back what it was given when it cannot find the end of the opening
        //fence line, which here means the two runs of backticks were adjacent with nothing between them.
        //An empty block is not a query, and returning the backticks themselves would be worse than none.
        if (body.StartsWith("```", StringComparison.Ordinal))
            return null;

        return body;
    }

    ///<summary>
    ///The same text with its last fenced block taken out, for showing the words a model wrapped around
    ///a query when the query itself is displayed separately. Returns the text unchanged when there is no
    ///block to remove, so a reply that is only prose survives it.
    ///</summary>
    public static string WithoutFencedBlock(string text)
    {
        if (ExtractFencedBlock(text) == null)
            return text;

        TryFindLastFence(text, out int open, out int close);

        var prose = text.Substring(0, open) + text.Substring(close + 3);

        return prose.Trim();
    }

    ///<summary>Locates the last complete pair of fences, which is the block both methods act on.</summary>
    private static bool TryFindLastFence(string text, out int open, out int close)
    {
        open = -1;
        close = -1;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        close = text.LastIndexOf("```", StringComparison.Ordinal);

        if (close <= 0)
            return false;

        open = text.LastIndexOf("```", close - 1, StringComparison.Ordinal);

        return open >= 0;
    }
}
