using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Presents the long-standing static <see cref="GremlinQueryBuilder"/> as an <see cref="IGraphQueryBuilder"/>,
///so the viewer can ask its provider for queries instead of naming Gremlin directly. Pure delegation — the
///statics stay the canonical implementation (and the tests keep exercising them there).
///</summary>
public sealed class GremlinQueryBuilderAdapter : IGraphQueryBuilder
{
    public static readonly GremlinQueryBuilderAdapter Instance = new();

    public string LimitedVertices(int limit)
    {
        return GremlinQueryBuilder.LimitedVertices(limit);
    }

    public string FullGraph(int? limit)
    {
        return GremlinQueryBuilder.FullGraph(limit);
    }

    public string Neighbors(string id, int limit)
    {
        return GremlinQueryBuilder.Neighbors(id, limit);
    }

    public string InEdges(string vertexId)
    {
        return GremlinQueryBuilder.InEdges(vertexId);
    }

    public string OutEdges(string vertexId)
    {
        return GremlinQueryBuilder.OutEdges(vertexId);
    }

    public string VertexDisplayLabel(string vertexId)
    {
        return GremlinQueryBuilder.VertexDisplayLabel(vertexId);
    }

    //The InEdges / OutEdges projection comes back as GraphSON objects keyed eId / eLabel / vId / vLabel.
    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        var edges = new List<EdgeInfo>();

        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array)
            return edges;

        foreach (var item in result.Data.EnumerateArray())
        {
            var unwrapped = GraphDataConverter.UnwrapElement(item);

            edges.Add(new EdgeInfo
            {
                EdgeId = GetJsonString(unwrapped, "eId"),
                EdgeIdType = GetJsonIdType(unwrapped, "eId"),
                Label = GetJsonString(unwrapped, "eLabel"),
                OtherNodeId = GetJsonString(unwrapped, "vId"),
                OtherNodeLabel = GetJsonString(unwrapped, "vLabel")
            });
        }

        return edges;
    }

    public string ParseDisplayLabel(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array || result.Data.GetArrayLength() == 0)
            return null;

        return GraphDataConverter.UnwrapElement(result.Data[0]).ToString().Trim('"');
    }

    private static string GetJsonString(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var val))
        {
            var unwrapped = GraphDataConverter.UnwrapElement(val);

            return unwrapped.ToString().Trim('"');
        }

        return "?";
    }

    //The GraphSON id type (e.g. "g:Int64") of a projected id field, read before it is unwrapped to a
    //bare value, so an edge removed from the connections panel emits a correctly-typed id literal.
    private static string GetJsonIdType(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty(prop, out var val))
            return null;

        if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("@type", out var t))
            return t.GetString();

        return null;
    }

    public string AddVertex(string label)
    {
        return GremlinQueryBuilder.AddVertex(label);
    }

    public string AddVertexWithName(string label, string name)
    {
        return GremlinQueryBuilder.AddVertexWithName(label, name);
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        return GremlinQueryBuilder.AddVertexWithNameAt(label, name, x, y);
    }

    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        return GremlinQueryBuilder.AddVertexWithProperties(label, id, properties);
    }

    public string AddEdge(string sourceId, string label, string targetId)
    {
        return GremlinQueryBuilder.AddEdge(sourceId, label, targetId);
    }

    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        return GremlinQueryBuilder.AddEdgeWithProperties(sourceId, label, targetId, properties);
    }

    public string DropVertex(string id, string idType)
    {
        return GremlinQueryBuilder.DropVertex(id, idType);
    }

    public string DropEdge(string id, string idType)
    {
        return GremlinQueryBuilder.DropEdge(id, idType);
    }

    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        return GremlinQueryBuilder.SetProperty(type, id, key, value, idType);
    }

    public string DropProperty(string type, string id, string key, string idType)
    {
        return GremlinQueryBuilder.DropProperty(type, id, key, idType);
    }

    public string DropAllViewerProperties()
    {
        return GremlinQueryBuilder.DropAllViewerProperties();
    }

    public bool IsMutating(string query)
    {
        return GremlinStepParser.IsMutating(query);
    }

    public List<GraphEdit> ParseEdits(string buffer)
    {
        return GremlinEditParser.Parse(buffer);
    }
}
