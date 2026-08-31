using System;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///One graph database the app can talk to. The whole contract is "run this query, get a normalized
///result back" — what a given engine can *do* beyond that (browse, traverse, stage edits, debug) is
///described by <see cref="GraphDbCapabilities"/> on its <see cref="GraphDbProvider"/>, because the UI
///has to know that before a connection exists.
///
///Everything else an engine might offer is an optional seam of its own, sitting beside this one:
///<see cref="IGraphQueryBuilder"/>, <see cref="IGraphSchemaSource"/>, <see cref="IGraphQueryDebugger"/>,
///<see cref="ILiveGraphDb"/>, <see cref="IGraphImportPreparation"/>. Keeping them out of here is what
///lets a backend implement only what it can actually do — six engines behind this interface, and not one
///of them throws <c>NotSupportedException</c> for a feature the UI never offered it.
///</summary>
public interface IGraphDb : IAsyncDisposable
{
    //The default matters: several callers omit the token, and once the static type is IGraphDb the
    //default is taken from this declaration rather than the implementation's.
    Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default);
}
