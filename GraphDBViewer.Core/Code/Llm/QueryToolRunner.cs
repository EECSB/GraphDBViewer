using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Exposes the query tools a model may call against the connected graph.
///
///<c>run_read_query</c> is always offered: it executes a query and hands back the result, so the model can
///explore the data and check a query before answering. Mutating queries are refused on it by the engine's
///own guard (<see cref="IGraphQueryBuilder.IsMutating"/>, the debugger's too) — the guard, not the model's
///good manners — and what passes the guard runs on the engine's own read-only path where it has one
///(<see cref="IReadOnlyCapableGraphDb"/>): a READ session on Neo4j and Memgraph, <c>/query</c> on Dgraph.
///ArangoDB and the Gremlin servers have the guard alone.
///
///<c>run_write_query</c> is the same thing without the guard, and is offered only when
///<see cref="AllowWrites"/> has been turned on, which happens in one place: the approval mode a person
///picked. Off, it is neither listed nor honored.
///
///The query language is the connected database's own (<see cref="GraphDbProvider.QueryLanguage"/>): the
///tools name it, and the guard is that engine's builder. Gremlin where nothing says otherwise, which is what
///the one-argument constructor means. Named GremlinToolRunner until 2026-10-03, when Ask AI began offering
///these tools on every engine whose language the editor speaks rather than on Gremlin alone.
///</summary>
public class QueryToolRunner : ILlmToolRunner, IWriteCapableToolRunner
{
    private const int MaxResultChars = 4000;

    ///<summary>The read-only tool's name, which the approval layer and the prompt both have to agree on.</summary>
    public const string ReadToolName = "run_read_query";

    ///<summary>The name of the tool that changes the graph. Its presence in a tool list means writes are on.</summary>
    public const string WriteToolName = "run_write_query";

    //Takes the interface rather than a concrete client: this only needs "something that runs a query",
    //which also makes it testable without a live database.
    private readonly IGraphDb _db;
    private readonly IGraphQueryBuilder _queryBuilder;
    private readonly IReadOnlyList<LlmTool> _readOnlyTools;
    private readonly IReadOnlyList<LlmTool> _allTools;

    public QueryToolRunner(IGraphDb db) : this(db, null, null)
    {
    }

    ///<param name="queryBuilder">Supplies the mutation guard; null means Gremlin.</param>
    ///<param name="language">Editor-language id of the database's language ("cypher", "aql", "dql"); null means Gremlin.</param>
    public QueryToolRunner(IGraphDb db, IGraphQueryBuilder queryBuilder, string language)
    {
        _db = db;
        _queryBuilder = queryBuilder ?? GremlinQueryBuilderAdapter.Instance;
        _readOnlyTools = BuildTools(language, false);
        _allTools = BuildTools(language, true);
    }

    ///<summary>
    ///Whether the write tool is offered and honored. Off until something sets it, and the only thing that
    ///does is <see cref="ApprovingToolRunner"/> acting on the mode a person chose.
    ///</summary>
    public bool AllowWrites { get; set; }

    public IReadOnlyList<LlmTool> Tools
    {
        get
        {
            if (AllowWrites)
                return _allTools;
            else
                return _readOnlyTools;
        }
    }

    //The language is named and nothing more. A model that knows the language knows what writes in it, and the
    //keyword lists these carried until 2026-10-03 only ever named some of it, and for two of the four languages:
    //built for AQL or DQL, the tools said Gremlin.
    private static IReadOnlyList<LlmTool> BuildTools(string languageId, bool withWrites)
    {
        var language = NlQueryPrompt.LanguageDisplayName(languageId);

        var tools = new List<LlmTool>
        {
            new LlmTool
            {
                Name = ReadToolName,
                Description = $@"Runs a read-only {language} query against the connected database and returns the result as JSON.
Use it to explore the data and to check that a query returns what the user wants before giving your
final answer. A query that would change the database is refused.",
                InputSchemaJson = QuerySchema($"The read-only {language} query to run.")
            }
        };

        if (!withWrites)
            return tools;

        tools.Add(new LlmTool
        {
            Name = WriteToolName,
            Description = $@"Runs a {language} query that changes the connected database and returns the result as JSON.
This is not undoable. Use it only when the user has asked for the data to change, make the smallest
change that does what they asked, read first with run_read_query when you are unsure what is there,
and never delete more than was asked for. Say plainly what you changed.",
            InputSchemaJson = QuerySchema($"The {language} query to run."),
            Changes = true
        });

        return tools;
    }

    ///<summary>Both tools take the same one argument; only the sentence describing it differs.</summary>
    private static string QuerySchema(string description)
    {
        return $$$"""{"type":"object","properties":{"query":{"type":"string","description":"{{{description}}}"}},"required":["query"]}""";
    }

    ///<summary>
    ///Runs a call and says what came of it. A refusal or a failure is marked as one (<see cref="LlmToolOutput.IsError"/>),
    ///which the providers pass on to the model, so the words no longer start with "Error:" to say it.
    ///</summary>
    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        bool isWrite = name == WriteToolName;

        if (name != ReadToolName && !isWrite)
            return LlmToolOutput.Failure($"Unknown tool: {name}");

        //Asked for by a model that was never offered it. Refused here as well as at the approval layer,
        //because whichever of the two is wired up wrong, this is the one holding the database.
        if (isWrite && !AllowWrites)
            return LlmToolOutput.Failure("Writing to the graph is turned off, so that query was not run.");

        var query = ExtractQuery(argumentsJson);

        if (string.IsNullOrWhiteSpace(query))
            return LlmToolOutput.Failure("No 'query' argument was provided.");

        //The guard's reason rather than a bare no, so the model can put its query right: a closure that only reads is
        //refused with the rest, and told so it can use the traversal's steps instead.
        if (!isWrite)
        {
            var refusal = _queryBuilder.ReadRefusal(query);

            if (refusal != null)
                return LlmToolOutput.Failure(refusal);
        }

        var result = await RunAsync(query, isWrite, ct);

        if (result.IsError)
            return LlmToolOutput.Failure($"Query error: {result.Error}");

        var text = result.ToString();

        //Bound the tool output so a large result can't blow the model's context window.
        if (text.Length > MaxResultChars)
            text = text.Substring(0, MaxResultChars) + "\n…(truncated)";

        return LlmToolOutput.Result(text);
    }

    //A read goes down the engine's own read-only path where it has one, behind the guard rather than in place of
    //it: the guard is this runner's reading of the text, and the engine's path is the one a query cannot talk its
    //way past.
    private Task<GraphDbResult> RunAsync(string query, bool isWrite, CancellationToken ct)
    {
        if (!isWrite && _db is IReadOnlyCapableGraphDb reader)
            return reader.ExecuteReadOnlyAsync(query, ct);
        else
            return _db.ExecuteAsync(query, ct);
    }

    ///<summary>The query argument out of a tool call, or null when there is not one. Public so an approval
    ///prompt can show what it is about to run rather than the raw JSON the model sent.</summary>
    public static string ExtractQuery(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);

            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                return q.GetString();
        }
        catch { }

        return null;
    }
}
