using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///What Dgraph needs before a graph can be imported into it: an <b>index</b> on the predicate the viewer
///keeps an imported node's original id in.
///
///The reason is Dgraph's alone. An import's edges reference the source graph's ids, and Dgraph assigns a
///<c>uid</c> only when a mutation commits — so a node written on one staged line is unfindable by the next
///unless its id is in the graph and can be looked up. Looking it up means <c>eq()</c>, and <c>eq()</c>
///needs an index. Worse, without the index Dgraph does not complain: it matches nothing, so every upsert
///would quietly make another copy. That is why this is checked up front rather than left to fail.
///
///An index is a schema change to someone's database, which the viewer does not make on its own — see
///<see cref="IGraphImportPreparation"/>.
///</summary>
public sealed class DgraphImportPreparation : IGraphImportPreparation
{
    public static readonly DgraphImportPreparation Instance = new();

    ///<summary>The alter Dgraph is asked to apply, which is also what the user is shown.</summary>
    public static readonly string IndexAlter = $"{DqlQueryBuilder.ImportIdPredicate}: string @index(exact) .";

    public string Requirement => IndexAlter;

    public string Reason =>
        $"An import's edges reference the ids the graph came with, and Dgraph assigns its own only when a mutation commits, so the viewer keeps each imported id in a \"{DqlQueryBuilder.ImportIdPredicate}\" predicate and finds the nodes by it. That lookup needs an index, and without one Dgraph matches nothing silently, which would import a second copy every time.";

    ///<summary>True when <see cref="DqlQueryBuilder.ImportIdPredicate"/> is declared with an index.</summary>
    public async Task<bool> IsReadyAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        //Asking for the one predicate rather than the whole schema, so the answer is unambiguous.
        var result = await db.ExecuteAsync($"schema(pred: [{DqlQueryBuilder.ImportIdPredicate}]) {{ type index tokenizer }}", cancellationToken);

        if (result.IsError)
            return false;

        return IsIndexed(result.RawResponse);
    }

    ///<summary>Reads a schema answer for whether the import predicate is there and indexed.</summary>
    public static bool IsIndexed(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return false;

        System.Text.Json.JsonDocument doc;

        try
        {
            doc = System.Text.Json.JsonDocument.Parse(responseJson);
        }
        catch
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("schema", out var schema)
                || schema.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;

            foreach (var predicate in schema.EnumerateArray())
            {
                if (predicate.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;

                if (!predicate.TryGetProperty("predicate", out var name)
                    || name.GetString() != DqlQueryBuilder.ImportIdPredicate)
                    continue;

                return predicate.TryGetProperty("index", out var index) && index.ValueKind == System.Text.Json.JsonValueKind.True;
            }
        }

        return false;
    }

    ///<summary>
    ///Adds the index. Dgraph takes a schema change at its own <c>/alter</c> endpoint, which is a third one
    ///the driver otherwise never touches — so this asks the driver for it by name rather than pretending a
    ///schema line is a query.
    ///</summary>
    public async Task<string> PrepareAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        if (db is DgraphDb dgraph)
            return await dgraph.AlterAsync(IndexAlter, cancellationToken);

        //The server route hands back a proxy rather than the driver itself.
        return await AlterThroughAsync(db, cancellationToken);
    }

    //Over the proxy the alter travels as the query text it is; DgraphDb routes it by shape on the far side.
    private static async Task<string> AlterThroughAsync(IGraphDb db, CancellationToken cancellationToken)
    {
        var result = await db.ExecuteAsync(DgraphDb.AlterPrefix + IndexAlter, cancellationToken);

        if (result.IsError)
            return result.Error;

        return null;
    }
}
