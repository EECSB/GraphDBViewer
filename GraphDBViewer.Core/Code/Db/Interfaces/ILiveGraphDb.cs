using System;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///An engine that can keep answering after the answer arrives.
///
///Every other backend here is request/response: a query is asked, a result comes back, and it is as true as
///the moment it was read. GUN is not — it is a synchronizing peer-to-peer store whose own model is
///<c>.on()</c>, a subscription that fires whenever any peer changes the data. That does not fit
///<see cref="IGraphDb.ExecuteAsync"/>, which can only hand back one result, so it is a seam of its own
///rather than a stretched version of that one.
///
///The driver pushes; the viewer merges. Each push carries only what changed, in the same graph shape a
///query answers with, so the canvas can fold it into what is already drawn instead of redrawing.
///</summary>
public interface ILiveGraphDb
{
    ///<summary>
    ///Raised when a peer changes data inside the watched reach. Carries the changed part of the graph, not
    ///the whole of it — merging is the subscriber's job, and it is the same merge an expand does.
    ///</summary>
    event Action<GraphDbResult> GraphChanged;

    ///<summary>True while a subscription is running, so the UI can show what it has switched on.</summary>
    bool IsWatching { get; }

    ///<summary>
    ///Starts watching everything the query reaches. Replaces any previous subscription — a viewer shows one
    ///graph at a time, and a subscription left behind would keep pushing into a canvas that moved on.
    ///</summary>
    Task WatchAsync(string query, CancellationToken cancellationToken = default);

    ///<summary>Stops watching. Safe to call when nothing is being watched.</summary>
    Task StopWatchingAsync();
}
