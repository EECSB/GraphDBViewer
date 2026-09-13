using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads a Gremlin database's schema with the <c>groupCount()</c> / <c>group()</c> / <c>project()</c>
///queries and hands the GraphSON straight to <see cref="SchemaBuilder"/> — the behavior the viewer has
///always had, moved behind <see cref="IGraphSchemaSource"/> so a non-Gremlin engine can answer the same
///questions its own way.
///</summary>
public sealed class GremlinSchemaSource : IGraphSchemaSource
{
    public static readonly GremlinSchemaSource Instance = new();

    public async Task<SchemaReadResult> ReadVocabularyAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var vLabels = await db.ExecuteAsync(GremlinQueryBuilder.SchemaVertexLabels, cancellationToken);
        var eLabels = await db.ExecuteAsync(GremlinQueryBuilder.SchemaEdgeLabels, cancellationToken);
        var vKeys = await db.ExecuteAsync(GremlinQueryBuilder.SchemaVertexKeys, cancellationToken);

        var vocabulary = SchemaBuilder.ExtractVocabulary(Answered(vLabels), Answered(eLabels), Answered(vKeys));

        return new SchemaReadResult
        {
            Vocabulary = vocabulary,
            HasErrors = vLabels.IsError || eLabels.IsError || vKeys.IsError
        };
    }

    public async Task<string> BuildSchemaGraphJsonAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var vLabels = await db.ExecuteAsync(GremlinQueryBuilder.SchemaVertexLabels, cancellationToken);
        var eLabels = await db.ExecuteAsync(GremlinQueryBuilder.SchemaEdgeLabels, cancellationToken);
        var vKeys = await db.ExecuteAsync(GremlinQueryBuilder.SchemaVertexKeys, cancellationToken);
        var triples = await db.ExecuteAsync(GremlinQueryBuilder.SchemaEdgeTriples, cancellationToken);

        return SchemaBuilder.BuildSchemaGraphJson(Answered(vLabels), Answered(eLabels), Answered(vKeys), Answered(triples));
    }

    ///<summary>
    ///What a query answered, and nothing when it failed. Each of the four is asked separately and a
    ///schema built from three of them is worth more than none, so a failure contributes an empty
    ///element rather than stopping the read — which is why <c>HasErrors</c> is reported beside it.
    ///</summary>
    private static JsonElement Answered(GraphDbResult result)
    {
        if (result.IsError)
            return default;

        return result.Data;
    }
}
