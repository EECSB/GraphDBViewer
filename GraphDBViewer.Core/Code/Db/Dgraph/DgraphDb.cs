using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Runs DQL against Dgraph's HTTP API. Plain HTTP and JSON, so one driver serves both routes — the browser
///runs it directly when Dgraph allows the origin, the host runs the same code when it doesn't.
///
///Dgraph splits reading from writing across <b>two endpoints</b>: <c>/query</c> refuses a mutation outright
///("Expected some name. Got: lex.Item set"), and <c>/mutate</c> is where <c>set</c> / <c>delete</c> blocks
///go. The viewer hands a driver one string and expects it to work, so the request is routed by looking at
///what the query actually is.
///</summary>
public class DgraphDb : IGraphDb
{
    ///<summary>Dgraph Alpha's HTTP port.</summary>
    public const int DefaultPort = 8080;


    //A mutation is a top-level set / delete block, or an upsert (which contains one). Matched outside of
    //any nesting so a predicate merely named "set" inside a query block doesn't reroute a read.
    private static readonly Regex MutationPattern = new(
        @"^\s*(upsert\s*\{|\{\s*(set|delete)\s*\{)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //The same thing said in JSON — {"set":[…]} / {"delete":[…]} — which is what the viewer stages, RDF
    //needing a newline after every triple and the staged buffer committing line by line. An upsert leads
    //with its query instead, the mutation following in the same object.
    private static readonly Regex JsonMutationPattern = new(
        @"^\s*\{\s*""(set|delete|query)""\s*:",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly GremlinDB.GremlinConnection _connection;

    public DgraphDb(HttpClient http, GremlinDB.GremlinConnection connection)
    {
        _http = http;
        _connection = connection;
    }

    ///<summary>Where reads go.</summary>
    public string QueryUrl => BaseUrl + "/query";

    ///<summary>Where writes go — committed immediately, the viewer having no transaction of its own.</summary>
    public string MutateUrl => BaseUrl + "/mutate?commitNow=true";

    ///<summary>Where a schema change goes. A third endpoint, reached only to prepare a database for import.</summary>
    public string AlterUrl => BaseUrl + "/alter";

    ///<summary>
    ///Marks a schema change so it survives the trip through the server proxy, which can only carry query
    ///text. Nothing a user could type: a DQL query never starts with this.
    ///</summary>
    public const string AlterPrefix = "#alter# ";

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

    ///<summary>True when the query writes, and so belongs at <see cref="MutateUrl"/>.</summary>
    public static bool IsMutation(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return MutationPattern.IsMatch(query) || IsJsonMutation(query);
    }

    ///<summary>
    ///True when the write is the JSON form rather than RDF N-Quads. The two go to the same endpoint and
    ///differ only in what Dgraph is told the body is, so the content type is decided here too.
    ///</summary>
    public static bool IsJsonMutation(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return JsonMutationPattern.IsMatch(query);
    }

    public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connection.Hostname))
            return GraphDbResult.Failure("No Dgraph host configured.");

        //A schema change marked by the preparation step. It travels as query text so the server route can
        //carry it, and is unpacked here rather than at a second endpoint the proxy would have to know about.
        if ((query ?? "").StartsWith(AlterPrefix, StringComparison.Ordinal))
        {
            var error = await AlterAsync(query.Substring(AlterPrefix.Length), cancellationToken);

            if (error != null)
                return GraphDbResult.Failure(error);

            return DgraphConverter.ToGraphDbResult("""{ "data": { "code": "Success" } }""");
        }

        try
        {
            bool mutating = IsMutation(query);

            string url;
            string contentType;

            if (mutating)
            {
                url = MutateUrl;

                //A hand-written set/delete block is RDF N-Quads; what the viewer stages is the JSON form,
                //and Dgraph parses the body by what it is told it is.
                if (IsJsonMutation(query))
                    contentType = "application/json";
                else
                    contentType = "application/rdf";
            }
            else
            {
                url = QueryUrl;
                contentType = "application/dql";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(query ?? "", Encoding.UTF8, contentType);
            Authorize(request);

            var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            //Dgraph reports failures in the body's errors array, and says far more there than the status
            //code does — so the body is read first either way.
            var result = DgraphConverter.ToGraphDbResult(body);

            if (result.IsError || response.IsSuccessStatusCode)
                return result;

            return GraphDbResult.Failure($"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}");
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
    ///Applies a schema change. Returns null when it worked, else what Dgraph said. Kept separate from
    ///<see cref="ExecuteAsync"/> because it is not a query: it changes the shape of someone's database, and
    ///the viewer only reaches it after asking (see <see cref="DgraphImportPreparation"/>).
    ///</summary>
    public async Task<string> AlterAsync(string schema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connection.Hostname))
            return "No Dgraph host configured.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AlterUrl);
            request.Content = new StringContent(schema ?? "", Encoding.UTF8, "application/rdf");
            Authorize(request);

            var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var error = DgraphConverter.ReadError(doc.RootElement);

            if (error != null)
                return error;

            if (!response.IsSuccessStatusCode)
                return $"HTTP {(int)response.StatusCode}: {GraphWireText.Truncate(body)}";

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    ///<summary>
    ///Attaches the access token, if one is configured. Dgraph reads it from a different header depending on
    ///where it runs — <c>X-Dgraph-AccessToken</c> for a self-hosted cluster with ACLs, <c>Dg-Auth</c> for
    ///Dgraph Cloud — so both are sent rather than making the user say which kind they are pointing at.
    ///</summary>
    private void Authorize(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_connection.AuthKey))
            return;

        request.Headers.TryAddWithoutValidation("X-Dgraph-AccessToken", _connection.AuthKey);
        request.Headers.TryAddWithoutValidation("Dg-Auth", _connection.AuthKey);
    }


    public ValueTask DisposeAsync()
    {
        //Stateless over HTTP — the HttpClient is owned by the caller.
        return ValueTask.CompletedTask;
    }
}
