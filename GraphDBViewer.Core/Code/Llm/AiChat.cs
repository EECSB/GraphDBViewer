using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>One entry in the conversation, including the ones that are not something anybody said.</summary>
public sealed class ChatTurn
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    public const string Error = "error";

    ///<summary>
    ///A run of tool calls paused at its limit of rounds with nothing wrong: shown as the question whether to go on for
    ///another as many, with Continue on it rather than as an error, and left out of what the model is sent, as an error
    ///is. The calls before it go, said in words, with whatever is sent next.
    ///</summary>
    public const string Paused = "paused";

    ///<summary>
    ///The briefing a compaction put in place of the conversation before it: sent as the person's message, with the files
    ///that conversation carried kept on it unticked, and where the conversation the model is sent starts. See
    ///<see cref="AiChat.CompactAsync"/>.
    ///</summary>
    public const string Summary = "summary";

    ///<summary>
    ///A line from the page where a kept chat was opened again: shown as a note, and <strong>not where the conversation the
    ///model is sent starts</strong>, since carrying a chat on is the point of opening it. What it says goes to the model too,
    ///with the warning that what the queries above returned may be out of date. See <see cref="AiChat.Restore"/>.
    ///</summary>
    public const string Reopened = "reopened";

    public string Kind { get; init; }

    public string Text { get; set; } = "";

    ///<summary>The files a question was sent with, shown over its words and sent again with every question after it while they are ticked.</summary>
    public List<AiAttachment> Attachments { get; } = new();

    ///<summary>
    ///The query a turn is about: on an answer, the one it proposed in a fenced block, lifted out so it can be put in the
    ///editor or run; on a call, the one the call carries. Null where there is none.
    ///</summary>
    public string Query { get; set; }

    ///<summary>The tool a call asked for.</summary>
    public string ToolName { get; init; } = "";

    ///<summary>What it was asked to call it with, as the model sent it.</summary>
    public string ArgumentsJson { get; init; } = "";

    ///<summary>Whether the call changes the graph, which is worth showing differently from a read.</summary>
    public bool Changes { get; init; }

    ///<summary>Whether the mode answered for the person. The card is shown either way; only the wording differs.</summary>
    public bool Auto { get; set; }

    ///<summary>Null while the card is still waiting to be answered.</summary>
    public bool? Decision { get; set; }

    ///<summary>What the call answered. Null until it has, and on a call that was declined.</summary>
    public string Result { get; set; }

    public bool ResultIsError { get; set; }

    ///<summary>The picture the call took, as base64, for the card to show what the model was shown.</summary>
    public string Png { get; set; }

    ///<summary>How the waiting call is let go when one of the card's buttons is pressed.</summary>
    public TaskCompletionSource<bool> Gate { get; set; }

    ///<summary>What the run this turn ended was billed for, every request of it. Null on a turn that ended none, and where the provider said nothing.</summary>
    public LlmUsage Usage { get; set; }

    ///<summary>The model that ran it, whose rates priced it.</summary>
    public string Model { get; set; } = "";

    ///<summary>What <see cref="Usage"/> cost at the model's rates, or null where there is no rate for it.</summary>
    public decimal? Cost { get; set; }
}

///<summary>How a message went, for the window to do what follows.</summary>
public enum ChatOutcome
{
    ///<summary>Nothing was sent: an answer was still coming, or there were no words.</summary>
    NotSent,

    Answered,

    ///<summary>A refusal or an error ended it.</summary>
    Failed,

    ///<summary>The calls reached the cap on rounds still going somewhere, and wait for Continue.</summary>
    Paused,

    ///<summary>Stop, or New chat, ended it.</summary>
    Stopped
}

///<summary>What one model has been billed for in this window.</summary>
public sealed class ModelSpend
{
    public LlmUsage Usage { get; } = new LlmUsage();

    ///<summary>What the priced part of it cost: null until an answer from it had a rate to price it at.</summary>
    public decimal? Cost { get; set; }
}

///<summary>
///The conversation behind the Ask AI window: what has been said, what has been run, the run in progress, and what it has
///all been billed for.
///
///<strong>Out of the component and into a class of its own</strong> (2026-11-01, as GDS Viewer's port of this window has
///it): until then all of this lived in NlQueryModal, where a whole conversation, approvals, declines and a stop in the
///middle of one, could only be driven through the markup. Here a test drives it with a stand-in provider and no browser.
///</summary>
public sealed class AiChat
{
    ///<summary>The most of a call's arguments replayed into later messages: enough to say what was run, short of a query's every line.</summary>
    private const int MostArgumentsReplayed = 600;

    ///<summary>
    ///The most of a call's answer replayed. More than the arguments get, since what a query returned is what a follow-up
    ///asks about ("the third one"), where GDS Viewer's tools answer in a sentence; short of a whole result set sent again
    ///with every question.
    ///</summary>
    private const int MostResultReplayed = 4000;

    ///<summary>What Continue sends after a pause.</summary>
    public const string ContinueText = "Continue where you stopped.";

    ///<summary>
    ///How much history every question has to be sending again before compacting it is suggested, in tokens: GDS Viewer's
    ///and ConfigurationRuleEditor's. Counted over the conversation alone, files included, and not the instructions or the
    ///tools, which compacting does not touch.
    ///</summary>
    public const long CompactAfterTokens = 20_000;

    ///<summary>
    ///How many questions have to have been asked since the conversation began or was last compacted before compacting it
    ///is suggested, so one question with a big file does not trip it on its own: that file is better unticked in the files
    ///panel than summarized while the next question may still need it.
    ///</summary>
    public const int QuestionsBeforeCompacting = 3;

    ///<summary>About how long a briefing comes back, in tokens, for pricing a compaction before it runs.</summary>
    public const long BriefingTokens = 2_000;

    ///<summary>
    ///What a compaction asks for: a briefing the assistant can carry on from, with what a conversation turns on kept exactly.
    ///Names, ids and what the tools were called with above all: a briefing that said "the products" where the conversation
    ///said hasLabel('Product') would leave the next question to find them again. For every window, so it names no domain
    ///(2026-11-02, when the window was shared; a graph's labels and queries until then).
    ///</summary>
    public const string CompactSystem =
        "You compress a conversation between a person and an assistant that works with tools, so that it can go on in a "
        + "smaller context. Write a briefing for the assistant that picks the conversation up: what the person is after; the "
        + "names, ids, keys and values that came up, exactly as they were written; the tool calls that mattered, with what "
        + "they were called with, written out as it was, and what they returned, with counts and values as they were and "
        + "never rounded or paraphrased; what was changed, what the person declined and what was refused, and why; and what "
        + "is still open. Where an attached file mattered, carry over the specific facts that were used from it, not the "
        + "file. Plain prose under short headings, no preamble and no closing remarks, and length in proportion to what "
        + "happened: a short conversation compresses to a short briefing.";

    ///<summary>How a briefing is introduced when it is sent, since nobody typed it.</summary>
    public const string SummaryPreface = "Summary of the earlier conversation, compacted to save tokens:";

    ///<summary>What a reopened chat tells the model after the page's own line, since what its tools read may have moved on since.</summary>
    public const string ReopenedWarning = "What the tools above returned was read when they ran, and may have changed since: call one again before relying on what it said.";

    ///<summary>The window's name, which a chat saved as text is headed with.</summary>
    public string Name { get; set; } = "AI";

    ///<summary>
    ///Lifts what an answer proposes out of its words, to be shown on its own (<see cref="ChatTurn.Query"/>) with the words
    ///around it kept: Ask AI's fenced query to begin with. Null where answers propose nothing, and they are kept whole; the
    ///shared window sets it from its own parameter, so a plugin's window lifts nothing unless it says so.
    ///</summary>
    public Func<string, string> ProposedOf { get; set; } = NlQueryPrompt.ExtractProposedQuery;

    ///<summary>A window's name as the start of a file name: "Ask AI" saves as ask-ai-….txt.</summary>
    public static string FileNameOf(string name)
    {
        var made = new StringBuilder();

        foreach (var character in (name ?? "").Trim().ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(character))
                made.Append(character);
            else if (made.Length > 0 && made[^1] != '-')
                made.Append('-');

        var file = made.ToString().Trim('-');

        if (file.Length == 0)
            return "ai-chat";

        return file;
    }

    private readonly List<ChatTurn> _turns = new();

    private readonly Dictionary<string, ModelSpend> _spend = new();

    private readonly List<AiAttachment> _pending = new();

    private CancellationTokenSource _cancel;

    ///<summary>The card a call is waiting on, so what it answered goes on the right one. One is enough: both providers run calls one after another.</summary>
    private ChatTurn _pendingTool;

    ///<summary>
    ///Counts conversations, so a run stopped by New chat does not write its ending into the one that replaced it. Moved on
    ///by <see cref="Clear"/>; a run adds its answer, its error or its stop only while it is still the number it started
    ///under (2026-10-14, when a cleared window kept a "Generation canceled." from the answer it stopped).
    ///</summary>
    private int _generation;

    private ToolApprovalMode _mode = ToolApprovalMode.Ask;

    public IReadOnlyList<ChatTurn> Turns => _turns;

    ///<summary>
    ///What this conversation holds as text, for a quote to be looked for in (2026-11-02, a graph drawn from text): the person's
    ///messages, the words of the files they sent that are words, and what the tools answered, an article read among them. A
    ///PDF sent as the file and a picture are not words here; <see cref="SentFilesNotRead"/> says when one was sent. A PDF sent
    ///as its text is (<see cref="AiAttachment.AsText"/>).
    ///</summary>
    public string SourceText()
    {
        var text = new StringBuilder();

        foreach (var turn in _turns)
        {
            if (turn.Kind == ChatTurn.User)
            {
                text.AppendLine(turn.Text);

                foreach (var file in turn.Attachments.Where(IsWords))
                    text.AppendLine(file.Text);
            }
            else if (turn.Kind == ChatTurn.Tool && turn.Result != null && !turn.ResultIsError)
                text.AppendLine(turn.Result);
        }

        return text.ToString();
    }

    ///<summary>Whether the person sent a PDF or a picture, whose words <see cref="SourceText"/> does not hold.</summary>
    public bool SentFilesNotRead()
    {
        return _turns.Any(turn => turn.Kind == ChatTurn.User && turn.Attachments.Any(file => !IsWords(file)));
    }

    private static bool IsWords(AiAttachment file)
    {
        return file.SentKind == AttachmentKind.Text || file.SentKind == AttachmentKind.Spreadsheet || file.SentKind == AttachmentKind.Document;
    }

    public bool Generating { get; private set; }

    ///<summary>Whether what is running is a compaction rather than an answer (<see cref="CompactAsync"/>).</summary>
    public bool Compacting { get; private set; }

    ///<summary>Whether the suggestion to compact was put away. It comes back after the next compaction, and in a new chat.</summary>
    public bool CompactSuggestionDismissed { get; set; }

    ///<summary>How many rounds of tool calls a message may take before the run pauses to ask.</summary>
    public int MaxToolRounds { get; set; } = LlmProviderFactory.DefaultToolRounds;

    ///<summary>
    ///The tools switched off in the window's tool picker, by name: not offered, refused if called anyway, and named in the
    ///instructions (<see cref="SwitchableToolRunner"/>). <strong>Kept through a new chat</strong>, as GDS Viewer keeps
    ///them: somebody who switched a tool off wants it off in the next conversation too. Not through a reload.
    ///</summary>
    public HashSet<string> ToolsOff { get; } = new(StringComparer.Ordinal);

    ///<summary>
    ///What has been billed, by model, since the page opened. <strong>New chat does not take it back</strong>: what was
    ///spent was spent, and a figure that went to nothing with every new conversation would hide what a day of them came to.
    ///</summary>
    public IReadOnlyDictionary<string, ModelSpend> Spend => _spend;

    ///<summary>
    ///The files waiting to go with the next question, in the order they were attached: the chips over the box. Kept
    ///through a new chat, as the words in the box are.
    ///</summary>
    public IReadOnlyList<AiAttachment> Pending => _pending;

    ///<summary>
    ///Whether the last message failed with files, and gave them back to the box.
    ///
    ///<strong>A file the provider refused would be refused again with every later question</strong>, since each one sends
    ///the conversation whole: a PDF past its page limit, a picture it cannot read. So a question that fails takes its files
    ///off itself and puts them back where one can be taken off before asking again (GDS Viewer's rule, from
    ///ConfigurationRuleEditor); the window puts the question's words back with them when the box is empty. A stop is not a
    ///failure, and keeps them.
    ///</summary>
    public bool GaveFilesBack { get; private set; }

    ///<summary>The bytes of every file the conversation still sends with each question: the ticked ones since it was last compacted.</summary>
    public long SentBytes
    {
        get
        {
            long bytes = 0;

            foreach (var (_, file) in SentFiles())
                if (!file.Excluded)
                    bytes += file.Data.Length;

            return bytes;
        }
    }

    public long PendingBytes => _pending.Sum(file => (long)file.Data.Length);

    ///<summary>Everything billed, every model together.</summary>
    public LlmUsage Used
    {
        get
        {
            var used = new LlmUsage();

            foreach (var model in _spend.Values)
                used.Add(model.Usage);

            return used;
        }
    }

    ///<summary>What the priced part of <see cref="Used"/> cost, or null until something had a rate to be priced at.</summary>
    public decimal? Cost
    {
        get
        {
            decimal? total = null;

            foreach (var model in _spend.Values)
                if (model.Cost is decimal dollars)
                    total = (total ?? 0m) + dollars;

            return total;
        }
    }

    ///<summary>
    ///How far the model may go on its own.
    ///
    ///<strong>Ask before everything, to start with</strong>, and never remembered: the rung that lets the model change
    ///the graph unattended is one nobody should find already on, and a write here cannot be undone.
    ///
    ///<strong>In force from the moment it is chosen</strong>, in the middle of an answer too (2026-11-01, from GDS
    ///Viewer, where it was asked for on 2026-10-21): every call reads it as it is made, and a card already waiting that the
    ///new rung would not have stopped for is let go, approved and marked as run without asking. Until then the rung was
    ///read once as a run started, so a switch to auto-run went on putting up cards until the answer was finished. A rung
    ///that asks more leaves a waiting card waiting, and only stops the calls after it.
    ///</summary>
    public ToolApprovalMode Mode
    {
        get
        {
            return _mode;
        }
        set
        {
            _mode = value;

            if (_pendingTool is not ChatTurn waiting || waiting.Decision != null || waiting.Gate == null)
                return;

            if (ApprovingToolRunner.NeedsApproval(value, waiting.Changes))
                return;

            //Marked only when this is what let it go, rather than a button pressed a moment before.
            if (waiting.Gate.TrySetResult(true))
                waiting.Auto = true;
        }
    }

    ///<summary>Raised whenever the conversation changes, so the window can draw it again.</summary>
    public event Func<Task> Changed;

    ///<summary>
    ///Says something and waits for the answer: with the runner's tools, through the approval gate, or with none.
    ///
    ///The person's words are added before anything is sent, so they are on screen at once; the answer, a refusal or a
    ///stop is added when it comes. Nothing thrown gets out: a failure is a turn that says what went wrong. <strong>What the
    ///run was billed for is counted however it ends</strong>, answered, refused, stopped or cleared away, since each
    ///request of it was billed as it was answered; the turn that ends it carries the figure.
    ///
    ///<strong>The files waiting in the box go with it</strong>, and the box is left empty for the next question; a failure
    ///gives them back (<see cref="GaveFilesBack"/>).
    ///</summary>
    public async Task<ChatOutcome> SendAsync(string text, ILlmProvider provider, string systemPrompt, ILlmToolRunner tools)
    {
        if (Generating || string.IsNullOrWhiteSpace(text))
            return ChatOutcome.NotSent;

        var question = new ChatTurn { Kind = ChatTurn.User, Text = text.Trim() };

        question.Attachments.AddRange(_pending);
        _pending.Clear();
        _turns.Add(question);

        var conversation = BuildConversation();
        var asOf = _generation;
        var before = provider.Spent.Copy();
        var outcome = ChatOutcome.Stopped;
        ChatTurn ending = null;
        var source = new CancellationTokenSource();

        Generating = true;
        GaveFilesBack = false;
        _cancel = source;

        await OnChanged();

        try
        {
            LlmResult result;

            if (tools == null)
                result = await provider.CompleteAsync(systemPrompt, conversation, source.Token);
            else
            {
                //The rung as it is when each call is made, not as it was when this began: see Mode. The tools switched
                //off are left out in front of the gate, so a call to one is refused without a card.
                var approving = new SwitchableToolRunner(new ApprovingToolRunner(tools, ApproveAsync, CompletedAsync, () => Mode), ToolsOff);

                result = await provider.CompleteWithToolsAsync(systemPrompt, conversation, approving, source.Token, MaxToolRounds);
            }

            if (asOf != _generation)
                return ChatOutcome.Stopped;

            if (result.IsError)
            {
                ending = new ChatTurn { Kind = ChatTurn.Error, Text = result.Error };
                outcome = ChatOutcome.Failed;
            }
            else if (result.IsPaused)
            {
                ending = new ChatTurn { Kind = ChatTurn.Paused, Text = result.Text };
                outcome = ChatOutcome.Paused;
            }
            else
            {
                ending = Answer(result.Text);
                outcome = ChatOutcome.Answered;
            }

            _turns.Add(ending);
        }
        catch (OperationCanceledException)
        {
            outcome = ChatOutcome.Stopped;

            if (asOf == _generation)
            {
                ending = new ChatTurn { Kind = ChatTurn.Error, Text = "Stopped." };
                _turns.Add(ending);
            }
        }
        catch (Exception failed)
        {
            //The providers turn what they meet into the answer's error, a server that cannot be reached among it, so
            //little reaches this.
            outcome = ChatOutcome.Failed;

            if (asOf == _generation)
            {
                ending = new ChatTurn { Kind = ChatTurn.Error, Text = failed.Message };
                _turns.Add(ending);
            }
        }
        finally
        {
            Record(provider, before, ending);

            if (asOf == _generation)
            {
                Generating = false;
                _pendingTool = null;

                if (outcome == ChatOutcome.Failed)
                    GiveFilesBack(question);
            }

            if (ReferenceEquals(_cancel, source))
                _cancel = null;

            source.Dispose();

            await OnChanged();
        }

        return outcome;
    }

    ///<summary>
    ///Asks the model for a briefing that can stand in for the conversation so far, and puts it in its place (2026-11-02, GDS
    ///Viewer's, from ConfigurationRuleEditor): a conversation only grows, and every question sends all of it again, its
    ///files included. See <see cref="ChatTurn.Summary"/>.
    ///
    ///<strong>One request, no tools</strong>: the transcript, questions, answers and each call with what came of it, under
    ///<see cref="CompactSystem"/>, with the files still ticked, on the model picked. <strong>What was folded away stays on
    ///screen and is not sent</strong>, and its files stay on the briefing, unticked: what was used from them is in the
    ///briefing now, and sending them with every question after it would go on paying for what compacting stops paying for.
    ///<strong>On any failure, an error, a refusal or Stop, the conversation is as it was</strong>, with a line saying so;
    ///what the request was billed for is counted either way.
    ///</summary>
    public async Task<ChatOutcome> CompactAsync(ILlmProvider provider)
    {
        if (WhyNotCompact() != null)
            return ChatOutcome.NotSent;

        var files = new List<AiAttachment>();

        foreach (var (_, file) in SentFiles())
            if (!file.Excluded)
                files.Add(file);

        var request = new List<LlmMessage> { LlmMessage.FromUser(CompactionAsk(), files.ToArray()) };
        var asOf = _generation;
        var before = provider.Spent.Copy();
        var outcome = ChatOutcome.Stopped;
        ChatTurn ending = null;
        var source = new CancellationTokenSource();

        Generating = true;
        Compacting = true;
        _cancel = source;

        await OnChanged();

        try
        {
            var result = await provider.CompleteAsync(CompactSystem, request, source.Token);

            if (asOf != _generation)
                return ChatOutcome.Stopped;

            if (result.IsError || string.IsNullOrWhiteSpace(result.Text))
            {
                var why = result.Error;

                if (!result.IsError)
                    why = "The model wrote no briefing.";

                ending = new ChatTurn { Kind = ChatTurn.Error, Text = $"The conversation could not be compacted, and is as it was. {why}" };
                outcome = ChatOutcome.Failed;
            }
            else
            {
                ending = new ChatTurn { Kind = ChatTurn.Summary, Text = result.Text.Trim() };

                foreach (var (_, file) in SentFiles())
                    ending.Attachments.Add(file.Unticked());

                outcome = ChatOutcome.Answered;
                CompactSuggestionDismissed = false;
            }

            _turns.Add(ending);
        }
        catch (OperationCanceledException)
        {
            outcome = ChatOutcome.Stopped;

            if (asOf == _generation)
            {
                ending = new ChatTurn { Kind = ChatTurn.Error, Text = "Compacting was stopped. The conversation is as it was." };
                _turns.Add(ending);
            }
        }
        catch (Exception failed)
        {
            outcome = ChatOutcome.Failed;

            if (asOf == _generation)
            {
                ending = new ChatTurn { Kind = ChatTurn.Error, Text = $"The conversation could not be compacted, and is as it was. {failed.Message}" };
                _turns.Add(ending);
            }
        }
        finally
        {
            Record(provider, before, ending);

            if (asOf == _generation)
            {
                Generating = false;
                Compacting = false;
            }

            if (ReferenceEquals(_cancel, source))
                _cancel = null;

            source.Dispose();

            await OnChanged();
        }

        return outcome;
    }

    ///<summary>Why the conversation cannot be compacted now, or null when it can.</summary>
    public string WhyNotCompact()
    {
        if (Generating)
            return "Not while an answer is coming";

        if (QuestionsSinceCompacted > 0)
            return null;

        var from = FoldedBefore;

        if (from < _turns.Count && _turns[from].Kind == ChatTurn.Summary)
            return "Nothing has been asked since it was last compacted";

        return "There is nothing to compact yet";
    }

    ///<summary>The questions asked since the conversation began or was last compacted.</summary>
    public int QuestionsSinceCompacted
    {
        get
        {
            var questions = 0;

            for (var i = FoldedBefore; i < _turns.Count; i++)
                if (_turns[i].Kind == ChatTurn.User)
                    questions++;

            return questions;
        }
    }

    ///<summary>
    ///About how many tokens of history every question sends again, the part a compaction replaces: the conversation since
    ///it was last compacted, with its ticked files, and not the instructions or the tools, which go with every question
    ///whatever is done to it.
    ///</summary>
    public long HistoryTokens(string providerType)
    {
        long tokens = 0;

        foreach (var message in BuildConversation())
            tokens += AiEstimate.Message(message, providerType);

        return tokens;
    }

    ///<summary>
    ///Whether to suggest compacting: enough has been asked since the conversation began or was last compacted, and the
    ///history every question sends again has passed <see cref="CompactAfterTokens"/>. A compaction costs a reading of that
    ///history, so it is only worth it where there is a lot to stop sending and a conversation likely to go on.
    ///</summary>
    public bool WorthCompacting(string providerType)
    {
        if (Generating || CompactSuggestionDismissed)
            return false;

        if (QuestionsSinceCompacted < QuestionsBeforeCompacting)
            return false;

        return HistoryTokens(providerType) >= CompactAfterTokens;
    }

    ///<summary>What compacting would cost at <paramref name="price"/>: the history and the instructions read once, and a briefing written. Null where there is no rate.</summary>
    public static decimal? CompactCost(LlmPrice price, long historyTokens)
    {
        if (price == null)
            return null;

        var usage = new LlmUsage
        {
            InputTokens = historyTokens + AiEstimate.TokensIn(CompactSystem.Length),
            OutputTokens = BriefingTokens,
            Requests = 1
        };

        return price.Cost(usage);
    }

    ///<summary>
    ///The one message of a compaction: the conversation since it was last compacted, as a transcript, an earlier briefing,
    ///the questions and answers and each call with what came of it, and the ask. What was decided goes in with what was
    ///said: a briefing that lost a decline would walk the next answer into asking for the same thing.
    ///</summary>
    private string CompactionAsk()
    {
        var text = new StringBuilder();

        text.AppendLine("Here is the conversation to compress. The files still sent with it, if any, are above.");
        text.AppendLine();
        text.AppendLine("<conversation>");

        var ran = new StringBuilder();

        for (var i = FoldedBefore; i < _turns.Count; i++)
        {
            var turn = _turns[i];

            if (turn.Kind == ChatTurn.Tool)
            {
                AppendRunOf(ran, turn);

                continue;
            }

            FlushCalls(text, ran);

            if (turn.Kind == ChatTurn.Summary)
            {
                text.AppendLine("SUMMARY LEFT BY AN EARLIER COMPACTION:");
                text.AppendLine(turn.Text);
                text.AppendLine();
            }
            else if (turn.Kind == ChatTurn.User)
            {
                text.AppendLine("PERSON:");
                text.AppendLine(turn.Text);
                text.AppendLine();
            }
            else if (turn.Kind == ChatTurn.Assistant)
            {
                text.AppendLine("ASSISTANT:");
                text.AppendLine(Said(turn));
                text.AppendLine();
            }
            else if (turn.Kind == ChatTurn.Error || turn.Kind == ChatTurn.Paused)
            {
                text.AppendLine($"(The answer ended there: {turn.Text})");
                text.AppendLine();
            }
        }

        FlushCalls(text, ran);

        text.AppendLine("</conversation>");
        text.AppendLine();
        text.Append("Write the briefing now.");

        return text.ToString();
    }

    private static void FlushCalls(StringBuilder text, StringBuilder ran)
    {
        if (ran.Length == 0)
            return;

        text.AppendLine("ASSISTANT'S TOOL CALLS:");
        text.Append(ran);
        text.AppendLine();
        ran.Clear();
    }

    ///<summary>One call in the compaction's transcript, said from outside rather than to the model that made it.</summary>
    private static void AppendRunOf(StringBuilder ran, ChatTurn turn)
    {
        var called = $"{turn.ToolName} with:\n{Shortened(turn.Query ?? turn.ArgumentsJson, MostArgumentsReplayed)}\n";

        if (turn.Decision == false)
            ran.AppendLine($"- asked to run {called}and the person declined.");
        else if (turn.Decision == null || turn.Result == null)
            ran.AppendLine($"- ran {called}and it did not finish.");
        else if (turn.ResultIsError)
            ran.AppendLine($"- ran {called}and it was refused: {Shortened(turn.Result, MostResultReplayed)}");
        else
            ran.AppendLine($"- ran {called}and it returned: {Shortened(turn.Result, MostResultReplayed)}");
    }

    ///<summary>
    ///Where the turns a compaction folded away end, which is where the conversation the model is sent starts: the last
    ///briefing's place, or 0 when there is none. The window shows what is before it only when asked, dimmed: it is still
    ///there to read, and none of it is sent.
    ///</summary>
    public int FoldedBefore
    {
        get
        {
            for (var i = _turns.Count - 1; i >= 0; i--)
                if (_turns[i].Kind == ChatTurn.Summary)
                    return i;

            return 0;
        }
    }

    ///<summary>
    ///An answer as its turn: what it proposed lifted out (<see cref="ProposedOf"/>), shown on its own below, and the words
    ///around it kept, since the bubble would otherwise say it twice.
    ///</summary>
    private ChatTurn Answer(string text)
    {
        if (ProposedOf == null)
            return new ChatTurn { Kind = ChatTurn.Assistant, Text = text };

        var proposed = ProposedOf(text);

        if (proposed == null)
            return new ChatTurn { Kind = ChatTurn.Assistant, Text = text };
        else
            return new ChatTurn { Kind = ChatTurn.Assistant, Text = LlmText.WithoutFencedBlock(text), Query = proposed };
    }

    ///<summary>
    ///The conversation written out as text, to read back another day (2026-11-02, GDS Viewer's, from ConfigurationRuleEditor):
    ///every turn there is, the part a compaction folded away included and each briefing headed as one, the queries proposed
    ///and run with what each returned in full, and what each answer was billed. The files are named, with their sizes, not
    ///included: they are still on the machine they came from. <paramref name="subject"/> is the database it was about, or
    ///empty.
    ///</summary>
    public string Transcript(string subject, DateTime at)
    {
        var text = new StringBuilder();

        if (string.IsNullOrEmpty(subject))
            text.AppendLine($"{Name} conversation");
        else
            text.AppendLine($"{Name} conversation - {subject}");

        text.AppendLine($"Exported {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");

        foreach (var turn in _turns)
        {
            text.AppendLine();

            if (turn.Kind == ChatTurn.User)
            {
                text.AppendLine("YOU");
                text.AppendLine(turn.Text);
                AppendFiles(text, "Attached", turn.Attachments);
            }
            else if (turn.Kind == ChatTurn.Assistant)
            {
                text.AppendLine("ASSISTANT");
                text.AppendLine(turn.Text);

                if (!string.IsNullOrWhiteSpace(turn.Query))
                {
                    text.AppendLine("  Proposed query:");
                    AppendIndented(text, turn.Query);
                }
            }
            else if (turn.Kind == ChatTurn.Tool)
                AppendCall(text, turn);
            else if (turn.Kind == ChatTurn.Summary)
            {
                text.AppendLine("SUMMARY OF THE ABOVE (compacted)");
                text.AppendLine(turn.Text);
                AppendFiles(text, "Kept from before it", turn.Attachments);
            }
            else
                text.AppendLine($"({turn.Text})");

            if (turn.Usage != null)
                text.AppendLine($"  Billed: {UsageLine(turn)}, on {turn.Model}");
        }

        return text.ToString();
    }

    private static void AppendFiles(StringBuilder text, string said, IReadOnlyList<AiAttachment> files)
    {
        if (files.Count == 0)
            return;

        var named = new List<string>();

        foreach (var file in files)
        {
            var size = AiAttachment.Size(file.Data.Length);

            if (file.Excluded)
                named.Add($"{file.Name} ({size}, not sent)");
            else
                named.Add($"{file.Name} ({size})");
        }

        text.AppendLine($"  {said}: {string.Join(", ", named)}");
    }

    ///<summary>A call, in the export: what was asked for, and what came of it in full.</summary>
    private static void AppendCall(StringBuilder text, ChatTurn turn)
    {
        var called = turn.Query ?? turn.ArgumentsJson;

        if (turn.Decision == false)
        {
            text.AppendLine($"  Asked to run {turn.ToolName}, and was declined:");
            AppendIndented(text, called);

            return;
        }

        if (turn.Decision == null || turn.Result == null)
        {
            text.AppendLine($"  Ran {turn.ToolName}, and it did not finish:");
            AppendIndented(text, called);

            return;
        }

        text.AppendLine($"  Ran {turn.ToolName}:");
        AppendIndented(text, called);

        if (turn.ResultIsError)
            text.AppendLine("  It was refused:");
        else
            text.AppendLine("  It returned:");

        AppendIndented(text, turn.Result);

        if (turn.Png != null)
            text.AppendLine("    (and a picture)");
    }

    private static void AppendIndented(StringBuilder text, string lines)
    {
        foreach (var line in (lines ?? "").Trim().Split('\n'))
            text.AppendLine($"    {line.TrimEnd('\r')}");
    }

    ///<summary>What a run was billed for, as one line: the requests it took, the tokens each way, and the cost where there is a rate.</summary>
    public static string UsageLine(ChatTurn turn)
    {
        if (turn.Usage is not LlmUsage used)
            return "";

        var line = new StringBuilder();

        if (used.Requests == 1)
            line.Append("1 request");
        else
            line.Append($"{used.Requests} requests");

        line.Append($" · {AiPricing.Count(used.InputTokens)} tokens in");

        if (used.CachedInputTokens > 0)
            line.Append($" ({AiPricing.Count(used.CachedInputTokens)} cached)");

        line.Append($" · {AiPricing.Count(used.OutputTokens)} out");

        if (turn.Cost is decimal dollars)
            line.Append($" · {AiPricing.Money(dollars)}");

        return line.ToString();
    }

    ///<summary>Answers the card a call is waiting on.</summary>
    public void Decide(ChatTurn turn, bool approved)
    {
        turn.Gate?.TrySetResult(approved);
    }

    ///<summary>Stops the run in progress. A card still waiting is answered no, which is what a stop is.</summary>
    public void Cancel()
    {
        _cancel?.Cancel();
    }

    ///<summary>
    ///Throws the conversation away and starts a new one. Stopped first: a run left going would add its answer to a
    ///conversation that no longer has the question in it, and its ending is not added at all (<see cref="_generation"/>).
    ///What has been spent stays.
    ///</summary>
    public void Clear()
    {
        Cancel();

        _generation++;
        _turns.Clear();
        _pendingTool = null;
        Generating = false;
        Compacting = false;
        CompactSuggestionDismissed = false;
    }

    ///<summary>
    ///Puts a kept chat back in place of the conversation, as <see cref="Clear"/> leaves it and with these turns, then, when
    ///there are any, a <see cref="ChatTurn.Reopened"/> line saying so. One line however often it is opened: a chat opened
    ///twice without a word in between does not say it twice. The files waiting in the box, the tools switched off and what
    ///has been spent stay, as they do through Clear. No line when there is nothing to say: a chat only being saved as text.
    ///</summary>
    public void Restore(IEnumerable<ChatTurn> kept, string reopened)
    {
        Clear();

        _turns.AddRange(kept);

        if (_turns.Count == 0 || reopened == null)
            return;

        if (_turns[^1].Kind == ChatTurn.Reopened)
            _turns[^1].Text = reopened;
        else
            _turns.Add(new ChatTurn { Kind = ChatTurn.Reopened, Text = reopened });
    }

    ///<summary>
    ///Says why what was typed was not sent. A turn of its own that the model is never sent, since it is the page talking
    ///rather than either side of the conversation.
    ///</summary>
    public void Refuse(string why)
    {
        _turns.Add(new ChatTurn { Kind = ChatTurn.Error, Text = why });
    }

    ///<summary>
    ///The conversation as the model should see it: everything from the last briefing, which goes as the person's message,
    ///introduced as what it is, with the files kept on it.
    ///
    ///<strong>What was run is replayed in words</strong> rather than dropped. Its answer is what the model's next reply was
    ///built on, and neither provider's message list can carry a tool exchange across a request without rebuilding that
    ///provider's format from a copy of it, so each run is said plainly. The calls of one answer go together in one turn
    ///(2026-11-01, from GDS Viewer; one turn per query until then), each cut short where it runs long.
    ///</summary>
    public List<LlmMessage> BuildConversation()
    {
        var conversation = new List<LlmMessage>();
        var ran = new StringBuilder();

        for (var i = FoldedBefore; i < _turns.Count; i++)
        {
            var turn = _turns[i];

            if (turn.Kind == ChatTurn.Tool)
            {
                AppendRun(ran, turn);

                continue;
            }

            FlushRuns(conversation, ran);

            if (turn.Kind == ChatTurn.User)
                conversation.Add(LlmMessage.FromUser(turn.Text, turn.Attachments.ToArray()));
            else if (turn.Kind == ChatTurn.Assistant)
                conversation.Add(LlmMessage.FromAssistant(Said(turn)));
            else if (turn.Kind == ChatTurn.Summary)
                conversation.Add(LlmMessage.FromUser($"{SummaryPreface}\n\n{turn.Text}", turn.Attachments.ToArray()));
            else if (turn.Kind == ChatTurn.Reopened)
                conversation.Add(LlmMessage.FromUser($"(From the page: {turn.Text} {ReopenedWarning})"));
        }

        FlushRuns(conversation, ran);

        return conversation;
    }

    ///<summary>An answer as the model wrote it: its words, with the query it proposed put back in its fence.</summary>
    private static string Said(ChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Query))
            return turn.Text;

        return $"{turn.Text}\n\n```\n{turn.Query}\n```".Trim();
    }

    private static void AppendRun(StringBuilder ran, ChatTurn turn)
    {
        if (turn.Decision == null)
            return;

        if (ran.Length == 0)
            ran.Append("(What you did while answering that:");

        ran.AppendLine();

        var called = $"{turn.ToolName} with:\n{Shortened(turn.Query ?? turn.ArgumentsJson, MostArgumentsReplayed)}\n";

        if (turn.Decision == false)
            ran.Append($"- You asked to run {called}and I declined, so it was not run.");
        else if (turn.Result == null)
            ran.Append($"- You ran {called}and it did not finish.");
        else if (turn.ResultIsError)
            ran.Append($"- You ran {called}and it was refused: {Shortened(turn.Result, MostResultReplayed)}");
        else
            ran.Append($"- You ran {called}and it returned: {Shortened(turn.Result, MostResultReplayed)}");
    }

    private static void FlushRuns(List<LlmMessage> conversation, StringBuilder ran)
    {
        if (ran.Length == 0)
            return;

        ran.Append(')');

        conversation.Add(LlmMessage.FromUser(ran.ToString()));
        ran.Clear();
    }

    private static string Shortened(string text, int most)
    {
        var flat = (text ?? "").Trim();

        if (flat.Length <= most)
            return flat;

        return $"{flat.Substring(0, most)}... ({flat.Length - most} more characters not repeated here)";
    }

    ///<summary>
    ///Puts a file in the box for the next question, or says why not: what the file is (<see cref="AiAttachment.Problem"/>),
    ///how many one question carries, how much one conversation sends, and whether a workbook or a document can be read.
    ///</summary>
    public string Attach(AiAttachment file)
    {
        if (AiAttachment.Problem(file.Name, file.MediaType, file.Data) is string problem)
            return problem;

        if (_pending.Count >= AiAttachment.MostPerQuestion)
            return $"A question can carry {AiAttachment.MostPerQuestion} files at most, so {file.Name} was left out. Send these with a question first.";

        var total = SentBytes + PendingBytes + file.Data.Length;

        if (total > AiAttachment.MostConversationBytes)
            return $"{file.Name} would bring this conversation's files to {AiAttachment.Size(total)}, past the {AiAttachment.Size(AiAttachment.MostConversationBytes)} one conversation can send, since every question sends them all again. Untick a file in the files panel, or start a new chat.";

        //Last, as the one that reads a workbook or a document through.
        if (file.Unreadable is string unreadable)
            return unreadable;

        _pending.Add(file);

        return null;
    }

    ///<summary>Takes a file out of the box before it was sent.</summary>
    public void Unattach(AiAttachment file)
    {
        _pending.Remove(file);
    }

    ///<summary>
    ///Ticks a file sent earlier back in, or unticks it, from the files panel, or says why it cannot be ticked.
    ///
    ///Unticking is always allowed: it is the way out of a conversation that can take no more. Ticking one in again is held
    ///to the same budget as attaching it was.
    ///</summary>
    public string Include(AiAttachment file, bool include)
    {
        if (!include)
        {
            file.Excluded = true;

            return null;
        }

        if (!file.Excluded)
            return null;

        var total = SentBytes + PendingBytes + file.Data.Length;

        if (total > AiAttachment.MostConversationBytes)
            return $"{file.Name} would bring this conversation's files to {AiAttachment.Size(total)}, past the {AiAttachment.Size(AiAttachment.MostConversationBytes)} one conversation can send. Untick another first.";

        file.Excluded = false;

        return null;
    }

    ///<summary>
    ///Every file sent since the conversation was last compacted, with the question it went with, or the briefing that keeps
    ///it after a compaction: what the files panel lists, ticked or not.
    ///</summary>
    public List<(ChatTurn Question, AiAttachment File)> SentFiles()
    {
        var files = new List<(ChatTurn Question, AiAttachment File)>();

        for (var i = FoldedBefore; i < _turns.Count; i++)
            foreach (var file in _turns[i].Attachments)
                files.Add((_turns[i], file));

        return files;
    }

    ///<summary>A failed question's files back in the box, ahead of any attached since (<see cref="GaveFilesBack"/>).</summary>
    private void GiveFilesBack(ChatTurn question)
    {
        if (question.Attachments.Count == 0)
            return;

        _pending.InsertRange(0, question.Attachments);
        question.Attachments.Clear();

        GaveFilesBack = true;
    }

    ///<summary>A run's share of what its provider was billed, put on the turn that ended it and added to <see cref="Spend"/>.</summary>
    private void Record(ILlmProvider provider, LlmUsage before, ChatTurn ending)
    {
        var used = provider.Spent.Since(before);

        if (!used.Reported)
            return;

        decimal? cost = null;

        if (provider.Price is LlmPrice price)
            cost = price.Cost(used);

        if (ending != null)
        {
            ending.Usage = used;
            ending.Model = provider.Model;
            ending.Cost = cost;
        }

        if (!_spend.TryGetValue(provider.Model, out var spent))
        {
            spent = new ModelSpend();
            _spend[provider.Model] = spent;
        }

        spent.Usage.Add(used);

        if (cost is decimal dollars)
            spent.Cost = (spent.Cost ?? 0m) + dollars;
    }

    ///<summary>
    ///Puts the model's call on screen, and waits for a button to answer it unless the mode already has. A call the mode let
    ///through still gets a card, since a query that runs unwatched is the one most worth seeing afterwards; it just does not
    ///stop for anybody.
    ///</summary>
    private async Task<bool> ApproveAsync(ToolRunRequest request, CancellationToken ct)
    {
        //A run stopped, or cleared away, between the model's answer and its call puts up no card: it would land in
        //whatever conversation is on screen now.
        ct.ThrowIfCancellationRequested();

        var turn = new ChatTurn
        {
            Kind = ChatTurn.Tool,
            ToolName = request.ToolName,
            ArgumentsJson = request.ArgumentsJson,
            Query = request.Query,
            Changes = request.IsWrite
        };

        //A proposal went ahead because there was nothing in it to approve, not because of the rung, so it is not marked as one
        //the rung let through.
        if (request.AutoApproved)
        {
            turn.Auto = !request.Proposes;
            turn.Decision = true;

            _turns.Add(turn);
            _pendingTool = turn;

            await OnChanged();

            return true;
        }

        //Continuations run on their own, so a button's handler that answers the card does not go on to run the rest of
        //the provider's loop inside its own render.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        turn.Gate = gate;

        _turns.Add(turn);
        _pendingTool = turn;

        await OnChanged();

        //A stop while a card is unanswered has to let it go, or the loop waits for a button nobody can press. A stop is
        //not a yes, so it answers the way declining does.
        using var registration = ct.Register(() => gate.TrySetResult(false));

        var approved = await gate.Task;

        turn.Decision = approved;

        await OnChanged();

        return approved;
    }

    ///<summary>Writes what the call answered onto the card that asked for it.</summary>
    private async Task CompletedAsync(ToolRunRequest request, LlmToolOutput output)
    {
        if (_pendingTool != null && output != null)
        {
            _pendingTool.Result = output.Text;
            _pendingTool.ResultIsError = output.IsError;
            _pendingTool.Png = output.Png;
        }

        _pendingTool = null;

        await OnChanged();
    }

    private async Task OnChanged()
    {
        var handler = Changed;

        if (handler == null)
            return;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();
    }
}
