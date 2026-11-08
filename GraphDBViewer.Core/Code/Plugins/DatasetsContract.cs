using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///How the viewer reads a dataset's rows (ADR 0016, question 5: "every store's module gains a read
///surface beside the policy surface — the rows of one dataset, a range, a page").
///
///<strong>The platform's endpoint, not the owning perspective's.</strong> The viewer draws a dataset it
///knows nothing about, and the vertex says which store answers for it — so asking MQTT for the rows
///would make the canvas stop drawing charts the day MQTT is uninstalled, for datasets that are still
///there. The perspective's own page keeps its own endpoint, because it draws the dataset <em>with</em>
///its meaning; these are not in competition (question 5 says so in as many words).
///
///<strong>A window of rows, never the whole payload.</strong> A dataset is the thing that is too big
///for the graph; handing all of it to a browser would be the same mistake one level out. Two ways to
///ask: the newest so many, which is what a chart of "how is it now" wants, or a window between two
///instants, which is how anything older is reached at all.
///</summary>
public static class DatasetsContract
{
    ///<summary>The rows of one dataset: <c>GET api/datasets/{key}/rows</c>.</summary>
    public const string BasePath = "api/datasets";

    public static string RowsPath(string key)
    {
        return $"{BasePath}/{key}/rows";
    }

    ///<summary>
    ///The most rows one look may take. Enough for a chart that reads as a line rather than as a
    ///sketch, and few enough that a canvas with several datasets on it is not a download.
    ///</summary>
    public const int MostRows = 500;

    ///<summary>What the viewer asks for by default — a chart of the recent past, not of everything.</summary>
    public const int SomeRows = 200;

    ///<summary>
    ///The windows the panel offers, and what each is worth.
    ///
    ///<strong>The first is not a window at all</strong> — it is the newest rows whenever they were, and
    ///it is the default because a sensor that stopped publishing last week should still draw something
    ///rather than an empty hour. The rest are how anything older is reached.
    ///</summary>
    public static readonly IReadOnlyList<(string Name, TimeSpan? Window)> Windows =
    [
        ("Recent", null),
        ("Last hour", TimeSpan.FromHours(1)),
        ("Last day", TimeSpan.FromDays(1)),
        ("Last week", TimeSpan.FromDays(7)),
        ("Last month", TimeSpan.FromDays(30))
    ];

    ///<summary>The rows of one dataset within a window, as the panel asks for them.</summary>
    public static string RowsPath(string key, TimeSpan? window, DateTimeOffset now)
    {
        var path = $"{RowsPath(key)}?limit={SomeRows}";

        if (window == null)
            return path;

        //Half-open, as the store reads it: at least from, before to.
        return $"{path}&from={Stamp(now - window.Value)}&to={Stamp(now.AddSeconds(1))}";
    }

    private static string Stamp(DateTimeOffset at)
    {
        return Uri.EscapeDataString(at.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }
}

///<summary>One row of a dataset, as generic as the declaration that describes it.</summary>
public sealed class DatasetRow
{
    ///<summary>Which channel within the dataset — one topic of a wildcard mapping, one sensor of a rig.</summary>
    [JsonPropertyName("series")]
    public string Series { get; set; }

    ///<summary>
    ///When a series' row was received, which orders the rows (<c>received_at</c>, named so on 2026-10-05). Unset on a
    ///table's row, whose time, when it has one, is one of its <see cref="Values"/>.
    ///</summary>
    [JsonPropertyName("receivedAt")]
    public DateTimeOffset ReceivedAt { get; set; }

    ///<summary>The numeric value, when the row has one. A row is a number or a word.</summary>
    [JsonPropertyName("number")]
    public double? Number { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; }

    ///<summary>
    ///When the row's source said it was, on a series' row whose device sends its own time and whose instance keeps it
    ///(<c>recorded_at</c>, 2026-10-04); null otherwise. <see cref="ReceivedAt"/> is when it was received, and orders
    ///the rows.
    ///</summary>
    [JsonPropertyName("recordedAt")]
    public DateTimeOffset? RecordedAt { get; set; }

    ///<summary>
    ///A table's row: every declared column's value by the column's name, as the store handed it over. Null on
    ///a series' row, whose values are the fields above. The panel draws a cell from here when a row has
    ///it, so a table of any columns draws under the headings its declaration names (ADR 0016 question 5).
    ///</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; }
}

///<summary>
///What one look at a dataset answers: its rows, and why there are none when there are none.
///
///<strong>A problem is a sentence rather than a status.</strong> The panel sits on a canvas beside
///other things that are working — a dataset whose store is not configured has to say so where it is
///drawn, not fail the page around it.
///</summary>
public sealed class DatasetRows
{
    [JsonPropertyName("rows")]
    public List<DatasetRow> Rows { get; set; } = new();

    ///<summary>The distinct channels the rows came from, in the order they should be drawn.</summary>
    [JsonPropertyName("series")]
    public List<string> Series { get; set; } = new();

    ///<summary>Why there is nothing to draw, or null.</summary>
    [JsonPropertyName("problem")]
    public string Problem { get; set; }

    ///<summary>
    ///What the rows leave out, in a sentence said beside them, or null. A problem is why nothing is drawn; this is
    ///why what is drawn is not all there is, as when one file of a table kept as files cannot be drawn and the rest
    ///can (2026-09-24).
    ///</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; }

    ///<summary>Whether the answer hit the cap — so the panel can say it is showing the most recent rows rather than all of them.</summary>
    [JsonPropertyName("capped")]
    public bool Capped { get; set; }

    ///<summary>The window that was asked for, echoed back. Null for "the newest, whenever they were".</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; set; }

    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; set; }
}
