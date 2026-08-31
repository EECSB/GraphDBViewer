using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///What the browser posts to the host's execute endpoint when a connection is marked
///<see cref="GremlinDB.GremlinConnection.ViaServer"/>: the connection to dial and the query to run.
///</summary>
public sealed class GraphQueryRequest
{
    public GremlinDB.GremlinConnection Connection { get; set; }
    public string Query { get; set; }
}

///<summary>
///The wire form of a <see cref="GraphDbResult"/>. The result itself is a struct with a private
///constructor — deliberately, so no implementation can hand back a null — so it crosses the wire as
///this and is rebuilt through the same factory methods every other caller uses.
///</summary>
public sealed class GraphQueryResponse
{
    public bool IsError { get; set; }
    public string Error { get; set; }

    ///<summary>The graph payload, or null for an error or a tabular answer.</summary>
    public JsonElement? Data { get; set; }

    ///<summary>The rows/boolean answer, or null for a graph result.</summary>
    public GraphDbTable Table { get; set; }

    ///<summary>The engine's verbatim response text, or null to let the JSON view pretty-print Data.</summary>
    public string Raw { get; set; }

    public static GraphQueryResponse From(GraphDbResult result)
    {
        if (result.IsError)
            return new GraphQueryResponse { IsError = true, Error = result.Error };

        if (result.Table != null)
            return new GraphQueryResponse { Table = result.Table, Raw = result.RawResponse };

        var response = new GraphQueryResponse { Raw = result.RawResponse };

        //A tabular or empty result leaves Data at default(JsonElement), which has no backing document —
        //serializing it would throw, so it travels as null and rebuilds the same way.
        if (result.Data.ValueKind != JsonValueKind.Undefined)
            response.Data = result.Data;

        return response;
    }

    public GraphDbResult ToResult()
    {
        if (IsError)
            return GraphDbResult.Failure(Error);

        if (Table != null)
            return GraphDbResult.Tabular(Table, Raw);

        if (Data.HasValue)
            return GraphDbResult.Success(Data.Value, Raw);

        return GraphDbResult.Success(default, Raw);
    }
}
