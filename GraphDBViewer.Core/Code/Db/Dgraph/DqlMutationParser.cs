using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads Dgraph's staged mutations back into the edits the canvas previews — the DQL counterpart of
///<see cref="GremlinEditParser"/> and <see cref="AqlStatementParser"/>.
///
///What is staged for Dgraph is a JSON mutation rather than query text (see <see cref="DqlQueryBuilder"/>
///for why), so this reads JSON rather than parsing a language. A <c>set</c> is an upsert — Dgraph writes
///the node whether or not it existed — so one yields both the add and the property sets: the add is
///ignored when the node is already drawn, the sets when it is not, which is how the pair describes
///whichever case is real.
///</summary>
public static class DqlMutationParser
{
    ///<summary>Every recognized mutation in the buffer, in order. Anything else is skipped.</summary>
    public static List<GraphEdit> Parse(string buffer)
    {
        var edits = new List<GraphEdit>();

        if (string.IsNullOrWhiteSpace(buffer))
            return edits;

        var lines = buffer.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            try
            {
                ParseLine(line, i, edits);
            }
            catch { }
        }

        return edits;
    }

    private static void ParseLine(string line, int lineIndex, List<GraphEdit> edits)
    {
        using var doc = JsonDocument.Parse(line);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        //An import is an upsert: its nodes are named by the variable its query bound, not by a uid it does
        //not have yet. Reading the query back is what lets the preview draw them under the ids the graph
        //came with, rather than under "uid(v)".
        var variables = ReadVariables(doc.RootElement);

        if (doc.RootElement.TryGetProperty("set", out var set))
            foreach (var node in Nodes(set))
                ReadSet(node, lineIndex, variables, edits);

        if (doc.RootElement.TryGetProperty("delete", out var delete))
            foreach (var node in Nodes(delete))
                ReadDelete(node, edits);
    }

    //"{ s as var(func: eq(gdbvId, "alice")) t as var(func: eq(gdbvId, "bob")) }" — the variables an upsert
    //binds, and the imported id each one stands for.
    private static readonly Regex VariableBinding = new(
        @"(?<var>\w+)\s+as\s+var\s*\(\s*func:\s*eq\(\s*\w+\s*,\s*""(?<id>(?:[^""\\]|\\.)*)""\s*\)\s*\)",
        RegexOptions.Compiled);

    private static Dictionary<string, string> ReadVariables(JsonElement root)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String)
            return variables;

        foreach (Match match in VariableBinding.Matches(query.GetString() ?? ""))
            variables[match.Groups["var"].Value] = match.Groups["id"].Value.Replace("\\\"", "\"");

        return variables;
    }

    //"uid(v)" resolved to the imported id v was bound to, or left alone when it is an ordinary uid.
    private static string Resolve(string uid, Dictionary<string, string> variables)
    {
        if (uid == null || variables.Count == 0 || !uid.StartsWith("uid(", StringComparison.Ordinal) || !uid.EndsWith(")", StringComparison.Ordinal))
            return uid;

        var name = uid.Substring(4, uid.Length - 5);

        if (variables.TryGetValue(name, out var id))
            return id;

        return uid;
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
            yield return value;
    }

    private static void ReadSet(JsonElement node, int lineIndex, Dictionary<string, string> variables, List<GraphEdit> edits)
    {
        var uid = Resolve(Uid(node), variables);

        if (uid == null)
            return;

        //A blank node has no id until the mutation commits, so the preview gives it a temporary one. An
        //upsert's variable has already been resolved to the id the import gave it, which is a real one.
        var id = uid;
        bool isNew = uid.StartsWith("_:", StringComparison.Ordinal);

        if (isNew)
            id = StagedIds.ForVertex(lineIndex);

        var properties = new Dictionary<string, string>();
        var links = new List<(string Predicate, string Target)>();

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == DgraphConverter.UidField)
                continue;

            if (property.Name == DgraphConverter.TypeField)
                continue;

            var targets = Targets(property.Value);

            foreach (var target in targets)
                links.Add((property.Name, Resolve(target, variables)));

            if (targets.Count > 0)
                continue;

            //A facet describes the link, not this node — it is drawn on the edge below.
            if (property.Name.IndexOf(DgraphConverter.FacetSeparator) >= 0)
                continue;

            var value = Scalar(property.Value);

            if (value != null)
                properties[property.Name] = value;
        }

        //Only a mutation that says what the node *is* creates one worth drawing; a bare set of a predicate
        //on an existing node is a property change, and the add would invent a node that is already there.
        if (isNew || node.TryGetProperty(DgraphConverter.TypeField, out _))
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.AddNode,
                Type = "node",
                Id = id,
                Label = DgraphConverter.ReadLabel(node),
                Properties = new Dictionary<string, string>(properties)
            });

        foreach (var pair in properties)
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.SetProperty,
                Type = "node",
                Id = id,
                Key = pair.Key,
                Value = pair.Value
            });

        foreach (var link in links)
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.AddEdge,
                Type = "edge",
                Id = DgraphConverter.EdgeId(id, link.Predicate, link.Target),
                Label = link.Predicate,
                Source = id,
                Target = link.Target,
                Properties = Facets(node, link.Predicate)
            });
    }

    private static void ReadDelete(JsonElement node, List<GraphEdit> edits)
    {
        var uid = Uid(node);

        if (uid == null)
            return;

        bool named = false;

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == DgraphConverter.UidField)
                continue;

            named = true;

            var targets = Targets(property.Value);

            //A predicate holding nodes is an edge; the same predicate set to null is a property removal.
            if (targets.Count > 0)
            {
                foreach (var target in targets)
                    edits.Add(new GraphEdit
                    {
                        Kind = GraphEditKind.RemoveEdge,
                        Type = "edge",
                        Id = DgraphConverter.EdgeId(uid, property.Name, target)
                    });

                continue;
            }

            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.DropProperty,
                Type = "node",
                Id = uid,
                Key = property.Name
            });
        }

        //A delete naming only the node removes every predicate it has, which is Dgraph's "delete the node".
        if (!named)
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.RemoveNode,
                Type = "node",
                Id = uid
            });
    }

    //The facets written alongside a link, which are the edge's own properties.
    private static Dictionary<string, string> Facets(JsonElement node, string predicate)
    {
        var facets = new Dictionary<string, string>();

        foreach (var property in node.EnumerateObject())
            foreach (var target in Nodes(property.Value))
                foreach (var pair in DgraphConverter.ReadFacets(target, predicate))
                    facets[pair.Key] = pair.Value;

        return facets;
    }

    private static List<string> Targets(JsonElement value)
    {
        var targets = new List<string>();

        if (value.ValueKind == JsonValueKind.Object)
        {
            var uid = Uid(value);

            if (uid != null)
                targets.Add(uid);

            return targets;
        }

        if (value.ValueKind != JsonValueKind.Array)
            return targets;

        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
            {
                var uid = Uid(item);

                if (uid != null)
                    targets.Add(uid);
            }

        return targets;
    }

    private static string Uid(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(DgraphConverter.UidField, out var uid)
            && uid.ValueKind == JsonValueKind.String)
            return uid.GetString();

        return null;
    }

    private static string Scalar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            return value.ToString();

        return null;
    }
}
