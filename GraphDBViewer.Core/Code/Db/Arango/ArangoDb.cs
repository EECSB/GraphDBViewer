using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Runs AQL against ArangoDB's HTTP cursor API. Plain HTTP and JSON, so — unlike Bolt — the same driver
///serves both routes: the browser runs it directly when the server allows this origin, and the host runs
///the identical code when it doesn't.
///
///An AQL answer can arrive in batches. The cursor is followed to the end before the result is returned, so
///a caller never sees half a graph; <see cref="ArangoConverter"/> then decides whether it is a graph or a
///table.
///</summary>
public class ArangoDb : IGraphDb
{
    ///<summary>ArangoDB's default database — used when the connection names none.</summary>
    public const string DefaultDatabase = "_system";

    ///<summary>Rows fetched per batch. Large enough that a normal viewer query completes in one round trip.</summary>
    public const int BatchSize = 2000;


    private readonly HttpClient _http;
    private readonly GremlinDB.GremlinConnection _connection;

    public ArangoDb(HttpClient http, GremlinDB.GremlinConnection connection)
    {
        _http = http;
        _connection = connection;
    }

    ///<summary>The cursor endpoint for this connection's database.</summary>
    public string CursorUrl
    {
        get { return $"{BaseUrl}/_db/{DatabaseName}/_api/cursor"; }
    }

    ///<summary>
    ///Where a query is planned without being run. ArangoDB has no <c>EXPLAIN</c> keyword — explaining is a
    ///different endpoint, which is why the debugger marks the query and this driver routes on the mark.
    ///</summary>
    public string ExplainUrl
    {
        get { return $"{BaseUrl}/_db/{DatabaseName}/_api/explain"; }
    }

    private string BaseUrl
    {
        get
        {
            string scheme;
            if (_connection.UseSSL)
                scheme = "https";
            else
                scheme = "http";

            return $"{scheme}://{_connection.Hostname}:{_connection.Port}";
        }
    }

    private string DatabaseName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_connection.Database))
                return DefaultDatabase;

            return _connection.Database;
        }
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connection.Hostname))
            return GraphDbResult.Failure("No ArangoDB host configured.");

        //The debugger cannot ask for a plan in AQL — explaining is a different endpoint and profiling is an
        //option on the request — so it marks the query and the routing happens here. See AqlPlan.
        var (kind, unmarked) = AqlPlan.ReadMarker(query);

        try
        {
            if (kind == AqlPlan.PlanKind.Explain)
                return await ExplainAsync(unmarked, cancellationToken);

            var run = await RunCursorAsync(BuildRequestBody(unmarked, kind == AqlPlan.PlanKind.Profile), cancellationToken);

            if (run.Error != null)
                return GraphDbResult.Failure(run.Error);

            if (kind == AqlPlan.PlanKind.Profile)
                return PlanResult(run.Extra);

            var json = JsonSerializer.Serialize(run.Rows);
            var parsed = JsonDocument.Parse(json).RootElement;

            return ArangoConverter.ToGraphDbResult(parsed, JsonSerializer.Serialize(run.Rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (OperationCanceledException)
        {
            //Re-thrown like the other drivers so callers can tell a cancel from a failure.
            throw;
        }
        catch (Exception ex)
        {
            return GraphDbResult.Failure(ex.Message);
        }
    }

    ///<summary>
    ///Runs a query to the end of its cursor. An AQL answer can arrive in batches, and the caller is handed
    ///one complete result rather than half a graph. The measurements a profile asks for arrive with the
    ///last batch, which is why <c>Extra</c> is whatever the final response carried.
    ///</summary>
    private async Task<(List<JsonElement> Rows, JsonElement Extra, string Error)> RunCursorAsync(string body, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();

        using var first = await PostAsync(CursorUrl, body, cancellationToken);
        var error = await ReadBatchAsync(first, rows, cancellationToken);

        if (error != null)
            return (rows, default, error);

        string cursorId = _cursorId;
        int guard = 0;

        while (cursorId != null && guard++ < 1000)
        {
            using var next = await FetchNextAsync(cursorId, cancellationToken);
            var batchError = await ReadBatchAsync(next, rows, cancellationToken);

            if (batchError != null)
                return (rows, default, batchError);

            cursorId = _cursorId;
        }

        return (rows, _extra, null);
    }

    ///<summary>Plans the query without running it, through ArangoDB's own explain endpoint.</summary>
    private async Task<GraphDbResult> ExplainAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(ExplainUrl, JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = query ?? "" }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch
        {
            return GraphDbResult.Failure($"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            //A query that cannot be planned is usually a syntax error, and ArangoDB says exactly where.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var errorFlag)
                && errorFlag.ValueKind == JsonValueKind.True)
                return GraphDbResult.Failure(ReadErrorMessage(root, response));

            if (!response.IsSuccessStatusCode)
                return GraphDbResult.Failure($"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}");

            return PlanResult(root);
        }
    }

    //A plan reaches the viewer as an ordinary table, with the engine's own response kept for the JSON view.
    private static GraphDbResult PlanResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return GraphDbResult.Failure("ArangoDB answered without a plan.");

        var raw = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });

        return GraphDbResult.Tabular(AqlPlan.ToTable(root), raw);
    }

    //Set by ReadBatchAsync to the cursor id when the server says there is another batch, else null.
    private string _cursorId;

    //Set by ReadBatchAsync to the response's "extra" — the plan and per-node statistics a profile asks for.
    private JsonElement _extra;

    private static string BuildRequestBody(string query, bool profile = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["query"] = query ?? "",
            ["batchSize"] = BatchSize
        };

        if (profile)
            payload["options"] = new Dictionary<string, object> { ["profile"] = AqlPlan.ProfileLevel };

        return JsonSerializer.Serialize(payload);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        Authorize(request);

        return await _http.SendAsync(request, cancellationToken);
    }

    ///<summary>
    ///Reads the batch after the first. ArangoDB moved this from PUT to POST during 3.x and kept PUT working,
    ///so POST is tried first and a server that rejects the verb falls back — rather than the viewer only
    ///working against one half of the supported versions.
    ///</summary>
    private async Task<HttpResponseMessage> FetchNextAsync(string cursorId, CancellationToken cancellationToken)
    {
        var url = $"{CursorUrl}/{cursorId}";
        var response = await PostAsync(url, "", cancellationToken);

        if (response.StatusCode != System.Net.HttpStatusCode.NotFound && response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
            return response;

        response.Dispose();

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        Authorize(request);

        return await _http.SendAsync(request, cancellationToken);
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_connection.Username) && string.IsNullOrEmpty(_connection.AuthKey))
            return;

        //ArangoDB's default user is "root"; a blank username with a password almost always means it.
        string username = _connection.Username;

        if (string.IsNullOrEmpty(username))
            username = "root";

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{_connection.AuthKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    ///<summary>
    ///Appends one batch's rows and records whether another follows. Returns an error message, or null when
    ///the batch was read cleanly.
    ///</summary>
    private async Task<string> ReadBatchAsync(HttpResponseMessage response, List<JsonElement> rows, CancellationToken cancellationToken)
    {
        _cursorId = null;
        _extra = default;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch
        {
            //A non-JSON body is the server (or something in front of it) failing outside the API.
            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}";

            return $"ArangoDB returned a response that isn't JSON: {GraphWireText.Truncate(body)}";
        }

        using (doc)
        {
            var root = doc.RootElement;

            //ArangoDB reports failures in the body — errorMessage says far more than the status code does.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var errorFlag)
                && errorFlag.ValueKind == JsonValueKind.True)
                return ReadErrorMessage(root, response);

            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}";

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                foreach (var row in result.EnumerateArray())
                    rows.Add(row.Clone());

            //Cloned because the document it belongs to is disposed with this batch, and a profile reads it
            //after the last one.
            if (root.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object)
                _extra = extra.Clone();

            if (root.TryGetProperty("hasMore", out var hasMore)
                && hasMore.ValueKind == JsonValueKind.True
                && root.TryGetProperty("id", out var id))
                _cursorId = id.ToString();

            return null;
        }
    }

    private static string ReadErrorMessage(JsonElement root, HttpResponseMessage response)
    {
        if (root.TryGetProperty("errorMessage", out var message) && message.ValueKind == JsonValueKind.String)
        {
            var text = message.GetString();

            if (root.TryGetProperty("errorNum", out var number))
                return $"{text} (ArangoDB error {number})";

            return text;
        }

        return $"HTTP {(int)response.StatusCode}";
    }


    public ValueTask DisposeAsync()
    {
        //Stateless over HTTP — the HttpClient is owned by the caller.
        return ValueTask.CompletedTask;
    }
}
