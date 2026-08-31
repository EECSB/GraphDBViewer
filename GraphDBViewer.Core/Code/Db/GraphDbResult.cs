using System.Collections.Generic;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///A tabular result: the rows an engine returns when it answers with records rather than a graph — a
///SPARQL SELECT's bindings today, Cypher records later. <see cref="Boolean"/> carries a boolean-shaped
///answer instead (a SPARQL ASK), leaving <see cref="Vars"/> / <see cref="Rows"/> empty.
///</summary>
public sealed class GraphDbTable
{
    public List<string> Vars { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
    public bool? Boolean { get; set; }
}

///<summary>
///The result of a query against any graph database. Holds a graph payload (<see cref="Data"/>), a
///tabular one (<see cref="Table"/>), or an error. Never null — every caller reads it straight back
///without a null check, and a struct is the only way an implementation can't break that.
///</summary>
public readonly struct GraphDbResult
{
    //The engine's own response text, kept verbatim for the JSON view. Null means "pretty-print Data
    //instead" — the Gremlin path, where the JSON view has always shown the re-serialized payload.
    private readonly string _raw;

    private GraphDbResult(bool isError, string error, JsonElement data, GraphDbTable table, string raw)
    {
        IsError = isError;
        Error = error;
        Data = data;
        Table = table;
        _raw = raw;
    }

    public bool IsError { get; }
    public string Error { get; }

    ///<summary>The graph payload, in the flat vertex/edge JSON the converters render. Undefined for a tabular result.</summary>
    public JsonElement Data { get; }

    ///<summary>The rows/boolean the engine answered with, or null when this is a graph result.</summary>
    public GraphDbTable Table { get; }

    ///<summary>
    ///The engine's own response text, or null when the JSON view should pretty-print <see cref="Data"/>.
    ///Exposed so a result can cross a wire and be rebuilt on the other side without shipping the payload
    ///twice — once as <see cref="Data"/> and again as its pretty-printed form.
    ///</summary>
    public string RawResponse => _raw;

    public static GraphDbResult Success(JsonElement data, string raw = null)
    {
        return new(false, null, data, null, raw);
    }

    public static GraphDbResult Tabular(GraphDbTable table, string raw)
    {
        return new(false, null, default, table, raw);
    }

    public static GraphDbResult Failure(string error)
    {
        return new(true, error, default, null, null);
    }

    ///<summary>
    ///What the JSON view shows: the error on failure, else the engine's own response text, else the
    ///pretty-printed graph payload.
    ///</summary>
    public override string ToString()
    {
        if (IsError)
            return Error;

        if (_raw != null)
            return _raw;

        //A tabular result leaves Data at default(JsonElement), which has no backing document —
        //serializing it would throw.
        if (Data.ValueKind == JsonValueKind.Undefined)
            return "";

        return JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
    }
}
