using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///The GUN side of <see cref="IGraphQueryBuilder"/>: traversal, and writes composed as the GUN JavaScript
///that performs them.
///
///What is missing is missing because GUN cannot do it, not because it is unwritten:
///
///  * <b>Browse is impossible.</b> Souls cannot be enumerated over a peer, so there is no "load the
///    database"; the viewer's form asks for a starting key instead.
///  * <b>In-edges are impossible.</b> A GUN link is one-directional with no reverse index, so "what points
///    at this node" cannot be answered without reading the whole graph. It answers empty rather than
///    lying or scanning.
///  * <b>Edge properties have nowhere to live.</b> A link is a key holding a node, not an object, so
///    properties offered for one are dropped rather than written somewhere they would not be found again.
///
///Writes are staged like every other engine's, and the statements are real GUN — see <see cref="GunWrite"/>
///for why that is the honest form for a database with no query language.
///</summary>
public sealed class GunQueryBuilder : IGraphQueryBuilder
{
    public static readonly GunQueryBuilder Instance = new();

    //── Browse ──────────────────────────────────────────────────────────

    //Reachable only if BrowseGraph were switched on, which it is not: GUN cannot list its own nodes.
    public string LimitedVertices(int limit)
    {
        return GunQuery.Nothing;
    }

    public string FullGraph(int? limit)
    {
        return GunQuery.Nothing;
    }

    //── Traverse ────────────────────────────────────────────────────────

    ///<summary>The node and everything one hop out — what expanding it on the canvas asks for.</summary>
    public string Neighbors(string id, int limit)
    {
        return GunQuery.ForSoul(id).ToQueryString();
    }

    ///<summary>
    ///Nothing. GUN links point one way and it keeps no reverse index, so the nodes linking *to* this one
    ///cannot be found without reading every node there is.
    ///</summary>
    public string InEdges(string vertexId)
    {
        return GunQuery.Nothing;
    }

    ///<summary>The node itself: its links out are its properties that hold a soul.</summary>
    public string OutEdges(string vertexId)
    {
        return GunQuery.ForSoul(vertexId, 0).ToQueryString();
    }

    public string VertexDisplayLabel(string vertexId)
    {
        return GunQuery.ForSoul(vertexId, 0).ToQueryString();
    }

    ///<summary>
    ///Reads the node's links out of the graph the query answered with. Unlike the other engines this is not
    ///a projection — GUN has no way to ask for one — so the node comes back whole and its links are read
    ///off it here.
    ///</summary>
    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        var edges = new List<EdgeInfo>();

        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return edges;

        var table = GraphDataConverter.ToTable(result.Data);

        //The label shown for the far end: its own display name when the walk fetched it, else its soul.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in table.Nodes)
        {
            if (node.Properties.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
                names[node.Id] = name;
            else
                names[node.Id] = node.Id;
        }

        foreach (var edge in table.Edges)
        {
            edges.Add(new EdgeInfo
            {
                EdgeId = edge.Id,
                //A GUN edge is the triple, not a stored thing with a type of its own.
                EdgeIdType = null,
                Label = edge.Label,
                OtherNodeId = edge.Target,
                OtherNodeLabel = names.TryGetValue(edge.Target, out var label) ? label : edge.Target
            });
        }

        return edges;
    }

    public string ParseDisplayLabel(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var node in GraphDataConverter.ToTable(result.Data).Nodes)
            if (node.Properties.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
                return name;

        return null;
    }

    //── Writes ──────────────────────────────────────────────────────────
    //
    //GUN writes with a chained .put(), so what gets staged is that call — real GUN JavaScript, one
    //statement per line — rather than a query in a language GUN does not have. See GunWrite.

    ///<summary>
    ///A new node. GUN has no server-assigned ids, so the viewer picks the key: the label plus enough
    ///randomness that two people adding a node at once do not write over each other.
    ///</summary>
    public string AddVertex(string label)
    {
        return GunWrite.Put(NewSoul(label), Typed(label)).ToStatement();
    }

    public string AddVertexWithName(string label, string name)
    {
        var values = Typed(label);
        values["name"] = name;

        return GunWrite.Put(NewSoul(label), values).ToStatement();
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        var values = Typed(label);
        values["name"] = name;
        values[GdbvKeys.X] = x.ToString(CultureInfo.InvariantCulture);
        values[GdbvKeys.Y] = y.ToString(CultureInfo.InvariantCulture);

        return GunWrite.Put(NewSoul(label), values).ToStatement();
    }

    ///<summary>
    ///A node whose key is already decided — an import's. GUN keys are the caller's to choose, so an
    ///imported id is used as the soul directly, and re-importing the same graph writes over itself rather
    ///than duplicating.
    ///</summary>
    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var values = Typed(label);

        foreach (var kv in properties)
            values[kv.Key] = kv.Value;

        return GunWrite.Put(id, values).ToStatement();
    }

    public string AddEdge(string sourceId, string label, string targetId)
    {
        return GunWrite.Link(sourceId, label, targetId).ToStatement();
    }

    ///<summary>
    ///An edge, with its properties dropped. A GUN link is a key holding a node — there is nothing on it to
    ///carry a property, so an edge that had them would need a node of its own standing in for it, which is
    ///a modelling decision for whoever owns the data rather than one the viewer should make silently.
    ///</summary>
    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        return AddEdge(sourceId, label, targetId);
    }

    ///<summary>
    ///Not offered, because GUN will not do it. A node lives at the root of the graph and the root only
    ///accepts nodes — <c>put(null)</c> there comes back <i>"Data at root of graph must be a node (an
    ///object)"</i>. Deleting in GUN means nulling the <b>references</b> to a node, which is what dropping
    ///its edges does; the data itself stays for any peer that still holds the key.
    ///</summary>
    public string DropVertex(string id, string idType)
    {
        throw new NotSupportedException(
            "GUN has no way to delete a node. Its data stays for any peer holding the key. Its links were dropped, which is what makes it unreachable; clear the properties you no longer want.");
    }

    ///<summary>
    ///Nulls the key holding the link. The edge id is the triple it was synthesised from, so the node and
    ///the key come straight back out of it.
    ///</summary>
    public string DropEdge(string id, string idType)
    {
        if (!GunConverter.TryReadEdgeId(id, out var source, out var label, out var target))
            return GunWrite.Clear(id).ToStatement();

        return GunWrite.Clear(source, label, target).ToStatement();
    }

    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        return GunWrite.PutValue(EdgeSoul(type, id), key, value).ToStatement();
    }

    ///<summary>Nulls one key, which is how GUN removes a property.</summary>
    public string DropProperty(string type, string id, string key, string idType)
    {
        return GunWrite.PutValue(EdgeSoul(type, id), key, null).ToStatement();
    }

    ///<summary>
    ///Not offered. Stripping every <c>gdbv*</c> property means visiting every node there is, and GUN cannot
    ///enumerate its nodes — the same reason it has no "load the database".
    ///</summary>
    public string DropAllViewerProperties()
    {
        throw new NotSupportedException(
            "GUN cannot list its own nodes, so there is no way to visit every one and strip the viewer's properties. Remove them from a node you can reach instead.");
    }

    //A property written to an "edge" has nowhere to go: a GUN link is a key holding a node, not a thing.
    //The write is aimed at the node the id names, which for a node id is simply itself.
    private static string EdgeSoul(string type, string id)
    {
        if (type == "edge" && GunConverter.TryReadEdgeId(id, out var source, out _, out _))
            return source;

        return id;
    }

    private static Dictionary<string, string> Typed(string label)
    {
        var values = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(label))
            values[GunConverter.TypeProperty] = label;

        return values;
    }

    //A key for a node nobody has named. Readable first, so a soul in the data still says what it is.
    private static string NewSoul(string label)
    {
        var prefix = (label ?? "").Trim();

        if (prefix.Length == 0)
            prefix = GunConverter.DefaultLabel;

        return $"{prefix}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    //── Guards and parsing ──────────────────────────────────────────────

    ///<summary>
    ///True for a staged write statement. A GUN <i>read</i> is a key path and can never mutate — the form
    ///cannot express anything else — so this only ever fires on the Generated buffer.
    ///</summary>
    public bool IsMutating(string query)
    {
        return GunStatementParser.IsMutating(query);
    }

    public List<GraphEdit> ParseEdits(string buffer)
    {
        return GunStatementParser.Parse(buffer);
    }
}
