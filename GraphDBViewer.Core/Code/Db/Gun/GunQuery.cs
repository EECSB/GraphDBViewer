using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a GUN read is: where to start, whether to walk into that node's children, and how far to follow
///links outward.
///
///GUN has no query language — it is a chained JavaScript API — so there is nothing for the viewer to let
///someone type. This type is the honest replacement: the connection card's form fills it in, it
///round-trips through a short wire string the interop parses, and it renders <b>the actual GUN JavaScript
///that will run</b> for the read-only preview. The user reads real GUN rather than a language invented
///for one text box.
///</summary>
public sealed class GunQuery
{
    ///<summary>Hops followed outward from the starting node when none is given.</summary>
    public const int DefaultDepth = 1;

    ///<summary>The furthest the walk will go; a deeply linked graph would otherwise arrive whole.</summary>
    public const int MaxDepth = 5;

    ///<summary>
    ///A query that reads nothing, without troubling the peer. Answers the questions GUN cannot — chiefly
    ///"what links *to* this node", which has no reverse index to consult.
    ///</summary>
    public const string Nothing = "~none";

    ///<summary>The keys walked from the root — <c>alice</c>, or <c>alice</c> then <c>knows</c>.</summary>
    public IReadOnlyList<string> Keys { get; init; } = new List<string>();

    ///<summary>
    ///Walk into the node's children rather than reading the node itself — GUN's <c>.map()</c>. This is how
    ///a GUN graph is usually listed: a root node holds the records, since souls cannot be enumerated.
    ///</summary>
    public bool MapChildren { get; init; }

    public int Depth { get; init; } = DefaultDepth;

    ///<summary>True when there is a key to start from. GUN cannot read without one.</summary>
    public bool HasStart => Keys.Count > 0;

    ///<summary>Builds a read of one node by its soul — what expanding a node on the canvas asks for.</summary>
    public static GunQuery ForSoul(string soul, int depth = DefaultDepth)
    {
        return new GunQuery { Keys = new List<string> { soul ?? "" }, Depth = Clamp(depth) };
    }

    ///<summary>
    ///The wire string the interop parses: the key path, <c>*</c> when mapping over children, and
    ///<c>~depth</c>. Short because it is machinery, not something anyone has to write.
    ///</summary>
    public string ToQueryString()
    {
        if (!HasStart)
            return "";

        var sb = new StringBuilder(string.Join("/", Keys));

        if (MapChildren)
            sb.Append('*');

        sb.Append(" ~").Append(Depth);

        return sb.ToString();
    }

    ///<summary>
    ///True when the text is one this form wrote. Query text outlives a connection — a tab keeps whatever
    ///was last in it — so switching to GUN can leave a Gremlin or Cypher query sitting where a key should
    ///be, and running that would read a key that cannot exist.
    ///
    ///The test is that it round-trips: <see cref="ToQueryString"/> always ends in <c>~depth</c>, so text
    ///that parses back to something else did not come from here. That beats guessing at characters a
    ///query language uses — a GUN key may hold almost anything, including spaces.
    ///</summary>
    public static bool IsWireString(string query)
    {
        var text = (query ?? "").Trim();

        if (text.Length == 0)
            return false;

        if (text == Nothing)
            return true;

        return Parse(text).ToQueryString() == text;
    }

    public static GunQuery Parse(string query)
    {
        var text = (query ?? "").Trim();
        int depth = DefaultDepth;

        int tilde = text.LastIndexOf('~');

        if (tilde >= 0)
        {
            if (int.TryParse(text.Substring(tilde + 1).Trim(), out var requested))
                depth = Clamp(requested);

            text = text.Substring(0, tilde).Trim();
        }

        bool map = text.EndsWith("*", StringComparison.Ordinal);

        if (map)
            text = text.Substring(0, text.Length - 1);

        var keys = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return new GunQuery { Keys = keys, MapChildren = map, Depth = depth };
    }

    ///<summary>
    ///The GUN JavaScript this read amounts to. Shown to the user instead of a query, because it is what
    ///actually runs — and because it is the same code they would write against GUN themselves.
    ///</summary>
    public string ToJavaScript()
    {
        if (!HasStart)
            return "//Enter a key to start from. GUN cannot list every node, so a read has to begin somewhere known.";

        var sb = new StringBuilder("gun");

        foreach (var key in Keys)
            sb.Append($".get('{Escape(key)}')");

        if (MapChildren)
            sb.Append(".map()");

        //.once is a single read; .on would keep firing as peers changed the data, which the viewer has
        //nowhere to put yet.
        sb.Append(".once(…)");

        var lines = new List<string> { sb.ToString() };

        if (Depth > 0)
        {
            lines.Add("");
            lines.Add($"//then, for each link found, {Depth} more hop(s) of:");
            lines.Add("gun.get(soul).once(…)");
        }

        return string.Join("\n", lines);
    }

    ///<summary>Escapes a key for a single-quoted JavaScript string.</summary>
    public static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static int Clamp(int depth)
    {
        if (depth < 0)
            return 0;

        if (depth > MaxDepth)
            return MaxDepth;

        return depth;
    }
}
