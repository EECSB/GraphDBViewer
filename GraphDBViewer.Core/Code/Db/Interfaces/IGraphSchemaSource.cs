using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///The outcome of reading a database's schema vocabulary. <see cref="Vocabulary"/> is never null so the
///editor's autocomplete always has something to bind to; <see cref="HasErrors"/> says whether any of the
///underlying queries failed, which the caller needs because an empty schema and a failed read look
///identical otherwise — autocomplete can live with "empty", the NL prompt must say "unknown".
///</summary>
public sealed class SchemaReadResult
{
    public SchemaVocabulary Vocabulary { get; init; } = new();
    public bool HasErrors { get; init; }
}

///<summary>
///Reads a database's schema — which labels exist, what properties they carry, and how they connect.
///
///It is a seam rather than a shared helper because the engines answer in different shapes: Gremlin's
///<c>groupCount()</c> / <c>project()</c> queries come back as GraphSON maps, while Cypher answers the same
///questions as ordinary rows. Each implementation runs its own queries and normalizes the result, so the
///viewer gets one <see cref="SchemaVocabulary"/> and one schema-graph JSON regardless of engine.
///</summary>
public interface IGraphSchemaSource
{
    ///<summary>The labels / keys used for editor autocomplete and the NL prompt.</summary>
    Task<SchemaReadResult> ReadVocabularyAsync(IGraphDb db, CancellationToken cancellationToken = default);

    ///<summary>
    ///The schema as a renderable meta-graph — a node per vertex label (count + property keys), an edge per
    ///distinct relationship triple — in the same flat JSON the normal 2D / 3D pipeline draws.
    ///</summary>
    Task<string> BuildSchemaGraphJsonAsync(IGraphDb db, CancellationToken cancellationToken = default);
}
