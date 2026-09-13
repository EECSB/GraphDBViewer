using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a dataset's column holds, in the vocabulary's own words — the same ones a perspective's fields
///are declared with, so a reader already knows them. Named here because the declaration is a contract
///and a second perspective spelling a kind differently would draw the wrong control.
///</summary>
public static class DatasetColumnKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Flag = "flag";

    ///<summary>ISO 8601, UTC — what the seam writes for a date and what orders a series.</summary>
    public const string Date = "date";
}

///<summary>
///How a vertex the write seam wrote is addressed.
///
///<strong>Not the engine's id.</strong> TinkerGraph hands out a Long, Cosmos a string, JanusGraph
///something else again, and none of them survives a move between engines — so every vertex the seam
///writes carries its own identity property and that is what everything addresses it by.
///
///In the core because <em>both</em> halves need it now: the host writes it, and since the viewer draws
///a dataset it has to read one off the canvas to know which dataset it is looking at.
///</summary>
public static class GraphIdentity
{
    public const string Property = "uid";
}

///<summary>
///The stores the platform has a module for, named here because a <c>Dataset</c> vertex says which one
///answers for its payload and <em>two</em> readers act on that word: the viewer's read surface, which
///dispatches on it, and whoever writes the vertex. A perspective spelling its store's name itself would
///be one of those two holding the only copy.
///</summary>
public static class DatasetStores
{
    ///<summary>PostgreSQL with TimescaleDB, behind <c>IReadingStore</c> (ADR 0016, question 2).</summary>
    public const string Readings = "readings";
}

///<summary>What shape a dataset is, which decides what the viewer draws (ADR 0016, question 5).</summary>
public static class DatasetKinds
{
    ///<summary>An ordered series: rows with a column that orders them. The viewer draws a chart.</summary>
    public const string Series = "series";

    ///<summary>A uniform table with no ordering column. The viewer draws a table.</summary>
    public const string Table = "table";
}

///<summary>
///One column of a dataset, as the declaration names it: what it is called, what kind of value it holds,
///and whether it is the one that orders the rows.
///</summary>
public sealed record DatasetColumn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("orders")] bool Orders = false);

///<summary>
///The figures a dataset's vertex carries about its payload: how many rows, what the last one said and
///when, and <strong>when that was counted</strong>.
///
///The last is not decoration. These are a snapshot, not a live count, so anything reading them has to
///be able to see how old they are — and a dataset whose rows have since aged out is a
///<em>tombstone</em> (ADR 0016 §3), which is only a useful one if the date on it is visible.
///</summary>
public sealed record DatasetStats(long Rows, string Last, DateTimeOffset? LastAt, DateTimeOffset CountedAt);

///<summary>
///The <c>Dataset</c> catalog vertex's declaration (ADR 0016 §2, and question 5 which turned it from
///description into a <strong>contract</strong>): what a dataset is, what columns it holds, which one
///orders them, which store answers for the payload, and the figures as of when they were taken.
///
///<strong>In the core rather than in the host or a perspective.</strong> <c>Dataset</c> is the shared
///label every writer may write and every perspective may read, so the words on it cannot be one
///plugin's — a second perspective spelling <c>columns</c> its own way would be a second contract. And
///it is in the CORE because the contract has two halves that are not both server-side: a perspective's
///server half writes the declaration, and <strong>the viewer's client half reads it</strong> to decide
///what to draw. A shape only the host could see would leave the viewer parsing it from memory.
///
///<strong>The figures are refreshed when somebody looks, never as rows arrive.</strong> ADR 0016 §4's
///invariant is that ingesting N rows adds O(1) vertices, and an update per drain — or per minute —
///would make the graph grow with the amount of data rather than with the kinds of it. Every update is
///a <c>Change</c>; a counter that ticked would be the readings back in the graph wearing a different
///shape.
///</summary>
public static class DatasetVertex
{
    ///<summary>The shared label itself, so the viewer can pick one off the canvas without the host's constants.</summary>
    public const string Label = "Dataset";

    ///<summary>Which perspective made it. Written by every writer of a Dataset, so the orphan audit can attribute one.</summary>
    public const string Plugin = "plugin";

    public const string Name = "name";

    ///<summary><see cref="DatasetKinds"/>.</summary>
    public const string Kind = "kind";

    ///<summary>The columns, as JSON — the contract the viewer draws from.</summary>
    public const string Columns = "columns";

    ///<summary>
    ///Which store answers for the payload: <c>readings</c>, <c>blobs</c>. <strong>The address itself is
    ///this vertex's own key</strong>, which is what the store names its table or its object from — so a
    ///separate address property would be a second copy of the key, and the two could disagree.
    ///</summary>
    public const string Store = "store";

    ///<summary>When the dataset began. Static: it is the moment the dataset was made, not the first row's.</summary>
    public const string Since = "since";

    public const string Rows = "rows";
    public const string Last = "last";
    public const string LastAt = "lastAt";

    ///<summary>When <see cref="Rows"/>, <see cref="Last"/> and <see cref="LastAt"/> were taken.</summary>
    public const string CountedAt = "countedAt";

    ///<summary>
    ///How stale the figures may get before somebody looking at the dataset refreshes them.
    ///
    ///<strong>Fifteen minutes because the figures are not what anybody watches live.</strong> A page
    ///following a sensor has the reading itself; these are for the catalog, the viewer's chart heading
    ///and the tombstone, none of which changes meaning in a quarter of an hour. Shorter would put
    ///writes into the graph for no one's benefit, which is the thing ADR 0016 §4 measures.
    ///</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);

    ///<summary>
    ///The declaration, as properties, for the moment a dataset is created. Everything here is static —
    ///the figures are <see cref="Counted"/>'s and arrive later.
    ///</summary>
    public static Dictionary<string, string> Declared(
        string plugin,
        string name,
        string kind,
        string store,
        IEnumerable<DatasetColumn> columns,
        DateTimeOffset since)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Plugin] = plugin ?? "",
            [Name] = name ?? "",
            [Kind] = kind ?? DatasetKinds.Table,
            [Store] = store ?? "",
            [Columns] = Write(columns),
            [Since] = Stamp(since),

            //Zero rows counted at the moment it was made, rather than no properties at all: "nothing has
            //arrived yet" and "nobody has ever counted" are different states, and a reader that cannot
            //tell them apart reports a live dataset as empty.
            [Rows] = "0",
            [CountedAt] = Stamp(since)
        };
    }

    ///<summary>The figures, as properties. A null last reading removes the property rather than writing an empty one.</summary>
    public static Dictionary<string, string> Counted(DatasetStats stats)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Rows] = stats.Rows.ToString(CultureInfo.InvariantCulture),
            [Last] = Empty(stats.Last),
            [LastAt] = Stamp(stats.LastAt),
            [CountedAt] = Stamp(stats.CountedAt)
        };
    }

    ///<summary>
    ///Whether the figures are old enough to be worth refreshing. <strong>Decided from the vertex rather
    ///than by the caller</strong>, so a page polling once a second cannot turn looking into writing.
    ///</summary>
    public static bool Stale(IReadOnlyDictionary<string, string> properties, DateTimeOffset now)
    {
        return Stale(properties, now, Fresh);
    }

    public static bool Stale(IReadOnlyDictionary<string, string> properties, DateTimeOffset now, TimeSpan fresh)
    {
        if (properties == null)
            return true;

        if (!properties.TryGetValue(CountedAt, out var text) || string.IsNullOrWhiteSpace(text))
            return true;

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var counted))
            return true;

        //A count from the future is a clock somebody changed, and refusing to refresh until it catches
        //up would freeze the figures for as long as the skew lasts.
        if (counted > now)
            return true;

        return now - counted >= fresh;
    }

    ///<summary>The columns as JSON. Ordered as given: the declaration is what the viewer draws its headings from, in order.</summary>
    public static string Write(IEnumerable<DatasetColumn> columns)
    {
        return JsonSerializer.Serialize((columns ?? []).ToList());
    }

    ///<summary>The columns back out, or an empty list for anything unreadable — a dataset with no declaration draws as a table with no headings, which is question 5's stated failure and not an exception.</summary>
    public static IReadOnlyList<DatasetColumn> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<DatasetColumn>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    ///<summary>The column that orders the rows, or null when nothing does — which is what makes a dataset a table rather than a series.</summary>
    public static string OrderedBy(IEnumerable<DatasetColumn> columns)
    {
        foreach (var column in columns ?? [])
        {
            if (column.Orders)
                return column.Name;
        }

        return null;
    }

    private static string Stamp(DateTimeOffset? at)
    {
        if (at == null)
            return null;

        return at.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    private static string Empty(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value;
    }
}
