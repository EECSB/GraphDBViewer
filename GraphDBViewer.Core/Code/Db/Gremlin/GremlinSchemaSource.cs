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

        var vocabulary = SchemaBuilder.ExtractVocabulary(
            vLabels.IsError ? default : vLabels.Data,
            eLabels.IsError ? default : eLabels.Data,
            vKeys.IsError ? default : vKeys.Data);

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

        return SchemaBuilder.BuildSchemaGraphJson(
            vLabels.IsError ? default : vLabels.Data,
            eLabels.IsError ? default : eLabels.Data,
            vKeys.IsError ? default : vKeys.Data,
            triples.IsError ? default : triples.Data);
    }
}
