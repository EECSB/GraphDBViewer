using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Something an engine needs in place before a graph can be imported into it — and, because that means
///changing someone's database beyond the data they asked to import, something the viewer has to ask about
///rather than quietly do.
///
///Only Dgraph needs one so far. An import names its nodes with the source graph's ids and its edges
///reference those same ids, but Dgraph assigns a <c>uid</c> only when a mutation commits, so the ids have
///to be findable in the graph for the edges to resolve — which needs an <b>indexed</b> predicate, and an
///index is a schema change.
///</summary>
public interface IGraphImportPreparation
{
    ///<summary>
    ///What has to change, in the engine's own terms, for the user to decide with. Shown in the confirm.
    ///</summary>
    string Requirement { get; }

    ///<summary>Why, in one sentence — so the ask is a decision rather than a demand.</summary>
    string Reason { get; }

    ///<summary>True when the database already has what an import needs, so nothing need be asked.</summary>
    Task<bool> IsReadyAsync(IGraphDb db, CancellationToken cancellationToken = default);

    ///<summary>Makes the change. Returns null when it worked, else what the engine said.</summary>
    Task<string> PrepareAsync(IGraphDb db, CancellationToken cancellationToken = default);
}
