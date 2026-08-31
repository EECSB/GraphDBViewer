using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Maps a Neo4j / Bolt result — handed over as a small, driver-agnostic "records envelope" — onto the
///shared <see cref="GraphDbResult"/>. Both drivers that speak Bolt build the very same envelope from their
///own record objects and hand it here (the browser-direct <c>Neo4jBrowserDb</c> from the JavaScript
///driver's records, the host-side <c>Neo4jServerDb</c> from the .NET driver's <c>IRecord</c>s), so every
///bit of record→graph shaping lives in one place and is unit-tested once rather than twice across the
///JS / .NET divide.
///
///The envelope is <c>{ "columns": [..], "records": [ { col: value, .. }, .. ] }</c>, or
///<c>{ "error": ".." }</c>. A value is a plain JSON scalar, an array, a map, or a tagged graph element:
///a node <c>{ "$e":"node", "id":.., "labels":[..], "props":{..} }</c>, a relationship
///<c>{ "$e":"rel", "id":.., "type":.., "start":.., "end":.., "props":{..} }</c>, or a path
///<c>{ "$e":"path", "nodes":[node..], "rels":[rel..] }</c>.
///
///When any node or relationship appears anywhere in the records the result is a graph — the flat
///vertex/edge JSON the converters render, exactly the shape
///<see cref="GraphDataConverter.BuildEffectiveGraphSON"/> emits. Otherwise it is the rows the query
///answered with, as a <see cref="GraphDbTable"/> — the same split SPARQL makes between a CONSTRUCT graph
///and a SELECT table.
///</summary>
public static class Neo4jConverter
{
    private const string ElementTag = "$e";

    public static GraphDbResult ToGraphDbResult(string envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return GraphDbResult.Success(EmptyGraph());

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(envelopeJson);
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure($"Could not parse the Neo4j response: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return GraphDbResult.Failure(error.GetString() ?? "");

            var records = ReadArray(root, "records");

            var nodes = new Dictionary<string, object>();
            var edges = new Dictionary<string, object>();

            foreach (var record in records)
                if (record.ValueKind == JsonValueKind.Object)
                    foreach (var field in record.EnumerateObject())
                        Collect(field.Value, nodes, edges);

            //Any node or relationship anywhere makes it a graph; a query that only answered with scalars
            //(RETURN n.name, count(*), a bare RETURN 1 probe) has none, so it becomes a table instead.
            if (nodes.Count > 0 || edges.Count > 0)
                return GraphDbResult.Success(BuildGraph(nodes, edges));

            //A genuinely empty result (a MATCH that found nothing) stays an empty graph rather than a
            //zero-row table, so a graph query that matched nothing still lands on the graph view.
            if (records.Count == 0)
                return GraphDbResult.Success(EmptyGraph());

            return BuildTable(ReadColumns(root, records), records);
        }
    }

    //Walks one record field, gathering every node and relationship it contains — directly, inside a path,
    //or nested in a returned list or map — so a query shaped any way still lights up the graph.
    private static void Collect(JsonElement value, Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                Collect(item, nodes, edges);

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
            return;

        if (!value.TryGetProperty(ElementTag, out var tag) || tag.ValueKind != JsonValueKind.String)
        {
            //A plain map — recurse into its values, which may themselves be nodes or relationships.
            foreach (var prop in value.EnumerateObject())
                Collect(prop.Value, nodes, edges);

            return;
        }

        string? kind = tag.GetString();

        if (kind == "node")
            AddNode(value, nodes);
        else if (kind == "rel")
            AddEdge(value, edges);
        else if (kind == "path")
        {
            foreach (var n in ReadArray(value, "nodes"))
                AddNode(n, nodes);

            foreach (var r in ReadArray(value, "rels"))
                AddEdge(r, edges);
        }
    }

    private static void AddNode(JsonElement node, Dictionary<string, object> nodes)
    {
        string? id = GetString(node, "id");

        if (id == null || nodes.ContainsKey(id))
            return;

        nodes[id] = new { id, label = FirstLabel(node), properties = ReadProps(node) };
    }

    private static void AddEdge(JsonElement rel, Dictionary<string, object> edges)
    {
        string? id = GetString(rel, "id");

        if (id == null || edges.ContainsKey(id))
            return;

        edges[id] = new
        {
            id,
            label = GetString(rel, "type") ?? "edge",
            outV = GetString(rel, "start"),
            inV = GetString(rel, "end"),
            properties = ReadProps(rel)
        };
    }

    //A Neo4j node can carry several labels; the first is its primary type, which is what the viewer groups
    //and colors by. A label-less node (Neo4j allows it) shows as the generic "node".
    private static string FirstLabel(JsonElement node)
    {
        foreach (var label in ReadArray(node, "labels"))
            if (label.ValueKind == JsonValueKind.String)
                return label.GetString() ?? "node";

        return "node";
    }

    private static Dictionary<string, string> ReadProps(JsonElement element)
    {
        var props = new Dictionary<string, string>();

        if (!element.TryGetProperty("props", out var bag) || bag.ValueKind != JsonValueKind.Object)
            return props;

        foreach (var prop in bag.EnumerateObject())
        {
            string? value = Stringify(prop.Value);

            if (value != null)
                props[prop.Name] = value;
        }

        return props;
    }

    //Flattens a JSON value to the string the graph's property map (and the table's cells) hold. Numbers
    //keep their exact text so a Long doesn't pick up a decimal point; a nested list or map keeps its JSON.
    private static string? Stringify(JsonElement value)
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

    private static GraphDbResult BuildTable(List<string> columns, List<JsonElement> records)
    {
        var table = new GraphDbTable { Vars = columns };

        foreach (var record in records)
        {
            var row = new Dictionary<string, string>();

            foreach (var column in columns)
            {
                if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty(column, out var cell))
                    row[column] = Stringify(cell) ?? "";
                else
                    row[column] = "";
            }

            table.Rows.Add(row);
        }

        //The JSON view shows the rows the query answered with, so a table result isn't a blank panel.
        string raw = JsonSerializer.Serialize(table.Rows, new JsonSerializerOptions { WriteIndented = true });

        return GraphDbResult.Tabular(table, raw);
    }

    private static JsonElement BuildGraph(Dictionary<string, object> nodes, Dictionary<string, object> edges)
    {
        var elements = new List<object>();
        elements.AddRange(nodes.Values);
        elements.AddRange(edges.Values);

        //Reparsed into its own document so the element outlives the input envelope's document, exactly as
        //GraphDataConverter.BuildEffectiveGraphSON does when it hands a freshly built graph back.
        return JsonDocument.Parse(JsonSerializer.Serialize(elements)).RootElement;
    }

    private static JsonElement EmptyGraph()
    {
        return JsonDocument.Parse("[]").RootElement;
    }

    //The column order to show, preferring the driver's own column list and falling back to the union of
    //keys across the records (in first-seen order) if it wasn't supplied.
    private static List<string> ReadColumns(JsonElement root, List<JsonElement> records)
    {
        var columns = new List<string>();

        foreach (var column in ReadArray(root, "columns"))
            if (column.ValueKind == JsonValueKind.String)
                columns.Add(column.GetString()!);

        if (columns.Count > 0)
            return columns;

        foreach (var record in records)
            if (record.ValueKind == JsonValueKind.Object)
                foreach (var field in record.EnumerateObject())
                    if (!columns.Contains(field.Name))
                        columns.Add(field.Name);

        return columns;
    }

    private static List<JsonElement> ReadArray(JsonElement element, string name)
    {
        var items = new List<JsonElement>();

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var array)
            && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                items.Add(item);

        return items;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }
}
