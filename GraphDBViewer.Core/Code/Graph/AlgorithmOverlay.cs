using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///What an algorithm said about the loaded graph, computed in the browser (<see cref="GraphAlgorithms"/>) or by the
///database's own library and mapped onto the vertices drawn by id (<see cref="DatabaseAlgorithms"/>), kept beside the
///graph rather than written into it. The graph's JSON is what edits and commits are made from, and rebuilding it with
///the numbers in would lose what it knows of its ids' types; an algorithm's numbers are a way of looking at the graph,
///not the database's data. So the converters draw it (a vertex sized by its score, colored by its score or its group, a path
///in a color of its own with the rest dimmed), the table shows it as a column, and the table's export carries it.
///
///It belongs to the result it was computed on (<see cref="Of"/>). A new result, an expand among them, leaves it behind
///rather than drawing one graph's numbers on another's vertices.
///</summary>
public sealed class AlgorithmOverlay
{
    public enum Kinds
    {
        Score,
        Group,
        Path
    }

    ///<summary>A path's color: the palette's magenta, deeper, so it is not mistaken for a label's.</summary>
    public const string PathColor = "#c2255c";

    ///<summary>What is not on the path: faint, as the low-confidence gray is, and lighter still.</summary>
    public const string DimColor = "#dee2e6";

    ///<summary>The smallest and largest a score draws a vertex, in pixels on the 2D canvas.</summary>
    public const int MinSize = 20;
    public const int MaxSize = 80;

    //A score's color runs from the palette's blue, low, to its red, high.
    private static readonly (int R, int G, int B) Low = (0x4e, 0x79, 0xa7);
    private static readonly (int R, int G, int B) High = (0xe1, 0x57, 0x59);

    ///<summary>What it is called, as the table's column and the panel's heading: "PageRank", "Degree (out)".</summary>
    public string Name { get; init; } = "";

    public Kinds Kind { get; init; }

    ///<summary>The result it was computed on; it is drawn on that result alone.</summary>
    public JsonElement Of { get; set; }

    ///<summary>A score per vertex, for <see cref="Kinds.Score"/>.</summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();

    ///<summary>A group per vertex, numbered from the largest, for <see cref="Kinds.Group"/>.</summary>
    public IReadOnlyDictionary<string, int> Groups { get; init; } = new Dictionary<string, int>();

    ///<summary>The path's vertices in order, for <see cref="Kinds.Path"/>.</summary>
    public IReadOnlyList<string> PathNodes { get; init; } = [];

    ///<summary>What a path cost, its edges or their weights.</summary>
    public double PathCost { get; init; }

    ///<summary>Anything the reader should know, as "estimated from 2,000 of 12,000 vertices".</summary>
    public string Note { get; set; }

    ///<summary>
    ///Where it was computed and over what, as the panel says it: in this browser over the vertices drawn, or in the
    ///database by its own library over all of its vertices (<see cref="DatabaseAlgorithms"/>).
    ///</summary>
    public string Where { get; set; }

    public bool SizeByScore { get; set; } = true;
    public bool ColorByScore { get; set; }
    public bool ColorByGroup { get; set; } = true;

    private double? low;
    private double? high;
    private HashSet<string> onPath;
    private Dictionary<string, int> pathIndex;

    ///<summary>Whether it was computed on this result, which is the only one it is drawn on.</summary>
    public bool AppliesTo(JsonElement data)
    {
        if (Of.ValueKind == JsonValueKind.Undefined || data.ValueKind == JsonValueKind.Undefined)
            return false;

        return Of.Equals(data);
    }

    ///<summary>Its look folded into a vertex's data, after every other style, for both views: they read the same keys.</summary>
    public void ApplyToNode(Dictionary<string, object> data, string id)
    {
        if (id == null)
            return;

        if (Kind == Kinds.Score && Scores.TryGetValue(id, out var score))
        {
            if (SizeByScore)
                data["nodeSize"] = SizeFor(score);

            if (ColorByScore)
                data["bgColor"] = ColorFor(score);
        }
        else if (Kind == Kinds.Group && ColorByGroup && Groups.TryGetValue(id, out var group))
            data["bgColor"] = GraphPalette.Colors[group % GraphPalette.Colors.Length];
        else if (Kind == Kinds.Path)
        {
            if (OnPath(id))
                data["bgColor"] = PathColor;
            else
                data["bgColor"] = DimColor;
        }
    }

    ///<summary>The color an edge draws in, or null to keep its own: on a path, the path's color when its ends are one step of it.</summary>
    public string EdgeColor(string source, string target)
    {
        if (Kind != Kinds.Path)
            return null;

        if (Step(source, target))
            return PathColor;

        return DimColor;
    }

    ///<summary>Whether a vertex is on the path.</summary>
    public bool OnPath(string id)
    {
        onPath ??= new HashSet<string>(PathNodes, StringComparer.Ordinal);

        return id != null && onPath.Contains(id);
    }

    ///<summary>Whether two vertices are one step of the path, either way round: an edge between them is how the path went.</summary>
    public bool Step(string source, string target)
    {
        var index = PathIndex();

        if (source == null || target == null || !index.TryGetValue(source, out var a) || !index.TryGetValue(target, out var b))
            return false;

        return Math.Abs(a - b) == 1;
    }

    ///<summary>Each path vertex's place along it, from 0.</summary>
    private Dictionary<string, int> PathIndex()
    {
        if (pathIndex == null)
        {
            pathIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < PathNodes.Count; i++)
                pathIndex.TryAdd(PathNodes[i], i);
        }

        return pathIndex;
    }

    ///<summary>A vertex's value as the table and the panel show it, or empty when it has none.</summary>
    public string ValueOf(string id)
    {
        if (id == null)
            return "";

        if (Kind == Kinds.Score && Scores.TryGetValue(id, out var score))
            return Formatted(score);
        else if (Kind == Kinds.Group && Groups.TryGetValue(id, out var group))
            return (group + 1).ToString(CultureInfo.InvariantCulture);
        else if (Kind == Kinds.Path && PathIndex().TryGetValue(id, out var place))
            return (place + 1).ToString(CultureInfo.InvariantCulture);

        return "";
    }

    ///<summary>A score as a person reads it: a whole number as one, anything else in four significant digits.</summary>
    public static string Formatted(double score)
    {
        if (Math.Abs(score - Math.Round(score)) < 1e-9 && Math.Abs(score) < 1e15)
            return ((long)Math.Round(score)).ToString(CultureInfo.InvariantCulture);

        return score.ToString("G4", CultureInfo.InvariantCulture);
    }

    ///<summary>Its column added to a table being exported, the vertices' rows carrying their values; a copy's, since an export builds its own.</summary>
    public void AddColumnTo(GraphDataConverter.GraphTable table)
    {
        var column = Name;

        //A vertex that already has a property of this name keeps it, and the computed one is told apart.
        if (table.NodePropertyColumns.Contains(column))
            column += " (computed)";

        table.NodePropertyColumns.Add(column);

        foreach (var row in table.Nodes)
        {
            var value = ValueOf(row.Id);

            if (value.Length > 0)
                row.Properties[column] = value;
        }
    }

    ///<summary>
    ///How big a score draws a vertex: from <see cref="MinSize"/> to <see cref="MaxSize"/> along the square root of where
    ///it lies between the lowest score and the highest, so a few high scores do not leave everything else the same dot.
    ///</summary>
    public int SizeFor(double score)
    {
        //Every score the same says nothing of one vertex against another: all of them at the middle size.
        if (Flat())
            return (MinSize + MaxSize) / 2;

        return (int)Math.Round(MinSize + (MaxSize - MinSize) * Math.Sqrt(Share(score)));
    }

    ///<summary>The color a score draws a vertex in, from blue, lowest, to red, highest.</summary>
    public string ColorFor(double score)
    {
        var t = Share(score);
        var r = (int)Math.Round(Low.R + (High.R - Low.R) * t);
        var g = (int)Math.Round(Low.G + (High.G - Low.G) * t);
        var b = (int)Math.Round(Low.B + (High.B - Low.B) * t);

        return $"#{r:x2}{g:x2}{b:x2}";
    }

    ///<summary>Where a score lies between the lowest and the highest, 0 to 1; a half when they are all the same.</summary>
    private double Share(double score)
    {
        if (Flat())
            return 0.5;

        return Math.Clamp((score - low.Value) / (high.Value - low.Value), 0, 1);
    }

    ///<summary>Whether every score is the same, the lowest and the highest read once.</summary>
    private bool Flat()
    {
        if (low == null)
        {
            low = 0;
            high = 0;

            if (Scores.Count > 0)
            {
                low = Scores.Values.Min();
                high = Scores.Values.Max();
            }
        }

        return high.Value - low.Value < 1e-12;
    }
}
