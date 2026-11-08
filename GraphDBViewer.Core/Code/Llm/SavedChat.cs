using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///<strong>An Ask AI conversation as it is kept between visits</strong> (2026-11-02, GDS Viewer's, where it was asked for on
///2026-10-31 like the Claude app's): every question, answer and call, so a chat can be opened again and carried on, or saved
///as text. Kept by a <see cref="ChatStore"/>: with the account in Treeality, in the browser in the standalone viewer.
///
///<strong>Its own shape rather than the window's turns written out</strong>: a turn holds things that only mean anything
///while the page is up, the gate a waiting card is let go by, and the names written here are pinned, so a chat kept by an
///earlier version still reads. See <see cref="SavedTurn"/>.
///</summary>
public sealed class SavedChat
{
    ///<summary>How long a title is before it is cut: about a row of the chats list.</summary>
    public const int MostTitleCharacters = 80;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    ///<summary>What the list calls it: the first question, cut to a row.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    ///<summary>The database that was connected when it was last kept, as the bar names it, for the list to say what it was about.</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("created")]
    public DateTime Created { get; set; }

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }

    [JsonPropertyName("turns")]
    public List<SavedTurn> Turns { get; set; } = new();

    ///<summary>A new id, unique enough for one person's chats wherever they are kept.</summary>
    public static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    ///<summary>The row the list shows for it.</summary>
    public ChatSummary Summary()
    {
        return new ChatSummary { Id = Id, Title = Title, Subject = Subject, Updated = Updated };
    }

    ///<summary>
    ///What a conversation is called: its first question, on one line and cut to <see cref="MostTitleCharacters"/>. "New
    ///chat" when nothing has been asked yet, which the list shows only for the chat on screen, since a chat is kept from its
    ///first answer on.
    ///</summary>
    public static string TitleFor(IEnumerable<ChatTurn> turns)
    {
        var first = turns.FirstOrDefault(turn => turn.Kind == ChatTurn.User);

        if (first == null)
            return "New chat";

        var flat = string.Join(" ", first.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        if (flat.Length <= MostTitleCharacters)
            return flat;

        return flat.Substring(0, MostTitleCharacters).TrimEnd() + "…";
    }

    ///<summary>A conversation's turns, kept. The window's own are left as they are.</summary>
    public static List<SavedTurn> Keep(IEnumerable<ChatTurn> turns)
    {
        return turns.Select(SavedTurn.From).ToList();
    }

    ///<summary>The turns again, to put back into a conversation (<see cref="AiChat.Restore"/>).</summary>
    public List<ChatTurn> Unkept()
    {
        return Turns.Select(turn => turn.ToTurn()).ToList();
    }
}

///<summary>One row of the chats list: enough to name a chat and order it, without reading every turn of every chat.</summary>
public sealed class ChatSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; }
}

///<summary>
///One <see cref="ChatTurn"/> as it is kept. A card that was still waiting when it was kept comes back answered no, since
///nothing can let it go after a reload, which is what a stop does to one.
///</summary>
public sealed class SavedTurn
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ChatTurn.Error;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("query")]
    public string Query { get; set; }

    [JsonPropertyName("tool")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string ArgumentsJson { get; set; } = "";

    [JsonPropertyName("changes")]
    public bool Changes { get; set; }

    [JsonPropertyName("auto")]
    public bool Auto { get; set; }

    [JsonPropertyName("decision")]
    public bool? Decision { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; }

    [JsonPropertyName("resultIsError")]
    public bool ResultIsError { get; set; }

    [JsonPropertyName("png")]
    public string Png { get; set; }

    [JsonPropertyName("files")]
    public List<SavedFile> Files { get; set; } = new();

    [JsonPropertyName("usage")]
    public LlmUsage Usage { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }

    public static SavedTurn From(ChatTurn turn)
    {
        var kept = new SavedTurn
        {
            Kind = turn.Kind,
            Text = turn.Text,
            Query = turn.Query,
            ToolName = turn.ToolName,
            ArgumentsJson = turn.ArgumentsJson,
            Changes = turn.Changes,
            Auto = turn.Auto,
            Decision = turn.Decision,
            Result = turn.Result,
            ResultIsError = turn.ResultIsError,
            Png = turn.Png,
            Usage = turn.Usage?.Copy(),
            Model = turn.Model,
            Cost = turn.Cost
        };

        if (turn.Kind == ChatTurn.Tool && kept.Decision == null)
            kept.Decision = false;

        foreach (var file in turn.Attachments)
            kept.Files.Add(new SavedFile { Name = file.Name, MediaType = file.MediaType, Data = file.Data, Excluded = file.Excluded });

        return kept;
    }

    public ChatTurn ToTurn()
    {
        var turn = new ChatTurn
        {
            Kind = Kind,
            Text = Text,
            Query = Query,
            ToolName = ToolName,
            ArgumentsJson = ArgumentsJson,
            Changes = Changes,
            Auto = Auto,
            Decision = Decision,
            Result = Result,
            ResultIsError = ResultIsError,
            Png = Png,
            Usage = Usage,
            Model = Model,
            Cost = Cost
        };

        foreach (var file in Files)
            turn.Attachments.Add(new AiAttachment(file.Name, file.MediaType, file.Data) { Excluded = file.Excluded });

        return turn;
    }
}

///<summary>A file a question was sent with, kept with its bytes so it goes with the questions after it as it did before.</summary>
public sealed class SavedFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("excluded")]
    public bool Excluded { get; set; }
}
