using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>One event of a streamed answer: more text, the end of it, or the reason it stopped.</summary>
public readonly struct LlmStreamEvent
{
    private LlmStreamEvent(string text, string error, bool done, LlmUsage usage = null)
    {
        Text = text;
        Error = error;
        Done = done;
        Usage = usage;
    }

    ///<summary>The text this event adds, or null when it adds none (a heartbeat, a start, a usage report).</summary>
    public string Text { get; }

    public string Error { get; }

    ///<summary>True for the event that says the answer is complete, after which nothing is read.</summary>
    public bool Done { get; }

    ///<summary>
    ///What the answer has been billed for as this event reports it, or null when it reports nothing: the Messages API's
    ///message_start and message_delta, a chat-completions stream's last chunk (2026-11-07). Folded with
    ///<see cref="LlmUsage.Latest"/>, since a stream reports the counts so far rather than a share to add.
    ///</summary>
    public LlmUsage Usage { get; }

    ///<summary>The same event, carrying what the answer was billed for so far; null is no report.</summary>
    public LlmStreamEvent Billed(LlmUsage usage)
    {
        return new LlmStreamEvent(Text, Error, Done, usage);
    }

    public static LlmStreamEvent Nothing => new(null, null, false);

    public static LlmStreamEvent Ended => new(null, null, true);

    public static LlmStreamEvent More(string text)
    {
        return new LlmStreamEvent(text, null, false);
    }

    public static LlmStreamEvent Failed(string error)
    {
        return new LlmStreamEvent(null, error, false);
    }
}

///<summary>
///Reads a streamed answer — server-sent events, which is what both provider families speak — into the same
///<see cref="LlmResult"/> a whole answer comes back as, reporting the text so far as it grows.
///
///The event format is shared; what each event means is not, so the provider passes in a reader for its own
///events (<see cref="AnthropicProvider.ReadStreamEvent"/>, <see cref="OpenAiProvider.ReadStreamEvent"/>) and
///everything else happens here once.
///</summary>
public static class LlmStream
{
    ///<summary>
    ///The payload of a <c>data:</c> line, or null for every other line: the blank line between events, the
    ///<c>event:</c> name (the payload says its own type), a comment, a retry hint.
    ///</summary>
    public static string DataLine(string line)
    {
        if (line == null || !line.StartsWith("data:", StringComparison.Ordinal))
            return null;

        return line.Substring("data:".Length).Trim();
    }

    ///<summary>
    ///Reads until the stream ends or an event says it is done. <paramref name="onProgress"/> is handed the
    ///whole answer so far rather than the piece that just arrived, because what a progress display wants is
    ///the state, and a caller that wanted the pieces could not put them together without the order.
    ///</summary>
    public static async Task<LlmResult> ReadAsync(
        Stream stream,
        Func<string, LlmStreamEvent> readEvent,
        Action<string> onProgress,
        CancellationToken ct)
    {
        var answer = new StringBuilder();
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);

            if (line == null)
                break;

            var data = DataLine(line);

            if (data == null)
                continue;

            var next = readEvent(data);

            if (next.Error != null)
                return LlmResult.Failure(next.Error);

            if (next.Done)
                break;

            if (next.Text == null)
                continue;

            answer.Append(next.Text);
            onProgress?.Invoke(answer.ToString());
        }

        var text = answer.ToString().Trim();

        //The same answer an empty non-streamed response gives, so a caller reads one failure either way.
        if (text.Length == 0)
            return LlmResult.Failure("Empty response from the model.");

        return LlmResult.Success(text);
    }
}
