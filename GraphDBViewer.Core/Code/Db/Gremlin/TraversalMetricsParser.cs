using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>One row of a profile() breakdown — a traversal step with its counts, time and share of the total.</summary>
public class MetricsRow
{
    public int Depth { get; set; }
    public string Name { get; set; } = "";
    public long ElementCount { get; set; }
    public long TraverserCount { get; set; }
    public double DurationMs { get; set; }
    public double PercentDur { get; set; }
}

///<summary>
///Parses a Gremlin profile() result (g:TraversalMetrics GraphSON) into a flat table of step metrics.
///Nested metrics are flattened with a Depth so the UI can indent them. Everything is routed through
///GraphDataConverter.UnwrapElement so g:Map/g:List/g:Int64 wrappers are handled uniformly.
///</summary>
public static class TraversalMetricsParser
{
    public static (double TotalMs, List<MetricsRow> Rows) Parse(JsonElement data)
    {
        var rows = new List<MetricsRow>();
        double total = 0;

        var metrics = FindTraversalMetrics(data);
        if (metrics.ValueKind != JsonValueKind.Object)
            return (0, rows);

        if (metrics.TryGetProperty("dur", out var dur))
            total = ReadDouble(dur);

        if (metrics.TryGetProperty("metrics", out var list))
            foreach (var m in Items(list))
                AddMetric(rows, m, 0);

        return (total, rows);
    }

    //Locates the g:TraversalMetrics @value object inside the result (which is typically a one-element list).
    private static JsonElement FindTraversalMetrics(JsonElement data)
    {
        var el = data;

        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
            el = el[0];

        return GraphDataConverter.UnwrapElement(el);
    }

    private static void AddMetric(List<MetricsRow> rows, JsonElement metric, int depth)
    {
        var m = GraphDataConverter.UnwrapElement(metric);
        if (m.ValueKind != JsonValueKind.Object)
            return;

        var row = new MetricsRow { Depth = depth };

        if (m.TryGetProperty("name", out var name))
            row.Name = name.GetString() ?? "";

        if (m.TryGetProperty("counts", out var counts))
        {
            var c = GraphDataConverter.UnwrapElement(counts);

            if (c.ValueKind == JsonValueKind.Object)
            {
                if (c.TryGetProperty("traverserCount", out var tc))
                    row.TraverserCount = ReadLong(tc);

                if (c.TryGetProperty("elementCount", out var ec))
                    row.ElementCount = ReadLong(ec);
            }
        }

        if (m.TryGetProperty("dur", out var dur))
            row.DurationMs = ReadDouble(dur);

        if (m.TryGetProperty("annotations", out var annotations))
        {
            var a = GraphDataConverter.UnwrapElement(annotations);

            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("percentDur", out var pd))
                row.PercentDur = ReadDouble(pd);
        }

        rows.Add(row);

        if (m.TryGetProperty("metrics", out var nested))
            foreach (var child in Items(nested))
                AddMetric(rows, child, depth + 1);
    }

    private static IEnumerable<JsonElement> Items(JsonElement element)
    {
        var el = GraphDataConverter.UnwrapElement(element);

        if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                yield return item;
    }

    private static long ReadLong(JsonElement element)
    {
        var el = GraphDataConverter.UnwrapElement(element);

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
            return n;

        if (long.TryParse(el.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n2))
            return n2;

        return 0;
    }

    private static double ReadDouble(JsonElement element)
    {
        var el = GraphDataConverter.UnwrapElement(element);

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            return d;

        if (double.TryParse(el.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
            return d2;

        return 0;
    }
}
