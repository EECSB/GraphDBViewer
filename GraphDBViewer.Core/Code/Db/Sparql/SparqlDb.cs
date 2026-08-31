using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Runs SPARQL queries against an HTTP SPARQL endpoint directly from the browser (no backend). Uses a
///form-encoded POST so it stays a CORS "simple request" when there's no auth; basic-auth adds an
///Authorization header (which needs the endpoint to allow it via CORS). Results are normalized by
///<see cref="SparqlConverter"/>.
///</summary>
public class SparqlDb : IGraphDb
{
    private readonly HttpClient _http;
    private readonly GremlinDB.GremlinConnection _connection;

    public SparqlDb(HttpClient http, GremlinDB.GremlinConnection connection)
    {
        _http = http;
        _connection = connection;
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        return ToGraphDbResult(await QueryAsync(query, cancellationToken));
    }

    ///<summary>
    ///Maps a parsed SPARQL response onto the shared result type. A CONSTRUCT/DESCRIBE graph joins the
    ///same pipeline a Gremlin result does; SELECT / ASK become a table instead. An unrecognised body
    ///maps to an empty table, which is what the bindings pane already showed for it.
    ///</summary>
    public static GraphDbResult ToGraphDbResult(SparqlResult result)
    {
        if (result.IsError)
            return GraphDbResult.Failure(result.Error);

        if (result.Kind == SparqlKind.Graph)
            return GraphDbResult.Success(result.GraphData, result.RawJson);

        if (result.Kind == SparqlKind.Ask)
            return GraphDbResult.Tabular(new GraphDbTable { Boolean = result.Boolean }, result.RawJson);

        return GraphDbResult.Tabular(new GraphDbTable { Vars = result.Vars, Rows = result.Rows }, result.RawJson);
    }

    private async Task<SparqlResult> QueryAsync(string query, CancellationToken cancellationToken)
    {
        var endpoint = _connection.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return new SparqlResult { IsError = true, Error = "No SPARQL endpoint URL configured." };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new FormUrlEncodedContent(new[] { new System.Collections.Generic.KeyValuePair<string, string>("query", query ?? "") });

            //CONSTRUCT/DESCRIBE return RDF (ask for RDF/JSON so it renders as a graph); SELECT/ASK return a bindings table.
            if (IsGraphQuery(query))
            {
                request.Headers.Accept.ParseAdd("application/rdf+json");
                request.Headers.Accept.ParseAdd("application/ld+json");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/sparql-results+json");
            }

            if (!string.IsNullOrEmpty(_connection.Username) || !string.IsNullOrEmpty(_connection.AuthKey))
            {
                var credentials = System.Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_connection.Username}:{_connection.AuthKey}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new SparqlResult { IsError = true, Error = $"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}", RawJson = body };

            return SparqlConverter.Parse(body);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            return new SparqlResult { IsError = true, Error = ex.Message };
        }
    }

    //True when the query's form is CONSTRUCT or DESCRIBE (which return RDF triples, not a bindings table).
    private static bool IsGraphQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(query, @"\b(select|ask|construct|describe)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        var form = match.Groups[1].Value.ToLowerInvariant();

        return form == "construct" || form == "describe";
    }


    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
