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
///explore the data and check a query before answering. Mutating queries are refused on it (reusing the
///debugger's mutation guard), which is the whole of its safety — the guard, not the model's good manners.
///
///<c>run_write_query</c> is the same thing without the guard, and is offered only when
///<see cref="AllowWrites"/> has been turned on, which happens in one place: the approval mode a person
///picked. Off, it is neither listed nor honored.
///
///The query language follows the connected database: the guard and the wording come from the provider's
///<see cref="IGraphQueryBuilder"/>, defaulting to Gremlin, which is the only language this ran when it
///was written and what every existing caller still means.
///</summary>
public class GremlinToolRunner : ILlmToolRunner, IWriteCapableToolRunner
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

    public GremlinToolRunner(IGraphDb db) : this(db, null, null)
    {
    }

    ///<param name="queryBuilder">Supplies the mutation guard; null means Gremlin.</param>
    ///<param name="language">Editor-language id of the database ("cypher"); null or anything else means Gremlin.</param>
    public GremlinToolRunner(IGraphDb db, IGraphQueryBuilder queryBuilder, string language)
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

    private static IReadOnlyList<LlmTool> BuildTools(string languageId, bool withWrites)
    {
        string language;
        string mutations;
        string example;
        string writeExample;

        if (languageId == "cypher")
        {
            language = "Cypher";
            mutations = "CREATE/MERGE/SET/DELETE/REMOVE";
            example = "MATCH (n:Product) RETURN n LIMIT 3";
            writeExample = "CREATE (p:Product {name: 'Widget'}) RETURN p";
        }
        else
        {
            language = "Gremlin";
            mutations = "addV/addE/drop/property/merge";
            example = "g.V().hasLabel('Product').limit(3).valueMap(true)";
            writeExample = "g.addV('Product').property('name','Widget')";
        }

        var tools = new List<LlmTool>
        {
            new LlmTool
            {
                Name = ReadToolName,
                Description = $"Run a READ-ONLY {language} query against the connected graph and get the JSON result. "
                    + "Use it to explore the data (labels, property values, counts) and to verify your query returns "
                    + $"what the user wants before giving your final answer. Mutations ({mutations}) are rejected.",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\""
                    + $"The read-only {language} query to run, e.g. {example}" + "\"}},\"required\":[\"query\"]}"
            }
        };

        if (!withWrites)
            return tools;

        tools.Add(new LlmTool
        {
            Name = WriteToolName,
            Description = $"Run a {language} query that CHANGES the connected graph ({mutations}) and get the JSON result. "
                + "This is not undoable. Use it only when the user has asked for the data to change, make the smallest "
                + "change that does what they asked, read first with run_read_query when you are unsure what is there, "
                + "and never delete more than was asked for. Say plainly what you changed.",
            InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\""
                + $"The {language} query to run, e.g. {writeExample}" + "\"}},\"required\":[\"query\"]}"
        });

        return tools;
    }

    public async Task<string> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        bool isWrite = name == WriteToolName;

        if (name != ReadToolName && !isWrite)
            return $"Unknown tool: {name}";

        //Asked for by a model that was never offered it. Refused here as well as at the approval layer,
        //because whichever of the two is wired up wrong, this is the one holding the database.
        if (isWrite && !AllowWrites)
            return "Error: writing to the graph is turned off, so that query was not run.";

        var query = ExtractQuery(argumentsJson);

        if (string.IsNullOrWhiteSpace(query))
            return "Error: no 'query' argument was provided.";

        if (!isWrite && _queryBuilder.IsMutating(query))
            return "Error: that query mutates the graph, which is not allowed here. Only read-only queries can be run.";

        var result = await _db.ExecuteAsync(query, ct);

        if (result.IsError)
            return $"Query error: {result.Error}";

        var text = result.ToString();

        //Bound the tool output so a large result can't blow the model's context window.
        if (text.Length > MaxResultChars)
            text = text.Substring(0, MaxResultChars) + "\n…(truncated)";

        return text;
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
