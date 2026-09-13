using System;
using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///The small readings every engine's driver and converter need, in one place.
///
///Each of these existed three or four times over, copied as each new backend arrived — identical bodies
///in ArangoConverter, DgraphConverter and GunConverter, and in four drivers' error paths. They are here
///not because they are interesting but because a copy is a place for the copies to disagree: two of them
///rendering a JSON <c>true</c> differently is the kind of difference nobody notices until a property
///reads "True" in one view and "true" in another.
///</summary>
public static class GraphWireText
{
    ///<summary>How much of an engine's error body is worth showing before it stops being a message.</summary>
    public const int MaxErrorLength = 400;

    ///<summary>
    ///A JSON value as the viewer's string-keyed property maps hold it. Null stays null — a property that
    ///is absent and one that is null are the same thing to a graph — and anything that is not a scalar
    ///keeps its JSON, which is the only lossless way to put an object in a string.
    ///</summary>
    public static string Stringify(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.True)
            return "true";

        if (value.ValueKind == JsonValueKind.False)
            return "false";

        return value.GetRawText();
    }

    ///<summary>An error body cut to a readable length, with an ellipsis when there was more of it.</summary>
    public static string Truncate(string text, int max = MaxErrorLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        if (text.Length <= max)
            return text;

        return text.Substring(0, max) + "…";
    }

    ///<summary>Separates an edge's label from the node it points at, in the id synthesised for one.</summary>
    public const string EdgeArrow = "->";

    ///<summary>
    ///The id an edge gets in an engine that gives it none — Dgraph, where an edge is a predicate holding a
    ///node, and GUN, where it is a key holding a soul. In both the edge <i>is</i> the triple, so that is
    ///what the id says.
    ///</summary>
    public static string EdgeId(string source, string label, string target)
    {
        return $"{source}-{label}{EdgeArrow}{target}";
    }

    ///<summary>
    ///Reads such an id back into its triple, or returns false when it is not one. Read from the right, so
    ///a source containing a dash still parses; a <i>label</i> containing one would not, which is the price
    ///of an id that stays readable.
    ///</summary>
    public static bool TryReadEdgeId(string id, out string source, out string label, out string target)
    {
        source = null;
        label = null;
        target = null;

        if (string.IsNullOrEmpty(id))
            return false;

        int arrow = id.LastIndexOf(EdgeArrow, StringComparison.Ordinal);

        if (arrow <= 0)
            return false;

        var head = id.Substring(0, arrow);
        int dash = head.LastIndexOf('-');

        if (dash <= 0)
            return false;

        source = head.Substring(0, dash);
        label = head.Substring(dash + 1);
        target = id.Substring(arrow + EdgeArrow.Length);

        return true;
    }

    ///<summary>
    ///A value off the wire as a number, whatever the driver typed it as. A <c>long</c> or <c>double</c>
    ///is read as itself; a string is read invariantly — the wire has one number format, and it is not
    ///the host's culture — and anything else is null. Reading a driver's number through
    ///<c>ToString</c> and back parses it under the host's culture twice, which round-trips only by
    ///accident, and turns <c>1234.5</c> into twelve thousand on a comma-decimal host when it does not.
    ///</summary>
    public static double? Number(object? value)
    {
        if (value is long whole)
            return whole;

        if (value is int narrow)
            return narrow;

        if (value is double fraction)
            return fraction;

        if (value is float single)
            return single;

        if (value is decimal exact)
            return (double)exact;

        if (value is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }
}
