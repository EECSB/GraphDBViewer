using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///What the embedded viewer answers a query the page around it sent (2026-10-23, the embedded viewer's messages,
///Home.EmbedMessages.cs): how many vertices and edges the answer drew, or why it drew none. EmbedMessagesInterop.js
///posts it as <c>treeality:result</c>, beside the id the query came with.
///</summary>
public sealed class EmbedQueryAnswer
{
    public bool Ok { get; init; }

    public int Vertices { get; init; }

    public int Edges { get; init; }

    ///<summary>Why nothing was drawn, when <see cref="Ok"/> is false; null otherwise.</summary>
    public string Error { get; init; }

    ///<summary>Said when a query arrives and the page was never given the messages.</summary>
    public const string Off = "This viewer takes no messages: its address names no origin for the page around it.";

    ///<summary>Said when a query arrives with no text.</summary>
    public const string Blank = "No query was sent.";

    ///<summary>Said when a query arrives while another one is still running: one at a time, and the second is not queued.</summary>
    public const string Busy = "Another query is still running. Send this one when its result has come back.";

    ///<summary>Said for a query the engine's own guard reads as a write: nothing a message sends edits the graph.</summary>
    public const string Edits = "The embedded viewer only reads, and this query would change the graph, so it was not run.";

    ///<summary>Said when a run ended with neither an answer nor an error, which is a run that was stopped.</summary>
    public const string Stopped = "The query was stopped before it answered.";

    public static EmbedQueryAnswer Refused(string error)
    {
        return new EmbedQueryAnswer { Ok = false, Error = error };
    }

    ///<summary>Said for a query that answered something the viewer could not read as a graph, with what reading it found.</summary>
    public static string Unreadable(string problem)
    {
        return $"The viewer could not read the result as a graph ({problem}).";
    }

    ///<summary>
    ///The answer to a query that ran: its error, or the vertices and edges of the graph it answered, counted the way the
    ///viewer's own stats line counts them. An answer of rows (a SPARQL SELECT, a Cypher RETURN of values) or of values
    ///that are no graph ran and drew nothing, so it is counted 0 and 0. A run with neither was stopped.
    ///</summary>
    public static EmbedQueryAnswer After(string error, bool rows, JsonElement data)
    {
        if (!string.IsNullOrEmpty(error))
            return Refused(error);

        if (rows)
            return new EmbedQueryAnswer { Ok = true };

        if (data.ValueKind == JsonValueKind.Undefined)
            return Refused(Stopped);

        var table = GraphDataConverter.ToTable(data);

        return new EmbedQueryAnswer { Ok = true, Vertices = table.Nodes.Count, Edges = table.Edges.Count };
    }
}

///<summary>
///The element selected in the embedded viewer, as the page around it is told of it (2026-10-23, the embedded viewer's
///messages): whether it is a vertex or an edge, its id, its label and its properties. EmbedMessagesInterop.js posts it as
///<c>treeality:selection</c>, or null there when nothing is selected.
///</summary>
public sealed class EmbedSelection
{
    public const string Vertex = "vertex";
    public const string Edge = "edge";

    ///<summary><see cref="Vertex"/> or <see cref="Edge"/>.</summary>
    public string Kind { get; init; }

    public string Id { get; init; }

    ///<summary>The element's label in the graph: a vertex's type, not the caption the canvas draws under it.</summary>
    public string Label { get; init; }

    public Dictionary<string, string> Properties { get; init; } = new();

    ///<summary>What the page is told of a selection, or null when nothing is selected.</summary>
    public static EmbedSelection Of(SelectedElementInfo element)
    {
        if (element == null || string.IsNullOrEmpty(element.Id))
            return null;

        string kind;

        if (element.Type == "edge")
            kind = Edge;
        else
            kind = Vertex;

        //A vertex clicked on the canvas carries its caption as Label and its type as GLabel; one picked from the table,
        //and every edge, carries the type as Label.
        string label;

        if (!string.IsNullOrEmpty(element.GLabel))
            label = element.GLabel;
        else
            label = element.Label ?? "";

        var properties = new Dictionary<string, string>();

        if (element.EditableProperties != null)
            foreach (var property in element.EditableProperties)
                properties[property.Key] = property.Value;

        return new EmbedSelection { Kind = kind, Id = element.Id, Label = label, Properties = properties };
    }

    ///<summary>Which element a selection is, so a page is told again only when that changes: empty for none.</summary>
    public static string KeyOf(EmbedSelection selection)
    {
        if (selection == null)
            return "";

        return selection.Kind + "\n" + selection.Id;
    }
}
