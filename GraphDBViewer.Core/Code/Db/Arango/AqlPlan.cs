using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///An ArangoDB execution plan, flattened into the ordinary <see cref="GraphDbTable"/> the debugger panel
///reads — the AQL counterpart of <see cref="CypherPlan"/>.
///
///AQL differs from Cypher in where a plan comes from. <c>PROFILE</c> and <c>EXPLAIN</c> are Cypher
///<i>prefixes</i>, so the debugger there only has to compose text. ArangoDB instead has a <b>separate
///endpoint</b> for explain (<c>/_api/explain</c>) and profiles through an <b>option on the cursor
///request</b> (<c>options.profile</c>) — neither of which the "compose a query string" seam can express.
///So the debugger marks the query with an AQL comment and <see cref="ArangoDb"/> routes on it. The markers
///are real comments on purpose: one that somehow reached the server unrouted would still run the query
///rather than fail to parse.
///
///Both answers carry the same <c>plan.nodes</c> array — execution order, leaf first — and a profile adds
///per-node <c>stats.nodes</c>. One reader handles both.
///</summary>
public static class AqlPlan
{
    ///<summary>Marks a query to be profiled: run it, and answer with the plan and what each node did.</summary>
    public const string ProfileMarker = "/*profile*/";

    ///<summary>Marks a query to be explained: plan it without running it.</summary>
    public const string ExplainMarker = "/*explain*/";

    ///<summary>ArangoDB's most detailed profile: per-node statistics as well as the plan.</summary>
    public const int ProfileLevel = 2;

    ///<summary>Nesting level, so a subquery's nodes are indented under the one that started it.</summary>
    public const string DepthColumn = "depth";

    ///<summary>The execution node's type — EnumerateCollectionNode, IndexNode, TraversalNode…</summary>
    public const string NodeColumn = "node";

    ///<summary>Items the node produced. Profile only — an explain has not run the query.</summary>
    public const string ItemsColumn = "items";

    ///<summary>Times the node was asked for more. Profile only.</summary>
    public const string CallsColumn = "calls";

    ///<summary>The node's own time in milliseconds, its dependencies excluded. Profile only.</summary>
    public const string TimeColumn = "timeMs";

    ///<summary>What the node works on — the collection, the index, the edge collections and direction.</summary>
    public const string DetailsColumn = "details";

    ///<summary>Items the optimizer expects the node to produce. Always present, estimate or not.</summary>
    public const string EstimatedItemsColumn = "estItems";

    ///<summary>The optimizer's cost for the node, which is what it compares plans by.</summary>
    public const string EstimatedCostColumn = "estCost";

    ///<summary>Which plan request a query is marked for, if any.</summary>
    public enum PlanKind
    {
        None,
        Profile,
        Explain
    }

    ///<summary>
    ///Splits a marked query into what it asks for and the query itself. An unmarked query comes back
    ///<see cref="PlanKind.None"/> and unchanged, which is the normal path.
    ///</summary>
    public static (PlanKind Kind, string Query) ReadMarker(string query)
    {
        var text = (query ?? "").TrimStart();

        if (text.StartsWith(ProfileMarker, StringComparison.Ordinal))
            return (PlanKind.Profile, text.Substring(ProfileMarker.Length).TrimStart());

        if (text.StartsWith(ExplainMarker, StringComparison.Ordinal))
            return (PlanKind.Explain, text.Substring(ExplainMarker.Length).TrimStart());

        return (PlanKind.None, query);
    }

    ///<summary>True when a result's columns are a plan rather than ordinary query output.</summary>
    public static bool IsPlan(GraphDbTable table)
    {
        return table != null
            && table.Vars.Contains(NodeColumn)
            && table.Vars.Contains(DepthColumn);
    }

    ///<summary>
    ///Reads a plan into rows. <paramref name="root"/> is the explain response or a cursor response's
    ///<c>extra</c> — both hold <c>plan.nodes</c>, and only the profile adds <c>stats.nodes</c>.
    ///</summary>
    public static GraphDbTable ToTable(JsonElement root)
    {
        var table = new GraphDbTable
        {
            Vars =
            {
                DepthColumn,
                NodeColumn,
                ItemsColumn,
                CallsColumn,
                TimeColumn,
                DetailsColumn,
                EstimatedItemsColumn,
                EstimatedCostColumn
            }
        };

        if (!TryPlanNodes(root, out var nodes))
            return table;

        var stats = ReadNodeStats(root);
        int depth = 0;

        foreach (var node in nodes.EnumerateArray())
        {
            var type = ReadString(node, "type");

            //A subquery is spliced into the plan between these two, so they are what the indentation reads.
            if (type == "SubqueryEndNode" && depth > 0)
                depth--;

            long id = ReadLong(node, "id");
            stats.TryGetValue(id, out var measured);

            table.Rows.Add(new Dictionary<string, string>
            {
                [DepthColumn] = Text(depth),
                [NodeColumn] = type,
                [ItemsColumn] = Text(measured.Items),
                [CallsColumn] = Text(measured.Calls),
                [TimeColumn] = Text(SelfMs(node, stats, measured.RuntimeMs)),
                [DetailsColumn] = Details(node, type),
                [EstimatedItemsColumn] = Text(ReadLong(node, "estimatedNrItems")),
                [EstimatedCostColumn] = Text(ReadDouble(node, "estimatedCost"))
            });

            if (type == "SubqueryStartNode")
                depth++;
        }

        return table;
    }

    //One node's measurements. Runtime is what the server reported, which is cumulative.
    private struct NodeStats
    {
        public long Calls;
        public long Items;
        public double RuntimeMs;
    }

    private static bool TryPlanNodes(JsonElement root, out JsonElement nodes)
    {
        nodes = default;

        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            return false;

        if (!plan.TryGetProperty("nodes", out nodes) || nodes.ValueKind != JsonValueKind.Array)
            return false;

        return true;
    }

    //The per-node measurements a profile adds, by node id. Empty for an explain, which measured nothing.
    private static Dictionary<long, NodeStats> ReadNodeStats(JsonElement root)
    {
        var stats = new Dictionary<long, NodeStats>();

        if (!root.TryGetProperty("stats", out var container) || container.ValueKind != JsonValueKind.Object)
            return stats;

        if (!container.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return stats;

        foreach (var node in nodes.EnumerateArray())
            stats[ReadLong(node, "id")] = new NodeStats
            {
                Calls = ReadLong(node, "calls"),
                Items = ReadLong(node, "items"),
                //ArangoDB reports seconds; every other engine's debugger talks in milliseconds.
                RuntimeMs = ReadDouble(node, "runtime") * 1000
            };

        return stats;
    }

    ///<summary>
    ///The time the node itself spent. ArangoDB's runtime is <b>cumulative</b> — the final node reports
    ///nearly the whole query — so a table of those numbers would suggest every node was the slow one.
    ///Subtracting the slowest dependency leaves the node's own share, which is what the panel's percentages
    ///are of, and what points at the step actually worth fixing.
    ///</summary>
    private static double SelfMs(JsonElement node, Dictionary<long, NodeStats> stats, double runtimeMs)
    {
        if (runtimeMs <= 0)
            return 0;

        double dependencies = 0;

        if (node.TryGetProperty("dependencies", out var ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var id in ids.EnumerateArray())
                if (id.TryGetInt64(out var value) && stats.TryGetValue(value, out var dependency) && dependency.RuntimeMs > dependencies)
                    dependencies = dependency.RuntimeMs;

        if (dependencies >= runtimeMs)
            return 0;

        return runtimeMs - dependencies;
    }

    ///<summary>
    ///What the node works on, in one line. This is where a plan answers the question people actually bring
    ///to it — an <c>IndexNode</c> naming an index did a lookup, an <c>EnumerateCollectionNode</c> read the
    ///whole collection.
    ///</summary>
    private static string Details(JsonElement node, string type)
    {
        var parts = new List<string>();

        var collection = ReadString(node, "collection");

        if (collection.Length > 0)
            parts.Add(collection);

        //An IndexNode lists the indexes it will use. A TraversalNode's "indexes" is an object of its own
        //shape (the edge index it always uses), which says nothing a reader needs — the edge collection does.
        if (node.TryGetProperty("indexes", out var indexes) && indexes.ValueKind == JsonValueKind.Array)
            foreach (var index in indexes.EnumerateArray())
            {
                var name = ReadString(index, "name");

                if (name.Length > 0)
                    parts.Add($"index {name}");
            }

        var edges = ReadStringArray(node, "edgeCollections");

        if (edges.Count > 0)
            parts.Add($"{Direction(node)} {string.Join(", ", edges)}");

        if (type == "LimitNode")
        {
            var limit = ReadLong(node, "limit");
            var offset = ReadLong(node, "offset");

            if (offset > 0)
                parts.Add($"skip {offset}, limit {limit}");
            else
                parts.Add($"limit {limit}");
        }

        //Nothing more specific: the variable it produces at least says which part of the query this is.
        if (parts.Count == 0)
        {
            var variable = VariableName(node, "outVariable");

            if (variable.Length == 0)
                variable = VariableName(node, "inVariable");

            if (variable.Length > 0)
                parts.Add(variable);
        }

        return string.Join(", ", parts);
    }

    //How a traversal walks its edges. ArangoDB codes the direction per edge collection: 1 inbound,
    //2 outbound, and defaultDirection 0 for ANY (which lists both).
    private static string Direction(JsonElement node)
    {
        if (node.TryGetProperty("defaultDirection", out var value) && value.TryGetInt64(out var direction))
        {
            if (direction == 1)
                return "INBOUND";

            if (direction == 2)
                return "OUTBOUND";
        }

        return "ANY";
    }

    //A variable named by the optimizer is a number ("7"), which tells a reader nothing; those are skipped.
    private static string VariableName(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var variable) || variable.ValueKind != JsonValueKind.Object)
            return "";

        var name = ReadString(variable, "name");

        if (name.Length == 0 || long.TryParse(name, out _))
            return "";

        return name;
    }

    private static List<string> ReadStringArray(JsonElement node, string property)
    {
        var values = new List<string>();

        if (!node.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? "");

        return values;
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";

        return "";
    }

    private static long ReadLong(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
            return number;

        return 0;
    }

    private static double ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number))
            return number;

        return 0;
    }

    private static string Text(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Text(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Text(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
